using Fusion;
using UnityEngine;
using UnityEngine.Serialization;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorAIShooting : MonoBehaviour
	{
		[Header("State")]
		public bool AutoShootEnabled = true;

		[Header("Timing")]
		public float FirstShotDelayMin = 0.5f;
		public float FirstShotDelayMax = 1.4f;
		public float MovingFirstShotDelayMultiplier = 1.5f;
		public float FollowupShotDelayMin = 0.6f;
		public float FollowupShotDelayMax = 1.8f;
		public float TriggerHoldDuration = 0.35f;

		[Header("Accuracy - Survivors")]
		[FormerlySerializedAs("AimErrorDegrees")]
		public float HorizontalAimErrorDegrees = 8f;
		public float VerticalAimErrorDegrees = 4f;

		[Header("Accuracy - Zombies")]
		public float ZombieHorizontalAimErrorDegrees = 3f;
		public float ZombieVerticalAimErrorDegrees = 1.5f;

		[Header("Aiming")]
		public float AimTargetHeight = 1.4f;
		public float AimErrorRefreshInterval = 0.8f;
		public float FireAlignmentAngle = 10f;
		public float MaxYawDegreesPerTick = 8f;
		public float MaxPitchDegreesPerTick = 6f;

		[Header("Targeting")]
		[Tooltip("Stay on the current target unless another enemy is at least this many metres closer. Prevents the " +
		         "first-shot timer from resetting every sensor tick when a pack surrounds the survivor and the closest " +
		         "enemy flickers between several near-equal candidates — which otherwise leaves it never firing.")]
		public float TargetSwitchHysteresis = 1.5f;

		private Survivor _survivor;
		private NetworkObject _currentTarget;
		private float _aimYawError;
		private float _aimPitchError;
		private float _nextShotTime;
		private float _nextAimErrorRefreshTime;
		private float _triggerReleaseTime;
		private EInputButton _lastWeaponSwitchButton;
		private bool _weaponSwitchButtonHeld;
		private readonly System.Collections.Generic.List<KnownEnemyInfo> _directTargets = new(8);

		private enum EWeaponSwitchInputState
		{
			None,
			Release,
			Press,
		}

		public void SetAutoShootEnabled(bool enabled)
		{
			if (AutoShootEnabled == enabled)
				return;

			AutoShootEnabled = enabled;
			ResetTargetState();
		}

		public void ToggleAutoShoot()
		{
			SetAutoShootEnabled(AutoShootEnabled == false);
		}

		public bool TryGetInput(out NetworkedInput input, bool isMoving = false)
		{
			input = default;

			if (TryGetDirectTarget(out var enemy, out bool hasLineOfFire) == false)
			{
				ResetTargetState();
				return false;
			}

			UpdateTargetState(enemy, isMoving);
			if (hasLineOfFire == false)
			{
				ResetTargetState();
				return false;
			}

			Vector3 toTarget = GetAimDirection(enemy);
			if (toTarget.sqrMagnitude < 0.001f)
				return false;

			Vector3 aimDirection = toTarget.normalized;
			float desiredYaw = Quaternion.LookRotation(aimDirection).eulerAngles.y;
			float desiredPitch = -Mathf.Asin(Mathf.Clamp(aimDirection.y, -1f, 1f)) * Mathf.Rad2Deg;
			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float currentPitch = _survivor.KCC.GetLookRotation(true, false).x;
			float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);
			float pitchDelta = Mathf.DeltaAngle(currentPitch, desiredPitch);

			input.LookRotationDelta = new Vector2(
				Mathf.Clamp(pitchDelta, -MaxPitchDegreesPerTick, MaxPitchDegreesPerTick),
				Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick));

			EWeaponSwitchInputState weaponSwitchInput = GetWeaponSwitchInput(enemy, out var weaponButton);
			if (weaponSwitchInput == EWeaponSwitchInputState.Press)
			{
				input.Buttons.Set(weaponButton, true);
				return true;
			}
			if (weaponSwitchInput == EWeaponSwitchInputState.Release)
				return true;

			if (AutoShootEnabled && Time.timeSinceLevelLoad < _triggerReleaseTime)
			{
				input.Buttons.Set(EInputButton.Fire, true);
			}
			else if (AutoShootEnabled && IsAligned(yawDelta, pitchDelta) && Time.timeSinceLevelLoad >= _nextShotTime)
			{
				StartBurst();
				input.Buttons.Set(EInputButton.Fire, true);
			}

			return true;
		}

		private EWeaponSwitchInputState GetWeaponSwitchInput(KnownEnemyInfo enemy, out EInputButton button)
		{
			button = default;

			var weapons = _survivor != null ? _survivor.Weapons : null;
			if (weapons == null || weapons.IsSwitching)
			{
				_weaponSwitchButtonHeld = false;
				return EWeaponSwitchInputState.None;
			}

			SurvivorWeaponPreferenceAI preferenceAI = _survivor.WeaponPreferenceAI;
			if (preferenceAI == null ||
			    preferenceAI.TryGetDesiredWeapon(enemy, _survivor.CombatAISettings.WeaponPreference, out var bestWeapon) == false)
			{
				_weaponSwitchButtonHeld = false;
				return EWeaponSwitchInputState.None;
			}
			if (bestWeapon == weapons.CurrentWeapon)
			{
				_weaponSwitchButtonHeld = false;
				return EWeaponSwitchInputState.None;
			}

			if (TryGetWeaponButton(bestWeapon.Type, out button) == false)
			{
				_weaponSwitchButtonHeld = false;
				return EWeaponSwitchInputState.None;
			}

			if (_weaponSwitchButtonHeld && button == _lastWeaponSwitchButton)
			{
				_weaponSwitchButtonHeld = false;
				return EWeaponSwitchInputState.Release;
			}

			_lastWeaponSwitchButton = button;
			_weaponSwitchButtonHeld = true;
			return EWeaponSwitchInputState.Press;
		}

		private static bool TryGetWeaponButton(EWeaponType weaponType, out EInputButton button)
		{
			switch (weaponType)
			{
				case EWeaponType.Pistol:
					button = EInputButton.Pistol;
					return true;
				case EWeaponType.Rifle:
					button = EInputButton.Rifle;
					return true;
				case EWeaponType.Shotgun:
					button = EInputButton.Shotgun;
					return true;
				default:
					button = default;
					return false;
			}
		}

		public bool TryGetDirectTarget(out KnownEnemyInfo enemy, out bool hasLineOfFire)
		{
			enemy = default;
			hasLineOfFire = false;

			if (_survivor == null || _survivor.Sensor == null)
				return false;

			_directTargets.Clear();
			_survivor.Sensor.GetDirectKnownEnemies(_directTargets);

			float closestDistanceSqr = float.MaxValue;
			KnownEnemyInfo closest = default;
			float currentDistanceSqr = float.MaxValue;
			KnownEnemyInfo current = default;
			bool hasCurrent = false;

			Vector3 origin = transform.position;
			for (int i = 0; i < _directTargets.Count; i++)
			{
				var candidate = _directTargets[i];
				if (IsDeadTarget(candidate.Object))
					continue;
				if (CharacterFactionUtility.CanSurvivorAutoAttack(_survivor, candidate.Object) == false)
					continue;

				float distanceSqr = (candidate.LastKnownPosition - origin).sqrMagnitude;
				if (distanceSqr < closestDistanceSqr)
				{
					closestDistanceSqr = distanceSqr;
					closest = candidate;
				}

				if (_currentTarget != null && candidate.Object == _currentTarget)
				{
					currentDistanceSqr = distanceSqr;
					current = candidate;
					hasCurrent = true;
				}
			}

			_directTargets.Clear();
			if (closestDistanceSqr == float.MaxValue)
				return false;

			// Target stickiness: keep the still-valid current target unless a different enemy is decisively closer.
			// Without this, a surrounding pack makes the "closest" enemy flip between several near-equal candidates
			// every sensor tick; each flip resets the first-shot delay in UpdateTargetState, so the survivor stays
			// perpetually "about to fire" and never actually shoots.
			enemy = closest;
			if (hasCurrent && current.Object != closest.Object)
			{
				float closestDistance = Mathf.Sqrt(closestDistanceSqr);
				float currentDistance = Mathf.Sqrt(currentDistanceSqr);
				if (currentDistance - closestDistance <= Mathf.Max(0f, TargetSwitchHysteresis))
					enemy = current;
			}

			hasLineOfFire = HasLineOfFire(enemy);
			return true;
		}

		private void Awake()
		{
			_survivor = GetComponent<Survivor>();
		}

		private static bool IsDeadTarget(NetworkObject target)
		{
			if (target == null)
				return false;

			var survivor = target.GetComponent<Survivor>();
			if (survivor != null)
				return survivor.Health == null || survivor.Health.IsAlive == false;

			var zombie = target.GetComponent<ZombieCharacter>();
			return zombie != null && (zombie.Health == null || zombie.Health.IsAlive == false);
		}

		private void UpdateTargetState(KnownEnemyInfo enemy, bool isMoving)
		{
			if (_currentTarget != enemy.Object)
			{
				_currentTarget = enemy.Object;
				ScheduleFirstShot(isMoving);
				RefreshAimError(enemy);
				return;
			}

			if (Time.timeSinceLevelLoad >= _nextAimErrorRefreshTime)
			{
				RefreshAimError(enemy);
			}
		}

		private Vector3 GetAimDirection(KnownEnemyInfo enemy)
		{
			Vector3 toTarget = GetAimTargetPosition(enemy) - GetAimOrigin();
			if (toTarget.sqrMagnitude < 0.001f)
				return toTarget;

			if (_aimYawError == 0f && _aimPitchError == 0f)
				return toTarget;

			Vector3 direction = toTarget.normalized;
			Vector3 right = Vector3.Cross(Vector3.up, direction);
			if (right.sqrMagnitude < 0.001f)
				right = transform.right;

			Vector3 badDirection = Quaternion.AngleAxis(_aimYawError, Vector3.up) *
			                       Quaternion.AngleAxis(_aimPitchError, right.normalized) *
			                       direction;
			return badDirection * toTarget.magnitude;
		}

		private Vector3 GetAimTargetPosition(KnownEnemyInfo enemy)
		{
			Vector3 targetPosition = enemy.LastKnownPosition;
			if (enemy.Object != null)
			{
				targetPosition = enemy.Object.transform.position;
			}

			targetPosition.y += AimTargetHeight;
			return targetPosition;
		}

		private void RefreshAimError(KnownEnemyInfo enemy)
		{
			GetAimErrorDegrees(enemy, out float horizontalAimError, out float verticalAimError);
			_aimYawError = horizontalAimError > 0f ? Random.Range(-horizontalAimError, horizontalAimError) : 0f;
			_aimPitchError = verticalAimError > 0f ? Random.Range(-verticalAimError, verticalAimError) : 0f;
			_nextAimErrorRefreshTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, AimErrorRefreshInterval);
		}

		private void GetAimErrorDegrees(KnownEnemyInfo enemy, out float horizontalAimError, out float verticalAimError)
		{
			if (IsZombieTarget(enemy.Object))
			{
				horizontalAimError = ZombieHorizontalAimErrorDegrees;
				verticalAimError = ZombieVerticalAimErrorDegrees;
				return;
			}

			horizontalAimError = HorizontalAimErrorDegrees;
			verticalAimError = VerticalAimErrorDegrees;
		}

		private bool HasLineOfFire(KnownEnemyInfo enemy)
		{
			if (enemy.Object == null)
				return false;

			var sensor = _survivor != null ? _survivor.Sensor : null;
			if (sensor == null || sensor.VisionBlockers.value == 0)
				return true;

			Vector3 origin = GetAimOrigin();
			Vector3 target = GetAimTargetPosition(enemy);
			if ((target - origin).sqrMagnitude < 0.001f)
				return false;

			return Physics.Linecast(origin, target, sensor.VisionBlockers, QueryTriggerInteraction.Ignore) == false;
		}

		private void ScheduleNextShot(float minDelay, float maxDelay)
		{
			float min = Mathf.Max(0f, minDelay);
			float max = Mathf.Max(min, maxDelay);
			_nextShotTime = Time.timeSinceLevelLoad + Random.Range(min, max);
		}

		private void ScheduleFirstShot(bool isMoving)
		{
			float multiplier = isMoving ? Mathf.Max(1f, MovingFirstShotDelayMultiplier) : 1f;
			ScheduleNextShot(FirstShotDelayMin * multiplier, FirstShotDelayMax * multiplier);
		}

		private void StartBurst()
		{
			_triggerReleaseTime = Time.timeSinceLevelLoad + Mathf.Max(0.02f, TriggerHoldDuration);
			ScheduleNextShot(FollowupShotDelayMin, FollowupShotDelayMax);
			_nextShotTime = Mathf.Max(_nextShotTime, _triggerReleaseTime);
		}

		private bool IsAligned(float yawDelta, float pitchDelta)
		{
			return Mathf.Abs(yawDelta) <= FireAlignmentAngle && Mathf.Abs(pitchDelta) <= FireAlignmentAngle;
		}

		private Vector3 GetAimOrigin()
		{
			return _survivor != null && _survivor.Weapons != null && _survivor.Weapons.FireTransform != null
				? _survivor.Weapons.FireTransform.position
				: transform.position + Vector3.up * AimTargetHeight;
		}

		private void ResetTargetState()
		{
			_currentTarget = null;
			_aimYawError = 0f;
			_aimPitchError = 0f;
			_nextShotTime = 0f;
			_nextAimErrorRefreshTime = 0f;
			_triggerReleaseTime = 0f;
			_weaponSwitchButtonHeld = false;
		}

		private static bool IsZombieTarget(NetworkObject target)
		{
			return target != null && target.GetComponent<ZombieCharacter>() != null;
		}
	}
}
