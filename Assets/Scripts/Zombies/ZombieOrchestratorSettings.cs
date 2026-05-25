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
		public float MatchDurationSeconds = 0f;
		public bool ScaleDuringSkirmish;

		[Header("Spawn Rate")]
		public float StartSpawnRatePerMinute = 12f;
		public float EndSpawnRatePerMinute = 60f;
		public float SpawnPulseInterval = 5f;
		public int MaxSpawnPerPulse = 30;

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

		[Header("Overtime Stats")]
		public float OvertimeHealth = 180f;
		public float OvertimeDamage = 35f;
		public float OvertimeMoveSpeed = 7.5f;

		[Header("Regional Pressure")]
		[Range(0f, 1f)]
		public float UnderpopulatedRegionBias = 0.65f;
		public int RegionGridSize = 4;
		public int SeedOffset = 30000;

		private void OnValidate()
		{
			StartMaxZombies = Mathf.Max(0, StartMaxZombies);
			EndMaxZombies = Mathf.Max(StartMaxZombies, EndMaxZombies);
			MatchDurationSeconds = Mathf.Max(0f, MatchDurationSeconds);
			StartSpawnRatePerMinute = Mathf.Max(0f, StartSpawnRatePerMinute);
			EndSpawnRatePerMinute = Mathf.Max(0f, EndSpawnRatePerMinute);
			SpawnPulseInterval = Mathf.Max(0.25f, SpawnPulseInterval);
			MaxSpawnPerPulse = Mathf.Max(0, MaxSpawnPerPulse);
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
			OvertimeDamage = Mathf.Max(0f, OvertimeDamage);
			OvertimeMoveSpeed = Mathf.Max(0f, OvertimeMoveSpeed);
			RegionGridSize = Mathf.Max(1, RegionGridSize);
		}
	}
}
