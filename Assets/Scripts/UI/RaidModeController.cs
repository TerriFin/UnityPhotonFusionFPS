using Cinemachine;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SimpleFPS
{
	/// <summary>
	/// Local (host-peer) UX for raid mode. The host is a pure RTS commander who never possesses a survivor
	/// (see <see cref="Gameplay.SpawnTeam"/> / <see cref="RaidModeRules"/>). Instead of possessing, the host
	/// <b>inspects</b> a survivor: a third-person spectator camera follows the inspected survivor while it stays
	/// fully AI-controlled.
	///
	/// In the 3D view (map closed) WASD orbits the camera around the inspected survivor and Ctrl/Shift switch to
	/// the previous/next survivor. On the map, selecting one survivor and pressing Space switches the inspect
	/// target without closing the map (see <see cref="GameMapSelectionController"/>). The inspected survivor is
	/// drawn larger on the map, mirroring the active-character highlight for normal players. Target switches glide
	/// the camera between survivors. See <c>Docs/RaidMode.md</c>.
	///
	/// Runs only on the local peer. The raid host is detected as <c>RaidMode &amp;&amp; HasStateAuthority</c>,
	/// which is uniquely true on the host peer; clients (no state authority) are never treated as the raid host.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class RaidModeController : MonoBehaviour
	{
		[Header("Spectator Camera")]
		[Tooltip("Distance of the spectator camera from the inspected survivor.")]
		public float SpectatorDistance = 16f;
		[Tooltip("Starting downward tilt of the spectator camera, in degrees.")]
		public float SpectatorDefaultPitch = 35f;
		public float SpectatorMinPitch = 8f;
		public float SpectatorMaxPitch = 80f;
		[Tooltip("Orbit yaw speed (A/D) in degrees per second while the map is closed.")]
		public float SpectatorYawSpeed = 100f;
		[Tooltip("Orbit pitch speed (W/S) in degrees per second while the map is closed.")]
		public float SpectatorPitchSpeed = 70f;
		[Tooltip("Point on the survivor the spectator camera aims at, relative to the survivor's feet.")]
		public Vector3 SpectatorLookAtOffset = new Vector3(0f, 1.2f, 0f);
		public float SpectatorFieldOfView = 55f;
		[Tooltip("Camera move damping. Higher = a longer, smoother glide when switching inspect target.")]
		public float SpectatorDamping = 1f;
		public int SpectatorCameraPriority = 100;

		[Header("Spectator Camera Collision")]
		[Tooltip("Layers the spectator camera pulls forward off of to avoid clipping through geometry. Leave empty to auto-use Default + MapNonVisible (the world-geometry layers zombies treat as walls).")]
		public LayerMask SpectatorCollisionMask = default;
		[Tooltip("Spherecast radius for camera collision so the near plane does not poke through walls.")]
		public float SpectatorCameraRadius = 0.3f;
		[Tooltip("Closest the camera will pull toward the inspected survivor when something is in the way.")]
		public float SpectatorMinDistanceFromTarget = 1.5f;

		public bool IsLocalRaidHost { get; private set; }
		public Survivor InspectTarget => _inspectTarget;

		private CinemachineVirtualCamera _spectatorCamera;
		private CinemachineTransposer _spectatorTransposer;
		private Survivor _inspectTarget;
		private float _orbitYaw;
		private float _orbitPitch;
		private bool _orbitInitialized;
		private bool _hasAutoOpenedMap;

		// Set by the map's Space handler to switch the inspected survivor without closing the map.
		public void SetInspectTarget(Survivor survivor)
		{
			if (survivor != null)
				_inspectTarget = survivor;
		}

		public void Tick(Gameplay gameplay, NetworkRunner runner, GameMapView mapView, bool gameplayActive)
		{
			IsLocalRaidHost = gameplay != null &&
			                  gameplay.Object != null &&
			                  gameplay.HasStateAuthority &&
			                  gameplay.RaidMode;

			if (IsLocalRaidHost && gameplayActive)
				UpdateInspectCamera(gameplay, runner, mapView);
			else
				DisableSpectatorCamera();
		}

		private void UpdateInspectCamera(Gameplay gameplay, NetworkRunner runner, GameMapView mapView)
		{
			if (runner == null)
			{
				DisableSpectatorCamera();
				return;
			}

			EnsureSpectatorCamera();
			EnsureOrbitInitialized();

			bool mapOpen = mapView != null && mapView.IsMapOpen;

			// In the 3D view, Ctrl/Shift switch which survivor is inspected and WASD orbit the camera around it.
			// While the map is open those keys belong to the map (selection cycling and map panning), so skip them.
			if (mapOpen == false)
			{
				HandleInspectCycleInput(gameplay, runner);
				HandleOrbitInput();
			}

			if (IsValidInspectTarget(_inspectTarget, runner) == false)
				_inspectTarget = FindFirstInspectTarget(gameplay, runner);

			if (_inspectTarget == null)
			{
				DisableSpectatorCamera();
				return;
			}

			// The host starts in the map (their primary RTS tool), but unlike the old lock they can close it.
			// Open it once, when survivors first exist, then never force it again.
			if (_hasAutoOpenedMap == false && mapView != null)
			{
				mapView.OpenMap();
				_hasAutoOpenedMap = true;
			}

			Transform target = _inspectTarget.transform;
			_spectatorCamera.Follow = target;
			_spectatorCamera.LookAt = target;
			_spectatorCamera.Priority = SpectatorCameraPriority;
			if (_spectatorTransposer != null)
			{
				_spectatorTransposer.m_FollowOffset = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f) * Vector3.back * Mathf.Max(1f, SpectatorDistance);
				_spectatorTransposer.m_XDamping = SpectatorDamping;
				_spectatorTransposer.m_YDamping = SpectatorDamping;
				_spectatorTransposer.m_ZDamping = SpectatorDamping;
			}

			// Snap only when the camera first turns on, so it doesn't swoop in from the origin at match start.
			// Inspect-target switches then glide via the transposer/composer damping.
			if (_spectatorCamera.enabled == false)
			{
				_spectatorCamera.enabled = true;
				_spectatorCamera.PreviousStateIsValid = false;
			}
		}

		private void HandleOrbitInput()
		{
			var keyboard = Keyboard.current;
			if (keyboard == null)
				return;

			float yaw = 0f;
			float pitch = 0f;
			if (keyboard.aKey.isPressed) yaw -= 1f;
			if (keyboard.dKey.isPressed) yaw += 1f;
			if (keyboard.wKey.isPressed) pitch += 1f;
			if (keyboard.sKey.isPressed) pitch -= 1f;

			float dt = Time.unscaledDeltaTime;
			_orbitYaw += yaw * SpectatorYawSpeed * dt;
			_orbitPitch = Mathf.Clamp(_orbitPitch + pitch * SpectatorPitchSpeed * dt, SpectatorMinPitch, SpectatorMaxPitch);
		}

		private void HandleInspectCycleInput(Gameplay gameplay, NetworkRunner runner)
		{
			var keyboard = Keyboard.current;
			if (keyboard == null)
				return;

			bool nextPressed = keyboard.leftCtrlKey.wasPressedThisFrame || keyboard.rightCtrlKey.wasPressedThisFrame;
			bool previousPressed = keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;
			if (nextPressed == previousPressed)
				return;

			Survivor next = FindAdjacentInspectTarget(gameplay, runner, nextPressed ? 1 : -1);
			if (next != null)
				_inspectTarget = next;
		}

		private bool IsValidInspectTarget(Survivor survivor, NetworkRunner runner)
		{
			return survivor != null &&
			       survivor.Object != null && survivor.Object.IsValid &&
			       survivor.Health != null && survivor.Health.IsAlive &&
			       survivor.OwnerRef == runner.LocalPlayer;
		}

		// Lowest-index alive survivor owned by the local (host) player. Used when there is no valid target yet or
		// the current one died, so the camera holds on the "first" survivor and advances on death.
		private Survivor FindFirstInspectTarget(Gameplay gameplay, NetworkRunner runner)
		{
			if (gameplay == null)
				return null;

			PlayerRef localPlayer = runner.LocalPlayer;
			if (gameplay.PlayerData.TryGet(localPlayer, out var data) == false)
				return null;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				var survivor = gameplay.GetSurvivor(localPlayer, i);
				if (IsValidInspectTarget(survivor, runner))
					return survivor;
			}

			return null;
		}

		// Next/previous alive owned survivor relative to the current inspect target, wrapping around.
		private Survivor FindAdjacentInspectTarget(Gameplay gameplay, NetworkRunner runner, int direction)
		{
			if (gameplay == null)
				return null;

			PlayerRef localPlayer = runner.LocalPlayer;
			if (gameplay.PlayerData.TryGet(localPlayer, out var data) == false)
				return null;

			int count = Mathf.Max(0, data.CharacterCount);
			if (count <= 0)
				return null;

			int startIndex = _inspectTarget != null ? _inspectTarget.CharacterIndex : 0;
			for (int step = 1; step <= count; step++)
			{
				int index = ((startIndex + direction * step) % count + count) % count;
				var survivor = gameplay.GetSurvivor(localPlayer, index);
				if (IsValidInspectTarget(survivor, runner))
					return survivor;
			}

			return null;
		}

		private void EnsureOrbitInitialized()
		{
			if (_orbitInitialized)
				return;

			_orbitYaw = 0f;
			_orbitPitch = Mathf.Clamp(SpectatorDefaultPitch, SpectatorMinPitch, SpectatorMaxPitch);
			_orbitInitialized = true;
		}

		private void EnsureSpectatorCamera()
		{
			if (_spectatorCamera != null)
				return;

			var cameraObject = new GameObject("RaidSpectatorCamera");
			_spectatorCamera = cameraObject.AddComponent<CinemachineVirtualCamera>();
			_spectatorCamera.m_Lens.FieldOfView = SpectatorFieldOfView;

			_spectatorTransposer = _spectatorCamera.AddCinemachineComponent<CinemachineTransposer>();
			_spectatorTransposer.m_BindingMode = CinemachineTransposer.BindingMode.WorldSpace;
			_spectatorTransposer.m_FollowOffset = Quaternion.Euler(SpectatorDefaultPitch, 0f, 0f) * Vector3.back * Mathf.Max(1f, SpectatorDistance);
			_spectatorTransposer.m_XDamping = SpectatorDamping;
			_spectatorTransposer.m_YDamping = SpectatorDamping;
			_spectatorTransposer.m_ZDamping = SpectatorDamping;

			var composer = _spectatorCamera.AddCinemachineComponent<CinemachineComposer>();
			composer.m_TrackedObjectOffset = SpectatorLookAtOffset;
			composer.m_HorizontalDamping = 0.5f;
			composer.m_VerticalDamping = 0.5f;

			// Bump the camera forward off walls instead of clipping through them. The collider is an extension
			// that casts from the camera toward the inspected survivor (the LookAt target) and pulls in when blocked.
			var cameraCollider = cameraObject.AddComponent<CinemachineCollider>();
			cameraCollider.m_AvoidObstacles = true;
			cameraCollider.m_Strategy = CinemachineCollider.ResolutionStrategy.PullCameraForward;
			cameraCollider.m_CollideAgainst = SpectatorCollisionMask.value != 0 ? SpectatorCollisionMask.value : LayerMask.GetMask("Default", "MapNonVisible");
			cameraCollider.m_CameraRadius = Mathf.Max(0.01f, SpectatorCameraRadius);
			cameraCollider.m_MinimumDistanceFromTarget = Mathf.Max(0.1f, SpectatorMinDistanceFromTarget);
			cameraCollider.m_DampingWhenOccluded = 0.2f;
			cameraCollider.m_Damping = 0.5f;

			_spectatorCamera.enabled = false;
		}

		private void DisableSpectatorCamera()
		{
			_inspectTarget = null;
			_hasAutoOpenedMap = false;

			if (_spectatorCamera == null)
				return;

			_spectatorCamera.Follow = null;
			_spectatorCamera.LookAt = null;
			_spectatorCamera.Priority = 0;
			_spectatorCamera.enabled = false;
		}

		private void OnDestroy()
		{
			if (_spectatorCamera != null)
				Destroy(_spectatorCamera.gameObject);
		}
	}
}
