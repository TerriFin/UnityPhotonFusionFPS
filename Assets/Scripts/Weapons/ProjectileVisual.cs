using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Drives the visual representation of a single in-flight projectile.
	/// Position is computed each frame from the bullet's initial conditions — no networking required.
	/// Weapon.Render() calls Initialize() when a bullet is fired and Terminate() when the server
	/// confirms a hit. If no hit is confirmed before the bullet's maximum lifetime the visual
	/// self-destructs.
	/// </summary>
	public class ProjectileVisual : MonoBehaviour
	{
		[Header("Impact Setup")]
		public GameObject ProjectileObject;
		public GameObject HitEffectPrefab;
		public float      LifeTimeAfterHit = 2f;

		private Vector3 _origin;
		private Vector3 _direction;
		private float   _bulletSpeed;
		private float   _gravityScale;
		private float   _maxLifetime;
		private float   _initElapsed;
		private float   _initTime;
		private bool    _terminated;

		public int SlotIndex { get; private set; }

		/// <param name="origin">World-space muzzle position at fire time.</param>
		/// <param name="direction">Normalized aim direction including dispersion.</param>
		/// <param name="slotIndex">Circular buffer slot this bullet occupies in the weapon.</param>
		/// <param name="bulletSpeed">Metres per second from the weapon's BulletSpeed field.</param>
		/// <param name="gravityScale">Gravity multiplier from the weapon's GravityScale field.</param>
		/// <param name="alreadyElapsed">Seconds already elapsed since the bullet was spawned (for late-joining visuals).</param>
		/// <param name="maxLifetime">Seconds until the bullet is considered a miss and the visual removed.</param>
		public void Initialize(Vector3 origin, Vector3 direction, int slotIndex,
			float bulletSpeed, float gravityScale, float alreadyElapsed, float maxLifetime)
		{
			_origin       = origin;
			_direction    = direction;
			SlotIndex     = slotIndex;
			_bulletSpeed  = bulletSpeed;
			_gravityScale = gravityScale;
			_initElapsed  = Mathf.Max(0f, alreadyElapsed);
			_maxLifetime  = maxLifetime;
			_initTime     = Time.timeSinceLevelLoad;

			// Place at the correct in-flight position immediately so there is no single-frame pop.
			transform.position = EvaluatePosition(_initElapsed);
		}

		/// <summary>
		/// Called by Weapon.Render() when the server confirms this bullet hit something.
		/// Snaps to impact point, plays the hit effect, then self-destructs.
		/// </summary>
		public void Terminate(Vector3 hitPosition, Vector3 hitNormal, bool showEffect)
		{
			_terminated        = true;
			transform.position = hitPosition;

			if (!showEffect)
			{
				Destroy(gameObject);
				return;
			}

			if (ProjectileObject != null)
				ProjectileObject.SetActive(false);

			if (HitEffectPrefab != null)
				Instantiate(HitEffectPrefab, hitPosition, Quaternion.LookRotation(hitNormal), transform);

			Destroy(gameObject, LifeTimeAfterHit);
		}

		private void Update()
		{
			if (_terminated)
				return;

			float elapsed = _initElapsed + (Time.timeSinceLevelLoad - _initTime);

			if (elapsed >= _maxLifetime)
			{
				Destroy(gameObject);
				return;
			}

			transform.position = EvaluatePosition(elapsed);

			Vector3 tangent = EvaluateTangent(elapsed);
			if (tangent.sqrMagnitude > 0.001f)
				transform.forward = tangent.normalized;
		}

		private Vector3 EvaluatePosition(float elapsed)
		{
			Vector3 pos = _origin + _direction * _bulletSpeed * elapsed;
			pos.y -= 0.5f * _gravityScale * 9.81f * elapsed * elapsed;
			return pos;
		}

		private Vector3 EvaluateTangent(float elapsed)
		{
			return new Vector3(
				_direction.x * _bulletSpeed,
				_direction.y * _bulletSpeed - _gravityScale * 9.81f * elapsed,
				_direction.z * _bulletSpeed
			);
		}
	}
}
