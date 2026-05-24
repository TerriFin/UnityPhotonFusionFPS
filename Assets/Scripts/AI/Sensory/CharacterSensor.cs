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

		internal static readonly List<CharacterSensor> ActiveSensors = new(64);

		public NetworkObject NetworkObject => _networkObject;
		public Survivor Survivor => _survivor;
		public ZombieCharacter Zombie => _zombie;

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

			var survivor = info.Object.GetComponent<Survivor>();
			if (survivor != null)
				return survivor.Health == null || survivor.Health.IsAlive == false;

			var zombie = info.Object.GetComponent<ZombieCharacter>();
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

		public void RecordNoise(Vector3 noisePosition, NetworkObject source, float radius)
		{
			if (IsSenseEnabled == false || NoiseAwarenessRadius <= 0f)
				return;
			if (_survivor == null && _zombie == null)
				return;
			if (IsEnemySource(source) == false)
				return;

			float effectiveRadius = radius > 0f ? Mathf.Min(radius, NoiseAwarenessRadius) : NoiseAwarenessRadius;
			if ((noisePosition - transform.position).sqrMagnitude > effectiveRadius * effectiveRadius)
				return;

			if (_survivor != null)
				_survivor.ReceiveInvestigationStimulus(noisePosition, GetTick());
			else
				_zombie.ReceiveInvestigationStimulus(noisePosition, GetTick());
		}

		public void RecordBulletImpact(Vector3 impactPosition, Vector3 approximateShooterPosition, NetworkObject shooter)
		{
			if (IsSenseEnabled == false || BulletImpactAwarenessRadius <= 0f)
				return;
			if ((_survivor == null && _zombie == null) || IsEnemySource(shooter) == false)
				return;
			if ((impactPosition - transform.position).sqrMagnitude > BulletImpactAwarenessRadius * BulletImpactAwarenessRadius)
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
			if (_survivor != null)
				return _survivor.Health != null && _survivor.Health.IsAlive;
			if (_zombie != null)
				return _zombie.Health != null && _zombie.Health.IsAlive;

			return true;
		}

		private void ScanCharacters()
		{
			for (int i = ActiveSensors.Count - 1; i >= 0; i--)
			{
				var other = ActiveSensors[i];
				if (other == null)
				{
					ActiveSensors.RemoveAt(i);
					continue;
				}

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
					Remember(new KnownEnemyInfo(other.NetworkObject, otherPosition, otherPosition, ESensoryStimulus.Proximity, GetTick()));
					continue;
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
			for (int i = WeaponPickup.ActivePickups.Count - 1; i >= 0; i--)
			{
				WeaponPickup pickup = WeaponPickup.ActivePickups[i];
				if (pickup == null)
				{
					WeaponPickup.ActivePickups.RemoveAt(i);
					continue;
				}
				if (pickup.IsVisibleForSensor == false)
					continue;

				Vector3 position = pickup.transform.position;
				Vector3 offset = position - transform.position;
				offset.y = 0f;
				float distanceSqr = offset.sqrMagnitude;

				if (CanSeePosition(position + Vector3.up * 0.5f, distanceSqr))
					Remember(new KnownPickupInfo(pickup, position, GetTick()));
			}
		}

		private void ScanHealthPickups()
		{
			for (int i = HealthPickup.ActivePickups.Count - 1; i >= 0; i--)
			{
				HealthPickup pickup = HealthPickup.ActivePickups[i];
				if (pickup == null)
				{
					HealthPickup.ActivePickups.RemoveAt(i);
					continue;
				}
				if (pickup.IsVisibleForSensor == false)
					continue;

				Vector3 position = pickup.transform.position;
				Vector3 offset = position - transform.position;
				offset.y = 0f;
				float distanceSqr = offset.sqrMagnitude;

				if (CanSeePosition(position + Vector3.up * 0.5f, distanceSqr))
					Remember(new KnownPickupInfo(pickup, position, GetTick()));
			}
		}

		private bool CanSee(CharacterSensor other, float distanceSqr)
		{
			return CanSeePosition(other.GetEyePosition(), distanceSqr);
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

			if (VisionBlockers.value != 0 && Physics.Linecast(origin, target, VisionBlockers, QueryTriggerInteraction.Ignore))
				return false;

			return true;
		}

		private bool IsEnemy(CharacterSensor other)
		{
			if (_survivor != null && other._survivor != null)
				return _survivor.OwnerRef != other._survivor.OwnerRef;

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

			var sourceZombie = source.GetComponent<ZombieCharacter>();
			if (_zombie != null)
				return sourceZombie == null;

			if (_survivor == null)
				return true;
			if (sourceZombie != null)
				return true;
			if (source.InputAuthority.IsRealPlayer == false)
				return true;

			return source.InputAuthority != _survivor.OwnerRef;
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
			if (runner == null || MemoryDuration <= 0f)
				return;

			int maxAgeTicks = Mathf.CeilToInt(MemoryDuration / runner.DeltaTime);
			int currentTick = GetTick();

			for (int i = _knownEnemies.Count - 1; i >= 0; i--)
			{
				if (currentTick - _knownEnemies[i].Tick > maxAgeTicks)
				{
					_knownEnemies.RemoveAt(i);
				}
			}
		}

		private void ExpireOldPickupEntries()
		{
			NetworkRunner runner = GetRunner();
			if (runner == null || MemoryDuration <= 0f)
				return;

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
