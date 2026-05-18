using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	public class RoadGridGenerator : MonoBehaviour
	{
		private static readonly Vector2Int[] DirectionOffsets =
		{
			new Vector2Int(0, 1),
			new Vector2Int(1, 0),
			new Vector2Int(0, -1),
			new Vector2Int(-1, 0),
		};

		[Header("Setup")]
		public RoadGenerationSettings Settings;
		public HeightMapGenerator HeightGenerator;
		[Min(3)]
		public int Width = 12;
		[Min(3)]
		public int Height = 12;
		public int Seed = 12345;
		public bool RandomizeSeedOnGenerate;
		public float TileSize = 20f;
		public string GeneratedRootName = "Generated Road Grid";
		public bool GenerateOnStart;
		public bool ClearBeforeGenerate = true;
		public bool GenerateHeightMapIfMissing = true;

		[Header("Debug")]
		public bool DrawGizmos = true;
		public Color RoadGizmoColor = new Color(0.2f, 0.7f, 1f, 0.45f);
		public Color ExitGizmoColor = new Color(1f, 0.65f, 0.1f, 0.7f);

		private RoadCell[,] _lastGrid;
		private Transform _generatedRoot;
		private Vector3 _lastOrigin;
		private float _lastTileSize;
		private float _lastHeightLevelWorldUnits;
		private int _actualExitCount;
		private int _failedPathAttempts;
		private int _lastAppliedNetworkedSeed;
		private Coroutine _generationCompleteCoroutine;

		public bool IsGenerationComplete { get; private set; }

		private void Awake()
		{
			ResolveHeightGenerator();
			if (HeightGenerator == null)
				return;

			Width = HeightGenerator.EffectiveWidth;
			Height = HeightGenerator.EffectiveHeight;
			Seed = HeightGenerator.Seed;
			RandomizeSeedOnGenerate = HeightGenerator.RandomizeSeedOnGenerate;
			TileSize = HeightGenerator.EffectiveTileSize;
		}

		private IEnumerator Start()
		{
			if (GenerateOnStart)
			{
				if (Application.isPlaying)
					yield return WaitForNetworkedWorldSeed();

				yield return WaitForHeightMapIfNeeded();
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
			RegenerateBuildingPlacement();
		}

		[ContextMenu("Generate Road Grid")]
		public void Generate()
		{
			IsGenerationComplete = false;

			if (Settings == null)
			{
				Debug.LogWarning($"{nameof(RoadGridGenerator)} on {name} has no settings asset.", this);
				return;
			}

			ResolveHeightGenerator();

			if (ClearBeforeGenerate)
				ClearGenerated();

			if (Application.isPlaying && TryGetNetworkedWorldSeed(out int worldSeed))
			{
				Seed = worldSeed;
				Debug.Log($"{nameof(RoadGridGenerator)} generated with networked world seed {Seed}.", this);
			}
			else if (RandomizeSeedOnGenerate)
			{
				Seed = GetRandomizedSeed();
				if (Application.isPlaying)
					Debug.Log($"{nameof(RoadGridGenerator)} generated with seed {Seed}.", this);
			}

			WorldHeightSnapshot heightSnapshot = GetHeightSnapshot();
			ApplyHeightSnapshotRunValues(heightSnapshot);

			GenerateGrid(heightSnapshot, out RoadCell[,] grid);
			_lastGrid = grid;
			InstantiateGrid(grid, new System.Random(Seed ^ 0x6A09E667));
			ScheduleGenerationComplete();

			if (Application.isPlaying)
				_lastAppliedNetworkedSeed = Seed;
		}

		[ContextMenu("Clear Generated Road Grid")]
		public void ClearGenerated()
		{
			Transform existing = transform.Find(GeneratedRootName);
			if (existing == null && _generatedRoot != null)
				existing = _generatedRoot;

			if (existing == null)
				return;

			if (Application.isPlaying)
			{
				Destroy(existing.gameObject);
			}
			else
			{
				DestroyImmediate(existing.gameObject);
			}

			_generatedRoot = null;
			IsGenerationComplete = false;
		}

		public bool TryGetWorldGridSnapshot(out WorldGridSnapshot snapshot)
		{
			if (_lastGrid == null)
			{
				snapshot = default;
				return false;
			}

			int width = _lastGrid.GetLength(0);
			int height = _lastGrid.GetLength(1);
			var cells = new WorldGridCell[width, height];

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					RoadCell cell = _lastGrid[x, y];
					cells[x, y] = new WorldGridCell(cell.Position, cell.HeightLevel, cell.IsRoad, cell.IsBoundaryExit, cell.IsLedge, cell.IsHeightChangeRoad, cell.Environment);
				}
			}

			snapshot = new WorldGridSnapshot(cells, _lastTileSize > 0f ? _lastTileSize : TileSize, _lastHeightLevelWorldUnits, _lastOrigin);
			return true;
		}

		private WorldHeightSnapshot GetHeightSnapshot()
		{
			ResolveHeightGenerator();
			if (HeightGenerator == null)
				return default;

			if (HeightGenerator.TryGetHeightSnapshot(out WorldHeightSnapshot snapshot) && snapshot.IsValid)
			{
				if (Application.isPlaying && snapshot.Seed != Seed)
				{
					HeightGenerator.Generate();
					if (HeightGenerator.TryGetHeightSnapshot(out snapshot) && snapshot.IsValid)
						return snapshot;
				}

				return snapshot;
			}

			if (GenerateHeightMapIfMissing)
			{
				HeightGenerator.Generate();
				if (HeightGenerator.TryGetHeightSnapshot(out snapshot) && snapshot.IsValid)
					return snapshot;
			}

			return default;
		}

		private void ApplyHeightSnapshotRunValues(WorldHeightSnapshot heightSnapshot)
		{
			if (heightSnapshot.IsValid)
			{
				Width = heightSnapshot.Width;
				Height = heightSnapshot.Height;
				Seed = heightSnapshot.Seed;
				TileSize = heightSnapshot.TileSize;
				_lastOrigin = heightSnapshot.Origin;
				_lastTileSize = heightSnapshot.TileSize;
				_lastHeightLevelWorldUnits = heightSnapshot.HeightLevelWorldUnits;
				return;
			}

			_lastOrigin = transform.position;
			_lastTileSize = Mathf.Max(0.01f, TileSize);
			_lastHeightLevelWorldUnits = 0f;
		}

		private void GenerateGrid(WorldHeightSnapshot heightSnapshot, out RoadCell[,] grid)
		{
			int width = heightSnapshot.IsValid ? heightSnapshot.Width : Mathf.Max(3, Width);
			int height = heightSnapshot.IsValid ? heightSnapshot.Height : Mathf.Max(3, Height);
			grid = new RoadCell[width, height];
			var random = new System.Random(Seed);

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					Vector2Int position = new Vector2Int(x, y);
					RoadCell cell = new RoadCell
					{
						Position = position,
						Environment = RoadEnvironment.Normal,
					};

					if (heightSnapshot.IsValid && heightSnapshot.TryGetCell(position, out WorldHeightCell heightCell))
					{
						cell.HeightLevel = heightCell.HeightLevel;
						cell.IsLedge = heightCell.IsLedge;
						cell.CanBeHeightChangeRoad = heightCell.CanBeReplacedByHeightChangeRoad;
						cell.HighDirection = heightCell.HighDirection;
						cell.LowHeightLevel = heightCell.LowHeightLevel;
						cell.HighHeightLevel = heightCell.HighHeightLevel;
					}

					grid[x, y] = cell;
				}
			}

			List<ExitPlacement> exits = ChooseExits(width, height, random);
			_actualExitCount = exits.Count;
			_failedPathAttempts = 0;

			if (exits.Count == 0)
			{
				Debug.LogWarning($"{nameof(RoadGridGenerator)} could not place any exits for {width}x{height}.", this);
				return;
			}

			CarveExit(grid, exits[0]);
			MarkRoad(grid, exits[0].InnerPosition);
			grid[exits[0].InnerPosition.x, exits[0].InnerPosition.y].IsRequiredRoad = true;

			for (int i = 1; i < exits.Count; i++)
			{
				if (TryConnectToExistingRoad(grid, exits[i].InnerPosition, random, out List<Vector2Int> path))
				{
					CarvePath(grid, path);
					MarkPathRequired(grid, path);
				}
				CarveExit(grid, exits[i]);
			}

			AddExtraRoads(grid, random);
			EnsureHeightChangeRoadContinuations(grid);
			DeriveSockets(grid);

			Debug.Log($"{nameof(RoadGridGenerator)} generated {width}x{height} road grid with {_actualExitCount}/{Settings.RequestedExitCount} exits and {_failedPathAttempts} failed path attempts.", this);
		}

		private List<ExitPlacement> ChooseExits(int width, int height, System.Random random)
		{
			List<ExitPlacement> candidates = BuildExitCandidates(width, height);
			Shuffle(candidates, random);

			int requested = Mathf.Clamp(Settings.RequestedExitCount, 0, candidates.Count);
			for (int count = requested; count > 0; count--)
			{
				var selected = new List<ExitPlacement>(count);

				for (int i = 0; i < candidates.Count && selected.Count < count; i++)
				{
					ExitPlacement candidate = candidates[i];
					if (IsExitCandidateAllowed(candidate, selected))
						selected.Add(candidate);
				}

				if (selected.Count == count)
					return selected;
			}

			return new List<ExitPlacement>();
		}

		private List<ExitPlacement> BuildExitCandidates(int width, int height)
		{
			var candidates = new List<ExitPlacement>(width * 2 + height * 2);
			bool avoidCorners = width > 3 && height > 3;

			for (int x = 0; x < width; x++)
			{
				if (avoidCorners && (x == 0 || x == width - 1))
					continue;

				candidates.Add(new ExitPlacement(new Vector2Int(x, height - 1), new Vector2Int(x, height - 2), RoadDirection.North, 0, x));
				candidates.Add(new ExitPlacement(new Vector2Int(x, 0), new Vector2Int(x, 1), RoadDirection.South, 2, x));
			}

			for (int y = 0; y < height; y++)
			{
				if (avoidCorners && (y == 0 || y == height - 1))
					continue;

				candidates.Add(new ExitPlacement(new Vector2Int(width - 1, y), new Vector2Int(width - 2, y), RoadDirection.East, 1, y));
				candidates.Add(new ExitPlacement(new Vector2Int(0, y), new Vector2Int(1, y), RoadDirection.West, 3, y));
			}

			return candidates;
		}

		private bool IsExitCandidateAllowed(ExitPlacement candidate, List<ExitPlacement> selected)
		{
			for (int i = 0; i < selected.Count; i++)
			{
				ExitPlacement other = selected[i];
				if (candidate.Edge != other.Edge)
					continue;

				if (Mathf.Abs(candidate.EdgeCoordinate - other.EdgeCoordinate) < Mathf.Max(1, Settings.MinExitSpacing))
					return false;
			}

			return true;
		}

		private void CarveExit(RoadCell[,] grid, ExitPlacement exit)
		{
			MarkRoad(grid, exit.Position);
			ref RoadCell cell = ref grid[exit.Position.x, exit.Position.y];
			cell.IsBoundaryExit = true;
			cell.IsRequiredRoad = true;
			cell.ExitDirection = exit.OutwardDirection;
		}

		private bool TryConnectToExistingRoad(RoadCell[,] grid, Vector2Int start, System.Random random, out List<Vector2Int> path)
		{
			int attempts = Mathf.Max(1, Settings.MaxPathAttempts);
			path = null;

			for (int i = 0; i < attempts; i++)
			{
				if (TryFindPathToExistingRoad(grid, start, random, out path))
					return true;

				_failedPathAttempts++;
			}

			MarkRoad(grid, start);
			path = new List<Vector2Int> { start };
			return false;
		}

		private bool TryFindPathToExistingRoad(RoadCell[,] grid, Vector2Int start, System.Random random, out List<Vector2Int> path)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			var open = new List<PathNode>(width * height);
			var nodes = new Dictionary<Vector2Int, PathNode>(width * height);
			var closed = new HashSet<Vector2Int>();

			PathNode startNode = new PathNode(start, null, 0f, EstimateDistanceToRoad(grid, start), -1);
			open.Add(startNode);
			nodes[start] = startNode;

			while (open.Count > 0)
			{
				int bestIndex = GetBestOpenIndex(open);
				PathNode current = open[bestIndex];
				open.RemoveAt(bestIndex);

				if (closed.Add(current.Position) == false)
					continue;

				if (current.Position != start && grid[current.Position.x, current.Position.y].IsRoad)
				{
					path = BuildPath(current);
					return true;
				}

				for (int direction = 0; direction < DirectionOffsets.Length; direction++)
				{
					Vector2Int next = current.Position + DirectionOffsets[direction];
					if (IsInBounds(grid, next) == false || closed.Contains(next))
						continue;

					if (IsEdgeCell(width, height, next) && grid[next.x, next.y].IsRoad == false)
						continue;

					if (IsStepAllowed(grid, current.Position, next) == false)
						continue;

					float stepCost = GetPathStepCost(grid, current, next, direction, random);
					if (stepCost >= 1000f)
						continue;

					float newCost = current.Cost + stepCost;
					if (nodes.TryGetValue(next, out PathNode existing) && existing.Cost <= newCost)
						continue;

					PathNode nextNode = new PathNode(next, current, newCost, EstimateDistanceToRoad(grid, next), direction);
					nodes[next] = nextNode;
					open.Add(nextNode);
				}
			}

			path = null;
			return false;
		}

		private float GetPathStepCost(RoadCell[,] grid, PathNode current, Vector2Int next, int direction, System.Random random)
		{
			float cost = 1f;

			if (current.Direction >= 0 && current.Direction != direction)
				cost += 0.2f;

			cost += (float)random.NextDouble() * 0.35f;

			if (grid[next.x, next.y].IsRoad)
				return cost * 0.25f;

			if (IsRoadPlacementAllowed(grid, next, current) == false)
				return 1000f;

			int adjacentRoads = CountAdjacentRoads(grid, next);
			if (adjacentRoads > 0)
			{
				cost += adjacentRoads * (3f + Settings.MinRoadSpacing);
			}

			return cost;
		}

		private float EstimateDistanceToRoad(RoadCell[,] grid, Vector2Int position)
		{
			float best = float.MaxValue;
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					if (grid[x, y].IsRoad == false)
						continue;

					float distance = Mathf.Abs(position.x - x) + Mathf.Abs(position.y - y);
					if (distance < best)
						best = distance;
				}
			}

			return best == float.MaxValue ? 0f : best;
		}

		private int GetBestOpenIndex(List<PathNode> open)
		{
			int bestIndex = 0;
			float bestScore = open[0].Score;

			for (int i = 1; i < open.Count; i++)
			{
				if (open[i].Score < bestScore)
				{
					bestIndex = i;
					bestScore = open[i].Score;
				}
			}

			return bestIndex;
		}

		private List<Vector2Int> BuildPath(PathNode node)
		{
			var path = new List<Vector2Int>();
			PathNode current = node;
			while (current != null)
			{
				path.Add(current.Position);
				current = current.Previous;
			}

			path.Reverse();
			return path;
		}

		private void CarvePath(RoadCell[,] grid, List<Vector2Int> path)
		{
			for (int i = 0; i < path.Count; i++)
			{
				MarkRoad(grid, path[i]);
			}
		}

		private void MarkPathRequired(RoadCell[,] grid, List<Vector2Int> path)
		{
			for (int i = 0; i < path.Count; i++)
			{
				Vector2Int position = path[i];
				if (IsInBounds(grid, position))
					grid[position.x, position.y].IsRequiredRoad = true;
			}
		}

		private void AddExtraRoads(RoadCell[,] grid, System.Random random)
		{
			float density = Mathf.Clamp01(Settings.ExtraRoadDensity);
			if (density <= 0f)
				return;

			int baseRoadCount = CountRoadCells(grid);
			int maxRoadCount = FillAllValidRoadCells(grid, random);
			int fillableRoads = maxRoadCount - baseRoadCount;
			if (fillableRoads <= 0)
				return;

			ClearNonRequiredRoads(grid);

			int targetRoadCount = baseRoadCount + Mathf.RoundToInt(fillableRoads * density);
			AddConnectingRoads(grid, random, targetRoadCount);

			float stubStart = Mathf.Clamp01(Settings.StubRoadStartDensity);
			if (density >= stubStart)
			{
				float stubFill = stubStart >= 1f ? 1f : Mathf.InverseLerp(stubStart, 1f, density);
				int stubTarget = Mathf.RoundToInt(Mathf.Lerp(CountRoadCells(grid), targetRoadCount, stubFill));
				FillTowardRoadTarget(grid, random, stubTarget, true);
			}
		}

		private void AddConnectingRoads(RoadCell[,] grid, System.Random random, int targetRoadCount)
		{
			int guard = grid.GetLength(0) * grid.GetLength(1);
			while (CountRoadCells(grid) < targetRoadCount && guard > 0)
			{
				if (TryFindConnectingRoadPath(grid, random, targetRoadCount, out List<Vector2Int> path) == false)
					return;

				CarvePath(grid, path);
				guard--;
			}
		}

		private bool TryFindConnectingRoadPath(RoadCell[,] grid, System.Random random, int targetRoadCount, out List<Vector2Int> path)
		{
			path = null;
			int currentRoadCount = CountRoadCells(grid);
			int remainingBudget = targetRoadCount - currentRoadCount;
			if (remainingBudget <= 0)
				return false;

			List<Vector2Int> roads = CollectRoadCells(grid, false);
			if (roads.Count < 2)
				return false;

			int minLength = Mathf.Max(1, Settings.MinConnectingRoadLength);
			int attempts = Mathf.Max(1, Settings.MaxPathAttempts);

			for (int i = 0; i < attempts; i++)
			{
				Vector2Int startRoad = roads[random.Next(roads.Count)];
				if (TryGetRandomEmptyNeighbor(grid, startRoad, random, out Vector2Int start) == false)
					continue;

				Vector2Int endRoad = roads[random.Next(roads.Count)];
				if (endRoad == startRoad || ManhattanDistance(start, endRoad) < minLength)
					continue;

				if (TryFindPathToTarget(grid, start, endRoad, random, out List<Vector2Int> candidatePath) == false)
					continue;

				int newRoadCount = CountNewRoadCells(grid, candidatePath);
				if (newRoadCount < minLength || newRoadCount > remainingBudget)
					continue;

				path = candidatePath;
				return true;
			}

			return false;
		}

		private int CountNewRoadCells(RoadCell[,] grid, List<Vector2Int> path)
		{
			int count = 0;
			for (int i = 0; i < path.Count; i++)
			{
				Vector2Int position = path[i];
				if (IsInBounds(grid, position) && grid[position.x, position.y].IsRoad == false)
					count++;
			}

			return count;
		}

		private void FillTowardRoadTarget(RoadCell[,] grid, System.Random random, int targetRoadCount, bool enforceStubRules)
		{
			bool placedAny;
			int guard = grid.GetLength(0) * grid.GetLength(1);

			do
			{
				placedAny = false;
				List<Vector2Int> candidates = CollectValidFillCandidates(grid);
				Shuffle(candidates, random);

				for (int i = 0; i < candidates.Count && CountRoadCells(grid) < targetRoadCount; i++)
				{
					Vector2Int candidate = candidates[i];
					if (IsRoadPlacementAllowed(grid, candidate, null) == false)
						continue;

					if (enforceStubRules && IsStubPlacementAllowed(grid, candidate) == false)
						continue;

					MarkRoad(grid, candidate);
					placedAny = true;
				}

				guard--;
			}
			while (placedAny && guard > 0 && CountRoadCells(grid) < targetRoadCount);
		}

		private int FillAllValidRoadCells(RoadCell[,] grid, System.Random random)
		{
			FillTowardRoadTarget(grid, random, int.MaxValue, false);
			return CountRoadCells(grid);
		}

		private void ClearNonRequiredRoads(RoadCell[,] grid)
		{
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					if (grid[x, y].IsRequiredRoad)
						continue;

					grid[x, y].IsRoad = false;
					grid[x, y].IsHeightChangeRoad = false;
				}
			}
		}

		private int CountRoadCells(RoadCell[,] grid)
		{
			int count = 0;
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					if (grid[x, y].IsRoad)
						count++;
				}
			}

			return count;
		}

		private List<Vector2Int> CollectValidFillCandidates(RoadCell[,] grid)
		{
			var candidates = new List<Vector2Int>();
			for (int x = 1; x < grid.GetLength(0) - 1; x++)
			{
				for (int y = 1; y < grid.GetLength(1) - 1; y++)
				{
					Vector2Int position = new Vector2Int(x, y);
					if (grid[x, y].IsRoad)
						continue;

					if (CountAdjacentRoads(grid, position) == 0)
						continue;

					candidates.Add(position);
				}
			}

			return candidates;
		}

		private bool TryGetRandomEmptyNeighbor(RoadCell[,] grid, Vector2Int road, System.Random random, out Vector2Int neighbor)
		{
			var candidates = new List<Vector2Int>(4);
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int candidate = road + DirectionOffsets[i];
				if (IsInBounds(grid, candidate)
				    && IsEdgeCell(grid.GetLength(0), grid.GetLength(1), candidate) == false
				    && grid[candidate.x, candidate.y].IsRoad == false
				    && IsRoadPlacementAllowed(grid, candidate, null))
				{
					candidates.Add(candidate);
				}
			}

			if (candidates.Count == 0)
			{
				neighbor = default;
				return false;
			}

			neighbor = candidates[random.Next(candidates.Count)];
			return true;
		}

		private bool TryFindPathToTarget(RoadCell[,] grid, Vector2Int start, Vector2Int target, System.Random random, out List<Vector2Int> path)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			var open = new List<PathNode>(width * height);
			var nodes = new Dictionary<Vector2Int, PathNode>(width * height);
			var closed = new HashSet<Vector2Int>();

			PathNode startNode = new PathNode(start, null, 0f, ManhattanDistance(start, target), -1);
			open.Add(startNode);
			nodes[start] = startNode;

			while (open.Count > 0)
			{
				int bestIndex = GetBestOpenIndex(open);
				PathNode current = open[bestIndex];
				open.RemoveAt(bestIndex);

				if (closed.Add(current.Position) == false)
					continue;

				if (current.Position == target)
				{
					path = BuildPath(current);
					return true;
				}

				for (int direction = 0; direction < DirectionOffsets.Length; direction++)
				{
					Vector2Int next = current.Position + DirectionOffsets[direction];
					if (IsInBounds(grid, next) == false || closed.Contains(next))
						continue;

					if (IsEdgeCell(width, height, next) && next != target)
						continue;

					if (grid[next.x, next.y].IsRoad && next != target)
						continue;

					if (IsStepAllowed(grid, current.Position, next) == false)
						continue;

					float stepCost = GetPathStepCost(grid, current, next, direction, random);
					if (stepCost >= 1000f)
						continue;

					float newCost = current.Cost + stepCost;
					if (nodes.TryGetValue(next, out PathNode existing) && existing.Cost <= newCost)
						continue;

					PathNode nextNode = new PathNode(next, current, newCost, ManhattanDistance(next, target), direction);
					nodes[next] = nextNode;
					open.Add(nextNode);
				}
			}

			path = null;
			return false;
		}

		private List<Vector2Int> CollectRoadCells(RoadCell[,] grid, bool includeBoundaryExits)
		{
			var roads = new List<Vector2Int>();
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					if (grid[x, y].IsRoad && (includeBoundaryExits || grid[x, y].IsBoundaryExit == false))
						roads.Add(new Vector2Int(x, y));
				}
			}

			return roads;
		}

		private void DeriveSockets(RoadCell[,] grid)
		{
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					ref RoadCell cell = ref grid[x, y];
					if (cell.IsRoad == false)
						continue;

					cell.North = GetNeighborSocket(grid, x, y, RoadDirection.North);
					cell.East = GetNeighborSocket(grid, x, y, RoadDirection.East);
					cell.South = GetNeighborSocket(grid, x, y, RoadDirection.South);
					cell.West = GetNeighborSocket(grid, x, y, RoadDirection.West);

					if (cell.IsBoundaryExit)
						SetSocket(ref cell, cell.ExitDirection, RoadSocket.Exit);
				}
			}
		}

		private RoadSocket GetNeighborSocket(RoadCell[,] grid, int x, int y, RoadDirection direction)
		{
			Vector2Int neighbor = new Vector2Int(x, y) + GetOffset(direction);
			if (IsInBounds(grid, neighbor) == false)
				return RoadSocket.Closed;

			if (grid[neighbor.x, neighbor.y].IsRoad == false)
				return RoadSocket.Closed;

			return AreRoadCellsConnected(grid[x, y], grid[neighbor.x, neighbor.y], direction) ? RoadSocket.Road : RoadSocket.Closed;
		}

		private void InstantiateGrid(RoadCell[,] grid, System.Random random)
		{
			var root = new GameObject(GeneratedRootName);
			root.transform.SetParent(transform, false);
			_generatedRoot = root.transform;

			var recentPlacements = new Dictionary<RoadTileDefinition, int>();
			int placementIndex = 0;

			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					RoadCell cell = grid[x, y];
					if (cell.IsRoad == false)
						continue;

					RoadTileCandidate tile = ChooseTile(cell, recentPlacements, placementIndex, random);
					Vector3 position = CellToWorld(cell.Position);
					Quaternion rotation = tile.IsValid ? Quaternion.Euler(0f, tile.YRotationDegrees, 0f) : Quaternion.identity;
					if (cell.IsHeightChangeRoad)
						HeightGenerator?.SuppressLedgeAt(cell.Position);

					GameObject instance;
					if (tile.IsValid && tile.Definition.gameObject != null)
					{
						instance = Instantiate(tile.Definition.gameObject, position, rotation, root.transform);
					}
					else
					{
						instance = new GameObject($"Missing Road Tile {cell.Position.x},{cell.Position.y}");
						instance.transform.SetParent(root.transform, false);
						instance.transform.position = position;
						instance.transform.rotation = rotation;
					}

					instance.name = tile.IsValid ? $"{tile.Definition.name} ({cell.Position.x},{cell.Position.y})" : instance.name;

					if (tile.IsValid)
						recentPlacements[tile.Definition] = placementIndex;

					placementIndex++;
				}
			}
		}

		private void RegenerateBuildingPlacement()
		{
			BuildingPlacementGenerator buildingGenerator = GetComponent<BuildingPlacementGenerator>() ?? FindObjectOfType<BuildingPlacementGenerator>();
			if (buildingGenerator == null || buildingGenerator.GenerateOnStart == false)
				return;

			buildingGenerator.RoadGenerator = this;
			buildingGenerator.Generate();
		}

		private IEnumerator WaitForHeightMapIfNeeded()
		{
			ResolveHeightGenerator();
			if (HeightGenerator == null || HeightGenerator.GenerateOnStart == false)
				yield break;

			while (HeightGenerator.TryGetHeightSnapshot(out WorldHeightSnapshot snapshot) == false || snapshot.IsValid == false)
				yield return null;
		}

		private void ResolveHeightGenerator()
		{
			if (HeightGenerator != null)
				return;

			HeightGenerator = GetComponent<HeightMapGenerator>() ?? FindObjectOfType<HeightMapGenerator>();
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

		private RoadTileCandidate ChooseTile(RoadCell cell, Dictionary<RoadTileDefinition, int> recentPlacements, int placementIndex, System.Random random)
		{
			RoadTileSet tileSet = Settings.RoadTiles;
			if (tileSet == null || tileSet.Tiles == null || tileSet.Tiles.Length == 0)
				return default;

			var candidates = new List<RoadTileCandidate>();
			var cooldownCandidates = new List<RoadTileCandidate>();

			for (int i = 0; i < tileSet.Tiles.Length; i++)
			{
				RoadTileDefinition tile = tileSet.Tiles[i];
				if (tile == null || tile.IsHeightChangeRamp != cell.IsHeightChangeRoad)
					continue;

				for (int rotationSteps = 0; rotationSteps < 4; rotationSteps++)
				{
					if (tile.Matches(cell.North, cell.East, cell.South, cell.West, cell.Environment, rotationSteps) == false)
						continue;

					// Height-change ramps have authored orientation: high side North at rotationSteps 0.
					// Sockets alone are symmetric, so without this constraint a ramp can spawn flipped 180 degrees.
					if (cell.IsHeightChangeRoad && rotationSteps != (int)cell.HighDirection)
						continue;

					var candidate = new RoadTileCandidate(tile, rotationSteps);
					candidates.Add(candidate);

					if (recentPlacements.TryGetValue(tile, out int lastPlacement) == false || placementIndex - lastPlacement > tile.RepeatCooldown)
						cooldownCandidates.Add(candidate);
				}
			}

			List<RoadTileCandidate> pool = cooldownCandidates.Count > 0 ? cooldownCandidates : candidates;
			if (pool.Count == 0)
			{
				Debug.LogWarning($"No road tile matches sockets N:{cell.North} E:{cell.East} S:{cell.South} W:{cell.West}.", this);
				return default;
			}

			return ChooseWeighted(pool, random);
		}

		private RoadTileCandidate ChooseWeighted(List<RoadTileCandidate> candidates, System.Random random)
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

		private void MarkRoad(RoadCell[,] grid, Vector2Int position)
		{
			if (IsInBounds(grid, position) == false)
				return;

			ref RoadCell cell = ref grid[position.x, position.y];
			cell.IsRoad = true;
			if (cell.IsLedge && cell.CanBeHeightChangeRoad)
			{
				cell.IsHeightChangeRoad = true;
			}
		}

		private bool IsRoadPlacementAllowed(RoadCell[,] grid, Vector2Int position, PathNode previous)
		{
			if (grid[position.x, position.y].IsLedge && grid[position.x, position.y].CanBeHeightChangeRoad == false)
				return false;

			if (Settings.PreventSolidRoadBlocks == false || grid[position.x, position.y].IsRoad)
				return true;

			return WouldCreateSolidRoadSquare(grid, position, previous) == false
				&& WouldExceedLocalRoadDensity(grid, position, previous) == false;
		}

		private void EnsureHeightChangeRoadContinuations(RoadCell[,] grid)
		{
			int completed = 0;
			int reverted = 0;

			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					Vector2Int position = new Vector2Int(x, y);
					if (grid[x, y].IsHeightChangeRoad == false)
						continue;

					if (HasRequiredRampConnections(grid, position))
						continue;

					if (TryCompleteRampContinuation(grid, position))
					{
						completed++;
						continue;
					}

					ref RoadCell cell = ref grid[x, y];
					cell.IsRoad = false;
					cell.IsHeightChangeRoad = false;
					reverted++;
				}
			}

			if (completed > 0 || reverted > 0)
				Debug.Log($"{nameof(RoadGridGenerator)} completed {completed} one-sided ramp(s) and reverted {reverted} invalid ramp(s).", this);
		}

		private bool TryCompleteRampContinuation(RoadCell[,] grid, Vector2Int rampPosition)
		{
			RoadCell ramp = grid[rampPosition.x, rampPosition.y];
			RoadDirection highDirection = ramp.HighDirection;
			RoadDirection lowDirection = GetOpposite(highDirection);
			bool highConnected = IsRampConnectedToRoad(grid, rampPosition, highDirection);
			bool lowConnected = IsRampConnectedToRoad(grid, rampPosition, lowDirection);

			if (highConnected && lowConnected)
				return true;

			if (highConnected == lowConnected)
				return false;

			RoadDirection missingDirection = highConnected ? lowDirection : highDirection;
			Vector2Int continuationPosition = rampPosition + GetOffset(missingDirection);
			if (CanCreateRampContinuationRoad(grid, ramp, continuationPosition, missingDirection) == false)
				return false;

			MarkRoad(grid, continuationPosition);
			return HasRequiredRampConnections(grid, rampPosition);
		}

		private bool HasRequiredRampConnections(RoadCell[,] grid, Vector2Int rampPosition)
		{
			RoadCell ramp = grid[rampPosition.x, rampPosition.y];
			return IsRampConnectedToRoad(grid, rampPosition, ramp.HighDirection)
				&& IsRampConnectedToRoad(grid, rampPosition, GetOpposite(ramp.HighDirection));
		}

		private bool IsRampConnectedToRoad(RoadCell[,] grid, Vector2Int rampPosition, RoadDirection directionFromRamp)
		{
			Vector2Int neighborPosition = rampPosition + GetOffset(directionFromRamp);
			if (IsInBounds(grid, neighborPosition) == false)
				return false;

			RoadCell neighbor = grid[neighborPosition.x, neighborPosition.y];
			return neighbor.IsRoad && AreRoadCellsConnected(grid[rampPosition.x, rampPosition.y], neighbor, directionFromRamp);
		}

		private bool CanCreateRampContinuationRoad(RoadCell[,] grid, RoadCell ramp, Vector2Int continuationPosition, RoadDirection directionFromRamp)
		{
			if (IsInBounds(grid, continuationPosition) == false)
				return false;

			RoadCell continuation = grid[continuationPosition.x, continuationPosition.y];
			if (continuation.IsRoad || continuation.IsLedge)
				return false;

			int requiredHeight = directionFromRamp == ramp.HighDirection ? ramp.HighHeightLevel : ramp.LowHeightLevel;
			if (continuation.HeightLevel != requiredHeight)
				return false;

			return IsRoadPlacementAllowed(grid, continuationPosition, null);
		}

		private bool IsStubPlacementAllowed(RoadCell[,] grid, Vector2Int position)
		{
			if (Settings.RequireDiagonalSpaceForStubRoads == false)
				return true;

			if (CountAdjacentRoads(grid, position) != 1)
				return true;

			if (HasDiagonalRoadNeighbor(grid, position) == false)
				return true;

			return HasValidStubContinuation(grid, position);
		}

		private bool HasValidStubContinuation(RoadCell[,] grid, Vector2Int position)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			var previous = new PathNode(position, null, 0f, 0f, -1);

			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int next = position + DirectionOffsets[i];
				if (IsInBounds(grid, next) == false
				    || IsEdgeCell(width, height, next)
				    || grid[next.x, next.y].IsRoad)
				{
					continue;
				}

				if (IsRoadPlacementAllowed(grid, next, previous))
					return true;
			}

			return false;
		}

		private bool HasDiagonalRoadNeighbor(RoadCell[,] grid, Vector2Int position)
		{
			for (int x = -1; x <= 1; x += 2)
			{
				for (int y = -1; y <= 1; y += 2)
				{
					Vector2Int neighbor = new Vector2Int(position.x + x, position.y + y);
					if (IsInBounds(grid, neighbor) && grid[neighbor.x, neighbor.y].IsRoad)
						return true;
				}
			}

			return false;
		}

		private bool WouldCreateSolidRoadSquare(RoadCell[,] grid, Vector2Int position, PathNode previous)
		{
			for (int startX = position.x - 1; startX <= position.x; startX++)
			{
				for (int startY = position.y - 1; startY <= position.y; startY++)
				{
					if (IsProspectiveRoad(grid, new Vector2Int(startX, startY), position, previous)
					    && IsProspectiveRoad(grid, new Vector2Int(startX + 1, startY), position, previous)
					    && IsProspectiveRoad(grid, new Vector2Int(startX, startY + 1), position, previous)
					    && IsProspectiveRoad(grid, new Vector2Int(startX + 1, startY + 1), position, previous))
					{
						return true;
					}
				}
			}

			return false;
		}

		private bool WouldExceedLocalRoadDensity(RoadCell[,] grid, Vector2Int position, PathNode previous)
		{
			int maxRoadCells = Mathf.Clamp(Settings.MaxRoadCellsIn3x3, 1, 9);

			for (int startX = position.x - 2; startX <= position.x; startX++)
			{
				for (int startY = position.y - 2; startY <= position.y; startY++)
				{
					int count = 0;

					for (int x = startX; x < startX + 3; x++)
					{
						for (int y = startY; y < startY + 3; y++)
						{
							if (IsProspectiveRoad(grid, new Vector2Int(x, y), position, previous))
								count++;
						}
					}

					if (count > maxRoadCells)
						return true;
				}
			}

			return false;
		}

		private bool IsProspectiveRoad(RoadCell[,] grid, Vector2Int query, Vector2Int candidate, PathNode previous)
		{
			if (IsInBounds(grid, query) == false)
				return false;

			if (grid[query.x, query.y].IsRoad || query == candidate)
				return true;

			for (PathNode node = previous; node != null; node = node.Previous)
			{
				if (node.Position == query)
					return true;
			}

			return false;
		}

		private int CountAdjacentRoads(RoadCell[,] grid, Vector2Int position)
		{
			int count = 0;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighbor = position + DirectionOffsets[i];
				if (IsInBounds(grid, neighbor)
				    && grid[neighbor.x, neighbor.y].IsRoad
				    && AreRoadCellsConnected(grid[position.x, position.y], grid[neighbor.x, neighbor.y], (RoadDirection)i))
				{
					count++;
				}
			}

			return count;
		}

		private bool IsStepAllowed(RoadCell[,] grid, Vector2Int from, Vector2Int to)
		{
			if (IsInBounds(grid, from) == false || IsInBounds(grid, to) == false)
				return false;

			int directionIndex = GetDirectionIndex(from, to);
			if (directionIndex < 0)
				return false;

			RoadDirection direction = (RoadDirection)directionIndex;
			RoadCell fromCell = grid[from.x, from.y];
			RoadCell toCell = grid[to.x, to.y];
			if (toCell.IsLedge && toCell.CanBeHeightChangeRoad == false && toCell.IsRoad == false)
				return false;

			return AreRoadCellsConnected(fromCell, toCell, direction);
		}

		private bool AreRoadCellsConnected(RoadCell from, RoadCell to, RoadDirection direction)
		{
			if (from.IsHeightChangeRoad)
				return IsRampSideCompatible(from, direction, to);

			if (to.IsHeightChangeRoad || (to.IsLedge && to.CanBeHeightChangeRoad))
				return IsRampSideCompatible(to, GetOpposite(direction), from);

			if (from.IsLedge && from.CanBeHeightChangeRoad)
				return IsRampSideCompatible(from, direction, to);

			return from.HeightLevel == to.HeightLevel && to.IsLedge == false;
		}

		private bool IsRampSideCompatible(RoadCell ramp, RoadDirection directionFromRampToNeighbor, RoadCell neighbor)
		{
			if (directionFromRampToNeighbor == ramp.HighDirection)
				return neighbor.HeightLevel == ramp.HighHeightLevel && neighbor.IsLedge == false;

			if (directionFromRampToNeighbor == GetOpposite(ramp.HighDirection))
				return neighbor.HeightLevel == ramp.LowHeightLevel && neighbor.IsLedge == false;

			return false;
		}

		private bool IsInBounds(RoadCell[,] grid, Vector2Int position)
		{
			return position.x >= 0 && position.y >= 0 && position.x < grid.GetLength(0) && position.y < grid.GetLength(1);
		}

		private bool IsEdgeCell(int width, int height, Vector2Int position)
		{
			return position.x == 0 || position.y == 0 || position.x == width - 1 || position.y == height - 1;
		}

		private int ManhattanDistance(Vector2Int a, Vector2Int b)
		{
			return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
		}

		private Vector2Int GetOffset(RoadDirection direction)
		{
			return DirectionOffsets[(int)direction];
		}

		private int GetDirectionIndex(Vector2Int from, Vector2Int to)
		{
			Vector2Int delta = to - from;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				if (DirectionOffsets[i] == delta)
					return i;
			}

			return -1;
		}

		private RoadDirection GetOpposite(RoadDirection direction)
		{
			return (RoadDirection)(((int)direction + 2) % 4);
		}

		private void SetSocket(ref RoadCell cell, RoadDirection direction, RoadSocket socket)
		{
			switch (direction)
			{
				case RoadDirection.North:
					cell.North = socket;
					break;
				case RoadDirection.East:
					cell.East = socket;
					break;
				case RoadDirection.South:
					cell.South = socket;
					break;
				case RoadDirection.West:
					cell.West = socket;
					break;
			}
		}

		private Vector3 CellToWorld(Vector2Int cell)
		{
			if (_lastGrid != null && IsInBounds(_lastGrid, cell))
			{
				RoadCell roadCell = _lastGrid[cell.x, cell.y];
				float heightLevel = roadCell.IsHeightChangeRoad
					? (roadCell.LowHeightLevel + roadCell.HighHeightLevel) * 0.5f
					: roadCell.HeightLevel;
				return _lastOrigin + new Vector3(cell.x * _lastTileSize, heightLevel * _lastHeightLevelWorldUnits, cell.y * _lastTileSize);
			}

			return _lastOrigin + new Vector3(cell.x * (_lastTileSize > 0f ? _lastTileSize : TileSize), 0f, cell.y * (_lastTileSize > 0f ? _lastTileSize : TileSize));
		}

		private void Shuffle<T>(List<T> list, System.Random random)
		{
			for (int i = list.Count - 1; i > 0; i--)
			{
				int j = random.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}
		}

		private IEnumerator WaitForNetworkedWorldSeed()
		{
			while (ShouldWaitForNetworkedWorldSeed())
			{
				yield return null;
			}
		}

		private int GetRandomizedSeed()
		{
			if (Application.isPlaying)
			{
				Debug.LogWarning($"{nameof(RoadGridGenerator)} could not find a networked world seed, so it kept the current seed ({Seed}) instead of rolling a local random seed.", this);
				return Seed;
			}

			return UnityEngine.Random.Range(int.MinValue, int.MaxValue);
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

		private void OnDrawGizmos()
		{
			if (DrawGizmos == false || _lastGrid == null)
				return;

			for (int x = 0; x < _lastGrid.GetLength(0); x++)
			{
				for (int y = 0; y < _lastGrid.GetLength(1); y++)
				{
					RoadCell cell = _lastGrid[x, y];
					if (cell.IsRoad == false)
						continue;

					Gizmos.color = cell.IsBoundaryExit ? ExitGizmoColor : RoadGizmoColor;
					Gizmos.DrawCube(CellToWorld(cell.Position) + Vector3.up * 0.05f, new Vector3(TileSize * 0.8f, 0.1f, TileSize * 0.8f));
				}
			}
		}

		private struct RoadCell
		{
			public Vector2Int Position;
			public int HeightLevel;
			public bool IsRoad;
			public bool IsRequiredRoad;
			public bool IsBoundaryExit;
			public bool IsLedge;
			public bool CanBeHeightChangeRoad;
			public bool IsHeightChangeRoad;
			public int LowHeightLevel;
			public int HighHeightLevel;
			public RoadDirection HighDirection;
			public RoadDirection ExitDirection;
			public RoadSocket North;
			public RoadSocket East;
			public RoadSocket South;
			public RoadSocket West;
			public RoadEnvironment Environment;
		}

		private readonly struct RoadTileCandidate
		{
			public readonly RoadTileDefinition Definition;
			public readonly int RotationSteps;
			public bool IsValid => Definition != null;
			public float YRotationDegrees => RotationSteps * 90f;

			public RoadTileCandidate(RoadTileDefinition definition, int rotationSteps)
			{
				Definition = definition;
				RotationSteps = rotationSteps;
			}
		}

		private readonly struct ExitPlacement
		{
			public readonly Vector2Int Position;
			public readonly Vector2Int InnerPosition;
			public readonly RoadDirection OutwardDirection;
			public readonly int Edge;
			public readonly int EdgeCoordinate;

			public ExitPlacement(Vector2Int position, Vector2Int innerPosition, RoadDirection outwardDirection, int edge, int edgeCoordinate)
			{
				Position = position;
				InnerPosition = innerPosition;
				OutwardDirection = outwardDirection;
				Edge = edge;
				EdgeCoordinate = edgeCoordinate;
			}
		}

		private sealed class PathNode
		{
			public readonly Vector2Int Position;
			public readonly PathNode Previous;
			public readonly float Cost;
			public readonly float Heuristic;
			public readonly int Direction;
			public float Score => Cost + Heuristic;

			public PathNode(Vector2Int position, PathNode previous, float cost, float heuristic, int direction)
			{
				Position = position;
				Previous = previous;
				Cost = cost;
				Heuristic = heuristic;
				Direction = direction;
			}
		}
	}
}
