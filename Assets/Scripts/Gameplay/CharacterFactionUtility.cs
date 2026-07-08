using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public static class CharacterFactionUtility
	{
		public static bool IsPlayerOwnedSurvivor(Survivor survivor)
		{
			return survivor != null && survivor.OwnerRef.IsRealPlayer;
		}

		public static bool IsNeutralSurvivor(Survivor survivor)
		{
			return survivor != null && survivor.OwnerRef.IsRealPlayer == false;
		}

		public static bool AreSurvivorsAllied(Survivor first, Survivor second)
		{
			if (first == null || second == null)
				return false;
			if (first.OwnerRef == second.OwnerRef)
				return true;
			if (first.OwnerRef.IsRealPlayer == false || second.OwnerRef.IsRealPlayer == false)
				return false;

			Gameplay gameplay = ResolveGameplay(first);
			return RaidModeRules.ArePlayerTeamsAllied(gameplay, first.OwnerRef, second.OwnerRef);
		}

		public static bool IsRaidClientAllianceActive(Survivor survivor)
		{
			return RaidModeRules.IsClientAllianceActive(ResolveGameplay(survivor));
		}

		public static bool CanPlayerDamageSurvivor(Gameplay gameplay, PlayerRef instigator, Survivor target)
		{
			if (target == null || instigator.IsRealPlayer == false || target.OwnerRef.IsRealPlayer == false)
				return true;

			return RaidModeRules.ArePlayerTeamsAllied(gameplay, instigator, target.OwnerRef) == false;
		}

		public static bool CanSurvivorAutoAttack(Survivor attacker, NetworkObject target)
		{
			if (attacker == null || target == null)
				return false;

			if (ZombieCharacter.GetFrom(target) != null)
				return true;

			var targetSurvivor = Survivor.GetFrom(target);
			if (targetSurvivor == null)
				return false;

			if (IsNeutralSurvivor(attacker))
				return false;

			if (IsNeutralSurvivor(targetSurvivor))
				return false;

			return AreSurvivorsAllied(attacker, targetSurvivor) == false;
		}

		public static bool CanSurvivorWeaponDamage(Survivor attacker, Health targetHealth)
		{
			if (attacker == null || targetHealth == null)
				return true;

			if (IsNeutralSurvivor(attacker))
				return targetHealth.GetComponent<ZombieCharacter>() != null;

			var targetSurvivor = targetHealth.GetComponent<Survivor>();
			if (targetSurvivor == null)
				return true;

			return AreSurvivorsAllied(attacker, targetSurvivor) == false;
		}

		public static bool IsEnemyNoiseSourceForSurvivor(Survivor listener, NetworkObject source)
		{
			if (listener == null)
				return true;
			if (source == null)
				return true;

			if (ZombieCharacter.GetFrom(source) != null)
				return true;

			// Explicit fallback instead of ?? — a missing-component lookup can return Unity's fake-null
			// placeholder in the editor, which ?? would treat as found and skip the parent search.
			var sourceSurvivor = Survivor.GetFrom(source);
			if (sourceSurvivor == null)
				sourceSurvivor = source.GetComponentInParent<Survivor>();
			if (sourceSurvivor != null)
			{
				if (IsNeutralSurvivor(listener))
					return false;
				if (IsNeutralSurvivor(sourceSurvivor))
					return false;

				return AreSurvivorsAllied(listener, sourceSurvivor) == false;
			}

			if (IsNeutralSurvivor(listener))
				return false;

			if (source.InputAuthority.IsRealPlayer == false)
				return true;

			Gameplay gameplay = ResolveGameplay(listener);
			return RaidModeRules.ArePlayerTeamsAllied(gameplay, listener.OwnerRef, source.InputAuthority) == false;
		}

		private static Gameplay ResolveGameplay(Survivor survivor)
		{
			if (survivor == null || survivor.Runner == null)
				return null;

			SceneObjects sceneObjects = survivor.Runner.GetSingleton<SceneObjects>();
			return sceneObjects != null ? sceneObjects.Gameplay : null;
		}
	}
}
