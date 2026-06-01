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

		[Header("Climbing")]
		public bool CanClimbUnreachableTargets = true;
		public float ClimbSpeedMultiplier = 0.5f;
		public float ClimbApproachMaxDistanceFromTarget = 4f;
		public float ClimbStartDistance = 1.25f;
		public float ClimbMinHeightDifference = 0.45f;
		public float ClimbMaxHeightDifference = 2.5f;
		public float ClimbMantleSnapDistance = 2.5f;
		public float ClimbMantleHeightTolerance = 1.25f;
		public float ClimbMantleForwardDistance = 0.9f;
		public float ClimbMantleProbeHeight = 1.5f;
		public float ClimbMantleProbeDistance = 3f;
		public float ClimbMantleMinSurfaceNormalY = 0.55f;

		[Header("Alerts")]
		public int MaxAlertRecipients = 8;
		public float AlertCooldown = 2f;

		[Header("Stuck Recovery")]
		public float StuckSampleRadius = 0.6f;
		public float StuckMinHeightAboveNavMesh = 0.6f;
		public float StuckCheckInterval = 0.5f;
		public float StuckRandomWanderDurationMin = 1f;
		public float StuckRandomWanderDurationMax = 2.5f;

		[Header("Ledge Drop")]
		public bool LedgeDropEnabled = true;
		public float LedgeDropMinHeightDrop = 1f;
		public float LedgeDropProbeDistance = 4f;
		public float LedgeDropStepSize = 0.75f;
		public float LedgeDropMaxFallHeight = 8f;

		public EZombieAIState State { get; private set; }
		public float LastAttackTime { get; private set; } = -1f;
		public bool WantsToClimb { get; private set; }

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
		private Vector3 _climbApproachTarget;
		private bool _hasClimbApproachTarget;
		private int _lastStimulusTick;
		private float _nextStuckCheckTime;
		private bool _isStuckElevated;
		private Vector3 _stuckWanderDirection;
		private float _stuckWanderEndTime;

		public void Activate(ZombieCharacter zombie)
		{
			_zombie = zombie != null ? zombie : GetComponent<ZombieCharacter>();
			State = EZombieAIState.Idle;
			ScheduleInitialIdleWander();
		}

		public NetworkedInput GetInput(NetworkRunner runner)
		{
			WantsToClimb = false;

			if (_zombie == null)
				Activate(GetComponent<ZombieCharacter>());
			if (_zombie == null || _zombie.Health == null || _zombie.Health.IsAlive == false)
				return default;

			if (_zombie.IsOvertime && State != EZombieAIState.Hunting)
				EnterHunting();

			if (State == EZombieAIState.Idle && TryGetStuckRandomMoveInput(out NetworkedInput stuckInput))
				return stuckInput;

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

		private bool TryGetStuckRandomMoveInput(out NetworkedInput input)
		{
			input = default;

			float now = Time.timeSinceLevelLoad;

			if (now >= _nextStuckCheckTime)
			{
				_nextStuckCheckTime = now + Mathf.Max(0.1f, StuckCheckInterval);
				_isStuckElevated = IsElevatedOffNavMesh();
			}

			if (_isStuckElevated == false)
				return false;

			if (now >= _stuckWanderEndTime)
			{
				Vector2 random = Random.insideUnitCircle.normalized;
				_stuckWanderDirection = new Vector3(random.x, 0f, random.y);

				float min = Mathf.Max(0.1f, StuckRandomWanderDurationMin);
				float max = Mathf.Max(min, StuckRandomWanderDurationMax);
				_stuckWanderEndTime = now + Random.Range(min, max);
			}

			ClearAttackMoveTarget();
			ClearNavigator();

			input = BuildMoveInputFromDirection(_stuckWanderDirection);
			return input.MoveDirection != Vector2.zero;
		}

		private bool IsElevatedOffNavMesh()
		{
			Vector3 position = transform.position;
			float radius = Mathf.Max(0.1f, StuckSampleRadius);

			if (NavMesh.SamplePosition(position, out var hit, radius, NavMesh.AllAreas) == false)
				return true;

			return (position.y - hit.position.y) > Mathf.Max(0.1f, StuckMinHeightAboveNavMesh);
		}

		private bool TryBuildOffNavMeshDirectMoveInput(Vector3 targetPosition, out NetworkedInput input)
		{
			input = default;

			if (IsElevatedOffNavMesh() == false)
				return false;

			Vector3 toTarget = targetPosition - transform.position;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude < 0.001f)
				return false;

			input = BuildMoveInputFromDirection(toTarget.normalized);
			return input.MoveDirection != Vector2.zero;
		}

		private bool TryExtendInvestigationTargetPastLedge(Vector3 lastKnown, out Vector3 extendedTarget)
		{
			extendedTarget = default;

			if (LedgeDropEnabled == false)
				return false;

			// Approach direction: from the zombie toward where it last saw the target. If the target
			// jumped off a ledge, the drop is usually in this same direction just past the last-known
			// spot. Extending the investigation target onto the lower terrain lets the existing ledge
			// drop check fire during UpdateInvestigating instead of the zombie stopping at the lip.
			Vector3 toLastKnown = lastKnown - transform.position;
			toLastKnown.y = 0f;
			if (toLastKnown.sqrMagnitude < 0.001f)
				return false;

			Vector3 direction = toLastKnown.normalized;

			int mask = LayerMask.GetMask("Default", "MapNonVisible");
			if (mask == 0)
				mask = Physics.DefaultRaycastLayers;

			var physicsScene = _zombie != null && _zombie.Runner != null
				? _zombie.Runner.GetPhysicsScene()
				: Physics.defaultPhysicsScene;

			float minDrop = Mathf.Max(0.1f, LedgeDropMinHeightDrop);
			float maxFall = Mathf.Max(minDrop, LedgeDropMaxFallHeight);
			float stepSize = Mathf.Max(0.5f, LedgeDropStepSize);
			float maxProbe = Mathf.Max(stepSize, LedgeDropProbeDistance);
			int maxSteps = Mathf.Max(1, Mathf.CeilToInt(maxProbe / stepSize));

			Vector3 forwardRayOrigin = lastKnown + Vector3.up * 0.8f;

			for (int i = 1; i <= maxSteps; i++)
			{
				float distance = Mathf.Min(stepSize * i, maxProbe);

				if (physicsScene.Raycast(forwardRayOrigin, direction, distance, mask, QueryTriggerInteraction.Ignore))
					return false;

				Vector3 probePoint = lastKnown + direction * distance;
				probePoint.y = lastKnown.y + 0.5f;

				if (physicsScene.Raycast(probePoint, Vector3.down, out RaycastHit groundHit,
					    maxFall + 1f, mask, QueryTriggerInteraction.Ignore) == false)
					continue;

				float drop = lastKnown.y - groundHit.point.y;
				if (drop < minDrop)
					continue;

				extendedTarget = groundHit.point;
				return true;
			}

			return false;
		}

		private bool TryBuildLedgeDropDirectMoveInput(Vector3 targetPosition, out NetworkedInput input)
		{
			input = default;

			if (LedgeDropEnabled == false)
				return false;

			Vector3 currentPosition = transform.position;

			// Goal must be meaningfully below the zombie. Same-level or above goals route normally.
			float minDrop = Mathf.Max(0.1f, LedgeDropMinHeightDrop);
			if (currentPosition.y - targetPosition.y < minDrop)
				return false;

			Vector3 toTarget = targetPosition - currentPosition;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude < 0.001f)
				return false;

			Vector3 direction = toTarget.normalized;

			int mask = LayerMask.GetMask("Default", "MapNonVisible");
			if (mask == 0)
				mask = Physics.DefaultRaycastLayers;

			var physicsScene = _zombie != null && _zombie.Runner != null
				? _zombie.Runner.GetPhysicsScene()
				: Physics.defaultPhysicsScene;

			float maxFall = Mathf.Max(minDrop, LedgeDropMaxFallHeight);
			float probeDistance = Mathf.Max(0.1f, LedgeDropProbeDistance);

			// Step forward in fixed increments. For each step, check that the path forward is clear
			// (no wall at chest height), then probe straight down. The first step where the ground is
			// meaningfully below the zombie is treated as a ledge drop. This makes detection work the
			// same whether the zombie is 0.5m or 5m back from the edge, as long as nothing blocks the
			// path between zombie and the edge.
			float stepSize = Mathf.Max(0.5f, LedgeDropStepSize);
			int maxSteps = Mathf.Max(1, Mathf.CeilToInt(probeDistance / stepSize));
			Vector3 forwardRayOrigin = currentPosition + Vector3.up * 0.8f;

			for (int i = 1; i <= maxSteps; i++)
			{
				float distance = Mathf.Min(stepSize * i, probeDistance);

				// Wall check from zombie to this step. If anything blocks before reaching the step,
				// we can't walk straight ahead — no ledge drop.
				if (physicsScene.Raycast(forwardRayOrigin, direction, distance, mask, QueryTriggerInteraction.Ignore))
					return false;

				Vector3 probePoint = currentPosition + direction * distance;
				probePoint.y = currentPosition.y + 0.5f;

				if (physicsScene.Raycast(probePoint, Vector3.down, out RaycastHit groundHit,
					    maxFall + 1f, mask, QueryTriggerInteraction.Ignore) == false)
					continue; // No ground in range at this step — keep stepping (drop might be deeper)

				float drop = currentPosition.y - groundHit.point.y;
				if (drop < minDrop)
					continue; // Same level here — keep stepping

				input = BuildMoveInputFromDirection(direction);
				return input.MoveDirection != Vector2.zero;
			}

			return false;
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

			if (TryBuildLedgeDropDirectMoveInput(_investigationTarget, out NetworkedInput ledgeInput))
			{
				ClearNavigator();
				return ledgeInput;
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
					Vector3 investigationTarget = _lastKnownTargetPosition;
					if (TryExtendInvestigationTargetPastLedge(investigationTarget, out Vector3 extended))
						investigationTarget = extended;

					StartInvestigation(investigationTarget, _lastStimulusTick, false);
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

			if (TryBuildLedgeDropDirectMoveInput(targetPosition, out NetworkedInput ledgeInput))
			{
				ClearAttackMoveTarget();
				ClearNavigator();
				return ledgeInput;
			}

			if (TryGetAttackMoveTarget(targetPosition, out Vector3 moveTarget))
				return BuildAttackMoveInput(moveTarget);

			if (TryBuildClimbInput(targetPosition, out NetworkedInput climbInput))
				return climbInput;

			if (TryBuildOffNavMeshDirectMoveInput(targetPosition, out NetworkedInput directInput))
				return directInput;

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

			if (TryBuildLedgeDropDirectMoveInput(targetPosition, out NetworkedInput ledgeInput))
			{
				ClearAttackMoveTarget();
				ClearNavigator();
				return ledgeInput;
			}

			if (TryGetAttackMoveTarget(targetPosition, out Vector3 moveTarget))
				return BuildAttackMoveInput(moveTarget);

			if (TryBuildClimbInput(targetPosition, out NetworkedInput climbInput))
				return climbInput;

			if (TryBuildOffNavMeshDirectMoveInput(targetPosition, out NetworkedInput directInput))
				return directInput;

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
			ClearClimbApproachTarget();

			var navigator = _zombie != null ? _zombie.Navigator : null;
			if (navigator != null &&
			    navigator.TryFindReachablePoint(transform.position, targetPosition, ReachablePointSampleDistance, out Vector3 reachablePoint))
			{
				if (IsUsefulAttackMoveTarget(reachablePoint, targetPosition) == false)
				{
					_hasAttackMoveTarget = false;
					if (TryCacheClimbApproachTarget(reachablePoint, targetPosition))
						navigator.SetDestination(reachablePoint);
					else
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

		private bool TryCacheClimbApproachTarget(Vector3 reachablePoint, Vector3 targetPosition)
		{
			if (CanClimbUnreachableTargets == false)
				return false;

			float heightDifference = targetPosition.y - reachablePoint.y;
			if (heightDifference < Mathf.Max(0f, ClimbMinHeightDifference))
				return false;
			if (heightDifference > Mathf.Max(ClimbMinHeightDifference, ClimbMaxHeightDifference))
				return false;

			float maxFlatDistance = Mathf.Max(ClimbApproachMaxDistanceFromTarget, ClimbStartDistance, AttackMoveTargetMaxDistanceFromTarget, _zombie != null ? _zombie.Stats.AttackRange : 0f);
			if (FlatDistanceSqr(reachablePoint, targetPosition) > maxFlatDistance * maxFlatDistance)
				return false;

			_climbApproachTarget = reachablePoint;
			_hasClimbApproachTarget = true;
			return true;
		}

		private bool TryBuildClimbInput(Vector3 targetPosition, out NetworkedInput input)
		{
			input = default;
			if (_hasClimbApproachTarget == false || _zombie == null || _zombie.Navigator == null)
				return false;

			float startDistance = Mathf.Max(0.1f, ClimbStartDistance);
			if (FlatDistanceSqr(transform.position, _climbApproachTarget) > startDistance * startDistance)
			{
				input = BuildMoveInput(_climbApproachTarget, Mathf.Max(0.05f, AttackMoveStoppingDistance), false);
				return input.MoveDirection != Vector2.zero;
			}

			float heightDifference = targetPosition.y - transform.position.y;
			if (heightDifference <= 0.05f)
				return false;
			if (heightDifference > Mathf.Max(ClimbMinHeightDifference, ClimbMaxHeightDifference))
				return false;

			Vector3 climbDirection = targetPosition - transform.position;
			climbDirection.y = 0f;
			if (climbDirection.sqrMagnitude < 0.001f)
				climbDirection = transform.forward;
			else
				climbDirection.Normalize();

			if (TryMantleOntoClimbTarget(targetPosition, climbDirection))
				return false;

			WantsToClimb = true;
			input = BuildMoveInputFromDirection(climbDirection);
			return true;
		}

		private bool TryMantleOntoClimbTarget(Vector3 targetPosition, Vector3 climbDirection)
		{
			if (_zombie == null || _zombie.KCC == null)
				return false;

			float snapDistance = Mathf.Max(0.1f, ClimbMantleSnapDistance);
			if (FlatDistanceSqr(transform.position, targetPosition) > snapDistance * snapDistance)
				return false;

			float heightDifference = targetPosition.y - transform.position.y;
			if (heightDifference < -0.1f || heightDifference > Mathf.Max(0.1f, ClimbMantleHeightTolerance))
				return false;

			Vector3 probeCenter = transform.position + climbDirection * Mathf.Max(0.05f, ClimbMantleForwardDistance);
			probeCenter.y = targetPosition.y;

			Vector3 rayOrigin = probeCenter + Vector3.up * Mathf.Max(0.1f, ClimbMantleProbeHeight);
			float rayDistance = Mathf.Max(0.1f, ClimbMantleProbeHeight + ClimbMantleProbeDistance);
			var physicsScene = _zombie.Runner != null ? _zombie.Runner.GetPhysicsScene() : Physics.defaultPhysicsScene;
			int mantleMask = LayerMask.GetMask("Default");
			if (mantleMask == 0)
				mantleMask = Physics.DefaultRaycastLayers;
			if (physicsScene.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, rayDistance, mantleMask, QueryTriggerInteraction.Ignore) == false)
				return false;
			if (hit.normal.y < Mathf.Clamp01(ClimbMantleMinSurfaceNormalY))
				return false;
			if (Mathf.Abs(hit.point.y - targetPosition.y) > Mathf.Max(0.1f, ClimbMantleHeightTolerance))
				return false;

			_zombie.MantleTo(hit.point);
			ClearAttackMoveTarget();
			ClearNavigator();
			return true;
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
			ClearClimbApproachTarget();
		}

		private void ClearClimbApproachTarget()
		{
			_hasClimbApproachTarget = false;
			_climbApproachTarget = default;
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
