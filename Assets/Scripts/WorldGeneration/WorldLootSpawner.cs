using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public class WorldLootSpawner : MonoBehaviour
	{
		[Header("Setup")]
		public BuildingPlacementGenerator BuildingGenerator;
		public WorldLootSpawnSettings Settings;
		public NetworkRunner Runner;

		[Header("Generation")]
		public bool ClearBeforeGenerate = true;
		public bool FindRunnerIfMissing = true;

		private readonly List<NetworkObject> _spawnedPickups = new();

		[ContextMenu("Generate Loot")]
		public void Generate()
		{
			SpawnLoot();
		}

		public void SpawnLoot()
		{
			if (Settings == null)
			{
				Debug.LogWarning($"{nameof(WorldLootSpawner)} on {name} has no settings asset.", this);
				return;
			}

			if (BuildingGenerator == null)
				BuildingGenerator = GetComponent<BuildingPlacementGenerator>();

			if (BuildingGenerator == null || BuildingGenerator.GeneratedRoot == null)
			{
				Debug.LogWarning($"{nameof(WorldLootSpawner)} could not find generated buildings to scan for pickup markers.", this);
				return;
			}

			NetworkRunner runner = GetRunner();
			if (runner == null)
			{
				Debug.LogWarning($"{nameof(WorldLootSpawner)} could not spawn loot because no {nameof(NetworkRunner)} was assigned or found.", this);
				return;
			}

			if (CanSpawnLoot(runner) == false)
				return;

			if (ClearBeforeGenerate)
				ClearSpawnedPickups(runner);

			List<PickupSpawnPoint> markers = CollectValidMarkers();
			int spawnCount = Mathf.Clamp(Mathf.RoundToInt(markers.Count * Mathf.Clamp01(Settings.PickupPointUsage)), 0, markers.Count);
			if (spawnCount <= 0)
				return;

			var random = new System.Random(GetSeed());
			Shuffle(markers, random);

			for (int i = 0; i < spawnCount; i++)
			{
				NetworkObject prefab = ChoosePickupPrefab(markers[i].Category, random);
				if (prefab == null)
					continue;

				runner.SpawnAsync(prefab, markers[i].transform.position, markers[i].transform.rotation, null, null, default, spawn => AddSpawnedPickup(spawn.Object));
			}
		}

		[ContextMenu("Clear Generated Loot")]
		public void ClearGenerated()
		{
			NetworkRunner runner = GetRunner();
			if (runner == null)
			{
				RemoveMissingSpawnedPickups();
				return;
			}

			if (CanSpawnLoot(runner) == false)
				return;

			ClearSpawnedPickups(runner);
		}

		private bool CanSpawnLoot(NetworkRunner runner)
		{
			return runner != null && runner.IsSceneAuthority;
		}

		private NetworkRunner GetRunner()
		{
			if (Runner != null)
				return Runner;

			if (FindRunnerIfMissing)
				Runner = FindObjectOfType<NetworkRunner>();

			return Runner;
		}

		private int GetSeed()
		{
			int roadSeed = BuildingGenerator != null && BuildingGenerator.RoadGenerator != null ? BuildingGenerator.RoadGenerator.Seed : 0;
			return roadSeed + Settings.SeedOffset;
		}

		private List<PickupSpawnPoint> CollectValidMarkers()
		{
			var validMarkers = new List<PickupSpawnPoint>();
			PickupSpawnPoint[] markers = BuildingGenerator.GeneratedRoot.GetComponentsInChildren<PickupSpawnPoint>(true);

			for (int i = 0; i < markers.Length; i++)
			{
				PickupSpawnPoint marker = markers[i];
				if (marker == null || HasPickupPool(marker.Category) == false)
					continue;

				validMarkers.Add(marker);
			}

			return validMarkers;
		}

		private bool HasPickupPool(PickupSpawnPoint.PickupSpawnCategory category)
		{
			return GetPickupPool(category).Count > 0;
		}

		private NetworkObject ChoosePickupPrefab(PickupSpawnPoint.PickupSpawnCategory category, System.Random random)
		{
			List<NetworkObject> pool = GetPickupPool(category);
			if (pool.Count == 0)
				return null;

			return pool[random.Next(pool.Count)];
		}

		private List<NetworkObject> GetPickupPool(PickupSpawnPoint.PickupSpawnCategory category)
		{
			NetworkObject[] source = category switch
			{
				PickupSpawnPoint.PickupSpawnCategory.Weapon => Settings.WeaponPickups,
				PickupSpawnPoint.PickupSpawnCategory.Health => Settings.HealthPickups,
				_ => null,
			};

			var pool = new List<NetworkObject>();
			if (source == null)
				return pool;

			for (int i = 0; i < source.Length; i++)
			{
				if (source[i] != null)
					pool.Add(source[i]);
			}

			return pool;
		}

		private void ClearSpawnedPickups(NetworkRunner runner)
		{
			for (int i = _spawnedPickups.Count - 1; i >= 0; i--)
			{
				NetworkObject pickup = _spawnedPickups[i];
				if (pickup != null)
					runner.Despawn(pickup);
			}

			_spawnedPickups.Clear();
		}

		private void AddSpawnedPickup(NetworkObject pickup)
		{
			if (pickup != null && _spawnedPickups.Contains(pickup) == false)
				_spawnedPickups.Add(pickup);
		}

		private void RemoveMissingSpawnedPickups()
		{
			for (int i = _spawnedPickups.Count - 1; i >= 0; i--)
			{
				if (_spawnedPickups[i] == null)
					_spawnedPickups.RemoveAt(i);
			}
		}

		private void Shuffle<T>(List<T> list, System.Random random)
		{
			for (int i = list.Count - 1; i > 0; i--)
			{
				int j = random.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}
		}
	}
}
