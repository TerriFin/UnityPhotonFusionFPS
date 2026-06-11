using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	/// <summary>
	/// Always-on minimap shown in a HUD corner. Reuses <see cref="GameMapIconController"/> to draw
	/// the same enemy / zombie / pickup awareness markers the full <see cref="GameMapView"/> uses,
	/// but renders into its own small render texture from its own orthographic camera that follows
	/// the local player. Read-only — no clicks, no orders, no input handling.
	/// </summary>
	public sealed class GameMinimapView : MonoBehaviour, IGameMapView
	{
		[Header("Setup")]
		public GameObject MinimapRoot;
		public RawImage MapImage;
		public Camera MinimapCamera;
		public GameMapIconController IconController;

		[Tooltip("The full-screen GameMapView. If assigned, the minimap reuses its GameMapAwarenessTracker so both maps share one model of \"what does this player know\". Leave null to use SharedAwarenessTracker or fall back to a local tracker.")]
		public GameMapView MainMapView;
		[Tooltip("Optional explicit tracker to share. Only used when MainMapView is null. Both fields can be left empty — the minimap will then spin up its own tracker, which works but does duplicate work each frame.")]
		public GameMapAwarenessTracker SharedAwarenessTracker;

		[Header("Render Texture")]
		public RenderTexture MinimapRenderTexture;
		public bool CreateRenderTextureIfMissing = true;
		public Vector2Int RuntimeTextureSize = new Vector2Int(256, 256);

		[Header("Camera")]
		public float CameraHeight = 80f;
		public float OrthographicSize = 30f;
		public Transform OverrideFollowTarget;

		[Header("Behaviour")]
		[Tooltip("Minimap automatically hides while the full GameMapView is open so the two overlays do not double up.")]
		public bool HideWhileFullMapIsOpen = true;
		[Tooltip("Debug only: when on, the minimap shows every survivor, zombie, and pickup at its live position (no fog of war). Off for normal play. Uses its own awareness model so it does not reveal anything on the full map.")]
		public bool RevealEverything;
		[Tooltip("Set by GameUI to hide the minimap entirely (defeated spectators have no minimap).")]
		[HideInInspector]
		public bool Suppressed;
		public bool LogSetupStatus;

		private NetworkRunner _runner;
		private bool _initialized;
		private bool _warningLogged;

		public RawImage GetMapImage() => MapImage;

		public Vector2 WorldToMapUI(Vector3 worldPosition)
		{
			if (MapImage == null || MinimapCamera == null)
				return default;

			Vector3 viewport = MinimapCamera.WorldToViewportPoint(worldPosition);
			Rect rect = MapImage.rectTransform.rect;
			return new Vector2(
				(viewport.x - 0.5f) * rect.width,
				(viewport.y - 0.5f) * rect.height);
		}

		public bool IsWorldPositionVisibleOnMap(Vector3 worldPosition)
		{
			if (MinimapCamera == null)
				return false;

			Vector3 viewport = MinimapCamera.WorldToViewportPoint(worldPosition);
			// Discard points behind the camera. WorldToViewportPoint returns negative z for those and
			// the x/y values become a wrong-side reflection that would otherwise stick icons on the map.
			return viewport.z > 0f &&
			       viewport.x >= 0f && viewport.x <= 1f &&
			       viewport.y >= 0f && viewport.y <= 1f;
		}

		private void Awake()
		{
			EnsureInitialized();
		}

		public void Tick(bool gameplayActive, NetworkRunner runner, Gameplay gameplay)
		{
			EnsureInitialized();
			_runner = runner;

			if (Application.isBatchMode)
				return;

			bool fullMapOpen = HideWhileFullMapIsOpen && GameMapView.IsAnyMapOpen;
			bool shouldShow = gameplayActive && fullMapOpen == false && Suppressed == false;

			SetVisible(shouldShow);
			if (MinimapCamera != null)
				MinimapCamera.enabled = shouldShow;

			if (shouldShow == false || gameplay == null || runner == null)
				return;

			TryAdoptSharedTracker();
			UpdateCameraTransform(runner);

			if (IconController != null)
				IconController.Tick(this, gameplay, runner, RevealEverything);
		}

		private void TryAdoptSharedTracker()
		{
			if (IconController == null)
				return;

			// A constantly-revealing minimap must keep its own awareness tracker. Sharing the full map's tracker
			// would seed every entity into the shared memory, leaking the reveal onto the (fog-of-war) full map.
			if (RevealEverything)
				return;

			GameMapAwarenessTracker desired = SharedAwarenessTracker;
			if (desired == null && MainMapView != null && MainMapView.IconController != null)
				desired = MainMapView.IconController.AwarenessTracker;

			if (desired != null && IconController.AwarenessTracker != desired)
				IconController.AwarenessTracker = desired;
		}

		private void UpdateCameraTransform(NetworkRunner runner)
		{
			if (MinimapCamera == null)
				return;

			Vector3 followPosition = GetFollowPosition(runner);

			MinimapCamera.orthographic = true;
			MinimapCamera.orthographicSize = Mathf.Max(1f, OrthographicSize);
			MinimapCamera.transform.position = new Vector3(followPosition.x, CameraHeight, followPosition.z);
			MinimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
		}

		private Vector3 GetFollowPosition(NetworkRunner runner)
		{
			if (OverrideFollowTarget != null)
				return OverrideFollowTarget.position;

			if (runner != null)
			{
				NetworkObject playerObject = runner.GetPlayerObject(runner.LocalPlayer);
				if (playerObject != null)
					return playerObject.transform.position;
			}

			return MinimapCamera != null
				? new Vector3(MinimapCamera.transform.position.x, 0f, MinimapCamera.transform.position.z)
				: Vector3.zero;
		}

		private void SetVisible(bool visible)
		{
			if (MinimapRoot != null && MinimapRoot.activeSelf != visible)
				MinimapRoot.SetActive(visible);
		}

		private void EnsureInitialized()
		{
			if (_initialized)
				return;

			if (MapImage == null)
			{
				if (MinimapRoot != null)
					MapImage = MinimapRoot.GetComponentInChildren<RawImage>(true);
				if (MapImage == null)
					MapImage = GetComponentInChildren<RawImage>(true);
			}

			if (MinimapRoot == null)
				MinimapRoot = MapImage != null ? MapImage.gameObject : gameObject;

			if (IconController == null)
				IconController = GetComponent<GameMapIconController>() ?? gameObject.AddComponent<GameMapIconController>();

			// Reuse the main map's awareness tracker if one is reachable now. Tick() re-runs this
			// each frame so lazy-attached trackers on the main map (the default path) still get
			// adopted on the first frame the minimap renders.
			TryAdoptSharedTracker();

			ApplyRenderTexture();
			LogSetupWarnings();

			// Start hidden until the first Tick decides we should be on. Avoids one frame of camera
			// render + UI flash before GameUI.Update runs.
			if (MinimapCamera != null)
				MinimapCamera.enabled = false;
			if (MinimapRoot != null)
				MinimapRoot.SetActive(false);

			_initialized = true;
		}

		private void ApplyRenderTexture()
		{
			RenderTexture renderTexture = MinimapRenderTexture;

			if (renderTexture == null && MapImage != null)
				renderTexture = MapImage.texture as RenderTexture;

			if (renderTexture == null && MinimapCamera != null)
				renderTexture = MinimapCamera.targetTexture;

			if (renderTexture == null && CreateRenderTextureIfMissing)
			{
				int width = Mathf.Max(16, RuntimeTextureSize.x);
				int height = Mathf.Max(16, RuntimeTextureSize.y);
				renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
				{
					name = "Runtime Minimap RenderTexture"
				};
				renderTexture.Create();
			}

			MinimapRenderTexture = renderTexture;

			if (MapImage != null && MapImage.texture != renderTexture)
				MapImage.texture = renderTexture;

			if (MinimapCamera != null && MinimapCamera.targetTexture != renderTexture)
				MinimapCamera.targetTexture = renderTexture;
		}

		private void LogSetupWarnings()
		{
			if (_warningLogged)
				return;

			_warningLogged = true;

			if (MapImage == null)
				Debug.LogWarning($"{nameof(GameMinimapView)} on {name} has no RawImage assigned or found. Nothing will be drawn for the minimap.", this);

			if (MinimapCamera == null)
				Debug.LogWarning($"{nameof(GameMinimapView)} on {name} has no minimap Camera assigned. Assign an orthographic camera that renders the world from above.", this);

			if (MinimapRenderTexture == null)
				Debug.LogWarning($"{nameof(GameMinimapView)} on {name} has no render texture. Assign one or enable CreateRenderTextureIfMissing.", this);

			if (LogSetupStatus)
			{
				string cameraName = MinimapCamera != null ? MinimapCamera.name : "None";
				string textureName = MinimapRenderTexture != null ? MinimapRenderTexture.name : "None";
				string trackerName = SharedAwarenessTracker != null ? SharedAwarenessTracker.name : "(local)";
				Debug.Log($"{nameof(GameMinimapView)} setup on {name}: Image={(MapImage != null ? MapImage.name : "None")}, Camera={cameraName}, Texture={textureName}, Tracker={trackerName}.", this);
			}
		}
	}
}
