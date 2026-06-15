using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public enum ENonCombatAssignment
	{
		HoldPosition,
		FollowSurvivor,
		MoveToPoint,
		AssignedArea,
	}

	public struct SurvivorNonCombatAISettings
	{
		public bool CollectVisiblePickups;
		public bool InvestigateSuspiciousStimuli;
		public bool RecruitNeutralSurvivors;
		public bool AllowCombatAIActivation;

		public static SurvivorNonCombatAISettings Default => new SurvivorNonCombatAISettings
		{
			CollectVisiblePickups = true,
			InvestigateSuspiciousStimuli = true,
			RecruitNeutralSurvivors = true,
			AllowCombatAIActivation = true,
		};

		public static SurvivorNonCombatAISettings Passive => new SurvivorNonCombatAISettings
		{
			CollectVisiblePickups = false,
			InvestigateSuspiciousStimuli = false,
			RecruitNeutralSurvivors = false,
			AllowCombatAIActivation = false,
		};
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorNonCombatAI : MonoBehaviour, Survivor.ICharacterInputSource
	{
		[Header("Assignment")]
		public float DefaultFollowStoppingDistance = 2f;
		public float DefaultMoveStoppingDistance = 1.25f;
		public float DirectFallbackDistance = 1.5f;
		public float MaxYawDegreesPerTick = 8f;

		[Header("Idle Look")]
		public float IdleLookRotationIntervalMin = 4f;
		public float IdleLookRotationIntervalMax = 8f;
		public float IdleLookMaxYawDegreesPerTick = 2f;

		private Survivor _survivor;
		private SurvivorLootingAI _looting;
		private SurvivorInvestigationAI _investigation;
		private SurvivorRecruitingAI _recruiting;
		private SurvivorAssignedAreaAI _assignedArea;
		private SurvivorCombatAI _combat;
		private readonly List<KnownEnemyInfo> _directEnemiesScratch = new(8);
		private SurvivorNonCombatAISettings _settings;
		private ENonCombatAssignment _assignment;
		private Vector3 _anchorPosition;
		private Vector3 _assignedAreaEntryPoint;
		private Vector3[] _assignedAreaPatrolPoints;
		private Survivor _followTarget;
		private float _assignmentRadius;
		private float _followStoppingDistance;
		private float _moveStoppingDistance;
		private bool _usePathfinding;
		private bool _playerAssignmentSatisfiedOnce;
		private float _nextIdleLookRotationTime;
		private float _idleLookYaw;
		private int _lastAlertedDirectEnemyTick;
		private NetworkObject _lastCombatEnemy;
		private Vector3 _lastCombatEnemyPosition;
		private int _lastCombatEnemyTick;
		private bool _hasLastCombatEnemy;
		private float _nextTravelDetourTime;

		private const float TravelDetourRetryDelay = 0.75f;

		public ENonCombatAssignment Assignment => _assignment;
		public Survivor FollowTarget => _followTarget;
		public Vector3 AnchorPosition => _anchorPosition;

		public static SurvivorNonCombatAI HoldPosition(Survivor survivor, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetHoldPosition(survivor != null ? survivor.transform.position : default);
			return ai;
		}

		public static SurvivorNonCombatAI Follow(Survivor survivor, Survivor target, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetFollowTarget(target);
			return ai;
		}

		public static SurvivorNonCombatAI MoveTo(Survivor survivor, Vector3 destination, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetMoveDestination(destination);
			return ai;
		}

		public static SurvivorNonCombatAI AssignedArea(Survivor survivor, Vector3 center, float radius, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			return ai.TrySetAssignedArea(center, radius) ? ai : null;
		}

		public static SurvivorNonCombatAI AssignedArea(Survivor survivor, Vector3 center, float radius, Vector3 entryPoint, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetAssignedArea(center, radius, entryPoint, null);
			return ai;
		}

		public static SurvivorNonCombatAI AssignedArea(Survivor survivor, Vector3 center, float radius, Vector3 entryPoint, Vector3[] patrolPoints, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetAssignedArea(center, radius, entryPoint, patrolPoints);
			return ai;
		}

		// Like AssignedArea, but starts already "satisfied" so the survivor has full autonomy (combat AND looting)
		// while travelling to the area, not just once it arrives. Used for roaming neutral survivors so they fight
		// zombies and grab pickups on the way between dynamic spawn points.
		public static SurvivorNonCombatAI RoamArea(Survivor survivor, Vector3 center, float radius, Vector3 entryPoint, Vector3[] patrolPoints, SurvivorNonCombatAISettings settings)
		{
			var ai = GetOrAdd(survivor);
			if (ai == null)
				return null;

			ai.SetSettings(settings);
			ai.SetAssignedArea(center, radius, entryPoint, patrolPoints);
			ai._playerAssignmentSatisfiedOnce = true;
			return ai;
		}

		public static bool TryBuildAssignedAreaPatrolPoints(Survivor survivor, Vector3 center, float radius, out Vector3[] patrolPoints,
			bool preferAuthoredWaypoints = false)
		{
			patrolPoints = null;
			if (survivor == null)
				return false;

			var assignedArea = survivor.GetComponent<SurvivorAssignedAreaAI>();
			if (assignedArea == null)
				assignedArea = survivor.gameObject.AddComponent<SurvivorAssignedAreaAI>();

			return assignedArea.TryBuildReachablePointSet(survivor, center, radius, out patrolPoints, preferAuthoredWaypoints);
		}

		private void Awake()
		{
			EnsureInitialized(GetComponent<Survivor>());
		}

		private static SurvivorNonCombatAI GetOrAdd(Survivor survivor)
		{
			if (survivor == null)
				return null;

			var ai = survivor.GetComponent<SurvivorNonCombatAI>();
			if (ai == null)
				ai = survivor.gameObject.AddComponent<SurvivorNonCombatAI>();

			ai.EnsureInitialized(survivor);
			return ai;
		}

		private void EnsureInitialized(Survivor survivor)
		{
			if (_survivor == null)
				_survivor = survivor != null ? survivor : GetComponent<Survivor>();

			EnsureBehaviorComponents();

			if (_followStoppingDistance <= 0f)
				_followStoppingDistance = Mathf.Max(0.25f, DefaultFollowStoppingDistance);
			if (_moveStoppingDistance <= 0f)
				_moveStoppingDistance = Mathf.Max(0.25f, DefaultMoveStoppingDistance);

			_usePathfinding = true;
			if (_settings.Equals(default(SurvivorNonCombatAISettings)))
				_settings = SurvivorNonCombatAISettings.Default;
		}

		private void EnsureBehaviorComponents()
		{
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();

			if (_looting == null)
			{
				_looting = GetComponent<SurvivorLootingAI>();
				if (_looting == null)
					_looting = gameObject.AddComponent<SurvivorLootingAI>();
			}

			if (_investigation == null)
			{
				_investigation = GetComponent<SurvivorInvestigationAI>();
				if (_investigation == null)
					_investigation = gameObject.AddComponent<SurvivorInvestigationAI>();
			}

			if (_recruiting == null)
			{
				_recruiting = GetComponent<SurvivorRecruitingAI>();
				if (_recruiting == null)
					_recruiting = gameObject.AddComponent<SurvivorRecruitingAI>();
			}

			if (_assignedArea == null)
			{
				_assignedArea = GetComponent<SurvivorAssignedAreaAI>();
				if (_assignedArea == null)
					_assignedArea = gameObject.AddComponent<SurvivorAssignedAreaAI>();
			}

			if (_combat == null)
			{
				_combat = GetComponent<SurvivorCombatAI>();
				if (_combat == null)
					_combat = gameObject.AddComponent<SurvivorCombatAI>();
				_combat.Activate(_survivor);
			}
		}

		public void Activate(Survivor survivor)
		{
			EnsureInitialized(survivor);
			_settings = SurvivorNonCombatAISettings.Default;
			SetHoldPosition(_survivor != null ? _survivor.transform.position : default);
		}

		public Survivor.ICharacterInputSource CreateEquivalentAssignmentFor(Survivor target, SurvivorNonCombatAISettings settings)
		{
			if (target == null)
				return null;

			return _assignment switch
			{
				ENonCombatAssignment.FollowSurvivor => Follow(target, _followTarget, settings),
				ENonCombatAssignment.MoveToPoint => MoveTo(target, _anchorPosition, settings),
				ENonCombatAssignment.AssignedArea => AssignedArea(target, _anchorPosition, _assignmentRadius, _assignedAreaEntryPoint, _assignedAreaPatrolPoints, settings),
				ENonCombatAssignment.HoldPosition => HoldPosition(target, settings),
				_ => HoldPosition(target, settings),
			};
		}

		public void SetSettings(SurvivorNonCombatAISettings settings)
		{
			bool combatWasEnabled = _settings.AllowCombatAIActivation;
			_settings = settings;

			EnsureBehaviorComponents();
			if (combatWasEnabled && _settings.AllowCombatAIActivation == false)
				_combat?.ClearMovementTask();

			if (_settings.CollectVisiblePickups == false && (_looting != null && (_looting.HasTask || _looting.IsReturning)))
				_looting.ClearTask(true, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			// A recruitment hand-off investigation is governed by the recruit setting, a stimulus investigation by the
			// investigate setting, so toggling one behavior off never cancels the other's work.
			if (_investigation != null && (_investigation.HasTask || _investigation.IsReturning))
			{
				bool investigationDisabled = _investigation.IsRecruitmentOrigin
					? _settings.RecruitNeutralSurvivors == false
					: _settings.InvestigateSuspiciousStimuli == false;
				if (investigationDisabled)
					_investigation.ClearTask(true, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			}
			if (_settings.RecruitNeutralSurvivors == false && (_recruiting != null && (_recruiting.HasTask || _recruiting.IsReturning)))
				_recruiting.ClearTask(true, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
		}

		public void ReceiveInvestigationAlert(Vector3 target, int stimulusTick, NetworkObject observedTarget = null)
		{
			TryStartInvestigation(target, stimulusTick, false, observedTarget, false, false);
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick)
		{
			TryStartInvestigation(target, stimulusTick, true);
		}

		private bool TryStartInvestigation(Vector3 target, int stimulusTick, bool alertAllies)
		{
			return TryStartInvestigation(target, stimulusTick, alertAllies, null, false, false);
		}

		private bool TryStartInvestigation(Vector3 target, int stimulusTick, bool alertAllies, NetworkObject observedTarget, bool allowSameTick, bool force, bool recruitmentOrigin = false)
		{
			EnsureBehaviorComponents();
			// Once committed to a recruitment, the survivor ignores investigation stimuli (gunshots, alerts) entirely.
			// The recruitment hand-off itself clears recruiting first, so it is not blocked here.
			if (recruitmentOrigin == false && _recruiting != null && _recruiting.HasTask)
				return false;
			return _investigation != null &&
			       _investigation.TryStart(
				       _survivor,
				       _settings.InvestigateSuspiciousStimuli,
				       CanStartInvestigationBehavior(),
				       ShouldPauseTemporaryNonCombatBehavior(),
				       target,
				       stimulusTick,
				       alertAllies,
				       observedTarget,
				       allowSameTick,
				       force,
				       recruitmentOrigin);
		}

		public void SetHoldPosition(Vector3 position)
		{
			_assignment = ENonCombatAssignment.HoldPosition;
			_anchorPosition = position;
			_assignedAreaPatrolPoints = null;
			_followTarget = null;
			_assignmentRadius = 0f;
			_playerAssignmentSatisfiedOnce = true;
			_looting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_investigation?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_recruiting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
			_survivor?.Navigator?.ClearDestination();
		}

		public void SetFollowTarget(Survivor target, float stoppingDistance = -1f, bool usePathfinding = true)
		{
			_assignment = ENonCombatAssignment.FollowSurvivor;
			_followTarget = target;
			_anchorPosition = target != null ? target.transform.position : (_survivor != null ? _survivor.transform.position : default);
			_assignedAreaPatrolPoints = null;
			_followStoppingDistance = Mathf.Max(0.25f, stoppingDistance > 0f ? stoppingDistance : DefaultFollowStoppingDistance);
			_usePathfinding = usePathfinding;
			_playerAssignmentSatisfiedOnce = false;
			_looting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_investigation?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_recruiting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
			ClearRememberedCombatEnemy();
		}

		public void SetMoveDestination(Vector3 destination, float stoppingDistance = -1f)
		{
			_assignment = ENonCombatAssignment.MoveToPoint;
			_anchorPosition = destination;
			_assignedAreaPatrolPoints = null;
			_followTarget = null;
			_assignmentRadius = 0f;
			_moveStoppingDistance = Mathf.Max(0.25f, stoppingDistance > 0f ? stoppingDistance : DefaultMoveStoppingDistance);
			_playerAssignmentSatisfiedOnce = false;
			_looting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_investigation?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_recruiting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
			ClearRememberedCombatEnemy();
			_survivor?.Navigator?.SetDestination(destination);
		}

		public bool TrySetAssignedArea(Vector3 center, float radius)
		{
			EnsureBehaviorComponents();
			if (_assignedArea == null || _assignedArea.TryBuildReachablePointSet(_survivor, center, radius, out var patrolPoints) == false)
				return false;

			Vector3 entryPoint = patrolPoints != null && patrolPoints.Length > 0 ? patrolPoints[0] : center;
			SetAssignedArea(center, radius, entryPoint, patrolPoints);
			return true;
		}

		private void SetAssignedArea(Vector3 center, float radius, Vector3 entryPoint, Vector3[] patrolPoints)
		{
			_assignment = ENonCombatAssignment.AssignedArea;
			_anchorPosition = center;
			_assignedAreaPatrolPoints = patrolPoints;
			_assignedAreaEntryPoint = patrolPoints != null && patrolPoints.Length > 0
				? patrolPoints[Random.Range(0, patrolPoints.Length)]
				: entryPoint;
			_followTarget = null;
			_assignmentRadius = Mathf.Max(0f, radius);
			_playerAssignmentSatisfiedOnce = false;
			_looting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_investigation?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_recruiting?.ClearTask(false, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
			ClearRememberedCombatEnemy();
			_survivor?.Navigator?.SetDestination(_assignedAreaEntryPoint);
		}

		public NetworkedInput GetInput(NetworkRunner runner)
		{
			if (_survivor == null || _survivor.Health == null || _survivor.Health.IsAlive == false)
				return default;

			switch (_assignment)
			{
				case ENonCombatAssignment.FollowSurvivor:
					return GetFollowInput();
				case ENonCombatAssignment.MoveToPoint:
					return GetMoveToPointInput();
				case ENonCombatAssignment.AssignedArea:
					return GetAssignedAreaInput();
				case ENonCombatAssignment.HoldPosition:
				default:
					return GetHoldOrPickupInput();
			}
		}

		private NetworkedInput GetFollowInput()
		{
			// Detour to recruit a nearby neutral while following, then resume following.
			if (TryGetTravelDetourRecruitingInput(out NetworkedInput detourInput))
				return detourInput;

			if (_followTarget == null || _followTarget.Health == null || _followTarget.Health.IsAlive == false)
			{
				SetHoldPosition(_survivor.transform.position);
				return GetHoldInput();
			}

			_anchorPosition = _followTarget.transform.position;

			Vector3 toTarget = _followTarget.transform.position - _survivor.transform.position;
			toTarget.y = 0f;

			if (toTarget.sqrMagnitude < 0.001f)
				return GetPlayerOrderHoldInput();

			if (toTarget.sqrMagnitude > _followStoppingDistance * _followStoppingDistance)
			{
				if (_usePathfinding && _survivor.Navigator != null)
				{
					_survivor.Navigator.SetDestination(_followTarget.transform.position);
					_survivor.Navigator.Tick(_survivor.transform.position);
					if (_survivor.Navigator.TryGetSteeringTarget(_survivor.transform.position, out var steeringTarget))
						return CreatePlayerOrderMoveInput(steeringTarget, false, _followStoppingDistance);
				}

				return CreatePlayerOrderMoveInput(_followTarget.transform.position, false, _followStoppingDistance);
			}

			_survivor.Navigator?.ClearDestination();
			return GetPlayerOrderHoldInput();
		}

		private NetworkedInput GetAssignedAreaInput()
		{
			EnsureBehaviorComponents();

			if (_assignedArea == null)
				return GetHoldOrPickupInput();

			bool isInsideArea = _assignedArea.IsInsideArea(_survivor, _anchorPosition, _assignmentRadius);
			if (isInsideArea)
				_playerAssignmentSatisfiedOnce = true;

			if (_playerAssignmentSatisfiedOnce == false)
			{
				// Detour to recruit a nearby neutral while travelling to the area, then resume heading there.
				if (TryGetTravelDetourRecruitingInput(out NetworkedInput travelDetourInput))
					return travelDetourInput;

				return _assignedArea.GetInput(
					_survivor,
					_anchorPosition,
					_assignedAreaEntryPoint,
					_assignmentRadius,
					_assignedAreaPatrolPoints,
					CreatePlayerOrderMoveInput,
					GetPlayerOrderHoldInput);
			}

			// Active recruitment outranks looting/investigation and ignores zombies; only a sensed enemy player stops it.
			// On loss (target killed or sight lost) the helper hands off to an investigation of the last known spot.
			if (TryGetRecruitingInput(_assignedAreaEntryPoint, out NetworkedInput activeRecruitInput))
				return activeRecruitInput;

			if (ShouldPauseTemporaryNonCombatBehavior())
			{
				if (_looting != null && (_looting.HasTask || _looting.IsReturning))
					_looting.ClearTask(true, _assignedAreaEntryPoint, _survivor.Navigator);
				if (_investigation != null && (_investigation.HasTask || _investigation.IsReturning))
					_investigation.ClearTask(true, _assignedAreaEntryPoint, _survivor.Navigator);
				return GetHoldInput();
			}

			if (TryGetCombatInput(out var combatInput))
				return combatInput;

			if (CanStartRecruiting() && _recruiting.TryStart(_survivor, _settings.RecruitNeutralSurvivors)
			    && TryGetRecruitingInput(_assignedAreaEntryPoint, out NetworkedInput startRecruitInput))
				return startRecruitInput;

			if (_investigation != null && _investigation.HasTask)
				return _investigation.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			if ((_looting != null && _looting.IsReturning) || (_investigation != null && _investigation.IsReturning) || (_recruiting != null && _recruiting.IsReturning))
				return GetReturnToAssignedAreaInput();

			if (_looting != null && _looting.HasTask)
				return _looting.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			if (_looting != null && _looting.TryStart(_survivor, _settings.CollectVisiblePickups, ShouldPauseTemporaryNonCombatBehavior()))
				return _looting.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			return _assignedArea.GetInput(_survivor, _anchorPosition, _assignedAreaEntryPoint, _assignmentRadius, _assignedAreaPatrolPoints, CreateMoveInput, GetHoldInput);
		}

		private NetworkedInput GetMoveToPointInput()
		{
			// Detour to recruit a nearby neutral while travelling to the move point, then resume heading there.
			if (TryGetTravelDetourRecruitingInput(out NetworkedInput detourInput))
				return detourInput;

			var navigator = _survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(_anchorPosition);

				navigator.Tick(_survivor.transform.position);
				// Order reached, or close-but-blocked (navigator reports it unreachable): stop and hold rather than
				// freezing mid-map or chasing a point we can never stand on.
				if (navigator.IsDestinationReached || navigator.IsDestinationUnreachable)
				{
					SetHoldPosition(_anchorPosition);
					return GetHoldInput();
				}
				if (navigator.TryGetSteeringTarget(_survivor.transform.position, out var steeringTarget))
					return CreatePlayerOrderMoveInput(steeringTarget, false, _moveStoppingDistance);
			}

			if (FlatDistanceSqr(_survivor.transform.position, _anchorPosition) <= DirectFallbackDistance * DirectFallbackDistance)
			{
				if (FlatDistanceSqr(_survivor.transform.position, _anchorPosition) <= _moveStoppingDistance * _moveStoppingDistance)
				{
					SetHoldPosition(_anchorPosition);
					return GetHoldInput();
				}

				return CreatePlayerOrderMoveInput(_anchorPosition, true, _moveStoppingDistance);
			}

			return GetPlayerOrderHoldInput();
		}

		private NetworkedInput GetHoldInput()
		{
			if (TryGetCombatInput(out var combatInput))
				return combatInput;

			if (_settings.InvestigateSuspiciousStimuli &&
			    _survivor.Sensor != null &&
			    _survivor.Sensor.TryGetLookRotationDelta(MaxYawDegreesPerTick, out Vector2 lookRotationDelta))
			{
				return new NetworkedInput { LookRotationDelta = lookRotationDelta };
			}

			return GetIdleLookAroundInput();
		}

		private NetworkedInput GetHoldOrPickupInput()
		{
			EnsureBehaviorComponents();

			// Active recruitment outranks looting/investigation and ignores zombies; only a sensed enemy player stops it.
			// On loss (target killed or sight lost) the helper hands off to an investigation of the last known spot.
			if (TryGetRecruitingInput(_anchorPosition, out NetworkedInput activeRecruitInput))
				return activeRecruitInput;

			if (ShouldPauseTemporaryNonCombatBehavior())
			{
				if (_looting != null && (_looting.HasTask || _looting.IsReturning))
					_looting.ClearTask(true, _anchorPosition, _survivor.Navigator);
				if (_investigation != null && (_investigation.HasTask || _investigation.IsReturning))
					_investigation.ClearTask(true, _anchorPosition, _survivor.Navigator);
				return GetHoldInput();
			}

			if (TryGetCombatInput(out var combatInput))
				return combatInput;

			if (CanStartRecruiting() && _recruiting.TryStart(_survivor, _settings.RecruitNeutralSurvivors)
			    && TryGetRecruitingInput(_anchorPosition, out NetworkedInput startRecruitInput))
				return startRecruitInput;

			if (_investigation != null && _investigation.HasTask)
				return _investigation.GetInput(_survivor, _anchorPosition, CreateMoveInput, GetReturnToAnchorInput);

			if ((_looting != null && _looting.IsReturning) || (_investigation != null && _investigation.IsReturning) || (_recruiting != null && _recruiting.IsReturning))
				return GetReturnToAnchorInput();

			if (_looting != null && _looting.HasTask)
				return _looting.GetInput(_survivor, _anchorPosition, CreateMoveInput, GetReturnToAnchorInput);

			if (_looting != null && _looting.TryStart(_survivor, _settings.CollectVisiblePickups, ShouldPauseTemporaryNonCombatBehavior()))
				return _looting.GetInput(_survivor, _anchorPosition, CreateMoveInput, GetReturnToAnchorInput);

			return GetHoldInput();
		}

		private NetworkedInput GetIdleLookAroundInput()
		{
			if (_settings.InvestigateSuspiciousStimuli == false || IdleLookMaxYawDegreesPerTick <= 0f)
				return default;

			float now = Time.timeSinceLevelLoad;
			if (now >= _nextIdleLookRotationTime)
				PickNextIdleLookYaw(now);

			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, _idleLookYaw);
			if (Mathf.Abs(yawDelta) < 0.1f)
				return default;

			float maxYawDelta = Mathf.Max(0f, IdleLookMaxYawDegreesPerTick);
			return new NetworkedInput
			{
				LookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -maxYawDelta, maxYawDelta)),
			};
		}

		private NetworkedInput GetReturnToAnchorInput()
		{
			if (FlatDistanceSqr(_survivor.transform.position, _anchorPosition) <= _moveStoppingDistance * _moveStoppingDistance)
			{
				_looting?.CompleteReturn();
				_investigation?.CompleteReturn();
				_recruiting?.CompleteReturn();
				_survivor.Navigator?.ClearDestination();
				return GetHoldInput();
			}

			var navigator = _survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(_anchorPosition);

				navigator.Tick(_survivor.transform.position);
				if (navigator.TryGetSteeringTarget(_survivor.transform.position, out var steeringTarget))
					return CreateMoveInput(steeringTarget, false, _moveStoppingDistance);
			}

			if (FlatDistanceSqr(_survivor.transform.position, _anchorPosition) <= DirectFallbackDistance * DirectFallbackDistance)
				return CreateMoveInput(_anchorPosition, true, _moveStoppingDistance);

			return GetHoldInput();
		}

		private NetworkedInput GetReturnToAssignedAreaInput()
		{
			if (_assignedArea != null && _assignedArea.IsInsideArea(_survivor, _anchorPosition, _assignmentRadius))
			{
				_looting?.CompleteReturn();
				_investigation?.CompleteReturn();
				_recruiting?.CompleteReturn();
				_survivor.Navigator?.ClearDestination();
				return GetHoldInput();
			}

			return GetReturnToPointInput(_assignedAreaEntryPoint);
		}

		private NetworkedInput GetReturnToPointInput(Vector3 destination)
		{
			if (FlatDistanceSqr(_survivor.transform.position, destination) <= _moveStoppingDistance * _moveStoppingDistance)
			{
				_looting?.CompleteReturn();
				_investigation?.CompleteReturn();
				_recruiting?.CompleteReturn();
				_survivor.Navigator?.ClearDestination();
				return GetHoldInput();
			}

			var navigator = _survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(destination);

				navigator.Tick(_survivor.transform.position);
				if (navigator.TryGetSteeringTarget(_survivor.transform.position, out var steeringTarget))
					return CreateMoveInput(steeringTarget, false, _moveStoppingDistance);
			}

			if (FlatDistanceSqr(_survivor.transform.position, destination) <= DirectFallbackDistance * DirectFallbackDistance)
				return CreateMoveInput(destination, true, _moveStoppingDistance);

			return GetHoldInput();
		}

		private void PickNextIdleLookYaw(float now)
		{
			_idleLookYaw = Random.Range(0f, 360f);
			_nextIdleLookRotationTime = now + RandomRangeClamped(
				IdleLookRotationIntervalMin,
				IdleLookRotationIntervalMax);
		}

		private bool ShouldPauseTemporaryNonCombatBehavior()
		{
			return HasCombatTargetWithLineOfFire();
		}

		private bool CanStartInvestigationBehavior()
		{
			return CanAIBehaviorOverridePlayerOrder();
		}

		// Recruitment may only begin once the player order is satisfied, the survivor is not in combat (no line of
		// fire on anything), no enemy player is sensed, and no investigation is currently active. Returning from an
		// investigation is allowed, since that only clears the active task, not the order.
		private bool CanStartRecruiting()
		{
			if (_settings.RecruitNeutralSurvivors == false || _recruiting == null)
				return false;
			if (CanAIBehaviorOverridePlayerOrder() == false)
				return false;
			if (ShouldPauseTemporaryNonCombatBehavior())
				return false;
			if (HasSensedEnemyPlayerSurvivor())
				return false;
			if (_investigation != null && _investigation.HasTask)
				return false;

			return true;
		}

		// True when any direct (vision/proximity) sensor contact is an attackable enemy player-owned survivor.
		// Zombies and neutral survivors are excluded, so only enemy players interrupt or block recruitment.
		private bool HasSensedEnemyPlayerSurvivor()
		{
			if (_survivor == null || _survivor.Sensor == null)
				return false;

			_directEnemiesScratch.Clear();
			_survivor.Sensor.GetDirectKnownEnemies(_directEnemiesScratch);

			bool found = false;
			for (int i = 0; i < _directEnemiesScratch.Count; i++)
			{
				var obj = _directEnemiesScratch[i].Object;
				if (obj == null)
					continue;
				if (obj.GetComponent<ZombieCharacter>() != null)
					continue;

				var other = obj.GetComponent<Survivor>();
				if (other == null || other.Health == null || other.Health.IsAlive == false)
					continue;
				if (CharacterFactionUtility.CanSurvivorAutoAttack(_survivor, obj) == false)
					continue;

				found = true;
				break;
			}

			_directEnemiesScratch.Clear();
			return found;
		}

		// Drives an active recruitment. Returns true with movement input while pursuing. When the recruit is lost
		// (killed or sight lost) it hands off to an investigation of the last known spot; when recruited it ends the
		// task. In every non-pursuing case it returns false so the caller falls through to the normal
		// combat/investigation/looting/hold flow.
		private bool TryGetRecruitingInput(Vector3 anchor, out NetworkedInput input)
		{
			input = default;
			if (_recruiting == null || _recruiting.HasTask == false)
				return false;

			CharacterNavigator navigator = _survivor != null ? _survivor.Navigator : null;

			// Only a sensed enemy player interrupts an active recruitment; combat then takes over below.
			if (HasSensedEnemyPlayerSurvivor())
			{
				_recruiting.ClearTask(true, anchor, navigator);
				return false;
			}

			ERecruitTickResult result = _recruiting.Tick(_survivor, CreateRecruitingMoveInput, out input);
			if (result == ERecruitTickResult.Pursuing)
				return true;

			if (result == ERecruitTickResult.Lost)
			{
				Vector3 lastKnown = _recruiting.LastKnownPosition;
				_recruiting.ClearTask(false, anchor, navigator);
				// Go look where we last saw the recruit; this can re-spot it or reveal other survivors. If an
				// investigation cannot start (disabled, or a closer threat), fall back to returning to the anchor.
				if (TryStartRecruitLossInvestigation(lastKnown) == false)
					_recruiting.ClearTask(true, anchor, navigator);
			}
			else
			{
				_recruiting.ClearTask(true, anchor, navigator);
			}

			input = default;
			return false;
		}

		private bool TryStartRecruitLossInvestigation(Vector3 lastKnownPosition)
		{
			if (_survivor == null)
				return false;

			int tick = _survivor.Runner != null ? _survivor.Runner.Tick : 0;
			// recruitmentOrigin: true -> this investigation is part of recruiting and ignores the investigate setting.
			return TryStartInvestigation(lastKnownPosition, tick, false, null, true, false, true);
		}

		// A travel detour may start while still moving to a player order (unlike the satisfied-order recruiting),
		// but only for a neutral within RecruitDetourDistance, and not while in combat or sensing an enemy player.
		private bool CanStartTravelDetourRecruiting()
		{
			if (_settings.RecruitNeutralSurvivors == false || _recruiting == null)
				return false;
			if (_recruiting.RecruitDetourDistance <= 0f)
				return false;
			// Brief cooldown after a detour ends so a sensed-but-unreachable neutral cannot make the survivor
			// start-and-abort a detour every tick (which would re-path twice per tick).
			if (Time.timeSinceLevelLoad < _nextTravelDetourTime)
				return false;
			if (ShouldPauseTemporaryNonCombatBehavior())
				return false;
			if (HasSensedEnemyPlayerSurvivor())
				return false;

			return true;
		}

		// In-travel detour: while the survivor is still moving toward a player order, allow a short, distance-limited
		// detour to recruit a nearby neutral, then resume the order. Returns true with movement while detouring;
		// otherwise false so the caller's normal order movement resumes (the navigator is cleared so it re-paths).
		private bool TryGetTravelDetourRecruitingInput(out NetworkedInput input)
		{
			input = default;
			if (_recruiting == null)
				return false;

			CharacterNavigator navigator = _survivor != null ? _survivor.Navigator : null;

			if (_recruiting.HasTask)
			{
				// Only a sensed enemy player interrupts the detour; the order then resumes (combat merges into it).
				if (HasSensedEnemyPlayerSurvivor())
				{
					EndTravelDetour(navigator);
					return false;
				}

				ERecruitTickResult result = _recruiting.Tick(_survivor, CreateRecruitingMoveInput, out input);
				if (result == ERecruitTickResult.Pursuing)
					return true;

				// Recruited, or lost the target -> drop the detour and resume the player order. Unlike satisfied
				// recruiting, a travel detour does not branch into a loss investigation; finishing the order wins.
				EndTravelDetour(navigator);
				input = default;
				return false;
			}

			if (CanStartTravelDetourRecruiting()
			    && _recruiting.TryStart(_survivor, _settings.RecruitNeutralSurvivors, _recruiting.RecruitDetourDistance))
			{
				if (_recruiting.Tick(_survivor, CreateRecruitingMoveInput, out input) == ERecruitTickResult.Pursuing)
					return true;

				EndTravelDetour(navigator);
				input = default;
			}

			return false;
		}

		private void EndTravelDetour(CharacterNavigator navigator)
		{
			_recruiting.ClearTask(false, default, navigator);
			// Drop the recruit destination so the resuming player order re-paths from the current position.
			navigator?.ClearDestination();
			_nextTravelDetourTime = Time.timeSinceLevelLoad + TravelDetourRetryDelay;
		}

		private bool CanAIBehaviorOverridePlayerOrder()
		{
			switch (_assignment)
			{
				case ENonCombatAssignment.HoldPosition:
					return true;
				case ENonCombatAssignment.AssignedArea:
					return _playerAssignmentSatisfiedOnce;
				case ENonCombatAssignment.FollowSurvivor:
				case ENonCombatAssignment.MoveToPoint:
				default:
					return false;
			}
		}

		private NetworkedInput CreateMoveInput(Vector3 steeringTarget, bool stopAtTarget, float stoppingDistance)
		{
			return CreateMoveInput(steeringTarget, stopAtTarget, stoppingDistance, true, true, true);
		}

		private NetworkedInput CreatePlayerOrderMoveInput(Vector3 steeringTarget, bool stopAtTarget, float stoppingDistance)
		{
			return CreateMoveInput(steeringTarget, stopAtTarget, stoppingDistance, true, false, false);
		}

		// Recruitment movement lets the survivor aim and fire at zombies (allowCombatInput) but never lets combat
		// take over its movement (allowCombatMovement = false), so it keeps beelining to the neutral instead of
		// retreating like the zombie combat AI would. Lost-combat investigation is also suppressed. Enemy players
		// are handled one level up (they interrupt recruitment entirely).
		private NetworkedInput CreateRecruitingMoveInput(Vector3 steeringTarget, bool stopAtTarget, float stoppingDistance)
		{
			return CreateMoveInput(steeringTarget, stopAtTarget, stoppingDistance, true, false, false);
		}

		private NetworkedInput CreateMoveInput(
			Vector3 steeringTarget,
			bool stopAtTarget,
			float stoppingDistance,
			bool allowCombatInput,
			bool allowCombatMovement,
			bool allowLostCombatInvestigation)
		{
			Vector3 toTarget = steeringTarget - _survivor.transform.position;
			toTarget.y = 0f;

			if (stopAtTarget && toTarget.sqrMagnitude <= stoppingDistance * stoppingDistance)
				return allowCombatMovement || allowLostCombatInvestigation ? GetHoldInput() : GetPlayerOrderHoldInput();
			if (toTarget.sqrMagnitude < 0.001f)
				return allowCombatMovement || allowLostCombatInvestigation ? GetHoldInput() : GetPlayerOrderHoldInput();

			Vector3 moveDirection = toTarget.normalized;
			float desiredYaw = Quaternion.LookRotation(moveDirection).eulerAngles.y;
			float currentYaw = _survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);

			var input = new NetworkedInput
			{
				LookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick)),
				MoveDirection = Vector2.up,
			};

			if (allowCombatInput && TryGetCombatInput(out var combatInput, true, allowCombatMovement, allowLostCombatInvestigation))
			{
				input.LookRotationDelta = combatInput.LookRotationDelta;
				input.Buttons = combatInput.Buttons;
				input.MoveDirection = allowCombatMovement && combatInput.MoveDirection != Vector2.zero
					? combatInput.MoveDirection
					: GetLocalMoveDirection(moveDirection, currentYaw + input.LookRotationDelta.y);
			}

			return input;
		}

		private NetworkedInput GetPlayerOrderHoldInput()
		{
			if (TryGetCombatInput(out var combatInput, false, false, false))
				return combatInput;

			return GetIdleLookAroundInput();
		}

		private bool TryGetCombatInput(
			out NetworkedInput input,
			bool isMoving = false,
			bool allowCombatMovement = true,
			bool allowLostCombatInvestigation = true)
		{
			input = default;

			if (_combat == null)
				return false;

			if (_combat.TryGetDirectTarget(out var enemy, out bool hasLineOfFire))
			{
				TryAlertAlliesAboutDirectEnemy(enemy);
				bool combatEnabled = _settings.AllowCombatAIActivation;
				bool allowLostInvestigationForTarget = allowLostCombatInvestigation && IsLostCombatInvestigationTarget(enemy);

				if (hasLineOfFire)
				{
					if (allowLostInvestigationForTarget)
						RememberCombatEnemy(enemy);
					if (_combat.TryGetInput(
						    enemy,
						    hasLineOfFire,
						    out input,
						    isMoving,
						    allowCombatMovement,
						    combatEnabled))
						return true;
				}

				if (allowLostInvestigationForTarget)
					_combat.NotifyTargetLost();
				if (allowLostInvestigationForTarget && TryStartLostCombatInvestigation(enemy))
					return false;

				return false;
			}

			if (allowLostCombatInvestigation)
			{
				_combat.NotifyTargetLost();
				TryStartLostCombatInvestigation();
			}
			return false;
		}

		private bool HasCombatTargetWithLineOfFire()
		{
			return _combat != null &&
			       _combat.HasTargetWithLineOfFire();
		}

		private void RememberCombatEnemy(KnownEnemyInfo enemy)
		{
			_lastCombatEnemy = enemy.Object;
			_lastCombatEnemyPosition = enemy.LastKnownPosition;
			_lastCombatEnemyTick = enemy.Tick;
			_hasLastCombatEnemy = true;
		}

		private void ClearRememberedCombatEnemy()
		{
			_lastCombatEnemy = null;
			_lastCombatEnemyPosition = default;
			_lastCombatEnemyTick = 0;
			_hasLastCombatEnemy = false;
		}

		private bool TryStartLostCombatInvestigation(KnownEnemyInfo enemy)
		{
			if (_hasLastCombatEnemy == false || enemy.Object != _lastCombatEnemy)
				return false;
			if (IsRememberedEnemyAlive(_lastCombatEnemy) == false)
			{
				_hasLastCombatEnemy = false;
				return false;
			}
			if (enemy.Tick < _lastCombatEnemyTick)
				return false;

			_lastCombatEnemyPosition = enemy.LastKnownPosition;
			_lastCombatEnemyTick = enemy.Tick;
			_hasLastCombatEnemy = false;
			return TryStartInvestigation(_lastCombatEnemyPosition, _lastCombatEnemyTick, true, _lastCombatEnemy, true, true);
		}

		private bool TryStartLostCombatInvestigation()
		{
			if (_hasLastCombatEnemy == false)
				return false;
			if (IsRememberedEnemyAlive(_lastCombatEnemy) == false)
			{
				_hasLastCombatEnemy = false;
				return false;
			}

			_hasLastCombatEnemy = false;
			return TryStartInvestigation(_lastCombatEnemyPosition, _lastCombatEnemyTick, true, _lastCombatEnemy, true, true);
		}

		private void TryAlertAlliesAboutDirectEnemy(KnownEnemyInfo enemy)
		{
			if (_settings.InvestigateSuspiciousStimuli == false)
				return;
			if (_survivor == null || _survivor.IsActiveCharacter())
				return;
			if (enemy.Tick <= _lastAlertedDirectEnemyTick)
				return;

			_lastAlertedDirectEnemyTick = enemy.Tick;
			_investigation?.AlertNearbyAllies(_survivor, enemy.LastKnownPosition, enemy.Tick, _settings.InvestigateSuspiciousStimuli, enemy.Object);
		}

		private static bool IsLostCombatInvestigationTarget(KnownEnemyInfo enemy)
		{
			return enemy.Object != null && enemy.Object.GetComponent<Survivor>() != null;
		}

		private static bool IsRememberedEnemyAlive(NetworkObject enemy)
		{
			if (enemy == null)
				return false;

			var survivor = enemy.GetComponent<Survivor>();
			if (survivor != null)
				return survivor.Health != null && survivor.Health.IsAlive;

			var zombie = enemy.GetComponent<ZombieCharacter>();
			if (zombie != null)
				return zombie.Health != null && zombie.Health.IsAlive;

			return false;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static float RandomRangeClamped(float min, float max)
		{
			min = Mathf.Max(0f, min);
			max = Mathf.Max(min, max);
			if (Mathf.Approximately(min, max))
				return min;

			return Random.Range(min, max);
		}

		private static Vector2 GetLocalMoveDirection(Vector3 worldDirection, float lookYaw)
		{
			Vector3 localDirection = Quaternion.Inverse(Quaternion.Euler(0f, lookYaw, 0f)) * worldDirection;
			var moveDirection = new Vector2(localDirection.x, localDirection.z);
			return moveDirection.sqrMagnitude > 1f ? moveDirection.normalized : moveDirection;
		}
	}
}
