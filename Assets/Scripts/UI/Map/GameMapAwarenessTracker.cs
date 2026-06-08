using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public sealed class GameMapAwarenessTracker : MonoBehaviour
	{
		public float EnemyIconForgetDelay = 2f;
		public float PickupIconForgetDelay = 2f;
		public float ZombieIconForgetDelay = 2f;

		private readonly List<KnownEnemyInfo> _directEnemies = new(16);
		private readonly List<KnownPickupInfo> _visiblePickups = new(16);
		private readonly List<NetworkObject> _expired = new(16);
		private readonly Dictionary<NetworkObject, EnemyMapMemory> _enemyMemory = new();
		private readonly Dictionary<NetworkObject, PickupMapMemory> _pickupMemory = new();
		private readonly Dictionary<NetworkObject, ZombieMapMemory> _zombieMemory = new();

		public IReadOnlyDictionary<NetworkObject, EnemyMapMemory> EnemyMemory => _enemyMemory;
		public IReadOnlyDictionary<NetworkObject, PickupMapMemory> PickupMemory => _pickupMemory;
		public IReadOnlyDictionary<NetworkObject, ZombieMapMemory> ZombieMemory => _zombieMemory;

		public void Tick(Gameplay gameplay, NetworkRunner runner, bool revealAll = false)
		{
			if (gameplay == null || runner == null)
				return;

			float now = Time.time;

			// Dev reveal: seed every entity into memory at its live position before the normal sensor pass, so the
			// existing icon rendering shows the whole map. Revealed entries are refreshed each tick (full opacity);
			// they fade out normally once the toggle is turned off.
			if (revealAll)
				RevealEverything(runner, now);

			var sensors = CharacterSensor.ActiveSensors;
			for (int i = 0; i < sensors.Count; i++)
			{
				var sensor = sensors[i];
				if (sensor == null)
					continue;

				var survivor = sensor.GetComponent<Survivor>();
				if (IsSpawnedSurvivor(survivor) == false || survivor.OwnerRef != runner.LocalPlayer)
					continue;
				if (survivor.Health == null || survivor.Health.IsAlive == false)
					continue;

				_directEnemies.Clear();
				sensor.GetDirectKnownEnemies(_directEnemies);

				for (int j = 0; j < _directEnemies.Count; j++)
				{
					var enemy = _directEnemies[j];
					if (enemy.Object == null)
						continue;

					var enemySurvivor = enemy.Object.GetComponent<Survivor>();
					if (enemySurvivor != null)
					{
						if (IsSpawnedSurvivor(enemySurvivor) == false || enemySurvivor.Health == null || enemySurvivor.Health.IsAlive == false)
							continue;

						// Only refresh position / rotation / timestamp when the sensor has a newer
						// sighting tick than the existing memory. Between sightings every field stays
						// frozen — that's what stops the enemy marker from spinning while the target
						// rotates out of view.
						if (_enemyMemory.TryGetValue(enemy.Object, out var existing) && existing.LastSenseTick >= enemy.Tick)
							continue;

						_enemyMemory[enemy.Object] = new EnemyMapMemory(
							enemySurvivor,
							enemy.LastKnownPosition,
							enemySurvivor.transform.eulerAngles.y,
							enemy.Tick,
							now);
						continue;
					}

					var zombie = enemy.Object.GetComponent<ZombieCharacter>();
					if (IsSpawnedZombie(zombie) == false || zombie.Health == null || zombie.Health.IsAlive == false)
						continue;

					if (_zombieMemory.TryGetValue(enemy.Object, out var existingZombie) && existingZombie.LastSenseTick >= enemy.Tick)
						continue;

					_zombieMemory[enemy.Object] = new ZombieMapMemory(zombie, enemy.LastKnownPosition, enemy.Tick, now);
				}

				_visiblePickups.Clear();
				sensor.GetVisiblePickups(_visiblePickups);

				for (int j = 0; j < _visiblePickups.Count; j++)
				{
					var pickup = _visiblePickups[j];
					if (pickup.Object == null || IsPickupDestroyed(pickup))
						continue;

					if (_pickupMemory.TryGetValue(pickup.Object, out var existingPickup) && existingPickup.LastSenseTick >= pickup.Tick)
					{
						// Still update IsActive / type bookkeeping each tick so a depleted pickup gets
						// the inactive color immediately, but preserve the fade timestamp.
						_pickupMemory[pickup.Object] = new PickupMapMemory(
							existingPickup.Position,
							pickup.Type,
							IsPickupActive(pickup),
							existingPickup.LastSenseTick,
							existingPickup.LastSenseTime);
						continue;
					}

					_pickupMemory[pickup.Object] = new PickupMapMemory(
						pickup.Position,
						pickup.Type,
						IsPickupActive(pickup),
						pickup.Tick,
						now);
				}
			}

			_expired.Clear();
			foreach (var pair in _enemyMemory)
			{
				var memory = pair.Value;
				if (IsSpawnedSurvivor(memory.Survivor) == false || memory.Survivor.Health == null || memory.Survivor.Health.IsAlive == false || IsFaded(now, memory.LastSenseTime, EnemyIconForgetDelay))
					_expired.Add(pair.Key);
			}

			for (int i = 0; i < _expired.Count; i++)
				_enemyMemory.Remove(_expired[i]);

			_expired.Clear();
			foreach (var pair in _zombieMemory)
			{
				var memory = pair.Value;
				if (IsSpawnedZombie(memory.Zombie) == false || memory.Zombie.Health == null || memory.Zombie.Health.IsAlive == false || IsFaded(now, memory.LastSenseTime, ZombieIconForgetDelay))
					_expired.Add(pair.Key);
			}

			for (int i = 0; i < _expired.Count; i++)
				_zombieMemory.Remove(_expired[i]);

			_expired.Clear();
			foreach (var pair in _pickupMemory)
			{
				var memory = pair.Value;
				if (pair.Key == null || pair.Key.IsValid == false || IsFaded(now, memory.LastSenseTime, PickupIconForgetDelay))
					_expired.Add(pair.Key);
			}

			for (int i = 0; i < _expired.Count; i++)
				_pickupMemory.Remove(_expired[i]);
		}

		// Seeds memory with every entity at its live position. The normal sensor pass that follows leaves these
		// entries untouched (their sense tick is current, so older sightings do not override them), and the expiry
		// pass keeps them (their sense time is now). Enumerated from the same global registries the sensors use.
		private void RevealEverything(NetworkRunner runner, float now)
		{
			int tick = runner.Tick;

			// Every survivor not owned by the local player (enemies and neutrals alike).
			var sensors = CharacterSensor.ActiveSensors;
			for (int i = 0; i < sensors.Count; i++)
			{
				var sensor = sensors[i];
				var survivor = sensor != null ? sensor.Survivor : null;
				if (survivor == null || IsSpawnedSurvivor(survivor) == false)
					continue;
				if (survivor.OwnerRef == runner.LocalPlayer)
					continue;
				if (survivor.Health == null || survivor.Health.IsAlive == false)
					continue;

				_enemyMemory[survivor.Object] = new EnemyMapMemory(
					survivor,
					survivor.transform.position,
					survivor.transform.eulerAngles.y,
					tick,
					now);
			}

			// Every zombie.
			var zombies = ZombieCharacter.ActiveZombies;
			for (int i = 0; i < zombies.Count; i++)
			{
				var zombie = zombies[i];
				if (IsSpawnedZombie(zombie) == false || zombie.Health == null || zombie.Health.IsAlive == false)
					continue;

				_zombieMemory[zombie.Object] = new ZombieMapMemory(zombie, zombie.transform.position, tick, now);
			}

			// Every pickup.
			for (int i = 0; i < WeaponPickup.ActivePickups.Count; i++)
			{
				var pickup = WeaponPickup.ActivePickups[i];
				if (pickup == null || pickup.Object == null || pickup.IsVisibleForSensor == false)
					continue;

				_pickupMemory[pickup.Object] = new PickupMapMemory(pickup.transform.position, EVisiblePickupType.Weapon, pickup.IsAvailableForSensor, tick, now);
			}

			for (int i = 0; i < HealthPickup.ActivePickups.Count; i++)
			{
				var pickup = HealthPickup.ActivePickups[i];
				if (pickup == null || pickup.Object == null || pickup.IsVisibleForSensor == false)
					continue;

				_pickupMemory[pickup.Object] = new PickupMapMemory(pickup.transform.position, EVisiblePickupType.Health, pickup.IsAvailableForSensor, tick, now);
			}
		}

		public static float ComputeOpacity(float now, float lastSenseTime, float forgetDelay)
		{
			if (forgetDelay <= 0f)
				return 1f;

			return 1f - Mathf.Clamp01((now - lastSenseTime) / forgetDelay);
		}

		private static bool IsFaded(float now, float lastSenseTime, float forgetDelay)
		{
			return forgetDelay > 0f && (now - lastSenseTime) > forgetDelay;
		}

		private static bool IsSpawnedSurvivor(Survivor survivor)
		{
			return survivor != null && survivor.Object != null && survivor.Object.IsValid;
		}

		private static bool IsSpawnedZombie(ZombieCharacter zombie)
		{
			return zombie != null && zombie.Object != null && zombie.Object.IsValid;
		}

		private static bool IsPickupActive(KnownPickupInfo pickup)
		{
			return pickup.Type switch
			{
				EVisiblePickupType.Health => pickup.HealthPickup != null && pickup.HealthPickup.IsAvailableForSensor,
				EVisiblePickupType.Weapon => pickup.WeaponPickup != null && pickup.WeaponPickup.IsAvailableForSensor,
				_ => false,
			};
		}

		private static bool IsPickupDestroyed(KnownPickupInfo pickup)
		{
			return pickup.Type switch
			{
				EVisiblePickupType.Health => pickup.HealthPickup == null || pickup.HealthPickup.IsVisibleForSensor == false,
				EVisiblePickupType.Weapon => pickup.WeaponPickup == null || pickup.WeaponPickup.IsVisibleForSensor == false,
				_ => true,
			};
		}

		public readonly struct EnemyMapMemory
		{
			public readonly Survivor Survivor;
			public readonly Vector3 LastKnownPosition;
			public readonly float LastKnownRotationY;
			public readonly int LastSenseTick;
			public readonly float LastSenseTime;

			public EnemyMapMemory(Survivor survivor, Vector3 lastKnownPosition, float lastKnownRotationY, int lastSenseTick, float lastSenseTime)
			{
				Survivor = survivor;
				LastKnownPosition = lastKnownPosition;
				LastKnownRotationY = lastKnownRotationY;
				LastSenseTick = lastSenseTick;
				LastSenseTime = lastSenseTime;
			}
		}

		public readonly struct PickupMapMemory
		{
			public readonly Vector3 Position;
			public readonly EVisiblePickupType Type;
			public readonly bool IsActive;
			public readonly int LastSenseTick;
			public readonly float LastSenseTime;

			public PickupMapMemory(Vector3 position, EVisiblePickupType type, bool isActive, int lastSenseTick, float lastSenseTime)
			{
				Position = position;
				Type = type;
				IsActive = isActive;
				LastSenseTick = lastSenseTick;
				LastSenseTime = lastSenseTime;
			}
		}

		public readonly struct ZombieMapMemory
		{
			public readonly ZombieCharacter Zombie;
			public readonly Vector3 LastKnownPosition;
			public readonly int LastSenseTick;
			public readonly float LastSenseTime;

			public ZombieMapMemory(ZombieCharacter zombie, Vector3 lastKnownPosition, int lastSenseTick, float lastSenseTime)
			{
				Zombie = zombie;
				LastKnownPosition = lastKnownPosition;
				LastSenseTick = lastSenseTick;
				LastSenseTime = lastSenseTime;
			}
		}
	}
}
