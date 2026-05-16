using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public sealed class GameMapAwarenessTracker : MonoBehaviour
	{
		public float EnemyIconForgetDelay = 2f;
		public float PickupIconForgetDelay = 2f;

		private readonly List<KnownEnemyInfo> _directEnemies = new(16);
		private readonly List<KnownPickupInfo> _visiblePickups = new(16);
		private readonly List<NetworkObject> _expired = new(16);
		private readonly Dictionary<NetworkObject, EnemyMapMemory> _enemyMemory = new();
		private readonly Dictionary<NetworkObject, PickupMapMemory> _pickupMemory = new();

		public IReadOnlyDictionary<NetworkObject, EnemyMapMemory> EnemyMemory => _enemyMemory;
		public IReadOnlyDictionary<NetworkObject, PickupMapMemory> PickupMemory => _pickupMemory;

		public void Tick(Gameplay gameplay, NetworkRunner runner)
		{
			if (gameplay == null || runner == null)
				return;

			float now = Time.time;
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
					if (IsSpawnedSurvivor(enemySurvivor) == false || enemySurvivor.Health == null || enemySurvivor.Health.IsAlive == false)
						continue;

					_enemyMemory[enemy.Object] = new EnemyMapMemory(enemySurvivor, enemy.LastKnownPosition, now + Mathf.Max(0f, EnemyIconForgetDelay));
				}

				_visiblePickups.Clear();
				sensor.GetVisiblePickups(_visiblePickups);

				for (int j = 0; j < _visiblePickups.Count; j++)
				{
					var pickup = _visiblePickups[j];
					if (pickup.Object == null || IsPickupDestroyed(pickup))
						continue;

					_pickupMemory[pickup.Object] = new PickupMapMemory(pickup.Position, pickup.Type, IsPickupActive(pickup), now + Mathf.Max(0f, PickupIconForgetDelay));
				}
			}

			_expired.Clear();
			foreach (var pair in _enemyMemory)
			{
				var memory = pair.Value;
				if (IsSpawnedSurvivor(memory.Survivor) == false || memory.Survivor.Health == null || memory.Survivor.Health.IsAlive == false || memory.ExpiresAt <= now)
					_expired.Add(pair.Key);
			}

			for (int i = 0; i < _expired.Count; i++)
				_enemyMemory.Remove(_expired[i]);

			_expired.Clear();
			foreach (var pair in _pickupMemory)
			{
				var memory = pair.Value;
				if (pair.Key == null || pair.Key.IsValid == false || memory.ExpiresAt <= now)
					_expired.Add(pair.Key);
			}

			for (int i = 0; i < _expired.Count; i++)
				_pickupMemory.Remove(_expired[i]);
		}

		private static bool IsSpawnedSurvivor(Survivor survivor)
		{
			return survivor != null && survivor.Object != null && survivor.Object.IsValid;
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
			public readonly float ExpiresAt;

			public EnemyMapMemory(Survivor survivor, Vector3 lastKnownPosition, float expiresAt)
			{
				Survivor = survivor;
				LastKnownPosition = lastKnownPosition;
				ExpiresAt = expiresAt;
			}
		}

		public readonly struct PickupMapMemory
		{
			public readonly Vector3 Position;
			public readonly EVisiblePickupType Type;
			public readonly bool IsActive;
			public readonly float ExpiresAt;

			public PickupMapMemory(Vector3 position, EVisiblePickupType type, bool isActive, float expiresAt)
			{
				Position = position;
				Type = type;
				IsActive = isActive;
				ExpiresAt = expiresAt;
			}
		}
	}
}
