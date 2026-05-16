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
	}

	public readonly struct SurvivorAICommand
	{
		private readonly Func<Survivor, Survivor.ICharacterInputSource> _createInputSource;

		public SurvivorAICommand(Func<Survivor, Survivor.ICharacterInputSource> createInputSource)
		{
			_createInputSource = createInputSource;
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
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.MoveTo(survivor, destination, survivor.NonCombatAISettings));
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, survivor.NonCombatAISettings));
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius, Vector3 entryPoint)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, entryPoint, survivor.NonCombatAISettings));
		}

		public static SurvivorAICommand AssignedArea(Vector3 center, float radius, Vector3 entryPoint, Vector3[] patrolPoints)
		{
			return new SurvivorAICommand(survivor => SurvivorNonCombatAI.AssignedArea(survivor, center, radius, entryPoint, patrolPoints, survivor.NonCombatAISettings));
		}
	}

	public sealed class SurvivorAICommandService
	{
		private readonly Gameplay _gameplay;
		private readonly Dictionary<PlayerRef, Dictionary<int, Survivor>> _survivorsByOwner;
		private readonly SurvivorAICommandSettings _settings;

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

			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;

				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);
			}
		}

		public void ApplySelectedTeamAssignedArea(PlayerRef owner, long selectedCharacterMask, Vector3 center, float radius)
		{
			if (TryGetSelectedCommandContext(owner, selectedCharacterMask, out var data, out var survivors) == false)
				return;
			if (TryBuildSharedAssignedAreaPatrolPoints(data, selectedCharacterMask, survivors, center, radius, out Vector3[] patrolPoints) == false)
				return;

			Vector3 entryPoint = patrolPoints[0];
			var command = SurvivorAICommand.AssignedArea(center, radius, entryPoint, patrolPoints);
			foreach (var pair in survivors)
			{
				int characterIndex = pair.Key;
				var survivor = pair.Value;

				if (IsSelectedCommandTargetValid(data, selectedCharacterMask, characterIndex, survivor) == false)
					continue;

				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);
			}
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
			ForEachNearbyTeamSurvivor(owner, data, origin, originCharacterIndex, survivor =>
			{
				var inputSource = command.CreateInputSource(survivor);
				if (inputSource != null)
					survivor.SetAI(inputSource);
			});
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
