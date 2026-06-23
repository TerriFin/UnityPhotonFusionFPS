using UnityEngine;
using UnityEngine.Serialization;

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
		[Tooltip("3D world-space radius required for recruitment. Survivors directly above or below each other are not considered close.")]
		public float RecruitmentRadius = 3f;
		public float RecruitmentCheckInterval = 0.25f;

		[Header("Neutral Stats")]
		[Tooltip("Neutral survivors use these weaker runtime stats until recruited. Recruitment restores the survivor prefab's original values.")]
		public bool ApplyNeutralStatOverrides = true;
		[Min(0f)]
		[FormerlySerializedAs("NeutralAIMoveSpeed")]
		public float NeutralMovementSpeed = 3.5f;
		[Min(0f)]
		public float NeutralVisionDistance = 14f;
		[Min(0f)]
		public float NeutralAllAroundDetectionRange = 2.5f;
		[Min(0.02f)]
		public float NeutralSensorInterval = 0.35f;
		[Min(0f)]
		[FormerlySerializedAs("NeutralZombieHorizontalAimErrorDegrees")]
		public float NeutralHorizontalAimErrorDegrees = 6f;
		[Min(0f)]
		[FormerlySerializedAs("NeutralZombieVerticalAimErrorDegrees")]
		public float NeutralVerticalAimErrorDegrees = 3f;
	}
}
