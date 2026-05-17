using UnityEngine;

namespace SimpleFPS
{
	public readonly struct WorldHeightCell
	{
		public readonly Vector2Int Position;
		public readonly int HeightLevel;
		public readonly bool IsLedge;
		public readonly bool IsBoundaryLedge;
		public readonly bool CanBeReplacedByHeightChangeRoad;
		public readonly HeightTileShape LedgeShape;
		public readonly RoadDirection HighDirection;
		public readonly int LowHeightLevel;
		public readonly int HighHeightLevel;

		public WorldHeightCell(
			Vector2Int position,
			int heightLevel,
			bool isLedge,
			bool isBoundaryLedge,
			bool canBeReplacedByHeightChangeRoad,
			HeightTileShape ledgeShape,
			RoadDirection highDirection,
			int lowHeightLevel,
			int highHeightLevel)
		{
			Position = position;
			HeightLevel = heightLevel;
			IsLedge = isLedge;
			IsBoundaryLedge = isBoundaryLedge;
			CanBeReplacedByHeightChangeRoad = canBeReplacedByHeightChangeRoad;
			LedgeShape = ledgeShape;
			HighDirection = highDirection;
			LowHeightLevel = lowHeightLevel;
			HighHeightLevel = highHeightLevel;
		}
	}

	public readonly struct WorldHeightSnapshot
	{
		private readonly WorldHeightCell[,] _cells;

		public readonly int Width;
		public readonly int Height;
		public readonly int Seed;
		public readonly float TileSize;
		public readonly float HeightLevelWorldUnits;
		public readonly Vector3 Origin;

		public bool IsValid => _cells != null;

		public WorldHeightSnapshot(WorldHeightCell[,] cells, int seed, float tileSize, float heightLevelWorldUnits, Vector3 origin)
		{
			_cells = cells;
			Width = cells != null ? cells.GetLength(0) : 0;
			Height = cells != null ? cells.GetLength(1) : 0;
			Seed = seed;
			TileSize = tileSize;
			HeightLevelWorldUnits = heightLevelWorldUnits;
			Origin = origin;
		}

		public bool TryGetCell(Vector2Int position, out WorldHeightCell cell)
		{
			if (_cells == null || position.x < 0 || position.y < 0 || position.x >= Width || position.y >= Height)
			{
				cell = default;
				return false;
			}

			cell = _cells[position.x, position.y];
			return true;
		}
	}
}
