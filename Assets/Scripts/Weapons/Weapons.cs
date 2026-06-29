using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Survivor-level weapon manager. Holds references to all weapons and mediates fire/reload/switch.
	///
	/// It also owns the single, shared projectile event system for the survivor: one per-shot fire stream and one
	/// per-pellet hit stream, plus the authoritative deterministic bullet simulation and the client-side visuals.
	/// Only one weapon fires at a time, so the streams live here once instead of once per <see cref="Weapon"/> — this
	/// removes the six fixed-capacity projectile arrays that previously dominated a survivor's network snapshot. A
	/// multi-pellet shot writes a single fire event; every peer reconstructs the identical pellet spread from the
	/// shot's seed (SpawnTick * survivor id). See Docs/ProjectileSystem.md and Docs/NetworkOptimizationImplementation.md.
	/// </summary>
	public class Weapons : NetworkBehaviour
	{
		private static readonly EWeaponType[] DefaultAIWeaponPriority =
		{
			EWeaponType.Rifle,
			EWeaponType.Shotgun,
			EWeaponType.Pistol,
		};

		// Per-shot fire stream capacity. One slot per trigger pull (not per pellet), so even a 12-pellet shotgun
		// costs one slot. At any realistic fire rate and bullet flight time only a handful of shots are ever in
		// flight at once, so 8 is ample headroom before a slot is reused.
		private const int FireStreamCapacity = 8;
		// Per-pellet hit stream capacity. A single shot resolves at most ProjectilesPerShot hits in one tick (12 for
		// the shotgun) and Render consumes the stream every frame, so 16 covers the worst single-tick burst.
		private const int HitStreamCapacity = 16;
		// Inspector-clamped maximum of Weapon.ProjectilesPerShot; bounds the per-shot visual book-keeping.
		private const int MaxPelletsPerShot = 20;

		[Header("Setup")]
		public Transform FireTransform;
		public Setup FirstPersonSetup;
		public Setup ThirdPersonSetup;
		public float WeaponSwitchTime = 1f;

		[Header("Sounds")]
		public AudioSource SwitchSound;

		[HideInInspector]
		public Weapon[] AllWeapons;

		public bool IsSwitching => _switchTimer.ExpiredOrNotRunning(Runner) == false;

		[Networked, HideInInspector]
		public Weapon CurrentWeapon { get; set; }

		[Networked]
		private TickTimer _switchTimer { get; set; }
		[Networked]
		private Weapon _pendingWeapon { get; set; }

		// Shared projectile event streams. _fireCount increments once per shot, _hitCount once per confirmed pellet
		// hit; their modulo gives the circular-buffer slot.
		[Networked]
		private int _fireCount { get; set; }
		[Networked]
		private int _hitCount { get; set; }
		[Networked, Capacity(FireStreamCapacity)]
		private NetworkArray<ProjectileSpawnData> _spawnData { get; }
		[Networked, Capacity(HitStreamCapacity)]
		private NetworkArray<ProjectileHitData> _hitData { get; }

		private Weapon _visibleWeapon;
		private bool _firstPersonActive;
		private Setup _activeSetup;

		// Projectile runtime (non-networked). _activeProjectiles is the state authority's in-flight pellet list for
		// hit detection; _activeVisuals tracks one client visual per (shot slot, pellet) so a confirmed hit can
		// terminate the right tracer.
		private readonly List<ActiveProjectile> _activeProjectiles = new List<ActiveProjectile>();
		private readonly ProjectileVisual[] _activeVisuals = new ProjectileVisual[FireStreamCapacity * MaxPelletsPerShot];
		private readonly Vector3[] _pelletDirections = new Vector3[MaxPelletsPerShot];
		private int _visibleFireCount;
		private int _visibleHitCount;
		private SceneObjects _sceneObjects;
		private Survivor _ownerSurvivor;

		public void SetFirstPersonVisuals(bool firstPerson)
		{
			if (firstPerson == _firstPersonActive)
				return;

			_firstPersonActive = firstPerson;
			_activeSetup = firstPerson ? FirstPersonSetup : ThirdPersonSetup;

			for (int i = 0; i < AllWeapons.Length; i++)
			{
				// First person weapons are rendered with a different (overlay) camera
				// to prevent clipping through geometry.
				AllWeapons[i].gameObject.SetLayer(_activeSetup.WeaponLayer, true);
			}
		}

		public void Fire(bool justPressed)
		{
			if (CurrentWeapon == null || IsSwitching)
				return;

			if (CurrentWeapon.Fire(FireTransform.position, FireTransform.forward, justPressed) == false)
				return;

			// For local player play fire animation but only
			// in forward tick as starting animation multiple times
			// during resimulations is not desired.
			if (_firstPersonActive && Runner.IsForward)
			{
				FirstPersonSetup.Animator.SetTrigger(AnimatorId.Fire);
			}
		}

		public void Reload()
		{
			if (CurrentWeapon == null || IsSwitching)
				return;

			CurrentWeapon.Reload();
		}

		public void SwitchWeapon(EWeaponType weaponType)
		{
			var newWeapon = GetWeapon(weaponType);

			if (newWeapon == null || newWeapon.IsCollected == false)
				return;

			if (newWeapon == CurrentWeapon && _pendingWeapon == null)
				return;

			if (newWeapon == _pendingWeapon)
				return;

			if (CurrentWeapon.IsReloading)
				return;

			_pendingWeapon = newWeapon;
			_switchTimer = TickTimer.CreateFromSeconds(Runner, WeaponSwitchTime);

			// For local player start with switch animation but only
			// in forward tick as starting animation multiple times
			// during resimulations is not desired.
			if (_firstPersonActive && Runner.IsForward)
			{
				FirstPersonSetup.Animator.SetTrigger(AnimatorId.Hide);
				SwitchSound.Play();
			}
		}

		public bool PickupWeapon(EWeaponType weaponType)
		{
			if (CurrentWeapon.IsReloading)
				return false;

			var weapon = GetWeapon(weaponType);
			if (weapon == null)
				return false;

			if (weapon.IsCollected)
			{
				// If the weapon is already collected at least refill the ammo.
				weapon.AddAmmo(weapon.StartAmmo - weapon.RemainingAmmo);
			}
			else
			{
				// Weapon is already present inside the survivor prefab,
				// marking it as IsCollected is all that is needed.
				weapon.IsCollected = true;
			}

			SwitchWeapon(weaponType);

			return true;
		}

		public Weapon GetWeapon(EWeaponType weaponType)
		{
			for (int i = 0; i < AllWeapons.Length; ++i)
			{
				if (AllWeapons[i].Type == weaponType)
					return AllWeapons[i];
			}

			return default;
		}

		public bool TryGetBestUsableWeapon(EWeaponType[] priority, out Weapon weapon)
		{
			weapon = null;

			var weaponPriority = priority != null && priority.Length > 0 ? priority : DefaultAIWeaponPriority;
			for (int i = 0; i < weaponPriority.Length; i++)
			{
				var candidate = GetWeapon(weaponPriority[i]);
				if (IsUsableWeapon(candidate) == false)
					continue;

				weapon = candidate;
				return true;
			}

			return false;
		}

		// Called by Weapon.Fire after its ammo/cooldown checks pass. Writes a single fire event for the shot and, on
		// the state authority's forward tick, expands the deterministic pellet spread into the local hit-detection
		// list. The fire event and counter are written on every call (including input-authority prediction and
		// resimulation) so all peers reconstruct identical visuals; the local list is forward-state-authority only.
		public void RegisterShot(Weapon weapon, Vector3 origin, Vector3 baseDirection)
		{
			if (weapon == null)
				return;

			int slot = _fireCount % _spawnData.Length;
			_spawnData.Set(slot, new ProjectileSpawnData
			{
				Origin     = origin,
				Direction  = baseDirection,
				SpawnTick  = Runner.Tick,
				WeaponType = (byte)weapon.Type,
			});

			if (HasStateAuthority && Runner.IsForward)
			{
				int count = ReconstructPelletDirections(weapon, baseDirection, Runner.Tick);
				for (int p = 0; p < count; p++)
				{
					_activeProjectiles.Add(new ActiveProjectile
					{
						Origin      = origin,
						Direction   = _pelletDirections[p],
						SpawnTick   = Runner.Tick,
						WeaponType  = weapon.Type,
						ShotSlot    = (byte)slot,
						PelletIndex = (byte)p,
					});
				}
			}

			_fireCount++;
		}

		private static bool IsUsableWeapon(Weapon weapon)
		{
			return weapon != null && weapon.IsCollected && weapon.HasAmmo;
		}

		private void Awake()
		{
			// All weapons are already present inside the survivor prefab.
			// This is the simplest solution when only few weapons are available in the game.
			AllWeapons = GetComponentsInChildren<Weapon>();

			_activeSetup = ThirdPersonSetup;
		}

		private void LateUpdate()
		{
			if (Object == null)
				return; // Not valid

			if (_visibleWeapon != null)
			{
				var weaponTransform = _visibleWeapon.transform;
				var weaponPivot = _firstPersonActive ? _visibleWeapon.FirstPersonPivot : _visibleWeapon.ThirdPersonPivot;

				// Snap visible weapon to weapon handle transform, use weapon pivot to adjust offset and rotation per weapon
				weaponTransform.rotation = _activeSetup.WeaponHandle.rotation * weaponPivot.localRotation;
				weaponTransform.position = _activeSetup.WeaponHandle.position + weaponTransform.rotation * weaponPivot.localPosition;
			}
		}

		public override void Spawned()
		{
			if (HasStateAuthority)
			{
				CurrentWeapon = AllWeapons[0];
				CurrentWeapon.IsCollected = true;
			}

			_visibleFireCount = _fireCount;
			_visibleHitCount = _hitCount;
			_activeProjectiles.Clear();

			_sceneObjects = Runner.GetSingleton<SceneObjects>();
			_ownerSurvivor = GetComponentInParent<Survivor>();
		}

		public override void FixedUpdateNetwork()
		{
			TryActivatePendingWeapon();
			StepActiveProjectiles();
		}

		public override void Render()
		{
			UpdateVisibleWeapon();
			RenderProjectiles();

			if (_firstPersonActive && CurrentWeapon != null)
			{
				FirstPersonSetup.Animator.SetBool(AnimatorId.IsReloading, CurrentWeapon.IsReloading);
			}
		}

		// Advance in-flight bullets and resolve hits. Only runs on the state authority's forward tick — skipping
		// resimulation avoids double-applying damage on replayed ticks, and skipping input authority avoids the
		// non-networked _activeProjectiles list getting out of sync with the authoritative simulation.
		private void StepActiveProjectiles()
		{
			if (HasStateAuthority == false || Runner.IsForward == false)
				return;

			float dt = Runner.DeltaTime;
			var hitOptions = HitOptions.IncludePhysX | HitOptions.IgnoreInputAuthority;

			for (int i = _activeProjectiles.Count - 1; i >= 0; i--)
			{
				var proj = _activeProjectiles[i];
				var weapon = GetWeapon(proj.WeaponType);
				if (weapon == null)
				{
					_activeProjectiles.RemoveAt(i);
					continue;
				}

				float bulletSpeed = weapon.BulletSpeed;
				float maxLifetime = weapon.MaxHitDistance / bulletSpeed;

				// Skip the spawn tick. Survivor runs FixedUpdateNetwork before Weapons, so the just-fired projectile
				// would otherwise be stepped with tPrev = -dt, which makes prevPos lie behind the muzzle and the
				// first lag-compensated raycast scan geometry the bullet never actually crossed — typically a wall
				// behind the shooter.
				if (Runner.Tick <= proj.SpawnTick)
					continue;

				float tPrev = (Runner.Tick - 1 - proj.SpawnTick) * dt;
				float tCurr = (Runner.Tick     - proj.SpawnTick) * dt;

				if (tCurr >= maxLifetime)
				{
					_activeProjectiles.RemoveAt(i);
					continue;
				}

				Vector3 prevPos = EvaluateTrajectory(bulletSpeed, weapon.GravityScale, proj.Origin, proj.Direction, tPrev);
				Vector3 currPos = EvaluateTrajectory(bulletSpeed, weapon.GravityScale, proj.Origin, proj.Direction, tCurr);
				Vector3 delta   = currPos - prevPos;
				float   stepLen = delta.magnitude;

				if (stepLen < 0.001f)
					continue;

				if (Runner.LagCompensation.Raycast(prevPos, delta / stepLen, stepLen,
					    Object.InputAuthority, out var hit, weapon.HitMask, hitOptions))
				{
					bool isKill = false;
					bool isCrit = false;

					if (hit.Hitbox != null)
						ApplyDamage(weapon, hit.Hitbox, hit.Point, proj.Direction, out isKill, out isCrit);

					// Store hit sequentially so the hit counter indexes directly into _hitData.
					int hitSlot = _hitCount % _hitData.Length;
					_hitData.Set(hitSlot, new ProjectileHitData
					{
						HitPosition   = hit.Point,
						HitNormal     = hit.Normal,
						ShowEffect    = hit.Hitbox == null,
						ShotSlot      = proj.ShotSlot,
						PelletIndex   = proj.PelletIndex,
						IsKill        = isKill,
						IsCriticalHit = isCrit
					});
					_hitCount++;

					CharacterSensorEvents.ReportBulletImpact(hit.Point, proj.Origin, Object);

					_activeProjectiles.RemoveAt(i);
				}
			}
		}

		private void RenderProjectiles()
		{
			// Play the muzzle/fire effect once per render frame for the latest new shot's weapon. Matches the
			// original once-per-frame behavior and avoids stacking sounds when several shots land in one frame.
			if (_visibleFireCount < _fireCount)
			{
				int latestSlot = (_fireCount - 1) % _spawnData.Length;
				var latestWeapon = GetWeapon((EWeaponType)_spawnData[latestSlot].WeaponType);
				latestWeapon?.PlayFireEffect();
			}

			// Spawn tracer visuals for newly fired shots.
			while (_visibleFireCount < _fireCount)
			{
				int slot = _visibleFireCount % _spawnData.Length;
				var spawnData = _spawnData[slot];
				var weapon = GetWeapon((EWeaponType)spawnData.WeaponType);

				if (weapon != null)
				{
					// A wrapped-around fire counter can reuse this slot; clear any leftover visuals first.
					ClearShotVisuals(slot);

					float spawnNetTime   = spawnData.SpawnTick * Runner.DeltaTime;
					float alreadyElapsed = (float)(Runner.LocalRenderTime - spawnNetTime);
					float maxLifetime    = weapon.MaxHitDistance / weapon.BulletSpeed;

					if (alreadyElapsed < maxLifetime && weapon.ProjectileVisualPrefab != null)
					{
						int count = ReconstructPelletDirections(weapon, spawnData.Direction, spawnData.SpawnTick);
						for (int p = 0; p < count; p++)
						{
							int visualIndex = VisualIndex(slot, p);
							var visual = Instantiate(weapon.ProjectileVisualPrefab, weapon.MuzzleTransform.position, weapon.MuzzleTransform.rotation);
							visual.Initialize(spawnData.Origin, _pelletDirections[p], visualIndex,
								weapon.BulletSpeed, weapon.GravityScale, alreadyElapsed, maxLifetime);
							_activeVisuals[visualIndex] = visual;
						}
					}
				}

				_visibleFireCount++;
			}

			// Terminate visuals for confirmed hits and show crosshair feedback for the local player.
			while (_visibleHitCount < _hitCount)
			{
				int hitSlot = _visibleHitCount % _hitData.Length;
				var hitData = _hitData[hitSlot];

				int visualIndex = VisualIndex(hitData.ShotSlot, hitData.PelletIndex);
				if (visualIndex >= 0 && visualIndex < _activeVisuals.Length && _activeVisuals[visualIndex] != null)
				{
					_activeVisuals[visualIndex].Terminate(hitData.HitPosition, hitData.HitNormal, hitData.ShowEffect);
					_activeVisuals[visualIndex] = null;
				}

				if (IsHeldByActiveLocalSurvivor() && hitData.ShowEffect == false && _sceneObjects != null)
				{
					_sceneObjects.GameUI.PlayerView.Crosshair.ShowHit(hitData.IsKill, hitData.IsCriticalHit);
				}

				_visibleHitCount++;
			}
		}

		private void ClearShotVisuals(int slot)
		{
			int baseIndex = slot * MaxPelletsPerShot;
			for (int p = 0; p < MaxPelletsPerShot; p++)
			{
				var visual = _activeVisuals[baseIndex + p];
				if (visual != null)
				{
					Destroy(visual.gameObject);
					_activeVisuals[baseIndex + p] = null;
				}
			}
		}

		private static int VisualIndex(int shotSlot, int pelletIndex)
		{
			return shotSlot * MaxPelletsPerShot + pelletIndex;
		}

		// Deterministic pellet spread for a shot, written into the reusable _pelletDirections buffer. Seeds Unity
		// Random with the shot's (SpawnTick * survivor id) and replays the same dispersion draws the fire used, so
		// the state authority's hit-detection pellets and every peer's visual pellets are identical. The global
		// Random state is saved/restored so this cannot perturb other systems. Returns the pellet count.
		private int ReconstructPelletDirections(Weapon weapon, Vector3 baseDirection, int spawnTick)
		{
			int count = Mathf.Clamp(weapon.ProjectilesPerShot, 1, _pelletDirections.Length);

			if (weapon.Dispersion <= 0f)
			{
				for (int i = 0; i < count; i++)
					_pelletDirections[i] = baseDirection;
				return count;
			}

			var previousState = UnityEngine.Random.state;
			UnityEngine.Random.InitState(spawnTick * unchecked((int)Object.Id.Raw));
			for (int i = 0; i < count; i++)
			{
				// Unit sphere on purpose -> non-uniform distribution (more projectiles in the centre).
				var dispersionRotation = Quaternion.Euler(UnityEngine.Random.insideUnitSphere * weapon.Dispersion);
				_pelletDirections[i] = dispersionRotation * baseDirection;
			}
			UnityEngine.Random.state = previousState;

			return count;
		}

		private void ApplyDamage(Weapon weapon, Hitbox enemyHitbox, Vector3 position, Vector3 direction, out bool isKill, out bool isCriticalHit)
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

			float damage = weapon.Damage * damageMultiplier;
			if (_sceneObjects.Gameplay.DoubleDamageActive)
			{
				damage *= 2f;
			}

			if (enemyHealth.ApplyDamage(Object.InputAuthority, damage, position, direction, weapon.Type, isCriticalHit) == false)
				return;

			isKill = enemyHealth.IsAlive == false;
		}

		private bool IsHeldByActiveLocalSurvivor()
		{
			return HasInputAuthority && _ownerSurvivor != null && _ownerSurvivor.IsActiveCharacter();
		}

		private static Vector3 EvaluateTrajectory(float bulletSpeed, float gravityScale, Vector3 origin, Vector3 direction, float elapsed)
		{
			Vector3 pos = origin + direction * bulletSpeed * elapsed;
			pos.y -= 0.5f * gravityScale * 9.81f * elapsed * elapsed;
			return pos;
		}

		private void UpdateVisibleWeapon()
		{
			if (_visibleWeapon == CurrentWeapon)
				return;

			_visibleWeapon = CurrentWeapon;

			// Update weapon visibility
			for (int i = 0; i < AllWeapons.Length; i++)
			{
				var weapon = AllWeapons[i];
				weapon.ToggleVisibility(weapon == CurrentWeapon);
			}

			FirstPersonSetup.LeftHandSnap.Handle = _visibleWeapon.LeftHandHandle;

			FirstPersonSetup.Animator.runtimeAnimatorController = _visibleWeapon.HandsAnimatorController;
			ThirdPersonSetup.Animator.SetFloat(AnimatorId.WeaponId, Array.IndexOf(AllWeapons, CurrentWeapon));

			// Hide and show animations are played only for local player
			if (_firstPersonActive)
			{
				FirstPersonSetup.Animator.SetTrigger(AnimatorId.Show);
			}
		}

		private void TryActivatePendingWeapon()
		{
			if (IsSwitching == false || _pendingWeapon == null)
				return;

			if (_switchTimer.RemainingTime(Runner) > WeaponSwitchTime * 0.5f)
				return; // Too soon.

			CurrentWeapon = _pendingWeapon;
			_pendingWeapon = null;

			// Make the weapon immediately active (previous weapon will be deactivated in Render)
			CurrentWeapon.ToggleVisibility(true);
		}

		// DATA STRUCTURES

		[Serializable]
		public class Setup
		{
			public Transform WeaponHandle;
			[Layer]
			public int       WeaponLayer;
			public Animator  Animator;
			public HandSnap  LeftHandSnap;
		}

		// One networked event per trigger pull. WeaponType selects the firing weapon's config (pellet count,
		// dispersion, speed, gravity, damage, hit mask) so a single event reconstructs the whole shot everywhere.
		private struct ProjectileSpawnData : INetworkStruct
		{
			public Vector3 Origin;
			public Vector3 Direction;
			public int     SpawnTick;
			public byte    WeaponType;
		}

		// One networked event per confirmed pellet hit. (ShotSlot, PelletIndex) identifies which tracer to terminate.
		private struct ProjectileHitData : INetworkStruct
		{
			public Vector3     HitPosition;
			public Vector3     HitNormal;
			public NetworkBool ShowEffect;
			public byte        ShotSlot;
			public byte        PelletIndex;
			public NetworkBool IsKill;
			public NetworkBool IsCriticalHit;
		}

		// State-authority-only, never networked. One entry per in-flight pellet.
		private struct ActiveProjectile
		{
			public Vector3     Origin;
			public Vector3     Direction;
			public int         SpawnTick;
			public EWeaponType WeaponType;
			public byte        ShotSlot;
			public byte        PelletIndex;
		}
	}
}
