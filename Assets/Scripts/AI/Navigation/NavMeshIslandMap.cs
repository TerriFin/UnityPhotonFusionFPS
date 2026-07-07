using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	/// <summary>
	/// Precomputed NavMesh connectivity map. After every NavMesh bake the triangulation is welded and
	/// flood-filled (union-find) into connectivity "islands" — a street network is one huge island, a car roof or
	/// crate top cut out by a NavMeshModifier is its own tiny island. A spatial grid over the triangles then
	/// answers "which island is this world position on" with a few dictionary lookups and zero NavMesh queries.
	///
	/// This exists because several hot AI questions are pure connectivity questions that were previously answered
	/// with rings of NavMesh.SamplePosition probes plus NavMesh.CalculatePath calls:
	///  - "Am I stranded on a small disconnected island?" (ZombieAI stuck detection)
	///  - "Does a complete path to the target's height exist at all?" (ZombieAI close-combat guards)
	///  - "Is this destination on a different island?" → no path can exist, skip the expensive unreachable
	///    path flood entirely (CharacterNavigator).
	///  - "Is this spawn marker on a big enough connected patch?" (zombie / neutral spawn validation)
	///
	/// Island inequality is a *universally valid* "no path exists" answer (a path cannot cross islands under any
	/// area mask). Island equality guarantees a complete path only for agents whose area mask covers all baked
	/// areas (NavMesh.AllAreas) — callers with restrictive masks must treat equality as "unknown" and fall back.
	///
	/// The map rebuilds lazily on first query and is invalidated by BuildingPlacementGenerator right after its
	/// runtime NavMeshSurface.BuildNavMesh() — the single place the NavMesh changes.
	/// </summary>
	public static class NavMeshIslandMap
	{
		// Lateral (XZ) quantization used to weld triangulation vertices. Unity's CalculateTriangulation
		// duplicates vertices along internal tile boundaries; welding by position restores adjacency. 0.05 m is
		// far below any real mesh feature but comfortably above float noise.
		private const float WeldEpsilon = 0.05f;
		// Vertical weld/merge tolerance. Recast connects bake-tile borders in the navmesh graph with a vertical
		// tolerance around walkableClimb (~0.4 m), so the polygon vertices on either side of a border can
		// legitimately disagree in height by several tenths of a metre while being one connected surface. Kept
		// well below the ~1 m+ gap to the nearest genuinely separate layer (car/crate tops, ledges).
		private const float VerticalWeldTolerance = 0.4f;
		// Lateral tolerance for the T-junction merge pass: a vertex within this XZ distance of another
		// triangle's edge (and within VerticalWeldTolerance of its height there) is treated as lying on it.
		private const float TJunctionEpsilon = 0.1f;
		// How far (in grid cells) the path-verified merge pass searches for main-component triangles around a
		// leftover island before asking NavMesh.CalculatePath whether the two are actually connected.
		private const int PathMergeSearchCells = 3;
		// Anything larger than this XZ area is ordinary walkable ground, never a "stranded on a prop" island.
		// Negative connectivity verdicts ("different islands → no path") are only trusted when one side is at
		// most this size: the fast paths exist for small prop islands (car roofs ~9 m², crates, short ledge
		// strips), while a "large vs large disconnect" is more likely triangulation fragmentation than truth,
		// so callers fall back to a real NavMesh query there instead of trusting the map.
		public const float SmallIslandMaxArea = 150f;
		// XZ size of one spatial-grid cell. Street triangles span multiple cells (they are inserted into every
		// cell their AABB overlaps); prop islands fall into one or two.
		private const float GridCellSize = 8f;

		private static Vector3[] _vertices;
		private static int[] _indices;
		private static int[] _triangleIsland;      // triangle index -> compact island id
		private static float[] _islandAreas;       // island id -> summed XZ-projected area (m²)
		private static bool[] _islandVerified;     // island id -> disconnection from other verified islands is path-proven
		private static readonly Dictionary<long, List<int>> _grid = new();
		private static int[] _triangleQueryStamp;  // per-triangle dedupe stamp for multi-cell queries
		private static int _queryStamp;
		private static bool _built;

		public static int IslandCount { get; private set; }

		/// <summary>Call whenever the NavMesh is (re)baked. The next query rebuilds the map.</summary>
		public static void Invalidate()
		{
			_built = false;
		}

#if UNITY_EDITOR
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetForDomainReloadDisabled()
		{
			// With Enter Play Mode domain reload disabled, statics survive between play sessions; start clean.
			Invalidate();
		}
#endif

		/// <summary>
		/// Finds the NavMesh island at <paramref name="position"/>. A triangle qualifies when the position is
		/// within <paramref name="horizontalTolerance"/> of it in XZ and within <paramref name="heightTolerance"/>
		/// of its surface height; the closest qualifying triangle wins. Returns false when the map is unavailable
		/// (no baked NavMesh yet) or no triangle qualifies — callers should fall back to real NavMesh probing only
		/// when they cannot treat "no island here" as their answer.
		/// </summary>
		public static bool TryGetIslandAt(Vector3 position, float horizontalTolerance, float heightTolerance, out int islandId)
		{
			islandId = -1;

			if (EnsureBuilt() == false)
				return false;

			float hTol = Mathf.Max(0.01f, horizontalTolerance);
			float yTol = float.IsPositiveInfinity(heightTolerance) ? float.MaxValue : Mathf.Max(0.01f, heightTolerance);

			int minCellX = Mathf.FloorToInt((position.x - hTol) / GridCellSize);
			int maxCellX = Mathf.FloorToInt((position.x + hTol) / GridCellSize);
			int minCellZ = Mathf.FloorToInt((position.z - hTol) / GridCellSize);
			int maxCellZ = Mathf.FloorToInt((position.z + hTol) / GridCellSize);

			_queryStamp++;
			float bestScore = float.PositiveInfinity;

			for (int cx = minCellX; cx <= maxCellX; cx++)
			{
				for (int cz = minCellZ; cz <= maxCellZ; cz++)
				{
					if (_grid.TryGetValue(PackCell(cx, cz), out List<int> bucket) == false)
						continue;

					for (int b = 0; b < bucket.Count; b++)
					{
						int triangle = bucket[b];
						if (_triangleQueryStamp[triangle] == _queryStamp)
							continue;
						_triangleQueryStamp[triangle] = _queryStamp;

						if (TryScoreTriangle(triangle, position, hTol, yTol, out float score) == false)
							continue;

						if (score < bestScore)
						{
							bestScore = score;
							islandId = _triangleIsland[triangle];
						}
					}
				}
			}

			return islandId >= 0;
		}

		/// <summary>XZ-projected surface area of an island in m². Returns 0 for unknown ids.</summary>
		public static float GetIslandArea(int islandId)
		{
			if (_built == false || _islandAreas == null || islandId < 0 || islandId >= _islandAreas.Length)
				return 0f;

			return _islandAreas[islandId];
		}

		/// <summary>
		/// True when this island's separateness was proven by the build's path-verified merge pass (a real
		/// NavMesh.CalculatePath to the main component failed, or it IS the main component). Between two
		/// verified islands, inequality is a trustworthy "no path exists" even for large islands (rooftops);
		/// unverified islands (no main-component triangle within the merge search radius) should fall back to
		/// real path queries for large-vs-large decisions.
		/// </summary>
		public static bool IsIslandVerified(int islandId)
		{
			if (_built == false || _islandVerified == null || islandId < 0 || islandId >= _islandVerified.Length)
				return false;

			return _islandVerified[islandId];
		}

		// Distance-based score of a triangle against the query position, or false when outside tolerance.
		// Score is squared XZ distance plus squared height delta so the nearest surface wins, matching what
		// NavMesh.SamplePosition would have picked.
		private static bool TryScoreTriangle(int triangle, Vector3 position, float hTol, float yTol, out float score)
		{
			score = float.PositiveInfinity;

			Vector3 a = _vertices[_indices[triangle * 3 + 0]];
			Vector3 b = _vertices[_indices[triangle * 3 + 1]];
			Vector3 c = _vertices[_indices[triangle * 3 + 2]];

			Vector2 p = new Vector2(position.x, position.z);
			Vector2 a2 = new Vector2(a.x, a.z);
			Vector2 b2 = new Vector2(b.x, b.z);
			Vector2 c2 = new Vector2(c.x, c.z);

			Vector2 closest = ClosestPointOnTriangle2D(p, a2, b2, c2, out float u, out float v, out float w);
			float xzDistSqr = (closest - p).sqrMagnitude;
			if (xzDistSqr > hTol * hTol)
				return false;

			float surfaceY = u * a.y + v * b.y + w * c.y;
			float heightDelta = Mathf.Abs(surfaceY - position.y);
			if (heightDelta > yTol)
				return false;

			score = xzDistSqr + heightDelta * heightDelta;
			return true;
		}

		// Closest point on a 2D triangle with the barycentric coordinates of that point (used to interpolate the
		// triangle's surface height there). Standard region-based closest-point; degenerate (near-zero XZ area)
		// triangles — vertical NavMesh slivers — resolve to their nearest vertex.
		private static Vector2 ClosestPointOnTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
			out float u, out float v, out float w)
		{
			Vector2 ab = b - a;
			Vector2 ac = c - a;
			Vector2 ap = p - a;

			float d1 = Vector2.Dot(ab, ap);
			float d2 = Vector2.Dot(ac, ap);
			if (d1 <= 0f && d2 <= 0f) { u = 1f; v = 0f; w = 0f; return a; }

			Vector2 bp = p - b;
			float d3 = Vector2.Dot(ab, bp);
			float d4 = Vector2.Dot(ac, bp);
			if (d3 >= 0f && d4 <= d3) { u = 0f; v = 1f; w = 0f; return b; }

			float vc = d1 * d4 - d3 * d2;
			if (vc <= 0f && d1 >= 0f && d3 <= 0f)
			{
				float t = d1 / Mathf.Max(1e-8f, d1 - d3);
				u = 1f - t; v = t; w = 0f;
				return a + t * ab;
			}

			Vector2 cp = p - c;
			float d5 = Vector2.Dot(ab, cp);
			float d6 = Vector2.Dot(ac, cp);
			if (d6 >= 0f && d5 <= d6) { u = 0f; v = 0f; w = 1f; return c; }

			float vb = d5 * d2 - d1 * d6;
			if (vb <= 0f && d2 >= 0f && d6 <= 0f)
			{
				float t = d2 / Mathf.Max(1e-8f, d2 - d6);
				u = 1f - t; v = 0f; w = t;
				return a + t * ac;
			}

			float va = d3 * d6 - d5 * d4;
			if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
			{
				float t = (d4 - d3) / Mathf.Max(1e-8f, (d4 - d3) + (d5 - d6));
				u = 0f; v = 1f - t; w = t;
				return b + t * (c - b);
			}

			float denom = 1f / Mathf.Max(1e-8f, va + vb + vc);
			v = vb * denom;
			w = vc * denom;
			u = 1f - v - w;
			return a + ab * v + ac * w;
		}

		private static bool EnsureBuilt()
		{
			if (_built)
				return _indices != null && _indices.Length > 0;

			Build();
			return _indices != null && _indices.Length > 0;
		}

		private static void Build()
		{
			_built = true;
			_grid.Clear();
			_vertices = null;
			_indices = null;
			_triangleIsland = null;
			_islandAreas = null;
			_islandVerified = null;
			_triangleQueryStamp = null;
			IslandCount = 0;

			NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
			if (triangulation.indices == null || triangulation.indices.Length < 3)
				return;

			_vertices = triangulation.vertices;
			_indices = triangulation.indices;
			int triangleCount = _indices.Length / 3;

			// 1. Weld vertices by quantized position (representative = first raw position seen per welded id).
			// Anisotropic on purpose: XZ is quantized tightly (border vertices agree laterally), but Y uses
			// coarse buckets plus a neighbour-bucket probe with a real distance check, because tile borders can
			// disagree vertically by up to VerticalWeldTolerance while being the same surface.
			var weldMap = new Dictionary<long, int>(_vertices.Length);
			var canonical = new int[_vertices.Length];
			var representative = new Vector3[_vertices.Length];
			int canonicalCount = 0;
			for (int i = 0; i < _vertices.Length; i++)
			{
				Vector3 v = _vertices[i];
				int qx = Mathf.RoundToInt(v.x / WeldEpsilon);
				int qz = Mathf.RoundToInt(v.z / WeldEpsilon);
				int qy = Mathf.RoundToInt(v.y / VerticalWeldTolerance);

				int id = -1;
				for (int dy = -1; dy <= 1; dy++)
				{
					if (weldMap.TryGetValue(PackWeldKey(qx, qy + dy, qz), out int candidate) &&
					    Mathf.Abs(representative[candidate].y - v.y) <= VerticalWeldTolerance)
					{
						id = candidate;
						break;
					}
				}

				if (id < 0)
				{
					id = canonicalCount++;
					weldMap[PackWeldKey(qx, qy, qz)] = id;
					representative[id] = v;
				}

				canonical[i] = id;
			}

			// 2. Union-find over welded vertices; every triangle unions its three corners, so triangles sharing
			// any welded vertex end up in one island.
			var parent = new int[canonicalCount];
			for (int i = 0; i < canonicalCount; i++)
				parent[i] = i;

			for (int t = 0; t < triangleCount; t++)
			{
				int v0 = canonical[_indices[t * 3 + 0]];
				int v1 = canonical[_indices[t * 3 + 1]];
				int v2 = canonical[_indices[t * 3 + 2]];
				Union(parent, v0, v1);
				Union(parent, v1, v2);
			}

			// 3. Spatial grid: each triangle goes into every cell its XZ AABB overlaps. Built before island
			// compaction because the T-junction pass below uses it to find edge candidates near a vertex.
			for (int t = 0; t < triangleCount; t++)
			{
				Vector3 a = _vertices[_indices[t * 3 + 0]];
				Vector3 b = _vertices[_indices[t * 3 + 1]];
				Vector3 c = _vertices[_indices[t * 3 + 2]];

				int minX = Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x) / GridCellSize);
				int maxX = Mathf.FloorToInt(Mathf.Max(a.x, b.x, c.x) / GridCellSize);
				int minZ = Mathf.FloorToInt(Mathf.Min(a.z, b.z, c.z) / GridCellSize);
				int maxZ = Mathf.FloorToInt(Mathf.Max(a.z, b.z, c.z) / GridCellSize);

				for (int cx = minX; cx <= maxX; cx++)
				{
					for (int cz = minZ; cz <= maxZ; cz++)
					{
						long key = PackCell(cx, cz);
						if (_grid.TryGetValue(key, out List<int> bucket) == false)
						{
							bucket = new List<int>(8);
							_grid.Add(key, bucket);
						}
						bucket.Add(t);
					}
				}
			}

			// 4. T-junction merge. Unity's tiled triangulation can split an edge on one side of a tile border
			// while the other side keeps it whole: the extra border vertex then has no welded counterpart — it
			// lies in the MIDDLE of the neighbouring triangle's edge — and vertex welding alone leaves the two
			// sides as separate islands. That fragments the street mesh and poisons every island-based
			// connectivity answer (zombies get false "tiny island / no path" verdicts and stall). Union every
			// welded vertex with any triangle whose edge it lies on.
			for (int c = 0; c < canonicalCount; c++)
			{
				Vector3 point = representative[c];
				if (_grid.TryGetValue(CellKey(point), out List<int> bucket) == false)
					continue;

				for (int b = 0; b < bucket.Count; b++)
				{
					int t = bucket[b];
					int c0 = canonical[_indices[t * 3 + 0]];
					int c1 = canonical[_indices[t * 3 + 1]];
					int c2 = canonical[_indices[t * 3 + 2]];
					if (c0 == c || c1 == c || c2 == c)
						continue; // the vertex is one of this triangle's own corners — already unioned by welding
					if (Find(parent, c0) == Find(parent, c))
						continue; // already the same island

					Vector3 a = _vertices[_indices[t * 3 + 0]];
					Vector3 bV = _vertices[_indices[t * 3 + 1]];
					Vector3 cV = _vertices[_indices[t * 3 + 2]];
					if (IsPointNearSegment(point, a, bV) || IsPointNearSegment(point, bV, cV) || IsPointNearSegment(point, cV, a))
						Union(parent, c, c0);
				}
			}

			// 5. Path-verified merge — the authoritative connectivity source. Geometry alone cannot always prove
			// two bake tiles connected (Recast links tile borders in the navmesh *graph* with a vertical
			// tolerance the polygon vertices do not respect), so for every island still separate from the
			// largest one, ask the engine directly: one NavMesh.CalculatePath from the island's representative
			// to the nearest triangle of the merged main component. A complete path proves they are one island;
			// a failed one is definitive disconnection (the merged component is connected, so failing to reach
			// one of its points means failing to reach all of them). The geometric welds above only exist to
			// keep the number of path probes small. Islands that were path-tested (or merged) are "verified":
			// between verified islands, inequality is trusted as "no path" even for large ones — which is the
			// common street-vs-rooftop case on this map, where inaccessible building roofs legitimately carry
			// their own large NavMesh islands.
			int pathMergedCount = MergePathConnectedIslands(parent, canonical, triangleCount,
				out HashSet<int> verifiedRoots, out int unverifiedRootCount);

			// 6. Compact roots into island ids, assign per-triangle ids, accumulate XZ areas.
			var rootToIsland = new Dictionary<int, int>();
			_triangleIsland = new int[triangleCount];
			var areas = new List<float>();
			var verified = new List<bool>();

			for (int t = 0; t < triangleCount; t++)
			{
				int root = Find(parent, canonical[_indices[t * 3]]);
				if (rootToIsland.TryGetValue(root, out int island) == false)
				{
					island = areas.Count;
					rootToIsland.Add(root, island);
					areas.Add(0f);
					verified.Add(verifiedRoots.Contains(root));
				}

				_triangleIsland[t] = island;

				Vector3 a = _vertices[_indices[t * 3 + 0]];
				Vector3 b = _vertices[_indices[t * 3 + 1]];
				Vector3 c = _vertices[_indices[t * 3 + 2]];
				areas[island] += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
			}

			_islandAreas = areas.ToArray();
			_islandVerified = verified.ToArray();
			IslandCount = areas.Count;

			_triangleQueryStamp = new int[triangleCount];
			_queryStamp = 0;

			// Health summary. Multiple large islands are EXPECTED on this map (inaccessible building rooftops
			// carry NavMesh); what matters is that they are path-verified as genuinely disconnected. Unverified
			// islands are the only ones whose large-vs-large negatives degrade to real path queries.
			int largeIslands = 0;
			float largestArea = 0f;
			for (int i = 0; i < _islandAreas.Length; i++)
			{
				if (_islandAreas[i] > SmallIslandMaxArea)
					largeIslands++;
				largestArea = Mathf.Max(largestArea, _islandAreas[i]);
			}

			Debug.Log($"{nameof(NavMeshIslandMap)}: built {IslandCount} island(s) from {triangleCount} NavMesh triangles — " +
			          $"main island {largestArea:0} m², {Mathf.Max(0, largeIslands - 1)} other large (> {SmallIslandMaxArea:0} m², rooftops/plateaus), " +
			          $"path-merge united {pathMergedCount} fragment(s). " +
			          (unverifiedRootCount == 0
				          ? "All separate islands are path-verified as genuinely disconnected."
				          : $"{unverifiedRootCount} island(s) unverified (no main-component triangle within the merge search radius); their large-vs-large negatives fall back to real path queries."));
		}

		// Unions every island that has a complete NavMesh path to the largest island's component. Returns how
		// many islands were united this way, plus the set of final roots whose connectivity status is now
		// path-proven ("verified": the main component itself and every island whose disconnection a failed
		// CalculatePath demonstrated) and how many roots could not be probed at all. Cost is one CalculatePath
		// per non-main island per pass (a handful of passes at most — chains of fragments merge over successive
		// passes as the main component grows); floods for genuinely disconnected islands are bounded by their
		// own tiny polygon count because the query starts on the small island's side.
		private static int MergePathConnectedIslands(int[] parent, int[] canonical, int triangleCount,
			out HashSet<int> verifiedRoots, out int unverifiedRootCount)
		{
			verifiedRoots = new HashSet<int>();
			unverifiedRootCount = 0;

			// Per-root bookkeeping: total area and the largest triangle as the path representative.
			var rootArea = new Dictionary<int, float>();
			var rootRepresentativeTriangle = new Dictionary<int, int>();
			var rootRepresentativeArea = new Dictionary<int, float>();

			for (int t = 0; t < triangleCount; t++)
			{
				int root = Find(parent, canonical[_indices[t * 3]]);
				float area = TriangleAreaXZ(t);

				rootArea.TryGetValue(root, out float total);
				rootArea[root] = total + area;

				if (rootRepresentativeArea.TryGetValue(root, out float repArea) == false || area > repArea)
				{
					rootRepresentativeArea[root] = area;
					rootRepresentativeTriangle[root] = t;
				}
			}

			int mainRoot = -1;
			float mainArea = -1f;
			foreach (var pair in rootArea)
			{
				if (pair.Value > mainArea)
				{
					mainArea = pair.Value;
					mainRoot = pair.Key;
				}
			}

			if (mainRoot >= 0)
				verifiedRoots.Add(mainRoot);

			if (rootArea.Count <= 1)
				return 0;

			var roots = new List<int>(rootArea.Keys);
			var separated = new HashSet<int>();
			var path = new UnityEngine.AI.NavMeshPath();
			int mergedCount = 0;

			bool merged = true;
			for (int pass = 0; merged && pass < 8; pass++)
			{
				merged = false;
				for (int i = 0; i < roots.Count; i++)
				{
					int root = roots[i];
					if (root == mainRoot || separated.Contains(root))
						continue;
					if (Find(parent, root) == Find(parent, mainRoot))
						continue;

					Vector3 from = TriangleCentroid(rootRepresentativeTriangle[root]);
					if (TryFindNearestMainComponentCentroid(from, parent, canonical, mainRoot, out Vector3 to) == false)
						continue; // nothing merged nearby yet — a later pass may reach it as the component grows

					if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) &&
					    path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
					{
						Union(parent, mainRoot, root);
						mergedCount++;
						merged = true;
					}
					else
					{
						separated.Add(root);
					}
				}
			}

			// Verified = the main component (merged islands share its root now) + every path-tested separate
			// island. Roots that never got probed (no main-component triangle within the search radius) stay
			// unverified and are counted for the health log.
			foreach (int root in separated)
				verifiedRoots.Add(Find(parent, root));

			int mainFind = Find(parent, mainRoot);
			verifiedRoots.Add(mainFind);

			for (int i = 0; i < roots.Count; i++)
			{
				int find = Find(parent, roots[i]);
				if (find != mainFind && verifiedRoots.Contains(find) == false)
					unverifiedRootCount++;
			}

			return mergedCount;
		}

		private static bool TryFindNearestMainComponentCentroid(Vector3 from, int[] parent, int[] canonical,
			int mainRoot, out Vector3 centroid)
		{
			centroid = default;
			int mainFind = Find(parent, mainRoot);
			int cellX = Mathf.FloorToInt(from.x / GridCellSize);
			int cellZ = Mathf.FloorToInt(from.z / GridCellSize);
			float bestDistSqr = float.PositiveInfinity;

			for (int cx = cellX - PathMergeSearchCells; cx <= cellX + PathMergeSearchCells; cx++)
			{
				for (int cz = cellZ - PathMergeSearchCells; cz <= cellZ + PathMergeSearchCells; cz++)
				{
					if (_grid.TryGetValue(PackCell(cx, cz), out List<int> bucket) == false)
						continue;

					for (int b = 0; b < bucket.Count; b++)
					{
						int t = bucket[b];
						if (Find(parent, canonical[_indices[t * 3]]) != mainFind)
							continue;

						Vector3 candidate = TriangleCentroid(t);
						float distSqr = (candidate - from).sqrMagnitude;
						if (distSqr < bestDistSqr)
						{
							bestDistSqr = distSqr;
							centroid = candidate;
						}
					}
				}
			}

			return float.IsPositiveInfinity(bestDistSqr) == false;
		}

		private static Vector3 TriangleCentroid(int triangle)
		{
			return (_vertices[_indices[triangle * 3 + 0]] +
			        _vertices[_indices[triangle * 3 + 1]] +
			        _vertices[_indices[triangle * 3 + 2]]) / 3f;
		}

		private static float TriangleAreaXZ(int triangle)
		{
			Vector3 a = _vertices[_indices[triangle * 3 + 0]];
			Vector3 b = _vertices[_indices[triangle * 3 + 1]];
			Vector3 c = _vertices[_indices[triangle * 3 + 2]];
			return Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
		}

		// Point-to-segment test used by the T-junction merge: tight in XZ, tolerant in Y (compared against the
		// segment's interpolated height), mirroring how tiled bakes disagree across borders.
		private static bool IsPointNearSegment(Vector3 point, Vector3 a, Vector3 b)
		{
			Vector2 p = new Vector2(point.x, point.z);
			Vector2 a2 = new Vector2(a.x, a.z);
			Vector2 ab2 = new Vector2(b.x - a.x, b.z - a.z);

			float lengthSqr = ab2.sqrMagnitude;
			if (lengthSqr < 1e-8f)
				return false;

			float t = Mathf.Clamp01(Vector2.Dot(p - a2, ab2) / lengthSqr);
			Vector2 closest = a2 + ab2 * t;
			if ((p - closest).sqrMagnitude > TJunctionEpsilon * TJunctionEpsilon)
				return false;

			float segmentY = Mathf.Lerp(a.y, b.y, t);
			return Mathf.Abs(point.y - segmentY) <= VerticalWeldTolerance;
		}

		private static long PackWeldKey(int qx, int qy, int qz)
		{
			long x = (long)qx & 0x1FFFFF;
			long y = (long)qy & 0x1FFFFF;
			long z = (long)qz & 0x1FFFFF;
			return (x << 42) | (y << 21) | z;
		}

		private static long PackCell(int cellX, int cellZ)
		{
			return ((long)cellX << 32) ^ (uint)cellZ;
		}

		private static long CellKey(Vector3 position)
		{
			return PackCell(Mathf.FloorToInt(position.x / GridCellSize), Mathf.FloorToInt(position.z / GridCellSize));
		}

		private static int Find(int[] parent, int i)
		{
			while (parent[i] != i)
			{
				parent[i] = parent[parent[i]]; // path halving
				i = parent[i];
			}

			return i;
		}

		private static void Union(int[] parent, int a, int b)
		{
			int rootA = Find(parent, a);
			int rootB = Find(parent, b);
			if (rootA != rootB)
				parent[rootB] = rootA;
		}
	}
}
