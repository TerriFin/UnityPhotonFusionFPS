using Fusion;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	public enum EZombieAIState
	{
		Idle,
		Investigating,
		Attacking,
		Hunting,
	}

	[DisallowMultipleComponent]
	public sealed class ZombieAI : MonoBehaviour
	{
		[Header("Idle Spread")]
		public float InitialIdleWanderDelayMax = 2f;
		public float IdleWanderIntervalMin = 10f;
		public float IdleWanderIntervalMax = 14f;
		public float IdleWanderRetryDelayMin = 1f;
		public float IdleWanderRetryDelayMax = 2f;
		public float IdleWanderRadius = 10f;
		public float IdleWanderAvoidZombieRadius = 8f;
		public int IdleWanderCandidateCount = 5;

		[Header("Targeting")]
		public float AttackRetargetInterval = 2.5f;
		public float HuntingRetargetInterval = 2.5f;

		[Header("Movement")]
		public float StoppingDistance = 1.35f;
		public float DirectFallbackDistance = 0.25f;
		public float MaxYawDegreesPerTick = 7f;
		public float ReachablePointSampleDistance = 6f;

		[Header("Stuck Recovery")]
		public float StuckCheckInterval = 0.4f;
		public float StuckMinProgress = 0.2f;
		public float StuckDuration = 0.8f;
		public float StuckRecoveryStepDistance = 2.5f;
		public float StuckRecoveryDuration = 0.75f;
		public int StuckRecoveryCandidateCount = 8;

		[Header("Alerts")]
		public int MaxAlertRecipients = 8;
		public float AlertCooldown = 2f;

		public EZombieAIState State { get; private set; }
		public float LastAttackTime { get; private set; } = -1f;

		private ZombieCharacter _zombie;
		private NetworkObject _target;
		private Vector3 _investigationTarget;
		private Vector3 _lastKnownTargetPosition;
		private float _nextIdleWanderTime;
		private float _nextRetargetTime;
		private float _nextAlertTime;
		private float _nextStuckCheckTime;
		private float _stuckTime;
		private Vector3 _lastStuckCheckPosition;
		private Vector3 _stuckRecoveryTarget;
		private float _stuckRecoveryUntil;
		private int _lastStimulusTick;

		public void Activate(ZombieCharacter zombie)
		{
			_zombie = zombie != null ? zombie : GetComponent<ZombieCharacter>();
			State = EZombieAIState.Idle;
			ResetStuckTracking();
			ScheduleInitialIdleWander();
		}

		public NetworkedInput GetInput(NetworkRunner runner)
		{
			if (_zombie == null)
				Activate(GetComponent<ZombieCharacter>());
			if (_zombie == null || _zombie.Health == null || _zombie.Health.IsAlive == false)
				return default;

			if (_zombie.IsOvertime && State != EZombieAIState.Hunting)
				EnterHunting();

			switch (State)
			{
				case EZombieAIState.Hunting:
					return UpdateHunting();
				case EZombieAIState.Attacking:
					return UpdateAttacking();
				case EZombieAIState.Investigating:
					return UpdateInvestigating();
				default:
					return UpdateIdle();
			}
		}

		public void EnterHunting()
		{
			State = EZombieAIState.Hunting;
			_target = null;
			_nextRetargetTime = 0f;
			ClearNavigator();
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick, bool fromAlert)
		{
			if (_zombie == null || _zombie.IsOvertime)
				return;
			if (stimulusTick < _lastStimulusTick)
				return;

			_lastStimulusTick = stimulusTick;
			StartInvestigation(target, stimulusTick, fromAlert);
		}

		private NetworkedInput UpdateIdle()
		{
			if (TryGetDirectEnemy(out var enemy))
			{
				StartAttack(enemy);
				return UpdateAttacking();
			}

			var navigator = _zombie.Navigator;
			Vector3 position = transform.position;

			if (navigator != null && navigator.HasDestination)
			{
				navigator.Tick(position);
				if (navigator.IsDestinationReached)
				{
					navigator.ClearDestination();
					ScheduleNextIdleWander();
					ResetStuckTracking();
					return default;
				}

				return BuildMoveInput(navigator.Destination, StoppingDistance);
			}

			if (Time.timeSinceLevelLoad >= _nextIdleWanderTime)
			{
				if (TryChooseIdleWanderPoint(out Vector3 wanderPoint))
				{
					navigator?.SetDestination(wanderPoint);
					ResetStuckTracking();
					return BuildMoveInput(wanderPoint, StoppingDistance);
				}
				else
					ScheduleIdleWanderRetry();
			}

			return default;
		}

		private NetworkedInput UpdateInvestigating()
		{
			if (TryGetDirectEnemy(out var enemy))
			{
				StartAttack(enemy);
				return UpdateAttacking();
			}

			var navigator = _zombie.Navigator;
			if (navigator == null)
			{
				State = EZombieAIState.Idle;
				ScheduleNextIdleWander();
				return default;
			}

			navigator.Tick(transform.position);
			if (navigator.IsDestinationReached)
			{
				navigator.ClearDestination();
				State = EZombieAIState.Idle;
				ScheduleNextIdleWander();
				ResetStuckTracking();
				return default;
			}

			return BuildMoveInput(_investigationTarget, StoppingDistance);
		}

		private NetworkedInput UpdateAttacking()
		{
			if (IsTargetAlive(_target) == false)
			{
				if (TryGetDirectEnemy(out var enemy))
					StartAttack(enemy);
				else
					ReturnToIdle();
			}

			if (TryRefreshDirectAttackTarget() == false)
			{
				if (IsTargetAlive(_target))
				{
					StartInvestigation(_lastKnownTargetPosition, _lastStimulusTick, false);
					return UpdateInvestigating();
				}

				ReturnToIdle();
				return UpdateIdle();
			}

			if (Time.timeSinceLevelLoad >= _nextRetargetTime)
			{
				if (TryGetDirectEnemy(out var enemy))
					StartAttack(enemy);
				else if (_target != null)
					StartInvestigation(_lastKnownTargetPosition, _lastStimulusTick, false);
			}

			if (_target == null)
				return default;

			Vector3 targetPosition = _lastKnownTargetPosition;

			if (_zombie.TryAttack(_target))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				return BuildLookInput(targetPosition);
			}

			_zombie.Navigator?.SetDestination(targetPosition);
			return BuildMoveInput(targetPosition, Mathf.Max(0.1f, _zombie.Stats.AttackRange * 0.85f));
		}

		private NetworkedInput UpdateHunting()
		{
			if (Time.timeSinceLevelLoad >= _nextRetargetTime || IsTargetAlive(_target) == false)
			{
				_target = FindClosestAliveSurvivor();
				_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, HuntingRetargetInterval);
			}

			if (_target == null)
				return default;

			Vector3 targetPosition = _target.transform.position;
			if (_zombie.TryAttack(_target))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				return BuildLookInput(targetPosition);
			}

			_zombie.Navigator?.SetDestination(targetPosition);
			return BuildMoveInput(targetPosition, Mathf.Max(0.1f, _zombie.Stats.AttackRange * 0.85f));
		}

		private void StartAttack(KnownEnemyInfo enemy)
		{
			_target = enemy.Object;
			_lastKnownTargetPosition = enemy.LastKnownPosition;
			_lastStimulusTick = enemy.Tick;
			State = EZombieAIState.Attacking;
			_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
			_zombie.Navigator?.SetDestination(_lastKnownTargetPosition);
			ResetStuckTracking();
			AlertNearbyZombies(_lastKnownTargetPosition, enemy.Tick, false);
		}

		private bool TryRefreshDirectAttackTarget()
		{
			if (TryGetDirectEnemy(out var enemy) == false)
				return false;

			bool targetChanged = enemy.Object != _target;
			_target = enemy.Object;
			_lastKnownTargetPosition = enemy.LastKnownPosition;
			_lastStimulusTick = enemy.Tick;

			if (targetChanged)
			{
				_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
				AlertNearbyZombies(_lastKnownTargetPosition, enemy.Tick, false);
			}

			return true;
		}

		private void StartInvestigation(Vector3 target, int stimulusTick, bool fromAlert)
		{
			var navigator = _zombie.Navigator;
			if (navigator == null)
				return;

			if (navigator.TryFindReachablePoint(transform.position, target, ReachablePointSampleDistance, out Vector3 reachablePoint) == false)
				return;

			_investigationTarget = reachablePoint;
			_target = null;
			State = EZombieAIState.Investigating;
			navigator.SetDestination(reachablePoint);
			ResetStuckTracking();

			if (fromAlert == false)
				AlertNearbyZombies(reachablePoint, stimulusTick, true);
		}

		private bool TryGetDirectEnemy(out KnownEnemyInfo enemy)
		{
			enemy = default;
			return _zombie.Sensor != null &&
			       _zombie.Sensor.TryGetClosestDirectEnemy(out enemy) &&
			       IsTargetAlive(enemy.Object);
		}

		private bool TryChooseIdleWanderPoint(out Vector3 point)
		{
			point = default;

			Vector3 origin = transform.position;
			Vector3 awayFromZombies = GetAwayFromNearbyZombies(origin);
			float bestScore = float.MinValue;
			Vector3 bestPoint = default;
			bool hasCandidate = false;
			int candidates = Mathf.Max(1, IdleWanderCandidateCount);

			for (int i = 0; i < candidates; i++)
			{
				Vector2 random = Random.insideUnitCircle.normalized;
				Vector3 direction = new Vector3(random.x, 0f, random.y);
				if (awayFromZombies.sqrMagnitude > 0.001f)
					direction = (direction + awayFromZombies * 1.5f).normalized;

				float distance = Random.Range(IdleWanderRadius * 0.35f, Mathf.Max(0.5f, IdleWanderRadius));
				Vector3 candidate = origin + direction * distance;
				if (NavMesh.SamplePosition(candidate, out var hit, 1.5f, NavMesh.AllAreas) == false)
					continue;
				if (_zombie.Navigator == null || _zombie.Navigator.TryFindReachablePoint(origin, hit.position, ReachablePointSampleDistance, out Vector3 reachable) == false)
					continue;

				float score = ScoreWanderPoint(reachable) + Random.Range(0f, 0.25f);
				if (score <= bestScore)
					continue;

				bestScore = score;
				bestPoint = reachable;
				hasCandidate = true;
			}

			if (hasCandidate == false)
				return false;

			point = bestPoint;
			return true;
		}

		private Vector3 GetAwayFromNearbyZombies(Vector3 origin)
		{
			Vector3 away = Vector3.zero;
			float radiusSqr = IdleWanderAvoidZombieRadius * IdleWanderAvoidZombieRadius;

			for (int i = 0; i < ZombieCharacter.ActiveZombies.Count; i++)
			{
				var other = ZombieCharacter.ActiveZombies[i];
				if (other == null || other == _zombie || other.Health == null || other.Health.IsAlive == false)
					continue;

				Vector3 offset = origin - other.transform.position;
				offset.y = 0f;
				float sqrDistance = offset.sqrMagnitude;
				if (sqrDistance <= 0.001f || sqrDistance > radiusSqr)
					continue;

				away += offset.normalized * (1f - sqrDistance / radiusSqr);
			}

			return away.sqrMagnitude > 0.001f ? away.normalized : Vector3.zero;
		}

		private float ScoreWanderPoint(Vector3 point)
		{
			float minDistanceSqr = IdleWanderAvoidZombieRadius * IdleWanderAvoidZombieRadius;

			for (int i = 0; i < ZombieCharacter.ActiveZombies.Count; i++)
			{
				var other = ZombieCharacter.ActiveZombies[i];
				if (other == null || other == _zombie || other.Health == null || other.Health.IsAlive == false)
					continue;

				Vector3 offset = point - other.transform.position;
				offset.y = 0f;
				minDistanceSqr = Mathf.Min(minDistanceSqr, offset.sqrMagnitude);
			}

			return minDistanceSqr;
		}

		private void AlertNearbyZombies(Vector3 target, int stimulusTick, bool investigation)
		{
			if (_zombie.Stats.AlertRadius <= 0f || Time.timeSinceLevelLoad < _nextAlertTime)
				return;

			float radiusSqr = _zombie.Stats.AlertRadius * _zombie.Stats.AlertRadius;
			int alerted = 0;

			for (int i = 0; i < ZombieCharacter.ActiveZombies.Count; i++)
			{
				var other = ZombieCharacter.ActiveZombies[i];
				if (other == null || other == _zombie || other.Health == null || other.Health.IsAlive == false)
					continue;
				if ((other.transform.position - transform.position).sqrMagnitude > radiusSqr)
					continue;

				other.ReceiveZombieAlert(target, stimulusTick);
				alerted++;
				if (alerted >= Mathf.Max(1, MaxAlertRecipients))
					break;
			}

			_nextAlertTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, AlertCooldown);
		}

		private NetworkedInput BuildMoveInput(Vector3 destination, float stoppingDistance)
		{
			Vector3 currentPosition = transform.position;
			Vector3 steeringTarget = destination;
			var navigator = _zombie.Navigator;

			if (TryGetActiveStuckRecoveryTarget(out Vector3 recoveryTarget))
			{
				destination = recoveryTarget;
				steeringTarget = recoveryTarget;
				stoppingDistance = Mathf.Min(stoppingDistance, 0.35f);
				navigator?.SetDestination(recoveryTarget);
			}

			if (navigator != null)
			{
				navigator.Tick(currentPosition);
				if (navigator.IsDestinationReached)
				{
					if (Time.timeSinceLevelLoad < _stuckRecoveryUntil)
						ClearStuckRecovery();

					return default;
				}

				if (navigator.TryGetSteeringTarget(currentPosition, out Vector3 pathTarget))
					steeringTarget = pathTarget;
				else if (FlatDistanceSqr(currentPosition, destination) > DirectFallbackDistance * DirectFallbackDistance)
				{
					if (IsStuck(currentPosition))
						RecoverFromStuck(navigator);

					return default;
				}
			}

			Vector3 toTarget = steeringTarget - currentPosition;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude <= stoppingDistance * stoppingDistance)
				return BuildLookInput(destination);

			if (IsStuck(currentPosition))
			{
				RecoverFromStuck(navigator);
				return default;
			}

			return BuildMoveInputFromDirection(toTarget.normalized);
		}

		private NetworkedInput BuildMoveInputFromDirection(Vector3 worldDirection)
		{
			var input = BuildLookInput(transform.position + worldDirection);
			float currentYaw = GetCurrentYaw() + input.LookRotationDelta.y;
			input.MoveDirection = GetLocalMoveDirection(worldDirection, currentYaw);
			return input;
		}

		private NetworkedInput BuildLookInput(Vector3 target)
		{
			Vector3 toTarget = target - transform.position;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude < 0.001f)
				return default;

			float desiredYaw = Quaternion.LookRotation(toTarget).eulerAngles.y;
			float yawDelta = Mathf.DeltaAngle(GetCurrentYaw(), desiredYaw);
			return new NetworkedInput
			{
				LookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick)),
			};
		}

		private NetworkObject FindClosestAliveSurvivor()
		{
			NetworkObject closest = null;
			float closestDistanceSqr = float.MaxValue;
			Vector3 origin = transform.position;

			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				var survivor = sensor.Survivor;
				if (survivor == null || survivor.Health == null || survivor.Health.IsAlive == false)
					continue;

				float distanceSqr = FlatDistanceSqr(origin, survivor.transform.position);
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				closest = survivor.Object;
			}

			return closest;
		}

		private static bool IsTargetAlive(NetworkObject target)
		{
			if (target == null)
				return false;

			var survivor = target.GetComponent<Survivor>();
			return survivor != null && survivor.Health != null && survivor.Health.IsAlive;
		}

		private void ReturnToIdle()
		{
			_target = null;
			State = EZombieAIState.Idle;
			ClearNavigator();
			ScheduleNextIdleWander();
			ResetStuckTracking();
		}

		private void ClearNavigator()
		{
			_zombie?.Navigator?.ClearDestination();
		}

		private void ScheduleNextIdleWander()
		{
			float min = Mathf.Max(0.1f, IdleWanderIntervalMin);
			float max = Mathf.Max(min, IdleWanderIntervalMax);
			_nextIdleWanderTime = Time.timeSinceLevelLoad + Random.Range(min, max);
		}

		private void ScheduleInitialIdleWander()
		{
			float max = Mathf.Max(0f, InitialIdleWanderDelayMax);
			_nextIdleWanderTime = Time.timeSinceLevelLoad + Random.Range(0f, max);
		}

		private void ScheduleIdleWanderRetry()
		{
			float min = Mathf.Max(0.1f, IdleWanderRetryDelayMin);
			float max = Mathf.Max(min, IdleWanderRetryDelayMax);
			_nextIdleWanderTime = Time.timeSinceLevelLoad + Random.Range(min, max);
		}

		private bool IsStuck(Vector3 currentPosition)
		{
			if (StuckDuration <= 0f || StuckCheckInterval <= 0f)
				return false;
			if (Time.timeSinceLevelLoad < _nextStuckCheckTime)
				return false;

			float progressSqr = FlatDistanceSqr(currentPosition, _lastStuckCheckPosition);
			float minProgress = Mathf.Max(0.01f, StuckMinProgress);
			_stuckTime = progressSqr < minProgress * minProgress ? _stuckTime + StuckCheckInterval : 0f;
			_lastStuckCheckPosition = currentPosition;
			_nextStuckCheckTime = Time.timeSinceLevelLoad + StuckCheckInterval;

			return _stuckTime >= StuckDuration;
		}

		private void ResetStuckTracking()
		{
			_stuckTime = 0f;
			_lastStuckCheckPosition = transform.position;
			_nextStuckCheckTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, StuckCheckInterval);
			ClearStuckRecovery();
		}

		private void RecoverFromStuck(CharacterNavigator navigator)
		{
			ResetStuckTracking();

			if (TryChooseStuckRecoveryPoint(out Vector3 recoveryPoint))
			{
				_stuckRecoveryTarget = recoveryPoint;
				_stuckRecoveryUntil = Time.timeSinceLevelLoad + Mathf.Max(0.1f, StuckRecoveryDuration);
				navigator?.SetDestination(recoveryPoint);
				return;
			}

			navigator?.ForceRepath();

			if (State == EZombieAIState.Idle || State == EZombieAIState.Investigating)
			{
				navigator?.ClearDestination();
				ScheduleIdleWanderRetry();
			}
		}

		private bool TryGetActiveStuckRecoveryTarget(out Vector3 target)
		{
			target = default;
			if (Time.timeSinceLevelLoad >= _stuckRecoveryUntil)
				return false;

			target = _stuckRecoveryTarget;
			return true;
		}

		private void ClearStuckRecovery()
		{
			_stuckRecoveryUntil = 0f;
			_stuckRecoveryTarget = default;
		}

		private bool TryChooseStuckRecoveryPoint(out Vector3 point)
		{
			point = default;
			var navigator = _zombie != null ? _zombie.Navigator : null;
			if (navigator == null || StuckRecoveryStepDistance <= 0f)
				return false;

			Vector3 origin = transform.position;
			Vector3 awayFromZombies = GetAwayFromNearbyZombies(origin);
			Vector3 back = -transform.forward;
			back.y = 0f;
			if (back.sqrMagnitude < 0.001f)
				back = Random.insideUnitSphere;
			back.Normalize();

			int candidates = Mathf.Max(1, StuckRecoveryCandidateCount);
			float stepDistance = Mathf.Max(0.25f, StuckRecoveryStepDistance);
			for (int i = 0; i < candidates; i++)
			{
				float angle = i == 0 ? 0f : (360f / candidates) * i + Random.Range(-15f, 15f);
				Vector3 direction = Quaternion.Euler(0f, angle, 0f) * back;
				if (awayFromZombies.sqrMagnitude > 0.001f)
					direction = (direction + awayFromZombies).normalized;

				Vector3 candidate = origin + direction * stepDistance;
				if (navigator.TryFindReachablePoint(origin, candidate, Mathf.Max(1f, stepDistance), out Vector3 reachable) == false)
					continue;

				point = reachable;
				return true;
			}

			return false;
		}

		private float GetCurrentYaw()
		{
			return _zombie != null && _zombie.KCC != null
				? _zombie.KCC.GetLookRotation(false, true).y
				: transform.eulerAngles.y;
		}

		private static Vector2 GetLocalMoveDirection(Vector3 worldDirection, float lookYaw)
		{
			Vector3 localDirection = Quaternion.Inverse(Quaternion.Euler(0f, lookYaw, 0f)) * worldDirection;
			var moveDirection = new Vector2(localDirection.x, localDirection.z);
			return moveDirection.sqrMagnitude > 1f ? moveDirection.normalized : moveDirection;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
