using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorAssignedAreaAI : MonoBehaviour
	{
		private static readonly Vector3[] EdgeProbeDirections =
		{
			Vector3.forward,
			Vector3.right,
			Vector3.back,
			Vector3.left,
			(Vector3.forward + Vector3.right).normalized,
			(Vector3.back + Vector3.right).normalized,
			(Vector3.back + Vector3.left).normalized,
			(Vector3.forward + Vector3.left).normalized,
		};

		[Header("Movement")]
		public float AreaStoppingDistance = 1.25f;
		public float PatrolPointStoppingDistance = 1f;
		// Vertical tolerance for "reached a patrol point", separate from the horizontal PatrolPointStoppingDistance.
		// Without it a survivor on the ground directly under a rooftop waypoint counts as having arrived and never
		// climbs. Keep this below a building floor's height so stacked floors are distinguished, but above survivor
		// pivot / step variance.
		public float PatrolPointVerticalStoppingDistance = 2f;
		public float DirectFallbackDistance = 1.5f;
		public int PatrolPointSampleAttempts = 8;

		[Header("Waiting")]
		public float WaitDurationMin = 4f;
		public float WaitDurationMax = 8f;

		// Reusable scratch for building a patrol-point set (manual waypoints + auto-sampled fill) without
		// per-order GC. Only ever used on state authority, synchronously, for one order at a time.
		private readonly List<Vector3> _patrolPointBuffer = new();

		private NavMeshPath _scratchPath;
		private Vector3 _patrolTarget;
		private bool _hasPatrolTarget;
		private bool _isWaiting;
		private float _waitUntil;

		public bool IsInsideArea(Survivor survivor, Vector3 center, float radius)
		{
			if (survivor == null)
				return false;

			float effectiveRadius = Mathf.Max(0f, radius);
			return FlatDistanceSqr(survivor.transform.position, center) <= effectiveRadius * effectiveRadius;
		}

		public void ClearTask(CharacterNavigator navigator)
		{
			_hasPatrolTarget = false;
			_isWaiting = false;
			_waitUntil = 0f;
			navigator?.ClearDestination();
		}

		public NetworkedInput GetInput(
			Survivor survivor,
			Vector3 center,
			Vector3 entryPoint,
			float radius,
			Vector3[] patrolPoints,
			Func<Vector3, bool, float, NetworkedInput> createMoveInput,
			Func<NetworkedInput> getHoldInput)
		{
			if (survivor == null)
				return default;
			if (radius <= 0f)
				return getHoldInput();

			if (IsInsideArea(survivor, center, radius) == false)
			{
				_hasPatrolTarget = false;
				_isWaiting = false;
				return MoveTo(survivor, entryPoint, AreaStoppingDistance, createMoveInput, getHoldInput);
			}

			if (_isWaiting)
			{
				if (Time.timeSinceLevelLoad < _waitUntil)
					return getHoldInput();

				_isWaiting = false;
			}

			if (_hasPatrolTarget == false && TryPickPatrolTarget(survivor, center, radius, patrolPoints) == false)
			{
				BeginWait();
				return getHoldInput();
			}

			if (HasReachedPoint(survivor.transform.position, _patrolTarget, PatrolPointStoppingDistance))
			{
				_hasPatrolTarget = false;
				BeginWait();
				return getHoldInput();
			}

			return MoveTo(survivor, _patrolTarget, PatrolPointStoppingDistance, createMoveInput, getHoldInput);
		}

		public bool TryBuildReachablePointSet(Survivor survivor, Vector3 center, float radius, out Vector3[] reachablePoints,
			bool preferAuthoredWaypoints = false)
		{
			reachablePoints = Array.Empty<Vector3>();
			if (survivor == null || radius <= 0f)
				return false;

			// Target number of patrol points for a circle this size (radius 3.14 -> ~4, 8.51 -> ~9).
			int targetCount = Mathf.Max(1, Mathf.CeilToInt(radius));

			// Prefab-authored waypoints whose XZ falls inside the circle are forced into the patrol set. The player
			// is responsible for placing them on reachable ground, so they are taken as-is with NO reachability test
			// (this is the whole point: they reach places the ground-level auto-sampler can't, like rooftops).
			_patrolPointBuffer.Clear();
			int manualCount = PatrolWaypoint.CollectInsideCircle(center, radius, _patrolPointBuffer);

			// Use the manual waypoints alone (skip auto-sampling) when either:
			//  - there are at least as many as we want for a circle this size (the normal "enough waypoints" rule), or
			//  - the caller asked to prefer authored waypoints and there is at least one. A garrison (e.g. a neutral
			//    survivor holding a building) wants to patrol its windows/rooftops, not be diluted by ground points
			//    auto-sampled to pad out a wide PatrolRadius.
			if (manualCount >= targetCount || (preferAuthoredWaypoints && manualCount > 0))
				return FinishPointSet(out reachablePoints);

			var navigator = survivor.Navigator;
			if (navigator == null)
			{
				// No navigator to validate auto points: use the manual waypoints if any, otherwise the centre.
				if (manualCount == 0)
					_patrolPointBuffer.Add(center);
				return FinishPointSet(out reachablePoints);
			}

			// Fewer manual waypoints than wanted: keep all of them and auto-sample reachable points to fill the rest.
			//
			// Evaluate reachability from the MIDDLE of the area, not the survivor's position. A patrol order can be
			// issued from across the map where the survivor has no in-budget NavMesh path to the area yet (it chains
			// there via the navigator, exactly like a move order). Patrol points only need to be mutually reachable
			// within the area, so anchoring the query at the centre stops a far order being wrongly rejected. Sample
			// the centre out to the radius so a centre landing just off the NavMesh still resolves to walkable ground.
			float centerSampleDistance = Mathf.Max(navigator.SampleMaxDistance, radius);
			if (TryCreateReachabilityQuery(navigator, center, centerSampleDistance, out var query) == false)
			{
				// No reachable NavMesh anchor for the auto-fill. With manual waypoints the order is still valid;
				// with none, the circle genuinely contains no reachable ground and the order is rejected (unchanged).
				return FinishPointSet(out reachablePoints);
			}

			// Seed a guaranteed-reachable ground anchor (centre, else an edge probe) only when there are no manual
			// waypoints. When manual waypoints exist they already define where to patrol, so the auto fill is pure
			// extra coverage and the centre point is not forced in.
			if (manualCount == 0)
			{
				if (TryFindReachablePoint(query, center, center, radius, out Vector3 anchor))
					AddPointIfFar(anchor);
				else if (TryFindEdgeProbe(query, center, radius, out anchor))
					AddPointIfFar(anchor);
				else
					return false; // No reachable ground and no manual waypoints: reject (unchanged behavior).
			}

			float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
			int candidateCount = Mathf.Max(8, targetCount * 3);
			for (int i = 0; i < candidateCount && _patrolPointBuffer.Count < targetCount; i++)
			{
				Vector3 candidate = GetGoldenSpiralPoint(center, radius, candidateCount, i, goldenAngle);
				if (TryFindReachablePoint(query, center, candidate, radius, out Vector3 reachablePoint) == false)
					continue;

				AddPointIfFar(reachablePoint);
			}

			return FinishPointSet(out reachablePoints);
		}

		private bool FinishPointSet(out Vector3[] reachablePoints)
		{
			reachablePoints = _patrolPointBuffer.Count > 0 ? _patrolPointBuffer.ToArray() : Array.Empty<Vector3>();
			_patrolPointBuffer.Clear();
			return reachablePoints.Length > 0;
		}

		private void AddPointIfFar(Vector3 point)
		{
			if (ContainsNearby(_patrolPointBuffer, point, PatrolPointStoppingDistance) == false)
				_patrolPointBuffer.Add(point);
		}

		private NetworkedInput MoveTo(
			Survivor survivor,
			Vector3 target,
			float stoppingDistance,
			Func<Vector3, bool, float, NetworkedInput> createMoveInput,
			Func<NetworkedInput> getHoldInput)
		{
			var navigator = survivor.Navigator;
			if (navigator != null)
			{
				navigator.SetDestination(target);
				navigator.Tick(survivor.transform.position);
				if (navigator.TryGetSteeringTarget(survivor.transform.position, out var steeringTarget))
					return createMoveInput(steeringTarget, false, stoppingDistance);
			}

			// Only walk straight at the target when it is close in 3D. A target far overhead (a rooftop point with
			// no path this tick) must not trigger the direct fallback, or the survivor would "walk" into the wall
			// under it and stop; instead it holds and lets the navigator path up next tick.
			if (HasReachedPoint(survivor.transform.position, target, DirectFallbackDistance))
				return createMoveInput(target, true, stoppingDistance);

			return getHoldInput();
		}

		// Horizontal proximity within stoppingDistance AND vertical proximity within PatrolPointVerticalStoppingDistance.
		// Keeping the two axes separate lets a survivor stand close on a flat floor while still refusing to count a
		// point directly above or below it (a different building level) as reached.
		private bool HasReachedPoint(Vector3 position, Vector3 target, float stoppingDistance)
		{
			if (FlatDistanceSqr(position, target) > stoppingDistance * stoppingDistance)
				return false;

			return Mathf.Abs(position.y - target.y) <= Mathf.Max(0.01f, PatrolPointVerticalStoppingDistance);
		}

		private bool TryPickPatrolTarget(Survivor survivor, Vector3 center, float radius, Vector3[] patrolPoints)
		{
			if (patrolPoints != null && patrolPoints.Length > 0)
			{
				_patrolTarget = patrolPoints[UnityEngine.Random.Range(0, patrolPoints.Length)];
				_hasPatrolTarget = true;
				survivor.Navigator?.SetDestination(_patrolTarget);
				return true;
			}

			var navigator = survivor.Navigator;
			if (navigator == null)
			{
				Vector2 offset = UnityEngine.Random.insideUnitCircle * Mathf.Max(0f, radius);
				_patrolTarget = center + new Vector3(offset.x, 0f, offset.y);
				_hasPatrolTarget = true;
				return true;
			}

			if (TryCreateReachabilityQuery(navigator, survivor.transform.position, out var query) == false)
				return false;

			int attempts = Mathf.Max(1, PatrolPointSampleAttempts);
			for (int i = 0; i < attempts; i++)
			{
				Vector2 offset = UnityEngine.Random.insideUnitCircle * Mathf.Max(0f, radius);
				Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);
				if (TryFindReachablePoint(query, center, candidate, radius, out _patrolTarget) == false)
					continue;

				_hasPatrolTarget = true;
				navigator.SetDestination(_patrolTarget);
				return true;
			}

			return false;
		}

		private bool TryCreateReachabilityQuery(CharacterNavigator navigator, Vector3 currentPosition, out ReachabilityQuery query)
		{
			return TryCreateReachabilityQuery(navigator, currentPosition, navigator.SampleMaxDistance, out query);
		}

		// startSampleDistance only widens how far the START is snapped onto the NavMesh (so an area centre that
		// lands just off-mesh still resolves to walkable ground inside the area). Candidate patrol points are always
		// sampled at the tight SampleMaxDistance so they stay on the intended ground.
		private bool TryCreateReachabilityQuery(CharacterNavigator navigator, Vector3 startPosition, float startSampleDistance, out ReachabilityQuery query)
		{
			query = default;
			if (_scratchPath == null)
				_scratchPath = new NavMeshPath();

			float candidateSampleDistance = Mathf.Max(0.01f, navigator.SampleMaxDistance);
			float startSample = Mathf.Max(candidateSampleDistance, startSampleDistance);
			if (NavMesh.SamplePosition(startPosition, out var startHit, startSample, navigator.AreaMask) == false)
				return false;

			query = new ReachabilityQuery(startHit.position, candidateSampleDistance, navigator.AreaMask, _scratchPath);
			return true;
		}

		private static bool TryFindEdgeProbe(ReachabilityQuery query, Vector3 center, float radius, out Vector3 reachablePoint)
		{
			reachablePoint = default;
			float probeRadius = Mathf.Max(0.1f, radius * 0.8f);

			for (int i = 0; i < EdgeProbeDirections.Length; i++)
			{
				Vector3 candidate = center + EdgeProbeDirections[i] * probeRadius;
				if (TryFindReachablePoint(query, center, candidate, radius, out reachablePoint))
					return true;
			}

			return false;
		}

		private static bool TryFindReachablePoint(
			ReachabilityQuery query,
			Vector3 areaCenter,
			Vector3 candidate,
			float areaRadius,
			out Vector3 reachablePoint)
		{
			reachablePoint = default;

			// Sample at the candidate's Y rather than the survivor's. The candidate carries the order's
			// center height (the cell the player started the circle on), so if the circle is on a ledge
			// the patrol points resolve on the ledge's NavMesh instead of the survivor's current floor.
			Vector3 navCandidate = new Vector3(candidate.x, candidate.y, candidate.z);
			if (NavMesh.SamplePosition(navCandidate, out var targetHit, query.SampleDistance, query.AreaMask) == false)
				return false;
			if (FlatDistanceSqr(targetHit.position, areaCenter) > areaRadius * areaRadius)
				return false;
			if (NavMesh.CalculatePath(query.StartPosition, targetHit.position, query.AreaMask, query.Path) == false)
				return false;
			if (query.Path.status != NavMeshPathStatus.PathComplete)
				return false;

			reachablePoint = targetHit.position;
			return true;
		}

		private static Vector3 GetGoldenSpiralPoint(Vector3 center, float radius, int count, int index, float goldenAngle)
		{
			float normalized = (index + 0.5f) / count;
			float sampleRadius = Mathf.Sqrt(normalized) * radius * 0.9f;
			float angle = index * goldenAngle;
			return center + new Vector3(Mathf.Cos(angle) * sampleRadius, 0f, Mathf.Sin(angle) * sampleRadius);
		}

		// 3D distance on purpose: an auto-sampled ground point and a manual rooftop waypoint can share the same XZ
		// but belong to different floors, so a flat check would wrongly merge them. Vertically separated points are
		// kept; genuine duplicates on the same level are dropped.
		private static bool ContainsNearby(List<Vector3> points, Vector3 point, float minDistance)
		{
			float minDistanceSqr = minDistance * minDistance;
			for (int i = 0; i < points.Count; i++)
			{
				if ((points[i] - point).sqrMagnitude <= minDistanceSqr)
					return true;
			}

			return false;
		}

		private void BeginWait()
		{
			float duration = RandomRangeClamped(WaitDurationMin, WaitDurationMax);
			_waitUntil = Time.timeSinceLevelLoad + duration;
			_isWaiting = true;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static float RandomRangeClamped(float min, float max)
		{
			min = Mathf.Max(0f, min);
			max = Mathf.Max(min, max);
			if (Mathf.Approximately(min, max))
				return min;

			return UnityEngine.Random.Range(min, max);
		}

		private readonly struct ReachabilityQuery
		{
			public readonly Vector3 StartPosition;
			public readonly float SampleDistance;
			public readonly int AreaMask;
			public readonly NavMeshPath Path;

			public ReachabilityQuery(Vector3 startPosition, float sampleDistance, int areaMask, NavMeshPath path)
			{
				StartPosition = startPosition;
				SampleDistance = sampleDistance;
				AreaMask = areaMask;
				Path = path;
			}
		}
	}
}
