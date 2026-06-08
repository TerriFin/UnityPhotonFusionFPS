using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Result of ticking an active recruitment, reported back to <see cref="SurvivorNonCombatAI"/>.
	/// </summary>
	public enum ERecruitTickResult
	{
		/// <summary>Still walking toward the neutral; the out input carries the movement.</summary>
		Pursuing,
		/// <summary>The target is no longer neutral (recruited by us or anyone). Recruiting succeeded; return to the task anchor.</summary>
		Recruited,
		/// <summary>The target died or we lost sight of it. The survivor should investigate <see cref="SurvivorRecruitingAI.LastKnownPosition"/>.</summary>
		Lost,
	}

	/// <summary>
	/// Temporary non-combat behavior: when a non-possessed player-owned survivor senses a neutral survivor it walks
	/// over to recruit it, then returns to its player-given task. The actual recruitment (ownership/team transfer)
	/// is performed by <see cref="NeutralSurvivorOrchestrator"/> once any player-owned survivor is within its
	/// recruitment radius; this component only provides the "go to the neutral" movement and reports the outcome each
	/// tick. Priority and interruption rules live in <see cref="SurvivorNonCombatAI"/>.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class SurvivorRecruitingAI : MonoBehaviour
	{
		[Header("Movement")]
		public float RecruitStoppingDistance = 1f;
		public float DirectFallbackDistance = 1.5f;

		[Header("Detour")]
		[Tooltip("While a non-possessed survivor is still travelling to a player order, it detours to recruit a neutral within this flat distance, then resumes the order. 0 disables the detour. (Unlimited sense-range recruiting still applies once the order is reached.)")]
		public float RecruitDetourDistance = 12f;

		private readonly List<KnownEnemyInfo> _candidates = new(8);
		private Survivor _recruitTarget;
		private Vector3 _lastKnownPosition;
		private bool _hasTarget;
		private bool _returning;

		public bool HasTask => _hasTarget;
		public bool IsReturning => _returning;
		public Survivor Target => _recruitTarget;

		/// <summary>Last position the target was sensed at. Used as the investigation point when recruiting is lost.</summary>
		public Vector3 LastKnownPosition => _lastKnownPosition;

		public void ClearTask(bool returnToAnchor, Vector3 anchor, CharacterNavigator navigator)
		{
			_recruitTarget = null;
			_hasTarget = false;
			_returning = returnToAnchor;

			if (returnToAnchor)
				navigator?.SetDestination(anchor);
		}

		public void CompleteReturn()
		{
			_returning = false;
		}

		public bool TryStart(Survivor survivor, bool recruitEnabled, float maxDistance = float.PositiveInfinity)
		{
			if (recruitEnabled == false)
				return false;
			// Only player-owned survivors recruit. Neutral survivors share this component but must never use it.
			if (CharacterFactionUtility.IsPlayerOwnedSurvivor(survivor) == false)
				return false;
			if (TryFindRecruitableNeutral(survivor, maxDistance, out _recruitTarget) == false)
				return false;

			_hasTarget = true;
			_returning = false;
			_lastKnownPosition = _recruitTarget.transform.position;
			survivor.Navigator?.SetDestination(_recruitTarget.transform.position);
			return true;
		}

		public ERecruitTickResult Tick(Survivor survivor, Func<Vector3, bool, float, NetworkedInput> createMoveInput, out NetworkedInput input)
		{
			input = default;

			// Target despawned or dead -> lost. Investigate where we last saw it.
			if (_recruitTarget == null
			    || _recruitTarget.Object == null
			    || _recruitTarget.Object.IsValid == false
			    || _recruitTarget.Health == null
			    || _recruitTarget.Health.IsAlive == false)
				return ERecruitTickResult.Lost;

			// Target is no longer neutral. If it joined our team, recruiting succeeded -> return to the order.
			// If some other team took it, it is now an enemy: report Lost so the survivor investigates the last
			// known spot. (If it is still in our senses as an enemy, SurvivorNonCombatAI hands off to combat first.)
			if (_recruitTarget.IsNeutral == false)
				return (survivor != null && _recruitTarget.OwnerRef == survivor.OwnerRef)
					? ERecruitTickResult.Recruited
					: ERecruitTickResult.Lost;

			// The survivor only "knows" where the neutral is while it can sense it. Losing it (sensor memory expires)
			// hands off to an investigation of the last sensed spot rather than magically tracking it.
			if (TryGetSensedPosition(survivor, out Vector3 sensedPosition) == false)
				return ERecruitTickResult.Lost;

			_lastKnownPosition = sensedPosition;

			// The neutral patrols, so chase its live position rather than a one-time snapshot.
			Vector3 targetPosition = _recruitTarget.transform.position;
			var navigator = survivor.Navigator;
			if (navigator != null)
			{
				navigator.SetDestination(targetPosition);
				navigator.Tick(survivor.transform.position);
				if (navigator.TryGetSteeringTarget(survivor.transform.position, out var steeringTarget))
				{
					input = createMoveInput(steeringTarget, false, RecruitStoppingDistance);
					return ERecruitTickResult.Pursuing;
				}
			}

			if (FlatDistanceSqr(survivor.transform.position, targetPosition) <= DirectFallbackDistance * DirectFallbackDistance)
			{
				input = createMoveInput(targetPosition, true, RecruitStoppingDistance);
				return ERecruitTickResult.Pursuing;
			}

			// Sensed but currently unreachable -> go look at the last known spot instead of stalling.
			return ERecruitTickResult.Lost;
		}

		private bool TryGetSensedPosition(Survivor survivor, out Vector3 position)
		{
			position = default;
			if (survivor == null || survivor.Sensor == null || _recruitTarget == null || _recruitTarget.Object == null)
				return false;

			_candidates.Clear();
			survivor.Sensor.GetDirectKnownEnemies(_candidates);

			bool found = false;
			for (int i = 0; i < _candidates.Count; i++)
			{
				if (_candidates[i].Object == _recruitTarget.Object)
				{
					position = _candidates[i].LastKnownPosition;
					found = true;
					break;
				}
			}

			_candidates.Clear();
			return found;
		}

		private bool TryFindRecruitableNeutral(Survivor survivor, float maxDistance, out Survivor target)
		{
			target = null;
			if (survivor == null || survivor.Sensor == null)
				return false;

			_candidates.Clear();
			survivor.Sensor.GetDirectKnownEnemies(_candidates);

			float maxDistanceSqr = maxDistance >= float.MaxValue ? float.MaxValue : maxDistance * maxDistance;
			float closestDistanceSqr = float.MaxValue;
			Vector3 origin = survivor.transform.position;
			for (int i = 0; i < _candidates.Count; i++)
			{
				var obj = _candidates[i].Object;
				if (obj == null)
					continue;

				var candidate = obj.GetComponent<Survivor>();
				if (candidate == null || candidate == survivor)
					continue;
				if (IsRecruitTargetValid(candidate) == false)
					continue;

				float distanceSqr = FlatDistanceSqr(origin, candidate.transform.position);
				if (distanceSqr > maxDistanceSqr)
					continue;
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				target = candidate;
			}

			_candidates.Clear();
			return target != null;
		}

		private static bool IsRecruitTargetValid(Survivor target)
		{
			return target != null
			       && target.Object != null
			       && target.Object.IsValid
			       && target.Health != null
			       && target.Health.IsAlive
			       && target.IsNeutral;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
