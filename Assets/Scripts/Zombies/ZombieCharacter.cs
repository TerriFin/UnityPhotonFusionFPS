using System.Collections.Generic;
using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

namespace SimpleFPS
{
	[DefaultExecutionOrder(-5)]
	public sealed class ZombieCharacter : NetworkBehaviour
	{
		internal static readonly List<ZombieCharacter> ActiveZombies = new(256);

		[Header("Components")]
		public SimpleKCC KCC;
		public Health Health;
		public HitboxRoot HitboxRoot;
		public Animator Animator;

		[Header("Default Stats")]
		public ZombieStats Stats = new ZombieStats
		{
			MaxHealth = 40f,
			Damage = 8f,
			MoveSpeed = 2.2f,
			AlertRadius = 8f,
			AttackRange = 1.2f,
			AttackCooldown = 1.1f,
		};

		[Header("Movement")]
		public float UpGravity = 15f;
		public float DownGravity = 25f;
		public float GroundAcceleration = 45f;
		public float GroundDeceleration = 20f;
		public float AirAcceleration = 20f;
		public float AirDeceleration = 1.3f;

		[Header("Death")]
		public float DespawnAfterDeath = 8f;

		[Networked]
		private Vector3 _moveVelocity { get; set; }

		private TickTimer _despawnTimer;
		private float _nextAttackTime;

		public CharacterSensor Sensor { get; private set; }
		public CharacterNavigator Navigator { get; private set; }
		public CharacterSeparation Separation { get; private set; }
		public ZombieAI AI { get; private set; }
		public bool IsOvertime { get; private set; }

		public override void Spawned()
		{
			if (KCC == null)
				KCC = GetComponent<SimpleKCC>();
			if (Health == null)
				Health = GetComponent<Health>();
			if (HitboxRoot == null)
				HitboxRoot = GetComponent<HitboxRoot>();
			if (Animator == null)
				Animator = GetComponentInChildren<Animator>();

			Sensor = GetComponent<CharacterSensor>();
			if (Sensor == null)
				Sensor = gameObject.AddComponent<CharacterSensor>();

			Navigator = GetComponent<CharacterNavigator>();
			if (Navigator == null)
				Navigator = gameObject.AddComponent<CharacterNavigator>();

			Separation = GetComponent<CharacterSeparation>();
			if (Separation == null)
				Separation = gameObject.AddComponent<CharacterSeparation>();
			Separation.Kind = CharacterSeparationKind.Zombie;
			Separation.Activate(this);

			AI = GetComponent<ZombieAI>();
			if (AI == null)
				AI = gameObject.AddComponent<ZombieAI>();
			AI.Activate(this);

			if (HasStateAuthority && Health != null)
			{
				Health.ImmortalDurationAfterSpawn = 0f;
				Health.SetMaxHealth(Stats.MaxHealth, false);
				Health.StopImmortality();
			}

			if (ActiveZombies.Contains(this) == false)
				ActiveZombies.Add(this);
		}

		public override void Despawned(NetworkRunner runner, bool hasState)
		{
			ActiveZombies.Remove(this);
			Separation?.Deactivate();
		}

		public override void FixedUpdateNetwork()
		{
			if (HasStateAuthority == false)
				return;

			if (Health == null || Health.IsAlive == false)
			{
				HandleDead();
				return;
			}

			NetworkedInput input = AI != null ? AI.GetInput(Runner) : default;
			ProcessInput(input);
		}

		public void ApplyStats(ZombieStats stats, bool preserveHealthPercentage)
		{
			Stats = SanitizeStats(stats);

			if (HasStateAuthority && Health != null)
			{
				Health.SetMaxHealth(Stats.MaxHealth, preserveHealthPercentage);
				Health.StopImmortality();
			}
		}

		public void EnterOvertime(ZombieStats overtimeStats)
		{
			if (IsOvertime)
				return;

			IsOvertime = true;
			ApplyStats(overtimeStats, true);
			AI?.EnterHunting();
		}

		public void ReceiveInvestigationStimulus(Vector3 target, int stimulusTick)
		{
			if (HasStateAuthority == false || Health == null || Health.IsAlive == false || IsOvertime)
				return;

			AI?.ReceiveInvestigationStimulus(target, stimulusTick, false);
		}

		public void ReceiveZombieAlert(Vector3 target, int stimulusTick)
		{
			if (HasStateAuthority == false || Health == null || Health.IsAlive == false || IsOvertime)
				return;

			AI?.ReceiveInvestigationStimulus(target, stimulusTick, true);
		}

		public bool TryAttack(NetworkObject target)
		{
			if (target == null || Time.timeSinceLevelLoad < _nextAttackTime)
				return false;

			var survivor = target.GetComponent<Survivor>();
			if (survivor == null || survivor.Health == null || survivor.Health.IsAlive == false)
				return false;

			Vector3 toTarget = survivor.transform.position - transform.position;
			toTarget.y = 0f;
			float range = Mathf.Max(0.1f, Stats.AttackRange);
			if (toTarget.sqrMagnitude > range * range)
				return false;

			Vector3 direction = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : transform.forward;
			survivor.Health.ApplyDamage(PlayerRef.None, Stats.Damage, survivor.transform.position, direction, default, false);
			_nextAttackTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, Stats.AttackCooldown);
			return true;
		}

		public void MantleTo(Vector3 groundPosition)
		{
			if (HasStateAuthority == false || KCC == null)
				return;

			_moveVelocity = Vector3.zero;
			Navigator?.ClearDestination();
			KCC.SetPosition(groundPosition + Vector3.up * 0.05f);
		}

		private void ProcessInput(NetworkedInput input)
		{
			if (KCC == null)
				return;

			bool isClimbing = AI != null && AI.WantsToClimb;
			KCC.AddLookRotation(input.LookRotationDelta, -89f, 89f);
			KCC.SetGravity(isClimbing ? 0f : KCC.RealVelocity.y >= 0f ? -UpGravity : -DownGravity);

			var inputDirection = KCC.TransformRotation * new Vector3(input.MoveDirection.x, 0f, input.MoveDirection.y);
			Vector3 desiredMoveVelocity = inputDirection * Stats.MoveSpeed;
			float climbImpulse = 0f;
			if (isClimbing)
			{
				float climbSpeed = Stats.MoveSpeed * Mathf.Max(0f, AI.ClimbSpeedMultiplier);
				desiredMoveVelocity = inputDirection * climbSpeed;
				climbImpulse = climbSpeed;
			}

			MoveZombie(desiredMoveVelocity, climbImpulse);
		}

		private void MoveZombie(Vector3 desiredMoveVelocity = default, float jumpImpulse = default)
		{
			if (HasStateAuthority && Separation != null)
				desiredMoveVelocity += Separation.GetSeparationVelocity();

			float acceleration = desiredMoveVelocity == Vector3.zero
				? (KCC.IsGrounded ? GroundDeceleration : AirDeceleration)
				: (KCC.IsGrounded ? GroundAcceleration : AirAcceleration);

			_moveVelocity = Vector3.Lerp(_moveVelocity, desiredMoveVelocity, acceleration * Runner.DeltaTime);
			KCC.Move(_moveVelocity, jumpImpulse);
		}

		private void HandleDead()
		{
			if (HitboxRoot != null)
				HitboxRoot.HitboxRootActive = false;

			if (KCC != null)
			{
				KCC.SetColliderLayer(LayerMask.NameToLayer("Ignore Raycast"));
				KCC.SetCollisionLayerMask(LayerMask.GetMask("Default"));
				MoveZombie();
			}

			if (_despawnTimer.IsRunning == false && DespawnAfterDeath > 0f)
				_despawnTimer = TickTimer.CreateFromSeconds(Runner, DespawnAfterDeath);

			if (_despawnTimer.Expired(Runner) && Object != null)
				Runner.Despawn(Object);
		}

		private static ZombieStats SanitizeStats(ZombieStats stats)
		{
			stats.MaxHealth = Mathf.Max(1f, stats.MaxHealth);
			stats.Damage = Mathf.Max(0f, stats.Damage);
			stats.MoveSpeed = Mathf.Max(0f, stats.MoveSpeed);
			stats.AlertRadius = Mathf.Max(0f, stats.AlertRadius);
			stats.AttackRange = Mathf.Max(0.1f, stats.AttackRange);
			stats.AttackCooldown = Mathf.Max(0.05f, stats.AttackCooldown);
			return stats;
		}
	}
}
