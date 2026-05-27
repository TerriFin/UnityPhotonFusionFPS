using Fusion;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Serialization;

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
		public float HuntingRetargetInterval = 5f;
		public float HuntingRetargetIntervalJitter = 1f;
		public float HuntingInitialRetargetStaggerMax = 3f;

		[Header("Movement")]
		public float StoppingDistance = 1.35f;
		[FormerlySerializedAs("DirectFallbackDistance")]
		public float DirectMoveDistance = 0.25f;
		public float MaxYawDegreesPerTick = 7f;
		public float ReachablePointSampleDistance = 6f;
		public float AttackMoveStoppingDistance = 0.2f;
		public float AttackMoveTargetMaxDistanceFromTarget = 1.5f;
		public float AttackMoveTargetMaxHeightDifference = 0.75f;
		public float AttackDestinationRefreshInterval = 0.5f;

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
		private Vector3 _attackMoveTarget;
		private float _nextAttackMoveTargetRefreshTime;
		private bool _hasAttackMoveTarget;
		private int _lastStimulusTick;

		public void Activate(ZombieCharacter zombie)
		{
			_zombie = zombie != null ? zombie : GetComponent<ZombieCharacter>();
			State = EZombieAIState.Idle;
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
			_nextRetargetTime = Time.timeSinceLevelLoad + Random.Range(0f, Mathf.Max(0f, HuntingInitialRetargetStaggerMax));
			ClearAttackMoveTarget();
			ClearNavigator();
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick, bool fromAlert)
		{
			if (_zombie == null || _zombie.IsOvertime)
				return;
			if (stimulusTick < _lastStimulusTick)
				return;

			_lastStimulusTick = stimulusTick;
			ClearAttackMoveTarget();
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
					return default;
				}

				return BuildMoveInput(navigator.Destination, StoppingDistance, true);
			}

			if (Time.timeSinceLevelLoad >= _nextIdleWanderTime)
			{
				if (TryChooseIdleWanderPoint(out Vector3 wanderPoint))
				{
					navigator?.SetDestination(wanderPoint);
					return BuildMoveInput(wanderPoint, StoppingDistance, true);
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
				return default;
			}

			return BuildMoveInput(_investigationTarget, StoppingDistance, true);
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

			if (IsCachedAttackMoveTargetReached())
			{
				if (IsTargetInsideAttackRange(_target))
					return BuildLookInput(targetPosition);

				ClearAttackMoveTarget();
				ClearNavigator();

				if (TryRefreshDirectAttackTarget())
					targetPosition = _lastKnownTargetPosition;
				else
				{
					ReturnToIdle();
					return UpdateIdle();
				}
			}

			if (TryGetAttackMoveTarget(targetPosition, out Vector3 moveTarget))
				return BuildAttackMoveInput(moveTarget);

			return BuildLookInput(targetPosition);
		}

		private NetworkedInput UpdateHunting()
		{
			bool targetAlive = IsTargetAlive(_target);
			if (_target == null)
			{
				if (Time.timeSinceLevelLoad < _nextRetargetTime)
					return default;

				RefreshHuntingTarget();
			}
			else if (targetAlive == false || Time.timeSinceLevelLoad >= _nextRetargetTime)
			{
				RefreshHuntingTarget();
			}

			if (_target == null)
				return default;

			Vector3 targetPosition = _target.transform.position;
			if (_zombie.TryAttack(_target))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				return BuildLookInput(targetPosition);
			}

			if (IsCachedAttackMoveTargetReached())
			{
				if (IsTargetInsideAttackRange(_target))
					return BuildLookInput(targetPosition);

				ClearAttackMoveTarget();
				ClearNavigator();

				RefreshHuntingTarget();
				if (_target == null)
					return default;

				targetPosition = _target.transform.position;
			}

			if (TryGetAttackMoveTarget(targetPosition, out Vector3 moveTarget))
				return BuildAttackMoveInput(moveTarget);

			return BuildLookInput(targetPosition);
		}

		private void RefreshHuntingTarget()
		{
			NetworkObject previousTarget = _target;
			_target = FindClosestAliveSurvivor();
			if (_target != previousTarget)
				ClearAttackMoveTarget();

			ScheduleNextHuntingRetarget();
		}

		private void StartAttack(KnownEnemyInfo enemy)
		{
			_target = enemy.Object;
			_lastKnownTargetPosition = enemy.LastKnownPosition;
			_lastStimulusTick = enemy.Tick;
			State = EZombieAIState.Attacking;
			_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
			ClearAttackMoveTarget();
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
				ClearAttackMoveTarget();
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
			ClearAttackMoveTarget();
			State = EZombieAIState.Investigating;
			navigator.SetDestination(reachablePoint);

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

		private bool TryGetAttackMoveTarget(Vector3 targetPosition, out Vector3 moveTarget)
		{
			moveTarget = default;

			float now = Time.timeSinceLevelLoad;
			if (_hasAttackMoveTarget && now < _nextAttackMoveTargetRefreshTime)
			{
				moveTarget = _attackMoveTarget;
				return true;
			}

			_nextAttackMoveTargetRefreshTime = now + Mathf.Max(0.05f, AttackDestinationRefreshInterval);

			var navigator = _zombie != null ? _zombie.Navigator : null;
			if (navigator != null &&
			    navigator.TryFindReachablePoint(transform.position, targetPosition, ReachablePointSampleDistance, out Vector3 reachablePoint))
			{
				if (IsUsefulAttackMoveTarget(reachablePoint, targetPosition) == false)
				{
					_hasAttackMoveTarget = false;
					navigator.ClearDestination();
					return false;
				}

				_attackMoveTarget = reachablePoint;
				_hasAttackMoveTarget = true;
				navigator.SetDestination(reachablePoint);
				moveTarget = reachablePoint;
				return true;
			}

			_hasAttackMoveTarget = false;
			navigator?.ClearDestination();
			return false;
		}

		private bool IsUsefulAttackMoveTarget(Vector3 moveTarget, Vector3 targetPosition)
		{
			float maxDistance = Mathf.Max(_zombie != null ? _zombie.Stats.AttackRange : 0f, AttackMoveTargetMaxDistanceFromTarget);
			if (FlatDistanceSqr(moveTarget, targetPosition) > maxDistance * maxDistance)
				return false;

			return Mathf.Abs(moveTarget.y - targetPosition.y) <= Mathf.Max(0f, AttackMoveTargetMaxHeightDifference);
		}

		private bool IsCachedAttackMoveTargetReached()
		{
			if (_hasAttackMoveTarget == false)
				return false;

			var navigator = _zombie != null ? _zombie.Navigator : null;
			if (navigator == null)
				return FlatDistanceSqr(transform.position, _attackMoveTarget) <= StoppingDistance * StoppingDistance;

			navigator.Tick(transform.position);
			return navigator.IsDestinationReached;
		}

		private bool IsTargetInsideAttackRange(NetworkObject target)
		{
			if (IsTargetAlive(target) == false || _zombie == null)
				return false;

			float range = Mathf.Max(0.1f, _zombie.Stats.AttackRange);
			return FlatDistanceSqr(transform.position, target.transform.position) <= range * range;
		}

		private void ClearAttackMoveTarget()
		{
			_hasAttackMoveTarget = false;
			_attackMoveTarget = default;
			_nextAttackMoveTargetRefreshTime = 0f;
		}

		private NetworkedInput BuildAttackMoveInput(Vector3 destination)
		{
			var input = BuildMoveInput(destination, Mathf.Max(0.05f, AttackMoveStoppingDistance), false);
			if (input.MoveDirection != Vector2.zero)
				return input;

			ClearAttackMoveTarget();
			ClearNavigator();
			return default;
		}

		private NetworkedInput BuildMoveInput(Vector3 destination, float stoppingDistance, bool allowDirectFallback)
		{
			Vector3 currentPosition = transform.position;
			Vector3 steeringTarget = destination;
			var navigator = _zombie.Navigator;

			if (navigator != null)
			{
				navigator.Tick(currentPosition);
				if (navigator.IsDestinationReached)
					return default;

				if (navigator.TryGetSteeringTarget(currentPosition, out Vector3 pathTarget))
					steeringTarget = pathTarget;
				else if (allowDirectFallback == false || FlatDistanceSqr(currentPosition, destination) > DirectMoveDistance * DirectMoveDistance)
					return default;
			}

			Vector3 toTarget = steeringTarget - currentPosition;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude <= stoppingDistance * stoppingDistance)
				return BuildLookInput(destination);

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
			ClearAttackMoveTarget();
			ClearNavigator();
			ScheduleNextIdleWander();
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

		private void ScheduleNextHuntingRetarget()
		{
			float interval = Mathf.Max(0.1f, HuntingRetargetInterval);
			float jitter = Mathf.Max(0f, HuntingRetargetIntervalJitter);
			_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, interval + Random.Range(-jitter, jitter));
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
