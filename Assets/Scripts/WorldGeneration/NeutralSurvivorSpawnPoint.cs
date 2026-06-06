using UnityEngine;

namespace SimpleFPS
{
	public sealed class NeutralSurvivorSpawnPoint : MonoBehaviour
	{
		[Min(0)]
		public int MinSpawnCount = 1;
		[Min(0)]
		public int MaxSpawnCount = 1;
		[Min(0f)]
		public float PatrolRadius = 8f;

		private void OnValidate()
		{
			MinSpawnCount = Mathf.Max(0, MinSpawnCount);
			MaxSpawnCount = Mathf.Max(MinSpawnCount, MaxSpawnCount);
			PatrolRadius = Mathf.Max(0f, PatrolRadius);
		}
	}
}
