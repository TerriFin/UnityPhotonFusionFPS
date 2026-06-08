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

		[Tooltip("If on, survivors spawned here roam between dynamic spawn points (including unchosen ones) instead of staying in this one patrol area. Off = they stay and patrol here (current behavior). Street spawns suit roaming; complex-building spawns suit staying put.")]
		public bool DynamicSpawn;

		private void OnValidate()
		{
			MinSpawnCount = Mathf.Max(0, MinSpawnCount);
			MaxSpawnCount = Mathf.Max(MinSpawnCount, MaxSpawnCount);
			PatrolRadius = Mathf.Max(0f, PatrolRadius);
		}
	}
}
