using System.Collections.Generic;
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
		public float ExplicitGoalHeightTolerance = 0.75f;

		[Header("Climbing")]
		// Zombies steer directly toward explicit goals, but may only climb broad registered climb surfaces: generated
		// terrain ledge faces and prefab-authored rescue faces on props/buildings. See Docs/ZombieAI.md.
		[FormerlySerializedAs("CanClimbUnreachableTargets")]
		[FormerlySerializedAs("CanClimbDirectGoals")]
		public bool UseClimbSurfaces = true;
		public bool UseTerrainShortcutClimbs = true;
		public bool UseRescueClimbs = true;
		public float ClimbSpeedMultiplier = 0.5f;
		[FormerlySerializedAs("ClimbStartDistance")]
		[FormerlySerializedAs("ClimbObstacleProbeDistance")]
		public float ClimbSurfaceEngageDistance = 1.25f;
		public float ClimbCommitDuration = 0.75f;
		public float ClimbMantleMaxSnapHeight = 2.0f;
		public float ClimbDirectApproachDistance = 18f;
		public float ExplicitGoalClimbRefreshInterval = 0.2f;
		public float ClimbRouteSideTolerance = 1.5f;
		public float ClimbMinRise = 0.75f;
		public float RescueMinTargetHeight = 0.75f;
		public float RescueLandingHeightTolerance = 2.5f;
		public float RescueLandingFlatTolerance = 2.5f;
		public float ClimbMantleMaxHorizontalSnapDistance = 1.5f;
		// A committed climb is abandoned if it makes no upward progress for this long (genuinely stuck/blocked). A
		// slow-but-rising climb keeps going — this is NOT a fixed clock, so a tall ledge that takes several seconds to
		// scale is not cut off mid-climb (which caused rise-then-drop oscillation).
		public float ClimbStuckTimeout = 1.5f;
		// Absolute safety cap on a single committed climb, however much progress it is making.
		[FormerlySerializedAs("ClimbLinkMaxDuration")]
		public float ClimbMaxDuration = 15f;
		// After a climb gives up without cresting, do not re-engage a climb for this long. Stops a zombie that cannot
		// complete a climb from oscillating (rise, drop, re-engage) on the same spot — it walks/re-plans instead.
		public float ClimbCooldown = 2f;

		[Header("Road Direct Movement")]
		[FormerlySerializedAs("UseEmergencyObstacleClimbs")]
		public bool UseRoadDirectMovement = true;
		public float RoadDirectMaxDistance = 18f;
		public float RoadDirectMaxObstacleHeight = 5f;
		public float RoadDirectClimbMaxZombieHeightAboveTarget = 0.75f;
		[FormerlySerializedAs("EmergencyClimbMinRise")]
		public float RoadDirectClimbMinRise = 0.2f;
		[FormerlySerializedAs("EmergencyClimbProbeDistance")]
		public float RoadDirectClimbProbeDistance = 1.25f;
		[FormerlySerializedAs("EmergencyClimbMaxHeight")]
		public float RoadDirectClimbMaxHeight = 2.25f;
		[FormerlySerializedAs("EmergencyClimbLandingInset")]
		public float RoadDirectClimbLandingInset = 0.75f;
		[FormerlySerializedAs("EmergencyClimbMinSurfaceNormalY")]
		public float RoadDirectClimbMinSurfaceNormalY = 0.45f;
		[FormerlySerializedAs("EmergencyIdlePerchIslandRadius")]
		public float StuckSmallIslandRadius = 4f;

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
		private PlayerRef _huntPlayer;
		private bool _hasHuntPlayer;
		private Vector3 _investigationTarget;
		private Vector3 _lastKnownTargetPosition;
		private float _nextIdleWanderTime;
		private float _nextRetargetTime;
		private float _nextAlertTime;
		private float _climbCommitUntil;
		private int _lastStimulusTick;
		// Set while the zombie is climbing a registered surface. Retargeting can change the post-climb goal, but the
		// climb itself keeps driving to this landing until it crests or times out.
		private bool _climbingSurface;
		private Vector3 _climbLanding;
		private float _climbUntil;
		private float _climbStuckUntil;
		private float _climbHighestY;
		private float _climbCooldownUntil;
		private float _nextStuckCheckTime;
		private bool _isStuckElevated;
		private Vector3 _stuckWanderDirection;
		private float _stuckWanderEndTime;
		private NavMeshPath _scratchNavMeshPath;
		private bool _hasCachedClimbDecision;
		private bool _cachedClimbCandidateFound;
		private ZombieClimbCandidate _cachedClimbCandidate;
		private Vector3 _cachedClimbGoal;
		private Vector3 _cachedClimbPosition;
		private float _nextClimbCandidateRefreshTime;

		// Smallest rise that still counts as a mantle (floors the snap height so Mathf.Max never returns 0).
		private const float MantleMinRise = 0.08f;
		private const float ClimbStopRise = 0.5f;
		private const float ClimbCandidateGoalRefreshDistance = 0.75f;
		private const float ClimbCandidatePositionRefreshDistance = 0.75f;
		private const float ShortcutBlockerProbeHeight = 0.8f;
		private const float RoadDirectLineOfSightHeight = 1.5f;
		private const float RoadDirectObstacleProbeHeight = 0.55f;
		private const float RoadDirectLandingMinGoalProgress = 0.05f;
		private const int ShortcutBlockerHitBufferSize = 16;

		private readonly RaycastHit[] _shortcutBlockerHits = new RaycastHit[ShortcutBlockerHitBufferSize];

		// Reused only inside TryPickRandomHuntPlayer, which runs synchronously on state authority, so a single
		// shared buffer avoids a per-retarget allocation across hundreds of zombies.
		private static readonly List<PlayerRef> HuntPlayerBuffer = new(32);

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
			_hasHuntPlayer = false;
			_nextRetargetTime = Time.timeSinceLevelLoad + Random.Range(0f, Mathf.Max(0f, HuntingInitialRetargetStaggerMax));
			ClearExplicitGoalRoute();
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick, bool fromAlert)
		{
			if (_zombie == null || _zombie.IsOvertime)
				return;
			if (stimulusTick < _lastStimulusTick)
				return;
			if (_climbingSurface || ShouldIgnoreInvestigationStimulus())
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
			if (TryGetStuckRandomMoveInput(out NetworkedInput stuckInput))
				return stuckInput;

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

			return BuildExplicitGoalMoveInput(_investigationTarget, StoppingDistance);
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
			bool shouldPrioritizeClimb = ShouldPrioritizeClimbTowardTarget(targetPosition);
			if (_zombie.TryAttack(_target))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				if (shouldPrioritizeClimb == false)
					return BuildLookInput(targetPosition);
			}
			if (TryBuildRoadDirectTargetInput(targetPosition, true, out NetworkedInput roadInput))
				return roadInput;
			if (TryBuildVisibleStuckTargetInput(targetPosition, true, out NetworkedInput stuckInput))
				return stuckInput;
			if (shouldPrioritizeClimb == false && ShouldHoldMeleePosition(_target, targetPosition))
				return BuildLookInput(targetPosition);

			return BuildExplicitGoalMoveInput(targetPosition, ExplicitGoalStoppingDistance);
		}

		private NetworkedInput UpdateHunting()
		{
			// A directly sensed enemy always overrides the committed global target. The global picker
			// deliberately ignores neutral survivors so overtime pressure is shared evenly between
			// players, but a zombie that senses any survivor (neutral included) on the way to its
			// target should still attack whoever it ran into. This is temporary: the committed player
			// is left untouched, so the zombie resumes heading to its team once the sensed enemy is lost.
			if (TryGetDirectEnemy(out var sensed))
				SetHuntTarget(sensed.Object);
			else
				EnsureCommittedHuntTarget();

			if (_target == null)
				return default;

			Vector3 targetPosition = _target.transform.position;
			bool shouldPrioritizeClimb = ShouldPrioritizeClimbTowardTarget(targetPosition);
			if (_zombie.TryAttack(_target))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				if (shouldPrioritizeClimb == false)
					return BuildLookInput(targetPosition);
			}
			bool targetDirectlySensed = sensed.Object == _target;
			if (targetDirectlySensed && TryBuildRoadDirectTargetInput(targetPosition, true, out NetworkedInput roadInput))
				return roadInput;
			if (targetDirectlySensed && TryBuildVisibleStuckTargetInput(targetPosition, true, out NetworkedInput stuckInput))
				return stuckInput;
			if (shouldPrioritizeClimb == false && ShouldHoldMeleePosition(_target, targetPosition))
				return BuildLookInput(targetPosition);

			return BuildExplicitGoalMoveInput(targetPosition, ExplicitGoalStoppingDistance);
		}

		// A hunting zombie commits to one player and keeps hunting that player's team until the team is wiped
		// out (or the zombie dies). Periodic refreshes only re-pick the closest survivor of the committed
		// player; they do NOT re-roll the player. Re-rolling the player every interval made in-transit zombies
		// flip target teams mid-journey, so a horde caught between two holed-up teams just oscillated back and
		// forth and never arrived. The player is only re-rolled once the committed team has no alive survivors.
		private void EnsureCommittedHuntTarget()
		{
			bool targetAlive = IsTargetAlive(_target);

			if (targetAlive)
			{
				// Keep heading to the committed survivor until the next refresh tick.
				if (Time.timeSinceLevelLoad < _nextRetargetTime)
					return;
			}
			else if (_target == null && _hasHuntPlayer == false && Time.timeSinceLevelLoad < _nextRetargetTime)
			{
				// First acquisition only: honor the stagger so the whole horde does not scan on the same tick
				// when overtime begins. A target that died mid-hunt re-picks immediately (no stagger wait).
				return;
			}

			if (_hasHuntPlayer == false || PlayerHasAliveSurvivor(_huntPlayer) == false)
				_hasHuntPlayer = TryPickRandomHuntPlayer(out _huntPlayer);

			SetHuntTarget(_hasHuntPlayer ? FindClosestSurvivorForOwner(_huntPlayer) : null);
			ScheduleNextHuntingRetarget();
		}

		private void SetHuntTarget(NetworkObject target)
		{
			if (target == _target)
				return;

			Vector3 targetPosition = target != null ? target.transform.position : default;
			bool preserveActiveClimb = target != null && ShouldPreserveActiveClimbForTarget(targetPosition);
			_target = target;
			ClearExplicitGoalRoute(preserveActiveClimb);
		}

		private void StartAttack(KnownEnemyInfo enemy)
		{
			_target = enemy.Object;
			_lastKnownTargetPosition = enemy.LastKnownPosition;
			_lastStimulusTick = enemy.Tick;
			State = EZombieAIState.Attacking;
			_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
			ClearExplicitGoalRoute(ShouldPreserveActiveClimbForTarget(enemy.LastKnownPosition));
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
				ClearExplicitGoalRoute(ShouldPreserveActiveClimbForTarget(enemy.LastKnownPosition));
				AlertNearbyZombies(_lastKnownTargetPosition, enemy.Tick);
			}

			return true;
		}

		private void StartInvestigation(Vector3 target, int stimulusTick, bool fromAlert)
		{
			_investigationTarget = target;
			_target = null;
			State = EZombieAIState.Investigating;
			ClearExplicitGoalRoute(ShouldPreserveActiveClimbForTarget(target));

			if (fromAlert == false)
				AlertNearbyZombies(target, stimulusTick);
		}

		private NetworkedInput BuildExplicitGoalMoveInput(Vector3 goal, float stoppingDistance)
		{
			var navigator = _zombie.Navigator;
			if (navigator == null)
				return BuildLookInput(goal);

			if (TryBuildStuckExplicitGoalMoveInput(goal, out NetworkedInput stuckInput))
				return stuckInput;

			if (HasReachedExplicitGoal(goal, stoppingDistance))
			{
				_climbingSurface = false;
				return BuildLookInput(goal);
			}

			if (TryBuildRoadDirectTargetInput(goal, false, out NetworkedInput roadInput))
				return roadInput;

			navigator.SetDestination(goal);
			navigator.Tick(transform.position);

			if (TryContinueActiveClimb(out NetworkedInput climbInput))
				return climbInput;

			if (_climbingSurface)
			{
				float now = Time.timeSinceLevelLoad;

				// Reset the stuck timer whenever we gain height, so a slow-but-rising climb is never cut off; only a
				// climb that stops making upward progress (blocked) gives up. An absolute cap backstops it.
				if (transform.position.y > _climbHighestY + 0.02f)
				{
					_climbHighestY = transform.position.y;
					_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
				}

				if (HasCrestedClimb(_climbLanding))
				{
					_climbingSurface = false;
				}
				else if (now >= _climbStuckUntil || now >= _climbUntil)
				{
					// Stuck (no progress) or hit the safety cap — sit out a cooldown so we do not immediately
					// re-engage and oscillate.
					_climbingSurface = false;
					_climbCooldownUntil = now + Mathf.Max(0f, ClimbCooldown);
				}
				else
				{
					return BuildClimbInput(_climbLanding);
				}
			}

			if (TryGetCachedDirectClimbCandidate(goal, navigator, out ZombieClimbCandidate climbCandidate))
				return BuildClimbCandidateMoveInput(goal, climbCandidate);

			if (navigator.IsDestinationReached)
				return BuildLookInput(goal);

			if (navigator.TryGetSteeringTarget(transform.position, out Vector3 steer))
			{
				float steerStoppingDistance = GetPathSteeringStoppingDistance(navigator, stoppingDistance);
				Vector3 flatToSteer = steer - transform.position;
				flatToSteer.y = 0f;
				if (flatToSteer.sqrMagnitude <= steerStoppingDistance * steerStoppingDistance)
					return BuildLookInput(goal);

				Vector3 steerDirection = flatToSteer.normalized;
				return BuildMoveInputFromDirection(steerDirection);
			}

			return BuildLookInput(goal);
		}

		private bool TryBuildRoadDirectTargetInput(Vector3 targetPosition, bool targetDirectlySensed, out NetworkedInput input)
		{
			input = default;
			if (UseRoadDirectMovement == false)
				return false;

			float maxDistance = Mathf.Max(0.5f, RoadDirectMaxDistance);
			if (FlatDistanceSqr(transform.position, targetPosition) > maxDistance * maxDistance)
				return false;
			if (IsOnRoadDirectTile(targetPosition) == false)
				return false;
			if (targetDirectlySensed == false && HasRoadDirectLineOfSight(targetPosition) == false)
				return false;

			ClearNavigator();

			if (_climbingSurface)
			{
				if (transform.position.y - targetPosition.y > Mathf.Max(0f, RoadDirectClimbMaxZombieHeightAboveTarget))
				{
					_climbingSurface = false;
					_climbCommitUntil = 0f;
					input = BuildDirectGoalMoveInput(targetPosition);
					return true;
				}

				if (TryContinueActiveClimb(out input))
					return true;
			}

			Vector3 direction = GetFlatDirectionTo(targetPosition, transform.forward);
			bool blockedByRejectedClimb = false;
			if (CanRoadDirectClimbToward(targetPosition) &&
			    TryStartRoadDirectObstacleClimb(direction, targetPosition, out input, out blockedByRejectedClimb))
			{
				return true;
			}
			if (blockedByRejectedClimb)
				return false;

			input = direction.sqrMagnitude > 0.001f
				? BuildMoveInputFromDirection(direction)
				: BuildLookInput(targetPosition);
			return true;
		}

		private bool TryBuildVisibleStuckTargetInput(Vector3 targetPosition, bool targetDirectlySensed, out NetworkedInput input)
		{
			input = default;
			if (targetDirectlySensed == false && HasRoadDirectLineOfSight(targetPosition) == false)
				return false;
			if (IsElevatedOffNavMesh() == false && IsOnSmallElevatedNavMeshIsland() == false)
				return false;

			if (_climbingSurface && transform.position.y - targetPosition.y > Mathf.Max(0f, RoadDirectClimbMaxZombieHeightAboveTarget))
			{
				_climbingSurface = false;
				_climbCommitUntil = 0f;
			}

			ClearNavigator();
			input = BuildDirectGoalMoveInput(targetPosition);
			return true;
		}

		private bool TryBuildStuckExplicitGoalMoveInput(Vector3 goal, out NetworkedInput input)
		{
			input = default;
			if (_climbingSurface || IsClimbing)
				return false;
			if (IsStuckElevatedCached() == false)
				return false;

			ClearNavigator();
			input = BuildDirectGoalMoveInput(goal);
			if (input.MoveDirection != Vector2.zero)
				return true;

			input = BuildStuckRandomMoveInput();
			return input.MoveDirection != Vector2.zero;
		}

		private bool CanRoadDirectClimbToward(Vector3 targetPosition)
		{
			return transform.position.y - targetPosition.y <= Mathf.Max(0f, RoadDirectClimbMaxZombieHeightAboveTarget);
		}

		private static bool IsOnRoadDirectTile(Vector3 position)
		{
			return ZombieClimbSurfaces.TryGetRoadCell(position, out WorldGridCell cell) &&
			       (cell.IsRoad || cell.IsHeightChangeRoad);
		}

		private bool HasRoadDirectLineOfSight(Vector3 targetPosition)
		{
			Vector3 origin = transform.position + Vector3.up * RoadDirectLineOfSightHeight;
			Vector3 target = targetPosition + Vector3.up * RoadDirectLineOfSightHeight;
			Vector3 direction = target - origin;
			float distance = direction.magnitude;
			if (distance < 0.05f)
				return true;

			return TryRaycastWorld(origin, direction / distance, distance, out _) == false;
		}

		private bool TryContinueActiveClimb(out NetworkedInput input)
		{
			input = default;
			if (_climbingSurface == false)
				return false;

			float now = Time.timeSinceLevelLoad;

			if (transform.position.y > _climbHighestY + 0.02f)
			{
				_climbHighestY = transform.position.y;
				_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			}

			if (HasCrestedClimb(_climbLanding))
			{
				_climbingSurface = false;
				return false;
			}
			if (now >= _climbStuckUntil || now >= _climbUntil)
			{
				_climbingSurface = false;
				_climbCooldownUntil = now + Mathf.Max(0f, ClimbCooldown);
				return false;
			}

			input = BuildClimbInput(_climbLanding);
			return true;
		}

		private NetworkedInput BuildClimbCandidateMoveInput(Vector3 goal, ZombieClimbCandidate candidate)
		{
			float climbEngageDistance = Mathf.Max(0.05f, ClimbSurfaceEngageDistance);
			float distanceToStart = GetCurrentDistanceToClimbStart(candidate);
			if (candidate.RequiresClimb && distanceToStart <= climbEngageDistance)
				return StartSurfaceClimb(candidate);

			if (candidate.IsRescue == false && distanceToStart > climbEngageDistance)
				return BuildDirectGoalMoveInput(GetShortcutApproachPoint(candidate));

			return BuildDirectGoalMoveInput(goal);
		}

		private bool TryGetCachedDirectClimbCandidate(Vector3 goal, CharacterNavigator navigator, out ZombieClimbCandidate candidate)
		{
			if (ShouldRefreshClimbCandidate(goal))
				RefreshClimbCandidateCache(goal, navigator);

			if (_cachedClimbCandidateFound)
			{
				candidate = _cachedClimbCandidate;
				return true;
			}

			candidate = default;
			return false;
		}

		private bool ShouldRefreshClimbCandidate(Vector3 goal)
		{
			if (_hasCachedClimbDecision == false)
				return true;
			if (Time.timeSinceLevelLoad >= _nextClimbCandidateRefreshTime)
				return true;
			if (FlatDistanceSqr(goal, _cachedClimbGoal) >
			    ClimbCandidateGoalRefreshDistance * ClimbCandidateGoalRefreshDistance)
				return true;
			return FlatDistanceSqr(transform.position, _cachedClimbPosition) >
			       ClimbCandidatePositionRefreshDistance * ClimbCandidatePositionRefreshDistance;
		}

		private void RefreshClimbCandidateCache(Vector3 goal, CharacterNavigator navigator)
		{
			_cachedClimbGoal = goal;
			_cachedClimbPosition = transform.position;
			_cachedClimbCandidateFound = TryFindDirectClimbCandidate(goal, navigator, out _cachedClimbCandidate);
			_hasCachedClimbDecision = true;
			ScheduleNextClimbCandidateRefresh();
		}

		private void ScheduleNextClimbCandidateRefresh()
		{
			float interval = Mathf.Max(0.02f, ExplicitGoalClimbRefreshInterval);
			_nextClimbCandidateRefreshTime = Time.timeSinceLevelLoad + Random.Range(interval * 0.75f, interval * 1.25f);
		}

		private void InvalidateClimbCandidateCache()
		{
			_hasCachedClimbDecision = false;
			_cachedClimbCandidateFound = false;
			_cachedClimbCandidate = default;
			_nextClimbCandidateRefreshTime = 0f;
		}

		private bool TryFindDirectClimbCandidate(Vector3 goal, CharacterNavigator navigator, out ZombieClimbCandidate candidate)
		{
			candidate = default;
			if (UseClimbSurfaces == false || Time.timeSinceLevelLoad < _climbCooldownUntil)
				return false;

			Vector3 position = transform.position;
			float directDistance = FlatDistance(position, goal);
			if (directDistance < 0.05f)
				return false;

			bool hasCompletePath = navigator != null && navigator.HasCompletePathToDestination;
			float pathSavings = hasCompletePath ? navigator.CurrentPathLength - directDistance : float.PositiveInfinity;

			bool allowShortcut = UseTerrainShortcutClimbs &&
			                     navigator != null &&
			                     (hasCompletePath || navigator.HasPath == false || navigator.IsDestinationUnreachable) &&
			                     pathSavings > 0f;
			bool allowRescue = UseRescueClimbs && IsRescueClimbAllowed(goal, navigator);
			if (allowShortcut == false && allowRescue == false)
				return false;

			float shortcutSearchDistance = Mathf.Max(ClimbDirectApproachDistance, directDistance + ClimbRouteSideTolerance);

			if (ZombieClimbSurfaces.TryFindDirectClimb(
				position,
				goal,
				allowShortcut,
				allowRescue,
				pathSavings,
				shortcutSearchDistance,
				ClimbDirectApproachDistance,
				ClimbRouteSideTolerance,
				ClimbMinRise,
				RescueLandingHeightTolerance,
				RescueLandingFlatTolerance,
				out candidate) == false)
			{
				return false;
			}

			return candidate.IsRescue || IsShortcutApproachClear(position, candidate, navigator);
		}

		private float GetCurrentDistanceToClimbStart(ZombieClimbCandidate candidate)
		{
			Vector3 start = candidate.IsRescue ? candidate.ContactPoint : GetShortcutApproachPoint(candidate);
			return FlatDistance(transform.position, start);
		}

		private bool IsRescueClimbAllowed(Vector3 goal, CharacterNavigator navigator)
		{
			if (goal.y - transform.position.y < Mathf.Max(0.05f, RescueMinTargetHeight))
				return false;
			if (navigator == null)
				return true;
			if (navigator.IsDestinationReached || navigator.IsDestinationUnreachable)
				return true;

			float maxDistance = Mathf.Max(0.5f, ClimbDirectApproachDistance);
			return navigator.HasPath == false && FlatDistanceSqr(transform.position, goal) <= maxDistance * maxDistance;
		}

		private NetworkedInput StartSurfaceClimb(ZombieClimbCandidate candidate)
		{
			InvalidateClimbCandidateCache();
			float now = Time.timeSinceLevelLoad;
			_climbingSurface = true;
			_climbLanding = candidate.LandingPoint;
			_climbHighestY = transform.position.y;
			_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			_climbUntil = now + Mathf.Max(0.5f, ClimbMaxDuration);
			return BuildClimbInput(_climbLanding);
		}

		private bool IsOnSmallElevatedNavMeshIsland()
		{
			var navigator = _zombie != null ? _zombie.Navigator : null;
			int areaMask = navigator != null ? navigator.AreaMask : NavMesh.AllAreas;
			float sampleDistance = Mathf.Max(0.25f, StuckSampleRadius);
			Vector3 position = transform.position;
			if (NavMesh.SamplePosition(position, out var startHit, sampleDistance, areaMask) == false)
				return false;

			float radius = Mathf.Max(0.5f, StuckSmallIslandRadius);
			float heightTolerance = Mathf.Max(0.1f, ExplicitGoalHeightTolerance);
			const int samples = 8;

			for (int i = 0; i < samples; i++)
			{
				float angle = i * Mathf.PI * 2f / samples;
				Vector3 probe = position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
				if (NavMesh.SamplePosition(probe, out var probeHit, sampleDistance, areaMask) == false)
					continue;
				if (Mathf.Abs(probeHit.position.y - startHit.position.y) > heightTolerance)
					continue;
				if (HasCompleteNavMeshPathToGoalHeight(startHit.position, probeHit.position, heightTolerance))
					return false;
			}

			return true;
		}

		private bool HasCompleteNavMeshPath(Vector3 start, Vector3 goal)
		{
			return HasCompleteNavMeshPathToGoalHeight(start, goal, float.PositiveInfinity);
		}

		private bool HasCompleteNavMeshPathToGoalHeight(Vector3 start, Vector3 goal, float maxGoalHeightDelta)
		{
			var navigator = _zombie != null ? _zombie.Navigator : null;
			int areaMask = navigator != null ? navigator.AreaMask : NavMesh.AllAreas;
			float sampleDistance = navigator != null ? Mathf.Max(0.25f, navigator.SampleMaxDistance) : Mathf.Max(0.25f, StuckSampleRadius);
			if (NavMesh.SamplePosition(start, out var startHit, sampleDistance, areaMask) == false)
				return false;
			if (TrySampleNavMeshGoalAtHeight(goal, sampleDistance, maxGoalHeightDelta, areaMask, out Vector3 sampledGoal) == false)
				return false;
			if (_scratchNavMeshPath == null)
				_scratchNavMeshPath = new NavMeshPath();
			if (NavMesh.CalculatePath(startHit.position, sampledGoal, areaMask, _scratchNavMeshPath) == false)
				return false;

			return _scratchNavMeshPath.status == NavMeshPathStatus.PathComplete;
		}

		private static bool TrySampleNavMeshGoalAtHeight(
			Vector3 goal,
			float sampleDistance,
			float maxGoalHeightDelta,
			int areaMask,
			out Vector3 sampledGoal)
		{
			sampledGoal = default;
			float maxHeightDelta = float.IsPositiveInfinity(maxGoalHeightDelta)
				? float.PositiveInfinity
				: Mathf.Max(0.05f, maxGoalHeightDelta);
			float radius = Mathf.Max(0.25f, sampleDistance);

			bool found = false;
			float bestScore = float.PositiveInfinity;
			const int rings = 3;
			const int samples = 8;

			for (int ring = 0; ring <= rings; ring++)
			{
				int ringSamples = ring == 0 ? 1 : samples;
				float ringRadius = ring == 0 ? 0f : radius * ring / rings;
				for (int sample = 0; sample < ringSamples; sample++)
				{
					float angle = ringSamples == 1 ? 0f : sample * Mathf.PI * 2f / ringSamples;
					Vector3 probe = goal + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ringRadius;
					if (NavMesh.SamplePosition(probe, out var hit, sampleDistance, areaMask) == false)
						continue;

					float heightDelta = Mathf.Abs(hit.position.y - goal.y);
					if (heightDelta > maxHeightDelta)
						continue;

					float score = FlatDistanceSqr(hit.position, goal) + heightDelta * heightDelta;
					if (score >= bestScore)
						continue;

					bestScore = score;
					sampledGoal = hit.position;
					found = true;
				}
			}

			return found;
		}

		private bool TryStartRoadDirectObstacleClimb(
			Vector3 desiredDirection,
			Vector3 goal,
			out NetworkedInput input,
			out bool blockedByRejectedClimb,
			float maxObstacleDistance = float.PositiveInfinity)
		{
			input = default;
			blockedByRejectedClimb = false;
			if (UseRoadDirectMovement == false ||
			    _climbingSurface ||
			    Time.timeSinceLevelLoad < _climbCooldownUntil)
			{
				return false;
			}

			Vector3 primaryDirection = FlattenDirection(desiredDirection);
			Vector3 directDirection = GetFlatDirectionTo(goal, primaryDirection.sqrMagnitude > 0.001f
				? primaryDirection
				: transform.forward);

			bool found = TryFindRoadDirectObstacleLanding(primaryDirection, maxObstacleDistance, out Vector3 landing);
			if (found == false && Vector3.Dot(primaryDirection, directDirection) < 0.98f)
				found = TryFindRoadDirectObstacleLanding(directDirection, maxObstacleDistance, out landing);
			if (found == false && ShouldSearchLocalRoadDirectClimb(goal))
				found = TryFindBestLocalRoadDirectObstacleLanding(goal, maxObstacleDistance, out landing);
			if (found == false)
				return false;
			if (ShouldRejectRoadDirectLanding(goal, landing))
			{
				blockedByRejectedClimb = true;
				return false;
			}

			float now = Time.timeSinceLevelLoad;
			_climbingSurface = true;
			_climbLanding = landing;
			_climbHighestY = transform.position.y;
			_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			_climbUntil = now + Mathf.Max(0.5f, ClimbMaxDuration);
			input = BuildClimbInput(_climbLanding);
			return true;
		}

		private bool ShouldRejectRoadDirectLanding(Vector3 goal, Vector3 landing)
		{
			if (DoesRoadDirectLandingProgressTowardGoal(goal, landing) == false)
				return true;

			if (IsStuckElevatedCached() == false)
				return false;

			if (TryGetRoadDirectSupportRoot(transform.position, out Transform currentSupport) == false ||
			    TryGetRoadDirectSupportRoot(goal, out Transform goalSupport) == false ||
			    TryGetRoadDirectSupportRoot(landing, out Transform landingSupport) == false)
				return false;

			if (currentSupport == goalSupport)
				return false;

			return landingSupport != goalSupport;
		}

		private bool DoesRoadDirectLandingProgressTowardGoal(Vector3 goal, Vector3 landing)
		{
			if (TryGetRoadDirectSupportRoot(goal, out Transform goalSupport) &&
			    TryGetRoadDirectSupportRoot(landing, out Transform landingSupport) &&
			    landingSupport == goalSupport)
			{
				return true;
			}

			float currentDistance = FlatDistance(transform.position, goal);
			float landingDistance = FlatDistance(landing, goal);
			return landingDistance < currentDistance - RoadDirectLandingMinGoalProgress;
		}

		private bool TryGetRoadDirectSupportCollider(Vector3 position, out Collider support)
		{
			support = null;
			Vector3 origin = position + Vector3.up * 0.35f;
			float distance = Mathf.Max(1f, RoadDirectMaxObstacleHeight + 1f);
			if (TryRaycastWorld(origin, Vector3.down, distance, out RaycastHit hit) == false)
				return false;

			support = hit.collider;
			return support != null;
		}

		private bool TryGetRoadDirectSupportRoot(Vector3 position, out Transform root)
		{
			root = null;
			if (TryGetRoadDirectSupportCollider(position, out Collider support) == false)
				return false;

			root = support.transform.parent != null ? support.transform.parent : support.transform;
			return root != null;
		}

		private bool ShouldSearchLocalRoadDirectClimb(Vector3 goal)
		{
			if (goal.y - transform.position.y <= ClimbStopRise)
				return false;

			float radius = Mathf.Max(0.5f, RoadDirectClimbProbeDistance + RoadDirectClimbLandingInset + 0.5f);
			return FlatDistanceSqr(transform.position, goal) <= radius * radius;
		}

		private bool TryFindBestLocalRoadDirectObstacleLanding(
			Vector3 goal,
			float maxObstacleDistance,
			out Vector3 bestLanding)
		{
			bestLanding = default;
			bool found = false;
			float bestScore = float.PositiveInfinity;
			const int samples = 8;

			for (int i = 0; i < samples; i++)
			{
				float angle = i * Mathf.PI * 2f / samples;
				Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
				if (TryFindRoadDirectObstacleLanding(direction, maxObstacleDistance, out Vector3 candidate) == false)
					continue;

				float heightDelta = candidate.y - goal.y;
				float score = FlatDistanceSqr(candidate, goal) + heightDelta * heightDelta;
				if (score >= bestScore)
					continue;

				bestScore = score;
				bestLanding = candidate;
				found = true;
			}

			return found;
		}

		private bool TryFindRoadDirectObstacleLanding(Vector3 desiredDirection, float maxObstacleDistance, out Vector3 landing)
		{
			landing = default;
			Vector3 direction = FlattenDirection(desiredDirection);
			if (direction.sqrMagnitude <= 0.001f)
				return false;

			float probeDistance = Mathf.Max(0.1f, RoadDirectClimbProbeDistance);
			float maxHeight = Mathf.Max(MantleMinRise, RoadDirectClimbMaxHeight, RoadDirectMaxObstacleHeight);
			Vector3 position = transform.position;
			float highProbeHeight = Mathf.Max(0.1f, RoadDirectObstacleProbeHeight);
			if (TryFindRoadDirectObstacleLandingFromProbe(position, direction, probeDistance, maxHeight, highProbeHeight,
				    maxObstacleDistance, out landing))
			{
				return true;
			}

			float lowProbeHeight = Mathf.Max(0.1f, RoadDirectClimbMinRise);
			if (lowProbeHeight < highProbeHeight - 0.05f &&
			    TryFindRoadDirectObstacleLandingFromProbe(position, direction, probeDistance, maxHeight, lowProbeHeight,
				    maxObstacleDistance, out landing))
			{
				return true;
			}

			return false;
		}

		private bool TryFindRoadDirectObstacleLandingFromProbe(
			Vector3 position,
			Vector3 direction,
			float probeDistance,
			float maxHeight,
			float probeHeight,
			float maxObstacleDistance,
			out Vector3 landing)
		{
			landing = default;
			float originBackoff = Mathf.Min(0.35f, probeDistance * 0.5f);
			Vector3 obstacleOrigin = position - direction * originBackoff + Vector3.up * Mathf.Max(0.1f, probeHeight);
			if (TryRaycastWorld(obstacleOrigin, direction, probeDistance + originBackoff, out RaycastHit obstacleHit) == false)
				return false;

			float obstacleDistanceFromPosition = Mathf.Max(0f, obstacleHit.distance - originBackoff);
			float allowedObstacleDistance = float.IsPositiveInfinity(maxObstacleDistance)
				? probeDistance
				: Mathf.Max(0.05f, maxObstacleDistance);
			if (obstacleDistanceFromPosition > allowedObstacleDistance)
				return false;
			if (HasRoadDirectClimbApproachSupport(position, direction, obstacleDistanceFromPosition) == false)
				return false;

			float landingInset = Mathf.Max(0.05f, RoadDirectClimbLandingInset);
			float edgeProbeInset = Mathf.Min(landingInset, 0.15f);
			if (TryFindRoadDirectObstacleLandingAtOffset(position, obstacleHit.point, direction, edgeProbeInset, maxHeight,
				    out landing))
				return true;
			if (landingInset > edgeProbeInset + 0.01f &&
			    TryFindRoadDirectObstacleLandingAtOffset(position, obstacleHit.point, direction, landingInset, maxHeight,
				    out landing))
				return true;

			return false;
		}

		private bool HasRoadDirectClimbApproachSupport(Vector3 position, Vector3 direction, float obstacleDistance)
		{
			if (obstacleDistance <= 0.25f)
				return true;

			float allowedDrop = Mathf.Max(0.15f, RoadDirectClimbMinRise);
			float rayStartHeight = Mathf.Max(0.2f, RoadDirectObstacleProbeHeight);
			float rayDistance = rayStartHeight + allowedDrop + 0.1f;
			float maxSampleDistance = Mathf.Max(0.1f, obstacleDistance - 0.1f);
			int sampleCount = obstacleDistance > 0.75f ? 2 : 1;

			for (int i = 0; i < sampleCount; i++)
			{
				float sampleDistance = sampleCount == 1
					? maxSampleDistance
					: Mathf.Lerp(maxSampleDistance * 0.5f, maxSampleDistance, (float)i / (sampleCount - 1));
				Vector3 probeOrigin = position + direction * sampleDistance + Vector3.up * rayStartHeight;
				if (TryRaycastWorld(probeOrigin, Vector3.down, rayDistance, out RaycastHit hit) == false)
					return false;
				if (position.y - hit.point.y > allowedDrop)
					return false;
			}

			return true;
		}

		private bool TryFindRoadDirectObstacleLandingAtOffset(
			Vector3 position,
			Vector3 obstaclePoint,
			Vector3 direction,
			float inset,
			float maxHeight,
			out Vector3 landing)
		{
			landing = default;
			Vector3 topProbeOrigin = obstaclePoint + direction * Mathf.Max(0.05f, inset) +
			                         Vector3.up * (maxHeight + 0.05f);
			float downDistance = maxHeight + 0.3f;
			if (TryRaycastWorld(topProbeOrigin, Vector3.down, downDistance, out RaycastHit topHit) == false)
				return false;

			float minSurfaceNormalY = Mathf.Clamp(RoadDirectClimbMinSurfaceNormalY, 0.05f, 1f);
			if (topHit.normal.y < minSurfaceNormalY)
				return false;

			float rise = topHit.point.y - position.y;
			if (rise < Mathf.Max(MantleMinRise, RoadDirectClimbMinRise) || rise > maxHeight)
				return false;

			Vector3 flatToLanding = topHit.point - position;
			flatToLanding.y = 0f;
			if (flatToLanding.sqrMagnitude > 0.001f && Vector3.Dot(flatToLanding.normalized, direction) < 0.2f)
				return false;

			landing = topHit.point;
			return true;
		}

		private bool TryRaycastWorld(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit)
		{
			hit = default;
			if (distance <= 0f)
				return false;

			direction.Normalize();
			Vector3 rayOrigin = origin;
			float remaining = distance;
			var physicsScene = GetPhysicsScene();
			for (int i = 0; i < 4; i++)
			{
				if (physicsScene.Raycast(rayOrigin, direction, out hit, remaining, GetTraversalMask(),
					    QueryTriggerInteraction.Ignore) == false)
				{
					hit = default;
					return false;
				}

				if (hit.collider != null && IsCharacterCollider(hit.collider) == false)
					return true;

				float advance = Mathf.Min(remaining, Mathf.Max(0.02f, hit.distance + 0.05f));
				rayOrigin += direction * advance;
				remaining -= advance;
				if (remaining <= 0.02f)
					break;
			}

			hit = default;
			return false;
		}

		private NetworkedInput BuildClimbInput(Vector3 top)
		{
			float verticalDelta = top.y - transform.position.y;
			float horizontalDistanceSqr = FlatDistanceSqr(transform.position, top);
			float horizontalSnap = Mathf.Max(0.05f, ClimbMantleMaxHorizontalSnapDistance);

			if (verticalDelta <= Mathf.Max(MantleMinRise, ClimbMantleMaxSnapHeight) &&
			    horizontalDistanceSqr <= horizontalSnap * horizontalSnap)
			{
				_zombie.MantleTo(top);
				_climbCommitUntil = 0f;
				_climbingSurface = false;
				ClearNavigator();
				return default;
			}

			bool shouldClimb = verticalDelta > ClimbStopRise || horizontalDistanceSqr <= horizontalSnap * horizontalSnap;
			if (shouldClimb)
			{
				_climbCommitUntil = Time.timeSinceLevelLoad + Mathf.Max(0.05f, ClimbCommitDuration);
				WantsToClimb = true;
			}

			Vector3 flat = top - transform.position;
			flat.y = 0f;
			Vector3 direction = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.forward;
			return BuildMoveInputFromDirection(direction);
		}

		private bool IsShortcutApproachClear(Vector3 position, ZombieClimbCandidate candidate, CharacterNavigator navigator)
		{
			Vector3 approachPoint = GetShortcutApproachPoint(candidate);
			if (FlatDistanceSqr(position, approachPoint) <= 0.05f * 0.05f)
				return true;

			if (navigator == null || IsNavMeshApproachBlocked(position, approachPoint, navigator))
				return false;

			return HasWorldGeometryBlocker(position, approachPoint) == false;
		}

		private static Vector3 GetShortcutApproachPoint(ZombieClimbCandidate candidate)
		{
			float inset = Mathf.Max(0.1f, candidate.Surface.LandingInset);
			Vector3 point = candidate.ContactPoint - candidate.ClimbDirection * inset;
			point.y = candidate.RequiresClimb ? candidate.Surface.BaseY : candidate.Surface.TopY;
			return point;
		}

		private bool IsNavMeshApproachBlocked(Vector3 position, Vector3 approachPoint, CharacterNavigator navigator)
		{
			float sampleDistance = Mathf.Max(0.5f, ClimbSurfaceEngageDistance);
			int areaMask = navigator != null ? navigator.AreaMask : NavMesh.AllAreas;
			if (NavMesh.SamplePosition(position, out var startHit, sampleDistance, areaMask) == false)
				return true;
			if (NavMesh.SamplePosition(approachPoint, out var approachHit, sampleDistance, areaMask) == false)
				return true;

			return NavMesh.Raycast(startHit.position, approachHit.position, out _, areaMask);
		}

		private bool HasWorldGeometryBlocker(Vector3 position, Vector3 approachPoint)
		{
			Vector3 origin = position + Vector3.up * ShortcutBlockerProbeHeight;
			Vector3 target = approachPoint + Vector3.up * ShortcutBlockerProbeHeight;
			Vector3 direction = target - origin;
			float distance = direction.magnitude;
			if (distance < 0.05f)
				return false;

			int hitCount = GetPhysicsScene().Raycast(origin, direction / distance, _shortcutBlockerHits, distance,
				GetTraversalMask(), QueryTriggerInteraction.Ignore);
			for (int i = 0; i < hitCount; i++)
			{
				Collider hitCollider = _shortcutBlockerHits[i].collider;
				if (hitCollider == null || IsCharacterCollider(hitCollider))
					continue;

				return true;
			}

			return false;
		}

		private bool IsCharacterCollider(Collider hitCollider)
		{
			Transform hitTransform = hitCollider.transform;
			if (_zombie != null && hitTransform.IsChildOf(_zombie.transform))
				return true;

			return hitCollider.GetComponentInParent<ZombieCharacter>() != null ||
			       hitCollider.GetComponentInParent<Survivor>() != null;
		}

		private bool HasCrestedClimb(Vector3 top)
		{
			if (top.y - transform.position.y > ClimbStopRise)
				return false;

			float reach = Mathf.Max(ClimbMantleMaxSnapHeight, ClimbSurfaceEngageDistance);
			return FlatDistanceSqr(transform.position, top) <= reach * reach;
		}

		private bool HasReachedExplicitGoal(Vector3 goal, float stoppingDistance)
		{
			return FlatDistanceSqr(transform.position, goal) <= stoppingDistance * stoppingDistance &&
			       Mathf.Abs(transform.position.y - goal.y) <= Mathf.Max(0.1f, ExplicitGoalHeightTolerance);
		}

		private void ClearExplicitGoalRoute(bool preserveActiveClimb = false)
		{
			// Intentionally does NOT reset _climbCommitUntil. State transitions (Investigating →
			// Attacking when the zombie peeks over a ledge and finally sees the survivor, target
			// changes, etc.) used to reset it here; that meant the climb impulse turned off in the
			// same tick as the state transition, gravity re-engaged, and the zombie dropped back.
			// The commit expires naturally after ClimbCommitDuration, and BuildClimbInput resets it
			// explicitly after a successful mantle (so a zombie that has already landed on the
			// surface does not keep climbing the next tick).
			InvalidateClimbCandidateCache();
			if (preserveActiveClimb == false)
				_climbingSurface = false;
			ClearNavigator();
		}

		private bool TryGetStuckRandomMoveInput(out NetworkedInput input)
		{
			input = default;
			if (IsStuckElevatedCached() == false)
				return false;

			ClearExplicitGoalRoute();
			input = BuildStuckRandomMoveInput();
			return input.MoveDirection != Vector2.zero;
		}

		private bool IsStuckElevatedCached()
		{
			float now = Time.timeSinceLevelLoad;
			if (now >= _nextStuckCheckTime)
			{
				_nextStuckCheckTime = now + Mathf.Max(0.1f, StuckCheckInterval);
				_isStuckElevated = IsElevatedOffNavMesh() ||
				                   IsOnSmallElevatedNavMeshIsland() ||
				                   IsOnSmallPropSupport();
			}

			return _isStuckElevated;
		}

		private NetworkedInput BuildStuckRandomMoveInput()
		{
			float now = Time.timeSinceLevelLoad;
			if (now >= _stuckWanderEndTime)
			{
				Vector2 random = Random.insideUnitCircle.normalized;
				_stuckWanderDirection = new Vector3(random.x, 0f, random.y);

				float min = Mathf.Max(0.1f, StuckRandomWanderDurationMin);
				float max = Mathf.Max(min, StuckRandomWanderDurationMax);
				_stuckWanderEndTime = now + Random.Range(min, max);
			}

			return BuildMoveInputFromDirection(_stuckWanderDirection);
		}

		private bool IsElevatedOffNavMesh()
		{
			Vector3 position = transform.position;
			float radius = Mathf.Max(0.1f, StuckSampleRadius);
			if (NavMesh.SamplePosition(position, out var hit, radius, NavMesh.AllAreas) == false)
				return true;

			return position.y - hit.position.y > Mathf.Max(0.1f, StuckMinHeightAboveNavMesh);
		}

		private bool IsOnSmallPropSupport()
		{
			if (TryGetRoadDirectSupportCollider(transform.position, out Collider support) == false)
				return false;

			Bounds bounds = support.bounds;
			float maxHorizontalSize = Mathf.Max(bounds.size.x, bounds.size.z);
			float allowedSize = Mathf.Max(2f, StuckSmallIslandRadius * 2f);
			if (maxHorizontalSize > allowedSize)
				return false;

			float topTolerance = Mathf.Max(0.15f, StuckMinHeightAboveNavMesh);
			return transform.position.y >= bounds.max.y - topTolerance;
		}

		private bool TryGetDirectEnemy(out KnownEnemyInfo enemy)
		{
			enemy = default;
			return _zombie.Sensor != null &&
			       _zombie.Sensor.TryGetClosestDirectEnemy(out enemy) &&
			       IsTargetAlive(enemy.Object);
		}

		private bool ShouldIgnoreInvestigationStimulus()
		{
			return (State == EZombieAIState.Attacking || State == EZombieAIState.Hunting) &&
			       IsTargetAlive(_target);
		}

		private bool ShouldHoldMeleePosition(NetworkObject target, Vector3 targetPosition)
		{
			if (IsTargetAlive(target) == false || _zombie == null)
				return false;
			if (ShouldPrioritizeClimbTowardTarget(targetPosition))
				return false;

			float range = Mathf.Max(0.1f, _zombie.Stats.AttackRange);
			return (targetPosition - transform.position).sqrMagnitude <= range * range;
		}

		private bool ShouldPrioritizeClimbTowardTarget(Vector3 targetPosition)
		{
			float verticalGap = targetPosition.y - transform.position.y;
			if (verticalGap <= ClimbStopRise)
				return false;
			if (_climbingSurface || IsClimbing)
				return true;

			float range = _zombie != null ? Mathf.Max(0.1f, _zombie.Stats.AttackRange) : 0.1f;
			return FlatDistanceSqr(transform.position, targetPosition) <= range * range;
		}

		private bool ShouldPreserveActiveClimbForTarget(Vector3 targetPosition)
		{
			if (_climbingSurface == false)
				return false;

			float attackRange = _zombie != null ? Mathf.Max(0.1f, _zombie.Stats.AttackRange) : 0.1f;
			float maxFlatDistance = Mathf.Max(attackRange, ClimbSurfaceEngageDistance, ClimbMantleMaxSnapHeight) + 1f;
			float maxFlatDistanceSqr = maxFlatDistance * maxFlatDistance;
			return FlatDistanceSqr(targetPosition, _climbLanding) <= maxFlatDistanceSqr ||
			       FlatDistanceSqr(targetPosition, transform.position) <= maxFlatDistanceSqr;
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
			float effectiveStoppingDistance = Mathf.Max(0f, stoppingDistance);
			var navigator = _zombie.Navigator;

			if (navigator != null)
			{
				navigator.Tick(currentPosition);
				if (navigator.IsDestinationReached)
					return default;

				if (navigator.TryGetSteeringTarget(currentPosition, out Vector3 pathTarget))
				{
					steeringTarget = pathTarget;
					effectiveStoppingDistance = GetPathSteeringStoppingDistance(navigator, stoppingDistance);
				}
				else if (allowDirectFallback == false || FlatDistanceSqr(currentPosition, destination) > DirectMoveDistance * DirectMoveDistance)
					return default;
			}

			Vector3 toTarget = steeringTarget - currentPosition;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude <= effectiveStoppingDistance * effectiveStoppingDistance)
				return BuildLookInput(destination);

			return BuildMoveInputFromDirection(toTarget.normalized);
		}

		private static float GetPathSteeringStoppingDistance(CharacterNavigator navigator, float desiredStoppingDistance)
		{
			if (navigator == null)
				return Mathf.Max(0f, desiredStoppingDistance);

			float cornerReachDistance = Mathf.Max(0.01f, navigator.CornerReachDistance);
			float desired = Mathf.Max(0.01f, desiredStoppingDistance);

			// Steering corners are not final goals. If the AI stops farther from a corner than the
			// navigator's corner-advance radius, it can wait forever until another character bumps it.
			return Mathf.Min(desired, cornerReachDistance * 0.5f);
		}

		private NetworkedInput BuildMoveInputFromDirection(Vector3 worldDirection)
		{
			var input = BuildLookInput(transform.position + worldDirection);
			float currentYaw = GetCurrentYaw() + input.LookRotationDelta.y;
			input.MoveDirection = GetLocalMoveDirection(worldDirection, currentYaw);
			return input;
		}

		private NetworkedInput BuildDirectGoalMoveInput(Vector3 goal)
		{
			Vector3 toGoal = goal - transform.position;
			toGoal.y = 0f;
			if (toGoal.sqrMagnitude < 0.001f)
				return BuildLookInput(goal);

			return BuildMoveInputFromDirection(toGoal.normalized);
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

		// Picks a random player that still owns at least one alive survivor. Choosing the player (instead of
		// the globally nearest survivor) shares overtime pressure evenly between players regardless of how their
		// survivors are spread across the map, so hiding in a corner no longer offloads zombies onto better-
		// positioned teams. Neutral survivors are excluded from this global pick on purpose; they are still
		// attacked through normal sensing (see UpdateHunting). The zombie then commits to this player.
		private bool TryPickRandomHuntPlayer(out PlayerRef player)
		{
			player = default;
			HuntPlayerBuffer.Clear();

			var sensors = CharacterSensor.ActiveSensors;
			for (int i = sensors.Count - 1; i >= 0; i--)
			{
				var sensor = sensors[i];
				if (sensor == null)
				{
					sensors.RemoveAt(i);
					continue;
				}

				var survivor = sensor.Survivor;
				if (IsHuntableSurvivor(survivor) == false)
					continue;

				PlayerRef owner = survivor.OwnerRef;
				if (HuntPlayerBuffer.Contains(owner) == false)
					HuntPlayerBuffer.Add(owner);
			}

			if (HuntPlayerBuffer.Count == 0)
				return false;

			player = HuntPlayerBuffer[Random.Range(0, HuntPlayerBuffer.Count)];
			return true;
		}

		private static bool PlayerHasAliveSurvivor(PlayerRef owner)
		{
			var sensors = CharacterSensor.ActiveSensors;
			for (int i = 0; i < sensors.Count; i++)
			{
				var sensor = sensors[i];
				if (sensor == null)
					continue;

				var survivor = sensor.Survivor;
				if (IsHuntableSurvivor(survivor) && survivor.OwnerRef == owner)
					return true;
			}

			return false;
		}

		private NetworkObject FindClosestSurvivorForOwner(PlayerRef owner)
		{
			NetworkObject closest = null;
			float closestDistanceSqr = float.MaxValue;
			Vector3 origin = transform.position;

			var sensors = CharacterSensor.ActiveSensors;
			for (int i = 0; i < sensors.Count; i++)
			{
				var sensor = sensors[i];
				if (sensor == null)
					continue;

				var survivor = sensor.Survivor;
				if (IsHuntableSurvivor(survivor) == false || survivor.OwnerRef != owner)
					continue;

				float distanceSqr = FlatDistanceSqr(origin, survivor.transform.position);
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				closest = survivor.Object;
			}

			return closest;
		}

		private static bool IsHuntableSurvivor(Survivor survivor)
		{
			return survivor != null &&
			       survivor.Health != null && survivor.Health.IsAlive &&
			       CharacterFactionUtility.IsPlayerOwnedSurvivor(survivor);
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

		private Vector3 GetFlatDirectionTo(Vector3 target, Vector3 fallback)
		{
			Vector3 direction = target - transform.position;
			direction.y = 0f;
			if (direction.sqrMagnitude > 0.001f)
				return direction.normalized;

			return FlattenDirection(fallback);
		}

		private static Vector3 FlattenDirection(Vector3 direction)
		{
			direction.y = 0f;
			return direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.zero;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static float FlatDistance(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).magnitude;
		}
	}
}
