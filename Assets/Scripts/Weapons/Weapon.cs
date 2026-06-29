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
	/// Per-weapon fire control, ammo, and visuals. Each survivor carries one of these per weapon type.
	///
	/// The actual projectile event streams (fire/hit) and the deterministic bullet simulation live on the
	/// survivor-level <see cref="Weapons"/> manager, shared by all weapons — only one weapon fires at a time, so
	/// every weapon does not need its own replicated event history. A successful Fire() registers a single shot with
	/// the manager via <see cref="Weapons.RegisterShot"/>; the manager expands pellets, detects hits, applies damage,
	/// and drives visuals. See Docs/ProjectileSystem.md and Docs/NetworkOptimizationImplementation.md.
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

		[Header("AI Evaluation")]
		[Min(0f)]
		public float       AIWeaponStrength = 1f;
		[Min(0.1f)]
		public float       AIEffectiveMaxRange = 20f;

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
		private TickTimer _fireCooldown { get; set; }

		private int _fireTicks;
		private bool _reloadingVisible;
		private GameObject _muzzleEffectInstance;
		private Survivor _ownerSurvivor;
		private Weapons _weapons;

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

			// Register one shot on the survivor-level shared stream. The manager expands ProjectilesPerShot pellets
			// deterministically (same seed on every peer), so a shotgun blast costs one networked fire event instead
			// of one per pellet. The manager owns hit detection, damage, and visuals.
			if (_weapons == null)
				_weapons = GetComponentInParent<Weapons>();
			if (_weapons == null)
				return false;

			_weapons.RegisterShot(this, firePosition, fireDirection);

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

			float fireTime = 60f / FireRate;
			_fireTicks = Mathf.CeilToInt(fireTime / Runner.DeltaTime);

			_muzzleEffectInstance = Instantiate(MuzzleEffectPrefab, MuzzleTransform);
			_muzzleEffectInstance.SetActive(false);

			_ownerSurvivor = GetComponentInParent<Survivor>();
			_weapons = GetComponentInParent<Weapons>();
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
		}

		public override void Render()
		{
			// Reload animation/sound is per-weapon and driven by the networked IsReloading state. Fire visuals
			// (muzzle, tracers, impacts) are driven centrally by the Weapons manager from the shared event streams.
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

		// Called by the Weapons manager when it renders a new shot fired by this weapon.
		public void PlayFireEffect()
		{
			if (FireSound != null)
			{
				FireSound.PlayOneShot(FireSound.clip);
			}

			// Reset muzzle effect visibility.
			_muzzleEffectInstance.SetActive(false);
			_muzzleEffectInstance.SetActive(true);

			WeaponAnimator.SetTrigger("Fire");

			if (_ownerSurvivor == null)
				_ownerSurvivor = GetComponentInParent<Survivor>();
			_ownerSurvivor?.PlayFireEffect();
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
	}
}
