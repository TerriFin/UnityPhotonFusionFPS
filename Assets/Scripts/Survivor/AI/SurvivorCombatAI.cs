using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public struct SurvivorCombatAISettings
	{
		public bool CombatMovementEnabled;

		public static SurvivorCombatAISettings Default => new SurvivorCombatAISettings
		{
			CombatMovementEnabled = true,
		};

		public static SurvivorCombatAISettings Passive => new SurvivorCombatAISettings
		{
			CombatMovementEnabled = false,
		};
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorCombatAI : MonoBehaviour
	{
		[Header("Movement Look")]
		public float MaxYawDegreesPerTick = 8f;

		private Survivor _survivor;
		private SurvivorCombatMovementAI _movement;
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

			if (_settings.CombatMovementEnabled == false)
				ClearMovementTask();
		}

		public void ClearMovementTask()
		{
			_movement?.ClearTask(_survivor != null ? _survivor.Navigator : null);
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

		public bool TryGetInput(KnownEnemyInfo enemy, bool hasLineOfFire, out NetworkedInput input, bool isAlreadyMoving = false, bool allowMovement = true)
		{
			input = default;
			EnsureSurvivor();
			EnsureBehaviorComponents();

			if (_survivor == null || _survivor.AIShooting == null)
				return false;

			Vector3 moveDirection = default;
			bool hasMovement = allowMovement &&
			                   _settings.CombatMovementEnabled &&
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

			if (hasMovement)
			{
				float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
				input.MoveDirection = GetLocalMoveDirection(moveDirection, currentYaw + input.LookRotationDelta.y);
			}

			return hasShooting || hasMovement;
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

		private static Vector2 GetLocalMoveDirection(Vector3 worldDirection, float lookYaw)
		{
			Vector3 localDirection = Quaternion.Inverse(Quaternion.Euler(0f, lookYaw, 0f)) * worldDirection;
			var moveDirection = new Vector2(localDirection.x, localDirection.z);
			return moveDirection.sqrMagnitude > 1f ? moveDirection.normalized : moveDirection;
		}
	}
}
