using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/World Loot Spawn Settings")]
	public class WorldLootSpawnSettings : ScriptableObject
	{
		[Range(0f, 1f)]
		public float PickupPointUsage = 0.35f;
		public NetworkObject[] WeaponPickups;
		public NetworkObject[] HealthPickups;
		public int SeedOffset = 20000;
		[Tooltip("Added to the seed for the match-start re-roll, so the loot the real match starts with differs from the skirmish preview.")]
		public int MatchStartSeedOffset = 31337;
	}
}
