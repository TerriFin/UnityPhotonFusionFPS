using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.Serialization;

namespace SimpleFPS
{
	public enum EWeaponType
	{
		None,
		Pistol,
		Rifle,
		Shotgun,
	}

	/// <summary>
	/// Main script that handles all the shooting. Weapon fires simulated projectiles that travel at
	/// a configurable speed with optional gravity drop. Spawn data is synchronized over the network;
	/// positions are never synced — every peer reconstructs the trajectory from initial conditions.
	/// </summary>
	public class Weapon : NetworkBehaviour
	{
		public EWeaponType Type;

		[Header("Fire Setup")]
		public bool        IsAutomatic = true;
		public float       Damage = 10f;
		public int         FireRate = 100;
		[Range(1, 20)]
		public int         ProjectilesPerShot = 1;
		public float       Dispersion = 0f;
		public LayerMask   HitMask;
		public float       MaxHitDistance = 100f;

		[Header("Projectile")]
		public float       BulletSpeed = 300f;
		public float       GravityScale = 0.2f;

		[Header("Ammo")]
		public int         MaxClipAmmo = 12;
		public int         StartAmmo = 25;
		public float       ReloadTime = 2f;

		[Header("Visuals")]
		public Sprite      Icon;
		public string      Name;
		public Animator    WeaponAnimator;
		public RuntimeAnimatorController HandsAnimatorController;

		[Header("Holding")]
		public Transform   FirstPersonPivot;
		public Transform   ThirdPersonPivot;
		public Transform   LeftHandHandle;

		[Header("Fire Effect")]
		[FormerlySerializedAs("MuzzleTransform")]
		public Transform   MuzzleTransform;
		public GameObject  MuzzleEffectPrefab;
		public ProjectileVisual ProjectileVisualPrefab;

		[Header("Sounds")]
		public AudioSource FireSound;
		public AudioSource ReloadingSound;
		public AudioSource EmptyClipSound;

		public bool HasAmmo => ClipAmmo > 0 || RemainingAmmo > 0;

		[Networked, HideInInspector]
		public NetworkBool IsCollected { get; set; }
		[Networked, HideInInspector]
		public NetworkBool IsReloading { get; set; }
		[Networked, HideInInspector]
		public int ClipAmmo { get; set; }
		[Networked, HideInInspector]
		public int RemainingAmmo { get; set; }

		[Networked]
		private int _fireCount { get; set; }
		[Networked]
		private int _hitCount { get; set; }
		[Networked]
		private TickTimer _fireCooldown { get; set; }
		[Networked, Capacity(16)]
		private NetworkArray<ProjectileSpawnData> _spawnData { get; }
		[Networked, Capacity(16)]
		private NetworkArray<ProjectileHitData> _hitData { get; }

		private int _fireTicks;
		private int _visibleFireCount;
		private int _visibleHitCount;
		private bool _reloadingVisible;
		private GameObject _muzzleEffectInstance;
		private SceneObjects _sceneObjects;
		private Survivor _ownerSurvivor;
		private readonly List<ActiveProjectile> _activeProjectiles = new List<ActiveProjectile>();
		private readonly ProjectileVisual[] _activeVisuals = new ProjectileVisual[64];

		public bool Fire(Vector3 firePosition, Vector3 fireDirection, bool justPressed)
		{
			if (IsCollected == false)
				return false;
			if (justPressed == false && IsAutomatic == false)
				return false;
			if (IsReloading)
				return false;
			if (_fireCooldown.ExpiredOrNotRunning(Runner) == false)
				return false;

			if (ClipAmmo <= 0)
			{
				PlayEmptyClipSound(justPressed);
				return false;
			}

			// Random needs to be initialized with same seed on both input and
			// state authority to ensure the projectiles are fired in the same direction on both.
			Random.InitState(Runner.Tick * unchecked((int)Object.Id.Raw));

			for (int i = 0; i < ProjectilesPerShot; i++)
			{
				var projectileDirection = fireDirection;

				if (Dispersion > 0f)
				{
					// We use unit sphere on purpose -> non-uniform distribution (more projectiles in the center).
					var dispersionRotation = Quaternion.Euler(Random.insideUnitSphere * Dispersion);
					projectileDirection = dispersionRotation * fireDirection;
				}

				FireProjectile(firePosition, projectileDirection);
			}

			if (HasStateAuthority && Runner.IsForward)
			{
				CharacterSensorEvents.ReportNoise(firePosition, Object);
			}

			_fireCooldown = TickTimer.CreateFromTicks(Runner, _fireTicks);
			ClipAmmo--;

			return true;
		}

		public void Reload()
		{
			if (IsCollected == false)
				return;
			if (ClipAmmo >= MaxClipAmmo)
				return;
			if (RemainingAmmo <= 0)
				return;
			if (IsReloading)
				return;
			if (_fireCooldown.ExpiredOrNotRunning(Runner) == false)
				return; // Fire finishing.

			IsReloading = true;
			_fireCooldown = TickTimer.CreateFromSeconds(Runner, ReloadTime);
		}

		public void AddAmmo(int amount)
		{
			RemainingAmmo += amount;
		}

		public void ToggleVisibility(bool isVisible)
		{
			gameObject.SetActive(isVisible);

			if (_muzzleEffectInstance != null)
			{
				_muzzleEffectInstance.SetActive(false);
			}
		}

		public float GetReloadProgress()
		{
			if (IsReloading == false)
				return 1f;

			return 1f - _fireCooldown.RemainingTime(Runner).GetValueOrDefault() / ReloadTime;
		}

		public override void Spawned()
		{
			if (HasStateAuthority)
			{
				ClipAmmo = Mathf.Clamp(StartAmmo, 0, MaxClipAmmo);
				RemainingAmmo = StartAmmo - ClipAmmo;
			}

			_visibleFireCount = _fireCount;
			_visibleHitCount = _hitCount;
			_activeProjectiles.Clear();

			float fireTime = 60f / FireRate;
			_fireTicks = Mathf.CeilToInt(fireTime / Runner.DeltaTime);

			_muzzleEffectInstance = Instantiate(MuzzleEffectPrefab, MuzzleTransform);
			_muzzleEffectInstance.SetActive(false);

			_sceneObjects = Runner.GetSingleton<SceneObjects>();
			_ownerSurvivor = GetComponentInParent<Survivor>();
		}

		public override void FixedUpdateNetwork()
		{
			if (IsCollected == false)
				return;

			if (ClipAmmo == 0)
			{
				// Try auto-reload.
				Reload();
			}

			if (IsReloading && _fireCooldown.ExpiredOrNotRunning(Runner))
			{
				// Reloading finished.
				IsReloading = false;

				int reloadAmmo = MaxClipAmmo - ClipAmmo;
				reloadAmmo = Mathf.Min(reloadAmmo, RemainingAmmo);

				ClipAmmo += reloadAmmo;
				RemainingAmmo -= reloadAmmo;

				// Add small prepare time after reload.
				_fireCooldown = TickTimer.CreateFromSeconds(Runner, 0.25f);
			}

			// Advance in-flight bullets and resolve hits. Only runs on the state authority's forward
			// tick — skipping resimulation avoids double-applying damage on replayed ticks, and
			// skipping input authority avoids the non-networked _activeProjectiles list getting
			// out of sync with the authoritative simulation.
			if (HasStateAuthority && Runner.IsForward)
			{
				float dt = Runner.DeltaTime;
				float maxLifetime = MaxHitDistance / BulletSpeed;
				var hitOptions = HitOptions.IncludePhysX | HitOptions.IgnoreInputAuthority;

				for (int i = _activeProjectiles.Count - 1; i >= 0; i--)
				{
					var proj = _activeProjectiles[i];

					// Skip the spawn tick. Survivor runs FixedUpdateNetwork before Weapon, so the
					// just-fired projectile would otherwise be stepped with tPrev = -dt, which makes
					// prevPos lie behind the muzzle and the first lag-compensated raycast scan
					// geometry the bullet never actually crossed — typically a wall behind the shooter.
					if (Runner.Tick <= proj.SpawnTick)
						continue;

					float tPrev = (Runner.Tick - 1 - proj.SpawnTick) * dt;
					float tCurr = (Runner.Tick     - proj.SpawnTick) * dt;

					if (tCurr >= maxLifetime)
					{
						_activeProjectiles.RemoveAt(i);
						continue;
					}

					Vector3 prevPos = EvaluateTrajectory(proj.Origin, proj.Direction, tPrev);
					Vector3 currPos = EvaluateTrajectory(proj.Origin, proj.Direction, tCurr);
					Vector3 delta   = currPos - prevPos;
					float   stepLen = delta.magnitude;

					if (stepLen < 0.001f)
						continue;

					if (Runner.LagCompensation.Raycast(prevPos, delta / stepLen, stepLen,
						    Object.InputAuthority, out var hit, HitMask, hitOptions))
					{
						bool isKill = false;
						bool isCrit = false;

						if (hit.Hitbox != null)
							ApplyDamage(hit.Hitbox, hit.Point, proj.Direction, out isKill, out isCrit);

						// Store hit sequentially so the hit counter indexes directly into _hitData.
						int hitSlot = _hitCount % _hitData.Length;
						_hitData.Set(hitSlot, new ProjectileHitData
						{
							HitPosition   = hit.Point,
							HitNormal     = hit.Normal,
							ShowEffect    = hit.Hitbox == null,
							FireSlot      = proj.SlotIndex,
							IsKill        = isKill,
							IsCriticalHit = isCrit
						});
						_hitCount++;

						CharacterSensorEvents.ReportBulletImpact(hit.Point, proj.Origin, Object);

						_activeProjectiles.RemoveAt(i);
					}
				}
			}
		}

		public override void Render()
		{
			if (_visibleFireCount < _fireCount)
			{
				PlayFireEffect();
			}

			// Spawn visuals for newly fired projectiles.
			while (_visibleFireCount < _fireCount)
			{
				int slot = _visibleFireCount % _spawnData.Length;
				var spawnData = _spawnData[slot];

				float spawnNetTime   = spawnData.SpawnTick * Runner.DeltaTime;
				float alreadyElapsed = (float)(Runner.LocalRenderTime - spawnNetTime);
				float maxLifetime    = MaxHitDistance / BulletSpeed;

				if (alreadyElapsed < maxLifetime)
				{
					var visual = Instantiate(ProjectileVisualPrefab, MuzzleTransform.position, MuzzleTransform.rotation);
					visual.Initialize(spawnData.Origin, spawnData.Direction, slot,
						BulletSpeed, GravityScale, alreadyElapsed, maxLifetime);
					_activeVisuals[slot] = visual;
				}

				_visibleFireCount++;
			}

			// Terminate visuals for confirmed hits and show crosshair feedback for the local player.
			while (_visibleHitCount < _hitCount)
			{
				int hitSlot = _visibleHitCount % _hitData.Length;
				var hitData = _hitData[hitSlot];

				int fireSlot = hitData.FireSlot;
				if (_activeVisuals[fireSlot] != null)
				{
					_activeVisuals[fireSlot].Terminate(hitData.HitPosition, hitData.HitNormal, hitData.ShowEffect);
					_activeVisuals[fireSlot] = null;
				}

				if (IsHeldByActiveLocalSurvivor() && hitData.ShowEffect == false)
				{
					_sceneObjects.GameUI.PlayerView.Crosshair.ShowHit(hitData.IsKill, hitData.IsCriticalHit);
				}

				_visibleHitCount++;
			}

			if (_reloadingVisible != IsReloading)
			{
				if (IsReloading)
				{
					WeaponAnimator.SetTrigger("Reload");
					ReloadingSound.Play();
				}

				_reloadingVisible = IsReloading;
			}
		}

		private void FireProjectile(Vector3 firePosition, Vector3 fireDirection)
		{
			int slot = _fireCount % _spawnData.Length;

			_spawnData.Set(slot, new ProjectileSpawnData
			{
				Origin    = firePosition,
				Direction = fireDirection,
				SpawnTick = Runner.Tick
			});

			// Only the state authority tracks active projectiles for hit detection.
			// Guarded by IsForward so resimulation doesn't add duplicates to the local list.
			if (HasStateAuthority && Runner.IsForward)
			{
				_activeProjectiles.Add(new ActiveProjectile
				{
					Origin    = firePosition,
					Direction = fireDirection,
					SpawnTick = Runner.Tick,
					SlotIndex = slot
				});
			}

			_fireCount++;
		}

		private void PlayFireEffect()
		{
			if (FireSound != null)
			{
				FireSound.PlayOneShot(FireSound.clip);
			}

			// Reset muzzle effect visibility.
			_muzzleEffectInstance.SetActive(false);
			_muzzleEffectInstance.SetActive(true);

			WeaponAnimator.SetTrigger("Fire");

			GetComponentInParent<Survivor>().PlayFireEffect();
		}

		private void ApplyDamage(Hitbox enemyHitbox, Vector3 position, Vector3 direction, out bool isKill, out bool isCriticalHit)
		{
			isKill = false;
			isCriticalHit = false;

			var enemyHealth = enemyHitbox.Root.GetComponent<Health>();
			if (enemyHealth == null || enemyHealth.IsAlive == false)
				return;
			if (CharacterFactionUtility.CanSurvivorWeaponDamage(_ownerSurvivor, enemyHealth) == false)
				return;

			float damageMultiplier = enemyHitbox is BodyHitbox bodyHitbox ? bodyHitbox.DamageMultiplier : 1f;
			isCriticalHit = damageMultiplier > 1f;

			float damage = Damage * damageMultiplier;
			if (_sceneObjects.Gameplay.DoubleDamageActive)
			{
				damage *= 2f;
			}

			if (enemyHealth.ApplyDamage(Object.InputAuthority, damage, position, direction, Type, isCriticalHit) == false)
				return;

			isKill = enemyHealth.IsAlive == false;
		}

		private void PlayEmptyClipSound(bool fireJustPressed)
		{
			// For automatic weapons we want to play empty clip sound once after last fire.
			bool firstEmptyShot = _fireCooldown.TargetTick.GetValueOrDefault() == Runner.Tick - 1;

			if (fireJustPressed == false && firstEmptyShot == false)
				return;

			if (EmptyClipSound == null || EmptyClipSound.isPlaying)
				return;

			if (Runner.IsForward && IsHeldByActiveLocalSurvivor())
			{
				EmptyClipSound.Play();
			}
		}

		private bool IsHeldByActiveLocalSurvivor()
		{
			return HasInputAuthority && _ownerSurvivor != null && _ownerSurvivor.IsActiveCharacter();
		}

		private Vector3 EvaluateTrajectory(Vector3 origin, Vector3 direction, float elapsed)
		{
			Vector3 pos = origin + direction * BulletSpeed * elapsed;
			pos.y -= 0.5f * GravityScale * 9.81f * elapsed * elapsed;
			return pos;
		}

		private struct ActiveProjectile
		{
			public Vector3 Origin;
			public Vector3 Direction;
			public int     SpawnTick;
			public int     SlotIndex;
		}

		private struct ProjectileSpawnData : INetworkStruct
		{
			public Vector3 Origin;
			public Vector3 Direction;
			public int     SpawnTick;
		}

		private struct ProjectileHitData : INetworkStruct
		{
			public Vector3     HitPosition;
			public Vector3     HitNormal;
			public NetworkBool ShowEffect;
			public int         FireSlot;
			public NetworkBool IsKill;
			public NetworkBool IsCriticalHit;
		}
	}
}
