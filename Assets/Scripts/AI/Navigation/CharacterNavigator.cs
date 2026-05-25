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
		public float DestinationChangeRepathDistance = 0.5f;
		public float SampleMaxDistance = 2f;
		public float ReachablePointSampleMaxDistance = 8f;
		public int AreaMask = NavMesh.AllAreas;

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
		private Vector3 _destination;
		private Vector3 _sampledDestination;
		private float _nextRepathTime;
		private int _cornerIndex;
		private bool _hasDestination;
		private bool _hasPath;
		private bool _isDestinationReached;

		public bool HasDestination => _hasDestination;
		public bool HasPath => _hasPath;
		public bool IsPathPending => false;
		public bool IsDestinationReached => _isDestinationReached;
		public Vector3 Destination => _destination;

		private void Awake()
		{
			_path = new NavMeshPath();
			_scratchPath = new NavMeshPath();
		}

		public void SetDestination(Vector3 destination)
		{
			if (_hasDestination && FlatDistanceSqr(_destination, destination) <= DestinationChangeRepathDistance * DestinationChangeRepathDistance)
				return;

			_destination = destination;
			_sampledDestination = destination;
			_hasDestination = true;
			_hasPath = false;
			_isDestinationReached = false;
			_cornerIndex = 0;
			_corners = Array.Empty<Vector3>();
			_nextRepathTime = 0f;
		}

		public void ClearDestination()
		{
			_hasDestination = false;
			_hasPath = false;
			_isDestinationReached = false;
			_cornerIndex = 0;
			_corners = Array.Empty<Vector3>();
			_path?.ClearCorners();
		}

		public void ForceRepath()
		{
			_hasPath = false;
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
			reachablePoint = default;

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
					return true;
				}

				sampleDistance *= 2f;
				if (sampleDistance < maxDistance && sampleDistance * 2f > maxDistance)
					sampleDistance = maxDistance;
			}

			return false;
		}

		private void CalculatePath(Vector3 currentPosition)
		{
			if (_path == null)
				_path = new NavMeshPath();

			_hasPath = false;
			_corners = Array.Empty<Vector3>();
			_cornerIndex = 0;

			if (NavMesh.SamplePosition(currentPosition, out var startHit, Mathf.Max(0.01f, SampleMaxDistance), AreaMask) == false)
				return;
			if (NavMesh.SamplePosition(_destination, out var endHit, Mathf.Max(0.01f, SampleMaxDistance), AreaMask) == false)
				return;

			_sampledDestination = endHit.position;
			UpdateReached(currentPosition);
			if (_isDestinationReached)
				return;

			if (NavMesh.CalculatePath(startHit.position, endHit.position, AreaMask, _path) == false)
				return;
			if (_path.status != NavMeshPathStatus.PathComplete || _path.corners == null || _path.corners.Length == 0)
				return;

			_corners = _path.corners;
			_hasPath = true;
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
			_isDestinationReached = FlatDistanceSqr(currentPosition, target) <= DestinationReachDistance * DestinationReachDistance;
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
