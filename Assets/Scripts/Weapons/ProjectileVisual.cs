using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Drives the visual representation of a single in-flight projectile.
	/// Position is computed each frame from the bullet's initial conditions — no networking required.
	/// Weapons.Render() obtains one via the static pool when a bullet is fired and Terminate() is called when
	/// the server confirms a hit. If no hit is confirmed before the bullet's maximum lifetime the visual
	/// releases itself.
	///
	/// Instances are pooled per prefab: with many AI shooters every client used to Instantiate/Destroy one
	/// object per pellet per shot, which dominated firefight GC. Get() reuses a released instance when one is
	/// available; Release() resets and parks it. Hit effects are still one-shot instantiations (they are
	/// stateful particle bursts), just no longer parented to the pooled tracer.
	/// </summary>
	public class ProjectileVisual : MonoBehaviour
	{
		[Header("Impact Setup")]
		public GameObject ProjectileObject;
		public GameObject HitEffectPrefab;
		public float      LifeTimeAfterHit = 2f;

		private static readonly Dictionary<ProjectileVisual, Stack<ProjectileVisual>> Pools = new();

		private Vector3 _origin;
		private Vector3 _direction;
		private float   _bulletSpeed;
		private float   _gravityScale;
		private float   _maxLifetime;
		private float   _initElapsed;
		private float   _initTime;
		private bool    _terminated;
		private bool    _pooled;
		private ProjectileVisual _sourcePrefab;
		private TrailRenderer[] _trails;

		public int SlotIndex { get; private set; }

		// Incremented every time this instance leaves the pool. Holders of a stale reference (a tracer that
		// already self-released and was handed to a newer shot) compare generations before Terminate/Release,
		// mirroring the "destroyed reference becomes null" safety of the old Instantiate/Destroy flow.
		public int Generation { get; private set; }

		/// <summary>
		/// Pool-aware replacement for Instantiate(prefab, position, rotation). Reuses a previously released
		/// instance of the same prefab when available.
		/// </summary>
		public static ProjectileVisual Get(ProjectileVisual prefab, Vector3 position, Quaternion rotation)
		{
			if (prefab == null)
				return null;

			if (Pools.TryGetValue(prefab, out Stack<ProjectileVisual> pool))
			{
				// Pooled instances can be destroyed externally (scene unload); skip the corpses.
				while (pool.Count > 0)
				{
					ProjectileVisual pooled = pool.Pop();
					if (pooled == null)
						continue;

					pooled.transform.SetPositionAndRotation(position, rotation);
					pooled.PrepareForReuse();
					return pooled;
				}
			}

			ProjectileVisual instance = Instantiate(prefab, position, rotation);
			instance._sourcePrefab = prefab;
			instance._trails = instance.GetComponentsInChildren<TrailRenderer>(true);
			return instance;
		}

		/// <summary>Returns this instance to its prefab's pool (or destroys it if it was not pool-created).</summary>
		public void Release()
		{
			if (_pooled)
				return;

			if (_sourcePrefab == null)
			{
				Destroy(gameObject);
				return;
			}

			_pooled = true;
			gameObject.SetActive(false);

			if (Pools.TryGetValue(_sourcePrefab, out Stack<ProjectileVisual> pool) == false)
			{
				pool = new Stack<ProjectileVisual>(16);
				Pools.Add(_sourcePrefab, pool);
			}

			pool.Push(this);
		}

		private void PrepareForReuse()
		{
			_terminated = false;
			_pooled = false;
			Generation++;

			if (ProjectileObject != null)
				ProjectileObject.SetActive(true);

			gameObject.SetActive(true);

			// A reused trail would otherwise draw a streak from the previous bullet's endpoint to the new muzzle.
			if (_trails != null)
			{
				for (int i = 0; i < _trails.Length; i++)
				{
					if (_trails[i] != null)
						_trails[i].Clear();
				}
			}
		}

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
		/// Called by Weapons.Render() when the server confirms this bullet hit something.
		/// Snaps to impact point, plays the hit effect, then returns to the pool.
		/// </summary>
		public void Terminate(Vector3 hitPosition, Vector3 hitNormal, bool showEffect)
		{
			_terminated        = true;
			transform.position = hitPosition;

			if (showEffect && HitEffectPrefab != null)
			{
				// Spawned detached (not parented to this pooled object) and cleaned up on its own timer.
				GameObject effect = Instantiate(HitEffectPrefab, hitPosition, Quaternion.LookRotation(hitNormal));
				Destroy(effect, LifeTimeAfterHit);
			}

			Release();
		}

		private void Update()
		{
			if (_terminated)
				return;

			float elapsed = _initElapsed + (Time.timeSinceLevelLoad - _initTime);

			if (elapsed >= _maxLifetime)
			{
				Release();
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
