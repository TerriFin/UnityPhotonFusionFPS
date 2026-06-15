using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	// State-authority-only terrain query service for zombie climbing decisions. It holds the generated height
	// snapshot (pushed by ZombieOrchestrator once world generation has produced it) and answers two questions:
	//
	//   - what terrain height level is at a world position?
	//   - where is the nearest climbable terrain ledge between a zombie and its target that steps UP a level?
	//
	// The height generator guarantees neighbouring cells differ by at most one level, so every ledge is a single
	// ~HeightLevelWorldUnits step, open-air and (by authoring) overhang-free — a bounded, safe climb. Building /
	// structure verticality is NOT a terrain ledge and is intentionally never returned here; zombies reach those
	// through the NavMesh plus a capped final climb.
	//
	// If no snapshot has been published (flat map, generation not finished, or running without a height generator),
	// HasSnapshot is false and zombies fall back to NavMesh + capped final climb, which still reaches every target.
	public static class ZombieTerrain
	{
		private static WorldHeightSnapshot _snapshot;
		private static readonly List<WorldHeightCell> _ledgeCells = new();
		private static bool _hasCache;
		private static int _cachedSeed;

		public static bool HasSnapshot => _snapshot.IsValid;

		// Called every spawner tick by the orchestrator. Cheap to no-op: the ledge cache is only rebuilt when the
		// snapshot actually changes (different seed = different generated map), so re-publishing each tick is free
		// while still picking up a mid-session regeneration.
		public static void SetSnapshot(WorldHeightSnapshot snapshot)
		{
			if (snapshot.IsValid == false)
				return;
			if (_hasCache && _snapshot.IsValid && snapshot.Seed == _cachedSeed)
			{
				_snapshot = snapshot;
				return;
			}

			_snapshot = snapshot;
			_cachedSeed = snapshot.Seed;
			RebuildLedgeCache();
			_hasCache = true;
		}

		public static void Clear()
		{
			_snapshot = default;
			_ledgeCells.Clear();
			_hasCache = false;
			_cachedSeed = 0;
		}

		public static bool TryGetLevel(Vector3 worldPosition, out int level)
		{
			return _snapshot.TryGetHeightLevel(worldPosition, out level);
		}

		// Nearest ledge (by flat distance) that bridges fromLevel -> fromLevel + 1, lies generally toward the
		// target rather than behind the zombie, and is within maxSearchDistance. Returns a low-side approach base
		// (which the NavMesh resolves onto the lower plateau) and a high-side climb target on the upper plateau.
		// Returns false when there is no snapshot or no suitable ledge, in which case the caller takes the ramp.
		public static bool TryFindClimbLedge(Vector3 from, Vector3 target, int fromLevel, float maxSearchDistance,
			out Vector3 ledgeBase, out Vector3 ledgeTop)
		{
			ledgeBase = default;
			ledgeTop = default;
			if (_snapshot.IsValid == false || _ledgeCells.Count == 0)
				return false;

			Vector3 toTarget = target - from;
			toTarget.y = 0f;
			if (toTarget.sqrMagnitude < 0.0001f)
				return false;
			Vector3 towardTarget = toTarget.normalized;

			float maxSqr = maxSearchDistance > 0f ? maxSearchDistance * maxSearchDistance : float.MaxValue;
			float bestSqr = float.MaxValue;
			float halfTile = _snapshot.TileSize * 0.5f;
			bool found = false;

			for (int i = 0; i < _ledgeCells.Count; i++)
			{
				WorldHeightCell cell = _ledgeCells[i];
				// Only ledges that take us up exactly one level from where we stand.
				if (cell.LowHeightLevel != fromLevel || cell.HighHeightLevel <= fromLevel)
					continue;

				Vector3 cellCenter = _snapshot.CellCenterWorld(cell.Position, cell.LowHeightLevel);
				Vector3 toCell = cellCenter - from;
				toCell.y = 0f;
				float distSqr = toCell.sqrMagnitude;
				if (distSqr > maxSqr || distSqr >= bestSqr)
					continue;
				// Must be generally toward the target, not a ledge behind the zombie.
				if (Vector3.Dot(toCell, towardTarget) <= 0f)
					continue;

				Vector3 highOffset = DirectionToOffset(cell.HighDirection);
				ledgeBase = cellCenter - highOffset * halfTile;
				ledgeBase.y = _snapshot.Origin.y + cell.LowHeightLevel * _snapshot.HeightLevelWorldUnits;
				ledgeTop = cellCenter + highOffset * halfTile;
				ledgeTop.y = _snapshot.Origin.y + cell.HighHeightLevel * _snapshot.HeightLevelWorldUnits;
				bestSqr = distSqr;
				found = true;
			}

			return found;
		}

		private static void RebuildLedgeCache()
		{
			_ledgeCells.Clear();
			if (_snapshot.IsValid == false)
				return;

			for (int x = 0; x < _snapshot.Width; x++)
			{
				for (int y = 0; y < _snapshot.Height; y++)
				{
					if (_snapshot.TryGetCell(new Vector2Int(x, y), out var cell) && cell.IsLedge)
						_ledgeCells.Add(cell);
				}
			}
		}

		private static Vector3 DirectionToOffset(RoadDirection direction)
		{
			switch (direction)
			{
				case RoadDirection.North: return Vector3.forward;
				case RoadDirection.East: return Vector3.right;
				case RoadDirection.South: return Vector3.back;
				case RoadDirection.West: return Vector3.left;
				default: return Vector3.forward;
			}
		}
	}
}
