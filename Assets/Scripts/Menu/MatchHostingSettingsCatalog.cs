using System;
using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/Menu/Match Hosting Settings Catalog")]
	public sealed class MatchHostingSettingsCatalog : ScriptableObject
	{
		public MatchHostingSettings DefaultHostedSettings = new();

		[Header("Generation Presets")]
		public HeightGenerationSettings[] HeightGenerationPresets;
		public RoadGenerationSettings[] RoadGenerationPresets;
		public BuildingPlacementSettings[] BuildingPlacementPresets;
		public WorldLootSpawnSettings[] LootSpawnPresets;
		public ZombieOrchestratorSettings[] ZombieOrchestratorPresets;
		public NeutralSurvivorSpawnSettings[] NeutralSurvivorPresets;

		public HeightGenerationSettings GetHeightGenerationPreset(int index) => GetPreset(HeightGenerationPresets, index);
		public RoadGenerationSettings GetRoadGenerationPreset(int index) => GetPreset(RoadGenerationPresets, index);
		public BuildingPlacementSettings GetBuildingPlacementPreset(int index) => GetPreset(BuildingPlacementPresets, index);
		public WorldLootSpawnSettings GetLootSpawnPreset(int index) => GetPreset(LootSpawnPresets, index);
		public ZombieOrchestratorSettings GetZombieOrchestratorPreset(int index) => GetPreset(ZombieOrchestratorPresets, index);
		public NeutralSurvivorSpawnSettings GetNeutralSurvivorPreset(int index) => GetPreset(NeutralSurvivorPresets, index);

		public string[] GetHeightGenerationPresetNames() => GetPresetNames(HeightGenerationPresets);
		public string[] GetRoadGenerationPresetNames() => GetPresetNames(RoadGenerationPresets);
		public string[] GetBuildingPlacementPresetNames() => GetPresetNames(BuildingPlacementPresets);
		public string[] GetLootSpawnPresetNames() => GetPresetNames(LootSpawnPresets);
		public string[] GetZombieOrchestratorPresetNames() => GetPresetNames(ZombieOrchestratorPresets);
		public string[] GetNeutralSurvivorPresetNames() => GetPresetNames(NeutralSurvivorPresets);

		public int ClampHeightGenerationPresetIndex(int index) => ClampPresetIndex(HeightGenerationPresets, index);
		public int ClampRoadGenerationPresetIndex(int index) => ClampPresetIndex(RoadGenerationPresets, index);
		public int ClampBuildingPlacementPresetIndex(int index) => ClampPresetIndex(BuildingPlacementPresets, index);
		public int ClampLootSpawnPresetIndex(int index) => ClampPresetIndex(LootSpawnPresets, index);
		public int ClampZombieOrchestratorPresetIndex(int index) => ClampPresetIndex(ZombieOrchestratorPresets, index);
		public int ClampNeutralSurvivorPresetIndex(int index) => ClampPresetIndex(NeutralSurvivorPresets, index);

		private static T GetPreset<T>(T[] presets, int index) where T : UnityEngine.Object
		{
			return index >= 0 && presets != null && index < presets.Length ? presets[index] : null;
		}

		private static string[] GetPresetNames<T>(T[] presets) where T : UnityEngine.Object
		{
			if (presets == null || presets.Length == 0)
				return Array.Empty<string>();

			string[] names = new string[presets.Length];
			for (int i = 0; i < presets.Length; i++)
			{
				names[i] = presets[i] != null ? presets[i].name : $"Missing Preset {i + 1}";
			}

			return names;
		}

		private static int ClampPresetIndex<T>(T[] presets, int index) where T : UnityEngine.Object
		{
			if (index < 0 || presets == null || presets.Length == 0)
				return -1;

			return Mathf.Clamp(index, 0, presets.Length - 1);
		}
	}
}
