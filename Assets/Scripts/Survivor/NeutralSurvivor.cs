using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>A dynamic spawn point's area a roaming neutral survivor can wander to.</summary>
	public readonly struct RoamDestination
	{
		public readonly Vector3 Center;
		public readonly float Radius;

		public RoamDestination(Vector3 center, float radius)
		{
			Center = center;
			Radius = Mathf.Max(0f, radius);
		}
	}

	[DisallowMultipleComponent]
	public sealed class NeutralSurvivor : MonoBehaviour
	{
		private struct StatSnapshot
		{
			public float AIMoveSpeed;
			public float VisionDistance;
			public float ProximityAwarenessRadius;
			public float SensorInterval;
			public float HorizontalAimErrorDegrees;
			public float VerticalAimErrorDegrees;
			public float ZombieHorizontalAimErrorDegrees;
			public float ZombieVerticalAimErrorDegrees;
		}

		[Header("Roaming")]
		[Min(0f)]
		[Tooltip("How long (seconds) a roaming survivor patrols a dynamic spawn area before independently picking the next one to move to. A random value in this range is rolled per move. Only used when the survivor spawned from a marker with DynamicSpawn enabled.")]
		public float RoamDwellTimeMin = 10f;
		[Min(0f)]
		public float RoamDwellTimeMax = 25f;

		public Vector3 PatrolCenter { get; private set; }
		public float PatrolRadius { get; private set; }
		public bool IsRoaming { get; private set; }

		private Survivor _survivor;
		private IReadOnlyList<RoamDestination> _roamDestinations;
		private Vector3 _roamCenter;
		private float _roamRadius;
		private int _lastRoamIndex = -1;
		private bool _hasRoamTarget;
		private bool _reachedRoamArea;
		private float _roamDwellUntil;
		private StatSnapshot _statSnapshot;
		private bool _hasStatSnapshot;

		public bool IsNeutral => _survivor != null && _survivor.IsNeutral;

		public void Initialize(Survivor survivor, Vector3 patrolCenter, float patrolRadius)
		{
			_survivor = survivor != null ? survivor : GetComponent<Survivor>();
			PatrolCenter = patrolCenter;
			PatrolRadius = Mathf.Max(0f, patrolRadius);
		}

		public void ApplyNeutralStats(NeutralSurvivorSpawnSettings settings)
		{
			_survivor = _survivor != null ? _survivor : GetComponent<Survivor>();
			if (_survivor == null || settings == null || settings.ApplyNeutralStatOverrides == false)
				return;

			CaptureStatsIfNeeded();

			_survivor.AIMoveSpeed = Mathf.Max(0f, settings.NeutralMovementSpeed);

			CharacterSensor sensor = _survivor.Sensor != null ? _survivor.Sensor : _survivor.GetComponent<CharacterSensor>();
			if (sensor != null)
			{
				sensor.VisionDistance = Mathf.Max(0f, settings.NeutralVisionDistance);
				sensor.ProximityAwarenessRadius = Mathf.Max(0f, settings.NeutralAllAroundDetectionRange);
				sensor.SensorInterval = Mathf.Max(0.02f, settings.NeutralSensorInterval);
			}

			SurvivorAIShooting shooting = _survivor.AIShooting != null ? _survivor.AIShooting : _survivor.GetComponent<SurvivorAIShooting>();
			if (shooting != null)
			{
				float horizontalAimError = Mathf.Max(0f, settings.NeutralHorizontalAimErrorDegrees);
				float verticalAimError = Mathf.Max(0f, settings.NeutralVerticalAimErrorDegrees);
				shooting.HorizontalAimErrorDegrees = horizontalAimError;
				shooting.VerticalAimErrorDegrees = verticalAimError;
				shooting.ZombieHorizontalAimErrorDegrees = horizontalAimError;
				shooting.ZombieVerticalAimErrorDegrees = verticalAimError;
			}
		}

		public void RestoreOriginalStats()
		{
			if (_hasStatSnapshot == false)
				return;

			_survivor = _survivor != null ? _survivor : GetComponent<Survivor>();
			if (_survivor == null)
				return;

			_survivor.AIMoveSpeed = _statSnapshot.AIMoveSpeed;

			CharacterSensor sensor = _survivor.Sensor != null ? _survivor.Sensor : _survivor.GetComponent<CharacterSensor>();
			if (sensor != null)
			{
				sensor.VisionDistance = _statSnapshot.VisionDistance;
				sensor.ProximityAwarenessRadius = _statSnapshot.ProximityAwarenessRadius;
				sensor.SensorInterval = _statSnapshot.SensorInterval;
			}

			SurvivorAIShooting shooting = _survivor.AIShooting != null ? _survivor.AIShooting : _survivor.GetComponent<SurvivorAIShooting>();
			if (shooting != null)
			{
				shooting.HorizontalAimErrorDegrees = _statSnapshot.HorizontalAimErrorDegrees;
				shooting.VerticalAimErrorDegrees = _statSnapshot.VerticalAimErrorDegrees;
				shooting.ZombieHorizontalAimErrorDegrees = _statSnapshot.ZombieHorizontalAimErrorDegrees;
				shooting.ZombieVerticalAimErrorDegrees = _statSnapshot.ZombieVerticalAimErrorDegrees;
			}

			_hasStatSnapshot = false;
		}

		/// <summary>
		/// Turns this neutral into a roamer that wanders between dynamic spawn areas. Called on scene/state authority
		/// right after the initial area is assigned. The survivor starts by dwelling at its spawn area; once the
		/// random dwell elapses the roam cycle independently picks the next dynamic spawn to head to.
		/// </summary>
		public void EnableRoaming(IReadOnlyList<RoamDestination> destinations)
		{
			_roamDestinations = destinations;
			IsRoaming = destinations != null && destinations.Count > 0;
			if (IsRoaming == false)
				return;

			_roamCenter = PatrolCenter;
			_roamRadius = PatrolRadius;
			_hasRoamTarget = true;
			_reachedRoamArea = true; // spawned inside its own area
			_roamDwellUntil = Time.timeSinceLevelLoad + RandomDwell();
		}

		private void Awake()
		{
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();
		}

		private void Update()
		{
			if (_survivor == null || _survivor.Object == null || _survivor.Object.IsValid == false)
				return;
			// Recruited (no longer neutral) -> restore player-survivor stats and stop roaming.
			if (_survivor.IsNeutral == false)
			{
				RestoreOriginalStats();
				IsRoaming = false;
				return;
			}
			if (IsRoaming == false)
				return;
			// The AI assignment is local to the state authority (where AI input is generated), so drive roaming there.
			if (_survivor.HasStateAuthority == false)
				return;
			if (_survivor.Health == null || _survivor.Health.IsAlive == false)
				return;

			AdvanceRoam();
		}

		private void OnDestroy()
		{
			RestoreOriginalStats();
		}

		private void CaptureStatsIfNeeded()
		{
			if (_hasStatSnapshot)
				return;

			_statSnapshot.AIMoveSpeed = _survivor.AIMoveSpeed;

			CharacterSensor sensor = _survivor.Sensor != null ? _survivor.Sensor : _survivor.GetComponent<CharacterSensor>();
			if (sensor != null)
			{
				_statSnapshot.VisionDistance = sensor.VisionDistance;
				_statSnapshot.ProximityAwarenessRadius = sensor.ProximityAwarenessRadius;
				_statSnapshot.SensorInterval = sensor.SensorInterval;
			}

			SurvivorAIShooting shooting = _survivor.AIShooting != null ? _survivor.AIShooting : _survivor.GetComponent<SurvivorAIShooting>();
			if (shooting != null)
			{
				_statSnapshot.HorizontalAimErrorDegrees = shooting.HorizontalAimErrorDegrees;
				_statSnapshot.VerticalAimErrorDegrees = shooting.VerticalAimErrorDegrees;
				_statSnapshot.ZombieHorizontalAimErrorDegrees = shooting.ZombieHorizontalAimErrorDegrees;
				_statSnapshot.ZombieVerticalAimErrorDegrees = shooting.ZombieVerticalAimErrorDegrees;
			}

			_hasStatSnapshot = true;
		}

		private void AdvanceRoam()
		{
			float now = Time.timeSinceLevelLoad;

			if (_hasRoamTarget == false)
			{
				PickAndAssignNextRoamArea();
				return;
			}

			if (_reachedRoamArea == false)
			{
				float radius = Mathf.Max(0.5f, _roamRadius);
				if (FlatDistanceSqr(_survivor.transform.position, _roamCenter) <= radius * radius)
				{
					_reachedRoamArea = true;
					_roamDwellUntil = now + RandomDwell();
				}
				return;
			}

			if (now >= _roamDwellUntil)
				PickAndAssignNextRoamArea();
		}

		private void PickAndAssignNextRoamArea()
		{
			if (_roamDestinations == null || _roamDestinations.Count == 0)
				return;

			int index = PickNextRoamIndex();
			_lastRoamIndex = index;
			RoamDestination destination = _roamDestinations[index];
			_roamCenter = destination.Center;
			_roamRadius = destination.Radius;
			_hasRoamTarget = true;
			_reachedRoamArea = false;
			PatrolCenter = _roamCenter;
			PatrolRadius = _roamRadius;

			if (_roamRadius > 0f && SurvivorNonCombatAI.TryBuildAssignedAreaPatrolPoints(_survivor, _roamCenter, _roamRadius, out Vector3[] patrolPoints))
			{
				Vector3 entryPoint = patrolPoints != null && patrolPoints.Length > 0 ? patrolPoints[0] : _roamCenter;
				_survivor.SetAI(SurvivorNonCombatAI.RoamArea(_survivor, _roamCenter, _roamRadius, entryPoint, patrolPoints, _survivor.NonCombatAISettings));
			}
			else
			{
				// Could not reach/build the chosen area: idle and retry a different one after a dwell.
				_survivor.SetIdleAI();
				_reachedRoamArea = true;
				_roamDwellUntil = Time.timeSinceLevelLoad + RandomDwell();
			}
		}

		private int PickNextRoamIndex()
		{
			int count = _roamDestinations.Count;
			if (count == 1)
				return 0;

			int index = Random.Range(0, count);
			if (index == _lastRoamIndex)
				index = (index + 1) % count; // avoid immediately re-picking the current area
			return index;
		}

		private float RandomDwell()
		{
			float min = Mathf.Max(0f, RoamDwellTimeMin);
			float max = Mathf.Max(min, RoamDwellTimeMax);
			return max > min ? Random.Range(min, max) : min;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
