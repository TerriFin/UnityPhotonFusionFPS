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
		public EWeaponType[] WeaponPriority =
		{
			EWeaponType.Rifle,
			EWeaponType.Shotgun,
			EWeaponType.Pistol,
		};

		[Header("Timing")]
		public float FirstShotDelayMin = 0.5f;
		public float FirstShotDelayMax = 1.4f;
		public float MovingFirstShotDelayMultiplier = 1.5f;
		public float FollowupShotDelayMin = 0.6f;
		public float FollowupShotDelayMax = 1.8f;
		public float TriggerHoldDuration = 0.35f;

		[Header("Accuracy")]
		[FormerlySerializedAs("AimErrorDegrees")]
		public float HorizontalAimErrorDegrees = 8f;
		public float VerticalAimErrorDegrees = 4f;
		public float AimTargetHeight = 1.4f;
		public float AimErrorRefreshInterval = 0.8f;
		public float FireAlignmentAngle = 10f;
		public float MaxYawDegreesPerTick = 8f;
		public float MaxPitchDegreesPerTick = 6f;

		private Survivor _survivor;
		private NetworkObject _currentTarget;
		private Vector3 _aimDirectionOffset;
		private float _nextShotTime;
		private float _nextAimErrorRefreshTime;
		private float _triggerReleaseTime;

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

			if (TryGetWeaponSwitchButton(out var weaponButton))
			{
				input.Buttons.Set(weaponButton, true);
				return true;
			}

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

		private bool TryGetWeaponSwitchButton(out EInputButton button)
		{
			button = default;

			var weapons = _survivor != null ? _survivor.Weapons : null;
			if (weapons == null || weapons.IsSwitching)
				return false;
			if (weapons.TryGetBestUsableWeapon(WeaponPriority, out var bestWeapon) == false)
				return false;
			if (bestWeapon == weapons.CurrentWeapon)
				return false;

			return TryGetWeaponButton(bestWeapon.Type, out button);
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
			if (_survivor.Sensor.TryGetClosestDirectEnemy(out enemy) == false)
				return false;
			if (IsDeadSurvivor(enemy.Object))
				return false;

			hasLineOfFire = HasLineOfFire(enemy);
			return true;
		}

		private void Awake()
		{
			_survivor = GetComponent<Survivor>();
		}

		private static bool IsDeadSurvivor(NetworkObject target)
		{
			if (target == null)
				return false;

			var survivor = target.GetComponent<Survivor>();
			return survivor != null && (survivor.Health == null || survivor.Health.IsAlive == false);
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
			return GetAimTargetPosition(enemy) - GetAimOrigin() + _aimDirectionOffset;
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
			Vector3 toTarget = GetAimTargetPosition(enemy) - GetAimOrigin();

			if (toTarget.sqrMagnitude < 0.001f || (HorizontalAimErrorDegrees <= 0f && VerticalAimErrorDegrees <= 0f))
			{
				_aimDirectionOffset = Vector3.zero;
			}
			else
			{
				float yawError = Random.Range(-HorizontalAimErrorDegrees, HorizontalAimErrorDegrees);
				float pitchError = Random.Range(-VerticalAimErrorDegrees, VerticalAimErrorDegrees);
				Vector3 right = Vector3.Cross(Vector3.up, toTarget.normalized);
				if (right.sqrMagnitude < 0.001f)
					right = transform.right;

				Vector3 badDirection = Quaternion.AngleAxis(yawError, Vector3.up) *
				                       Quaternion.AngleAxis(pitchError, right.normalized) *
				                       toTarget.normalized;
				_aimDirectionOffset = badDirection * toTarget.magnitude - toTarget;
			}

			_nextAimErrorRefreshTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, AimErrorRefreshInterval);
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
			_aimDirectionOffset = Vector3.zero;
			_nextShotTime = 0f;
			_nextAimErrorRefreshTime = 0f;
			_triggerReleaseTime = 0f;
		}
	}
}
