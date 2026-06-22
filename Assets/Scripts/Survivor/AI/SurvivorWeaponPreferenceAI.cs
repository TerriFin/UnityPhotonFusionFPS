using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public enum ESurvivorWeaponPreference
	{
		Automatic = 0,
		PreferStrongWeapons = 1,
		PreferPistol = 2,
		HoldFire = 3,
	}

	[DisallowMultipleComponent]
	public sealed class SurvivorWeaponPreferenceAI : MonoBehaviour
	{
		private const float ScoreEpsilon = 0.0001f;

		[Header("Automatic Zombie Escalation")]
		[Min(0f)]
		public float CloseZombieDistance = 4f;
		[Min(0)]
		public int NearbyZombieCountThreshold = 4;
		[Min(0f)]
		public float NearbyZombieCountRadius = 10f;

		private readonly List<KnownEnemyInfo> _knownEnemies = new(8);
		private Survivor _survivor;
		private Gameplay _gameplay;

		public void Activate(Survivor survivor)
		{
			_survivor = survivor != null ? survivor : GetComponent<Survivor>();
		}

		public bool TryGetDesiredWeapon(KnownEnemyInfo enemy, ESurvivorWeaponPreference preference, out Weapon weapon)
		{
			weapon = null;
			EnsureSurvivor();

			if (_survivor == null || _survivor.HasStateAuthority == false || _survivor.Weapons == null || enemy.Object == null)
				return false;

			float targetDistance = Vector3.Distance(GetFireOrigin(), enemy.Object.transform.position);
			switch (preference)
			{
				case ESurvivorWeaponPreference.HoldFire:
					return false;

				case ESurvivorWeaponPreference.PreferPistol:
					return TryGetPistolOrFallback(targetDistance, out weapon);

				case ESurvivorWeaponPreference.PreferStrongWeapons:
					return TryGetStrongWeapon(targetDistance, out weapon);

				case ESurvivorWeaponPreference.Automatic:
				if (IsZombie(enemy.Object) == false || IsOvertime() || ShouldEscalateAgainstZombies())
					return TryGetStrongWeapon(targetDistance, out weapon);

				return TryGetPistolOrFallback(targetDistance, out weapon);

				default:
					return false;
			}
		}

		private void Awake()
		{
			Activate(GetComponent<Survivor>());
		}

		private bool TryGetPistolOrFallback(float targetDistance, out Weapon weapon)
		{
			weapon = _survivor.Weapons.GetWeapon(EWeaponType.Pistol);
			if (IsUsable(weapon))
				return true;

			return TryGetStrongWeapon(targetDistance, out weapon);
		}

		private bool TryGetStrongWeapon(float targetDistance, out Weapon weapon)
		{
			weapon = null;
			Weapon fallback = null;
			float bestScore = float.MinValue;
			float longestRange = float.MinValue;
			Weapon currentWeapon = _survivor.Weapons.CurrentWeapon;
			Weapon[] weapons = _survivor.Weapons.AllWeapons;

			if (weapons == null)
				return false;

			for (int i = 0; i < weapons.Length; i++)
			{
				Weapon candidate = weapons[i];
				if (IsUsable(candidate) == false)
					continue;

				float effectiveRange = Mathf.Max(0.1f, candidate.AIEffectiveMaxRange);
				if (effectiveRange > longestRange + ScoreEpsilon ||
				    (Mathf.Abs(effectiveRange - longestRange) <= ScoreEpsilon && candidate == currentWeapon))
				{
					longestRange = effectiveRange;
					fallback = candidate;
				}

				if (targetDistance > effectiveRange)
					continue;

				float rangeHeadroom = effectiveRange - targetDistance;
				float rangeFit = 1f / (1f + rangeHeadroom);
				float score = Mathf.Max(0f, candidate.AIWeaponStrength) * rangeFit;
				if (score > bestScore + ScoreEpsilon ||
				    (Mathf.Abs(score - bestScore) <= ScoreEpsilon && candidate == currentWeapon))
				{
					bestScore = score;
					weapon = candidate;
				}
			}

			if (weapon != null)
				return true;

			weapon = fallback;
			return weapon != null;
		}

		private bool ShouldEscalateAgainstZombies()
		{
			CharacterSensor sensor = _survivor.Sensor;
			if (sensor == null)
				return false;

			float closeDistance = Mathf.Max(0f, CloseZombieDistance);
			float closeDistanceSqr = closeDistance * closeDistance;
			int threshold = Mathf.Max(0, NearbyZombieCountThreshold);
			float countRadius = Mathf.Max(0f, NearbyZombieCountRadius);
			float countRadiusSqr = countRadius * countRadius;
			int nearbyCount = 0;
			Vector3 origin = _survivor.transform.position;

			_knownEnemies.Clear();
			sensor.GetDirectKnownEnemies(_knownEnemies);
			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				NetworkObject target = _knownEnemies[i].Object;
				if (IsLiveZombie(target) == false)
					continue;

				float distanceSqr = (target.transform.position - origin).sqrMagnitude;
				if (closeDistance > 0f && distanceSqr <= closeDistanceSqr)
				{
					_knownEnemies.Clear();
					return true;
				}

				if (threshold > 0 && countRadius > 0f && distanceSqr <= countRadiusSqr)
				{
					nearbyCount++;
					if (nearbyCount >= threshold)
					{
						_knownEnemies.Clear();
						return true;
					}
				}
			}

			_knownEnemies.Clear();
			return false;
		}

		private bool IsOvertime()
		{
			if (_gameplay == null && _survivor != null && _survivor.Runner != null)
			{
				SceneObjects sceneObjects = _survivor.Runner.GetSingleton<SceneObjects>();
				_gameplay = sceneObjects != null ? sceneObjects.Gameplay : null;
			}

			return _gameplay != null &&
			       _gameplay.ZombieOrchestrator != null &&
			       _gameplay.ZombieOrchestrator.IsOvertime;
		}

		private Vector3 GetFireOrigin()
		{
			return _survivor.Weapons.FireTransform != null
				? _survivor.Weapons.FireTransform.position
				: _survivor.transform.position;
		}

		private void EnsureSurvivor()
		{
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();
		}

		private static bool IsUsable(Weapon weapon)
		{
			return weapon != null && weapon.IsCollected && weapon.HasAmmo;
		}

		private static bool IsZombie(NetworkObject target)
		{
			return target != null && target.GetComponent<ZombieCharacter>() != null;
		}

		private static bool IsLiveZombie(NetworkObject target)
		{
			if (target == null)
				return false;

			ZombieCharacter zombie = target.GetComponent<ZombieCharacter>();
			return zombie != null && zombie.Health != null && zombie.Health.IsAlive;
		}
	}
}
