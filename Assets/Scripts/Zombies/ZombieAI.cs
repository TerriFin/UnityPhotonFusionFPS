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
		[FormerlySerializedAs("AttackMoveStoppingDistance")]
		public float ExplicitGoalStoppingDistance = 0.2f;
		[FormerlySerializedAs("AttackDestinationRefreshInterval")]
		public float ExplicitGoalRouteRefreshInterval = 0.5f;
		public float DirectRouteLengthMultiplier = 1.5f;
		public float MaxDirectTraversalDistance = 40f;
		public float ExplicitGoalHeightTolerance = 0.75f;

		[Header("Climbing")]
		[FormerlySerializedAs("CanClimbUnreachableTargets")]
		public bool CanClimbDirectGoals = true;
		public float ClimbSpeedMultiplier = 0.5f;
		[FormerlySerializedAs("ClimbStartDistance")]
		public float ClimbObstacleProbeDistance = 1.25f;
		public float ClimbCommitDuration = 0.75f;
		public float ClimbMantleMaxSnapHeight = 2.0f;

		[Header("Alerts")]
		public int MaxAlertRecipients = 8;
		public float AlertCooldown = 2f;

		[Header("Idle Off-NavMesh Recovery")]
		public float StuckSampleRadius = 0.6f;
		public float StuckMinHeightAboveNavMesh = 0.6f;
		public float StuckCheckInterval = 0.5f;
		public float StuckRandomWanderDurationMin = 1f;
		public float StuckRandomWanderDurationMax = 2.5f;

		public EZombieAIState State { get; private set; }
		public float LastAttackTime { get; private set; } = -1f;
		public bool WantsToClimb { get; private set; }
		public bool IsClimbing => WantsToClimb || Time.timeSinceLevelLoad < _climbCommitUntil;

		private ZombieCharacter _zombie;
		private NetworkObject _target;
		private Vector3 _investigationTarget;
		private Vector3 _lastKnownTargetPosition;
		private float _nextIdleWanderTime;
		private float _nextRetargetTime;
		private float _nextAlertTime;
		private Vector3 _explicitRouteTarget;
		private float _nextExplicitGoalRouteRefreshTime;
		private float _climbCommitUntil;
		private bool _hasExplicitRouteTarget;
		private bool _useDirectExplicitGoalMovement;
		private bool _explicitRouteEndsBeforeGoal;
		private int _lastStimulusTick;
		private float _nextStuckCheckTime;
		private bool _isStuckElevated;
		private Vector3 _stuckWanderDirection;
		private float _stuckWanderEndTime;

		private const float ClimbCheckProbeHeight = 0.25f;
		private const float MantleForwardDistanceScale = 0.75f;
		private const float MantleProbeExtraHeight = 0.35f;
		private const float MantleProbeExtraDistance = 0.75f;
		private const float MantleMinRise = 0.08f;
		private const float MantleMinSurfaceNormalY = 0.65f;
		private const int TraversalHitBufferSize = 16;

		private readonly RaycastHit[] _traversalHits = new RaycastHit[TraversalHitBufferSize];

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

		public void EnterHunting()
		{
			State = EZombieAIState.Hunting;
			_target = null;
			_nextRetargetTime = Time.timeSinceLevelLoad + Random.Range(0f, Mathf.Max(0f, HuntingInitialRetargetStaggerMax));
			ClearExplicitGoalRoute();
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

			if (HasReachedExplicitGoal(_investigationTarget, StoppingDistance))
			{
				ReturnToIdle();
				return default;
			}

			return BuildExplicitGoalMoveInput(_investigationTarget, StoppingDistance, false);
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

			return BuildExplicitGoalMoveInput(targetPosition, ExplicitGoalStoppingDistance, true);
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

			return BuildExplicitGoalMoveInput(targetPosition, ExplicitGoalStoppingDistance, true);
		}

		private void RefreshHuntingTarget()
		{
			NetworkObject previousTarget = _target;
			_target = FindClosestAliveSurvivor();
			if (_target != previousTarget)
				ClearExplicitGoalRoute();

			ScheduleNextHuntingRetarget();
		}

		private void StartAttack(KnownEnemyInfo enemy)
		{
			_target = enemy.Object;
			_lastKnownTargetPosition = enemy.LastKnownPosition;
			_lastStimulusTick = enemy.Tick;
			State = EZombieAIState.Attacking;
			_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
			ClearExplicitGoalRoute();
			AlertNearbyZombies(_lastKnownTargetPosition, enemy.Tick);
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
				ClearExplicitGoalRoute();
				AlertNearbyZombies(_lastKnownTargetPosition, enemy.Tick);
			}

			return true;
		}

		private void StartInvestigation(Vector3 target, int stimulusTick, bool fromAlert)
		{
			_investigationTarget = target;
			_target = null;
			State = EZombieAIState.Investigating;
			ClearExplicitGoalRoute();

			if (fromAlert == false)
				AlertNearbyZombies(target, stimulusTick);
		}

		private NetworkedInput BuildExplicitGoalMoveInput(Vector3 goal, float stoppingDistance, bool directFinalApproach)
		{
			EnsureExplicitGoalRoute(goal);

			if (_useDirectExplicitGoalMovement)
			{
				if (CanUseDirectExplicitGoalMovement(goal))
					return BuildDirectExplicitGoalMoveInput(goal, stoppingDistance);

				_useDirectExplicitGoalMovement = false;
				_nextExplicitGoalRouteRefreshTime = 0f;
				EnsureExplicitGoalRoute(goal);
			}

			var navigator = _zombie.Navigator;
			if (_hasExplicitRouteTarget && navigator != null)
			{
				NetworkedInput routeInput = BuildMoveInput(_explicitRouteTarget, ExplicitGoalStoppingDistance, false);
				if (routeInput.MoveDirection != Vector2.zero)
					return routeInput;

				navigator.Tick(transform.position);
				if (navigator.IsDestinationReached)
				{
					if (_explicitRouteEndsBeforeGoal || directFinalApproach)
					{
						if (TryUseDirectExplicitGoalMovement(goal))
							return BuildDirectExplicitGoalMoveInput(goal, stoppingDistance);

						return BuildLookInput(goal);
					}

					return BuildLookInput(goal);
				}

				if (navigator.HasPath)
					return routeInput;
			}

			if (TryUseDirectExplicitGoalMovement(goal))
				return BuildDirectExplicitGoalMoveInput(goal, stoppingDistance);

			return BuildLookInput(goal);
		}

		private void EnsureExplicitGoalRoute(Vector3 goal)
		{
			if ((_hasExplicitRouteTarget || _useDirectExplicitGoalMovement) &&
			    Time.timeSinceLevelLoad < _nextExplicitGoalRouteRefreshTime)
				return;

			_nextExplicitGoalRouteRefreshTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, ExplicitGoalRouteRefreshInterval);
			_hasExplicitRouteTarget = false;
			_useDirectExplicitGoalMovement = false;
			_explicitRouteEndsBeforeGoal = false;

			var navigator = _zombie.Navigator;
			if (navigator == null ||
			    navigator.TryFindReachablePoint(transform.position, goal, ReachablePointSampleDistance,
				    out Vector3 reachablePoint, out float routeLength) == false)
			{
				if (TryUseDirectExplicitGoalMovement(goal) == false)
					ClearNavigator();

				return;
			}

			_explicitRouteEndsBeforeGoal = IsSameNavigableGoal(reachablePoint, goal) == false;
			float directDistance = Vector3.Distance(transform.position, goal);
			float maxRouteLength = Mathf.Max(0.01f, directDistance) * Mathf.Max(1f, DirectRouteLengthMultiplier);
			if (routeLength > maxRouteLength && TryUseDirectExplicitGoalMovement(goal))
			{
				return;
			}

			_explicitRouteTarget = reachablePoint;
			_hasExplicitRouteTarget = true;
			navigator.SetDestination(reachablePoint);
		}

		private NetworkedInput BuildDirectExplicitGoalMoveInput(Vector3 goal, float stoppingDistance)
		{
			if (CanUseDirectExplicitGoalMovement(goal) == false)
				return BuildLookInput(goal);

			Vector3 toGoal = goal - transform.position;
			Vector3 flatDirection = toGoal;
			flatDirection.y = 0f;

			if (flatDirection.sqrMagnitude <= stoppingDistance * stoppingDistance &&
			    Mathf.Abs(toGoal.y) <= Mathf.Max(0.1f, ExplicitGoalHeightTolerance))
				return BuildLookInput(goal);

			if (flatDirection.sqrMagnitude < 0.001f)
				flatDirection = transform.forward;
			else
				flatDirection.Normalize();

			if (CanClimbDirectGoals && ShouldClimbTowardGoal(flatDirection, goal))
			{
				if (TryMantleForward(flatDirection))
					return default;

				WantsToClimb = true;
			}

			return BuildMoveInputFromDirection(flatDirection);
		}

		private bool ShouldClimbTowardGoal(Vector3 direction, Vector3 goal)
		{
			float now = Time.timeSinceLevelLoad;
			if (now < _climbCommitUntil)
				return true;

			float probeDistance = Mathf.Max(0.1f, ClimbObstacleProbeDistance);
			bool targetIsCloseAndHigher = FlatDistanceSqr(transform.position, goal) <= probeDistance * probeDistance &&
			                              goal.y - transform.position.y > 0.1f;

			bool obstacleAhead = HasClimbObstacleAhead(direction, probeDistance);
			if (obstacleAhead == false && targetIsCloseAndHigher == false)
				return false;

			_climbCommitUntil = now + Mathf.Max(0.05f, ClimbCommitDuration);
			return true;
		}

		private bool HasClimbObstacleAhead(Vector3 direction, float probeDistance)
		{
			// Single probe at approximately knee height. A hit keeps the climb impulse active while
			// the zombie presses against a wall or ledge face. TryMantleForward separately looks for
			// a usable top surface; do not require this ray to clear, because the KCC capsule can
			// stall while it still grazes the face.
			var physicsScene = GetPhysicsScene();
			int mask = GetTraversalMask();
			Vector3 position = transform.position;

			return HasNonCharacterTraversalHit(physicsScene, position + Vector3.up * ClimbCheckProbeHeight, direction,
				probeDistance, mask);
		}

		private bool TryMantleForward(Vector3 direction)
		{
			if (_zombie == null || _zombie.KCC == null)
				return false;

			float maxSnapHeight = Mathf.Max(MantleMinRise, ClimbMantleMaxSnapHeight);
			float forwardDistance = Mathf.Max(0.1f, ClimbObstacleProbeDistance * MantleForwardDistanceScale);
			Vector3 probeCenter = transform.position + direction * forwardDistance;
			Vector3 rayOrigin = probeCenter + Vector3.up * (maxSnapHeight + MantleProbeExtraHeight);
			float rayDistance = maxSnapHeight + MantleProbeExtraHeight + MantleProbeExtraDistance;

			if (TryGetClosestNonCharacterTraversalHit(GetPhysicsScene(), rayOrigin, Vector3.down, rayDistance,
				    GetTraversalMask(), out RaycastHit hit) == false)
				return false;
			if (hit.normal.y < MantleMinSurfaceNormalY)
				return false;

			float heightDifference = hit.point.y - transform.position.y;
			if (heightDifference < MantleMinRise || heightDifference > maxSnapHeight)
				return false;

			_zombie.MantleTo(hit.point);
			_climbCommitUntil = 0f;
			ClearExplicitGoalRoute();
			return true;
		}

		private bool HasNonCharacterTraversalHit(PhysicsScene physicsScene, Vector3 origin, Vector3 direction,
			float distance, int mask)
		{
			int hitCount = physicsScene.Raycast(origin, direction, _traversalHits, distance, mask,
				QueryTriggerInteraction.Ignore);

			for (int i = 0; i < hitCount; i++)
			{
				if (IsCharacterCollider(_traversalHits[i].collider) == false)
					return true;
			}

			return false;
		}

		private bool TryGetClosestNonCharacterTraversalHit(PhysicsScene physicsScene, Vector3 origin, Vector3 direction,
			float distance, int mask, out RaycastHit closestHit)
		{
			closestHit = default;
			int hitCount = physicsScene.Raycast(origin, direction, _traversalHits, distance, mask,
				QueryTriggerInteraction.Ignore);
			float closestDistance = float.MaxValue;
			bool hasHit = false;

			for (int i = 0; i < hitCount; i++)
			{
				RaycastHit hit = _traversalHits[i];
				if (IsCharacterCollider(hit.collider) || hit.distance >= closestDistance)
					continue;

				closestHit = hit;
				closestDistance = hit.distance;
				hasHit = true;
			}

			return hasHit;
		}

		private static bool IsCharacterCollider(Collider collider)
		{
			return collider != null &&
			       (collider.GetComponentInParent<ZombieCharacter>() != null ||
			        collider.GetComponentInParent<Survivor>() != null);
		}

		private bool HasReachedExplicitGoal(Vector3 goal, float stoppingDistance)
		{
			return FlatDistanceSqr(transform.position, goal) <= stoppingDistance * stoppingDistance &&
			       Mathf.Abs(transform.position.y - goal.y) <= Mathf.Max(0.1f, ExplicitGoalHeightTolerance);
		}

		private bool IsSameNavigableGoal(Vector3 reachablePoint, Vector3 goal)
		{
			float stoppingDistance = Mathf.Max(0.1f, ExplicitGoalStoppingDistance);
			return FlatDistanceSqr(reachablePoint, goal) <= stoppingDistance * stoppingDistance &&
			       Mathf.Abs(reachablePoint.y - goal.y) <= Mathf.Max(0.1f, ExplicitGoalHeightTolerance);
		}

		private bool TryUseDirectExplicitGoalMovement(Vector3 goal)
		{
			if (CanUseDirectExplicitGoalMovement(goal) == false)
				return false;

			_hasExplicitRouteTarget = false;
			_useDirectExplicitGoalMovement = true;
			ClearNavigator();
			return true;
		}

		private bool CanUseDirectExplicitGoalMovement(Vector3 goal)
		{
			float maxDistance = Mathf.Max(0f, MaxDirectTraversalDistance);
			return Vector3.Distance(transform.position, goal) <= maxDistance;
		}

		private void ClearExplicitGoalRoute()
		{
			// Intentionally does NOT reset _climbCommitUntil. State transitions (Investigating →
			// Attacking when the zombie peeks over a ledge and finally sees the survivor, target
			// changes, etc.) used to reset it here; that meant the climb impulse turned off in the
			// same tick as the state transition, gravity re-engaged, and the zombie dropped back
			// below the ledge — only to re-engage the climb on the next obstacle hit and loop. The
			// commit expires naturally after ClimbCommitDuration, and TryMantleForward resets it
			// explicitly after a successful hoist (so a zombie that has already landed on the
			// surface does not keep climbing the next tick).
			_explicitRouteTarget = default;
			_nextExplicitGoalRouteRefreshTime = 0f;
			_hasExplicitRouteTarget = false;
			_useDirectExplicitGoalMovement = false;
			_explicitRouteEndsBeforeGoal = false;
			ClearNavigator();
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

			ClearExplicitGoalRoute();
			input = BuildMoveInputFromDirection(_stuckWanderDirection);
			return input.MoveDirection != Vector2.zero;
		}

		private bool IsElevatedOffNavMesh()
		{
			Vector3 position = transform.position;
			float radius = Mathf.Max(0.1f, StuckSampleRadius);
			if (NavMesh.SamplePosition(position, out var hit, radius, NavMesh.AllAreas) == false)
				return true;

			return position.y - hit.position.y > Mathf.Max(0.1f, StuckMinHeightAboveNavMesh);
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
				if (_zombie.Navigator == null ||
				    _zombie.Navigator.TryFindReachablePoint(origin, hit.position, ReachablePointSampleDistance, out Vector3 reachable) == false)
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

		private void AlertNearbyZombies(Vector3 target, int stimulusTick)
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
			ClearExplicitGoalRoute();
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

		private PhysicsScene GetPhysicsScene()
		{
			return _zombie != null && _zombie.Runner != null
				? _zombie.Runner.GetPhysicsScene()
				: Physics.defaultPhysicsScene;
		}

		private static int GetTraversalMask()
		{
			int mask = LayerMask.GetMask("Default", "MapNonVisible");
			return mask != 0 ? mask : Physics.DefaultRaycastLayers;
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
