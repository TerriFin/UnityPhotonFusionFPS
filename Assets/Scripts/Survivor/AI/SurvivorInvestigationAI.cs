using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorInvestigationAI : MonoBehaviour
	{
		[Header("Movement")]
		public float InvestigationStoppingDistance = 1.25f;
		public float DirectFallbackDistance = 1.5f;

		[Header("Alerts")]
		public float AllyAlertRadius = 8f;

		[Header("Look Around")]
		public float LookDurationMin = 3f;
		public float LookDurationMax = 5f;
		public float LookRotationIntervalMin = 0.75f;
		public float LookRotationIntervalMax = 1.5f;
		public float MaxYawDegreesPerTick = 8f;

		private readonly List<Survivor> _alertedAllies = new(8);
		private Vector3 _investigationTarget;
		private bool _hasInvestigationTarget;
		private bool _isLookingAroundInvestigationTarget;
		private bool _returningFromInvestigation;
		private float _investigationLookEndTime;
		private float _nextInvestigationLookRotationTime;
		private float _investigationLookYaw;
		private int _lastHandledInvestigationTick;

		public bool HasTask => _hasInvestigationTarget;
		public bool IsReturning => _returningFromInvestigation;

		public bool TryStart(
			Survivor survivor,
			bool investigateEnabled,
			bool canStart,
			bool shouldPause,
			Vector3 target,
			int stimulusTick,
			bool alertAllies,
			bool allowSameTick = false,
			bool force = false)
		{
			if (investigateEnabled == false)
				return false;
			if (canStart == false)
				return false;
			if (force == false && shouldPause)
				return false;
			if (allowSameTick == false && stimulusTick <= _lastHandledInvestigationTick)
				return false;
			if (TryResolveInvestigationTarget(survivor, target, out var investigationDestination) == false)
				return false;

			StartInvestigation(survivor, investigationDestination, stimulusTick, alertAllies);
			return true;
		}

		public NetworkedInput GetInput(
			Survivor survivor,
			Vector3 anchor,
			Func<Vector3, bool, float, NetworkedInput> createMoveInput,
			Func<NetworkedInput> getReturnToAnchorInput)
		{
			if (_isLookingAroundInvestigationTarget)
				return GetLookAroundInput(survivor, anchor, getReturnToAnchorInput);

			if (FlatDistanceSqr(survivor.transform.position, _investigationTarget) <= InvestigationStoppingDistance * InvestigationStoppingDistance)
			{
				BeginLookAround(survivor);
				return GetLookAroundInput(survivor, anchor, getReturnToAnchorInput);
			}

			var navigator = survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(_investigationTarget);

				navigator.Tick(survivor.transform.position);
				if (navigator.TryGetSteeringTarget(survivor.transform.position, out var steeringTarget))
					return createMoveInput(steeringTarget, false, InvestigationStoppingDistance);
			}

			if (FlatDistanceSqr(survivor.transform.position, _investigationTarget) <= DirectFallbackDistance * DirectFallbackDistance)
				return createMoveInput(_investigationTarget, true, InvestigationStoppingDistance);

			ClearTask(true, anchor, survivor.Navigator);
			return getReturnToAnchorInput();
		}

		public void ClearTask(bool returnToAnchor, Vector3 anchor, CharacterNavigator navigator)
		{
			_investigationTarget = default;
			_hasInvestigationTarget = false;
			_isLookingAroundInvestigationTarget = false;
			_returningFromInvestigation = returnToAnchor;
			_investigationLookEndTime = 0f;
			_nextInvestigationLookRotationTime = 0f;
			_investigationLookYaw = 0f;

			if (returnToAnchor)
				navigator?.SetDestination(anchor);
		}

		public void CompleteReturn()
		{
			_returningFromInvestigation = false;
		}

		public void AlertNearbyAllies(Survivor survivor, Vector3 target, int stimulusTick, bool investigateEnabled)
		{
			if (survivor == null || investigateEnabled == false || AllyAlertRadius <= 0f)
				return;
			if (survivor.IsActiveCharacter())
				return;

			_alertedAllies.Clear();

			float radiusSqr = AllyAlertRadius * AllyAlertRadius;
			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				var ally = sensor.Survivor;
				if (ally == null || ally == survivor || ally.Health == null || ally.Health.IsAlive == false)
					continue;
				if (ally.OwnerRef != survivor.OwnerRef)
					continue;
				if (ally.IsActiveCharacter())
					continue;
				if (HasOwnCombatLineOfFire(ally))
					continue;
				if (FlatDistanceSqr(ally.transform.position, survivor.transform.position) > radiusSqr)
					continue;

				_alertedAllies.Add(ally);
			}

			for (int i = 0; i < _alertedAllies.Count; i++)
			{
				_alertedAllies[i].ReceiveInvestigationAlert(target, stimulusTick);
			}

			_alertedAllies.Clear();
		}

		private void StartInvestigation(Survivor survivor, Vector3 target, int stimulusTick, bool alertAllies)
		{
			_investigationTarget = target;
			_hasInvestigationTarget = true;
			_isLookingAroundInvestigationTarget = false;
			_returningFromInvestigation = false;
			_lastHandledInvestigationTick = stimulusTick;
			survivor.Navigator?.SetDestination(target);

			if (alertAllies)
				AlertNearbyAllies(survivor, target, stimulusTick, true);
		}

		private void BeginLookAround(Survivor survivor)
		{
			_isLookingAroundInvestigationTarget = true;
			survivor.Navigator?.ClearDestination();

			float now = Time.timeSinceLevelLoad;
			_investigationLookEndTime = now + RandomRangeClamped(LookDurationMin, LookDurationMax);
			PickNextLookYaw(now);
		}

		private NetworkedInput GetLookAroundInput(Survivor survivor, Vector3 anchor, Func<NetworkedInput> getReturnToAnchorInput)
		{
			float now = Time.timeSinceLevelLoad;
			if (now >= _investigationLookEndTime)
			{
				ClearTask(true, anchor, survivor.Navigator);
				return getReturnToAnchorInput();
			}

			if (now >= _nextInvestigationLookRotationTime)
				PickNextLookYaw(now);

			float currentYaw = survivor.KCC.GetLookRotation(false, true).y;
			float yawDelta = Mathf.DeltaAngle(currentYaw, _investigationLookYaw);
			if (Mathf.Abs(yawDelta) < 0.1f)
				return default;

			return new NetworkedInput
			{
				LookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -MaxYawDegreesPerTick, MaxYawDegreesPerTick)),
			};
		}

		private void PickNextLookYaw(float now)
		{
			_investigationLookYaw = UnityEngine.Random.Range(0f, 360f);
			_nextInvestigationLookRotationTime = now + RandomRangeClamped(LookRotationIntervalMin, LookRotationIntervalMax);
		}

		private static bool TryResolveInvestigationTarget(Survivor survivor, Vector3 target, out Vector3 investigationDestination)
		{
			investigationDestination = target;

			var navigator = survivor != null ? survivor.Navigator : null;
			if (navigator == null)
				return true;

			return navigator.TryFindReachablePoint(survivor.transform.position, target, out investigationDestination);
		}

		private static bool HasOwnCombatLineOfFire(Survivor survivor)
		{
			return survivor != null &&
			       survivor.AIShooting != null &&
			       survivor.AIShooting.TryGetDirectTarget(out _, out bool hasLineOfFire) &&
			       hasLineOfFire;
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

			return UnityEngine.Random.Range(min, max);
		}
	}
}
