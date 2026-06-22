using UnityEngine;
using Fusion;
using Fusion.Addons.SimpleKCC;
using Cinemachine;

namespace SimpleFPS
{
	/// <summary>
	/// Main survivor script which handles input processing, visuals.
	/// </summary>
	[DefaultExecutionOrder(-5)]
	public class Survivor : NetworkBehaviour
	{
		private const float AnimationMoveDeadZone = 0.05f;

		[Header("Components")]
		public SimpleKCC     KCC;
		public Weapons       Weapons;
		public Health        Health;
		public Animator      Animator;
		public HitboxRoot    HitboxRoot;

		[Header("Setup")]
		public float         MoveSpeed = 6f;
		public float         JumpForce = 10f;
		public AudioSource   JumpSound;
		public AudioClip[]   JumpClips;
		public Transform     CameraHandle;
		public GameObject    FirstPersonRoot;
		public GameObject    ThirdPersonRoot;
		public NetworkObject SprayPrefab;

		[Header("Movement")]
		public float         AIMoveSpeed = 4.5f;
		public float         AIFollowFullSpeedRadius = 16f;
		public float         UpGravity = 15f;
		public float         DownGravity = 25f;
		public float         GroundAcceleration = 55f;
		public float         GroundDeceleration = 25f;
		public float         AirAcceleration = 25f;
		public float         AirDeceleration = 1.3f;

		[Networked, HideInInspector]
		public PlayerRef OwnerRef { get; set; }
		[Networked, HideInInspector]
		public int CharacterIndex { get; set; }
		[Networked, HideInInspector]
		public ESurvivorWeaponPreference WeaponPreference { get; private set; }

		[Networked]
		private NetworkButtons _previousButtons { get; set; }
		[Networked]
		private int _jumpCount { get; set; }
		[Networked]
		private Vector3 _moveVelocity { get; set; }

		private int _visibleJumpCount;
		private bool _firstPersonVisualsActive;
		private CinemachineVirtualCamera[] _virtualCameras;

		// Owner/index this survivor was last registered into Gameplay's lookup with. Used to re-sync the lookup on
		// every peer when recruitment changes the networked OwnerRef/CharacterIndex (which does not fire Spawned).
		private bool _hasRegisteredIdentity;
		private PlayerRef _registeredOwnerRef;
		private int _registeredCharacterIndex;

		private SceneObjects _sceneObjects;
		private ICharacterInputSource _aiController;
		private SurvivorNonCombatAISettings _nonCombatAISettings = SurvivorNonCombatAISettings.Default;
		private SurvivorCombatAISettings _combatAISettings = SurvivorCombatAISettings.Default;

		public CharacterSensor Sensor { get; private set; }
		public SurvivorAIShooting AIShooting { get; private set; }
		public CharacterNavigator Navigator { get; private set; }
		public CharacterSeparation Separation { get; private set; }
		public SurvivorNonCombatAI NonCombatAI { get; private set; }
		public SurvivorCombatAI CombatAI { get; private set; }
		public SurvivorWeaponPreferenceAI WeaponPreferenceAI { get; private set; }
		public SurvivorNonCombatAISettings NonCombatAISettings => _nonCombatAISettings;
		public SurvivorCombatAISettings CombatAISettings
		{
			get
			{
				SurvivorCombatAISettings settings = _combatAISettings;
				if (Object != null && Object.IsValid)
					settings.WeaponPreference = WeaponPreference;
				return settings;
			}
		}
		public bool IsNeutral => CharacterFactionUtility.IsNeutralSurvivor(this);

		public void PlayFireEffect()
		{
			Animator.SetTrigger("Fire");
		}

		public void SetAI(ICharacterInputSource aiController)
		{
			if (aiController is SurvivorNonCombatAI nonCombatAI)
			{
				EnsureNonCombatAI();
				nonCombatAI.SetSettings(_nonCombatAISettings);
				_aiController = nonCombatAI;
				return;
			}

			if (aiController == null)
			{
				EnsureNonCombatAI();
				NonCombatAI.SetSettings(_nonCombatAISettings);
				_aiController = NonCombatAI;
				return;
			}

			_aiController = aiController;
		}

		public void SetNonCombatAISettings(SurvivorNonCombatAISettings settings)
		{
			_nonCombatAISettings = settings;

			if (_aiController is SurvivorNonCombatAI nonCombatAI)
			{
				nonCombatAI.SetSettings(settings);
				return;
			}

			EnsureNonCombatAI();
			NonCombatAI.SetSettings(settings);
			SetAI(NonCombatAI);
		}

		public void SetCombatAISettings(SurvivorCombatAISettings settings)
		{
			_combatAISettings = settings;
			if (HasStateAuthority && Object != null && Object.IsValid)
				WeaponPreference = settings.WeaponPreference;
			EnsureCombatAI();
			CombatAI.SetSettings(settings);
		}

		public void ReceiveInvestigationAlert(Vector3 target, int stimulusTick, bool lookOnly = false)
		{
			if (IsActiveCharacter())
				return;

			if (_aiController is SurvivorNonCombatAI nonCombatAI)
			{
				nonCombatAI.ReceiveInvestigationAlert(target, stimulusTick, null, lookOnly);
			}
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick)
		{
			if (IsActiveCharacter())
				return;

			if (_aiController is SurvivorNonCombatAI nonCombatAI)
			{
				nonCombatAI.ReceiveInvestigationStimulus(target, stimulusTick);
			}
		}

		public void SetIdleAI()
		{
			EnsureNonCombatAI();
			NonCombatAI.SetHoldPosition(transform.position);
			SetAI(NonCombatAI);
		}

		public void ResetVerticalLook()
		{
			float currentYaw = KCC.GetLookRotation(false, true).y;
			KCC.SetLookRotation(new Vector2(0f, currentYaw), -89f, 89f);
			RefreshCamera();
		}

		public bool IsFollowing(Survivor target)
		{
			return _aiController is SurvivorNonCombatAI nonCombatAI &&
			       nonCombatAI.Assignment == ENonCombatAssignment.FollowSurvivor &&
			       nonCombatAI.FollowTarget == target;
		}

		public override void Spawned()
		{
			name = $"{Object.InputAuthority}:{CharacterIndex} ({(HasInputAuthority ? "Input Authority" : (HasStateAuthority ? "State Authority" : "Proxy"))})";

			_virtualCameras = GetComponentsInChildren<CinemachineVirtualCamera>(true);

			// Default to third-person visuals and disabled cameras. Render() will enable
			// first-person + virtual cameras when this character becomes active for the local player.
			SetFirstPersonVisuals(false);

			_sceneObjects = Runner.GetSingleton<SceneObjects>();
			Sensor = GetComponent<CharacterSensor>();
			if (Sensor == null)
			{
				Sensor = gameObject.AddComponent<CharacterSensor>();
			}

			AIShooting = GetComponent<SurvivorAIShooting>();
			if (AIShooting == null)
			{
				AIShooting = gameObject.AddComponent<SurvivorAIShooting>();
			}

			EnsureWeaponPreferenceAI();
			if (HasStateAuthority)
				WeaponPreference = _combatAISettings.WeaponPreference;

			EnsureCombatAI();
			CombatAI.Activate(this);
			CombatAI.SetSettings(_combatAISettings);

			Navigator = GetComponent<CharacterNavigator>();
			if (Navigator == null)
			{
				Navigator = gameObject.AddComponent<CharacterNavigator>();
			}

			Separation = GetComponent<CharacterSeparation>();
			if (Separation == null)
			{
				Separation = gameObject.AddComponent<CharacterSeparation>();
			}
			Separation.Activate(this);

			EnsureNonCombatAI();
			NonCombatAI.Activate(this);

			if (_sceneObjects != null && _sceneObjects.Gameplay != null)
			{
				_sceneObjects.Gameplay.RegisterSurvivor(this);
				_registeredOwnerRef = OwnerRef;
				_registeredCharacterIndex = CharacterIndex;
				_hasRegisteredIdentity = true;
			}

			SetIdleAI();
		}

		// Keeps Gameplay's per-peer survivor lookup in sync when recruitment changes the networked
		// OwnerRef/CharacterIndex. Recruitment does not fire Spawned/Despawned, so without this the recruit would
		// stay in the neutral list on non-authority peers and be invisible to cycling, AI commands, and switching.
		private void SyncRegistrationIdentity()
		{
			if (_hasRegisteredIdentity == false)
				return;
			if (OwnerRef == _registeredOwnerRef && CharacterIndex == _registeredCharacterIndex)
				return;
			if (_sceneObjects == null || _sceneObjects.Gameplay == null)
				return;

			_sceneObjects.Gameplay.ReregisterSurvivor(this, _registeredOwnerRef, _registeredCharacterIndex);
			_registeredOwnerRef = OwnerRef;
			_registeredCharacterIndex = CharacterIndex;
		}

		private void EnsureNonCombatAI()
		{
			if (NonCombatAI != null)
				return;

			NonCombatAI = GetComponent<SurvivorNonCombatAI>();
			if (NonCombatAI == null)
			{
				NonCombatAI = gameObject.AddComponent<SurvivorNonCombatAI>();
			}
		}

		private void EnsureCombatAI()
		{
			if (CombatAI != null)
				return;

			CombatAI = GetComponent<SurvivorCombatAI>();
			if (CombatAI == null)
			{
				CombatAI = gameObject.AddComponent<SurvivorCombatAI>();
			}
		}

		private void EnsureWeaponPreferenceAI()
		{
			if (WeaponPreferenceAI != null)
				return;

			WeaponPreferenceAI = GetComponent<SurvivorWeaponPreferenceAI>();
			if (WeaponPreferenceAI == null)
				WeaponPreferenceAI = gameObject.AddComponent<SurvivorWeaponPreferenceAI>();
			WeaponPreferenceAI.Activate(this);
		}

		public override void Despawned(NetworkRunner runner, bool hasState)
		{
			if (Separation != null)
			{
				Separation.Deactivate();
			}

			if (_sceneObjects != null && _sceneObjects.Gameplay != null)
			{
				_sceneObjects.Gameplay.UnregisterSurvivor(this);
			}
		}

		public override void FixedUpdateNetwork()
		{
			if (_sceneObjects.Gameplay.State == EGameplayState.Finished)
			{
				// After gameplay is finished we still want the survivor to finish movement and not get stuck in the air.
				MoveSurvivor();
				return;
			}

			if (Health.IsAlive == false)
			{
				// We want dead body to finish movement - fall to ground etc.
				MoveSurvivor();

				// Disable physics casts and collisions with other players.
				KCC.SetColliderLayer(LayerMask.NameToLayer("Ignore Raycast"));
				KCC.SetCollisionLayerMask(LayerMask.GetMask("Default"));

				HitboxRoot.HitboxRootActive = false;
				return;
			}

			// Input routing: active character consumes player input, inactive characters are driven
			// by the AI controller on the state authority. Proxies (non-authority) skip everything
			// and just interpolate the networked state.
			bool isActive = IsActiveCharacter();

			NetworkedInput input;
			bool hasInput;

			if (isActive)
			{
				hasInput = GetInput(out input);
			}
			else if (HasStateAuthority)
			{
				if (_aiController == null)
				{
					SetIdleAI();
				}

				input = _aiController.GetInput(Runner);
				hasInput = true;
			}
			else
			{
				return;
			}

			if (hasInput)
			{
				// Settled inactive characters (grounded + near-zero velocity) need no physics this
				// tick. KCC.Move runs physics sweeps even for zero input; skipping it once a
				// character is standing still cuts per-tick simulation cost by ~90% for the 4
				// idle characters that aren't being controlled. They resume full simulation the
				// moment they become active or leave the ground.
				if (!isActive && KCC.IsGrounded && _moveVelocity.sqrMagnitude < 0.01f && HasAnyInput(input) == false && HasSeparationIntent() == false)
					return;

				ProcessInput(input, isActive);
			}
			else
			{
				// Active character with no input this tick (packet loss) — keep movement going.
				MoveSurvivor();
				RefreshCamera();
			}
		}

		public override void Render()
		{
			SyncRegistrationIdentity();

			if (_sceneObjects.Gameplay.State == EGameplayState.Finished)
				return;

			UpdateFirstPersonVisuals();

			var moveVelocity = GetAnimationMoveVelocity();

			// Set animation parameters.
			Animator.SetFloat(AnimatorId.LocomotionTime, Time.time * 2f);
			Animator.SetBool(AnimatorId.IsAlive, Health.IsAlive);
			Animator.SetBool(AnimatorId.IsGrounded, KCC.IsGrounded);
			Animator.SetBool(AnimatorId.IsReloading, Weapons.CurrentWeapon.IsReloading);
			Animator.SetFloat(AnimatorId.MoveX, moveVelocity.x, 0.05f, Time.deltaTime);
			Animator.SetFloat(AnimatorId.MoveZ, moveVelocity.z, 0.05f, Time.deltaTime);
			Animator.SetFloat(AnimatorId.MoveSpeed, moveVelocity.magnitude, 0.1f, Time.deltaTime);
			Animator.SetFloat(AnimatorId.Look, -KCC.GetLookRotation(true, false).x / 90f);

			if (Health.IsAlive == false)
			{
				// Disable UpperBody (override) and Look (additive) layers. Death animation is full-body.

				int upperBodyLayerIndex = Animator.GetLayerIndex("UpperBody");
				Animator.SetLayerWeight(upperBodyLayerIndex, Mathf.Max(0f, Animator.GetLayerWeight(upperBodyLayerIndex) - Time.deltaTime));

				int lookLayerIndex = Animator.GetLayerIndex("Look");
				Animator.SetLayerWeight(lookLayerIndex, Mathf.Max(0f, Animator.GetLayerWeight(lookLayerIndex) - Time.deltaTime));
			}

			if (_visibleJumpCount < _jumpCount)
			{
				Animator.SetTrigger("Jump");

				JumpSound.clip = JumpClips[Random.Range(0, JumpClips.Length)];
				JumpSound.Play();
			}

			_visibleJumpCount = _jumpCount;
		}

		private void LateUpdate()
		{
			if (HasInputAuthority == false)
				return;

			if (IsActiveCharacter() == false)
				return;

			RefreshCamera();
		}

		private void ProcessInput(NetworkedInput input, bool isActive)
		{
			// Processing input - look rotation, jump, movement, weapon fire, weapon switching, weapon reloading, spray decal.

			KCC.AddLookRotation(input.LookRotationDelta, -89f, 89f);

			// It feels better when player falls quicker
			KCC.SetGravity(KCC.RealVelocity.y >= 0f ? -UpGravity : -DownGravity);

			var inputDirection = KCC.TransformRotation * new Vector3(input.MoveDirection.x, 0f, input.MoveDirection.y);
			var jumpImpulse = 0f;

			if (input.Buttons.WasPressed(_previousButtons, EInputButton.Jump) && KCC.IsGrounded)
			{
				jumpImpulse = JumpForce;
			}

			MoveSurvivor(inputDirection * GetCurrentMoveSpeed(isActive), jumpImpulse);
			RefreshCamera();

			if (KCC.HasJumped)
			{
				_jumpCount++;
			}

			if (input.Buttons.IsSet(EInputButton.Fire))
			{
				bool justPressed = input.Buttons.WasPressed(_previousButtons, EInputButton.Fire);
				Weapons.Fire(justPressed);
				Health.StopImmortality();
			}
			else if (input.Buttons.IsSet(EInputButton.Reload))
			{
				Weapons.Reload();
			}

			if (input.Buttons.WasPressed(_previousButtons, EInputButton.Pistol))
			{
				Weapons.SwitchWeapon(EWeaponType.Pistol);
			}
			else if (input.Buttons.WasPressed(_previousButtons, EInputButton.Rifle))
			{
				Weapons.SwitchWeapon(EWeaponType.Rifle);
			}
			else if (input.Buttons.WasPressed(_previousButtons, EInputButton.Shotgun))
			{
				Weapons.SwitchWeapon(EWeaponType.Shotgun);
			}

			if (input.Buttons.WasPressed(_previousButtons, EInputButton.CommandFollow) && isActive && HasStateAuthority)
			{
				_sceneObjects.Gameplay.SurvivorAICommands.SetNearbyTeamFollow(OwnerRef, CharacterIndex);
			}

			if (input.Buttons.WasPressed(_previousButtons, EInputButton.CommandMove) && isActive && HasStateAuthority)
			{
				_sceneObjects.Gameplay.SurvivorAICommands.MoveNearbyTeamToLookPoint(OwnerRef, CharacterIndex);
			}

			if (input.Buttons.WasPressed(_previousButtons, EInputButton.Spray) && HasStateAuthority)
			{
				if (Runner.GetPhysicsScene().Raycast(CameraHandle.position, KCC.LookDirection, out var hit, 2.5f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
				{
					// When spraying on the ground, rotate it so it aligns with player view.
					var sprayOrientation = hit.normal.y > 0.9f ? KCC.TransformRotation : Quaternion.identity;
					Runner.Spawn(SprayPrefab, hit.point, sprayOrientation * Quaternion.LookRotation(-hit.normal));
				}
			}

			if (HasStateAuthority)
			{
				bool nextPressed = input.Buttons.WasPressed(_previousButtons, EInputButton.NextCharacter);
				bool prevPressed = input.Buttons.WasPressed(_previousButtons, EInputButton.PrevCharacter);

				if (nextPressed || prevPressed)
				{
					int dir = nextPressed ? 1 : -1;
					_sceneObjects.Gameplay.SwitchActiveCharacter(OwnerRef, dir);
				}
			}

			// Store input buttons when the processing is done - next tick it is compared against current input buttons.
			_previousButtons = input.Buttons;
		}

		private void MoveSurvivor(Vector3 desiredMoveVelocity = default, float jumpImpulse = default)
		{
			if (HasStateAuthority && Separation != null)
			{
				desiredMoveVelocity += Separation.GetSeparationVelocity();
			}

			float acceleration = 1f;

			if (desiredMoveVelocity == Vector3.zero)
			{
				// No desired move velocity - we are stopping.
				acceleration = KCC.IsGrounded == true ? GroundDeceleration : AirDeceleration;
			}
			else
			{
				acceleration = KCC.IsGrounded == true ? GroundAcceleration : AirAcceleration;
			}

			_moveVelocity = Vector3.Lerp(_moveVelocity, desiredMoveVelocity, acceleration * Runner.DeltaTime);
			KCC.Move(_moveVelocity, jumpImpulse);
		}

		private float GetCurrentMoveSpeed(bool isActive)
		{
			if (isActive)
				return MoveSpeed;

			if (_aiController is SurvivorNonCombatAI nonCombatAI &&
			    nonCombatAI.Assignment == ENonCombatAssignment.FollowSurvivor &&
			    nonCombatAI.FollowTarget != null &&
			    nonCombatAI.FollowTarget.IsActiveCharacter() &&
			    FlatDistanceSqr(transform.position, nonCombatAI.FollowTarget.transform.position) <= AIFollowFullSpeedRadius * AIFollowFullSpeedRadius)
			{
				return MoveSpeed;
			}

			return AIMoveSpeed;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private bool HasSeparationIntent()
		{
			return HasStateAuthority && Separation != null && Separation.HasSeparation();
		}

		private void RefreshCamera()
		{
			// Camera is set based on KCC look rotation.
			Vector2 pitchRotation = KCC.GetLookRotation(true, false);
			CameraHandle.localRotation = Quaternion.Euler(pitchRotation);
		}

		private static bool HasAnyInput(NetworkedInput input)
		{
			return input.MoveDirection != Vector2.zero || input.LookRotationDelta != Vector2.zero || input.Buttons.Bits != 0;
		}

		public bool IsActiveCharacter()
		{
			if (_sceneObjects == null || _sceneObjects.Gameplay == null)
				return false;
			if (_sceneObjects.Gameplay.PlayerData.TryGet(OwnerRef, out var data) == false)
				return false;
			return data.ActiveCharacterIndex == CharacterIndex;
		}

		private void UpdateFirstPersonVisuals()
		{
			bool shouldBeFirstPerson = HasInputAuthority && Health.IsAlive && IsActiveCharacter();
			if (shouldBeFirstPerson == _firstPersonVisualsActive)
				return;

			SetFirstPersonVisuals(shouldBeFirstPerson);
			_firstPersonVisualsActive = shouldBeFirstPerson;
		}

		private void SetFirstPersonVisuals(bool firstPerson)
		{
			FirstPersonRoot.SetActive(firstPerson);
			ThirdPersonRoot.SetActive(firstPerson == false);

			Weapons.SetFirstPersonVisuals(firstPerson);

			if (_virtualCameras != null)
			{
				for (int i = 0; i < _virtualCameras.Length; i++)
				{
					_virtualCameras[i].enabled = firstPerson;
				}
			}
		}

		private Vector3 GetAnimationMoveVelocity()
		{
			Vector3 velocity = ShouldUseKCCAnimationVelocity() ? KCC.RealVelocity : _moveVelocity;

			// We only care about X an Z directions.
			velocity.y = 0f;

			if (velocity.sqrMagnitude < AnimationMoveDeadZone * AnimationMoveDeadZone)
				return default;

			if (velocity.sqrMagnitude > 1f)
			{
				velocity.Normalize();
			}

			// Transform velocity vector to local space.
			return transform.InverseTransformVector(velocity);
		}

		private bool ShouldUseKCCAnimationVelocity()
		{
			return HasStateAuthority || (HasInputAuthority && IsActiveCharacter());
		}

		/// <summary>
		/// Source of NetworkedInput for a character. The active character's input comes from the
		/// human player via Fusion. Inactive characters receive input from an implementation of
		/// this interface, producing movement and button presses the same way the player would.
		/// </summary>
		public interface ICharacterInputSource
		{
			NetworkedInput GetInput(NetworkRunner runner);
		}
	}
}
