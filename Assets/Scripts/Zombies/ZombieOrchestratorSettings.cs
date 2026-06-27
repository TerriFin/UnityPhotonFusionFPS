using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/Zombies/Zombie Orchestrator Settings")]
	public sealed class ZombieOrchestratorSettings : ScriptableObject
	{
		public NetworkObject ZombiePrefab;

		[Header("Population")]
		public int StartMaxZombies = 100;
		public int EndMaxZombies = 400;
		[Range(0f, 1f)]
		[Tooltip("How much of StartMaxZombies to pre-populate when zombie spawning starts. 0 keeps the map empty until spawn pulses. 1 tries to spawn up to StartMaxZombies, still respecting each spawn point's MaxSpawnCountPerPulse.")]
		public float InitialPopulation = 0f;
		public float MatchDurationSeconds = 0f;
		public bool ScaleDuringSkirmish;

		[Header("Spawn Rate (per connected player)")]
		[Tooltip("Zombies spawned per minute for each connected player at match start. The effective spawn rate is this value multiplied by the number of connected players, so every additional player raises the rate by this much.")]
		public float StartSpawnRatePerMinute = 12f;
		[Tooltip("Zombies spawned per minute for each connected player at full match progress. The effective spawn rate is this value multiplied by the number of connected players.")]
		public float EndSpawnRatePerMinute = 60f;
		public float SpawnPulseInterval = 5f;
		[Min(1)]
		[Tooltip("Maximum zombies instantiated per simulation tick when draining a spawn pulse. A pulse's whole budget is spread across consecutive ticks at this rate instead of spawning dozens of NetworkObjects in one frame, which avoids the periodic overtime hitch. The pulse still fully drains before the next pulse interval.")]
		public int MaxSpawnsPerTick = 4;

		[Header("Spawn Validity")]
		public float SpawnNavMeshSampleDistance = 1.5f;
		public float MinimumSpawnConnectedNavMeshRadius = 8f;

		[Header("Normal Stats")]
		public float StartHealth = 40f;
		public float EndHealth = 120f;
		public float StartDamage = 8f;
		public float EndDamage = 20f;
		public float StartMoveSpeed = 2.2f;
		public float EndMoveSpeed = 4.5f;
		public float StartAlertRadius = 8f;
		public float EndAlertRadius = 24f;

		[Header("Overtime Spawn Rate (per connected player)")]
		[Tooltip("Zombies spawned per minute for each connected player during overtime, independent of the normal Start/End spawn rate. Multiplied by the number of connected players.")]
		public float OvertimeSpawnRatePerMinute = 60f;

		[Header("Overtime Stats")]
		public float OvertimeHealth = 180f;
		[Min(0f)]
		[Tooltip("Flat max-health bonus applied only to newly spawned overtime zombies for each completed 10 seconds of overtime. Existing zombies are not repeatedly rescaled. Set to 0 to disable progressive overtime health.")]
		public float OvertimeNewZombieHealthIncreasePer10Seconds = 10f;
		public float OvertimeDamage = 35f;
		public float OvertimeMoveSpeed = 7.5f;

		[Header("Regional Pressure")]
		[Range(0f, 1f)]
		public float UnderpopulatedRegionBias = 0.65f;
		public int RegionGridSize = 4;
		public int SeedOffset = 30000;
		[Tooltip("Added to the world seed for the match-start re-roll. When the match leaves skirmish, the skirmish horde is despawned and a fresh layout is re-seeded with this offset so the live match differs from the skirmish preview.")]
		public int MatchStartSeedOffset = 7919;

		private void OnValidate()
		{
			StartMaxZombies = Mathf.Max(0, StartMaxZombies);
			EndMaxZombies = Mathf.Max(StartMaxZombies, EndMaxZombies);
			InitialPopulation = Mathf.Clamp01(InitialPopulation);
			MatchDurationSeconds = Mathf.Max(0f, MatchDurationSeconds);
			StartSpawnRatePerMinute = Mathf.Max(0f, StartSpawnRatePerMinute);
			EndSpawnRatePerMinute = Mathf.Max(0f, EndSpawnRatePerMinute);
			SpawnPulseInterval = Mathf.Max(0.25f, SpawnPulseInterval);
			MaxSpawnsPerTick = Mathf.Max(1, MaxSpawnsPerTick);
			OvertimeSpawnRatePerMinute = Mathf.Max(0f, OvertimeSpawnRatePerMinute);
			SpawnNavMeshSampleDistance = Mathf.Max(0.1f, SpawnNavMeshSampleDistance);
			MinimumSpawnConnectedNavMeshRadius = Mathf.Max(0f, MinimumSpawnConnectedNavMeshRadius);
			StartHealth = Mathf.Max(1f, StartHealth);
			EndHealth = Mathf.Max(1f, EndHealth);
			StartDamage = Mathf.Max(0f, StartDamage);
			EndDamage = Mathf.Max(0f, EndDamage);
			StartMoveSpeed = Mathf.Max(0f, StartMoveSpeed);
			EndMoveSpeed = Mathf.Max(0f, EndMoveSpeed);
			StartAlertRadius = Mathf.Max(0f, StartAlertRadius);
			EndAlertRadius = Mathf.Max(0f, EndAlertRadius);
			OvertimeHealth = Mathf.Max(1f, OvertimeHealth);
			OvertimeNewZombieHealthIncreasePer10Seconds = Mathf.Max(0f, OvertimeNewZombieHealthIncreasePer10Seconds);
			OvertimeDamage = Mathf.Max(0f, OvertimeDamage);
			OvertimeMoveSpeed = Mathf.Max(0f, OvertimeMoveSpeed);
			RegionGridSize = Mathf.Max(1, RegionGridSize);
		}
	}
}
