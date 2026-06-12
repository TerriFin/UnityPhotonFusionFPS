using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace SimpleFPS
{
	public enum EMapRevealMode
	{
		Auto,
		ForceOff,
		ForceOn,
	}

	public sealed class GameMapView : MonoBehaviour, IGameMapView
	{
		[Header("Setup")]
		public GameObject MapRoot;
		public RawImage MapImage;
		public GameMapCameraController CameraController;
		public GameMapIconController IconController;
		public GameMapSelectionController SelectionController;
		public SurvivorRosterController RosterController;

		[Header("Render Texture")]
		public RenderTexture MapRenderTexture;
		public bool CreateRenderTextureIfMissing = true;
		public Vector2Int RuntimeTextureSize = new Vector2Int(1024, 1024);
		public bool LogSetupStatus;

		[Header("Input")]
		public Key MapKey = Key.LeftAlt;

		[Header("Reveal")]
		[Tooltip("Auto: the full map reveals everything only for a defeated spectator (otherwise fog of war). ForceOn/ForceOff: debug override that always reveals / never reveals, ignoring the spectator state.")]
		public EMapRevealMode RevealMode = EMapRevealMode.Auto;

		// Set by GameUI. Lets the selection controller route map-Space to "inspect" instead of "possess" and
		// decides reveal/selection rules for raid and spectate modes. Null until GameUI wires it.
		[HideInInspector]
		public SpectatorController Spectator;

		private CursorLockMode _previousCursorLockState;
		private bool _previousCursorVisible;
		private bool _isMapOpen;
		private bool _initialized;
		private bool _warningLogged;
		private NetworkRunner _runner;
		private Gameplay _gameplay;
		private Canvas _canvas;

		public static bool IsAnyMapOpen { get; private set; }
		public bool IsMapOpen => _isMapOpen;

		// Generic "hold the map open" capability: while true the map cannot be closed by the player (Alt toggle or
		// possess close are ignored), and the match-end path still force-closes it so the game-over screen appears.
		// Currently unused by spectate/raid (the host's map auto-opens once but stays closeable), kept as a reusable
		// hook for any future "locked map" mode.
		public bool LockedOpen { get; set; }

		public void Initialize()
		{
			EnsureInitialized();
			_isMapOpen = false;
			IsAnyMapOpen = false;

			SetMapVisible(false);
		}

		private void Awake()
		{
			EnsureInitialized();

			if (_isMapOpen == false)
				SetMapVisible(false);
		}

		public void Tick(bool gameplayActive, NetworkRunner runner, Gameplay gameplay)
		{
			EnsureInitialized();
			_runner = runner;
			_gameplay = gameplay;

			if (Application.isBatchMode)
				return;

			if (gameplayActive == false)
			{
				// Force-close at match end regardless of the lock so the game-over screen can show.
				LockedOpen = false;
				if (_isMapOpen)
					CloseMapInternal();

				return;
			}

			if (LockedOpen)
			{
				// Held open (raid host): keep it open and skip toggle input so it cannot be closed.
				if (_isMapOpen == false)
					OpenMap();

				TickOpenMap(gameplay);
				return;
			}

			HandleToggleInput();

			if (_isMapOpen)
				TickOpenMap(gameplay);
		}

		public void OpenMap()
		{
			EnsureInitialized();

			if (_isMapOpen)
				return;

			_previousCursorLockState = Cursor.lockState;
			_previousCursorVisible = Cursor.visible;

			_isMapOpen = true;
			IsAnyMapOpen = true;

			SetMapVisible(true);
			Cursor.lockState = CursorLockMode.None;
			Cursor.visible = true;

			if (CameraController != null)
			{
				ApplyRenderTexture();
				CameraController.SetMapActive(true);
				CameraController.OpenAt(GetInitialCenterPosition());
			}
		}

		public void CloseMap()
		{
			// Locked open (raid host): ignore player-driven close requests (Alt toggle, map-possess close).
			// The match-end path uses CloseMapInternal to bypass this.
			if (LockedOpen)
				return;

			CloseMapInternal();
		}

		private void CloseMapInternal()
		{
			EnsureInitialized();

			if (_isMapOpen == false)
				return;

			_isMapOpen = false;
			IsAnyMapOpen = false;

			SetMapVisible(false);

			if (CameraController != null)
				CameraController.SetMapActive(false);

			Cursor.lockState = _previousCursorLockState;
			Cursor.visible = _previousCursorVisible;
		}

		public RawImage GetMapImage() => MapImage;

		public Vector2 WorldToMapUI(Vector3 worldPosition)
		{
			if (MapImage == null || CameraController == null)
				return default;

			Vector2 viewport = CameraController.WorldToMapViewport(worldPosition);
			Rect rect = MapImage.rectTransform.rect;
			return new Vector2(
				(viewport.x - 0.5f) * rect.width,
				(viewport.y - 0.5f) * rect.height);
		}

		public bool IsWorldPositionVisibleOnMap(Vector3 worldPosition)
		{
			if (CameraController == null)
				return false;

			Vector2 viewport = CameraController.WorldToMapViewport(worldPosition);
			return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
		}

		public bool TryMapUIToWorld(Vector2 screenPosition, out Vector3 worldPosition)
		{
			worldPosition = default;

			if (MapImage == null || CameraController == null)
				return false;

			if (RectTransformUtility.ScreenPointToLocalPointInRectangle(MapImage.rectTransform, screenPosition, GetEventCamera(), out Vector2 localPoint) == false)
				return false;

			Rect rect = MapImage.rectTransform.rect;
			Vector2 viewport = new Vector2(
				Mathf.InverseLerp(rect.xMin, rect.xMax, localPoint.x),
				Mathf.InverseLerp(rect.yMin, rect.yMax, localPoint.y));

			return CameraController.TryMapViewportToWorld(viewport, out worldPosition);
		}

		private void HandleToggleInput()
		{
			var keyboard = Keyboard.current;
			if (keyboard == null)
				return;

			KeyControl key = keyboard[MapKey];
			if (key == null || key.wasPressedThisFrame == false)
				return;

			if (_isMapOpen)
				CloseMap();
			else
				OpenMap();
		}

		public Camera GetEventCamera()
		{
			if (_canvas == null)
				_canvas = GetComponentInParent<Canvas>();

			if (_canvas == null || _canvas.renderMode == RenderMode.ScreenSpaceOverlay)
				return null;

			return _canvas.worldCamera;
		}

		private void TickOpenMap(Gameplay gameplay)
		{
			// Re-assert the free cursor every frame while the map is open. Unity drops the cursor's
			// visible/lock state when the window loses then regains focus (alt-tab), and the map
			// otherwise only sets the cursor once in OpenMap. Without this a raid host — whose map
			// never closes — permanently loses the mouse, and even opening the pause menu then
			// captures the bad state. Mirrors UIGameOverView, which enforces the cursor the same way.
			Cursor.lockState = CursorLockMode.None;
			Cursor.visible = true;

			if (CameraController == null)
				return;

			var keyboard = Keyboard.current;
			var mouse = Mouse.current;
			Vector2 panInput = Vector2.zero;

			if (keyboard != null)
			{
				if (keyboard.wKey.isPressed) { panInput += Vector2.up; }
				if (keyboard.sKey.isPressed) { panInput += Vector2.down; }
				if (keyboard.aKey.isPressed) { panInput += Vector2.left; }
				if (keyboard.dKey.isPressed) { panInput += Vector2.right; }
			}

			float zoomInput = 0f;
			if (mouse != null && CanZoomMapAtPointer(mouse.position.ReadValue()))
				zoomInput = mouse.scroll.ReadValue().y * 0.01f;

			CameraController.Tick(Time.unscaledDeltaTime, panInput.normalized, zoomInput);

			if (IconController != null)
				IconController.Tick(this, gameplay, _runner, ShouldRevealEverything());
			if (SelectionController != null)
				SelectionController.Tick(this, gameplay, _runner);
			if (RosterController != null)
				RosterController.Tick(this, gameplay, _runner);
		}

		// Reveal everything is a debug tool, off by default. The one automatic case in normal play is a defeated
		// spectator's full map. A ForceOn/ForceOff set in the inspector overrides that.
		private bool ShouldRevealEverything()
		{
			return RevealMode switch
			{
				EMapRevealMode.ForceOn => true,
				EMapRevealMode.ForceOff => false,
				_ => Spectator != null && Spectator.Mode == ESpectatorMode.DefeatedSpectator,
			};
		}

		private bool CanZoomMapAtPointer(Vector2 screenPosition)
		{
			if (SelectionController != null && SelectionController.IsDraggingAssignedArea)
				return false;
			if (MapImage == null)
				return false;

			Camera eventCamera = GetEventCamera();
			if (SelectionController != null && SelectionController.IsPointerBlocked(screenPosition, eventCamera))
				return false;

			return RectTransformUtility.RectangleContainsScreenPoint(MapImage.rectTransform, screenPosition, eventCamera);
		}

		private Vector3 GetInitialCenterPosition()
		{
			if (_runner != null)
			{
				NetworkObject playerObject = _runner.GetPlayerObject(_runner.LocalPlayer);
				if (playerObject != null)
					return playerObject.transform.position;

				// No possessed survivor (the raid host): center on the local team's first alive survivor.
				if (TryGetLocalTeamCenter(out Vector3 teamCenter))
					return teamCenter;
			}

			return Vector3.zero;
		}

		private bool TryGetLocalTeamCenter(out Vector3 position)
		{
			position = default;
			if (_gameplay == null || _runner == null)
				return false;

			PlayerRef localPlayer = _runner.LocalPlayer;
			if (_gameplay.PlayerData.TryGet(localPlayer, out var data) == false)
				return false;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				if (data.IsCharacterAlive(i) == false)
					continue;

				var survivor = _gameplay.GetSurvivor(localPlayer, i);
				if (survivor == null)
					continue;

				position = survivor.transform.position;
				return true;
			}

			return false;
		}

		private void SetMapVisible(bool visible)
		{
			if (MapRoot != null)
				MapRoot.SetActive(visible);
		}

		private void EnsureInitialized()
		{
			if (_initialized)
				return;

			if (MapImage == null)
			{
				if (MapRoot != null)
					MapImage = MapRoot.GetComponentInChildren<RawImage>(true);

				if (MapImage == null)
					MapImage = GetComponentInChildren<RawImage>(true);
			}

			if (MapRoot == null)
				MapRoot = MapImage != null ? MapImage.gameObject : gameObject;

			if (CameraController == null)
				CameraController = FindObjectOfType<GameMapCameraController>(true);

			if (IconController == null)
				IconController = GetComponent<GameMapIconController>() ?? gameObject.AddComponent<GameMapIconController>();

			if (SelectionController == null)
				SelectionController = GetComponent<GameMapSelectionController>() ?? gameObject.AddComponent<GameMapSelectionController>();

			if (SelectionController != null && SelectionController.IconController == null)
				SelectionController.IconController = IconController;

			if (RosterController == null)
				RosterController = GetComponentInChildren<SurvivorRosterController>(true) ?? GetComponent<SurvivorRosterController>();

			if (RosterController != null)
			{
				if (RosterController.MapView == null)
					RosterController.MapView = this;
				if (RosterController.SelectionController == null)
					RosterController.SelectionController = SelectionController;
				if (RosterController.IconController == null)
					RosterController.IconController = IconController;
			}

			if (_canvas == null)
				_canvas = GetComponentInParent<Canvas>();

			ApplyRenderTexture();
			LogSetupWarnings();
			_initialized = true;
		}

		private void ApplyRenderTexture()
		{
			RenderTexture renderTexture = MapRenderTexture;

			if (renderTexture == null && MapImage != null)
				renderTexture = MapImage.texture as RenderTexture;

			if (renderTexture == null && CameraController != null && CameraController.Camera != null)
				renderTexture = CameraController.Camera.targetTexture;

			if (renderTexture == null && CreateRenderTextureIfMissing)
			{
				int width = Mathf.Max(16, RuntimeTextureSize.x);
				int height = Mathf.Max(16, RuntimeTextureSize.y);
				renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
				{
					name = "Runtime Map RenderTexture"
				};
				renderTexture.Create();
			}

			MapRenderTexture = renderTexture;

			if (MapImage != null && MapImage.texture != renderTexture)
				MapImage.texture = renderTexture;

			if (CameraController != null)
				CameraController.SetTargetTexture(renderTexture);
		}

		private void LogSetupWarnings()
		{
			if (_warningLogged)
				return;

			_warningLogged = true;

			if (MapImage == null)
				Debug.LogWarning($"{nameof(GameMapView)} on {name} has no RawImage assigned or found. The map overlay can open, but it cannot display the render texture.", this);

			if (CameraController == null)
				Debug.LogWarning($"{nameof(GameMapView)} on {name} has no {nameof(GameMapCameraController)} assigned or found. The overlay can open, but no map camera will render.", this);

			if (MapRenderTexture == null)
				Debug.LogWarning($"{nameof(GameMapView)} on {name} has no render texture. Assign one to the RawImage or MapRenderTexture field.", this);

			if (LogSetupStatus)
			{
				string cameraName = CameraController != null && CameraController.Camera != null ? CameraController.Camera.name : "None";
				string textureName = MapRenderTexture != null ? MapRenderTexture.name : "None";
				string rootName = MapRoot != null ? MapRoot.name : "None";
				string imageName = MapImage != null ? MapImage.name : "None";
				Debug.Log($"{nameof(GameMapView)} setup on {name}: Root={rootName}, Image={imageName}, Camera={cameraName}, Texture={textureName}.", this);
			}
		}
	}
}
