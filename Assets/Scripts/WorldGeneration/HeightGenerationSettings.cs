using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Height Generation Settings")]
	public class HeightGenerationSettings : ScriptableObject
	{
		[Min(1)]
		public int HeightLayerCount = 1;
		[Min(1)]
		public int MinCellsBetweenHeightChanges = 3;
		[Min(1)]
		public int MinUsableRegionWidth = 3;
		[Min(1)]
		public int MinUsableRegionHeight = 3;
		[Min(1)]
		public int MinUsableRegionArea = 9;
		[Min(0)]
		public int SmoothingPasses = 2;
		[Range(0f, 1f)]
		public float RegionBalance = 0.5f;
		[Min(1)]
		public int MaxGenerationAttempts = 100;
		[Min(0)]
		public int DefaultLedgeRepeatCooldownDistance = 2;
		[Min(0)]
		public int MinRoadReplaceableLedgesPerHeightRegion = 5;

		public HeightTileSet LedgeTiles;
	}
}
