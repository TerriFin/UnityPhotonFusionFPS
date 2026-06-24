using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public enum ESurvivorCombatBehavior
	{
		Normal = 0,
		Aggressive = 1,
		Defensive = 2,
		None = 3,
	}

	public enum ESurvivorRetreatMode
	{
		NoRetreat = 0,
		RetreatAt25Percent = 1,
		RetreatAt50Percent = 2,
		RetreatAt75Percent = 3,
	}

	public struct SurvivorCombatAISettings
	{
		public ESurvivorWeaponPreference WeaponPreference;
		public ESurvivorCombatBehavior CombatBehavior;

		public static SurvivorCombatAISettings Default => new SurvivorCombatAISettings
		{
			WeaponPreference = ESurvivorWeaponPreference.Automatic,
			CombatBehavior = ESurvivorCombatBehavior.Normal,
		};
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorCombatAI : MonoBehaviour
	{
		[Header("Movement Look")]
		public float MaxYawDegreesPerTick = 8f;

		[Header("Zombie Combat")]
		public float ZombieRetreatDistance = 3.5f;
		public float NoneZombiePriorityDistanceMultiplier = 1.5f;
		public float NormalZombiePriorityDistanceMultiplier = 1.5f;
		public float AggressiveZombiePriorityDistanceMultiplier = 1.25f;
		public float DefensiveZombiePriorityDistanceMultiplier = 1.75f;

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
			bool movementWasEnabled = _settings.CombatBehavior != ESurvivorCombatBehavior.None;
			_settings = settings;
			EnsureBehaviorComponents();
			if (movementWasEnabled && settings.CombatBehavior == ESurvivorCombatBehavior.None)
				ClearMovementTask();
		}

		public float GetEmergencyZombieDistance()
		{
			float multiplier = _settings.CombatBehavior switch
			{
				ESurvivorCombatBehavior.Aggressive => AggressiveZombiePriorityDistanceMultiplier,
				ESurvivorCombatBehavior.Defensive => DefensiveZombiePriorityDistanceMultiplier,
				ESurvivorCombatBehavior.None => NoneZombiePriorityDistanceMultiplier,
				_ => NormalZombiePriorityDistanceMultiplier,
			};

			return Mathf.Max(0f, ZombieRetreatDistance) * Mathf.Max(0f, multiplier);
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
			bool allowMovement = true)
		{
			input = default;
			EnsureSurvivor();
			EnsureBehaviorComponents();

			if (_survivor == null || _survivor.AIShooting == null)
				return false;

			if (IsZombieTarget(enemy))
				return TryGetZombieInput(enemy, hasLineOfFire, out input, isAlreadyMoving, allowMovement);

			Vector3 moveDirection = default;
			bool hasMovement = _settings.CombatBehavior != ESurvivorCombatBehavior.None &&
			                   allowMovement &&
			                   _movement != null &&
			                   _movement.TryGetMoveDirection(_survivor, enemy, _settings.CombatBehavior, out moveDirection);

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
			bool allowMovement)
		{
			input = default;

			Vector3 moveDirection = default;
			bool hasMovement = _settings.CombatBehavior != ESurvivorCombatBehavior.None &&
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

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorRetreatAI : MonoBehaviour
	{
		private Survivor _survivor;
		private Gameplay _gameplay;

		public void Activate(Survivor survivor)
		{
			_survivor = survivor != null ? survivor : GetComponent<Survivor>();
			ResolveGameplay();
		}

		public void SetMode(ESurvivorRetreatMode mode)
		{
			if (_survivor == null || _survivor.HasStateAuthority == false)
				return;

			if (mode != ESurvivorRetreatMode.NoRetreat)
			{
				TryStartRetreat();
				return;
			}

			if (_survivor.HasRetreatAssignment)
			{
				_survivor.SetAI(SurvivorNonCombatAI.MoveTo(
					_survivor,
					_survivor.transform.position,
					_survivor.NonCombatAISettings));
			}
		}

		public void HandleDamageTaken()
		{
			if (_survivor == null || _survivor.RetreatMode == ESurvivorRetreatMode.NoRetreat)
				return;

			TryStartRetreat();
		}

		public void HandleHomeBaseChanged()
		{
			if (_survivor == null || _survivor.RetreatMode == ESurvivorRetreatMode.NoRetreat)
				return;

			TryStartRetreat();
		}

		public bool TryStartRetreat()
		{
			if (IsEligible() == false)
				return false;
			if (_gameplay.TryGetHomeBase(_survivor.OwnerRef, out Vector3 center, out float radius) == false)
				return false;
			if (IsInsideHomeBase(center, radius))
				return false;
			if (_survivor.HasRetreatAssignment &&
			    _survivor.NonCombatAI != null &&
			    _survivor.NonCombatAI.Assignment == ENonCombatAssignment.AssignedArea &&
			    FlatDistanceSqr(_survivor.NonCombatAI.AnchorPosition, center) <= 0.01f &&
			    Mathf.Abs(_survivor.NonCombatAI.AssignmentRadius - radius) <= 0.01f)
			{
				return false;
			}

			return _gameplay.SurvivorAICommands.TryApplyRetreatAssignedArea(_survivor, center, radius);
		}

		private bool IsEligible()
		{
			ResolveGameplay();
			return _survivor != null &&
			       _gameplay != null &&
			       _survivor.HasStateAuthority &&
			       CharacterFactionUtility.IsPlayerOwnedSurvivor(_survivor) &&
			       _survivor.Health != null &&
			       _survivor.Health.IsAlive &&
			       IsBelowRetreatThreshold() &&
			       _survivor.IsActiveCharacter() == false;
		}

		private bool IsBelowRetreatThreshold()
		{
			if (_survivor.Health == null || _survivor.Health.MaxHealth <= 0f)
				return false;

			float threshold = _survivor.RetreatMode switch
			{
				ESurvivorRetreatMode.RetreatAt25Percent => 0.25f,
				ESurvivorRetreatMode.RetreatAt50Percent => 0.5f,
				ESurvivorRetreatMode.RetreatAt75Percent => 0.75f,
				_ => 0f,
			};

			return threshold > 0f && _survivor.Health.CurrentHealth / _survivor.Health.MaxHealth < threshold;
		}

		private bool IsInsideHomeBase(Vector3 center, float radius)
		{
			SurvivorAssignedAreaAI assignedArea = _survivor.GetComponent<SurvivorAssignedAreaAI>();
			return assignedArea != null
				? assignedArea.IsInsideArea(_survivor, center, radius)
				: FlatDistanceSqr(_survivor.transform.position, center) <= radius * radius;
		}

		private void ResolveGameplay()
		{
			if (_gameplay != null || _survivor == null || _survivor.Runner == null)
				return;

			SceneObjects sceneObjects = _survivor.Runner.GetSingleton<SceneObjects>();
			_gameplay = sceneObjects != null ? sceneObjects.Gameplay : null;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
