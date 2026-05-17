using UnityEngine;

namespace SimpleFPS
{
	public readonly struct WorldGridCell
	{
		public readonly Vector2Int Position;
		public readonly int HeightLevel;
		public readonly bool IsRoad;
		public readonly bool IsBoundaryExit;
		public readonly bool IsLedge;
		public readonly bool IsHeightChangeRoad;
		public readonly RoadEnvironment Environment;

		public WorldGridCell(Vector2Int position, int heightLevel, bool isRoad, bool isBoundaryExit, bool isLedge, bool isHeightChangeRoad, RoadEnvironment environment)
		{
			Position = position;
			HeightLevel = heightLevel;
			IsRoad = isRoad;
			IsBoundaryExit = isBoundaryExit;
			IsLedge = isLedge;
			IsHeightChangeRoad = isHeightChangeRoad;
			Environment = environment;
		}
	}

	public readonly struct WorldGridSnapshot
	{
		private readonly WorldGridCell[,] _cells;

		public readonly int Width;
		public readonly int Height;
		public readonly float TileSize;
		public readonly float HeightLevelWorldUnits;
		public readonly Vector3 Origin;

		public bool IsValid => _cells != null;

		public WorldGridSnapshot(WorldGridCell[,] cells, float tileSize, float heightLevelWorldUnits, Vector3 origin)
		{
			_cells = cells;
			Width = cells != null ? cells.GetLength(0) : 0;
			Height = cells != null ? cells.GetLength(1) : 0;
			TileSize = tileSize;
			HeightLevelWorldUnits = heightLevelWorldUnits;
			Origin = origin;
		}

		public bool TryGetCell(Vector2Int position, out WorldGridCell cell)
		{
			if (position.x < 0 || position.y < 0 || position.x >= Width || position.y >= Height || _cells == null)
			{
				cell = default;
				return false;
			}

			cell = _cells[position.x, position.y];
			return true;
		}

		public Vector3 CellToWorld(Vector2 position)
		{
			return Origin + new Vector3(position.x * TileSize, 0f, position.y * TileSize);
		}

		public Vector3 CellToWorld(Vector2 position, int heightLevel)
		{
			return Origin + new Vector3(position.x * TileSize, heightLevel * HeightLevelWorldUnits, position.y * TileSize);
		}
	}
}
