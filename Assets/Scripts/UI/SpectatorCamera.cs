using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SimpleFPS
{
	/// <summary>
	/// Shared third-person orbit camera used by both raid mode and defeated-spectate mode (see
	/// <see cref="SpectatorController"/>). It lazily creates a runtime Cinemachine virtual camera that follows a
	/// survivor; WASD orbits around it, a <see cref="CinemachineCollider"/> pulls the camera forward off geometry,
	/// and target switches glide rather than teleport. The followed survivor is never controlled — this is a
	/// view-only camera. See <c>Docs/SpectateMode.md</c>.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class SpectatorCamera : MonoBehaviour
	{
		[Header("Framing")]
		[Tooltip("Distance of the spectator camera from the followed survivor.")]
		public float Distance = 16f;
		[Tooltip("Starting downward tilt of the spectator camera, in degrees.")]
		public float DefaultPitch = 35f;
		public float MinPitch = 8f;
		public float MaxPitch = 80f;
		[Tooltip("Point on the survivor the camera aims at, relative to the survivor's feet.")]
		public Vector3 LookAtOffset = new Vector3(0f, 1.2f, 0f);
		public float FieldOfView = 55f;
		[Tooltip("Camera move damping. Higher = a longer, smoother glide when switching target.")]
		public float Damping = 1f;
		public int CameraPriority = 100;

		[Header("Orbit")]
		[Tooltip("Orbit yaw speed (A/D) in degrees per second.")]
		public float YawSpeed = 100f;
		[Tooltip("Orbit pitch speed (W/S) in degrees per second.")]
		public float PitchSpeed = 70f;

		[Header("Collision")]
		[Tooltip("Layers the camera pulls forward off of to avoid clipping. Leave empty to auto-use Default + MapNonVisible (the world-geometry layers zombies treat as walls).")]
		public LayerMask CollisionMask = default;
		[Tooltip("Spherecast radius for camera collision so the near plane does not poke through walls.")]
		public float CameraRadius = 0.3f;
		[Tooltip("Closest the camera will pull toward the followed survivor when something is in the way.")]
		public float MinDistanceFromTarget = 1.5f;

		private CinemachineVirtualCamera _camera;
		private CinemachineTransposer _transposer;
		private float _orbitYaw;
		private float _orbitPitch;
		private bool _orbitInitialized;

		/// <summary>Applies WASD orbit input. Call only when the orbit should respond (e.g. the map is closed).</summary>
		public void HandleOrbitInput()
		{
			EnsureOrbitInitialized();

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
			_orbitYaw += yaw * YawSpeed * dt;
			_orbitPitch = Mathf.Clamp(_orbitPitch + pitch * PitchSpeed * dt, MinPitch, MaxPitch);
		}

		/// <summary>Points the camera at <paramref name="target"/>, enabling it. Glides to a new target; snaps only
		/// the first time it turns on so it does not swoop in from the origin.</summary>
		public void SetTarget(Survivor target)
		{
			if (target == null)
			{
				Disable();
				return;
			}

			EnsureCamera();
			EnsureOrbitInitialized();

			Transform follow = target.transform;
			_camera.Follow = follow;
			_camera.LookAt = follow;
			_camera.Priority = CameraPriority;

			if (_transposer != null)
			{
				_transposer.m_FollowOffset = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f) * Vector3.back * Mathf.Max(1f, Distance);
				_transposer.m_XDamping = Damping;
				_transposer.m_YDamping = Damping;
				_transposer.m_ZDamping = Damping;
			}

			if (_camera.enabled == false)
			{
				_camera.enabled = true;
				_camera.PreviousStateIsValid = false;
			}
		}

		public void Disable()
		{
			if (_camera == null)
				return;

			_camera.Follow = null;
			_camera.LookAt = null;
			_camera.Priority = 0;
			_camera.enabled = false;
		}

		private void EnsureOrbitInitialized()
		{
			if (_orbitInitialized)
				return;

			_orbitYaw = 0f;
			_orbitPitch = Mathf.Clamp(DefaultPitch, MinPitch, MaxPitch);
			_orbitInitialized = true;
		}

		private void EnsureCamera()
		{
			if (_camera != null)
				return;

			var cameraObject = new GameObject("SpectatorCamera");
			_camera = cameraObject.AddComponent<CinemachineVirtualCamera>();
			_camera.m_Lens.FieldOfView = FieldOfView;

			_transposer = _camera.AddCinemachineComponent<CinemachineTransposer>();
			_transposer.m_BindingMode = CinemachineTransposer.BindingMode.WorldSpace;
			_transposer.m_FollowOffset = Quaternion.Euler(DefaultPitch, 0f, 0f) * Vector3.back * Mathf.Max(1f, Distance);
			_transposer.m_XDamping = Damping;
			_transposer.m_YDamping = Damping;
			_transposer.m_ZDamping = Damping;

			var composer = _camera.AddCinemachineComponent<CinemachineComposer>();
			composer.m_TrackedObjectOffset = LookAtOffset;
			composer.m_HorizontalDamping = 0.5f;
			composer.m_VerticalDamping = 0.5f;

			// Bump the camera forward off walls instead of clipping through them.
			var cameraCollider = cameraObject.AddComponent<CinemachineCollider>();
			cameraCollider.m_AvoidObstacles = true;
			cameraCollider.m_Strategy = CinemachineCollider.ResolutionStrategy.PullCameraForward;
			cameraCollider.m_CollideAgainst = CollisionMask.value != 0 ? CollisionMask.value : LayerMask.GetMask("Default", "MapNonVisible");
			cameraCollider.m_CameraRadius = Mathf.Max(0.01f, CameraRadius);
			cameraCollider.m_MinimumDistanceFromTarget = Mathf.Max(0.1f, MinDistanceFromTarget);
			cameraCollider.m_DampingWhenOccluded = 0.2f;
			cameraCollider.m_Damping = 0.5f;

			_camera.enabled = false;
		}

		private void OnDestroy()
		{
			if (_camera != null)
				Destroy(_camera.gameObject);
		}
	}
}
