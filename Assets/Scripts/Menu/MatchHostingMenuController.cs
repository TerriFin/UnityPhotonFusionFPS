using System.Collections.Generic;
using Fusion.Menu;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	public sealed class MatchHostingMenuController : FusionMenuUIScreen
	{
		private static readonly int[] GameLengthMinutes = { 1, 5, 10, 15, 20 };

		[Header("Match Settings")]
		public TMP_InputField MapWidth;
		public TMP_InputField MapHeight;
		public TMP_InputField StartingSurvivorsPerPlayer;
		public TMP_Dropdown GameLength;
		public Slider FogDensity;
		public TMP_Text FogDensityValue;
		public Toggle RaidMode;
		public TMP_InputField RaidModeClientStartingSurvivors;
		public Toggle PreserveBuriedLedgeTunnels;
		public TMP_InputField MaxDeadEndBuriedLedgeLength;
		public TMP_InputField MaxBuriedLedgeTunnelLength;
		public TMP_InputField PreferredNeutralSurvivorCount;

		[Header("Preset Dropdowns")]
		public TMP_Dropdown HeightGenerationPreset;
		public TMP_Dropdown RoadGenerationPreset;
		public TMP_Dropdown BuildingPlacementPreset;
		public TMP_Dropdown LootSpawnPreset;
		public TMP_Dropdown ZombieOrchestratorPreset;
		public TMP_Dropdown NeutralSurvivorPreset;

		[Header("Actions")]
		public Button StartGameButton;

		private bool _isStarting;
		private bool _isInitialized;

		public override void Init()
		{
			base.Init();

			if (_isInitialized)
				return;

			PopulateDropdowns();
			ApplyDefaults();

			if (RaidMode != null)
				RaidMode.onValueChanged.AddListener(OnRaidModeChanged);
			if (FogDensity != null)
				FogDensity.onValueChanged.AddListener(OnFogDensityChanged);

			_isInitialized = true;
		}

		private void OnDestroy()
		{
			if (_isInitialized == false)
				return;

			if (RaidMode != null)
				RaidMode.onValueChanged.RemoveListener(OnRaidModeChanged);
			if (FogDensity != null)
				FogDensity.onValueChanged.RemoveListener(OnFogDensityChanged);
		}

		public void OnBackButtonPressed()
		{
			Controller.Show<FusionMenuUIMain>();
		}

		public void OnStartGameButtonPressed()
		{
			StartHostedGame();
		}

		public async void StartHostedGame()
		{
			if (_isStarting)
				return;

			if (Connection is not MenuConnectionBehaviour connectionBehaviour || connectionBehaviour.UIController == null)
			{
				Debug.LogError($"{nameof(MatchHostingMenuController)} on {name} has no {nameof(MenuConnectionBehaviour)}.", this);
				return;
			}

			if (Controller == null || ConnectionArgs == null)
			{
				Debug.LogError($"{nameof(MatchHostingMenuController)} on {name} is not registered in the menu screen list.", this);
				return;
			}

			_isStarting = true;
			if (StartGameButton != null)
				StartGameButton.interactable = false;

			try
			{
				MatchHostingSettings settings = ReadSettings();
				Controller.Show<FusionMenuUILoading>();
				ConnectResult result = await connectionBehaviour.StartConfiguredHostAsync(settings, ConnectionArgs);
				await FusionMenuUIMain.HandleConnectionResult(result, Controller);
			}
			finally
			{
				_isStarting = false;
				if (StartGameButton != null)
					StartGameButton.interactable = true;
			}
		}

		public void ApplyDefaults()
		{
			MatchHostingSettings settings = GetCatalog()?.DefaultHostedSettings?.Clone() ?? new MatchHostingSettings();
			settings.Validate(GetCatalog());

			SetText(MapWidth, settings.MapWidth);
			SetText(MapHeight, settings.MapHeight);
			SetText(StartingSurvivorsPerPlayer, settings.StartingSurvivorsPerPlayer);
			SetGameLengthDropdown(settings.GameLengthMinutes);

			if (FogDensity != null)
				FogDensity.SetValueWithoutNotify(settings.FogDensity);
			UpdateFogDensityLabel(settings.FogDensity);

			if (RaidMode != null)
				RaidMode.SetIsOnWithoutNotify(settings.RaidMode);
			SetText(RaidModeClientStartingSurvivors, settings.RaidModeClientStartingSurvivors);
			if (PreserveBuriedLedgeTunnels != null)
				PreserveBuriedLedgeTunnels.SetIsOnWithoutNotify(settings.PreserveBuriedLedgeTunnels);
			SetText(MaxDeadEndBuriedLedgeLength, settings.MaxDeadEndBuriedLedgeLength);
			SetText(MaxBuriedLedgeTunnelLength, settings.MaxBuriedLedgeTunnelLength);
			SetText(PreferredNeutralSurvivorCount, settings.PreferredNeutralSurvivorCount);

			SetPresetDropdown(HeightGenerationPreset, settings.HeightGenerationPreset);
			SetPresetDropdown(RoadGenerationPreset, settings.RoadGenerationPreset);
			SetPresetDropdown(BuildingPlacementPreset, settings.BuildingPlacementPreset);
			SetPresetDropdown(LootSpawnPreset, settings.LootSpawnPreset);
			SetPresetDropdown(ZombieOrchestratorPreset, settings.ZombieOrchestratorPreset);
			SetPresetDropdown(NeutralSurvivorPreset, settings.NeutralSurvivorPreset);
			RefreshRaidModeFields(settings.RaidMode);
		}

		private MatchHostingSettings ReadSettings()
		{
			MatchHostingSettings defaults = GetCatalog()?.DefaultHostedSettings?.Clone() ?? new MatchHostingSettings();
			var settings = new MatchHostingSettings
			{
				MapWidth = ReadInt(MapWidth, defaults.MapWidth),
				MapHeight = ReadInt(MapHeight, defaults.MapHeight),
				StartingSurvivorsPerPlayer = ReadInt(StartingSurvivorsPerPlayer, defaults.StartingSurvivorsPerPlayer),
				GameLengthMinutes = ReadGameLength(),
				FogDensity = FogDensity != null ? FogDensity.value : defaults.FogDensity,
				RaidMode = RaidMode != null ? RaidMode.isOn : defaults.RaidMode,
				RaidModeClientStartingSurvivors = ReadInt(RaidModeClientStartingSurvivors, defaults.RaidModeClientStartingSurvivors),
				PreserveBuriedLedgeTunnels = PreserveBuriedLedgeTunnels != null ? PreserveBuriedLedgeTunnels.isOn : defaults.PreserveBuriedLedgeTunnels,
				MaxDeadEndBuriedLedgeLength = ReadInt(MaxDeadEndBuriedLedgeLength, defaults.MaxDeadEndBuriedLedgeLength),
				MaxBuriedLedgeTunnelLength = ReadInt(MaxBuriedLedgeTunnelLength, defaults.MaxBuriedLedgeTunnelLength),
				PreferredNeutralSurvivorCount = ReadInt(PreferredNeutralSurvivorCount, defaults.PreferredNeutralSurvivorCount),
				HeightGenerationPreset = ReadPresetDropdown(HeightGenerationPreset),
				RoadGenerationPreset = ReadPresetDropdown(RoadGenerationPreset),
				BuildingPlacementPreset = ReadPresetDropdown(BuildingPlacementPreset),
				LootSpawnPreset = ReadPresetDropdown(LootSpawnPreset),
				ZombieOrchestratorPreset = ReadPresetDropdown(ZombieOrchestratorPreset),
				NeutralSurvivorPreset = ReadPresetDropdown(NeutralSurvivorPreset),
			};

			settings.Validate(GetCatalog());
			return settings;
		}

		private void PopulateDropdowns()
		{
			PopulateDropdown(GameLength, new[] { "1 minute", "5 minutes", "10 minutes", "15 minutes", "20 minutes" }, false);

			MatchHostingSettingsCatalog catalog = GetCatalog();
			PopulateDropdown(HeightGenerationPreset, catalog?.GetHeightGenerationPresetNames(), true);
			PopulateDropdown(RoadGenerationPreset, catalog?.GetRoadGenerationPresetNames(), true);
			PopulateDropdown(BuildingPlacementPreset, catalog?.GetBuildingPlacementPresetNames(), true);
			PopulateDropdown(LootSpawnPreset, catalog?.GetLootSpawnPresetNames(), true);
			PopulateDropdown(ZombieOrchestratorPreset, catalog?.GetZombieOrchestratorPresetNames(), true);
			PopulateDropdown(NeutralSurvivorPreset, catalog?.GetNeutralSurvivorPresetNames(), true);
		}

		private static void PopulateDropdown(TMP_Dropdown dropdown, string[] values, bool includeSceneDefault)
		{
			if (dropdown == null)
				return;

			var options = new List<string>();
			if (includeSceneDefault)
				options.Add("Scene Default");
			if (values != null)
				options.AddRange(values);

			dropdown.ClearOptions();
			dropdown.AddOptions(options);
		}

		private MatchHostingSettingsCatalog GetCatalog()
		{
			return Connection is MenuConnectionBehaviour connectionBehaviour
				? connectionBehaviour.MatchSettingsCatalog
				: null;
		}

		private void OnRaidModeChanged(bool enabled)
		{
			RefreshRaidModeFields(enabled);
		}

		private void OnFogDensityChanged(float value)
		{
			UpdateFogDensityLabel(value);
		}

		private void RefreshRaidModeFields(bool enabled)
		{
			if (RaidModeClientStartingSurvivors != null)
				RaidModeClientStartingSurvivors.interactable = enabled;
		}

		private void UpdateFogDensityLabel(float value)
		{
			if (FogDensityValue != null)
				FogDensityValue.text = value.ToString("0.000");
		}

		private void SetGameLengthDropdown(int minutes)
		{
			if (GameLength == null)
				return;

			int closest = MatchHostingSettings.GetClosestGameLength(minutes);
			for (int i = 0; i < GameLengthMinutes.Length; i++)
			{
				if (GameLengthMinutes[i] == closest)
				{
					GameLength.SetValueWithoutNotify(i);
					return;
				}
			}
		}

		private int ReadGameLength()
		{
			if (GameLength == null || GameLength.value < 0 || GameLength.value >= GameLengthMinutes.Length)
				return GameLengthMinutes[1];

			return GameLengthMinutes[GameLength.value];
		}

		private static int ReadInt(TMP_InputField input, int fallback)
		{
			return input != null && int.TryParse(input.text, out int value) ? value : fallback;
		}

		private static void SetText(TMP_InputField input, int value)
		{
			if (input != null)
				input.SetTextWithoutNotify(value.ToString());
		}

		private static int ReadPresetDropdown(TMP_Dropdown dropdown)
		{
			return dropdown != null ? dropdown.value - 1 : -1;
		}

		private static void SetPresetDropdown(TMP_Dropdown dropdown, int presetIndex)
		{
			if (dropdown != null)
				dropdown.SetValueWithoutNotify(Mathf.Max(0, presetIndex + 1));
		}
	}
}
