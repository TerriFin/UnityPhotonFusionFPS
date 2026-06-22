using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	public class HeightMapGenerator : MonoBehaviour
	{
		private static readonly Vector2Int[] DirectionOffsets =
		{
			new Vector2Int(0, 1),
			new Vector2Int(1, 0),
			new Vector2Int(0, -1),
			new Vector2Int(-1, 0),
		};

		private static readonly Vector2Int[] NeighborOffsets =
		{
			new Vector2Int(0, 1),
			new Vector2Int(1, 1),
			new Vector2Int(1, 0),
			new Vector2Int(1, -1),
			new Vector2Int(0, -1),
			new Vector2Int(-1, -1),
			new Vector2Int(-1, 0),
			new Vector2Int(-1, 1),
		};

		private static readonly Vector2Int[] DiagonalOffsets =
		{
			new Vector2Int(1, 1),
			new Vector2Int(1, -1),
			new Vector2Int(-1, -1),
			new Vector2Int(-1, 1),
		};

		[Header("Setup")]
		public HeightGenerationSettings Settings;
		[Min(3)]
		public int Width = 12;
		[Min(3)]
		public int Height = 12;
		public int Seed = 12345;
		public bool RandomizeSeedOnGenerate;
		public float TileSize = 20f;
		public float HeightLevelWorldUnits = 4f;
		public string GeneratedRootName = "Generated Height Map";
		public bool GenerateOnStart;
		public bool ClearBeforeGenerate = true;

		[Header("Debug")]
		public bool DrawGizmos = true;
		public Color FlatHeightGizmoColor = new Color(0.35f, 0.35f, 0.35f, 0.25f);
		public Color LedgeGizmoColor = new Color(0.9f, 0.55f, 0.1f, 0.55f);

		private WorldHeightCell[,] _lastCells;
		private Transform _generatedRoot;
		private readonly Dictionary<Vector2Int, GameObject> _ledgeInstances = new();
		private Coroutine _generationCompleteCoroutine;
		private int _lastAppliedNetworkedSeed;

		public bool IsGenerationComplete { get; private set; }
		public int EffectiveWidth => Mathf.Max(3, Width);
		public int EffectiveHeight => Mathf.Max(3, Height);
		public float EffectiveTileSize => Mathf.Max(0.01f, TileSize);
		public float EffectiveHeightLevelWorldUnits => Mathf.Max(0f, HeightLevelWorldUnits);
		public Transform GeneratedRoot => _generatedRoot;

		private IEnumerator Start()
		{
			if (GenerateOnStart)
			{
				if (Application.isPlaying)
					yield return WaitForNetworkedWorldSeed();

				Generate();
			}
		}

		private void Update()
		{
			if (Application.isPlaying == false)
				return;

			Gameplay gameplay = FindObjectOfType<Gameplay>();
			if (gameplay == null ||
			    gameplay.Object == null ||
			    gameplay.Object.IsValid == false ||
			    gameplay.HasStateAuthority ||
			    gameplay.WorldSeed == 0)
			{
				return;
			}

			if (_lastAppliedNetworkedSeed == gameplay.WorldSeed && Seed == gameplay.WorldSeed)
				return;

			Generate();
			_lastAppliedNetworkedSeed = Seed;
		}

		[ContextMenu("Generate Height Map")]
		public void Generate()
		{
			IsGenerationComplete = false;

			if (Settings == null)
			{
				Debug.LogWarning($"{nameof(HeightMapGenerator)} on {name} has no settings asset.", this);
				return;
			}

			if (ClearBeforeGenerate)
				ClearGenerated();

			if (Application.isPlaying && TryGetNetworkedWorldSeed(out int worldSeed))
			{
				Seed = worldSeed;
				Debug.Log($"{nameof(HeightMapGenerator)} generated with networked world seed {Seed}.", this);
			}
			else if (RandomizeSeedOnGenerate)
			{
				Seed = GetRandomizedSeed();
				if (Application.isPlaying)
					Debug.Log($"{nameof(HeightMapGenerator)} generated with seed {Seed}.", this);
			}

			GenerateCells(out WorldHeightCell[,] cells);
			_lastCells = cells;
			InstantiateLedges(cells, new System.Random(Seed ^ 0x243F6A88));
			ScheduleGenerationComplete();

			if (Application.isPlaying)
				_lastAppliedNetworkedSeed = Seed;
		}

		[ContextMenu("Clear Generated Height Map")]
		public void ClearGenerated()
		{
			Transform existing = transform.Find(GeneratedRootName);
			if (existing == null && _generatedRoot != null)
				existing = _generatedRoot;

			if (existing != null)
			{
				if (Application.isPlaying)
					Destroy(existing.gameObject);
				else
					DestroyImmediate(existing.gameObject);
			}

			_generatedRoot = null;
			_ledgeInstances.Clear();
			IsGenerationComplete = false;
		}

		public bool TryGetHeightSnapshot(out WorldHeightSnapshot snapshot)
		{
			if (_lastCells == null)
			{
				snapshot = default;
				return false;
			}

			snapshot = new WorldHeightSnapshot(_lastCells, Seed, EffectiveTileSize, EffectiveHeightLevelWorldUnits, transform.position);
			return true;
		}

		public void SuppressLedgeAt(Vector2Int position)
		{
			if (_ledgeInstances.TryGetValue(position, out GameObject instance) == false || instance == null)
				return;

			if (Application.isPlaying)
				Destroy(instance);
			else
				DestroyImmediate(instance);

			_ledgeInstances.Remove(position);
		}

		public bool TryReplaceTraversalLedgeWithNonTraversalTile(Vector2Int position)
		{
			if (_lastCells == null ||
			    position.x < 0 ||
			    position.y < 0 ||
			    position.x >= _lastCells.GetLength(0) ||
			    position.y >= _lastCells.GetLength(1))
			{
				return false;
			}

			WorldHeightCell cell = _lastCells[position.x, position.y];
			if (cell.IsLedge == false || cell.AllowsTraversalWithoutRoad == false)
				return false;

			HeightTileCandidate replacement = ChooseNonTraversalTile(cell, GetCellRandom(position));
			if (replacement.IsValid == false)
				return false;

			SuppressLedgeAt(position);
			cell = cell.WithAllowsTraversalWithoutRoad(false);
			_lastCells[position.x, position.y] = cell;
			CreateLedgeInstance(cell, replacement, _generatedRoot);
			return true;
		}

		private void GenerateCells(out WorldHeightCell[,] cells)
		{
			int width = EffectiveWidth;
			int height = EffectiveHeight;
			int[,] heights = GenerateHeightLevels(width, height);
			cells = BuildHeightCells(heights);
			Debug.Log($"{nameof(HeightMapGenerator)} generated {width}x{height} height map with {Mathf.Max(1, Settings.HeightLayerCount)} possible height layer(s).", this);
		}

		private int[,] GenerateHeightLevels(int width, int height)
		{
			int[,] heights = new int[width, height];
			int layerCount = Mathf.Max(1, Settings.HeightLayerCount);
			int preferredLedgeCount = Mathf.Max(0, Settings.PreferredLedgeCount);
			if (layerCount <= 1 || preferredLedgeCount <= 0)
				return heights;

			var random = new System.Random(Seed);
			TryGeneratePathFirstHeightLevels(width, height, layerCount, preferredLedgeCount, random, out int[,] pathFirstHeights);
			return pathFirstHeights;
		}

		private bool TryGeneratePathFirstHeightLevels(int width, int height, int layerCount, int preferredLedgeCount, System.Random random, out int[,] heights)
		{
			heights = new int[width, height];
			var acceptedLedgePaths = new List<List<Vector2Int>>();
			int attempts = Mathf.Max(1, Settings.MaxGenerationAttempts);

			for (int attempt = 0; attempt < attempts && acceptedLedgePaths.Count < preferredLedgeCount; attempt++)
			{
				TryAddHeightLayerByLedgePath(heights, layerCount, acceptedLedgePaths, random);
			}

			return acceptedLedgePaths.Count > 0;
		}

		private bool TryAddHeightLayerByLedgePath(
			int[,] heights,
			int layerCount,
			List<List<Vector2Int>> acceptedLedgePaths,
			System.Random random)
		{
			List<HeightRegion> regions = CollectHeightRegionsBelowMaxHeight(heights, layerCount - 1);
			if (regions.Count == 0)
				return false;

			regions.Sort((a, b) => b.Cells.Count.CompareTo(a.Cells.Count));
			int regionIndex = random.Next(Mathf.Min(regions.Count, 4));
			HeightRegion region = regions[regionIndex];
			int newHeight = region.HeightLevel + 1;

			if (TryFindOrganicLedgePath(heights, region, acceptedLedgePaths, random, out List<Vector2Int> ledgePath) == false)
				return false;

			if (TryApplyLedgePathSplit(heights, region, ledgePath, newHeight, out int[,] candidate) == false)
				return false;

			if (HasOnlyUsableHeightRegions(candidate) == false)
				return false;

			if (Settings.MinRoadReplaceableLedgesPerHeightRegion > 0
				&& HasEnoughRoadReplaceableLedgesInPath(BuildHeightCells(candidate, false), ledgePath) == false)
				return false;

			CopyHeights(candidate, heights);
			acceptedLedgePaths.Add(ledgePath);
			return true;
		}

		// Counts road-replaceable straight cardinal-step cells that exist along a single new ledge path.
		// Diagonal-step cells become inner/outer corner ledges (not road-replaceable), so this naturally
		// requires each division to have enough cardinal-step ledge segments to host height-change roads.
		private bool HasEnoughRoadReplaceableLedgesInPath(WorldHeightCell[,] cells, List<Vector2Int> ledgePath)
		{
			int required = Mathf.Max(0, Settings.MinRoadReplaceableLedgesPerHeightRegion);
			if (required <= 0)
				return true;

			int count = 0;
			int width = cells.GetLength(0);
			int height = cells.GetLength(1);
			for (int i = 0; i < ledgePath.Count; i++)
			{
				Vector2Int position = ledgePath[i];
				if (position.x < 0 || position.y < 0 || position.x >= width || position.y >= height)
					continue;

				if (cells[position.x, position.y].CanBeReplacedByHeightChangeRoad)
					count++;
			}

			return count >= required;
		}

		private List<HeightRegion> CollectHeightRegionsBelowMaxHeight(int[,] heights, int maxHeightLevel)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			bool[,] visited = new bool[width, height];
			var regions = new List<HeightRegion>();

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (visited[x, y] || heights[x, y] >= maxHeightLevel)
						continue;

					List<Vector2Int> cells = CollectHeightRegion(heights, new Vector2Int(x, y), visited, out Vector2Int min, out Vector2Int max);
					var bounds = new RectInt(min.x, min.y, max.x - min.x + 1, max.y - min.y + 1);
					var region = new HeightRegion(cells, bounds, heights[x, y]);
					if (CanSplitHeightRegion(region))
						regions.Add(region);
				}
			}

			return regions;
		}

		private bool CanSplitHeightRegion(HeightRegion region)
		{
			int minimumArea = Mathf.Max(1, Settings.MinUsableRegionArea);
			int minimumWidth = Mathf.Max(1, Settings.MinUsableRegionWidth);
			int minimumHeight = Mathf.Max(1, Settings.MinUsableRegionHeight);
			if (region.Cells.Count < minimumArea * 2)
				return false;

			return region.Bounds.width >= minimumWidth + 2 || region.Bounds.height >= minimumHeight + 2;
		}

		private bool TryFindOrganicLedgePath(
			int[,] heights,
			HeightRegion region,
			List<List<Vector2Int>> acceptedLedgePaths,
			System.Random random,
			out List<Vector2Int> path)
		{
			path = null;

			var regionCells = new HashSet<Vector2Int>(region.Cells);
			List<BoundarySidePair> sidePairs = BuildBoundarySidePairs(random);
			BoundarySidePair sidePair = sidePairs[0];
			List<Vector2Int> starts = GetPathBoundaryCells(region, regionCells, sidePair.StartSide, acceptedLedgePaths);
			List<Vector2Int> ends = GetPathBoundaryCells(region, regionCells, sidePair.EndSide, acceptedLedgePaths);
			if (starts.Count == 0 || ends.Count == 0)
				return false;

			Vector2Int start = starts[random.Next(starts.Count)];
			var targetCells = BuildTargetBoundaryCells(ends, start, sidePair.StartSide == sidePair.EndSide);
			if (targetCells.Count == 0)
				return false;

			var allowedBoundaryCells = new HashSet<Vector2Int>(starts);
			allowedBoundaryCells.UnionWith(ends);
			int noiseSalt = random.Next();
			return TryFindPathAcrossRegion(region, regionCells, start, targetCells, allowedBoundaryCells, acceptedLedgePaths, noiseSalt, out path)
				&& IsRandomEnoughLedgePath(path, region);
		}

		private List<BoundarySidePair> BuildBoundarySidePairs(System.Random random)
		{
			var sides = new[]
			{
				BoundarySide.North,
				BoundarySide.East,
				BoundarySide.South,
				BoundarySide.West,
			};
			var pairs = new List<BoundarySidePair>(sides.Length * sides.Length);

			for (int start = 0; start < sides.Length; start++)
			{
				for (int end = 0; end < sides.Length; end++)
					pairs.Add(new BoundarySidePair(sides[start], sides[end]));
			}

			for (int i = 0; i < pairs.Count; i++)
			{
				int swapIndex = random.Next(i, pairs.Count);
				(pairs[i], pairs[swapIndex]) = (pairs[swapIndex], pairs[i]);
			}

			return pairs;
		}

		private List<Vector2Int> GetPathBoundaryCells(
			HeightRegion region,
			HashSet<Vector2Int> regionCells,
			BoundarySide side,
			List<List<Vector2Int>> acceptedLedgePaths)
		{
			var cells = new List<Vector2Int>();
			int mapEdgeEndpointClearance = Mathf.Max(3, Mathf.Max(0, Settings.MinCellsBetweenHeightChanges) + 1);

			foreach (Vector2Int cell in region.Cells)
			{
				if (IsOnBoundarySide(cell, region, side) == false)
					continue;
				if (IsValidLedgePathCell(cell, region, regionCells, null, acceptedLedgePaths) == false)
					continue;

				// Defensive: a candidate that would become a map-edge endpoint of the new path must
				// not be the same cell as — nor visually crowd — any previously accepted path's
				// map-edge endpoint. MinCellsBetweenHeightChanges already keeps the new path's whole
				// length away from old path cells, but we apply an extra clearance specifically
				// against old endpoints so two ledges never appear to share the same edge tile.
				if (IsEdgeCell(EffectiveWidth, EffectiveHeight, cell)
					&& IsNearAcceptedMapEdgeEndpoint(cell, acceptedLedgePaths, mapEdgeEndpointClearance))
					continue;

				cells.Add(cell);
			}

			RemoveShortBoundaryRuns(cells, side);
			return cells;
		}

		private bool IsNearAcceptedMapEdgeEndpoint(
			Vector2Int cell,
			List<List<Vector2Int>> acceptedLedgePaths,
			int clearance)
		{
			int width = EffectiveWidth;
			int height = EffectiveHeight;
			for (int i = 0; i < acceptedLedgePaths.Count; i++)
			{
				List<Vector2Int> path = acceptedLedgePaths[i];
				if (path.Count == 0)
					continue;

				Vector2Int first = path[0];
				Vector2Int last = path[path.Count - 1];
				if (IsEdgeCell(width, height, first) && ChebyshevDistance(cell, first) < clearance)
					return true;
				if (IsEdgeCell(width, height, last) && ChebyshevDistance(cell, last) < clearance)
					return true;
			}

			return false;
		}

		private int ChebyshevDistance(Vector2Int a, Vector2Int b)
		{
			return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
		}

		private void RemoveShortBoundaryRuns(List<Vector2Int> cells, BoundarySide side)
		{
			const int minimumRunLength = 2;
			for (int i = cells.Count - 1; i >= 0; i--)
			{
				int runLength = CountBoundaryRunLength(cells, cells[i], side);
				if (runLength < minimumRunLength)
					cells.RemoveAt(i);
			}
		}

		private int CountBoundaryRunLength(List<Vector2Int> cells, Vector2Int origin, BoundarySide side)
		{
			int runLength = 0;
			for (int i = 0; i < cells.Count; i++)
			{
				Vector2Int cell = cells[i];
				bool sameRun = side == BoundarySide.North || side == BoundarySide.South
					? cell.y == origin.y && Mathf.Abs(cell.x - origin.x) <= 1
					: cell.x == origin.x && Mathf.Abs(cell.y - origin.y) <= 1;

				if (sameRun)
					runLength++;
			}

			return runLength;
		}

		private HashSet<Vector2Int> BuildTargetBoundaryCells(List<Vector2Int> ends, Vector2Int start, bool sameSide)
		{
			var targets = new HashSet<Vector2Int>();
			int minimumSameSideDistance = Mathf.Max(3, Settings.MinUsableRegionWidth, Settings.MinUsableRegionHeight);

			for (int i = 0; i < ends.Count; i++)
			{
				Vector2Int end = ends[i];
				int distance = Mathf.Abs(end.x - start.x) + Mathf.Abs(end.y - start.y);
				if (sameSide && distance < minimumSameSideDistance)
					continue;

				targets.Add(end);
			}

			return targets;
		}

		private bool TryFindPathAcrossRegion(
			HeightRegion region,
			HashSet<Vector2Int> regionCells,
			Vector2Int start,
			HashSet<Vector2Int> targetCells,
			HashSet<Vector2Int> allowedBoundaryCells,
			List<List<Vector2Int>> acceptedLedgePaths,
			int noiseSalt,
			out List<Vector2Int> path)
		{
			path = null;
			var open = new List<HeightPathNode>();
			var closed = new HashSet<Vector2Int>();
			var bestByPosition = new Dictionary<Vector2Int, HeightPathNode>();

			var startNode = new HeightPathNode(start, null, 0f, EstimatePathCost(start, targetCells));
			open.Add(startNode);
			bestByPosition[start] = startNode;
			float randomness = Mathf.Clamp01(Settings.LedgePathRandomness);
			float randomCostScale = Mathf.Lerp(0.1f, 8f, randomness);
			float heuristicWeight = Mathf.Lerp(1.2f, 0.05f, randomness);

			while (open.Count > 0)
			{
				int currentIndex = GetLowestScoreNodeIndex(open);
				HeightPathNode current = open[currentIndex];
				open.RemoveAt(currentIndex);

				if (closed.Add(current.Position) == false)
					continue;

				if (targetCells.Contains(current.Position) && current.Parent != null)
				{
					path = RebuildHeightPath(current);
					return path.Count >= 3;
				}

				for (int i = 0; i < NeighborOffsets.Length; i++)
				{
					Vector2Int next = current.Position + NeighborOffsets[i];
					bool isDiagonal = NeighborOffsets[i].x != 0 && NeighborOffsets[i].y != 0;
					if (closed.Contains(next)
						|| IsParallelBoundaryStep(current.Position, next, region)
						|| IsValidLedgePathCell(next, region, regionCells, allowedBoundaryCells, acceptedLedgePaths) == false)
						continue;

					// Boundary ledge tiles are only authored as Straight. A diagonal step into or out of a
					// map-edge cell would classify that cell as an inner/outer corner, which BuildHeightCells
					// then suppresses entirely — leaving a missing boundary cap. Force cardinal-only steps at
					// the map edge so endpoint cells stay Straight.
					if (isDiagonal
						&& (IsEdgeCell(EffectiveWidth, EffectiveHeight, current.Position)
							|| IsEdgeCell(EffectiveWidth, EffectiveHeight, next)))
						continue;

					// Reject 1-cell-wide notches/bumps. If `next` is cardinally adjacent to any path cell
					// other than the immediate predecessor, the path doubles back through a 1-cell gap.
					// The corner ledge tile set can only render proper U-turns when the parallel runs are
					// at least 2 cells apart, so tighter doublebacks would leave malformed corner pieces.
					if (HasNonPredecessorPathNeighbor(current, next))
						continue;

					// Reject one-cell side nubs such as:
					// A A A
					// . B .
					// where the path steps diagonally out from a straight run and immediately diagonally
					// back to the same run. These classify as valid corner ledges locally, but the authored
					// ledge pieces leave a small hole because the offshoot is only one cell deep.
					if (CreatesSingleCellOffshoot(current, next))
						continue;

					// Diagonal path steps create inner/outer corner ledge cells which the road generator cannot
					// replace with a ramp. Use the geometric step length (≈1.414) so the path doesn't trivially
					// prefer diagonals just because they cover more distance per step.
					float baseStepCost = isDiagonal ? 1.4142f : 1f;
					float stepCost = baseStepCost + Hash01(next.x + noiseSalt, next.y - noiseSalt, Seed ^ noiseSalt) * randomCostScale;
					float newCost = current.Cost + stepCost;
					if (bestByPosition.TryGetValue(next, out HeightPathNode existing) && existing.Cost <= newCost)
						continue;

					float score = newCost + EstimatePathCost(next, targetCells) * heuristicWeight;
					var node = new HeightPathNode(next, current, newCost, score);
					bestByPosition[next] = node;
					open.Add(node);
				}
			}

			return false;
		}

		private bool HasNonPredecessorPathNeighbor(HeightPathNode current, Vector2Int next)
		{
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighbor = next + DirectionOffsets[i];
				if (neighbor == current.Position)
					continue;

				for (HeightPathNode ancestor = current.Parent; ancestor != null; ancestor = ancestor.Parent)
				{
					if (ancestor.Position == neighbor)
						return true;
				}
			}

			return false;
		}

		private bool CreatesSingleCellOffshoot(HeightPathNode current, Vector2Int next)
		{
			if (current.Parent == null)
				return false;

			Vector2Int previousStep = current.Position - current.Parent.Position;
			Vector2Int nextStep = next - current.Position;
			bool previousIsDiagonal = previousStep.x != 0 && previousStep.y != 0;
			bool nextIsDiagonal = nextStep.x != 0 && nextStep.y != 0;
			if (previousIsDiagonal == false || nextIsDiagonal == false)
				return false;

			bool sameHorizontalRun = previousStep.x == nextStep.x && previousStep.y == -nextStep.y;
			bool sameVerticalRun = previousStep.y == nextStep.y && previousStep.x == -nextStep.x;
			return sameHorizontalRun || sameVerticalRun;
		}

		private bool IsRandomEnoughLedgePath(List<Vector2Int> path, HeightRegion region)
		{
			float randomness = Mathf.Clamp01(Settings.LedgePathRandomness);
			if (randomness <= 0.05f)
				return true;

			bool mostlyVertical = Mathf.Abs(path[path.Count - 1].y - path[0].y) >= Mathf.Abs(path[path.Count - 1].x - path[0].x);
			int perpendicularSpan = mostlyVertical ? region.Bounds.width : region.Bounds.height;
			int requiredDeviation = Mathf.RoundToInt(Mathf.Lerp(0f, Mathf.Max(1, perpendicularSpan) * 0.25f, randomness));
			int requiredTurns = Mathf.RoundToInt(Mathf.Lerp(0f, 4f, randomness));

			int minAxis = int.MaxValue;
			int maxAxis = int.MinValue;
			int turns = 0;
			Vector2Int previousDirection = Vector2Int.zero;

			for (int i = 0; i < path.Count; i++)
			{
				int axis = mostlyVertical ? path[i].x : path[i].y;
				minAxis = Mathf.Min(minAxis, axis);
				maxAxis = Mathf.Max(maxAxis, axis);

				if (i == 0)
					continue;

				Vector2Int direction = path[i] - path[i - 1];
				if (previousDirection != Vector2Int.zero && direction != previousDirection)
					turns++;

				previousDirection = direction;
			}

			return maxAxis - minAxis >= requiredDeviation && turns >= requiredTurns;
		}

		private bool IsParallelBoundaryStep(Vector2Int current, Vector2Int next, HeightRegion region)
		{
			bool bothOnBottom = current.y == region.Bounds.yMin && next.y == region.Bounds.yMin;
			bool bothOnTop = current.y == region.Bounds.yMax - 1 && next.y == region.Bounds.yMax - 1;
			bool bothOnLeft = current.x == region.Bounds.xMin && next.x == region.Bounds.xMin;
			bool bothOnRight = current.x == region.Bounds.xMax - 1 && next.x == region.Bounds.xMax - 1;
			return ((bothOnBottom || bothOnTop) && current.x != next.x)
				|| ((bothOnLeft || bothOnRight) && current.y != next.y);
		}

		private bool IsValidLedgePathCell(
			Vector2Int cell,
			HeightRegion region,
			HashSet<Vector2Int> regionCells,
			HashSet<Vector2Int> allowedBoundaryCells,
			List<List<Vector2Int>> acceptedLedgePaths)
		{
			if (regionCells.Contains(cell) == false)
				return false;

			if (allowedBoundaryCells != null && IsRegionBoundaryCell(cell, region) && allowedBoundaryCells.Contains(cell) == false)
				return false;

			return IsFarFromAcceptedLedgePaths(cell, acceptedLedgePaths);
		}

		private bool IsRegionBoundaryCell(Vector2Int cell, HeightRegion region)
		{
			return cell.x == region.Bounds.xMin
				|| cell.x == region.Bounds.xMax - 1
				|| cell.y == region.Bounds.yMin
				|| cell.y == region.Bounds.yMax - 1;
		}

		private bool IsOnBoundarySide(Vector2Int cell, HeightRegion region, BoundarySide side)
		{
			return side switch
			{
				BoundarySide.North => cell.y == region.Bounds.yMax - 1,
				BoundarySide.East => cell.x == region.Bounds.xMax - 1,
				BoundarySide.South => cell.y == region.Bounds.yMin,
				BoundarySide.West => cell.x == region.Bounds.xMin,
				_ => false,
			};
		}

		private bool IsFarFromAcceptedLedgePaths(Vector2Int cell, List<List<Vector2Int>> acceptedLedgePaths)
		{
			int minimumSpacing = Mathf.Max(0, Settings.MinCellsBetweenHeightChanges);
			if (minimumSpacing <= 0)
				return true;

			for (int pathIndex = 0; pathIndex < acceptedLedgePaths.Count; pathIndex++)
			{
				List<Vector2Int> path = acceptedLedgePaths[pathIndex];
				for (int i = 0; i < path.Count; i++)
				{
					Vector2Int other = path[i];
					int distance = Mathf.Max(Mathf.Abs(cell.x - other.x), Mathf.Abs(cell.y - other.y));
					if (distance <= minimumSpacing)
						return false;
				}
			}

			return true;
		}

		private float EstimatePathCost(Vector2Int cell, HashSet<Vector2Int> targetCells)
		{
			int best = int.MaxValue;
			foreach (Vector2Int target in targetCells)
			{
				int distance = Mathf.Abs(cell.x - target.x) + Mathf.Abs(cell.y - target.y);
				if (distance < best)
					best = distance;
			}

			return best == int.MaxValue ? 0f : best;
		}

		private int GetLowestScoreNodeIndex(List<HeightPathNode> nodes)
		{
			int bestIndex = 0;
			float bestScore = nodes[0].Score;
			for (int i = 1; i < nodes.Count; i++)
			{
				if (nodes[i].Score < bestScore)
				{
					bestIndex = i;
					bestScore = nodes[i].Score;
				}
			}

			return bestIndex;
		}

		private List<Vector2Int> RebuildHeightPath(HeightPathNode node)
		{
			var path = new List<Vector2Int>();
			for (HeightPathNode current = node; current != null; current = current.Parent)
				path.Add(current.Position);

			path.Reverse();
			return path;
		}

		private bool TryApplyLedgePathSplit(
			int[,] heights,
			HeightRegion region,
			List<Vector2Int> ledgePath,
			int newHeight,
			out int[,] candidate)
		{
			candidate = CloneHeights(heights);
			var regionCells = new HashSet<Vector2Int>(region.Cells);
			var pathCells = new HashSet<Vector2Int>(ledgePath);
			var raisedCells = ChooseRaisedComponent(region, regionCells, pathCells, heights);
			if (raisedCells == null)
				return false;

			int lowSideCellCount = region.Cells.Count - raisedCells.Count - pathCells.Count;

			if (raisedCells.Count < Mathf.Max(1, Settings.MinUsableRegionArea) || lowSideCellCount < Mathf.Max(1, Settings.MinUsableRegionArea))
				return false;

			foreach (Vector2Int cell in raisedCells)
				candidate[cell.x, cell.y] = newHeight;

			// Safety net: raising must never produce a >1 level difference against any neighbor (cardinal
			// or diagonal). ChooseRaisedComponent already filters out components that border a lower
			// region outside the current region, but if a future change weakens that filter this check
			// still rejects the candidate instead of silently emitting a cliff.
			if (ViolatesHeightAdjacencyRule(candidate, raisedCells))
				return false;

			return true;
		}

		private HashSet<Vector2Int> ChooseRaisedComponent(
			HeightRegion region,
			HashSet<Vector2Int> regionCells,
			HashSet<Vector2Int> pathCells,
			int[,] heights)
		{
			List<HashSet<Vector2Int>> components = CollectSplitComponents(region, regionCells, pathCells);
			if (components.Count < 2)
				return null;

			int minimumArea = Mathf.Max(1, Settings.MinUsableRegionArea);
			HashSet<Vector2Int> best = null;
			int bestSize = int.MaxValue;
			for (int i = 0; i < components.Count; i++)
			{
				HashSet<Vector2Int> component = components[i];
				int remaining = region.Cells.Count - component.Count - pathCells.Count;
				if (component.Count < minimumArea || remaining < minimumArea)
					continue;

				// Skip components whose cells touch a lower region beyond the current region's border.
				// Raising those cells to region.HeightLevel + 1 would put them next to a cell at
				// region.HeightLevel - 1 (or lower) and produce a 2+ level jump — the "way too high"
				// cliff the user reported. Subsequent passes can split the same region with a different
				// path that places the raised side on the safe interior.
				if (IsComponentAdjacentToLowerRegion(component, regionCells, heights, region.HeightLevel))
					continue;

				if (component.Count < bestSize)
				{
					best = component;
					bestSize = component.Count;
				}
			}

			return best;
		}

		private bool IsComponentAdjacentToLowerRegion(
			HashSet<Vector2Int> component,
			HashSet<Vector2Int> regionCells,
			int[,] heights,
			int regionHeightLevel)
		{
			foreach (Vector2Int cell in component)
			{
				for (int i = 0; i < NeighborOffsets.Length; i++)
				{
					Vector2Int neighbor = cell + NeighborOffsets[i];
					if (IsInBounds(heights, neighbor) == false)
						continue;
					if (regionCells.Contains(neighbor))
						continue;
					if (heights[neighbor.x, neighbor.y] < regionHeightLevel)
						return true;
				}
			}

			return false;
		}

		private bool ViolatesHeightAdjacencyRule(int[,] heights, HashSet<Vector2Int> raisedCells)
		{
			foreach (Vector2Int cell in raisedCells)
			{
				int height = heights[cell.x, cell.y];
				for (int i = 0; i < NeighborOffsets.Length; i++)
				{
					Vector2Int neighbor = cell + NeighborOffsets[i];
					if (IsInBounds(heights, neighbor) == false)
						continue;
					if (height - heights[neighbor.x, neighbor.y] > 1)
						return true;
				}
			}

			return false;
		}

		private List<HashSet<Vector2Int>> CollectSplitComponents(
			HeightRegion region,
			HashSet<Vector2Int> regionCells,
			HashSet<Vector2Int> pathCells)
		{
			var components = new List<HashSet<Vector2Int>>();
			var visited = new HashSet<Vector2Int>();

			foreach (Vector2Int cell in region.Cells)
			{
				if (pathCells.Contains(cell) || visited.Contains(cell))
					continue;

				var component = new HashSet<Vector2Int>();
				var open = new Queue<Vector2Int>();
				open.Enqueue(cell);
				visited.Add(cell);

				while (open.Count > 0)
				{
					Vector2Int current = open.Dequeue();
					component.Add(current);
					for (int i = 0; i < DirectionOffsets.Length; i++)
					{
						Vector2Int next = current + DirectionOffsets[i];
						if (regionCells.Contains(next) == false || pathCells.Contains(next) || visited.Add(next) == false)
							continue;

						open.Enqueue(next);
					}
				}

				components.Add(component);
			}

			return components;
		}

		private bool HasOnlyUsableHeightRegions(int[,] heights)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			bool[,] visited = new bool[width, height];

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (visited[x, y])
						continue;

					List<Vector2Int> region = CollectHeightRegion(heights, new Vector2Int(x, y), visited, out Vector2Int min, out Vector2Int max);
					int regionWidth = max.x - min.x + 1;
					int regionHeight = max.y - min.y + 1;
					if (region.Count < Mathf.Max(1, Settings.MinUsableRegionArea)
						|| regionWidth < Mathf.Max(1, Settings.MinUsableRegionWidth)
						|| regionHeight < Mathf.Max(1, Settings.MinUsableRegionHeight))
					{
						return false;
					}
				}
			}

			return true;
		}

		private int[,] CloneHeights(int[,] heights)
		{
			var clone = new int[heights.GetLength(0), heights.GetLength(1)];
			CopyHeights(heights, clone);
			return clone;
		}

		private void CullUnusableRegions(int[,] heights)
		{
			int guard = heights.GetLength(0) * heights.GetLength(1);
			while (guard-- > 0)
			{
				if (TryMergeSmallRegion(heights) == false)
					return;
			}
		}

		private bool TryMergeSmallRegion(int[,] heights)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			bool[,] visited = new bool[width, height];

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (visited[x, y])
						continue;

					List<Vector2Int> region = CollectHeightRegion(heights, new Vector2Int(x, y), visited, out Vector2Int min, out Vector2Int max);
					int regionWidth = max.x - min.x + 1;
					int regionHeight = max.y - min.y + 1;
					bool tooSmall = region.Count < Mathf.Max(1, Settings.MinUsableRegionArea)
						|| regionWidth < Mathf.Max(1, Settings.MinUsableRegionWidth)
						|| regionHeight < Mathf.Max(1, Settings.MinUsableRegionHeight);

					if (tooSmall == false)
						continue;

					if (TryGetBestNeighborHeight(heights, region, out int replacementHeight) == false)
						continue;

					for (int i = 0; i < region.Count; i++)
						heights[region[i].x, region[i].y] = replacementHeight;

					return true;
				}
			}

			return false;
		}

		private List<Vector2Int> CollectHeightRegion(int[,] heights, Vector2Int start, bool[,] visited, out Vector2Int min, out Vector2Int max)
		{
			int targetHeight = heights[start.x, start.y];
			var region = new List<Vector2Int>();
			var open = new Queue<Vector2Int>();
			open.Enqueue(start);
			visited[start.x, start.y] = true;
			min = start;
			max = start;

			while (open.Count > 0)
			{
				Vector2Int current = open.Dequeue();
				region.Add(current);
				min = Vector2Int.Min(min, current);
				max = Vector2Int.Max(max, current);

				for (int i = 0; i < DirectionOffsets.Length; i++)
				{
					Vector2Int next = current + DirectionOffsets[i];
					if (IsInBounds(heights, next) == false || visited[next.x, next.y] || heights[next.x, next.y] != targetHeight)
						continue;

					visited[next.x, next.y] = true;
					open.Enqueue(next);
				}
			}

			return region;
		}

		private bool TryGetBestNeighborHeight(int[,] heights, List<Vector2Int> region, out int replacementHeight)
		{
			var counts = new Dictionary<int, int>();
			int currentHeight = heights[region[0].x, region[0].y];

			for (int i = 0; i < region.Count; i++)
			{
				for (int direction = 0; direction < DirectionOffsets.Length; direction++)
				{
					Vector2Int neighbor = region[i] + DirectionOffsets[direction];
					if (IsInBounds(heights, neighbor) == false || heights[neighbor.x, neighbor.y] == currentHeight)
						continue;

					int height = heights[neighbor.x, neighbor.y];
					counts.TryGetValue(height, out int count);
					counts[height] = count + 1;
				}
			}

			replacementHeight = currentHeight;
			int bestCount = 0;
			foreach (KeyValuePair<int, int> pair in counts)
			{
				if (pair.Value > bestCount)
				{
					bestCount = pair.Value;
					replacementHeight = pair.Key;
				}
			}

			return bestCount > 0;
		}

		private void EnforceHeightDifferenceRule(int[,] heights, int layerCount)
		{
			int guard = heights.GetLength(0) * heights.GetLength(1) * Mathf.Max(1, layerCount);
			bool changed;
			do
			{
				changed = false;
				for (int x = 0; x < heights.GetLength(0); x++)
				{
					for (int y = 0; y < heights.GetLength(1); y++)
					{
						for (int i = 0; i < NeighborOffsets.Length; i++)
						{
							Vector2Int neighbor = new Vector2Int(x, y) + NeighborOffsets[i];
							if (IsInBounds(heights, neighbor) == false)
								continue;

							if (heights[x, y] > heights[neighbor.x, neighbor.y] + 1)
							{
								heights[x, y] = heights[neighbor.x, neighbor.y] + 1;
								changed = true;
							}
						}
					}
				}

				guard--;
			}
			while (changed && guard > 0);
		}

		private void EnforceMinimumDistanceBetweenHeightChanges(int[,] heights, int layerCount)
		{
			int minimumSpacing = Mathf.Max(0, Settings.MinCellsBetweenHeightChanges);
			if (minimumSpacing <= 1)
				return;

			int guard = heights.GetLength(0) * heights.GetLength(1);
			while (guard-- > 0)
			{
				if (TryResolveCloseHeightChanges(heights, minimumSpacing) == false)
					return;

				EnforceHeightDifferenceRule(heights, layerCount);
			}
		}

		private bool TryResolveCloseHeightChanges(int[,] heights, int minimumSpacing)
		{
			return TryResolveCloseHeightChangesHorizontal(heights, minimumSpacing)
				|| TryResolveCloseHeightChangesVertical(heights, minimumSpacing)
				|| TryResolveCloseHeightChangesDiagonal(heights, minimumSpacing, new Vector2Int(1, 1))
				|| TryResolveCloseHeightChangesDiagonal(heights, minimumSpacing, new Vector2Int(1, -1));
		}

		private void EnforceMapEdgeHeightContinuity(int[,] heights)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			if (width < 3 || height < 3)
				return;

			for (int x = 0; x < width; x++)
			{
				heights[x, 0] = heights[x, 1];
				heights[x, height - 1] = heights[x, height - 2];
			}

			for (int y = 0; y < height; y++)
			{
				heights[0, y] = heights[1, y];
				heights[width - 1, y] = heights[width - 2, y];
			}
		}

		private bool TryResolveCloseHeightChangesHorizontal(int[,] heights, int minimumSpacing)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);

			for (int y = 0; y < height; y++)
			{
				int previousTransition = -1;
				for (int x = 0; x < width - 1; x++)
				{
					if (heights[x, y] == heights[x + 1, y])
						continue;

					if (previousTransition >= 0 && x - previousTransition < minimumSpacing)
					{
						MergeHorizontalStrip(heights, y, previousTransition + 1, x);
						return true;
					}

					previousTransition = x;
				}
			}

			return false;
		}

		private bool TryResolveCloseHeightChangesVertical(int[,] heights, int minimumSpacing)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);

			for (int x = 0; x < width; x++)
			{
				int previousTransition = -1;
				for (int y = 0; y < height - 1; y++)
				{
					if (heights[x, y] == heights[x, y + 1])
						continue;

					if (previousTransition >= 0 && y - previousTransition < minimumSpacing)
					{
						MergeVerticalStrip(heights, x, previousTransition + 1, y);
						return true;
					}

					previousTransition = y;
				}
			}

			return false;
		}

		private bool TryResolveCloseHeightChangesDiagonal(int[,] heights, int minimumSpacing, Vector2Int direction)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			var starts = new List<Vector2Int>();

			if (direction.y > 0)
			{
				for (int x = 0; x < width; x++)
					starts.Add(new Vector2Int(x, 0));

				for (int y = 1; y < height; y++)
					starts.Add(new Vector2Int(0, y));
			}
			else
			{
				for (int x = 0; x < width; x++)
					starts.Add(new Vector2Int(x, height - 1));

				for (int y = 0; y < height - 1; y++)
					starts.Add(new Vector2Int(0, y));
			}

			for (int i = 0; i < starts.Count; i++)
			{
				if (TryResolveCloseHeightChangesOnLine(heights, starts[i], direction, minimumSpacing))
					return true;
			}

			return false;
		}

		private bool TryResolveCloseHeightChangesOnLine(int[,] heights, Vector2Int start, Vector2Int direction, int minimumSpacing)
		{
			var line = new List<Vector2Int>();
			for (Vector2Int current = start; IsInBounds(heights, current); current += direction)
				line.Add(current);

			int previousTransition = -1;
			for (int i = 0; i < line.Count - 1; i++)
			{
				Vector2Int current = line[i];
				Vector2Int next = line[i + 1];
				if (heights[current.x, current.y] == heights[next.x, next.y])
					continue;

				if (previousTransition >= 0 && i - previousTransition < minimumSpacing)
				{
					MergeLineStrip(heights, line, previousTransition + 1, i);
					return true;
				}

				previousTransition = i;
			}

			return false;
		}

		private void MergeHorizontalStrip(int[,] heights, int y, int startX, int endX)
		{
			int replacementHeight = ChooseHorizontalStripReplacementHeight(heights, y, startX, endX);
			for (int x = startX; x <= endX; x++)
				heights[x, y] = replacementHeight;
		}

		private void MergeVerticalStrip(int[,] heights, int x, int startY, int endY)
		{
			int replacementHeight = ChooseVerticalStripReplacementHeight(heights, x, startY, endY);
			for (int y = startY; y <= endY; y++)
				heights[x, y] = replacementHeight;
		}

		private void MergeLineStrip(int[,] heights, List<Vector2Int> line, int startIndex, int endIndex)
		{
			int replacementHeight = ChooseLineStripReplacementHeight(heights, line, startIndex, endIndex);
			for (int i = startIndex; i <= endIndex; i++)
				heights[line[i].x, line[i].y] = replacementHeight;
		}

		private int ChooseHorizontalStripReplacementHeight(int[,] heights, int y, int startX, int endX)
		{
			int width = heights.GetLength(0);
			int leftHeight = startX > 0 ? heights[startX - 1, y] : heights[startX, y];
			int rightHeight = endX < width - 1 ? heights[endX + 1, y] : heights[endX, y];
			if (leftHeight == rightHeight)
				return leftHeight;

			int leftSupport = CountHeightSupport(heights, new Vector2Int(startX, y), new Vector2Int(endX, y), leftHeight);
			int rightSupport = CountHeightSupport(heights, new Vector2Int(startX, y), new Vector2Int(endX, y), rightHeight);
			return rightSupport > leftSupport ? rightHeight : leftHeight;
		}

		private int ChooseVerticalStripReplacementHeight(int[,] heights, int x, int startY, int endY)
		{
			int height = heights.GetLength(1);
			int bottomHeight = startY > 0 ? heights[x, startY - 1] : heights[x, startY];
			int topHeight = endY < height - 1 ? heights[x, endY + 1] : heights[x, endY];
			if (bottomHeight == topHeight)
				return bottomHeight;

			int bottomSupport = CountHeightSupport(heights, new Vector2Int(x, startY), new Vector2Int(x, endY), bottomHeight);
			int topSupport = CountHeightSupport(heights, new Vector2Int(x, startY), new Vector2Int(x, endY), topHeight);
			return topSupport > bottomSupport ? topHeight : bottomHeight;
		}

		private int ChooseLineStripReplacementHeight(int[,] heights, List<Vector2Int> line, int startIndex, int endIndex)
		{
			int beforeHeight = startIndex > 0 ? heights[line[startIndex - 1].x, line[startIndex - 1].y] : heights[line[startIndex].x, line[startIndex].y];
			int afterHeight = endIndex < line.Count - 1 ? heights[line[endIndex + 1].x, line[endIndex + 1].y] : heights[line[endIndex].x, line[endIndex].y];
			if (beforeHeight == afterHeight)
				return beforeHeight;

			int beforeSupport = CountLineHeightSupport(heights, line, startIndex, endIndex, beforeHeight);
			int afterSupport = CountLineHeightSupport(heights, line, startIndex, endIndex, afterHeight);
			return afterSupport > beforeSupport ? afterHeight : beforeHeight;
		}

		private int CountLineHeightSupport(int[,] heights, List<Vector2Int> line, int startIndex, int endIndex, int heightLevel)
		{
			int support = 0;
			for (int i = startIndex; i <= endIndex; i++)
			{
				Vector2Int position = line[i];
				for (int direction = 0; direction < DirectionOffsets.Length; direction++)
				{
					Vector2Int neighbor = position + DirectionOffsets[direction];
					if (IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] == heightLevel)
						support++;
				}
			}

			return support;
		}

		private int CountHeightSupport(int[,] heights, Vector2Int min, Vector2Int max, int heightLevel)
		{
			int support = 0;
			for (int x = min.x; x <= max.x; x++)
			{
				for (int y = min.y; y <= max.y; y++)
				{
					for (int i = 0; i < DirectionOffsets.Length; i++)
					{
						Vector2Int neighbor = new Vector2Int(x, y) + DirectionOffsets[i];
						if (IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] == heightLevel)
							support++;
					}
				}
			}

			return support;
		}

		private void CopyHeights(int[,] source, int[,] target)
		{
			for (int x = 0; x < source.GetLength(0); x++)
			{
				for (int y = 0; y < source.GetLength(1); y++)
				{
					target[x, y] = source[x, y];
				}
			}
		}

		private float Hash01(int x, int y, int seed)
		{
			unchecked
			{
				int hash = seed;
				hash = hash * 397 ^ x;
				hash = hash * 397 ^ y;
				hash ^= hash << 13;
				hash ^= hash >> 17;
				hash ^= hash << 5;
				return (hash & 0x7fffffff) / (float)int.MaxValue;
			}
		}

		private List<int> BuildBandSizes(int length, int bandCount, int minBand, System.Random random)
		{
			var sizes = new List<int>(bandCount);
			int remaining = length;
			for (int i = 0; i < bandCount; i++)
			{
				int bandsLeft = bandCount - i;
				int minimumRemaining = Mathf.Max(0, bandsLeft - 1) * minBand;
				int maxSize = Mathf.Max(minBand, remaining - minimumRemaining);
				int size = i == bandCount - 1 ? remaining : random.Next(minBand, maxSize + 1);
				sizes.Add(size);
				remaining -= size;
			}

			return sizes;
		}

		private int[] BuildBoundaries(List<int> bandSizes)
		{
			var boundaries = new int[bandSizes.Count];
			int sum = 0;
			for (int i = 0; i < bandSizes.Count; i++)
			{
				sum += bandSizes[i];
				boundaries[i] = sum;
			}

			return boundaries;
		}

		private int GetBandIndex(int axis, int[] boundaries)
		{
			for (int i = 0; i < boundaries.Length; i++)
			{
				if (axis < boundaries[i])
					return i;
			}

			return boundaries.Length - 1;
		}

		private WorldHeightCell[,] BuildHeightCells(int[,] heights, bool allowFlatFallback = true)
		{
			int width = heights.GetLength(0);
			int height = heights.GetLength(1);
			var cells = new WorldHeightCell[width, height];

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					Vector2Int position = new Vector2Int(x, y);
					int currentHeight = heights[x, y];
					bool isLedge = TryGetLedgeData(heights, position, out RoadDirection highDirection, out int highHeight);
					bool isBoundary = isLedge && IsEdgeCell(width, height, position);
					HeightTileShape shape = isLedge ? GetLedgeShape(heights, position, currentHeight) : HeightTileShape.Straight;
					if (isBoundary && shape != HeightTileShape.Straight)
					{
						isLedge = false;
						isBoundary = false;
						shape = HeightTileShape.Straight;
						highDirection = RoadDirection.North;
						highHeight = currentHeight;
					}

					int rotationSteps = isLedge ? GetLedgeRotationSteps(heights, position, shape, highDirection, currentHeight) : 0;
					bool replaceable = isLedge && isBoundary == false && shape == HeightTileShape.Straight && HasRoadReplaceableSides(heights, position, highDirection, currentHeight, highHeight);

					cells[x, y] = new WorldHeightCell(
						position,
						currentHeight,
						isLedge,
						isBoundary,
						replaceable,
						shape,
						highDirection,
						rotationSteps,
						currentHeight,
						highHeight);
				}
			}

			if (allowFlatFallback && Settings.MinRoadReplaceableLedgesPerHeightRegion > 0 && HasEnoughRoadReplaceableLedges(cells) == false)
			{
				Debug.LogWarning($"{nameof(HeightMapGenerator)} could not find enough road-replaceable ledges, so it fell back to a flat height map.", this);
				return BuildFlatCells(width, height);
			}

			return cells;
		}

		private WorldHeightCell[,] BuildFlatCells(int width, int height)
		{
			var cells = new WorldHeightCell[width, height];
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					cells[x, y] = new WorldHeightCell(
						new Vector2Int(x, y),
						0,
						false,
						false,
						false,
						HeightTileShape.Straight,
						RoadDirection.North,
						0,
						0,
						0);
				}
			}

			return cells;
		}

		private bool TryGetLedgeData(int[,] heights, Vector2Int position, out RoadDirection highDirection, out int highHeight)
		{
			int currentHeight = heights[position.x, position.y];
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighbor = position + DirectionOffsets[i];
				if (IsInBounds(heights, neighbor) == false)
					continue;

				int neighborHeight = heights[neighbor.x, neighbor.y];
				if (neighborHeight > currentHeight)
				{
					highDirection = (RoadDirection)i;
					highHeight = neighborHeight;
					return true;
				}
			}

			for (int i = 0; i < DiagonalOffsets.Length; i++)
			{
				Vector2Int neighbor = position + DiagonalOffsets[i];
				if (IsInBounds(heights, neighbor) == false)
					continue;

				int neighborHeight = heights[neighbor.x, neighbor.y];
				if (neighborHeight > currentHeight)
				{
					highDirection = GetPrimaryDirection(DiagonalOffsets[i]);
					highHeight = neighborHeight;
					return true;
				}
			}

			highDirection = RoadDirection.North;
			highHeight = currentHeight;
			return false;
		}

		private HeightTileShape GetLedgeShape(int[,] heights, Vector2Int position, int currentHeight)
		{
			bool north = HasHigherNeighbor(heights, position, RoadDirection.North, currentHeight);
			bool east = HasHigherNeighbor(heights, position, RoadDirection.East, currentHeight);
			bool south = HasHigherNeighbor(heights, position, RoadDirection.South, currentHeight);
			bool west = HasHigherNeighbor(heights, position, RoadDirection.West, currentHeight);
			int highNeighborCount = (north ? 1 : 0) + (east ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0);

			if (highNeighborCount == 0 && HasHigherDiagonalNeighbor(heights, position, currentHeight))
				return HeightTileShape.OuterCorner;

			// Inner corner is only valid when exactly two ADJACENT cardinal sides are higher.
			// Sandwich cases (N+S or E+W) and 3+ high cardinals are degenerate: the inner-corner prefab
			// represents a single concave corner and would be placed at an arbitrary rotation that does
			// not match the actual terrain, which reads as "an inner corner in a straight section."
			// Fall back to straight in those cases; the rotation uses HighDirection (the first higher
			// cardinal found by TryGetLedgeData) and at least visually matches one of the high sides.
			if (highNeighborCount == 2 && ((north && east) || (east && south) || (south && west) || (west && north)))
				return HeightTileShape.InnerCorner;

			return HeightTileShape.Straight;
		}

		private int GetLedgeRotationSteps(int[,] heights, Vector2Int position, HeightTileShape shape, RoadDirection highDirection, int currentHeight)
		{
			if (shape == HeightTileShape.Straight)
				return (int)highDirection;

			if (shape == HeightTileShape.OuterCorner && TryGetOuterCornerRotationSteps(heights, position, currentHeight, out int outerRotationSteps))
				return outerRotationSteps;

			bool north = HasHigherNeighbor(heights, position, RoadDirection.North, currentHeight);
			bool east = HasHigherNeighbor(heights, position, RoadDirection.East, currentHeight);
			bool south = HasHigherNeighbor(heights, position, RoadDirection.South, currentHeight);
			bool west = HasHigherNeighbor(heights, position, RoadDirection.West, currentHeight);

			// Inner-corner prefab default orientation: high sides are North and East.
			if (north && east)
				return 0;
			if (east && south)
				return 1;
			if (south && west)
				return 2;
			if (west && north)
				return 3;

			// Fallback for unusual opposite/three-sided cases. These should become rare after smoothing,
			// but keeping the first high side deterministic is better than random rotation.
			return (int)highDirection;
		}

		private bool TryGetOuterCornerRotationSteps(int[,] heights, Vector2Int position, int currentHeight, out int rotationSteps)
		{
			// Outer-corner prefab default orientation: higher elevation touches the North-East diagonal.
			if (HasHigherDiagonalNeighbor(heights, position, new Vector2Int(1, 1), currentHeight))
			{
				rotationSteps = 0;
				return true;
			}

			if (HasHigherDiagonalNeighbor(heights, position, new Vector2Int(1, -1), currentHeight))
			{
				rotationSteps = 1;
				return true;
			}

			if (HasHigherDiagonalNeighbor(heights, position, new Vector2Int(-1, -1), currentHeight))
			{
				rotationSteps = 2;
				return true;
			}

			if (HasHigherDiagonalNeighbor(heights, position, new Vector2Int(-1, 1), currentHeight))
			{
				rotationSteps = 3;
				return true;
			}

			rotationSteps = 0;
			return false;
		}

		private bool HasHigherNeighbor(int[,] heights, Vector2Int position, RoadDirection direction, int currentHeight)
		{
			Vector2Int neighbor = position + DirectionOffsets[(int)direction];
			return IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] > currentHeight;
		}

		private bool HasHigherDiagonalNeighbor(int[,] heights, Vector2Int position, int currentHeight)
		{
			for (int i = 0; i < DiagonalOffsets.Length; i++)
			{
				if (HasHigherDiagonalNeighbor(heights, position, DiagonalOffsets[i], currentHeight))
					return true;
			}

			return false;
		}

		private bool HasHigherDiagonalNeighbor(int[,] heights, Vector2Int position, Vector2Int offset, int currentHeight)
		{
			Vector2Int neighbor = position + offset;
			return IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] > currentHeight;
		}

		private RoadDirection GetPrimaryDirection(Vector2Int diagonalOffset)
		{
			if (diagonalOffset.y > 0)
				return RoadDirection.North;

			if (diagonalOffset.x > 0)
				return RoadDirection.East;

			if (diagonalOffset.y < 0)
				return RoadDirection.South;

			return RoadDirection.West;
		}

		private bool HasRoadReplaceableSides(int[,] heights, Vector2Int position, RoadDirection highDirection, int lowHeight, int highHeight)
		{
			Vector2Int high = position + DirectionOffsets[(int)highDirection];
			Vector2Int low = position - DirectionOffsets[(int)highDirection];
			return IsInBounds(heights, high)
				&& IsInBounds(heights, low)
				&& heights[high.x, high.y] == highHeight
				&& heights[low.x, low.y] == lowHeight;
		}

		private bool HasEnoughRoadReplaceableLedges(WorldHeightCell[,] cells)
		{
			var countsByHighLevel = new Dictionary<int, int>();
			int maxPresentHeight = 0;
			for (int x = 0; x < cells.GetLength(0); x++)
			{
				for (int y = 0; y < cells.GetLength(1); y++)
				{
					maxPresentHeight = Mathf.Max(maxPresentHeight, Mathf.Max(cells[x, y].HeightLevel, cells[x, y].HighHeightLevel));
					if (cells[x, y].CanBeReplacedByHeightChangeRoad)
					{
						int highLevel = cells[x, y].HighHeightLevel;
						countsByHighLevel.TryGetValue(highLevel, out int count);
						countsByHighLevel[highLevel] = count + 1;
					}
				}
			}

			int required = Mathf.Max(0, Settings.MinRoadReplaceableLedgesPerHeightRegion);
			for (int level = 1; level <= maxPresentHeight; level++)
			{
				if (countsByHighLevel.TryGetValue(level, out int count) == false || count < required)
					return false;
			}

			return true;
		}

		private void InstantiateLedges(WorldHeightCell[,] cells, System.Random random)
		{
			var root = new GameObject(GeneratedRootName);
			root.transform.SetParent(transform, false);
			_generatedRoot = root.transform;

			var recentPlacements = new Dictionary<HeightTileDefinition, int>();
			var traversalPlacements = new List<Vector2Int>();
			int placementIndex = 0;

			for (int x = 0; x < cells.GetLength(0); x++)
			{
				for (int y = 0; y < cells.GetLength(1); y++)
				{
					WorldHeightCell cell = cells[x, y];
					if (cell.IsLedge == false)
						continue;

					HeightTileCandidate tile = ChooseTile(cell, recentPlacements, traversalPlacements, placementIndex, random);

					// Record whether the chosen tile is walkable without a road ramp so the runtime height snapshot
					// knows this ledge has ordinary NavMesh. `cells` is the same array exposed by the snapshot, so
					// writing it back here makes the flag available to consumers like ZombieClimbSurfaces, which skips
					// these walkable ledges because both factions already walk them normally.
					if (tile.IsValid && tile.Definition.AllowsTraversalWithoutRoad)
					{
						cell = cell.WithAllowsTraversalWithoutRoad(true);
						cells[x, y] = cell;
						traversalPlacements.Add(cell.Position);
					}

					CreateLedgeInstance(cell, tile, root.transform);

					if (tile.IsValid)
						recentPlacements[tile.Definition] = placementIndex;

					placementIndex++;
				}
			}
		}

		private HeightTileCandidate ChooseTile(
			WorldHeightCell cell,
			Dictionary<HeightTileDefinition, int> recentPlacements,
			List<Vector2Int> traversalPlacements,
			int placementIndex,
			System.Random random)
		{
			HeightTileSet tileSet = Settings.LedgeTiles;
			if (tileSet == null || tileSet.Tiles == null || tileSet.Tiles.Length == 0)
				return default;

			var candidates = new List<HeightTileCandidate>();
			var cooldownCandidates = new List<HeightTileCandidate>();
			var traversalSpacedCandidates = new List<HeightTileCandidate>();
			var traversalSpacedCooldownCandidates = new List<HeightTileCandidate>();

			for (int i = 0; i < tileSet.Tiles.Length; i++)
			{
				HeightTileDefinition tile = tileSet.Tiles[i];
				if (tile == null || tile.Shape != cell.LedgeShape || tile.IsBoundaryTile != cell.IsBoundaryLedge)
					continue;

				foreach (int rotationSteps in GetAllowedRotationSteps(cell))
				{
					var candidate = new HeightTileCandidate(tile, rotationSteps);
					candidates.Add(candidate);
					bool traversalAllowed = IsHeightTraversalTileAllowedAt(tile, cell.Position, traversalPlacements);
					if (traversalAllowed)
						traversalSpacedCandidates.Add(candidate);

					// RepeatCooldownDistance is the literal value: 0 means "no cooldown, always available".
					// Previously this fell back to Settings.DefaultLedgeRepeatCooldownDistance when the tile
					// was 0, which silently put every "common" tile on cooldown and caused the weighted
					// fallback pool to fire constantly — letting specialty tiles repeat far more than their
					// own cooldown should have allowed.
					int cooldown = Mathf.Max(0, tile.RepeatCooldownDistance);
					if (cooldown == 0
						|| recentPlacements.TryGetValue(tile, out int lastPlacement) == false
						|| placementIndex - lastPlacement > cooldown)
					{
						cooldownCandidates.Add(candidate);
						if (traversalAllowed)
							traversalSpacedCooldownCandidates.Add(candidate);
					}
				}
			}

			List<HeightTileCandidate> pool = traversalSpacedCooldownCandidates.Count > 0
				? traversalSpacedCooldownCandidates
				: traversalSpacedCandidates.Count > 0
					? traversalSpacedCandidates
					: cooldownCandidates.Count > 0
						? cooldownCandidates
						: candidates;
			if (pool.Count == 0)
				return default;

			return ChooseWeighted(pool, random);
		}

		private HeightTileCandidate ChooseNonTraversalTile(WorldHeightCell cell, System.Random random)
		{
			HeightTileSet tileSet = Settings.LedgeTiles;
			if (tileSet == null || tileSet.Tiles == null || tileSet.Tiles.Length == 0)
				return default;

			var candidates = new List<HeightTileCandidate>();
			for (int i = 0; i < tileSet.Tiles.Length; i++)
			{
				HeightTileDefinition tile = tileSet.Tiles[i];
				if (tile == null ||
				    tile.AllowsTraversalWithoutRoad ||
				    tile.Shape != cell.LedgeShape ||
				    tile.IsBoundaryTile != cell.IsBoundaryLedge)
				{
					continue;
				}

				foreach (int rotationSteps in GetAllowedRotationSteps(cell))
					candidates.Add(new HeightTileCandidate(tile, rotationSteps));
			}

			return candidates.Count > 0 ? ChooseWeighted(candidates, random) : default;
		}

		private bool IsHeightTraversalTileAllowedAt(HeightTileDefinition tile, Vector2Int position, List<Vector2Int> traversalPlacements)
		{
			if (tile == null || tile.AllowsTraversalWithoutRoad == false)
				return true;

			int minimumSpacing = Settings != null ? Mathf.Max(0, Settings.MinCellsBetweenHeightTraversalTiles) : 0;
			if (minimumSpacing <= 0 || traversalPlacements == null)
				return true;

			for (int i = 0; i < traversalPlacements.Count; i++)
			{
				Vector2Int other = traversalPlacements[i];
				if (ChebyshevDistance(position, other) <= minimumSpacing)
					return false;
			}

			return true;
		}

		private GameObject CreateLedgeInstance(WorldHeightCell cell, HeightTileCandidate tile, Transform root)
		{
			Vector3 position = CellToWorld(cell.Position, (cell.LowHeightLevel + cell.HighHeightLevel) * 0.5f);
			Quaternion rotation = tile.IsValid ? Quaternion.Euler(0f, tile.YRotationDegrees, 0f) : Quaternion.identity;

			GameObject instance;
			if (tile.IsValid && tile.Definition.gameObject != null)
			{
				instance = Instantiate(tile.Definition.gameObject, position, rotation, root);
			}
			else
			{
				instance = new GameObject($"Missing Height Tile {cell.Position.x},{cell.Position.y}");
				instance.transform.SetParent(root, false);
				instance.transform.position = position;
				instance.transform.rotation = rotation;
			}

			instance.name = tile.IsValid ? $"{tile.Definition.name} ({cell.Position.x},{cell.Position.y})" : instance.name;
			_ledgeInstances[cell.Position] = instance;
			return instance;
		}

		private static IEnumerable<int> GetAllowedRotationSteps(WorldHeightCell cell)
		{
			yield return cell.LedgeRotationSteps;
		}

		private HeightTileCandidate ChooseWeighted(List<HeightTileCandidate> candidates, System.Random random)
		{
			int totalWeight = 0;
			for (int i = 0; i < candidates.Count; i++)
				totalWeight += Mathf.Max(1, candidates[i].Definition.Weight);

			int roll = random.Next(totalWeight);
			for (int i = 0; i < candidates.Count; i++)
			{
				roll -= Mathf.Max(1, candidates[i].Definition.Weight);
				if (roll < 0)
					return candidates[i];
			}

			return candidates[candidates.Count - 1];
		}

		private Vector3 CellToWorld(Vector2Int cell, int heightLevel)
		{
			return CellToWorld(cell, (float)heightLevel);
		}

		private Vector3 CellToWorld(Vector2Int cell, float heightLevel)
		{
			return transform.position + new Vector3(cell.x * EffectiveTileSize, heightLevel * EffectiveHeightLevelWorldUnits, cell.y * EffectiveTileSize);
		}

		private System.Random GetCellRandom(Vector2Int cell)
		{
			unchecked
			{
				int hash = Seed;
				hash = (hash * 397) ^ cell.x;
				hash = (hash * 397) ^ cell.y;
				hash = (hash * 397) ^ 0x5F356495;
				return new System.Random(hash);
			}
		}

		private bool IsInBounds(int[,] grid, Vector2Int position)
		{
			return position.x >= 0 && position.y >= 0 && position.x < grid.GetLength(0) && position.y < grid.GetLength(1);
		}

		private bool IsEdgeCell(int width, int height, Vector2Int position)
		{
			return position.x == 0 || position.y == 0 || position.x == width - 1 || position.y == height - 1;
		}

		private IEnumerator WaitForNetworkedWorldSeed()
		{
			while (ShouldWaitForNetworkedWorldSeed())
				yield return null;
		}

		private bool ShouldWaitForNetworkedWorldSeed()
		{
			Gameplay gameplay = FindObjectOfType<Gameplay>();
			if (gameplay == null)
				return false;

			return gameplay.Object == null || gameplay.Object.IsValid == false || gameplay.WorldSeed == 0;
		}

		private bool TryGetNetworkedWorldSeed(out int seed)
		{
			seed = 0;

			Gameplay gameplay = FindObjectOfType<Gameplay>();
			if (gameplay == null || gameplay.Object == null || gameplay.Object.IsValid == false)
				return false;

			seed = gameplay.WorldSeed;
			return seed != 0;
		}

		private int GetRandomizedSeed()
		{
			if (Application.isPlaying)
			{
				Debug.LogWarning($"{nameof(HeightMapGenerator)} could not find a networked world seed, so it kept the current seed ({Seed}) instead of rolling a local random seed.", this);
				return Seed;
			}

			return UnityEngine.Random.Range(int.MinValue, int.MaxValue);
		}

		private void ScheduleGenerationComplete()
		{
			if (Application.isPlaying == false)
			{
				IsGenerationComplete = true;
				return;
			}

			if (_generationCompleteCoroutine != null)
				StopCoroutine(_generationCompleteCoroutine);

			_generationCompleteCoroutine = StartCoroutine(MarkGenerationCompleteAfterSceneSettles());
		}

		private IEnumerator MarkGenerationCompleteAfterSceneSettles()
		{
			yield return null;

			Physics.SyncTransforms();
			IsGenerationComplete = true;
			_generationCompleteCoroutine = null;
		}

		private void OnDrawGizmos()
		{
			if (DrawGizmos == false || _lastCells == null)
				return;

			for (int x = 0; x < _lastCells.GetLength(0); x++)
			{
				for (int y = 0; y < _lastCells.GetLength(1); y++)
				{
					WorldHeightCell cell = _lastCells[x, y];
					Gizmos.color = cell.IsLedge ? LedgeGizmoColor : FlatHeightGizmoColor;
					Gizmos.DrawCube(CellToWorld(cell.Position, cell.HeightLevel) + Vector3.up * 0.02f, new Vector3(EffectiveTileSize * 0.75f, 0.08f, EffectiveTileSize * 0.75f));
				}
			}
		}

		private enum BoundarySide
		{
			North,
			East,
			South,
			West,
		}

		private readonly struct BoundarySidePair
		{
			public readonly BoundarySide StartSide;
			public readonly BoundarySide EndSide;

			public BoundarySidePair(BoundarySide startSide, BoundarySide endSide)
			{
				StartSide = startSide;
				EndSide = endSide;
			}
		}

		private readonly struct HeightRegion
		{
			public readonly List<Vector2Int> Cells;
			public readonly RectInt Bounds;
			public readonly int HeightLevel;

			public HeightRegion(List<Vector2Int> cells, RectInt bounds, int heightLevel)
			{
				Cells = cells;
				Bounds = bounds;
				HeightLevel = heightLevel;
			}
		}

		private sealed class HeightPathNode
		{
			public readonly Vector2Int Position;
			public readonly HeightPathNode Parent;
			public readonly float Cost;
			public readonly float Score;

			public HeightPathNode(Vector2Int position, HeightPathNode parent, float cost, float score)
			{
				Position = position;
				Parent = parent;
				Cost = cost;
				Score = score;
			}
		}

		private readonly struct HeightTileCandidate
		{
			public readonly HeightTileDefinition Definition;
			public readonly int RotationSteps;
			public bool IsValid => Definition != null;
			public float YRotationDegrees => RotationSteps * 90f;

			public HeightTileCandidate(HeightTileDefinition definition, int rotationSteps)
			{
				Definition = definition;
				RotationSteps = rotationSteps;
			}
		}

	}
}
