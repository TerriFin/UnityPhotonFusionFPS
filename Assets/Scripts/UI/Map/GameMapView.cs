using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace SimpleFPS
{
	public sealed class GameMapView : MonoBehaviour
	{
		[Header("Setup")]
		public GameObject MapRoot;
		public RawImage MapImage;
		public GameMapCameraController CameraController;
		public GameMapIconController IconController;
		public GameMapSelectionController SelectionController;

		[Header("Render Texture")]
		public RenderTexture MapRenderTexture;
		public bool CreateRenderTextureIfMissing = true;
		public Vector2Int RuntimeTextureSize = new Vector2Int(1024, 1024);
		public bool LogSetupStatus;

		[Header("Input")]
		public Key MapKey = Key.LeftAlt;

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
				if (_isMapOpen)
					CloseMap();

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

			bool suppressZoom = SelectionController != null && SelectionController.IsDraggingAssignedArea;
			float zoomInput = mouse != null && suppressZoom == false ? mouse.scroll.ReadValue().y * 0.01f : 0f;
			CameraController.Tick(Time.unscaledDeltaTime, panInput.normalized, zoomInput);

			if (IconController != null)
				IconController.Tick(this, gameplay, _runner);
			if (SelectionController != null)
				SelectionController.Tick(this, gameplay, _runner);
		}

		private Vector3 GetInitialCenterPosition()
		{
			if (_runner != null)
			{
				NetworkObject playerObject = _runner.GetPlayerObject(_runner.LocalPlayer);
				if (playerObject != null)
					return playerObject.transform.position;
			}

			return Vector3.zero;
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
