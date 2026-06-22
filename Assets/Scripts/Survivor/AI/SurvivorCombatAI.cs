using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public struct SurvivorCombatAISettings
	{
		public ESurvivorWeaponPreference WeaponPreference;

		public static SurvivorCombatAISettings Default => new SurvivorCombatAISettings
		{
			WeaponPreference = ESurvivorWeaponPreference.Automatic,
		};
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorCombatAI : MonoBehaviour
	{
		[Header("Movement Look")]
		public float MaxYawDegreesPerTick = 8f;

		[Header("Zombie Combat")]
		public float ZombieRetreatDistance = 3.5f;

		private Survivor _survivor;
		private SurvivorCombatMovementAI _movement;
		private SurvivorWeaponPreferenceAI _weaponPreference;
		private SurvivorCombatAISettings _settings;

		public void Activate(Survivor survivor)
		{
			_survivor = survivor != null ? survivor : GetComponent<Survivor>();
			EnsureBehaviorComponents();

			if (_settings.Equals(default(SurvivorCombatAISettings)))
				_settings = SurvivorCombatAISettings.Default;
		}

		public void SetSettings(SurvivorCombatAISettings settings)
		{
			_settings = settings;
			EnsureBehaviorComponents();
		}

		public void ClearMovementTask()
		{
			_movement?.ClearTask(_survivor != null ? _survivor.Navigator : null);
		}

		public void NotifyTargetLost()
		{
			_movement?.NotifyTargetLost(_survivor);
		}

		public bool TryGetDirectTarget(out KnownEnemyInfo enemy, out bool hasLineOfFire)
		{
			enemy = default;
			hasLineOfFire = false;

			EnsureSurvivor();
			return _survivor != null &&
			       _survivor.AIShooting != null &&
			       _survivor.AIShooting.TryGetDirectTarget(out enemy, out hasLineOfFire);
		}

		public bool HasTargetWithLineOfFire()
		{
			return TryGetDirectTarget(out _, out bool hasLineOfFire) && hasLineOfFire;
		}

		public bool TryGetInput(
			KnownEnemyInfo enemy,
			bool hasLineOfFire,
			out NetworkedInput input,
			bool isAlreadyMoving = false,
			bool allowMovement = true,
			bool combatMovementEnabled = true)
		{
			input = default;
			EnsureSurvivor();
			EnsureBehaviorComponents();

			if (_survivor == null || _survivor.AIShooting == null)
				return false;

			if (IsZombieTarget(enemy))
				return TryGetZombieInput(enemy, hasLineOfFire, out input, isAlreadyMoving, allowMovement, combatMovementEnabled);

			Vector3 moveDirection = default;
			bool hasMovement = combatMovementEnabled &&
			                   allowMovement &&
			                   _movement != null &&
			                   _movement.TryGetMoveDirection(_survivor, enemy, out moveDirection);

			NetworkedInput shootingInput = default;
			bool hasShooting = hasLineOfFire &&
			                   _survivor.AIShooting.TryGetInput(out shootingInput, isAlreadyMoving || hasMovement);

			if (hasShooting)
			{
				input = shootingInput;
			}
			else if (hasMovement)
			{
				input.LookRotationDelta = GetMoveLookDelta(moveDirection);
			}
			else
			{
				input.LookRotationDelta = GetEnemyLookDelta(enemy);
			}

			if (hasMovement)
			{
				float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
				input.MoveDirection = GetLocalMoveDirection(moveDirection, currentYaw + input.LookRotationDelta.y);
			}

			return hasShooting || hasMovement || input.LookRotationDelta != Vector2.zero;
		}

		private bool TryGetZombieInput(
			KnownEnemyInfo enemy,
			bool hasLineOfFire,
			out NetworkedInput input,
			bool isAlreadyMoving,
			bool allowMovement,
			bool combatMovementEnabled)
		{
			input = default;

			Vector3 moveDirection = default;
			bool hasMovement = combatMovementEnabled &&
			                   allowMovement &&
			                   TryGetZombieRetreatDirection(enemy, out moveDirection);

			NetworkedInput shootingInput = default;
			bool hasShooting = hasLineOfFire &&
			                   _survivor.AIShooting.TryGetInput(out shootingInput, isAlreadyMoving || hasMovement);

			if (hasShooting)
			{
				input = shootingInput;
			}
			else
			{
				input.LookRotationDelta = GetEnemyLookDelta(enemy);
			}

			if (hasMovement)
			{
				float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
				input.MoveDirection = GetLocalMoveDirection(moveDirection, currentYaw + input.LookRotationDelta.y);
			}

			return hasShooting || hasMovement || input.LookRotationDelta != Vector2.zero;
		}

		private void Awake()
		{
			Activate(GetComponent<Survivor>());
		}

		private void EnsureSurvivor()
		{
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();
		}

		private void EnsureBehaviorComponents()
		{
			EnsureSurvivor();
			if (_survivor == null)
				return;

			if (_movement == null)
			{
				_movement = GetComponent<SurvivorCombatMovementAI>();
				if (_movement == null)
					_movement = gameObject.AddComponent<SurvivorCombatMovementAI>();
			}

			if (_weaponPreference == null)
			{
				_weaponPreference = GetComponent<SurvivorWeaponPreferenceAI>();
				if (_weaponPreference == null)
					_weaponPreference = gameObject.AddComponent<SurvivorWeaponPreferenceAI>();
				_weaponPreference.Activate(_survivor);
			}
		}

		private Vector2 GetMoveLookDelta(Vector3 moveDirection)
		{
			if (moveDirection.sqrMagnitude < 0.001f || _survivor == null)
				return default;

			float desiredYaw = Quaternion.LookRotation(moveDirection).eulerAngles.y;
			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);
			return new Vector2(0f, Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick));
		}

		private Vector2 GetEnemyLookDelta(KnownEnemyInfo enemy)
		{
			if (_survivor == null)
				return default;

			Vector3 toEnemy = GetEnemyPosition(enemy) - _survivor.transform.position;
			toEnemy.y = 0f;
			if (toEnemy.sqrMagnitude < 0.001f)
				return default;

			float desiredYaw = Quaternion.LookRotation(toEnemy).eulerAngles.y;
			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);
			return new Vector2(0f, Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick));
		}

		private bool TryGetZombieRetreatDirection(KnownEnemyInfo enemy, out Vector3 moveDirection)
		{
			moveDirection = default;
			if (_survivor == null)
				return false;

			Vector3 enemyPosition = GetEnemyPosition(enemy);
			Vector3 away = _survivor.transform.position - enemyPosition;
			away.y = 0f;

			float retreatDistance = Mathf.Max(0f, ZombieRetreatDistance);
			if (retreatDistance <= 0f || away.sqrMagnitude > retreatDistance * retreatDistance)
				return false;
			if (away.sqrMagnitude < 0.001f)
				away = -_survivor.transform.forward;

			moveDirection = away.normalized;
			return true;
		}

		private static bool IsZombieTarget(KnownEnemyInfo enemy)
		{
			return enemy.Object != null && enemy.Object.GetComponent<ZombieCharacter>() != null;
		}

		private static Vector3 GetEnemyPosition(KnownEnemyInfo enemy)
		{
			return enemy.Object != null ? enemy.Object.transform.position : enemy.LastKnownPosition;
		}

		private static Vector2 GetLocalMoveDirection(Vector3 worldDirection, float lookYaw)
		{
			Vector3 localDirection = Quaternion.Inverse(Quaternion.Euler(0f, lookYaw, 0f)) * worldDirection;
			var moveDirection = new Vector2(localDirection.x, localDirection.z);
			return moveDirection.sqrMagnitude > 1f ? moveDirection.normalized : moveDirection;
		}
	}
}
