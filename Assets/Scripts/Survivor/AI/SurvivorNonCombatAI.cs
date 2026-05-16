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
		public bool AllowCombatAIActivation;

		public static SurvivorNonCombatAISettings Default => new SurvivorNonCombatAISettings
		{
			CollectVisiblePickups = true,
			InvestigateSuspiciousStimuli = true,
			AllowCombatAIActivation = true,
		};

		public static SurvivorNonCombatAISettings Passive => new SurvivorNonCombatAISettings
		{
			CollectVisiblePickups = false,
			InvestigateSuspiciousStimuli = false,
			AllowCombatAIActivation = false,
		};
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorNonCombatAI : MonoBehaviour, Survivor.ICharacterInputSource
	{
		[Header("Assignment")]
		public float DefaultFollowStoppingDistance = 2f;
		public float DefaultMoveStoppingDistance = 1.25f;
		public float DirectFallbackDistance = 4f;
		public float MaxYawDegreesPerTick = 8f;

		[Header("Idle Look")]
		public float IdleLookRotationIntervalMin = 4f;
		public float IdleLookRotationIntervalMax = 8f;
		public float IdleLookMaxYawDegreesPerTick = 2f;

		private Survivor _survivor;
		private SurvivorLootingAI _looting;
		private SurvivorInvestigationAI _investigation;
		private SurvivorAssignedAreaAI _assignedArea;
		private SurvivorCombatAI _combat;
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

		public static bool TryBuildAssignedAreaPatrolPoints(Survivor survivor, Vector3 center, float radius, out Vector3[] patrolPoints)
		{
			patrolPoints = null;
			if (survivor == null)
				return false;

			var assignedArea = survivor.GetComponent<SurvivorAssignedAreaAI>();
			if (assignedArea == null)
				assignedArea = survivor.gameObject.AddComponent<SurvivorAssignedAreaAI>();

			return assignedArea.TryBuildReachablePointSet(survivor, center, radius, out patrolPoints);
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

		public void SetSettings(SurvivorNonCombatAISettings settings)
		{
			_settings = settings;

			EnsureBehaviorComponents();

			if (_settings.CollectVisiblePickups == false && (_looting != null && (_looting.HasTask || _looting.IsReturning)))
				_looting.ClearTask(true, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
			if (_settings.InvestigateSuspiciousStimuli == false && (_investigation != null && (_investigation.HasTask || _investigation.IsReturning)))
				_investigation.ClearTask(true, _anchorPosition, _survivor != null ? _survivor.Navigator : null);
		}

		public void ReceiveInvestigationAlert(Vector3 target, int stimulusTick)
		{
			TryStartInvestigation(target, stimulusTick, false);
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick)
		{
			TryStartInvestigation(target, stimulusTick, true);
		}

		private bool TryStartInvestigation(Vector3 target, int stimulusTick, bool alertAllies)
		{
			return TryStartInvestigation(target, stimulusTick, alertAllies, false, false);
		}

		private bool TryStartInvestigation(Vector3 target, int stimulusTick, bool alertAllies, bool allowSameTick, bool force)
		{
			EnsureBehaviorComponents();
			return _investigation != null &&
			       _investigation.TryStart(
				       _survivor,
				       _settings.InvestigateSuspiciousStimuli,
				       CanStartInvestigationBehavior(),
				       ShouldPauseTemporaryNonCombatBehavior(),
				       target,
				       stimulusTick,
				       alertAllies,
				       allowSameTick,
				       force);
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
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
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
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
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
			_assignedArea?.ClearTask(_survivor != null ? _survivor.Navigator : null);
			_combat?.ClearMovementTask();
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
				return _assignedArea.GetInput(
					_survivor,
					_anchorPosition,
					_assignedAreaEntryPoint,
					_assignmentRadius,
					_assignedAreaPatrolPoints,
					CreatePlayerOrderMoveInput,
					GetPlayerOrderHoldInput);
			}

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

			if (_investigation != null && _investigation.HasTask)
				return _investigation.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			if ((_looting != null && _looting.IsReturning) || (_investigation != null && _investigation.IsReturning))
				return GetReturnToAssignedAreaInput();

			if (_looting != null && _looting.HasTask)
				return _looting.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			if (_looting != null && _looting.TryStart(_survivor, _settings.CollectVisiblePickups, ShouldPauseTemporaryNonCombatBehavior()))
				return _looting.GetInput(_survivor, _assignedAreaEntryPoint, CreateMoveInput, GetReturnToAssignedAreaInput);

			return _assignedArea.GetInput(_survivor, _anchorPosition, _assignedAreaEntryPoint, _assignmentRadius, _assignedAreaPatrolPoints, CreateMoveInput, GetHoldInput);
		}

		private NetworkedInput GetMoveToPointInput()
		{
			var navigator = _survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(_anchorPosition);

				navigator.Tick(_survivor.transform.position);
				if (navigator.IsDestinationReached)
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

			if (_investigation != null && _investigation.HasTask)
				return _investigation.GetInput(_survivor, _anchorPosition, CreateMoveInput, GetReturnToAnchorInput);

			if ((_looting != null && _looting.IsReturning) || (_investigation != null && _investigation.IsReturning))
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

			if (_settings.AllowCombatAIActivation == false || _combat == null)
				return false;

			if (_combat.TryGetDirectTarget(out var enemy, out bool hasLineOfFire))
			{
				TryAlertAlliesAboutDirectEnemy(enemy);

				if (hasLineOfFire)
				{
					RememberCombatEnemy(enemy);
					if (_combat.TryGetInput(enemy, hasLineOfFire, out input, isMoving, allowCombatMovement))
						return true;
				}

				if (allowLostCombatInvestigation && TryStartLostCombatInvestigation(enemy))
					return false;

				return false;
			}

			if (allowLostCombatInvestigation)
				TryStartLostCombatInvestigation();
			return false;
		}

		private bool HasCombatTargetWithLineOfFire()
		{
			return _settings.AllowCombatAIActivation &&
			       _combat != null &&
			       _combat.HasTargetWithLineOfFire();
		}

		private void RememberCombatEnemy(KnownEnemyInfo enemy)
		{
			_lastCombatEnemy = enemy.Object;
			_lastCombatEnemyPosition = enemy.LastKnownPosition;
			_lastCombatEnemyTick = enemy.Tick;
			_hasLastCombatEnemy = true;
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
			return TryStartInvestigation(_lastCombatEnemyPosition, _lastCombatEnemyTick, true, true, true);
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
			return TryStartInvestigation(_lastCombatEnemyPosition, _lastCombatEnemyTick, true, true, true);
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
			_investigation?.AlertNearbyAllies(_survivor, enemy.LastKnownPosition, enemy.Tick, _settings.InvestigateSuspiciousStimuli);
		}

		private static bool IsRememberedEnemyAlive(NetworkObject enemy)
		{
			if (enemy == null)
				return false;

			var survivor = enemy.GetComponent<Survivor>();
			return survivor == null || (survivor.Health != null && survivor.Health.IsAlive);
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
