using UnityEngine;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class NeutralSurvivor : MonoBehaviour
	{
		public Vector3 PatrolCenter { get; private set; }
		public float PatrolRadius { get; private set; }

		private Survivor _survivor;

		public bool IsNeutral => _survivor != null && _survivor.IsNeutral;

		public void Initialize(Survivor survivor, Vector3 patrolCenter, float patrolRadius)
		{
			_survivor = survivor != null ? survivor : GetComponent<Survivor>();
			PatrolCenter = patrolCenter;
			PatrolRadius = Mathf.Max(0f, patrolRadius);
		}

		private void Awake()
		{
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();
		}
	}
}
