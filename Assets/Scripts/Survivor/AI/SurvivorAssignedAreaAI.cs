using System;
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
		public float DirectFallbackDistance = 4f;
		public int PatrolPointSampleAttempts = 8;

		[Header("Waiting")]
		public float WaitDurationMin = 4f;
		public float WaitDurationMax = 8f;

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

			if (FlatDistanceSqr(survivor.transform.position, _patrolTarget) <= PatrolPointStoppingDistance * PatrolPointStoppingDistance)
			{
				_hasPatrolTarget = false;
				BeginWait();
				return getHoldInput();
			}

			return MoveTo(survivor, _patrolTarget, PatrolPointStoppingDistance, createMoveInput, getHoldInput);
		}

		public bool TryBuildReachablePointSet(Survivor survivor, Vector3 center, float radius, out Vector3[] reachablePoints)
		{
			reachablePoints = Array.Empty<Vector3>();
			if (survivor == null || radius <= 0f)
				return false;

			var navigator = survivor.Navigator;
			if (navigator == null)
			{
				reachablePoints = new[] { center };
				return true;
			}

			if (TryCreateReachabilityQuery(navigator, survivor.transform.position, out var query) == false)
				return false;

			int targetCount = Mathf.Max(1, Mathf.CeilToInt(radius));
			int candidateCount = Mathf.Max(8, targetCount * 3);
			var results = new Vector3[targetCount];
			int count = 0;

			if (TryFindReachablePoint(query, center, center, radius, out Vector3 reachablePoint))
			{
				AddReachablePoint(results, ref count, reachablePoint);
			}
			else if (TryFindEdgeProbe(query, center, radius, out reachablePoint))
			{
				AddReachablePoint(results, ref count, reachablePoint);
			}
			else
			{
				return false;
			}

			float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
			for (int i = 0; i < candidateCount && count < targetCount; i++)
			{
				Vector3 candidate = GetGoldenSpiralPoint(center, radius, candidateCount, i, goldenAngle);
				if (TryFindReachablePoint(query, center, candidate, radius, out reachablePoint) == false)
					continue;
				if (ContainsNearby(results, count, reachablePoint, PatrolPointStoppingDistance))
					continue;

				AddReachablePoint(results, ref count, reachablePoint);
			}

			if (count != results.Length)
				Array.Resize(ref results, count);

			reachablePoints = results;
			return count > 0;
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

			if (FlatDistanceSqr(survivor.transform.position, target) <= DirectFallbackDistance * DirectFallbackDistance)
				return createMoveInput(target, true, stoppingDistance);

			return getHoldInput();
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
			query = default;
			if (_scratchPath == null)
				_scratchPath = new NavMeshPath();

			float sampleDistance = Mathf.Max(0.01f, navigator.SampleMaxDistance);
			if (NavMesh.SamplePosition(currentPosition, out var startHit, sampleDistance, navigator.AreaMask) == false)
				return false;

			query = new ReachabilityQuery(startHit.position, sampleDistance, navigator.AreaMask, _scratchPath);
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

			Vector3 navCandidate = new Vector3(candidate.x, query.StartPosition.y, candidate.z);
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

		private static void AddReachablePoint(Vector3[] results, ref int count, Vector3 point)
		{
			if (count < results.Length)
				results[count++] = point;
		}

		private static bool ContainsNearby(Vector3[] points, int count, Vector3 point, float minDistance)
		{
			float minDistanceSqr = minDistance * minDistance;
			for (int i = 0; i < count; i++)
			{
				if (FlatDistanceSqr(points[i], point) <= minDistanceSqr)
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
