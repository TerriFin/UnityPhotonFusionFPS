using UnityEngine;

namespace SimpleFPS
{
	public class HeightTileDefinition : MonoBehaviour
	{
		public HeightTileShape Shape = HeightTileShape.Straight;
		public Vector2Int FootprintSize = Vector2Int.one;
		public bool IsBoundaryTile;
		public bool AllowsTraversalWithoutRoad;
		public bool CanBeReplacedByHeightChangeRoad = true;
		[Min(1)]
		public int Weight = 1;
		[Min(0)]
		public int RepeatCooldownDistance;

		public Vector2Int ClampedFootprintSize => new Vector2Int(Mathf.Max(1, FootprintSize.x), Mathf.Max(1, FootprintSize.y));
		public bool IsRoadReplaceable => Shape == HeightTileShape.Straight && ClampedFootprintSize == Vector2Int.one && CanBeReplacedByHeightChangeRoad;
	}
}
