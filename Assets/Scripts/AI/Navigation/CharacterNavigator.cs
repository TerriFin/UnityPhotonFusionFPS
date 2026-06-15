using System;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class CharacterNavigator : MonoBehaviour
	{
		public float RepathInterval = 0.35f;
		public float CornerReachDistance = 0.75f;
		public float DestinationReachDistance = 1.35f;
		// Vertical tolerance for "reached", separate from the horizontal DestinationReachDistance. Without it a
		// survivor on the ground directly under a rooftop/upper-floor destination counts as arrived and never climbs.
		// Keep below a building floor's height so stacked floors are distinguished, but above pivot/step variance.
		public float VerticalReachDistance = 2f;
		public float DestinationChangeRepathDistance = 0.5f;
		public float SampleMaxDistance = 2f;
		public float ReachablePointSampleMaxDistance = 8f;
		public int AreaMask = NavMesh.AllAreas;

		// Fallback used when a saved move order has no complete, in-budget NavMesh path. See CalculatePath /
		// TryBuildMidpointChainPath. UnreachableDistance is the "give up" radius: if the order is this close yet
		// still has no complete path, the last stretch is genuinely blocked and the order is reported unreachable.
		public float UnreachableDistance = 12f;
		public int MidpointBisectionAttempts = 3;
		public float MidpointSampleDistance = 6f;
		public float MidpointSurroundingRadius = 8f;

		// Runtime lane-spread state. Written by SurvivorAICommandService at lane assignment time —
		// the canonical tunable values live on SurvivorAICommandSettings (on the Gameplay GameObject).
		// 0 lane offset = no spread, which is the default for non-group orders.
		[HideInInspector] public float LaneOffset;
		[HideInInspector] public float MaxLaneOffset = 2.5f;
		[HideInInspector] public float LaneOffsetTaperDistance = 4f;
		[HideInInspector] public float LaneOffsetCornerSoftenDistance = 1.5f;
		[HideInInspector] public float LaneOffsetSampleDistance = 1.0f;
		[HideInInspector] public bool ValidateLaneOffsetPath = true;

		private NavMeshPath _path;
		private NavMeshPath _scratchPath;
		private Vector3[] _corners = Array.Empty<Vector3>();

		// Surrounding-area probe pattern for a midpoint (centre first, then a ring of compass directions scaled by
		// MidpointSurroundingRadius). A bisected midpoint can land inside a building block; the ring lets it resolve
		// to a road beside it instead.
		private static readonly Vector3[] MidpointSurroundingOffsets =
		{
			new Vector3(0f, 0f, 0f),
			new Vector3(1f, 0f, 0f),
			new Vector3(-1f, 0f, 0f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, -1f),
			new Vector3(0.7071f, 0f, 0.7071f),
			new Vector3(-0.7071f, 0f, 0.7071f),
			new Vector3(0.7071f, 0f, -0.7071f),
			new Vector3(-0.7071f, 0f, -0.7071f),
		};

		private Vector3 _destination;
		private Vector3 _sampledDestination;
		private float _nextRepathTime;
		private int _cornerIndex;
		private bool _hasDestination;
		private bool _hasPath;
		private bool _isDestinationReached;
		private bool _isDestinationUnreachable;

		public bool HasDestination => _hasDestination;
		public bool HasPath => _hasPath;
		public bool IsPathPending => false;
		public bool IsDestinationReached => _isDestinationReached;
		// True when the saved order has no complete path and is inside UnreachableDistance — the last stretch is
		// genuinely blocked. The order issuer should abandon the order (hold) rather than keep waiting on a path.
		public bool IsDestinationUnreachable => _isDestinationUnreachable;
		public Vector3 Destination => _destination;

		private void Awake()
		{
			_path = new NavMeshPath();
			_scratchPath = new NavMeshPath();
		}

		public void SetDestination(Vector3 destination)
		{
			// 3D compare: two patrol points stacked on different floors can share the same XZ, so a flat check would
			// treat a switch between them as "same destination" and never repath up/down to the new one.
			if (_hasDestination && (_destination - destination).sqrMagnitude <= DestinationChangeRepathDistance * DestinationChangeRepathDistance)
				return;

			_destination = destination;
			_sampledDestination = destination;
			_hasDestination = true;
			_hasPath = false;
			_isDestinationReached = false;
			_isDestinationUnreachable = false;
			_cornerIndex = 0;
			_corners = Array.Empty<Vector3>();
			_nextRepathTime = 0f;
		}

		public void ClearDestination()
		{
			_hasDestination = false;
			_hasPath = false;
			_isDestinationReached = false;
			_isDestinationUnreachable = false;
			_cornerIndex = 0;
			_corners = Array.Empty<Vector3>();
			_path?.ClearCorners();
		}

		public void ForceRepath()
		{
			_hasPath = false;
			_isDestinationUnreachable = false;
			_cornerIndex = 0;
			_corners = Array.Empty<Vector3>();
			_nextRepathTime = 0f;
		}

		public void Tick(Vector3 currentPosition)
		{
			if (_hasDestination == false)
				return;

			UpdateReached(currentPosition);
			if (_isDestinationReached)
			{
				_hasPath = false;
				_isDestinationUnreachable = false;
				return;
			}

			if (_hasPath)
				AdvanceCorner(currentPosition);

			if (Time.timeSinceLevelLoad < _nextRepathTime)
				return;

			_nextRepathTime = Time.timeSinceLevelLoad + Mathf.Max(0.02f, RepathInterval);
			CalculatePath(currentPosition);
			AdvanceCorner(currentPosition);
		}

		public bool TryGetSteeringTarget(Vector3 currentPosition, out Vector3 steeringTarget)
		{
			steeringTarget = default;

			if (_hasDestination == false || _isDestinationReached)
				return false;

			if (_hasPath == false || _corners.Length == 0)
				return false;

			AdvanceCorner(currentPosition);
			if (_cornerIndex >= _corners.Length)
				return false;

			Vector3 corner = _corners[_cornerIndex];
			steeringTarget = ApplyLaneOffset(corner, currentPosition);
			return true;
		}

		private Vector3 ApplyLaneOffset(Vector3 corner, Vector3 currentPosition)
		{
			if (Mathf.Abs(LaneOffset) <= 0.001f)
				return corner;

			Vector3 toCorner = corner - currentPosition;
			toCorner.y = 0f;
			float distanceToCorner = toCorner.magnitude;
			if (distanceToCorner < 0.05f)
				return corner;

			float distanceToDestination = FlatDistance(currentPosition, _sampledDestination);
			float destinationTaper = LaneOffsetTaperDistance <= 0f
				? 1f
				: Mathf.Clamp01(distanceToDestination / LaneOffsetTaperDistance);

			float cornerSoften = LaneOffsetCornerSoftenDistance <= 0f
				? 1f
				: Mathf.Clamp01(distanceToCorner / LaneOffsetCornerSoftenDistance);

			float effectiveOffset = LaneOffset * destinationTaper * cornerSoften;
			if (MaxLaneOffset > 0f)
				effectiveOffset = Mathf.Clamp(effectiveOffset, -MaxLaneOffset, MaxLaneOffset);
			if (Mathf.Abs(effectiveOffset) <= 0.001f)
				return corner;

			Vector3 direction = toCorner / distanceToCorner;
			Vector3 perpendicular = new Vector3(-direction.z, 0f, direction.x);
			Vector3 candidate = corner + perpendicular * effectiveOffset;

			float sampleDistance = Mathf.Max(0.1f, LaneOffsetSampleDistance);
			if (NavMesh.SamplePosition(candidate, out var hit, sampleDistance, AreaMask))
			{
				if (ValidateLaneOffsetPath && IsLaneOffsetPathBlocked(currentPosition, hit.position, sampleDistance))
					return corner;

				return hit.position;
			}

			return corner;
		}

		private bool IsLaneOffsetPathBlocked(Vector3 currentPosition, Vector3 offsetTarget, float sampleDistance)
		{
			float currentSampleDistance = Mathf.Max(0.1f, sampleDistance);
			if (NavMesh.SamplePosition(currentPosition, out var currentHit, currentSampleDistance, AreaMask) == false)
				return true;

			return NavMesh.Raycast(currentHit.position, offsetTarget, out _, AreaMask);
		}

		public bool TryFindReachablePoint(Vector3 currentPosition, Vector3 targetPosition, out Vector3 reachablePoint)
		{
			return TryFindReachablePoint(currentPosition, targetPosition, ReachablePointSampleMaxDistance, out reachablePoint);
		}

		public bool TryFindReachablePoint(Vector3 currentPosition, Vector3 targetPosition, float maxSampleDistance, out Vector3 reachablePoint)
		{
			return TryFindReachablePoint(currentPosition, targetPosition, maxSampleDistance, out reachablePoint, out _);
		}

		public bool TryFindReachablePoint(Vector3 currentPosition, Vector3 targetPosition, float maxSampleDistance,
			out Vector3 reachablePoint, out float pathLength)
		{
			reachablePoint = default;
			pathLength = 0f;

			if (_scratchPath == null)
				_scratchPath = new NavMeshPath();

			float startSampleDistance = Mathf.Max(0.01f, SampleMaxDistance);
			if (NavMesh.SamplePosition(currentPosition, out var startHit, startSampleDistance, AreaMask) == false)
				return false;

			float maxDistance = Mathf.Max(0.01f, maxSampleDistance);
			float sampleDistance = Mathf.Min(startSampleDistance, maxDistance);

			while (sampleDistance <= maxDistance + 0.01f)
			{
				if (NavMesh.SamplePosition(targetPosition, out var targetHit, sampleDistance, AreaMask) &&
				    NavMesh.CalculatePath(startHit.position, targetHit.position, AreaMask, _scratchPath) &&
				    _scratchPath.status == NavMeshPathStatus.PathComplete)
				{
					reachablePoint = targetHit.position;
					pathLength = GetPathLength(_scratchPath);
					return true;
				}

				sampleDistance *= 2f;
				if (sampleDistance < maxDistance && sampleDistance * 2f > maxDistance)
					sampleDistance = maxDistance;
			}

			return false;
		}

		private static float GetPathLength(NavMeshPath path)
		{
			if (path == null || path.corners == null || path.corners.Length < 2)
				return 0f;

			float length = 0f;
			for (int i = 1; i < path.corners.Length; i++)
				length += Vector3.Distance(path.corners[i - 1], path.corners[i]);

			return length;
		}

		private void CalculatePath(Vector3 currentPosition)
		{
			if (_path == null)
				_path = new NavMeshPath();

			_hasPath = false;
			_corners = Array.Empty<Vector3>();
			_cornerIndex = 0;
			_isDestinationUnreachable = false;

			float sampleDistance = Mathf.Max(0.01f, SampleMaxDistance);
			if (NavMesh.SamplePosition(currentPosition, out var startHit, sampleDistance, AreaMask) == false)
				return;

			Vector3 goal = _destination;
			if (NavMesh.SamplePosition(_destination, out var endHit, sampleDistance, AreaMask))
			{
				goal = endHit.position;
				_sampledDestination = endHit.position;
				UpdateReached(currentPosition);
				if (_isDestinationReached)
					return;

				// The saved order is kept until reached; first try a direct route. Use it only if it fully reaches
				// the destination. A path spanning most of the large, maze-like generated map can exceed
				// NavMesh.CalculatePath's internal node budget and come back as PathPartial/PathInvalid; either way
				// it falls through to the midpoint-chaining fallback below.
				if (NavMesh.CalculatePath(startHit.position, endHit.position, AreaMask, _path) &&
				    _path.status == NavMeshPathStatus.PathComplete && _path.corners != null && _path.corners.Length > 0)
				{
					_corners = _path.corners;
					_hasPath = true;
					return;
				}
			}

			// No complete direct route. If the saved order is already within UnreachableDistance yet still has no
			// path, the last stretch is genuinely blocked (target inside a building / on a disconnected ledge):
			// report it unreachable so the order issuer abandons it instead of waiting on a path that can't exist.
			if (FlatDistanceSqr(startHit.position, goal) <= UnreachableDistance * UnreachableDistance)
			{
				_isDestinationUnreachable = true;
				return;
			}

			// Far, blocked order — almost always NavMesh.CalculatePath's fixed node budget giving up on a long
			// route, not a true disconnect (a survivor in the centre can reach every corner; one in a corner cannot
			// reach the far one in a single query). Step toward a reachable midpoint instead; repathing from the
			// advancing position re-tries the saved original order and chains the survivor the whole way across the
			// map instead of standing still. If even that finds nothing the survivor holds and retries next repath.
			TryBuildMidpointChainPath(startHit.position, goal);
		}

		// Bisect toward the origin: try the midpoint between us and the goal, then (if that is unreachable) halfway
		// again toward us, up to MidpointBisectionAttempts times. Closer candidates are ever more likely to fall on
		// a reachable, in-budget patch of NavMesh, so the survivor at least makes progress toward the goal and
		// re-evaluates from the new position. Returns true if a usable stepping path was found.
		private bool TryBuildMidpointChainPath(Vector3 startOnNavMesh, Vector3 goal)
		{
			int attempts = Mathf.Max(1, MidpointBisectionAttempts);
			float startToGoalSqr = FlatDistanceSqr(startOnNavMesh, goal);
			float minStep = Mathf.Max(0.5f, DestinationReachDistance);
			float minStepSqr = minStep * minStep;

			Vector3 far = goal;
			for (int attempt = 0; attempt < attempts; attempt++)
			{
				Vector3 mid = (startOnNavMesh + far) * 0.5f;
				if (TryBuildPathToReachableNear(startOnNavMesh, mid, goal, startToGoalSqr, minStepSqr))
					return true;

				far = mid;
			}

			return false;
		}

		// Sample the midpoint and its surrounding area for a NavMesh point that is reachable by a complete in-budget
		// path AND meaningfully closer to the goal than where we stand, then build the path to it.
		private bool TryBuildPathToReachableNear(Vector3 startOnNavMesh, Vector3 center, Vector3 goal,
			float startToGoalSqr, float minStepSqr)
		{
			float sampleDistance = Mathf.Max(0.5f, MidpointSampleDistance);
			float radius = Mathf.Max(0f, MidpointSurroundingRadius);

			for (int s = 0; s < MidpointSurroundingOffsets.Length; s++)
			{
				Vector3 probe = center + MidpointSurroundingOffsets[s] * radius;

				if (NavMesh.SamplePosition(probe, out var hit, sampleDistance, AreaMask) == false)
					continue;
				if (FlatDistanceSqr(hit.position, goal) >= startToGoalSqr)
					continue; // not actually closer to the goal than where we are — skip
				if (FlatDistanceSqr(startOnNavMesh, hit.position) < minStepSqr)
					continue; // basically where we already are — skip
				if (NavMesh.CalculatePath(startOnNavMesh, hit.position, AreaMask, _path) == false)
					continue;
				if (_path.status != NavMeshPathStatus.PathComplete || _path.corners == null || _path.corners.Length == 0)
					continue;

				_corners = _path.corners;
				_hasPath = true;
				return true;
			}

			return false;
		}

		private void AdvanceCorner(Vector3 currentPosition)
		{
			float reachDistanceSqr = CornerReachDistance * CornerReachDistance;
			while (_hasPath && _cornerIndex < _corners.Length &&
			       FlatDistanceSqr(currentPosition, _corners[_cornerIndex]) <= reachDistanceSqr)
			{
				_cornerIndex++;
			}

			if (_cornerIndex >= _corners.Length)
			{
				_hasPath = false;
			}
		}

		private void UpdateReached(Vector3 currentPosition)
		{
			Vector3 target = _sampledDestination;
			bool horizontalReached = FlatDistanceSqr(currentPosition, target) <= DestinationReachDistance * DestinationReachDistance;
			bool verticalReached = Mathf.Abs(currentPosition.y - target.y) <= Mathf.Max(0.01f, VerticalReachDistance);
			_isDestinationReached = horizontalReached && verticalReached;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static float FlatDistance(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).magnitude;
		}
	}
}
