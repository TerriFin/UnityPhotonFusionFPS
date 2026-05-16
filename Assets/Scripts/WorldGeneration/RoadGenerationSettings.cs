using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Road Generation Settings")]
	public class RoadGenerationSettings : ScriptableObject
	{
		[Min(0)]
		public int RequestedExitCount = 4;
		[Min(1)]
		public int MinExitSpacing = 2;
		[Min(0)]
		public int MinRoadSpacing = 1;
		[Min(0)]
		public int MaxRoadSpacing = 2;
		public bool PreventSolidRoadBlocks = true;
		[Range(1, 9)]
		public int MaxRoadCellsIn3x3 = 5;
		[Range(0f, 1f)]
		public float ExtraRoadDensity = 0.15f;
		[Range(0f, 1f)]
		public float StubRoadStartDensity = 0.75f;
		public bool RequireDiagonalSpaceForStubRoads;
		[Min(1)]
		public int MinConnectingRoadLength = 4;
		[Min(1)]
		public int MaxPathAttempts = 200;
		public RoadTileSet NormalRoadTiles;
	}
}
