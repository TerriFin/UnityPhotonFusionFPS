using UnityEngine;

namespace SimpleFPS
{
	public readonly struct WorldGridCell
	{
		public readonly Vector2Int Position;
		public readonly bool IsRoad;
		public readonly bool IsBoundaryExit;
		public readonly RoadEnvironment Environment;

		public WorldGridCell(Vector2Int position, bool isRoad, bool isBoundaryExit, RoadEnvironment environment)
		{
			Position = position;
			IsRoad = isRoad;
			IsBoundaryExit = isBoundaryExit;
			Environment = environment;
		}
	}

	public readonly struct WorldGridSnapshot
	{
		private readonly WorldGridCell[,] _cells;

		public readonly int Width;
		public readonly int Height;
		public readonly float TileSize;
		public readonly Vector3 Origin;

		public bool IsValid => _cells != null;

		public WorldGridSnapshot(WorldGridCell[,] cells, float tileSize, Vector3 origin)
		{
			_cells = cells;
			Width = cells != null ? cells.GetLength(0) : 0;
			Height = cells != null ? cells.GetLength(1) : 0;
			TileSize = tileSize;
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
	}
}
