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

		public static bool CanSurvivorAutoAttack(Survivor attacker, NetworkObject target)
		{
			if (attacker == null || target == null)
				return false;

			if (target.GetComponent<ZombieCharacter>() != null)
				return true;

			var targetSurvivor = target.GetComponent<Survivor>();
			if (targetSurvivor == null)
				return false;

			if (IsNeutralSurvivor(attacker))
				return false;

			if (IsNeutralSurvivor(targetSurvivor))
				return false;

			return attacker.OwnerRef != targetSurvivor.OwnerRef;
		}

		public static bool CanSurvivorWeaponDamage(Survivor attacker, Health targetHealth)
		{
			if (attacker == null || targetHealth == null)
				return true;

			if (IsNeutralSurvivor(attacker) == false)
				return true;

			return targetHealth.GetComponent<ZombieCharacter>() != null;
		}

		public static bool IsEnemyNoiseSourceForSurvivor(Survivor listener, NetworkObject source)
		{
			if (listener == null)
				return true;
			if (source == null)
				return true;

			if (source.GetComponent<ZombieCharacter>() != null)
				return true;

			var sourceSurvivor = source.GetComponent<Survivor>() ?? source.GetComponentInParent<Survivor>();
			if (sourceSurvivor != null)
			{
				if (IsNeutralSurvivor(listener))
					return false;
				if (IsNeutralSurvivor(sourceSurvivor))
					return false;

				return listener.OwnerRef != sourceSurvivor.OwnerRef;
			}

			if (IsNeutralSurvivor(listener))
				return false;

			if (source.InputAuthority.IsRealPlayer == false)
				return true;

			return source.InputAuthority != listener.OwnerRef;
		}
	}
}
