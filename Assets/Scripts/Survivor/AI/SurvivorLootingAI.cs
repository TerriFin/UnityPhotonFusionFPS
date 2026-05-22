using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorLootingAI : MonoBehaviour
	{
		[Header("Movement")]
		public float PickupStoppingDistance = 0.75f;
		public float DirectFallbackDistance = 1.5f;

		private readonly List<KnownPickupInfo> _pickupCandidates = new(8);
		private KnownPickupInfo _pickupTarget;
		private bool _hasPickupTarget;
		private bool _returningFromPickup;

		public bool HasTask => _hasPickupTarget;
		public bool IsReturning => _returningFromPickup;

		public void ClearTask(bool returnToAnchor, Vector3 anchor, CharacterNavigator navigator)
		{
			_pickupTarget = default;
			_hasPickupTarget = false;
			_returningFromPickup = returnToAnchor;

			if (returnToAnchor)
				navigator?.SetDestination(anchor);
		}

		public void CompleteReturn()
		{
			_returningFromPickup = false;
		}

		public bool TryStart(Survivor survivor, bool collectVisiblePickups, bool shouldPause)
		{
			if (collectVisiblePickups == false || shouldPause)
				return false;
			if (TryFindUsefulVisiblePickup(survivor, out _pickupTarget) == false)
				return false;

			_hasPickupTarget = true;
			_returningFromPickup = false;
			survivor.Navigator?.SetDestination(_pickupTarget.Position);
			return true;
		}

		public NetworkedInput GetInput(
			Survivor survivor,
			Vector3 anchor,
			Func<Vector3, bool, float, NetworkedInput> createMoveInput,
			Func<NetworkedInput> getReturnToAnchorInput)
		{
			if (IsPickupAvailable(_pickupTarget) == false || IsPickupUseful(survivor, _pickupTarget) == false)
			{
				if (TryStart(survivor, true, false))
					return GetInput(survivor, anchor, createMoveInput, getReturnToAnchorInput);

				ClearTask(true, anchor, survivor.Navigator);
				return getReturnToAnchorInput();
			}

			var navigator = survivor.Navigator;
			if (navigator != null)
			{
				if (navigator.HasDestination == false)
					navigator.SetDestination(_pickupTarget.Position);

				navigator.Tick(survivor.transform.position);
				if (navigator.TryGetSteeringTarget(survivor.transform.position, out var steeringTarget))
					return createMoveInput(steeringTarget, false, PickupStoppingDistance);
			}

			if (FlatDistanceSqr(survivor.transform.position, _pickupTarget.Position) <= DirectFallbackDistance * DirectFallbackDistance)
				return createMoveInput(_pickupTarget.Position, true, PickupStoppingDistance);

			ClearTask(true, anchor, survivor.Navigator);
			return getReturnToAnchorInput();
		}

		private bool TryFindUsefulVisiblePickup(Survivor survivor, out KnownPickupInfo pickup)
		{
			pickup = default;

			if (survivor == null || survivor.Sensor == null)
				return false;
			if (survivor.Sensor.TryGetClosestDirectEnemy(out _))
				return false;

			_pickupCandidates.Clear();
			survivor.Sensor.GetVisiblePickups(_pickupCandidates);

			float closestDistanceSqr = float.MaxValue;
			Vector3 position = survivor.transform.position;
			for (int i = 0; i < _pickupCandidates.Count; i++)
			{
				var candidate = _pickupCandidates[i];
				if (IsPickupAvailable(candidate) == false || IsPickupUseful(survivor, candidate) == false)
					continue;

				float distanceSqr = FlatDistanceSqr(position, candidate.Position);
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				pickup = candidate;
			}

			return closestDistanceSqr < float.MaxValue;
		}

		private static bool IsPickupUseful(Survivor survivor, KnownPickupInfo pickup)
		{
			switch (pickup.Type)
			{
				case EVisiblePickupType.Health:
					return survivor.Health != null &&
					       survivor.Health.IsAlive &&
					       survivor.Health.CurrentHealth < survivor.Health.MaxHealth;
				case EVisiblePickupType.Weapon:
					if (survivor.Weapons == null)
						return false;

					var weapon = survivor.Weapons.GetWeapon(pickup.WeaponType);
					if (weapon == null)
						return false;
					return weapon.IsCollected == false || weapon.RemainingAmmo < weapon.StartAmmo;
				default:
					return false;
			}
		}

		private static bool IsPickupAvailable(KnownPickupInfo pickup)
		{
			switch (pickup.Type)
			{
				case EVisiblePickupType.Health:
					return pickup.HealthPickup != null && pickup.HealthPickup.IsAvailableForSensor;
				case EVisiblePickupType.Weapon:
					return pickup.WeaponPickup != null && pickup.WeaponPickup.IsAvailableForSensor;
				default:
					return false;
			}
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
