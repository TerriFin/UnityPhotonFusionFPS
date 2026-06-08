using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimpleFPS
{
	public static class MatchRuntimeSettings
	{
		private static MatchHostingSettingsCatalog _catalog;
		private static MatchHostingSettings _settings;
		private static bool _isSceneLoadedHookRegistered;

		public static void Configure(
			MatchHostingSettingsCatalog catalog,
			IReadOnlyDictionary<string, SessionProperty> sessionProperties)
		{
			_catalog = catalog;
			MatchHostingSettings.TryFromSessionProperties(sessionProperties, catalog, out _settings);
			RegisterSceneLoadedHook();
		}

		public static void Clear()
		{
			_catalog = null;
			_settings = null;
		}

		public static void ApplyToScene(Scene scene)
		{
			if (_settings == null || scene.IsValid() == false || scene.isLoaded == false)
				return;

			_settings.Validate(_catalog);
			RenderSettings.fogDensity = _settings.FogDensity;

			Gameplay gameplay = FindInScene<Gameplay>(scene);
			if (gameplay != null)
			{
				gameplay.GameDuration = _settings.GameLengthMinutes * 60f;
				gameplay.StartingCharacterCount = _settings.StartingSurvivorsPerPlayer;
				gameplay.RaidMode = _settings.RaidMode;
				gameplay.RaidModeClientStartingCharacterCount = _settings.RaidModeClientStartingSurvivors;
			}

			HeightMapGenerator heightGenerator = FindInScene<HeightMapGenerator>(scene);
			if (heightGenerator != null)
			{
				heightGenerator.Width = _settings.MapWidth;
				heightGenerator.Height = _settings.MapHeight;

				HeightGenerationSettings preset = _catalog != null
					? _catalog.GetHeightGenerationPreset(_settings.HeightGenerationPreset)
					: null;
				if (preset != null)
					heightGenerator.Settings = preset;
			}

			RoadGridGenerator roadGenerator = FindInScene<RoadGridGenerator>(scene);
			if (roadGenerator != null)
			{
				roadGenerator.Width = _settings.MapWidth;
				roadGenerator.Height = _settings.MapHeight;

				RoadGenerationSettings preset = _catalog != null
					? _catalog.GetRoadGenerationPreset(_settings.RoadGenerationPreset)
					: null;
				if (preset != null)
					roadGenerator.Settings = preset;
			}

			BuildingPlacementGenerator buildingGenerator = FindInScene<BuildingPlacementGenerator>(scene);
			if (buildingGenerator != null)
			{
				BuildingPlacementSettings preset = _catalog != null
					? _catalog.GetBuildingPlacementPreset(_settings.BuildingPlacementPreset)
					: null;
				if (preset != null)
					buildingGenerator.Settings = preset;

				buildingGenerator.SetRuntimeLedgeTunnelPruningSettings(
					_settings.PreserveBuriedLedgeTunnels,
					_settings.MaxDeadEndBuriedLedgeLength,
					_settings.MaxBuriedLedgeTunnelLength);
			}

			WorldLootSpawner lootSpawner = FindInScene<WorldLootSpawner>(scene);
			if (lootSpawner != null)
			{
				WorldLootSpawnSettings preset = _catalog != null
					? _catalog.GetLootSpawnPreset(_settings.LootSpawnPreset)
					: null;
				if (preset != null)
					lootSpawner.Settings = preset;
			}

			ZombieOrchestrator zombieOrchestrator = FindInScene<ZombieOrchestrator>(scene);
			if (zombieOrchestrator != null)
			{
				ZombieOrchestratorSettings preset = _catalog != null
					? _catalog.GetZombieOrchestratorPreset(_settings.ZombieOrchestratorPreset)
					: null;
				if (preset != null)
					zombieOrchestrator.Settings = preset;
			}

			NeutralSurvivorOrchestrator neutralSurvivorOrchestrator = FindInScene<NeutralSurvivorOrchestrator>(scene);
			if (neutralSurvivorOrchestrator != null)
			{
				NeutralSurvivorSpawnSettings preset = _catalog != null
					? _catalog.GetNeutralSurvivorPreset(_settings.NeutralSurvivorPreset)
					: null;
				if (preset != null)
					neutralSurvivorOrchestrator.Settings = preset;

				neutralSurvivorOrchestrator.SetRuntimeDesiredNeutralSurvivorCount(_settings.PreferredNeutralSurvivorCount);
			}
		}

		private static void RegisterSceneLoadedHook()
		{
			if (_isSceneLoadedHookRegistered)
				return;

			SceneManager.sceneLoaded += OnSceneLoaded;
			_isSceneLoadedHookRegistered = true;
		}

		private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			ApplyToScene(scene);
		}

		private static T FindInScene<T>(Scene scene) where T : Component
		{
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				T component = root.GetComponentInChildren<T>(true);
				if (component != null)
					return component;
			}

			return null;
		}

	}
}
