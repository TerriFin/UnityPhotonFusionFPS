using UnityEngine;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class ZombieAnimator : MonoBehaviour
	{
		[Header("Components")]
		public ZombieCharacter Zombie;
		public ZombieAI AI;
		public Animator Animator;

		[Header("Parameters")]
		public string MoveSpeedParameter = "MoveSpeed";
		public string IsAliveParameter = "IsAlive";
		public string IsGroundedParameter = "IsGrounded";
		public string AttackTrigger = "Attack";

		[Header("Smoothing")]
		public float MoveSpeedDampTime = 0.1f;

		private int _moveSpeedId;
		private int _isAliveId;
		private int _isGroundedId;
		private int _attackId;
		private float _lastAttackTime = -1f;
		private bool _loggedMissingReferences;

		private void Awake()
		{
			ResolveReferences();
			RefreshParameterIds();
			WriteInitialAliveParameters();
		}

		private void OnEnable()
		{
			ResolveReferences();
			RefreshParameterIds();
			WriteInitialAliveParameters();
		}

		private void OnValidate()
		{
			RefreshParameterIds();
		}

		private void Update()
		{
			ResolveReferences();

			if (Animator == null || Zombie == null)
			{
				LogMissingReferencesOnce();
				return;
			}

			_loggedMissingReferences = false;

			bool isAlive = IsZombieAliveForAnimation();
			bool isGrounded = Zombie.KCC == null || Zombie.KCC.IsGrounded;
			float moveSpeed = Zombie.KCC != null ? FlatSpeed(Zombie.KCC.RealVelocity) : 0f;

			if (_moveSpeedId != 0)
				Animator.SetFloat(_moveSpeedId, moveSpeed, MoveSpeedDampTime, Time.deltaTime);
			if (_isAliveId != 0)
				Animator.SetBool(_isAliveId, isAlive);
			if (_isGroundedId != 0)
				Animator.SetBool(_isGroundedId, isGrounded);

			if (AI != null && AI.LastAttackTime > _lastAttackTime)
			{
				_lastAttackTime = AI.LastAttackTime;
				if (_attackId != 0)
					Animator.SetTrigger(_attackId);
			}
		}

		private void ResolveReferences()
		{
			if (Zombie == null)
				Zombie = GetComponent<ZombieCharacter>() ?? GetComponentInParent<ZombieCharacter>() ?? GetComponentInChildren<ZombieCharacter>(true);

			if (AI == null)
			{
				if (Zombie != null)
					AI = Zombie.AI != null ? Zombie.AI : Zombie.GetComponent<ZombieAI>();
				if (AI == null)
					AI = GetComponent<ZombieAI>() ?? GetComponentInParent<ZombieAI>() ?? GetComponentInChildren<ZombieAI>(true);
			}

			if (Animator == null)
				Animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>(true) ?? GetComponentInParent<Animator>();
		}

		private bool IsZombieAliveForAnimation()
		{
			if (Zombie == null)
				return true;
			if (Zombie.Object == null || Zombie.Object.IsValid == false)
				return true;
			return Zombie.Health == null || Zombie.Health.IsAlive;
		}

		private void LogMissingReferencesOnce()
		{
			if (_loggedMissingReferences)
				return;

			_loggedMissingReferences = true;
			Debug.LogWarning($"{nameof(ZombieAnimator)} on {name} is missing references. Zombie={Zombie}, Animator={Animator}. Put it on the zombie prefab root or assign both fields explicitly.", this);
		}

		private void RefreshParameterIds()
		{
			_moveSpeedId = string.IsNullOrWhiteSpace(MoveSpeedParameter) ? 0 : Animator.StringToHash(MoveSpeedParameter);
			_isAliveId = string.IsNullOrWhiteSpace(IsAliveParameter) ? 0 : Animator.StringToHash(IsAliveParameter);
			_isGroundedId = string.IsNullOrWhiteSpace(IsGroundedParameter) ? 0 : Animator.StringToHash(IsGroundedParameter);
			_attackId = string.IsNullOrWhiteSpace(AttackTrigger) ? 0 : Animator.StringToHash(AttackTrigger);
		}

		private void WriteInitialAliveParameters()
		{
			if (Animator == null)
				return;

			if (_moveSpeedId != 0)
				Animator.SetFloat(_moveSpeedId, 0f);
			if (_isAliveId != 0)
				Animator.SetBool(_isAliveId, true);
			if (_isGroundedId != 0)
				Animator.SetBool(_isGroundedId, true);
		}

		private static float FlatSpeed(Vector3 velocity)
		{
			velocity.y = 0f;
			return velocity.magnitude;
		}
	}
}
