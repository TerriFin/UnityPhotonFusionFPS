using UnityEngine;

namespace SimpleFPS
{
	// Drives the zombie body animator from the character's networked state. All references are resolved
	// automatically (put this anywhere on the zombie prefab) and the animator parameter names are fixed
	// contract constants — there is nothing to configure in the inspector.
	[DisallowMultipleComponent]
	public sealed class ZombieAnimator : MonoBehaviour
	{
		private const float MoveSpeedDampTime = 0.1f;

		private static readonly int MoveSpeedId = Animator.StringToHash("MoveSpeed");
		private static readonly int IsAliveId = Animator.StringToHash("IsAlive");
		private static readonly int IsGroundedId = Animator.StringToHash("IsGrounded");
		private static readonly int AttackId = Animator.StringToHash("Attack");

		private ZombieCharacter _zombie;
		private ZombieAI _ai;
		private Animator _animator;
		private float _lastAttackTime = -1f;
		private bool _loggedMissingReferences;

		private void Awake()
		{
			ResolveReferences();
			WriteInitialAliveParameters();
		}

		private void OnEnable()
		{
			ResolveReferences();
			WriteInitialAliveParameters();
		}

		private void Update()
		{
			ResolveReferences();

			if (_animator == null || _zombie == null)
			{
				LogMissingReferencesOnce();
				return;
			}

			_loggedMissingReferences = false;

			bool isAlive = IsZombieAliveForAnimation();
			bool isGrounded = _zombie.KCC == null || _zombie.KCC.IsGrounded;
			float moveSpeed = _zombie.KCC != null ? FlatSpeed(_zombie.KCC.RealVelocity) : 0f;

			_animator.SetFloat(MoveSpeedId, moveSpeed, MoveSpeedDampTime, Time.deltaTime);
			_animator.SetBool(IsAliveId, isAlive);
			_animator.SetBool(IsGroundedId, isGrounded);

			if (_ai != null && _ai.LastAttackTime > _lastAttackTime)
			{
				_lastAttackTime = _ai.LastAttackTime;
				_animator.SetTrigger(AttackId);
			}
		}

		// No ?? chains here: in the editor GetComponent returns a fake-null placeholder for a missing component,
		// which ?? (a plain CLR reference check) treats as found — the chain then never tries the child/parent
		// searches. Explicit checks go through Unity's overloaded == and handle the placeholder correctly.
		private void ResolveReferences()
		{
			if (_zombie == null)
			{
				_zombie = GetComponent<ZombieCharacter>();
				if (_zombie == null)
					_zombie = GetComponentInParent<ZombieCharacter>();
				if (_zombie == null)
					_zombie = GetComponentInChildren<ZombieCharacter>(true);
			}

			if (_ai == null && _zombie != null)
				_ai = _zombie.AI != null ? _zombie.AI : _zombie.GetComponent<ZombieAI>();
			if (_ai == null)
			{
				_ai = GetComponent<ZombieAI>();
				if (_ai == null)
					_ai = GetComponentInParent<ZombieAI>();
				if (_ai == null)
					_ai = GetComponentInChildren<ZombieAI>(true);
			}

			if (_animator == null)
			{
				_animator = GetComponent<Animator>();
				if (_animator == null)
					_animator = GetComponentInChildren<Animator>(true);
				if (_animator == null)
					_animator = GetComponentInParent<Animator>();

				// Zombie hitboxes sit on a static (non-animated) node, so the pose is visual-only: culling
				// transform writes for offscreen zombies is safe and removes most animation cost of a horde.
				if (_animator != null)
					_animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
			}
		}

		private bool IsZombieAliveForAnimation()
		{
			if (_zombie == null)
				return true;
			if (_zombie.Object == null || _zombie.Object.IsValid == false)
				return true;
			return _zombie.Health == null || _zombie.Health.IsAlive;
		}

		private void LogMissingReferencesOnce()
		{
			if (_loggedMissingReferences)
				return;

			_loggedMissingReferences = true;
			Debug.LogWarning($"{nameof(ZombieAnimator)} on {name} could not resolve its references. Zombie={_zombie}, Animator={_animator}. Put it on the zombie prefab root.", this);
		}

		private void WriteInitialAliveParameters()
		{
			if (_animator == null)
				return;

			_animator.SetFloat(MoveSpeedId, 0f);
			_animator.SetBool(IsAliveId, true);
			_animator.SetBool(IsGroundedId, true);
		}

		private static float FlatSpeed(Vector3 velocity)
		{
			velocity.y = 0f;
			return velocity.magnitude;
		}
	}
}
