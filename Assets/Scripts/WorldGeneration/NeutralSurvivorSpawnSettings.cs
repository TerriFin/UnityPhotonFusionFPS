using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Neutral Survivor Spawn Settings")]
	public sealed class NeutralSurvivorSpawnSettings : ScriptableObject
	{
		[Range(0f, 1f)]
		public float SpawnPointUsage = 1f;
		public float MinDistanceBetweenSelectedSpawnPoints;
		public int SeedOffset;
		public float SpawnNavMeshSampleDistance = 1.5f;
		public float MinimumSpawnConnectedNavMeshRadius = 8f;
		public float RecruitmentRadius = 3f;
		public float RecruitmentCheckInterval = 0.25f;
	}
}
