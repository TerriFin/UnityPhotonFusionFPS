using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Building Placement Settings")]
	public class BuildingPlacementSettings : ScriptableObject
	{
		public BuildingSet BuildingSet;
		[Range(0f, 1f)]
		public float LargeBuildingPreference = 0.5f;
		[Min(0)]
		public int RepeatCooldownDistance = 2;
		public int SeedOffset = 10000;
		public bool FillMapEdgesWithBlockingBuildings = true;
		public bool FillRemainingEmptyCellsWithBlockingBuildings = true;
		public bool ReplaceCornerLedgesWithBlockingBuildings = true;
	}
}
