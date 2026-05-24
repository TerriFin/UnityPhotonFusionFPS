using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

namespace SimpleFPS
{
	public enum CharacterSeparationKind
	{
		Survivor,
		Zombie,
	}

	/// <summary>
	/// Keeps friendly characters from hard-blocking each other without involving individual AI controllers.
	/// </summary>
	public class CharacterSeparation : MonoBehaviour
	{
		private static readonly List<CharacterSeparation> ActiveSeparators = new(64);

		[Header("Identity")]
		public CharacterSeparationKind Kind = CharacterSeparationKind.Survivor;

		[Header("Separation")]
		public float Radius = 0.8f;
		public float DesiredDistance = 0.65f;
		public float PushSpeed = 1.5f;
		public int MaxNeighbors = 8;

		private Survivor _survivor;
		private ZombieCharacter _zombie;
		private Collider[] _colliders = Array.Empty<Collider>();
		private PlayerRef _lastOwnerRef = PlayerRef.None;
		private Func<KCC, Collider, bool> _previousResolveCollision;
		private Func<KCC, Collider, bool> _resolveCollisionCallback;
		private bool _isRegistered;

		public void Activate(Survivor survivor)
		{
			_survivor = survivor;
			_zombie = null;
			_lastOwnerRef = _survivor != null ? _survivor.OwnerRef : PlayerRef.None;
			ActivateShared();
		}

		public void Activate(ZombieCharacter zombie)
		{
			_zombie = zombie;
			_survivor = null;
			_lastOwnerRef = PlayerRef.None;
			ActivateShared();
		}

		public void Deactivate()
		{
			if (_isRegistered == false)
				return;

			DeactivateKCCCollisionFilter();
			RestoreCollisionPairs();
			ActiveSeparators.Remove(this);
			_isRegistered = false;
		}

		public bool HasSeparation()
		{
			return GetSeparationDirection() != Vector3.zero;
		}

		public Vector3 GetSeparationVelocity()
		{
			if (HasStateAuthority() == false)
				return Vector3.zero;

			Vector3 direction = GetSeparationDirection();
			return direction == Vector3.zero ? Vector3.zero : direction * PushSpeed;
		}

		private void OnDisable()
		{
			Deactivate();
		}

		private void Update()
		{
			if (_isRegistered == false)
				return;

			bool needsRefresh = RefreshOwnerRef();

			if (_colliders.Length == 0)
			{
				CacheColliders();
				needsRefresh |= _colliders.Length > 0;
			}

			if (needsRefresh)
				RefreshCollisionPairs();
		}

		private void OnValidate()
		{
			Radius = Mathf.Max(0.01f, Radius);
			DesiredDistance = Mathf.Clamp(DesiredDistance, 0.01f, Radius);
			PushSpeed = Mathf.Max(0f, PushSpeed);
			MaxNeighbors = Mathf.Max(1, MaxNeighbors);
		}

		private void ActivateShared()
		{
			CacheColliders();
			ActivateKCCCollisionFilter();

			if (_isRegistered == false)
			{
				ActiveSeparators.Add(this);
				_isRegistered = true;
			}

			RefreshCollisionPairs();
		}

		private Vector3 GetSeparationDirection()
		{
			if (_isRegistered == false || IsAlive() == false)
				return Vector3.zero;

			Vector3 awaySum = Vector3.zero;
			int neighborCount = 0;
			float radiusSqr = Radius * Radius;

			for (int i = 0; i < ActiveSeparators.Count; i++)
			{
				CharacterSeparation other = ActiveSeparators[i];
				if (other == this || ShouldSeparateFrom(other) == false)
					continue;

				Vector3 away = transform.position - other.transform.position;
				away.y = 0f;

				float sqrDistance = away.sqrMagnitude;
				if (sqrDistance > radiusSqr)
					continue;

				float weight = 1f;
				if (sqrDistance <= 0.0001f)
				{
					away = transform.right;
				}
				else
				{
					float distance = Mathf.Sqrt(sqrDistance);
					away /= distance;

					if (distance > DesiredDistance && Radius > DesiredDistance)
					{
						weight = Mathf.Clamp01((Radius - distance) / (Radius - DesiredDistance));
						if (weight <= 0f)
							continue;
					}
				}

				awaySum += away * weight;
				neighborCount++;

				if (neighborCount >= MaxNeighbors)
					break;
			}

			if (neighborCount == 0 || awaySum.sqrMagnitude <= 0.0001f)
				return Vector3.zero;

			return awaySum.normalized;
		}

		private bool ShouldSeparateFrom(CharacterSeparation other)
		{
			if (other == null || other.IsAlive() == false)
				return false;

			return CanPhaseWith(other);
		}

		private bool CanPhaseWith(CharacterSeparation other)
		{
			if (Kind == CharacterSeparationKind.Zombie && other.Kind == CharacterSeparationKind.Zombie)
				return true;

			if (Kind != CharacterSeparationKind.Survivor || other.Kind != CharacterSeparationKind.Survivor)
				return false;

			if (_survivor == null || other._survivor == null)
				return false;

			if (_survivor.OwnerRef == PlayerRef.None || other._survivor.OwnerRef == PlayerRef.None)
				return false;

			return _survivor.OwnerRef == other._survivor.OwnerRef;
		}

		private bool IsAlive()
		{
			if (_survivor != null)
				return _survivor.Health == null || _survivor.Health.IsAlive;
			if (_zombie != null)
				return _zombie.Health == null || _zombie.Health.IsAlive;
			return true;
		}

		private bool HasStateAuthority()
		{
			if (_survivor != null)
				return _survivor.HasStateAuthority;
			if (_zombie != null)
				return _zombie.HasStateAuthority;
			return false;
		}

		private bool RefreshOwnerRef()
		{
			if (_survivor == null || _survivor.OwnerRef == _lastOwnerRef)
				return false;

			_lastOwnerRef = _survivor.OwnerRef;
			return true;
		}

		private void CacheColliders()
		{
			_colliders = GetComponentsInChildren<Collider>(true);
		}

		private void RefreshCollisionPairs()
		{
			for (int i = 0; i < ActiveSeparators.Count; i++)
			{
				CharacterSeparation other = ActiveSeparators[i];
				if (other == null || other == this)
					continue;

				SetCollisionIgnored(other, CanPhaseWith(other));
			}
		}

		private void RestoreCollisionPairs()
		{
			for (int i = 0; i < ActiveSeparators.Count; i++)
			{
				CharacterSeparation other = ActiveSeparators[i];
				if (other == null || other == this)
					continue;

				SetCollisionIgnored(other, false);
			}
		}

		private void SetCollisionIgnored(CharacterSeparation other, bool ignored)
		{
			if (_colliders.Length == 0)
				CacheColliders();

			if (other._colliders.Length == 0)
				other.CacheColliders();

			for (int i = 0; i < _colliders.Length; i++)
			{
				Collider ownCollider = _colliders[i];
				if (ownCollider == null)
					continue;

				for (int j = 0; j < other._colliders.Length; j++)
				{
					Collider otherCollider = other._colliders[j];
					if (otherCollider == null || ownCollider == otherCollider)
						continue;

					Physics.IgnoreCollision(ownCollider, otherCollider, ignored);
				}
			}
		}

		private void ActivateKCCCollisionFilter()
		{
			var kcc = GetKCC();
			if (kcc == null)
				return;

			_resolveCollisionCallback = ResolveKCCCollision;
			_previousResolveCollision = kcc.ResolveCollision;
			kcc.ResolveCollision = _resolveCollisionCallback;
		}

		private void DeactivateKCCCollisionFilter()
		{
			var kcc = GetKCC();
			if (kcc == null)
				return;

			if (kcc.ResolveCollision == _resolveCollisionCallback)
			{
				kcc.ResolveCollision = _previousResolveCollision;
			}

			_resolveCollisionCallback = null;
			_previousResolveCollision = null;
		}

		private bool ResolveKCCCollision(KCC kcc, Collider otherCollider)
		{
			CharacterSeparation other = otherCollider != null ? otherCollider.GetComponentInParent<CharacterSeparation>() : null;
			if (other != null && CanPhaseWith(other))
				return false;

			return _previousResolveCollision == null || _previousResolveCollision.Invoke(kcc, otherCollider);
		}

		private SimpleKCC GetKCC()
		{
			if (_survivor != null)
				return _survivor.KCC;
			if (_zombie != null)
				return _zombie.KCC;
			return null;
		}
	}
}
