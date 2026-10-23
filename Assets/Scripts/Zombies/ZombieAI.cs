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
		// Designer-facing knobs. Everything else that used to be a serialized field here is now a constant below:
		// those values were internal mechanics (probe distances, commit timers, stuck-recovery intervals, …) that
		// were tuned once and add nothing as per-prefab settings. The constants carry the tuned prefab values.
		[Header("Idle Wander")]
		public float IdleWanderIntervalMin = 3f;
		public float IdleWanderIntervalMax = 7f;
		public float IdleWanderRadius = 20f;
		public float IdleWanderAvoidZombieRadius = 16f;

		[Header("Waypoint Wander")]
		// Idle wander candidates that land near an authored PatrolWaypoint snap to it, pulling idle zombies into
		// buildings and other points of interest the flat random sampler never discovers. Crowding is the
		// anti-black-hole boundary: a waypoint whose surroundings already hold MaxZombiesNearWaypoint zombies
		// stops attracting, and a zombie standing inside such a crowd stops snapping entirely for the sweep —
		// the existing away-from-zombies wander bias then walks it out of the building.
		public bool UseWaypointWander = true;
		public float WanderWaypointSnapRadius = 6f;
		public float WaypointCrowdRadius = 10f;
		public int MaxZombiesNearWaypoint = 4;

		[Header("Targeting")]
		[Tooltip("Attack alert refresh interval. Target observations refresh every tick without restarting navigation.")]
		public float AttackRetargetInterval = 1f;
		public float HuntingRetargetInterval = 6f;
		[Range(0f, 1f)]
		[Tooltip("While the current target is still directly seen/felt, another visible survivor only steals the zombie's attention when its distance is below this fraction of the current target's distance. 0.5 = must be at least twice as close. 1 = old behavior (closest always wins).")]
		public float TargetSwitchDistanceFactor = 0.5f;

		[Header("Movement")]
		// Also the arrival distance for explicit hunt/attack goals (the two were always tuned to the same value).
		public float StoppingDistance = 0.66f;
		public float MaxYawDegreesPerTick = 6f;

		[Header("Climbing")]
		// V6 reactive traversal: climbing is a REACTION to touching a wall while moving at the goal in direct mode
		// (pathfinding cannot get closer), never a decision made from landing probes or reachability verdicts.
		// Registered climb surfaces (generated terrain ledge faces / authored shortcut faces) stay as deterministic
		// shortcut data. See Docs/ZombieAI.md.
		[FormerlySerializedAs("CanClimbUnreachableTargets")]
		[FormerlySerializedAs("CanClimbDirectGoals")]
		public bool UseClimbSurfaces = true;
		public bool UseTerrainShortcutClimbs = true;
		// Reactive wall climbs: in direct mode, a wall touched while moving toward the goal is climbed, capped at
		// max(goal height + AboveGoalTolerance, start + VaultMaxHeight). Successor of pursuit/rescue/chase climbs.
		[FormerlySerializedAs("UseRescueClimbs")]
		[FormerlySerializedAs("UseClimbPursuit")]
		public bool UseWallClimbs = true;
		public float ClimbSpeedMultiplier = 0.45f;
		public float ClimbMantleMaxSnapHeight = 1.75f;

		[Header("Debug")]
		// Logs this zombie's explicit-goal routing gates twice a second while it has an explicit goal. Enable on a
		// misbehaving zombie in the editor to see which traversal branch owns it and why.
		public bool LogTraversalDebug;

		// --- Internal tuning constants (previously inspector fields; values are the tuned prefab values). ---

		// Idle wander internals.
		private const float InitialIdleWanderDelayMax = 2f;
		private const float IdleWanderRetryDelayMin = 1f;
		private const float IdleWanderRetryDelayMax = 2f;
		private const int IdleWanderCandidateCount = 3;
		private const float IdleWanderNavMeshSampleDistance = 8f;
		private const float IdleWanderStuckTimeout = 3f;
		private const float IdleWanderStuckMinProgress = 0.25f;

		// Targeting internals.
		private const float HuntingRetargetIntervalJitter = 2f;
		private const float HuntingInitialRetargetStaggerMax = 3f;
		// After getting an explicit investigation point, new noise/impact/alert stimuli are ignored for this many
		// seconds so two shooters cannot ping-pong the zombie. Within InvestigationRetriggerDistance of the point
		// the commit timer is bypassed (the point is spent). Seeing a survivor always overrides instantly.
		private const float InvestigationCommitDuration = 5f;
		private const float InvestigationRetriggerDistance = 3f;
		// Give-up: an investigation that makes no flat+vertical progress toward its point for this long (while not
		// actively climbing) is abandoned back to Idle wandering. This is the pre-overtime "return to wandering"
		// guarantee for unreachable points; overtime hunting has no investigation path and never gives up.
		private const float InvestigationGiveUpTimeout = 10f;
		private const float InvestigationMinProgress = 0.25f;

		// Movement internals.
		private const float DirectMoveDistance = 0.75f;
		private const float ExplicitGoalHeightTolerance = 0.75f;

		// Climb execution internals.
		private const float ClimbSurfaceEngageDistance = 1.25f;
		private const float ClimbCommitDuration = 0.75f;
		private const float ClimbDirectApproachDistance = 18f;
		private const float ExplicitGoalClimbRefreshInterval = 0.2f;
		private const float ClimbRouteSideTolerance = 1.6f;
		private const float ClimbMinRise = 0.75f;
		private const float ClimbMantleMaxHorizontalSnapDistance = 1.5f;
		// A committed climb is abandoned if it makes no upward progress for this long (genuinely stuck/blocked). A
		// slow-but-rising climb keeps going — this is NOT a fixed clock, so a tall ledge that takes several seconds
		// to scale is not cut off mid-climb (which caused rise-then-drop oscillation).
		private const float ClimbStuckTimeout = 1.5f;
		// Absolute safety cap on a single committed climb, however much progress it is making.
		private const float ClimbMaxDuration = 15f;
		// After a climb gives up without cresting, do not re-engage a climb for this long. Stops a zombie that
		// cannot complete a climb from oscillating (rise, drop, re-engage) on the same spot.
		private const float ClimbCooldown = 2f;

		// Reactive wall-climb internals (V6). The wall-touch ray is a physical contact test, not a landing search:
		// there is deliberately NO probing for where a climb will end. The climb rises until it caps (roof rule),
		// crests (wall ray clears -> one crest probe -> mantle), stalls (progress timeout), or times out.
		private const float WallProbeHeight = 0.55f;
		private const float WallProbeDistance = 0.6f;
		// Walls only: reject touch hits on ramps/slopes (normal.y >= this is walkable-ish ground, not a wall) and
		// grazing hits on walls being walked past (facing dot below threshold).
		private const float WallMaxNormalY = 0.45f;
		private const float WallMinFacingDot = 0.5f;
		// The roof rule + small-vault allowance in one cap, computed at climb start:
		//   capY = max(goal.y + AboveGoalTolerance, climbStartFeetY + VaultMaxHeight)
		// Survivor on a roof pulls the cap above the roof lip (full ascent); a survivor inside floor 2 caps climbs
		// at window height (never the roof); a same-level goal beyond a car/fence gets the small vault; a taller
		// building face toward a same-level goal caps out at VaultMaxHeight and can never be scaled.
		private const float AboveGoalTolerance = 1.5f;
		private const float VaultMaxHeight = 2.25f;
		// Path mode normally never climbs (NavMesh routes are preferred by contract), with one exception: within
		// this flat distance of the goal, a LOW obstacle (wall ray hits, parallel ray at vault height misses)
		// blocking the direct line is vaulted instead of pathed around (hop the car between you and the survivor).
		private const float VaultTriggerMaxDistance = 6f;
		// Crest: when the wall-touch ray stops hitting mid-climb, one forward-down probe finds the surface just
		// crested and mantles onto it. Landing must be standable (normal.y >= CrestLandingMinNormalY).
		private const float CrestProbeForwardDistance = 0.6f;
		private const float CrestProbeDownDistance = 1.5f;
		private const float CrestLandingMinNormalY = 0.45f;
		// Jam reflex — the sole never-still safety net: a nonzero move input producing ~zero real velocity for
		// JamTimeout arms a short random sidestep. Raw physical blockage detection, deliberately goal-agnostic
		// (goal-progress trackers are banned by design), never reset by goal/state changes.
		private const float JamVelocityThreshold = 0.15f;
		private const float JamTimeout = 0.75f;
		// Down-ray length used to find the support collider under a position (used by idle stuck recovery).
		private const float SupportProbeDistance = 6f;
		private const float StuckSmallIslandRadius = 4f;

		// Alert internals.
		private const int MaxAlertRecipients = 16;
		private const float AlertCooldown = 2f;

		// Off-NavMesh stuck-recovery internals. The expensive island-connectivity verification
		// (IsOnSmallElevatedNavMeshIsland) is split from the cheap off-mesh/prop checks: it runs only when the
		// cheap checks are inconclusive AND the navigator shows evidence of failure, and then no more often than
		// the jittered ElevatedIslandCheckInterval (kept >= StuckCheckInterval).
		private const float StuckSampleRadius = 0.6f;
		private const float StuckMinHeightAboveNavMesh = 0.6f;
		private const float StuckCheckInterval = 0.5f;
		private const float ElevatedIslandCheckInterval = 1.5f;
		private const float StuckRandomWanderDurationMin = 1f;
		private const float StuckRandomWanderDurationMax = 2.5f;

		public EZombieAIState State { get; private set; }
		public float LastAttackTime { get; private set; } = -1f;
		public bool WantsToClimb { get; private set; }
		public bool IsClimbing => WantsToClimb || Time.timeSinceLevelLoad < _climbCommitUntil;

		private ZombieCharacter _zombie;
		private NetworkObject _target;
		private PlayerRef _huntPlayer;
		private bool _hasHuntPlayer;
		private Vector3 _investigationTarget;
		// When the current investigation point was accepted; drives the stimulus re-trigger cadence.
		private float _investigationStartTime;
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
		// Set while the zombie is in a reactive wall climb (V6). No landing is known in advance: the climb ends by
		// cap (_wallClimbCapY, the roof rule), crest (wall ray clears -> crest probe -> mantle), stall, or timeout.
		private bool _climbingWall;
		private float _wallClimbCapY;
		private float _climbUntil;
		private float _climbStuckUntil;
		private float _climbHighestY;
		private float _climbCooldownUntil;
		private float _nextStuckCheckTime;
		private bool _isStuckElevated;
		private float _nextElevatedIslandCheckTime;
		private bool _islandStuckResult;
		private Vector3 _stuckWanderDirection;
		private float _stuckWanderEndTime;
		private NavMeshPath _scratchNavMeshPath;
		private bool _hasCachedClimbDecision;
		private bool _cachedClimbCandidateFound;
		private ZombieClimbCandidate _cachedClimbCandidate;
		private Vector3 _cachedClimbGoal;
		private Vector3 _cachedClimbPosition;
		private float _nextClimbCandidateRefreshTime;
		private bool _hasIdleWanderProgressPosition;
		private Vector3 _idleWanderProgressPosition;
		private float _nextIdleWanderProgressCheckTime;
		private float _idleWanderStuckSince = -1f;
		// Jam recovery survives goal/state changes and re-baselines after a gap in movement ticks.
		private float _jamStuckSince = -1f;
		private float _nextJamCheckTime;
		private float _jamSidestepUntil;
		// Path-loss transient filter: AdvanceCorner drops HasPath when a midpoint path's last corner is consumed;
		// one forced repath bridges the <=0.35 s window that would otherwise read as direct mode mid-route.
		private bool _hadPathLastTick;
		// Investigation give-up state (pre-overtime return-to-wandering guarantee).
		private float _investigationBestDistance;
		private Vector3 _investigationProgressPosition;
		private float _investigationNoProgressSince = -1f;
		private float _nextInvestigationProgressCheckTime;
		private float _nextTraversalDebugLogTime;

		// Smallest rise that still counts as a mantle (floors the snap height so Mathf.Max never returns 0).
		private const float MantleMinRise = 0.08f;
		private const float ClimbStopRise = 0.5f;
		// Emergency-only displacement thresholds for the climb-candidate cache. ExplicitGoalClimbRefreshInterval is
		// authoritative: at overtime speed a zombie travels well over 0.75 m within one refresh interval, so the old
		// 0.75 m thresholds forced an extra movement-driven refresh between every interval (roughly doubling refresh
		// frequency to ~10/sec). These are now large enough that ordinary hunting movement never triggers an early
		// refresh; only a discontinuous jump (target teleport, large reposition) does.
		private const float ClimbCandidateGoalRefreshDistance = 3f;
		private const float ClimbCandidatePositionRefreshDistance = 3f;
		private const float ShortcutBlockerProbeHeight = 0.8f;
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
			if (_climbingSurface || _climbingWall || ShouldIgnoreInvestigationStimulus())
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
					ResetIdleWanderProgress();
					ScheduleNextIdleWander();
					return default;
				}

				NetworkedInput input = BuildMoveInput(navigator.Destination, StoppingDistance, true);
				if (IsIdleWanderDestinationStuck(position))
				{
					navigator.ClearDestination();
					ResetIdleWanderProgress();
					ScheduleIdleWanderRetry();
					return default;
				}

				return input;
			}

			if (Time.timeSinceLevelLoad >= _nextIdleWanderTime)
			{
				if (TryChooseIdleWanderPoint(out Vector3 wanderPoint))
				{
					navigator?.SetDestination(wanderPoint);
					BeginIdleWanderProgress(position);
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

			var navigator = _zombie != null ? _zombie.Navigator : null;

			// Reached: the height-aware explicit check, or standing flat-on-top of the point with the navigator
			// reporting arrival at the nearest navigable spot. Bullet-impact stimuli aim at the shooter's MUZZLE
			// (~1.5 m above ground), which the height gate alone can never accept — without this relaxation a
			// zombie stands exactly on the stimulus forever, and the investigation commitment keeps it deaf the
			// whole time.
			bool flatWithinStopping =
				FlatDistanceSqr(transform.position, _investigationTarget) <= StoppingDistance * StoppingDistance;
			if (HasReachedExplicitGoal(_investigationTarget, StoppingDistance) ||
			    (flatWithinStopping && navigator != null && navigator.IsDestinationReached))
			{
				ReturnToIdle();
				return default;
			}

			// Give-up: an unreachable point (roof noise with no climbable approach, sealed interior) must not hold
			// the zombie forever — pre-overtime it returns to Idle wandering instead.
			if (ShouldGiveUpInvestigation())
			{
				ReturnToIdle();
				return default;
			}

			return BuildExplicitGoalMoveInput(_investigationTarget, StoppingDistance);
		}

		private bool ShouldGiveUpInvestigation()
		{
			float now = Time.timeSinceLevelLoad;
			if (now < _nextInvestigationProgressCheckTime)
				return false;

			_nextInvestigationProgressCheckTime = now + Mathf.Max(0.1f, StuckCheckInterval);

			// Deliberately NOT paused while climbing: under V6's capped wall climbs a zombie besieging an
			// unreachable interior point is climbing most of the time (the fall/shuffle/climb yo-yo), and pausing
			// the clock during climbs would make this give-up never fire. A productive climb resets the timer
			// through real distance improvement below; a stalled climb dies in ClimbStuckTimeout on its own; only
			// the capped yo-yo accumulates the timeout — exactly the case that must give up.
			float distance = GetGoalProgressDistance(_investigationTarget);
			float minProgress = Mathf.Max(0.01f, InvestigationMinProgress);
			var navigator = _zombie.Navigator;
			// Walking a route may lead away from the goal (stairs or an indoor detour).
			// Climbing and jam sidesteps still need real goal progress, so wall yo-yos can time out.
			bool followingPath = navigator != null && navigator.HasPath &&
				_climbingSurface == false && _climbingWall == false && now >= _jamSidestepUntil;
			bool pathProgress = followingPath &&
				(transform.position - _investigationProgressPosition).sqrMagnitude >= minProgress * minProgress;
			if (followingPath == false || pathProgress)
				_investigationProgressPosition = transform.position;
			if (pathProgress || distance <= _investigationBestDistance - minProgress)
			{
				_investigationBestDistance = Mathf.Min(_investigationBestDistance, distance);
				_investigationNoProgressSince = -1f;
				return false;
			}

			if (_investigationNoProgressSince < 0f)
			{
				_investigationNoProgressSince = now;
				return false;
			}

			return now - _investigationNoProgressSince >= Mathf.Max(0.5f, InvestigationGiveUpTimeout);
		}

		private float GetGoalProgressDistance(Vector3 goal)
		{
			return FlatDistance(transform.position, goal) + Mathf.Abs(transform.position.y - goal.y);
		}

		private NetworkedInput UpdateAttacking()
		{
			if (IsTargetAlive(_target) == false)
			{
				if (TryGetDirectEnemy(out var enemy))
				{
					StartAttack(enemy);
				}
				else
				{
					ReturnToIdle();
					return UpdateIdle();
				}
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
				_nextRetargetTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, AttackRetargetInterval);
				AlertNearbyZombies(_lastKnownTargetPosition, _lastStimulusTick);
			}

			if (_target == null)
				return default;

			Vector3 targetPosition = _lastKnownTargetPosition;
			bool shouldPrioritizeClimb = ShouldPrioritizeClimbTowardTarget(targetPosition);
			if (_zombie.TryAttack(_target, out bool canReachTarget))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				if (shouldPrioritizeClimb == false)
					return BuildLookInput(targetPosition);
			}
			if (shouldPrioritizeClimb == false && canReachTarget)
				return BuildLookInput(targetPosition);

			return BuildExplicitGoalMoveInput(targetPosition, StoppingDistance, dynamicTarget: true);
		}

		private NetworkedInput UpdateHunting()
		{
			// A directly sensed enemy always overrides the committed global target. The global picker
			// deliberately ignores neutral survivors so overtime pressure is shared evenly between
			// players, but a zombie that senses any survivor (neutral included) on the way to its
			// target should still attack whoever it ran into. This is temporary: the committed player
			// is left untouched. After sight is lost, a living local target remains until the next hunt refresh.
			// Sticky: while the current target is itself directly sensed, another visible survivor only
			// takes over when dramatically closer (TargetSwitchDistanceFactor).
			if (TryGetStickyDirectEnemy(out var sensed))
				SetHuntTarget(sensed.Object);
			else
				EnsureCommittedHuntTarget();

			if (_target == null)
				return default;

			Vector3 targetPosition = _target.transform.position;
			bool shouldPrioritizeClimb = ShouldPrioritizeClimbTowardTarget(targetPosition);
			if (_zombie.TryAttack(_target, out bool canReachTarget))
			{
				LastAttackTime = Time.timeSinceLevelLoad;
				if (shouldPrioritizeClimb == false)
					return BuildLookInput(targetPosition);
			}
			if (shouldPrioritizeClimb == false && canReachTarget)
				return BuildLookInput(targetPosition);

			return BuildExplicitGoalMoveInput(targetPosition, StoppingDistance, dynamicTarget: true);
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
				// Keep heading to the current survivor, including a local distraction, until the next refresh.
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
			if (TryGetStickyDirectEnemy(out var enemy) == false)
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
			_investigationStartTime = Time.timeSinceLevelLoad;
			// A fresh (or re-triggered) point restarts the give-up budget.
			_investigationBestDistance = GetGoalProgressDistance(target);
			_investigationProgressPosition = transform.position;
			_investigationNoProgressSince = -1f;
			_nextInvestigationProgressCheckTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, StuckCheckInterval);
			ClearExplicitGoalRoute(ShouldPreserveActiveClimbForTarget(target));

			if (fromAlert == false)
				AlertNearbyZombies(target, stimulusTick);
		}

		private NetworkedInput BuildExplicitGoalMoveInput(Vector3 goal, float stoppingDistance, bool dynamicTarget = false)
		{
			var navigator = _zombie.Navigator;
			if (navigator == null)
				return BuildLookInput(goal);

			if (LogTraversalDebug && Time.timeSinceLevelLoad >= _nextTraversalDebugLogTime)
			{
				_nextTraversalDebugLogTime = Time.timeSinceLevelLoad + 0.5f;
				Debug.Log(
					$"[ZombieAI] {name} state={State} flat={FlatDistance(transform.position, goal):F2} " +
					$"dy={goal.y - transform.position.y:F2} " +
					$"climbSurface={_climbingSurface} climbWall={_climbingWall} capY={_wallClimbCapY:F2} " +
					$"climbCooldown={Mathf.Max(0f, _climbCooldownUntil - Time.timeSinceLevelLoad):F1} " +
					$"jam={(_jamStuckSince >= 0f ? Time.timeSinceLevelLoad - _jamStuckSince : 0f):F1} " +
					$"sidestep={Mathf.Max(0f, _jamSidestepUntil - Time.timeSinceLevelLoad):F1} " +
					$"navPath={navigator.HasPath} navReached={navigator.IsDestinationReached} " +
					$"navUnreachable={navigator.IsDestinationUnreachable}",
					this);
			}

			// Combat stops only when melee can reach the target; distance alone can stop us across a wall.
			if (dynamicTarget == false && HasReachedExplicitGoal(goal, stoppingDistance))
			{
				_climbingSurface = false;
				_climbingWall = false;
				return BuildLookInput(goal);
			}

			// Active climbs (surface or wall) are finished before anything else gets a vote; the navigator stays
			// cleared for their duration.
			if (TryContinueActiveClimb(goal, out NetworkedInput climbInput))
				return climbInput;

			// A live hunt/attack target moves every tick; SetDynamicDestination updates the goal in place and lets
			// the navigator repath on its interval instead of recalculating the instant the target drifts. A fixed
			// investigation point uses the normal SetDestination.
			if (dynamicTarget)
				navigator.SetDynamicDestination(goal);
			else
				navigator.SetDestination(goal);
			navigator.Tick(transform.position);

			// Path-loss transient: AdvanceCorner drops HasPath when a midpoint path's last corner is consumed and
			// the rebuild otherwise waits for the next repath (<= 0.35 s). Without bridging it, that window reads
			// as "pathfinding cannot get closer" and can commit a spurious wall climb mid-route at every midpoint
			// boundary. An off-mesh zombie never had a path, so this cannot loop.
			if (_hadPathLastTick && navigator.HasPath == false &&
			    navigator.IsDestinationReached == false && navigator.IsDestinationUnreachable == false)
			{
				navigator.ForceRepath();
				navigator.Tick(transform.position);
			}
			_hadPathLastTick = navigator.HasPath;

			// Deterministic shortcut data keeps first claim: generated terrain ledge faces (and authored shortcut
			// faces) are climbed/dropped through the registry exactly as before.
			if (TryGetCachedDirectClimbCandidate(goal, navigator, out ZombieClimbCandidate climbCandidate))
				return ApplyJamReflex(BuildClimbCandidateMoveInput(goal, climbCandidate));

			// Path mode: pathfinding can still get closer — walk the corners. One exception: a LOW obstacle
			// blocking the direct line to a goal at most VaultTriggerMaxDistance away is hopped (the car or fence
			// between zombie and survivor) instead of pathed around.
			if (IsPathModeUsable(navigator, goal, stoppingDistance, out NetworkedInput pathInput))
			{
				if (FlatDistanceSqr(transform.position, goal) <= VaultTriggerMaxDistance * VaultTriggerMaxDistance &&
				    TryStartWallClimb(goal, true, out NetworkedInput vaultInput))
					return vaultInput;

				return ApplyJamReflex(pathInput);
			}

			// Direct mode: pathfinding cannot get closer. Always move toward the goal; a wall touched on the way
			// is climbed, capped by the roof rule. This is the whole V6 traversal answer for props, roofs, and
			// everything else the NavMesh cannot represent.
			if (TryStartWallClimb(goal, false, out NetworkedInput wallClimbInput))
				return wallClimbInput;

			NetworkedInput directInput = BuildDirectGoalMoveInput(goal);
			if (directInput.MoveDirection == Vector2.zero)
			{
				// Goal directly overhead/below with no flat direction: random-walk to shift position.
				return BuildStuckRandomMoveInput();
			}

			return ApplyJamReflex(directInput);
		}

		// "Pathfinding cannot get closer" — derived purely from existing navigator signals, zero NavMesh queries:
		// unreachable (<= 12 m flat with no complete path), no path at all (off-mesh start, reached-short — the
		// navigator clears its path once it arrives at the nearest navigable point), or flat-arrived at a steering
		// corner that 3D corner advancement can never consume (the path runs underneath the prop the zombie stands
		// on) while genuinely off the mesh. When path mode IS usable, this also produces its move input.
		private bool IsPathModeUsable(CharacterNavigator navigator, Vector3 goal, float stoppingDistance, out NetworkedInput pathInput)
		{
			pathInput = default;

			if (navigator.IsDestinationUnreachable)
				return false;
			if (navigator.HasPath == false)
				return false;

			if (navigator.TryGetSteeringTarget(transform.position, out Vector3 steer) == false)
				return false;

			float steerStoppingDistance = GetPathSteeringStoppingDistance(navigator, stoppingDistance);
			Vector3 flatToSteer = steer - transform.position;
			flatToSteer.y = 0f;
			if (flatToSteer.sqrMagnitude <= steerStoppingDistance * steerStoppingDistance)
			{
				// Flat-arrived at a corner. Far below us while we stand off the mesh means the corner can never
				// 3D-advance (zombie on a prop above its own path) — pathfinding cannot get closer from here.
				if (transform.position.y - steer.y > Mathf.Max(0.1f, StuckMinHeightAboveNavMesh) &&
				    IsElevatedOffNavMesh())
					return false;

				// On-mesh flat-arrival at a corner is a transient (the next repath advances it): hold with a look
				// input rather than fighting the navigator.
				pathInput = BuildLookInput(goal);
				return true;
			}

			pathInput = BuildMoveInputFromDirection(flatToSteer.normalized);
			return true;
		}

		// Reactive wall climb trigger — the V6 traversal core. One short touch ray in the movement direction; a
		// hit that is a real wall (steep, facing us) while we are meaningfully below the cap starts a committed
		// climb. There is deliberately NO landing search and NO reachability verdict: every failure of this ray
		// degrades into movement (walk, fall, sidestep), never a freeze. requireLowObstacle is path mode's vault
		// variant: a second parallel ray at vault height must MISS (the obstacle is low enough to hop over), so a
		// tall building wall is never climbed while a walking route to the goal exists.
		private bool TryStartWallClimb(Vector3 goal, bool requireLowObstacle, out NetworkedInput input)
		{
			input = default;
			if (UseWallClimbs == false)
				return false;
			if (_climbingSurface || _climbingWall || Time.timeSinceLevelLoad < _climbCooldownUntil)
				return false;

			Vector3 direction = GetFlatDirectionTo(goal, transform.forward);
			if (direction.sqrMagnitude <= 0.001f)
				return false;

			// The one cap formula: the roof rule (never rise past the goal's height allowance — a survivor inside
			// floor 2 caps climbs at window height, never the roof) plus the small-vault allowance (a same-level
			// goal beyond a car/fence still gets the hop, but a building face toward it caps out harmlessly).
			float capY = Mathf.Max(goal.y + AboveGoalTolerance, transform.position.y + VaultMaxHeight);
			if (transform.position.y >= capY - ClimbStopRise)
				return false;

			Vector3 origin = transform.position + Vector3.up * WallProbeHeight;
			if (TryRaycastWorld(origin, direction, WallProbeDistance, out RaycastHit wallHit) == false)
				return false;
			// Walls only: a walkable-ish surface (ramp, slope, rubble) is the navigator's business.
			if (wallHit.normal.y >= WallMaxNormalY)
				return false;
			// And only walls actually blocking us: grazing a face being walked past must not yank us up it.
			Vector3 flatNormal = wallHit.normal;
			flatNormal.y = 0f;
			if (flatNormal.sqrMagnitude > 0.0001f &&
			    Vector3.Dot(-flatNormal.normalized, direction) < WallMinFacingDot)
				return false;

			if (requireLowObstacle &&
			    TryRaycastWorld(transform.position + Vector3.up * (VaultMaxHeight + 0.1f), direction,
				    WallProbeDistance, out _))
				return false;

			float now = Time.timeSinceLevelLoad;
			_climbingWall = true;
			_wallClimbCapY = capY;
			_climbHighestY = transform.position.y;
			_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			_climbUntil = now + GetClimbDurationCap(capY - transform.position.y);
			ClearNavigator();
			input = BuildWallClimbInput(goal);
			return true;
		}

		// Drives an active wall climb: flat input toward the goal (pressing into the face; any lateral component
		// slides the body along the wall toward under-the-goal) while the climb impulse rises. Terminates on the
		// roof cap, on crest (touch ray clears -> crest probe -> mantle), on stalled rise, or on the duration cap.
		// Every exit that is not a mantle sets ClimbCooldown, so the fall / jam-sidestep / re-climb loop shifts the
		// approach spot instead of vibrating at the cap band.
		private NetworkedInput BuildWallClimbInput(Vector3 goal)
		{
			float now = Time.timeSinceLevelLoad;

			if (transform.position.y > _climbHighestY + 0.02f)
			{
				_climbHighestY = transform.position.y;
				_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			}

			// Roof rule: never rise above the cap computed at climb start.
			if (transform.position.y >= _wallClimbCapY)
			{
				EndWallClimb(true);
				return BuildDirectGoalMoveInput(goal);
			}

			// Stalled (no upward progress) or over the rise-scaled duration cap.
			if (now >= _climbStuckUntil || now >= _climbUntil)
			{
				EndWallClimb(true);
				return BuildDirectGoalMoveInput(goal);
			}

			Vector3 direction = GetFlatDirectionTo(goal, transform.forward);
			if (direction.sqrMagnitude <= 0.001f)
				direction = FlattenDirection(transform.forward);

			Vector3 origin = transform.position + Vector3.up * WallProbeHeight;
			bool wallAhead = TryRaycastWorld(origin, direction, WallProbeDistance, out RaycastHit wallHit) &&
			                 wallHit.normal.y < WallMaxNormalY;
			if (wallAhead == false)
			{
				// The wall ended at chest height: we are at the lip (or an opening). One probe just past it finds
				// the surface we crested — mantle onto it (the ONLY remaining landing probe, taken from a spot we
				// have physically reached, so it cannot start inside hollow shells the way ground-based landing
				// inference did). A deep opening with no floor keeps rising through the commit grace, then drops.
				if (TryProbeCrestLanding(direction, out Vector3 landing))
				{
					EndWallClimb(false);
					_climbCommitUntil = 0f;
					_zombie.MantleTo(landing);
					return default;
				}

				if (now >= _climbCommitUntil)
				{
					EndWallClimb(true);
					return BuildDirectGoalMoveInput(goal);
				}

				WantsToClimb = true;
				return BuildMoveInputFromDirection(direction);
			}

			_climbCommitUntil = now + Mathf.Max(0.05f, ClimbCommitDuration);
			WantsToClimb = true;
			return BuildMoveInputFromDirection(direction);
		}

		private void EndWallClimb(bool withCooldown)
		{
			_climbingWall = false;
			if (withCooldown)
				_climbCooldownUntil = Time.timeSinceLevelLoad + Mathf.Max(0f, ClimbCooldown);
		}

		private bool TryProbeCrestLanding(Vector3 direction, out Vector3 landing)
		{
			landing = default;
			Vector3 origin = transform.position + Vector3.up * WallProbeHeight +
			                 direction * Mathf.Max(0.1f, CrestProbeForwardDistance);
			if (TryRaycastWorld(origin, Vector3.down, CrestProbeDownDistance, out RaycastHit hit) == false)
				return false;
			// Only mantle onto standable ground; a beveled lip or steep top is left to fall away from.
			if (hit.normal.y < CrestLandingMinNormalY)
				return false;

			landing = hit.point;
			return true;
		}

		// The sole never-still safety net: a nonzero move input producing ~zero real velocity for JamTimeout means
		// the body is physically blocked (a face the climb gates rejected, a prop section, a crowd knot) — sidestep
		// in a random direction briefly, then resume. Raw physics, deliberately goal-agnostic (goal-progress
		// tracking is banned by design), and never reset by goal/state changes. It re-baselines itself after a
		// gap in move ticks instead.
		private NetworkedInput ApplyJamReflex(NetworkedInput input)
		{
			float now = Time.timeSinceLevelLoad;
			if (now < _jamSidestepUntil)
				return BuildStuckRandomMoveInput();

			if (input.MoveDirection == Vector2.zero || IsClimbing)
			{
				_jamStuckSince = -1f;
				return input;
			}

			// Gap in move ticks (another branch owned recent frames, or a mantle suspended input): re-baseline so
			// a stale timer cannot trip an instant sidestep.
			float tickGapAllowance = Mathf.Max(0.05f, Time.fixedDeltaTime * 4f);
			if (now - _nextJamCheckTime > tickGapAllowance)
				_jamStuckSince = -1f;
			_nextJamCheckTime = now;

			Vector3 velocity = _zombie != null && _zombie.KCC != null ? _zombie.KCC.RealVelocity : Vector3.zero;
			velocity.y = 0f;
			if (velocity.sqrMagnitude > JamVelocityThreshold * JamVelocityThreshold)
			{
				_jamStuckSince = -1f;
				return input;
			}

			if (_jamStuckSince < 0f)
			{
				_jamStuckSince = now;
				return input;
			}

			if (now - _jamStuckSince >= Mathf.Max(0.1f, JamTimeout))
			{
				float min = Mathf.Max(0.1f, StuckRandomWanderDurationMin);
				float max = Mathf.Max(min, StuckRandomWanderDurationMax);
				_jamSidestepUntil = now + Random.Range(min, max);
				_stuckWanderEndTime = 0f; // fresh random direction for the sidestep burst
				_jamStuckSince = -1f;
				return BuildStuckRandomMoveInput();
			}

			return input;
		}

		// Continues whichever climb kind is active. Wall climbs drive through BuildWallClimbInput (cap/crest/
		// stall); surface climbs keep the exact registry landing logic they have always had.
		private bool TryContinueActiveClimb(Vector3 goal, out NetworkedInput input)
		{
			input = default;

			if (_climbingWall)
			{
				input = BuildWallClimbInput(goal);
				return true;
			}

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

			if (distanceToStart > climbEngageDistance)
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
			if (allowShortcut == false)
				return false;

			float shortcutSearchDistance = Mathf.Max(ClimbDirectApproachDistance, directDistance + ClimbRouteSideTolerance);

			if (ZombieClimbSurfaces.TryFindDirectClimb(
				position,
				goal,
				pathSavings,
				shortcutSearchDistance,
				ClimbRouteSideTolerance,
				ClimbMinRise,
				out candidate) == false)
			{
				return false;
			}

			return IsShortcutApproachClear(position, candidate, navigator);
		}

		private float GetCurrentDistanceToClimbStart(ZombieClimbCandidate candidate)
		{
			return FlatDistance(transform.position, GetShortcutApproachPoint(candidate));
		}

		private NetworkedInput StartSurfaceClimb(ZombieClimbCandidate candidate)
		{
			InvalidateClimbCandidateCache();
			float now = Time.timeSinceLevelLoad;
			_climbingSurface = true;
			_climbLanding = candidate.LandingPoint;
			_climbHighestY = transform.position.y;
			_climbStuckUntil = now + Mathf.Max(0.25f, ClimbStuckTimeout);
			_climbUntil = now + GetClimbDurationCap(candidate.LandingPoint.y - transform.position.y);
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

			// Island-map fast path: the ring question is "does connected same-height NavMesh exist at ring
			// distance", which the precomputed island map answers per probe with a grid lookup instead of a
			// NavMesh sample plus a ring-sampled CalculatePath (formerly up to 8 × ~27 NavMesh queries here).
			if (NavMeshIslandMap.TryGetIslandAt(startHit.position, sampleDistance, sampleDistance, out int startIsland))
			{
				// A large island is ordinary walkable ground by definition — it can never be a "small elevated
				// island", so skip the ring outright. This also shields the verdict from triangulation
				// fragmentation: a zombie standing on a big (even wrongly split) street fragment must never be
				// flagged stranded, because that verdict feeds the direct-move/random-wander stuck recovery.
				if (NavMeshIslandMap.GetIslandArea(startIsland) > NavMeshIslandMap.SmallIslandMaxArea)
					return false;

				float probeHeightTolerance = Mathf.Min(sampleDistance, heightTolerance);
				for (int i = 0; i < samples; i++)
				{
					float angle = i * Mathf.PI * 2f / samples;
					Vector3 probe = startHit.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
					if (NavMeshIslandMap.TryGetIslandAt(probe, sampleDistance, probeHeightTolerance, out int probeIsland) &&
					    probeIsland == startIsland)
						return false;
				}

				return true;
			}

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

		// Used only by the small-elevated-island probe's non-island-map fallback (V6 traversal itself never asks
		// reachability questions — climbing is a reaction to touching a wall, not a verdict).
		private bool HasCompleteNavMeshPathToGoalHeight(Vector3 start, Vector3 goal, float maxGoalHeightDelta)
		{
			var navigator = _zombie != null ? _zombie.Navigator : null;
			int areaMask = navigator != null ? navigator.AreaMask : NavMesh.AllAreas;
			float sampleDistance = navigator != null ? Mathf.Max(0.25f, navigator.SampleMaxDistance) : Mathf.Max(0.25f, StuckSampleRadius);
			float goalSampleDistance = sampleDistance;
			if (NavMesh.SamplePosition(start, out var startHit, sampleDistance, areaMask) == false)
				return false;

			// Island-map fast path. This method only needs the boolean "does a complete path exist", which for a
			// full area mask is exactly "are start and goal on the same island". Replaces the up-to-25-sample
			// goal ring search plus a CalculatePath (which floods a whole island when the goal is unreachable).
			// Restrictive masks fall through: island equality does not guarantee a mask-limited path.
			if (areaMask == NavMesh.AllAreas &&
			    NavMeshIslandMap.TryGetIslandAt(startHit.position, 0.5f, 0.5f, out int startIsland))
			{
				// Mirrors TrySampleNavMeshGoalAtHeight's search: NavMesh near the goal within goalSampleDistance
				// horizontally and maxGoalHeightDelta vertically. No qualifying NavMesh → no path, same as the
				// sampling failing.
				if (NavMeshIslandMap.TryGetIslandAt(goal, goalSampleDistance, maxGoalHeightDelta, out int goalIsland))
				{
					if (goalIsland == startIsland)
						return true;

					// Trust the negative when one side is a small (prop-perch) island or both islands were
					// path-verified at build time (street vs rooftop). An unverified large-vs-large disconnect
					// could be triangulation fragmentation, so verify with a real path query below instead of
					// feeding a false "no path" into the stuck/direct-move logic.
					if (NavMeshIslandMap.GetIslandArea(startIsland) <= NavMeshIslandMap.SmallIslandMaxArea ||
					    NavMeshIslandMap.GetIslandArea(goalIsland) <= NavMeshIslandMap.SmallIslandMaxArea ||
					    (NavMeshIslandMap.IsIslandVerified(startIsland) && NavMeshIslandMap.IsIslandVerified(goalIsland)))
						return false;
				}
				else
				{
					return false;
				}
			}

			if (TrySampleNavMeshGoalAtHeight(goal, goalSampleDistance, maxGoalHeightDelta, areaMask, out Vector3 sampledGoal) == false)
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

		// The absolute climb cap must scale with the wall: at ~1 m/s climb speed a fixed 15 s cap truncates any
		// >= ~13 m ascent mid-wall, producing the classic rise/drop/cooldown oscillation at building scale. The
		// progress-based stuck timeout remains the real abort signal; this only backstops it.
		private float GetClimbDurationCap(float rise)
		{
			float baseCap = Mathf.Max(0.5f, ClimbMaxDuration);
			if (rise <= 0f)
				return baseCap;

			float moveSpeed = _zombie != null ? _zombie.Stats.MoveSpeed : 2.2f;
			float climbSpeed = Mathf.Max(0.2f, moveSpeed * Mathf.Max(0f, ClimbSpeedMultiplier));
			return Mathf.Max(baseCap, rise / climbSpeed * 1.5f);
		}

		private bool TryGetSupportCollider(Vector3 position, out Collider support)
		{
			support = null;
			Vector3 origin = position + Vector3.up * 0.35f;
			if (TryRaycastWorld(origin, Vector3.down, SupportProbeDistance, out RaycastHit hit) == false)
				return false;

			support = hit.collider;
			return support != null;
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
			// The jam reflex measures physical blockage independently of target changes.
			InvalidateClimbCandidateCache();
			if (preserveActiveClimb == false)
			{
				_climbingSurface = false;
				_climbingWall = false;
			}
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

				// Cheap, conclusive detection first: completely off the NavMesh, or perched on a small prop. Either
				// is decisive on its own and short-circuits before the expensive island-connectivity probe.
				if (IsElevatedOffNavMesh() || IsOnSmallPropSupport())
				{
					_isStuckElevated = true;
					_islandStuckResult = false;        // cheap signal owns the verdict; drop any stale island result
					_nextElevatedIslandCheckTime = 0f; // re-verify island promptly once the cheap signal clears
				}
				else if (ShouldVerifyElevatedIsland() == false)
				{
					// Evidence of progress (a usable navigator path to a destination): the zombie is on connected
					// NavMesh, so skip the ~29-sample island probe entirely. This is the common grounded-zombie case.
					_isStuckElevated = false;
					_islandStuckResult = false;
					_nextElevatedIslandCheckTime = 0f;
				}
				else
				{
					// Inconclusive cheap checks plus evidence of failure (no usable path / unreachable / no active
					// route). Verify island connectivity, but no more often than the slower jittered interval; reuse
					// the previous verdict in between so a stranded zombie is still detected without re-probing 64 Hz.
					if (now >= _nextElevatedIslandCheckTime)
					{
						float interval = Mathf.Max(Mathf.Max(0.1f, StuckCheckInterval), ElevatedIslandCheckInterval);
						_nextElevatedIslandCheckTime = now + interval * Random.Range(0.85f, 1.15f);
						_islandStuckResult = IsOnSmallElevatedNavMeshIsland();
					}

					_isStuckElevated = _islandStuckResult;
				}
			}

			return _isStuckElevated;
		}

		// Cheap gate for the expensive island-connectivity probe. A zombie genuinely stranded on a small disconnected
		// island has no complete NavMesh path off it, so the navigator reports no usable path or an unreachable
		// destination. A grounded zombie that is actively steering along a complete path is not stranded and must
		// not pay for the probe.
		//
		// Note: we intentionally do NOT skip on IsDestinationReached. "Reached" only means the zombie is within the
		// navigator's DestinationReachDistance of the sampled destination, which for a wall-hugging target on a thin
		// navmesh strip can be ~1.35 m short of the real target. Skipping the stuck probe there left the zombie
		// staring instead of closing the final gap. When reached, the navigator also clears its path, so HasPath is
		// false and this returns true (verify) — the correct behaviour.
		private bool ShouldVerifyElevatedIsland()
		{
			var navigator = _zombie != null ? _zombie.Navigator : null;
			if (navigator == null || navigator.HasDestination == false)
				return true;
			if (navigator.IsDestinationUnreachable)
				return true;
			return navigator.HasPath == false;
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
			if (TryGetSupportCollider(transform.position, out Collider support) == false)
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
			       _zombie.Sensor.TryGetClosestDirectEnemy(out enemy, GetOldestDirectEnemyTick()) &&
			       IsTargetAlive(enemy.Object);
		}

		private int GetOldestDirectEnemyTick()
		{
			var runner = _zombie.Runner;
			if (runner == null)
				return int.MinValue;

			// Allow scan cadence jitter, but filter stale memories BEFORE choosing the closest enemy.
			float freshWindow = Mathf.Max(0.05f, _zombie.Sensor.SensorInterval) * 3f + 0.1f;
			return runner.Tick - Mathf.FloorToInt(freshWindow / runner.DeltaTime);
		}

		// Sticky variant for zombies that already have a live target: while that target is still directly
		// seen/felt, keep it — another visible survivor only steals attention when it is dramatically closer
		// (below TargetSwitchDistanceFactor of the current target's distance). Without this, two shooters at
		// similar range flip the "closest sensed enemy" back and forth every tick and the zombie ping-pongs
		// between them instead of committing to one. Degrades to the plain closest pick when there is no live
		// current target or it is no longer directly sensed.
		private bool TryGetStickyDirectEnemy(out KnownEnemyInfo enemy)
		{
			if (TryGetDirectEnemy(out enemy) == false)
				return false;
			if (enemy.Object == _target || IsTargetAlive(_target) == false)
				return true;
			if (_zombie.Sensor == null ||
			    _zombie.Sensor.TryGetDirectEnemy(_target, out KnownEnemyInfo current, GetOldestDirectEnemyTick()) == false)
				return true;

			// Same 3D metric TryGetClosestDirectEnemy ranks by.
			Vector3 origin = transform.position;
			float closestSqr = (enemy.LastKnownPosition - origin).sqrMagnitude;
			float currentSqr = (current.LastKnownPosition - origin).sqrMagnitude;
			float factor = Mathf.Clamp01(TargetSwitchDistanceFactor);
			if (closestSqr < currentSqr * factor * factor)
				return true;

			enemy = current;
			return true;
		}

		private bool ShouldIgnoreInvestigationStimulus()
		{
			// A concrete live target beats any noise: actively chasing zombies never divert to stimuli.
			if ((State == EZombieAIState.Attacking || State == EZombieAIState.Hunting) && IsTargetAlive(_target))
				return true;

			if (State != EZombieAIState.Investigating)
				return false;

			// An ongoing investigation re-triggers on a cadence rather than being fully deaf: the point is
			// committed for InvestigationCommitDuration (so two shooters cannot ping-pong the zombie back and
			// forth within a couple of seconds), after which the next stimulus re-aims it. A zombie that has
			// basically arrived (within InvestigationRetriggerDistance flat of the point) accepts a fresh
			// stimulus immediately — its current point is spent. Directly seeing a survivor overrides all of
			// this instantly, and active climbs are protected by the _climbingSurface guard in
			// ReceiveInvestigationStimulus.
			float retriggerDistance = Mathf.Max(0f, InvestigationRetriggerDistance);
			if (FlatDistanceSqr(transform.position, _investigationTarget) <= retriggerDistance * retriggerDistance)
				return false;

			return Time.timeSinceLevelLoad - _investigationStartTime < Mathf.Max(0f, InvestigationCommitDuration);
		}

		private bool ShouldPrioritizeClimbTowardTarget(Vector3 targetPosition)
		{
			float verticalGap = targetPosition.y - transform.position.y;
			if (verticalGap <= ClimbStopRise)
				return false;
			if (_climbingSurface || _climbingWall || IsClimbing)
				return true;

			float range = _zombie != null ? Mathf.Max(0.1f, _zombie.Stats.AttackRange) : 0.1f;
			return FlatDistanceSqr(transform.position, targetPosition) <= range * range;
		}

		private bool ShouldPreserveActiveClimbForTarget(Vector3 targetPosition)
		{
			// Wall climbs have no landing to compare against: preserve while the goal is still above us (finish
			// the wall; dropping mid-face just means re-approaching the same face), cancel outright when the goal
			// moved below — the roof cap would end the climb within a tick anyway, this just skips the climb tick.
			if (_climbingWall)
				return targetPosition.y - transform.position.y > ClimbStopRise;

			if (_climbingSurface == false)
				return false;

			// Finish the wall as long as the goal is still ABOVE us. The old rule preserved the climb only when
			// the new goal was basically at this climb's landing, which dropped climbers off the wall whenever
			// the target strolled along the ledge above or ducked out of sight (sight-loss →
			// StartInvestigation with a last-known point on the ledge). Mid-wall the drop is never right — the
			// zombie just falls, re-approaches, and climbs the same face again. A goal that moved BELOW the
			// zombie (target dropped down) falls through to the old landing-proximity rule, so pointless ascents
			// still get cancelled.
			if (targetPosition.y - transform.position.y > ClimbStopRise)
				return true;

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

			// Waypoint snapping is suppressed for the whole sweep while this zombie stands inside a crowded
			// waypoint cluster: candidates stay plain points, and the away-from-zombies bias above walks it out
			// of the building instead of letting it snap straight back in ("incentivized to leave").
			bool allowWaypointSnap = UseWaypointWander && IsStandingInCrowdedWaypointCluster(origin) == false;
			bool usedSweepWaypoint = false;
			Vector3 sweepWaypoint = default;

			for (int i = 0; i < candidates; i++)
			{
				Vector2 random = Random.insideUnitCircle.normalized;
				Vector3 direction = new Vector3(random.x, 0f, random.y);
				if (awayFromZombies.sqrMagnitude > 0.001f)
					direction = (direction + awayFromZombies * 1.5f).normalized;

				float distance = Random.Range(IdleWanderRadius * 0.35f, Mathf.Max(0.5f, IdleWanderRadius));
				Vector3 candidate = origin + direction * distance;

				// A candidate landing near an authored waypoint becomes that waypoint (buildings' interiors and
				// upper floors are never found by flat random sampling). Each waypoint is considered once per
				// sweep; a crowded waypoint stops attracting and the candidate stays a plain point.
				if (allowWaypointSnap &&
				    PatrolWaypoint.TryGetNearestInRadiusXZ(candidate, Mathf.Max(0f, WanderWaypointSnapRadius), out Vector3 waypoint))
				{
					if (usedSweepWaypoint && FlatDistanceSqr(waypoint, sweepWaypoint) <= 0.01f)
						continue;

					if (CountZombiesNearXZ(waypoint, WaypointCrowdRadius) < Mathf.Max(1, MaxZombiesNearWaypoint))
					{
						candidate = waypoint;
						usedSweepWaypoint = true;
						sweepWaypoint = waypoint;
					}
				}

				if (TryResolveIdleWanderCandidate(origin, candidate, out Vector3 reachable) == false)
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

		// The zombie stands within a crowded waypoint cluster when the nearest waypoint to it (within
		// WaypointCrowdRadius, XZ) already has MaxZombiesNearWaypoint or more zombies around it. Crowding counts
		// zombies, not waypoints, in a shared radius — a dense waypoint cluster shares one occupancy pool, so
		// stacking more waypoints into a building does not raise its capacity.
		private bool IsStandingInCrowdedWaypointCluster(Vector3 origin)
		{
			float crowdRadius = Mathf.Max(0.5f, WaypointCrowdRadius);
			if (PatrolWaypoint.TryGetNearestInRadiusXZ(origin, crowdRadius, out Vector3 waypoint) == false)
				return false;

			return CountZombiesNearXZ(waypoint, crowdRadius) >= Mathf.Max(1, MaxZombiesNearWaypoint);
		}

		// Linear scan like GetAwayFromNearbyZombies — same per-sweep cost class, no new spatial structure.
		private int CountZombiesNearXZ(Vector3 center, float radius)
		{
			float radiusSqr = Mathf.Max(0.5f, radius);
			radiusSqr *= radiusSqr;
			int count = 0;

			for (int i = 0; i < ZombieCharacter.ActiveZombies.Count; i++)
			{
				var other = ZombieCharacter.ActiveZombies[i];
				if (other == null || other.Health == null || other.Health.IsAlive == false)
					continue;

				if (FlatDistanceSqr(center, other.transform.position) <= radiusSqr)
					count++;
			}

			return count;
		}

		private bool TryResolveIdleWanderCandidate(Vector3 origin, Vector3 candidate, out Vector3 reachable)
		{
			reachable = default;

			var navigator = _zombie != null ? _zombie.Navigator : null;
			int areaMask = navigator != null ? navigator.AreaMask : NavMesh.AllAreas;
			float startSampleDistance = navigator != null
				? Mathf.Max(0.25f, navigator.SampleMaxDistance)
				: Mathf.Max(0.25f, StuckSampleRadius);
			float candidateSampleDistance = Mathf.Max(0.25f, IdleWanderNavMeshSampleDistance);

			if (NavMesh.SamplePosition(origin, out var startHit, startSampleDistance, areaMask) == false)
				return false;
			if (NavMesh.SamplePosition(candidate, out var candidateHit, candidateSampleDistance, areaMask) == false)
				return false;

			if (_scratchNavMeshPath == null)
				_scratchNavMeshPath = new NavMeshPath();
			if (NavMesh.CalculatePath(startHit.position, candidateHit.position, areaMask, _scratchNavMeshPath) == false)
				return false;
			if (_scratchNavMeshPath.status != NavMeshPathStatus.PathComplete ||
			    _scratchNavMeshPath.corners == null ||
			    _scratchNavMeshPath.corners.Length == 0)
			{
				return false;
			}

			reachable = candidateHit.position;
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
			if (target == null || target.IsValid == false)
				return false;

			var survivor = Survivor.GetFrom(target);
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
			ResetIdleWanderProgress();
		}

		private void BeginIdleWanderProgress(Vector3 position)
		{
			_hasIdleWanderProgressPosition = true;
			_idleWanderProgressPosition = position;
			_idleWanderStuckSince = -1f;
			_nextIdleWanderProgressCheckTime = Time.timeSinceLevelLoad + Mathf.Max(0.1f, StuckCheckInterval);
		}

		private void ResetIdleWanderProgress()
		{
			_hasIdleWanderProgressPosition = false;
			_idleWanderProgressPosition = default;
			_idleWanderStuckSince = -1f;
			_nextIdleWanderProgressCheckTime = 0f;
		}

		private bool IsIdleWanderDestinationStuck(Vector3 position)
		{
			float now = Time.timeSinceLevelLoad;
			if (now < _nextIdleWanderProgressCheckTime)
				return false;

			_nextIdleWanderProgressCheckTime = now + Mathf.Max(0.1f, StuckCheckInterval);

			if (_hasIdleWanderProgressPosition == false)
			{
				BeginIdleWanderProgress(position);
				return false;
			}

			Vector3 progress = position - _idleWanderProgressPosition;
			progress.y = 0f;
			float minProgress = Mathf.Max(0.01f, IdleWanderStuckMinProgress);
			if (progress.sqrMagnitude >= minProgress * minProgress)
			{
				_idleWanderProgressPosition = position;
				_idleWanderStuckSince = -1f;
				return false;
			}

			if (_idleWanderStuckSince < 0f)
			{
				_idleWanderStuckSince = now;
				return false;
			}

			return now - _idleWanderStuckSince >= Mathf.Max(0.1f, IdleWanderStuckTimeout);
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
