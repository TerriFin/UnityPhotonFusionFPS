using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.Serialization;

namespace SimpleFPS
{
	[Serializable]
	public sealed class SurvivorAICommandSettings
	{
		[FormerlySerializedAs("FollowCommandRadius")]
		[FormerlySerializedAs("AutoShootCommandRadius")]
		public float CommandRadius = 12f;
		public float MoveCommandMaxDistance = 80f;
		public float AssignedAreaMinRadius = 3f;
		public float AssignedAreaMaxRadius = 16f;
		public LayerMask MoveCommandHitMask = 1;
		// Meters between adjacent lanes when a group move / assigned-area order spreads survivors across
		// the corridor. Three survivors at 0.9 produce lanes at -0.9, 0, +0.9.
		public float LaneSpacing = 0.9f;
		// Caps the total sideways offset so very large groups do not spread wider than useful roads.
		// Set to 0 or less to leave the lane offset uncapped.
		public float MaxLaneOffset = 2.5f;
		// Within this distance to the final destination the lane offset fades to 0 so a group still
		// converges on the goal instead of stopping in a fan shape.
		public float LaneOffsetTaperDistance = 4f;
		// The lane offset softens within this distance of the current path corner so a perpendicular
		// shove doesn't make a survivor cut a corner from the inside.
		public float LaneOffsetCornerSoftenDistance = 1.5f;
		// NavMesh.SamplePosition clamp distance applied to the offset steering target, so a sideways
		// shove never pushes the target into a wall.
		public float LaneOffsetSampleDistance = 1.0f;
		// Reject offset steering targets when the straight NavMesh segment from the survivor to the
		// offset target crosses a blocked edge. This keeps indoor lane spread from cutting through props.
		public bool ValidateLaneOffsetPath = true;
	}

	public readonly struct SurvivorAICommand
	{
		private readonly Func<Survivor, Survivor.ICharacterInputSource> _createInputSource;

		// Commands toward a static destination (MoveTo / AssignedArea) spread the group across lanes so
		// the squad fills the corridor instead of stacking on the optimal path. Follow / Idle commands
		// don't benefit from a fixed sideways offset (Follow's target moves, Idle doesn't path) so they
		// opt out.
		public readonly bool AllowsLaneSpread;

		public SurvivorAICommand(Func<Survivor, Survivor.ICharacterInputSource> createInputSource, bool allowsLaneSpread = false)
		{
			_createInputSource = createInputSource;
			AllowsLaneSpread = allowsLaneSpread;
		}

		public Survivor.ICharacterInputSource CreateInputSource(Survivor survivor)
		{
			return _createInputSource != null ? _createInputSource(survivor) : SurvivorNonCombatAI.HoldPosition(survivor, survivor.NonCombatAISettings);
		}

		public static SurvivorAICommand Idle()
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.HoldPosition(survivor, survivor.NonCombatAISettings));
		}

		public static SurvivorAICommand Follow(Survivor target)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.Follow(survivor, target, survivor.NonCombatAISettings));
		}

		public static SurvivorAICommand MoveTo(Vector3 destination)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.MoveTo(survivor, destination, survivor.NonCombatAISettings), allowsLaneSpread: true);
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, survivor.NonCombatAISettings), allowsLaneSpread: true);
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius, Vector3 entryPoint)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, entryPoint, survivor.NonCombatAISettings), allowsLaneSpread: true);
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius, Vector3 entryPoint, Vector3[] patrolPoints)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, entryPoint, patrolPoints, survivor.NonCombatAISettings), allowsLaneSpread: true);
		}
	}

	public sealed class SurvivorAICommandService
	{
		private readonly Gameplay _gameplay;
		private readonly Dictionary<PlayerRef, Dictionary<int, Survivor>> _survivorsByOwner;
		private readonly SurvivorAICommandSettings _settings;

		// Reusable buffer for sorting group-command targets by CharacterIndex so lane assignment is
		// deterministic (same survivor always lands in the same lane within a given group composition).
		private readonly List<KeyValuePair<int, Survivor>> _laneTargetBuffer = new();
		private static readonly Comparison<KeyValuePair<int, Survivor>> LaneTargetComparer =
			(a, b) => a.Key.CompareTo(b.Key);

		public SurvivorAICommandService(
			Gameplay gameplay,
			Dictionary<PlayerRef, Dictionary<int, Survivor>> survivorsByOwner,
			SurvivorAICommandSettings settings)
		{
			_gameplay = gameplay;
			_survivorsByOwner = survivorsByOwner;
			_settings = settings;
		}

		public void SetNearbyTeamFollow(PlayerRef owner, int originCharacterIndex)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;

			ApplyNearbyTeamCommand(owner, data, origin, originCharacterIndex, SurvivorAICommand.Follow(origin));
		}

		public void SetNearbyTeamIdle(PlayerRef owner, int originCharacterIndex)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;

			ApplyNearbyTeamCommand(owner, data, origin, originCharacterIndex, SurvivorAICommand.Idle());
		}

		public void SetNearbyTeamNonCombatSettings(PlayerRef owner, int originCharacterIndex, bool enabled)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;

			var settings = enabled ? SurvivorNonCombatAISettings.Default : SurvivorNonCombatAISettings.Passive;
			ForEachNearbyTeamSurvivor(owner, data, origin, originCharacterIndex, survivor =>
			{
				survivor.SetNonCombatAISettings(settings);
			});
		}

		public void SetNearbyTeamCombatSettings(PlayerRef owner, int originCharacterIndex, bool enabled)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;

			var settings = enabled ? SurvivorCombatAISettings.Default : SurvivorCombatAISettings.Passive;
			ForEachNearbyTeamSurvivor(owner, data, origin, originCharacterIndex, survivor =>
			{
				survivor.SetCombatAISettings(settings);
			});
		}

		public void MoveNearbyTeamToLookPoint(PlayerRef owner, int originCharacterIndex)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;
			if (TryGetLookPoint(origin, out Vector3 destination) == false)
				return;

			ApplyNearbyTeamCommand(owner, data, origin, originCharacterIndex, SurvivorAICommand.MoveTo(destination));
		}

		public void ApplyNearbyTeamCommand(
			PlayerRef owner,
			int originCharacterIndex,
			SurvivorAICommand command)
		{
			if (TryGetCommandContext(owner, originCharacterIndex, out var data, out var origin) == false)
				return;

			ApplyNearbyTeamCommand(owner, data, origin, originCharacterIndex, command);
		}

		public void ApplySelectedTeamCommand(PlayerRef owner, long selectedCharacterMask, SurvivorAICommand command)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;

			_laneTargetBuffer.Clear();
			foreach (var pair in survivors)
			{
				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, pair.Key, pair.Value) == false)
					continue;
				_laneTargetBuffer.Add(pair);
			}
			_laneTargetBuffer.Sort(LaneTargetComparer);

			for (int i = 0; i < _laneTargetBuffer.Count; i++)
			{
				var survivor = _laneTargetBuffer[i].Value;
				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);

				AssignLaneOffset(survivor, i, _laneTargetBuffer.Count, command.AllowsLaneSpread, _settings);
			}

			_laneTargetBuffer.Clear();
		}

		public void ApplySelectedTeamAssignedArea(PlayerRef owner, long selectedCharacterMask, Vector3 center, float radius)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;
			if (TryBuildSharedAssignedAreaPatrolPoints(data, selectedCharacterMask, survivors, center, radius, out Vector3[] patrolPoints) == false)
				return;

			Vector3 entryPoint = patrolPoints[0];
			var command = SurvivorAICommand.AssignedArea(center, radius, entryPoint, patrolPoints);

			_laneTargetBuffer.Clear();
			foreach (var pair in survivors)
			{
				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, pair.Key, pair.Value) == false)
					continue;
				_laneTargetBuffer.Add(pair);
			}
			_laneTargetBuffer.Sort(LaneTargetComparer);

			for (int i = 0; i < _laneTargetBuffer.Count; i++)
			{
				var survivor = _laneTargetBuffer[i].Value;
				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);

				AssignLaneOffset(survivor, i, _laneTargetBuffer.Count, command.AllowsLaneSpread, _settings);
			}

			_laneTargetBuffer.Clear();
		}

		public void ApplySelectedTeamFollow(PlayerRef owner, long selectedCharacterMask, int targetCharacterIndex)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;

			if (survivors.TryGetValue(targetCharacterIndex, out var target) == false || target == null)
				return;
			if ((data.AliveCharacterMask & (1L << targetCharacterIndex)) == 0)
				return;

			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (characterIndex == targetCharacterIndex)
					continue;
				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;

				survivor.SetAI(SurvivorAICommand.Follow(target).CreateInputSource(survivor));
				// Follow tracks a moving target, so a persistent lateral offset would look strange — reset.
				ClearLaneOffset(survivor);
			}
		}

		public void ApplySelectedTeamNonCombatSettings(PlayerRef owner, long selectedCharacterMask, bool enabled)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;

			var settings = enabled ? SurvivorNonCombatAISettings.Default : SurvivorNonCombatAISettings.Passive;
			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;

				survivor.SetNonCombatAISettings(settings);
			}
		}

		public void ApplySelectedTeamCombatSettings(PlayerRef owner, long selectedCharacterMask, bool enabled)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;

			var settings = enabled ? SurvivorCombatAISettings.Default : SurvivorCombatAISettings.Passive;
			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;

				survivor.SetCombatAISettings(settings);
			}
		}

		private void ApplyNearbyTeamCommand(
			PlayerRef owner,
			PlayerData data,
			Survivor origin,
			int originCharacterIndex,
			SurvivorAICommand command)
		{
			if (_survivorsByOwner.TryGetValue(owner, out var survivors) == false)
				return;

			float radius = Mathf.Max(0f, _settings.CommandRadius);
			float radiusSqr = radius * radius;
			long aliveMask = data.AliveCharacterMask;

			_laneTargetBuffer.Clear();
			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (survivor == null || characterIndex == originCharacterIndex)
					continue;
				if ((aliveMask & (1L << characterIndex)) == 0)
					continue;

				Vector3 offset = survivor.transform.position - origin.transform.position;
				offset.y = 0f;
				if (offset.sqrMagnitude > radiusSqr)
					continue;

				_laneTargetBuffer.Add(pair);
			}
			_laneTargetBuffer.Sort(LaneTargetComparer);

			for (int i = 0; i < _laneTargetBuffer.Count; i++)
			{
				var survivor = _laneTargetBuffer[i].Value;
				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);

				AssignLaneOffset(survivor, i, _laneTargetBuffer.Count, command.AllowsLaneSpread, _settings);
			}

			_laneTargetBuffer.Clear();
		}

		private bool TryGetLookPoint(Survivor origin, out Vector3 destination)
		{
			destination = default;

			if (origin == null || origin.Runner == null || origin.CameraHandle == null)
				return false;

			int hitMask = _settings.MoveCommandHitMask.value != 0 ? _settings.MoveCommandHitMask.value : LayerMask.GetMask("Default");
			float maxDistance = Mathf.Max(1f, _settings.MoveCommandMaxDistance);
			if (origin.Runner.GetPhysicsScene().Raycast(
				    origin.CameraHandle.position,
				    origin.KCC.LookDirection,
				    out var hit,
				    maxDistance,
				    hitMask,
				    QueryTriggerInteraction.Ignore) == false)
				return false;

			destination = hit.point;
			return true;
		}

		private bool TryGetCommandContext(PlayerRef owner, int originCharacterIndex, out PlayerData data, out Survivor origin)
		{
			data = default;
			origin = null;

			if (_gameplay.HasStateAuthority == false)
				return false;

			if (_gameplay.PlayerData.TryGet(owner, out data) == false)
				return false;

			origin = _gameplay.GetSurvivor(owner, originCharacterIndex);
			return origin != null && origin.Health.IsAlive;
		}

		private bool TryGetSelectedCommandContext(PlayerRef owner, long selectedCharacterMask, out PlayerData data, out Dictionary<int, Survivor> survivors)
		{
			data = default;
			survivors = null;

			if (_gameplay.HasStateAuthority == false)
				return false;

			if (selectedCharacterMask == 0)
				return false;

			if (_gameplay.PlayerData.TryGet(owner, out data) == false)
				return false;

			return _survivorsByOwner.TryGetValue(owner, out survivors);
		}

		private bool IsSelectedCommandTargetValid(PlayerData data, long selectedCharacterMask, int characterIndex, Survivor survivor)
		{
			if (survivor == null || survivor.Health == null || survivor.Health.IsAlive == false)
				return false;
			if (characterIndex == data.ActiveCharacterIndex)
				return false;
			if ((selectedCharacterMask & (1L << characterIndex)) == 0)
				return false;
			if ((data.AliveCharacterMask & (1L << characterIndex)) == 0)
				return false;

			return true;
		}

		// Distribute survivors across lanes so they fill the corridor instead of stacking on the optimal
		// path. Order in the group determines lane: 0 -> centre, 1 -> +1, 2 -> -1, 3 -> +2, 4 -> -2, ...
		// This keeps the group symmetric around the leader and stays cheap (no state, no lookups).
		// Commands that don't want a static offset (Follow, Idle) pass allowsLaneSpread=false and reset
		// the lane so a previous group order doesn't leak into them.
		private static void AssignLaneOffset(Survivor survivor, int orderIndex, int groupSize, bool allowsLaneSpread, SurvivorAICommandSettings settings)
		{
			var navigator = survivor != null ? survivor.Navigator : null;
			if (navigator == null)
				return;

			float spacing = settings != null ? settings.LaneSpacing : 0f;
			if (allowsLaneSpread == false || groupSize <= 1 || spacing <= 0f)
			{
				navigator.LaneOffset = 0f;
				return;
			}

			int laneIndex;
			if (orderIndex == 0)
				laneIndex = 0;
			else
			{
				int magnitude = (orderIndex + 1) / 2;
				laneIndex = (orderIndex % 2 == 1) ? magnitude : -magnitude;
			}

			float laneOffset = laneIndex * spacing;
			float maxLaneOffset = settings != null ? settings.MaxLaneOffset : 0f;
			if (maxLaneOffset > 0f)
				laneOffset = Mathf.Clamp(laneOffset, -maxLaneOffset, maxLaneOffset);

			navigator.LaneOffset = laneOffset;
			// Push the corridor-shape knobs onto the navigator each assignment so retuning the Gameplay
			// settings takes effect without requiring a domain reload or prefab edit.
			navigator.MaxLaneOffset = maxLaneOffset;
			navigator.LaneOffsetTaperDistance = settings.LaneOffsetTaperDistance;
			navigator.LaneOffsetCornerSoftenDistance = settings.LaneOffsetCornerSoftenDistance;
			navigator.LaneOffsetSampleDistance = settings.LaneOffsetSampleDistance;
			navigator.ValidateLaneOffsetPath = settings.ValidateLaneOffsetPath;
		}

		private static void ClearLaneOffset(Survivor survivor)
		{
			var navigator = survivor != null ? survivor.Navigator : null;
			if (navigator != null)
				navigator.LaneOffset = 0f;
		}

		private bool TryBuildSharedAssignedAreaPatrolPoints(
			PlayerData data,
			long selectedCharacterMask,
			Dictionary<int, Survivor> survivors,
			Vector3 center,
			float radius,
			out Vector3[] patrolPoints)
		{
			patrolPoints = null;

			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;
				if (SurvivorNonCombatAI.TryBuildAssignedAreaPatrolPoints(survivor, center, radius, out patrolPoints))
					return true;
			}

			return false;
		}

		private void ForEachNearbyTeamSurvivor(
			PlayerRef owner,
			PlayerData data,
			Survivor origin,
			int originCharacterIndex,
			Action<Survivor> apply)
		{
			if (_survivorsByOwner.TryGetValue(owner, out var survivors) == false)
				return;

			float radius = Mathf.Max(0f, _settings.CommandRadius);
			float radiusSqr = radius * radius;
			long aliveMask = data.AliveCharacterMask;

			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (survivor == null || characterIndex == originCharacterIndex)
					continue;
				if ((aliveMask & (1L << characterIndex)) == 0)
					continue;

				Vector3 offset = survivor.transform.position - origin.transform.position;
				offset.y = 0f;
				if (offset.sqrMagnitude > radiusSqr)
					continue;

				apply(survivor);
			}
		}
	}
}
