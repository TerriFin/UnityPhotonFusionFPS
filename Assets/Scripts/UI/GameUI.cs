using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace SimpleFPS
{
	/// <summary>
	/// Main UI script that stores references to other elements (views).
	/// </summary>
	public class GameUI : MonoBehaviour
	{
		public Gameplay       Gameplay;
		[HideInInspector]
		public NetworkRunner  Runner;

		public UIPlayerView   PlayerView;
		public UIGameplayView GameplayView;
		public UIGameOverView GameOverView;
		public GameObject     ScoreboardView;
		public GameObject     MenuView;
		public UISettingsView SettingsView;
		public GameObject     DisconnectedView;
		public GameMapView    MapView;
		public SpectatorController Spectator;

		// Called from NetworkEvents on NetworkRunner object
		public void OnRunnerShutdown(NetworkRunner runner, ShutdownReason reason)
		{
			if (GameOverView.gameObject.activeSelf)
				return; // Regular shutdown - GameOver already active

			ScoreboardView.SetActive(false);
			SettingsView.gameObject.SetActive(false);
			MenuView.gameObject.SetActive(false);

			DisconnectedView.SetActive(true);
		}

		public void GoToMenu()
		{
			if (Runner != null)
			{
				Runner.Shutdown();
			}

			SceneManager.LoadScene("Startup");
		}

		private void Awake()
		{
			if (MapView == null)
				MapView = GetComponentInChildren<GameMapView>(true);
			if (MapView != null)
				MapView.Initialize();

			if (Spectator == null)
				Spectator = GetComponent<SpectatorController>() ?? gameObject.AddComponent<SpectatorController>();
			if (MapView != null)
				MapView.Spectator = Spectator;

			if (GameplayView != null && GameplayView.MinimapView == null)
				GameplayView.MinimapView = GameplayView.GetComponentInChildren<GameMinimapView>(true);
			if (GameplayView != null && GameplayView.MinimapView != null && GameplayView.MinimapView.MainMapView == null)
				GameplayView.MinimapView.MainMapView = MapView;

			PlayerView.gameObject.SetActive(false);
			MenuView.SetActive(false);
			SettingsView.gameObject.SetActive(false);
			DisconnectedView.SetActive(false);

			SettingsView.LoadSettings();

			// Make sure the cursor starts unlocked
			Cursor.lockState = CursorLockMode.None;
			Cursor.visible = true;
		}

		private void Update()
		{
			if (Application.isBatchMode == true)
				return;

			if (Gameplay.Object == null || Gameplay.Object.IsValid == false)
				return;

			Runner = Gameplay.Runner;

			var keyboard = Keyboard.current;
			bool gameplayActive = Gameplay.State < EGameplayState.Finished;
			// Tick the spectator controller before the map so the inspect target / camera are current this frame.
			if (Spectator != null)
				Spectator.Tick(Gameplay, Runner, MapView, gameplayActive);

			// Draw the inspected survivor enlarged on the map (raid host or defeated spectator). Works for own or
			// other-team icons. Normal players leave this null and keep their active-character highlight.
			Survivor inspectHighlight = Spectator != null && Spectator.IsActive ? Spectator.InspectTarget : null;
			if (MapView != null && MapView.IconController != null)
				MapView.IconController.InspectHighlightSurvivor = inspectHighlight;
			if (GameplayView != null && GameplayView.MinimapView != null)
			{
				if (GameplayView.MinimapView.IconController != null)
					GameplayView.MinimapView.IconController.InspectHighlightSurvivor = inspectHighlight;
				// Defeated spectators have no minimap. The raid host's minimap follows the inspected survivor (the
				// host has no possessed PlayerObject for it to follow).
				GameplayView.MinimapView.Suppressed = Spectator != null && Spectator.Mode == ESpectatorMode.DefeatedSpectator;
				if (Spectator != null && Spectator.IsRaidCommander)
					GameplayView.MinimapView.OverrideFollowTarget = inspectHighlight != null ? inspectHighlight.transform : null;
			}

			if (MapView != null)
				MapView.Tick(gameplayActive, Runner, Gameplay);
			if (GameplayView != null && GameplayView.MinimapView != null)
				GameplayView.MinimapView.Tick(gameplayActive, Runner, Gameplay);

			ScoreboardView.SetActive(gameplayActive && keyboard != null && keyboard.tabKey.isPressed);

			if (gameplayActive && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
			{
				MenuView.SetActive(!MenuView.activeSelf);
			}

			GameplayView.gameObject.SetActive(gameplayActive);
			GameOverView.gameObject.SetActive(gameplayActive == false);

			var playerObject = Runner.GetPlayerObject(Runner.LocalPlayer);
			if (playerObject != null)
			{
				var player = playerObject.GetComponent<Survivor>();
				var playerData = Gameplay.PlayerData.Get(Runner.LocalPlayer);

				PlayerView.UpdatePlayer(player, playerData);
				PlayerView.gameObject.SetActive(gameplayActive);
			}
			else
			{
				PlayerView.gameObject.SetActive(false);
			}
		}
	}
}
