using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Frame-cached XZ spatial hash over a list of active components. Callers keep their existing static
	/// registries (ActiveSensors, ActiveSeparators, ActivePickups, …) as the source of truth; the hash is a
	/// throwaway per-frame index over them so "who is near me" queries stop scanning the entire registry.
	///
	/// Usage per query: RebuildIfStale(registry) then Query(position, radius, buffer). The rebuild runs at most
	/// once per rendered frame regardless of how many actors query (a few hundred dictionary inserts), and only
	/// on frames where someone actually queries. Query returns a *superset* — every item within the covered cells
	/// — and callers keep their exact distance/eligibility filtering, so results are identical to the old full
	/// scans, just fed fewer candidates. Height is ignored (XZ only), matching the flat-distance checks the
	/// existing scans already use.
	///
	/// Not thread-safe; matches Unity main-thread usage. RebuildIfStale also prunes destroyed (null) entries from
	/// the source registry, preserving the housekeeping the old full scans performed.
	/// </summary>
	public sealed class SpatialHash2D<T> where T : Component
	{
		private readonly Dictionary<long, List<T>> _cells = new();
		private readonly Stack<List<T>> _bucketPool = new();
		private readonly float _cellSize;
		private int _rebuildFrame = -1;

		public SpatialHash2D(float cellSize)
		{
			_cellSize = Mathf.Max(0.5f, cellSize);
		}

		public void RebuildIfStale(List<T> source)
		{
			if (_rebuildFrame == Time.frameCount)
				return;
			_rebuildFrame = Time.frameCount;

			foreach (List<T> bucket in _cells.Values)
			{
				bucket.Clear();
				_bucketPool.Push(bucket);
			}
			_cells.Clear();

			for (int i = source.Count - 1; i >= 0; i--)
			{
				T item = source[i];
				if (item == null)
				{
					source.RemoveAt(i);
					continue;
				}

				long key = CellKey(item.transform.position);
				if (_cells.TryGetValue(key, out List<T> cellBucket) == false)
				{
					cellBucket = _bucketPool.Count > 0 ? _bucketPool.Pop() : new List<T>(8);
					_cells.Add(key, cellBucket);
				}

				cellBucket.Add(item);
			}
		}

		/// <summary>
		/// Collects every item in the cells overlapped by the XZ circle into <paramref name="results"/> (cleared
		/// first). A coarse superset — callers apply their own exact distance/eligibility checks.
		/// </summary>
		public void Query(Vector3 position, float radius, List<T> results)
		{
			results.Clear();

			float r = Mathf.Max(0f, radius);
			int minX = Mathf.FloorToInt((position.x - r) / _cellSize);
			int maxX = Mathf.FloorToInt((position.x + r) / _cellSize);
			int minZ = Mathf.FloorToInt((position.z - r) / _cellSize);
			int maxZ = Mathf.FloorToInt((position.z + r) / _cellSize);

			for (int cx = minX; cx <= maxX; cx++)
			{
				for (int cz = minZ; cz <= maxZ; cz++)
				{
					if (_cells.TryGetValue(PackCell(cx, cz), out List<T> bucket) == false)
						continue;

					for (int i = 0; i < bucket.Count; i++)
						results.Add(bucket[i]);
				}
			}
		}

		private long CellKey(Vector3 position)
		{
			return PackCell(Mathf.FloorToInt(position.x / _cellSize), Mathf.FloorToInt(position.z / _cellSize));
		}

		private static long PackCell(int cellX, int cellZ)
		{
			return ((long)cellX << 32) ^ (uint)cellZ;
		}
	}
}
