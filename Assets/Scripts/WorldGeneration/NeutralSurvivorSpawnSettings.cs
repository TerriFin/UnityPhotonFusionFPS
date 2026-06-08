using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Neutral Survivor Spawn Settings")]
	public sealed class NeutralSurvivorSpawnSettings : ScriptableObject
	{
		[Min(0)]
		[Tooltip("Total neutral survivors the orchestrator tries to spawn. It selects spaced markers until this many survivors are placed; if valid markers run out first, fewer spawn (that is fine).")]
		public int DesiredNeutralSurvivorCount = 20;
		public float MinDistanceBetweenSelectedSpawnPoints;
		[Tooltip("Neutral spawn markers within this flat distance (plus the marker's patrol radius) of an in-use player spawn are skipped. 0 disables the constraint. Only player spawns currently assigned to a connected player count.")]
		public float MinDistanceToActivePlayerSpawns;
		public int SeedOffset;
		[Tooltip("Added to the world seed for the match-start re-roll, so the layout the real match starts with differs from the skirmish preview.")]
		public int MatchStartSeedOffset = 7919;
		public float SpawnNavMeshSampleDistance = 1.5f;
		public float MinimumSpawnConnectedNavMeshRadius = 8f;
		public float RecruitmentRadius = 3f;
		public float RecruitmentCheckInterval = 0.25f;
	}
}
