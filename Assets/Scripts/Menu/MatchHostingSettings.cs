using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	[Serializable]
	public sealed class MatchHostingSettings
	{
		private const int CurrentVersion = 5;
		private const float FogDensityPrecision = 100000f;

		private const string PackedProfileKey = "msp";
		private const string VersionKey = "msv";
		private const string MapWidthKey = "mw";
		private const string MapHeightKey = "mh";
		private const string StartingSurvivorsKey = "ss";
		private const string GameLengthMinutesKey = "gm";
		private const string FogDensityKey = "fg";
		private const string RaidModeKey = "rm";
		private const string RaidClientSurvivorsKey = "rs";
		private const string HeightPresetKey = "hp";
		private const string RoadPresetKey = "rp";
		private const string BuildingPresetKey = "bp";
		private const string LootPresetKey = "lp";
		private const string ZombiePresetKey = "zp";
		private const string NeutralSurvivorPresetKey = "np";
		private const string PreferredNeutralSurvivorCountKey = "nc";

		private static readonly int[] AllowedGameLengths = { 5, 10, 15, 20 };

		[Min(3)]
		public int MapWidth = 12;
		[Min(3)]
		public int MapHeight = 12;
		[Range(1, CharacterMask128.Capacity)]
		public int StartingSurvivorsPerPlayer = 5;
		public int GameLengthMinutes = 10;
		[Min(0f)]
		public float FogDensity = 0.06f;
		public bool RaidMode;
		[Range(1, CharacterMask128.Capacity)]
		public int RaidModeClientStartingSurvivors = 1;

		[Header("Preset Indices")]
		public int HeightGenerationPreset = -1;
		public int RoadGenerationPreset = -1;
		public int BuildingPlacementPreset = -1;
		public int LootSpawnPreset = -1;
		public int ZombieOrchestratorPreset = -1;
		public int NeutralSurvivorPreset = -1;
		public bool PreserveBuriedLedgeTunnels;
		[Min(0)]
		public int MaxDeadEndBuriedLedgeLength;
		[Min(0)]
		public int MaxBuriedLedgeTunnelLength;
		[Min(0)]
		public int PreferredNeutralSurvivorCount = 20;

		public MatchHostingSettings Clone()
		{
			return (MatchHostingSettings)MemberwiseClone();
		}

		public void Validate(MatchHostingSettingsCatalog catalog)
		{
			MapWidth = Mathf.Max(3, MapWidth);
			MapHeight = Mathf.Max(3, MapHeight);
			StartingSurvivorsPerPlayer = Mathf.Clamp(StartingSurvivorsPerPlayer, 1, CharacterMask128.Capacity);
			GameLengthMinutes = GetClosestGameLength(GameLengthMinutes);
			FogDensity = Mathf.Max(0f, FogDensity);
			RaidModeClientStartingSurvivors = Mathf.Clamp(RaidModeClientStartingSurvivors, 1, CharacterMask128.Capacity);
			MaxDeadEndBuriedLedgeLength = Mathf.Max(0, MaxDeadEndBuriedLedgeLength);
			MaxBuriedLedgeTunnelLength = Mathf.Max(0, MaxBuriedLedgeTunnelLength);
			PreferredNeutralSurvivorCount = Mathf.Max(0, PreferredNeutralSurvivorCount);

			if (catalog == null)
				return;

			HeightGenerationPreset = catalog.ClampHeightGenerationPresetIndex(HeightGenerationPreset);
			RoadGenerationPreset = catalog.ClampRoadGenerationPresetIndex(RoadGenerationPreset);
			BuildingPlacementPreset = catalog.ClampBuildingPlacementPresetIndex(BuildingPlacementPreset);
			LootSpawnPreset = catalog.ClampLootSpawnPresetIndex(LootSpawnPreset);
			ZombieOrchestratorPreset = catalog.ClampZombieOrchestratorPresetIndex(ZombieOrchestratorPreset);
			NeutralSurvivorPreset = catalog.ClampNeutralSurvivorPresetIndex(NeutralSurvivorPreset);
		}

		public Dictionary<string, SessionProperty> ToSessionProperties()
		{
			return new Dictionary<string, SessionProperty>
			{
				{ PackedProfileKey, ToPackedProfile() },
			};
		}

		public static bool TryFromSessionProperties(
			IReadOnlyDictionary<string, SessionProperty> properties,
			MatchHostingSettingsCatalog catalog,
			out MatchHostingSettings settings)
		{
			settings = null;
			if (TryGetString(properties, PackedProfileKey, out string packedProfile))
				return TryFromPackedProfile(packedProfile, catalog, out settings);

			if (TryGetInt(properties, VersionKey, out int version) == false || version < 1 || version > CurrentVersion)
				return false;

			var parsedSettings = new MatchHostingSettings();
			TryAssignInt(properties, MapWidthKey, value => parsedSettings.MapWidth = value);
			TryAssignInt(properties, MapHeightKey, value => parsedSettings.MapHeight = value);
			TryAssignInt(properties, StartingSurvivorsKey, value => parsedSettings.StartingSurvivorsPerPlayer = value);
			TryAssignInt(properties, GameLengthMinutesKey, value => parsedSettings.GameLengthMinutes = value);
			TryAssignInt(properties, FogDensityKey, value => parsedSettings.FogDensity = value / FogDensityPrecision);
			TryAssignBool(properties, RaidModeKey, value => parsedSettings.RaidMode = value);
			TryAssignInt(properties, RaidClientSurvivorsKey, value => parsedSettings.RaidModeClientStartingSurvivors = value);
			TryAssignInt(properties, HeightPresetKey, value => parsedSettings.HeightGenerationPreset = value);
			TryAssignInt(properties, RoadPresetKey, value => parsedSettings.RoadGenerationPreset = value);
			TryAssignInt(properties, BuildingPresetKey, value => parsedSettings.BuildingPlacementPreset = value);
			TryAssignInt(properties, LootPresetKey, value => parsedSettings.LootSpawnPreset = value);
			TryAssignInt(properties, ZombiePresetKey, value => parsedSettings.ZombieOrchestratorPreset = value);
			TryAssignInt(properties, NeutralSurvivorPresetKey, value => parsedSettings.NeutralSurvivorPreset = value);
			TryAssignInt(properties, PreferredNeutralSurvivorCountKey, value => parsedSettings.PreferredNeutralSurvivorCount = value);
			parsedSettings.Validate(catalog);
			settings = parsedSettings;
			return true;
		}

		private string ToPackedProfile()
		{
			int fogDensity = Mathf.RoundToInt(FogDensity * FogDensityPrecision);
			int raidMode = RaidMode ? 1 : 0;

			return string.Join("|",
				CurrentVersion,
				MapWidth,
				MapHeight,
				StartingSurvivorsPerPlayer,
				GameLengthMinutes,
				fogDensity,
				raidMode,
				RaidModeClientStartingSurvivors,
				HeightGenerationPreset,
				RoadGenerationPreset,
				BuildingPlacementPreset,
				LootSpawnPreset,
				ZombieOrchestratorPreset,
				PreserveBuriedLedgeTunnels ? 1 : 0,
				MaxDeadEndBuriedLedgeLength,
				MaxBuriedLedgeTunnelLength,
				NeutralSurvivorPreset,
				PreferredNeutralSurvivorCount);
		}

		private static bool TryFromPackedProfile(string packedProfile, MatchHostingSettingsCatalog catalog, out MatchHostingSettings settings)
		{
			settings = null;
			if (string.IsNullOrWhiteSpace(packedProfile))
				return false;

			string[] values = packedProfile.Split('|');
			if (values.Length < 13)
				return false;

			if (TryParse(values[0], out int version) == false || version < 1 || version > CurrentVersion)
				return false;

			var parsedSettings = new MatchHostingSettings();
			if (TryParse(values[1], out int mapWidth))
				parsedSettings.MapWidth = mapWidth;
			if (TryParse(values[2], out int mapHeight))
				parsedSettings.MapHeight = mapHeight;
			if (TryParse(values[3], out int startingSurvivors))
				parsedSettings.StartingSurvivorsPerPlayer = startingSurvivors;
			if (TryParse(values[4], out int gameLengthMinutes))
				parsedSettings.GameLengthMinutes = gameLengthMinutes;
			if (TryParse(values[5], out int fogDensity))
				parsedSettings.FogDensity = fogDensity / FogDensityPrecision;
			if (TryParse(values[6], out int raidMode))
				parsedSettings.RaidMode = raidMode != 0;
			if (TryParse(values[7], out int raidClientSurvivors))
				parsedSettings.RaidModeClientStartingSurvivors = raidClientSurvivors;
			if (TryParse(values[8], out int heightPreset))
				parsedSettings.HeightGenerationPreset = heightPreset;
			if (TryParse(values[9], out int roadPreset))
				parsedSettings.RoadGenerationPreset = roadPreset;
			if (TryParse(values[10], out int buildingPreset))
				parsedSettings.BuildingPlacementPreset = buildingPreset;
			if (TryParse(values[11], out int lootPreset))
				parsedSettings.LootSpawnPreset = lootPreset;
			if (TryParse(values[12], out int zombiePreset))
				parsedSettings.ZombieOrchestratorPreset = zombiePreset;
			if (version >= 2 && values.Length >= 15)
			{
				if (TryParse(values[13], out int preserveBuriedLedgeTunnels))
					parsedSettings.PreserveBuriedLedgeTunnels = preserveBuriedLedgeTunnels != 0;
				if (TryParse(values[14], out int maxDeadEndBuriedLedgeLength))
					parsedSettings.MaxDeadEndBuriedLedgeLength = maxDeadEndBuriedLedgeLength;
			}
			if (version >= 3 && values.Length >= 16)
			{
				if (TryParse(values[15], out int maxBuriedLedgeTunnelLength))
					parsedSettings.MaxBuriedLedgeTunnelLength = maxBuriedLedgeTunnelLength;
			}
			if (version >= 4 && values.Length >= 17)
			{
				if (TryParse(values[16], out int neutralSurvivorPreset))
					parsedSettings.NeutralSurvivorPreset = neutralSurvivorPreset;
			}
			if (version >= 5 && values.Length >= 18)
			{
				if (TryParse(values[17], out int preferredNeutralSurvivorCount))
					parsedSettings.PreferredNeutralSurvivorCount = preferredNeutralSurvivorCount;
			}

			parsedSettings.Validate(catalog);
			settings = parsedSettings;
			return true;
		}

		public static int GetClosestGameLength(int minutes)
		{
			int closest = AllowedGameLengths[0];
			int closestDifference = Mathf.Abs(minutes - closest);
			for (int i = 1; i < AllowedGameLengths.Length; i++)
			{
				int difference = Mathf.Abs(minutes - AllowedGameLengths[i]);
				if (difference < closestDifference)
				{
					closest = AllowedGameLengths[i];
					closestDifference = difference;
				}
			}

			return closest;
		}

		private static void TryAssignInt(IReadOnlyDictionary<string, SessionProperty> properties, string key, Action<int> assign)
		{
			if (TryGetInt(properties, key, out int value))
				assign(value);
		}

		private static void TryAssignBool(IReadOnlyDictionary<string, SessionProperty> properties, string key, Action<bool> assign)
		{
			if (properties != null && properties.TryGetValue(key, out SessionProperty property) && property.Isbool)
				assign(property);
		}

		private static bool TryGetInt(IReadOnlyDictionary<string, SessionProperty> properties, string key, out int value)
		{
			if (properties != null && properties.TryGetValue(key, out SessionProperty property) && property.IsInt)
			{
				value = property;
				return true;
			}

			value = default;
			return false;
		}

		private static bool TryGetString(IReadOnlyDictionary<string, SessionProperty> properties, string key, out string value)
		{
			if (properties != null && properties.TryGetValue(key, out SessionProperty property) && property.IsString)
			{
				value = property;
				return true;
			}

			value = default;
			return false;
		}

		private static bool TryParse(string value, out int result)
		{
			return int.TryParse(value, out result);
		}
	}
}
