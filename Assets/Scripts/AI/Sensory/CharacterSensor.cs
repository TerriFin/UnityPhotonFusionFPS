using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public enum ESensoryStimulus
	{
		Proximity,
		Vision,
	}

	public readonly struct KnownEnemyInfo
	{
		public readonly NetworkObject Object;
		public readonly Vector3 LastKnownPosition;
		public readonly Vector3 ApproximateSourcePosition;
		public readonly ESensoryStimulus Stimulus;
		public readonly int Tick;

		public KnownEnemyInfo(NetworkObject obj, Vector3 lastKnownPosition, Vector3 approximateSourcePosition, ESensoryStimulus stimulus, int tick)
		{
			Object = obj;
			LastKnownPosition = lastKnownPosition;
			ApproximateSourcePosition = approximateSourcePosition;
			Stimulus = stimulus;
			Tick = tick;
		}
	}

	public enum EVisiblePickupType
	{
		Health,
		Weapon,
	}

	public readonly struct KnownPickupInfo
	{
		public readonly NetworkObject Object;
		public readonly HealthPickup HealthPickup;
		public readonly WeaponPickup WeaponPickup;
		public readonly EVisiblePickupType Type;
		public readonly EWeaponType WeaponType;
		public readonly Vector3 Position;
		public readonly int Tick;

		public KnownPickupInfo(HealthPickup pickup, Vector3 position, int tick)
		{
			Object = pickup != null ? pickup.Object : null;
			HealthPickup = pickup;
			WeaponPickup = null;
			Type = EVisiblePickupType.Health;
			WeaponType = default;
			Position = position;
			Tick = tick;
		}

		public KnownPickupInfo(WeaponPickup pickup, Vector3 position, int tick)
		{
			Object = pickup != null ? pickup.Object : null;
			HealthPickup = null;
			WeaponPickup = pickup;
			Type = EVisiblePickupType.Weapon;
			WeaponType = pickup != null ? pickup.Type : default;
			Position = position;
			Tick = tick;
		}
	}

	[DisallowMultipleComponent]
	public sealed class CharacterSensor : MonoBehaviour
	{
		private const int DefaultMaxKnownEntries = 8;

		[Header("Awareness")]
		public float ProximityAwarenessRadius = 4f;
		public float NoiseAwarenessRadius = 10f;
		public float BulletImpactAwarenessRadius = 12f;

		[Header("Vision")]
		public float VisionDistance = 22f;
		[Range(1f, 180f)]
		public float VisionAngle = 90f;
		public float EyeHeight = 1.6f;
		public LayerMask VisionBlockers;

		[Header("Runtime")]
		public bool DisableWhenPossessed = true;
		public float SensorInterval = 0.2f;
		public float MemoryDuration = 4f;
		public int MaxKnownEntries = DefaultMaxKnownEntries;
		public int MaxKnownPickups = DefaultMaxKnownEntries;

		private readonly List<KnownEnemyInfo> _knownEnemies = new(DefaultMaxKnownEntries);
		private readonly List<KnownPickupInfo> _knownPickups = new(DefaultMaxKnownEntries);
		private Survivor _survivor;
		private ZombieCharacter _zombie;
		private NetworkObject _networkObject;
		private float _nextSenseTime;
		private int _lastEnemyExpireTick = int.MinValue;
		private int _lastPickupExpireTick = int.MinValue;

		internal static readonly List<CharacterSensor> ActiveSensors = new(64);

		// Highest awareness radii seen on any registered sensor. Conservative (never shrinks); lets the global
		// noise/bullet-impact events query the spatial hash instead of fanning out to every sensor in the game.
		internal static float MaxNoiseAwarenessRadius { get; private set; }
		internal static float MaxBulletImpactAwarenessRadius { get; private set; }

		// Scans used to test every active sensor/pickup in the game per scan tick — quadratic in actor count
		// (~85k candidate checks/second at the audit's 170-actor scale before line-of-sight tests). Each hash
		// rebuilds at most once per rendered frame and feeds the scan only candidates within range-covering
		// cells; all existing eligibility/distance/LOS filtering below is unchanged.
		private static readonly SpatialHash2D<CharacterSensor> CharacterHash = new(16f);
		private static readonly List<CharacterSensor> CharacterQueryResults = new(64);
		// Separate buffer for the global noise/impact events so an event raised mid-scan cannot clobber a scan.
		private static readonly List<CharacterSensor> EventQueryResults = new(64);
		private static readonly SpatialHash2D<WeaponPickup> WeaponPickupHash = new(16f);
		private static readonly List<WeaponPickup> WeaponPickupQueryResults = new(32);
		private static readonly SpatialHash2D<HealthPickup> HealthPickupHash = new(16f);
		private static readonly List<HealthPickup> HealthPickupQueryResults = new(32);

		public NetworkObject NetworkObject => _networkObject;
		public Survivor Survivor => _survivor;
		public ZombieCharacter Zombie => _zombie;

		// Sensors within radius of the position (spatial-hash superset — callers keep their exact checks).
		internal static void QuerySensorsForEvent(Vector3 position, float radius, List<CharacterSensor> results)
		{
			CharacterHash.RebuildIfStale(ActiveSensors);
			CharacterHash.Query(position, radius, results);
		}

		internal static List<CharacterSensor> EventQueryBuffer => EventQueryResults;

		public bool IsStateAuthority => _networkObject != null && _networkObject.HasStateAuthority;

		public bool IsSenseEnabled
		{
			get
			{
				if (IsStateAuthority == false)
					return false;
				if (IsAliveCharacter() == false)
					return false;
				if (DisableWhenPossessed && _survivor != null && _survivor.IsActiveCharacter())
					return false;
				return true;
			}
		}

		private bool IsDirectScanEnabled
		{
			get
			{
				if (_networkObject == null)
					return false;
				if (_networkObject.HasStateAuthority == false && _networkObject.HasInputAuthority == false)
					return false;
				if (IsAliveCharacter() == false)
					return false;
				if (DisableWhenPossessed && _survivor != null && _survivor.IsActiveCharacter() && _networkObject.HasInputAuthority == false)
					return false;
				return true;
			}
		}

		public bool TryGetClosestKnownEnemy(out KnownEnemyInfo enemy)
		{
			ExpireOldEntries();
			return TryGetClosestKnownEnemy(out enemy, false);
		}

		public bool TryGetClosestDirectEnemy(out KnownEnemyInfo enemy)
		{
			ExpireOldEntries();
			return TryGetClosestKnownEnemy(out enemy, true);
		}

		// Targeted lookup: is this specific object currently directly sensed (vision/proximity), and with what
		// info? Lets a chaser check whether its committed target is still visible without scanning for a new one.
		public bool TryGetDirectEnemy(NetworkObject target, out KnownEnemyInfo enemy)
		{
			enemy = default;
			if (target == null)
				return false;

			ExpireOldEntries();

			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				var candidate = _knownEnemies[i];
				if (candidate.Object != target)
					continue;

				if (IsDeadRememberedCharacter(candidate))
				{
					_knownEnemies.RemoveAt(i);
					return false;
				}

				if (IsDirectCombatStimulus(candidate.Stimulus) == false)
					return false;

				enemy = candidate;
				return true;
			}

			return false;
		}

		public void GetDirectKnownEnemies(List<KnownEnemyInfo> results)
		{
			if (results == null)
				return;

			ExpireOldEntries();

			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				var candidate = _knownEnemies[i];
				if (IsDeadRememberedCharacter(candidate))
				{
					_knownEnemies.RemoveAt(i);
					i--;
					continue;
				}

				if (IsDirectCombatStimulus(candidate.Stimulus))
					results.Add(candidate);
			}
		}

		public bool TryGetClosestVisiblePickup(out KnownPickupInfo pickup)
		{
			ExpireOldPickupEntries();

			pickup = default;
			float closestDistanceSqr = float.MaxValue;
			Vector3 origin = transform.position;

			for (int i = 0; i < _knownPickups.Count; i++)
			{
				var candidate = _knownPickups[i];
				if (IsDestroyedPickup(candidate))
				{
					_knownPickups.RemoveAt(i);
					i--;
					continue;
				}

				float distanceSqr = (candidate.Position - origin).sqrMagnitude;
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				pickup = candidate;
			}

			return closestDistanceSqr < float.MaxValue;
		}

		public void GetVisiblePickups(List<KnownPickupInfo> results)
		{
			if (results == null)
				return;

			ExpireOldPickupEntries();

			for (int i = 0; i < _knownPickups.Count; i++)
			{
				var candidate = _knownPickups[i];
				if (IsDestroyedPickup(candidate))
				{
					_knownPickups.RemoveAt(i);
					i--;
					continue;
				}

				results.Add(candidate);
			}
		}

		public bool HasLineOfSightTo(CharacterSensor other)
		{
			return other != null && HasLineOfSightToPosition(other.GetEyePosition());
		}

		public bool HasLineOfSightToPosition(Vector3 target)
		{
			return IsLineBlocked(target) == false;
		}

		private bool TryGetClosestKnownEnemy(out KnownEnemyInfo enemy, bool directOnly)
		{
			enemy = default;
			float closestDistanceSqr = float.MaxValue;
			Vector3 origin = transform.position;

			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				var candidate = _knownEnemies[i];
				if (IsDeadRememberedCharacter(candidate))
				{
					_knownEnemies.RemoveAt(i);
					i--;
					continue;
				}

				if (directOnly && IsDirectCombatStimulus(candidate.Stimulus) == false)
					continue;

				float distanceSqr = (candidate.LastKnownPosition - origin).sqrMagnitude;
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				enemy = candidate;
			}

			return closestDistanceSqr < float.MaxValue;
		}

		private static bool IsDirectCombatStimulus(ESensoryStimulus stimulus)
		{
			return stimulus == ESensoryStimulus.Vision || stimulus == ESensoryStimulus.Proximity;
		}

		private static bool IsDeadRememberedCharacter(KnownEnemyInfo info)
		{
			if (info.Object == null)
				return false;

			// A remembered enemy can despawn while the memory entry is still alive (disconnect, corpse
			// cleanup). Its [Networked] health is inaccessible then — treat it as gone so the entry is removed
			// instead of throwing.
			if (info.Object.IsValid == false)
				return true;

			var survivor = Survivor.GetFrom(info.Object);
			if (survivor != null)
				return survivor.Health == null || survivor.Health.IsAlive == false;

			var zombie = ZombieCharacter.GetFrom(info.Object);
			return zombie != null && (zombie.Health == null || zombie.Health.IsAlive == false);
		}

		public bool TryGetLookRotationDelta(float maxYawDegreesPerTick, out Vector2 lookRotationDelta)
		{
			lookRotationDelta = default;

			if (_survivor == null || TryGetClosestKnownEnemy(out var enemy) == false)
				return false;

			Vector3 toEnemy = enemy.LastKnownPosition - transform.position;
			toEnemy.y = 0f;
			if (toEnemy.sqrMagnitude < 0.001f)
				return false;

			float desiredYaw = Quaternion.LookRotation(toEnemy).eulerAngles.y;
			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);
			lookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -maxYawDegreesPerTick, maxYawDegreesPerTick));
			return true;
		}

		// Cheapest checks first: these run once per nearby sensor for every shot fired in the game, so the
		// distance rejection happens before authority/aliveness reads and the faction lookup.
		public void RecordNoise(Vector3 noisePosition, NetworkObject source, float radius)
		{
			if (NoiseAwarenessRadius <= 0f)
				return;

			float effectiveRadius = radius > 0f ? Mathf.Min(radius, NoiseAwarenessRadius) : NoiseAwarenessRadius;
			if ((noisePosition - transform.position).sqrMagnitude > effectiveRadius * effectiveRadius)
				return;
			if (_survivor == null && _zombie == null)
				return;
			if (IsSenseEnabled == false)
				return;
			if (IsEnemySource(source) == false)
				return;

			if (_survivor != null)
				_survivor.ReceiveInvestigationStimulus(noisePosition, GetTick());
			else
				_zombie.ReceiveInvestigationStimulus(noisePosition, GetTick());
		}

		public void RecordBulletImpact(Vector3 impactPosition, Vector3 approximateShooterPosition, NetworkObject shooter)
		{
			if (BulletImpactAwarenessRadius <= 0f)
				return;
			if ((impactPosition - transform.position).sqrMagnitude > BulletImpactAwarenessRadius * BulletImpactAwarenessRadius)
				return;
			if (_survivor == null && _zombie == null)
				return;
			if (IsSenseEnabled == false)
				return;
			if (IsEnemySource(shooter) == false)
				return;

			if (_survivor != null)
				_survivor.ReceiveInvestigationStimulus(approximateShooterPosition, GetTick());
			else
				_zombie.ReceiveInvestigationStimulus(approximateShooterPosition, GetTick());
		}

		private void Awake()
		{
			_survivor = GetComponent<Survivor>();
			_zombie = GetComponent<ZombieCharacter>();
			_networkObject = GetComponent<NetworkObject>();
		}

		private void OnEnable()
		{
			if (ActiveSensors.Contains(this) == false)
			{
				ActiveSensors.Add(this);
			}

			MaxNoiseAwarenessRadius = Mathf.Max(MaxNoiseAwarenessRadius, NoiseAwarenessRadius);
			MaxBulletImpactAwarenessRadius = Mathf.Max(MaxBulletImpactAwarenessRadius, BulletImpactAwarenessRadius);
		}

		private void OnDisable()
		{
			ActiveSensors.Remove(this);
		}

		private void FixedUpdate()
		{
			if (IsDirectScanEnabled == false)
				return;
			if (Time.timeSinceLevelLoad < _nextSenseTime)
				return;

			_nextSenseTime = Time.timeSinceLevelLoad + Mathf.Max(0.02f, SensorInterval);
			ExpireOldEntries();
			ExpireOldPickupEntries();
			ScanCharacters();
			ScanPickups();
		}

		private bool IsAliveCharacter()
		{
			// Health.IsAlive reads a [Networked] property, which throws for an object that has despawned (or
			// not yet spawned). Validity must be checked first — ScanCharacters can receive a stale spatial-hash
			// entry whose object despawned earlier in the same rendered frame.
			if (_survivor != null)
			{
				if (_survivor.Object == null || _survivor.Object.IsValid == false)
					return false;
				return _survivor.Health != null && _survivor.Health.IsAlive;
			}

			if (_zombie != null)
			{
				if (_zombie.Object == null || _zombie.Object.IsValid == false)
					return false;
				return _zombie.Health != null && _zombie.Health.IsAlive;
			}

			return true;
		}

		private void ScanCharacters()
		{
			CharacterHash.RebuildIfStale(ActiveSensors);
			CharacterHash.Query(transform.position, Mathf.Max(VisionDistance, ProximityAwarenessRadius), CharacterQueryResults);

			for (int i = CharacterQueryResults.Count - 1; i >= 0; i--)
			{
				var other = CharacterQueryResults[i];

				if (other == this || other.IsAliveCharacter() == false)
					continue;
				if (IsEnemy(other) == false)
					continue;

				Vector3 otherPosition = other.transform.position;
				Vector3 offset = otherPosition - transform.position;
				offset.y = 0f;
				float distanceSqr = offset.sqrMagnitude;

				if (ProximityAwarenessRadius > 0f && distanceSqr <= ProximityAwarenessRadius * ProximityAwarenessRadius)
				{
					if (CanUseProximityAwareness(other))
					{
						Remember(new KnownEnemyInfo(other.NetworkObject, otherPosition, otherPosition, ESensoryStimulus.Proximity, GetTick()));
						continue;
					}
				}

				if (CanSee(other, distanceSqr))
				{
					Remember(new KnownEnemyInfo(other.NetworkObject, otherPosition, otherPosition, ESensoryStimulus.Vision, GetTick()));
				}
			}
		}

		private void ScanPickups()
		{
			if (_survivor == null)
				return;

			ScanWeaponPickups();
			ScanHealthPickups();
		}

		private void ScanWeaponPickups()
		{
			WeaponPickupHash.RebuildIfStale(WeaponPickup.ActivePickups);
			WeaponPickupHash.Query(transform.position, Mathf.Max(VisionDistance, ProximityAwarenessRadius), WeaponPickupQueryResults);

			for (int i = WeaponPickupQueryResults.Count - 1; i >= 0; i--)
			{
				WeaponPickup pickup = WeaponPickupQueryResults[i];
				if (pickup.IsVisibleForSensor == false)
					continue;

				Vector3 position = pickup.transform.position;
				Vector3 offset = position - transform.position;
				offset.y = 0f;
				float distanceSqr = offset.sqrMagnitude;
				Vector3 sensorPosition = position + Vector3.up * 0.5f;

				if (CanSensePickupByProximity(sensorPosition, distanceSqr) || CanSeePosition(sensorPosition, distanceSqr))
					Remember(new KnownPickupInfo(pickup, position, GetTick()));
			}
		}

		private void ScanHealthPickups()
		{
			HealthPickupHash.RebuildIfStale(HealthPickup.ActivePickups);
			HealthPickupHash.Query(transform.position, Mathf.Max(VisionDistance, ProximityAwarenessRadius), HealthPickupQueryResults);

			for (int i = HealthPickupQueryResults.Count - 1; i >= 0; i--)
			{
				HealthPickup pickup = HealthPickupQueryResults[i];
				if (pickup.IsVisibleForSensor == false)
					continue;

				Vector3 position = pickup.transform.position;
				Vector3 offset = position - transform.position;
				offset.y = 0f;
				float distanceSqr = offset.sqrMagnitude;
				Vector3 sensorPosition = position + Vector3.up * 0.5f;

				if (CanSensePickupByProximity(sensorPosition, distanceSqr) || CanSeePosition(sensorPosition, distanceSqr))
					Remember(new KnownPickupInfo(pickup, position, GetTick()));
			}
		}

		private bool CanSee(CharacterSensor other, float distanceSqr)
		{
			return CanSeePosition(other.GetEyePosition(), distanceSqr);
		}

		private bool CanUseProximityAwareness(CharacterSensor other)
		{
			// Zombies keep their permissive close-range survivor detection. Survivors still get all-around
			// proximity awareness, but only through an unobstructed line so walls do not create combat targets.
			return _survivor == null || HasLineOfSightTo(other);
		}

		private bool CanSensePickupByProximity(Vector3 target, float distanceSqr)
		{
			if (_survivor == null || ProximityAwarenessRadius <= 0f)
				return false;
			if (distanceSqr > ProximityAwarenessRadius * ProximityAwarenessRadius)
				return false;

			return HasLineOfSightToPosition(target);
		}

		private bool CanSeePosition(Vector3 target, float distanceSqr)
		{
			if (VisionDistance <= 0f || distanceSqr > VisionDistance * VisionDistance)
				return false;

			Vector3 origin = GetEyePosition();
			Vector3 toTarget = target - origin;
			Vector3 flatToTarget = toTarget;
			flatToTarget.y = 0f;

			if (flatToTarget.sqrMagnitude < 0.001f)
				return true;

			Vector3 forward = transform.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude < 0.001f)
				forward = transform.forward;

			float dot = Vector3.Dot(forward.normalized, flatToTarget.normalized);
			float minDot = Mathf.Cos(VisionAngle * 0.5f * Mathf.Deg2Rad);
			if (dot < minDot)
				return false;

			if (IsLineBlocked(target))
				return false;

			return true;
		}

		private bool IsLineBlocked(Vector3 target)
		{
			if (VisionBlockers.value == 0)
				return false;

			Vector3 origin = GetEyePosition();
			if ((target - origin).sqrMagnitude < 0.001f)
				return false;

			return Physics.Linecast(origin, target, VisionBlockers, QueryTriggerInteraction.Ignore);
		}

		private bool IsEnemy(CharacterSensor other)
		{
			if (_survivor != null && other._survivor != null)
				return CharacterFactionUtility.AreSurvivorsAllied(_survivor, other._survivor) == false;

			if (_zombie != null && other._survivor != null)
				return true;

			if (_survivor != null && other._zombie != null)
				return true;

			return false;
		}

		private bool IsEnemySource(NetworkObject source)
		{
			if (source == null)
				return true;

			var sourceZombie = ZombieCharacter.GetFrom(source);
			if (_zombie != null)
				return sourceZombie == null;

			if (_survivor == null)
				return true;
			if (sourceZombie != null)
				return true;

			return CharacterFactionUtility.IsEnemyNoiseSourceForSurvivor(_survivor, source);
		}

		private Vector3 GetEyePosition()
		{
			return transform.position + Vector3.up * EyeHeight;
		}

		private void Remember(KnownEnemyInfo info)
		{
			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				if (_knownEnemies[i].Object == info.Object && info.Object != null)
				{
					_knownEnemies[i] = info;
					return;
				}
			}

			int maxEntries = Mathf.Max(1, MaxKnownEntries);
			if (_knownEnemies.Count >= maxEntries)
			{
				_knownEnemies.RemoveAt(0);
			}

			_knownEnemies.Add(info);
		}

		private void Remember(KnownPickupInfo info)
		{
			for (int i = 0; i < _knownPickups.Count; i++)
			{
				if (_knownPickups[i].Object == info.Object && info.Object != null)
				{
					_knownPickups[i] = info;
					return;
				}
			}

			int maxEntries = Mathf.Max(1, MaxKnownPickups);
			if (_knownPickups.Count >= maxEntries)
			{
				_knownPickups.RemoveAt(0);
			}

			_knownPickups.Add(info);
		}

		private void ExpireOldEntries()
		{
			NetworkRunner runner = GetRunner();
			if (runner == null)
				return;

			// The AI queries known enemies several times within one simulation tick; nothing can expire between
			// those calls, so run the age/faction sweep once per tick. Dead/despawned entries are additionally
			// filtered (and pruned) by every query loop itself.
			if (runner.Tick == _lastEnemyExpireTick)
				return;
			_lastEnemyExpireTick = runner.Tick;

			bool expireByAge = MemoryDuration > 0f;
			int maxAgeTicks = expireByAge ? Mathf.CeilToInt(MemoryDuration / runner.DeltaTime) : int.MaxValue;
			int currentTick = GetTick();

			for (int i = _knownEnemies.Count - 1; i >= 0; i--)
			{
				KnownEnemyInfo enemy = _knownEnemies[i];
				if (IsRememberedAlliedSurvivor(enemy) ||
				    (expireByAge && currentTick - enemy.Tick > maxAgeTicks))
				{
					_knownEnemies.RemoveAt(i);
				}
			}
		}

		private bool IsRememberedAlliedSurvivor(KnownEnemyInfo info)
		{
			if (_survivor == null || info.Object == null || info.Object.IsValid == false)
				return false;

			Survivor other = Survivor.GetFrom(info.Object);
			return other != null && CharacterFactionUtility.AreSurvivorsAllied(_survivor, other);
		}

		private void ExpireOldPickupEntries()
		{
			NetworkRunner runner = GetRunner();
			if (runner == null || MemoryDuration <= 0f)
				return;

			if (runner.Tick == _lastPickupExpireTick)
				return;
			_lastPickupExpireTick = runner.Tick;

			int maxAgeTicks = Mathf.CeilToInt(MemoryDuration / runner.DeltaTime);
			int currentTick = GetTick();

			for (int i = _knownPickups.Count - 1; i >= 0; i--)
			{
				if (currentTick - _knownPickups[i].Tick > maxAgeTicks || IsDestroyedPickup(_knownPickups[i]))
				{
					_knownPickups.RemoveAt(i);
				}
			}
		}

		private static bool IsDestroyedPickup(KnownPickupInfo info)
		{
			return info.Type switch
			{
				EVisiblePickupType.Health => info.HealthPickup == null || info.HealthPickup.IsVisibleForSensor == false,
				EVisiblePickupType.Weapon => info.WeaponPickup == null || info.WeaponPickup.IsVisibleForSensor == false,
				_ => true,
			};
		}

		private int GetTick()
		{
			NetworkRunner runner = GetRunner();
			return runner != null ? runner.Tick : 0;
		}

		private NetworkRunner GetRunner()
		{
			if (_survivor != null)
				return _survivor.Runner;
			if (_zombie != null)
				return _zombie.Runner;
			return null;
		}
	}
}
