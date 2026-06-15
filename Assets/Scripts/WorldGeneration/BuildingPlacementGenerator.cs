using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;

namespace SimpleFPS
{
	public class BuildingPlacementGenerator : MonoBehaviour
	{
		private static readonly Vector2Int[] DirectionOffsets =
		{
			new Vector2Int(0, 1),
			new Vector2Int(1, 0),
			new Vector2Int(0, -1),
			new Vector2Int(-1, 0),
		};

		[Header("Setup")]
		public RoadGridGenerator RoadGenerator;
		public BuildingPlacementSettings Settings;
		public string GeneratedRootName = "Generated Buildings";
		public bool GenerateOnStart;
		public bool ClearBeforeGenerate = true;
		public bool GenerateRoadGridIfMissing = true;

		[Header("Navigation")]
		public NavMeshSurface NavMeshSurface;
		public bool RebuildNavMeshAfterGenerate = true;
		public bool FindNavMeshSurfaceIfMissing = true;

		[Header("Loot")]
		public WorldLootSpawner LootSpawner;
		public bool SpawnLootAfterGenerate = true;
		public bool FindLootSpawnerIfMissing = true;

		[Header("Debug")]
		public bool DrawGizmos = true;
		public Color ComplexBuildingGizmoColor = new Color(0.35f, 0.9f, 0.45f, 0.45f);
		public Color SimpleBlockingGizmoColor = new Color(0.9f, 0.75f, 0.25f, 0.45f);

		private BuildingCell[,] _lastGrid;
		private readonly List<PlacedBuilding> _lastPlacements = new();
		private Transform _generatedRoot;
		private WorldGridSnapshot _lastRoadGrid;
		private Coroutine _navMeshRebuildCoroutine;
		private bool _hasRuntimeLedgeTunnelPruningSettings;
		private bool _runtimePreserveBuriedLedgeTunnels;
		private int _runtimeMaxDeadEndBuriedLedgeLength;
		private int _runtimeMaxBuriedLedgeTunnelLength;

		public Transform GeneratedRoot => _generatedRoot;
		public bool IsGenerationComplete { get; private set; }

		public void SetRuntimeLedgeTunnelPruningSettings(bool preserveBuriedLedgeTunnels, int maxDeadEndBuriedLedgeLength, int maxBuriedLedgeTunnelLength)
		{
			_hasRuntimeLedgeTunnelPruningSettings = true;
			_runtimePreserveBuriedLedgeTunnels = preserveBuriedLedgeTunnels;
			_runtimeMaxDeadEndBuriedLedgeLength = Mathf.Max(0, maxDeadEndBuriedLedgeLength);
			_runtimeMaxBuriedLedgeTunnelLength = Mathf.Max(0, maxBuriedLedgeTunnelLength);
		}

		private IEnumerator Start()
		{
			if (GenerateOnStart)
			{
				yield return WaitForRoadGridIfNeeded();
				Generate();
			}
		}

		[ContextMenu("Generate Buildings")]
		public void Generate()
		{
			IsGenerationComplete = false;

			if (Settings == null)
			{
				Debug.LogWarning($"{nameof(BuildingPlacementGenerator)} on {name} has no settings asset.", this);
				return;
			}

			if (RoadGenerator == null)
				RoadGenerator = GetComponent<RoadGridGenerator>();

			if (RoadGenerator == null)
			{
				Debug.LogWarning($"{nameof(BuildingPlacementGenerator)} on {name} has no road generator.", this);
				return;
			}

			if (RoadGenerator.TryGetWorldGridSnapshot(out WorldGridSnapshot roadGrid) == false)
			{
				if (GenerateRoadGridIfMissing)
				{
					RoadGenerator.Generate();
					RoadGenerator.TryGetWorldGridSnapshot(out roadGrid);
				}

				if (roadGrid.IsValid == false)
				{
					Debug.LogWarning($"{nameof(BuildingPlacementGenerator)} could not read a generated road grid.", this);
					return;
				}
			}

			if (ClearBeforeGenerate)
				ClearGenerated();

			_lastRoadGrid = roadGrid;
			GenerateBuildings(roadGrid, out BuildingCell[,] grid, out List<PlacedBuilding> placements);
			_lastGrid = grid;
			_lastPlacements.Clear();
			_lastPlacements.AddRange(placements);
			InstantiateBuildings(roadGrid, placements);
			SchedulePostGenerateWork();
			SpawnLoot();
		}

		[ContextMenu("Clear Generated Buildings")]
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
		}

		private void GenerateBuildings(WorldGridSnapshot roadGrid, out BuildingCell[,] grid, out List<PlacedBuilding> placements)
		{
			grid = CreateBuildingGrid(roadGrid);
			placements = new List<PlacedBuilding>();
			var random = new System.Random(GetSeed());

			if (Settings.FillMapEdgesWithBlockingBuildings)
				FillEdgeBlockers(grid, placements, random);

			PlaceComplexBuildings(grid, placements, random);

			if (Settings.FillRemainingEmptyCellsWithBlockingBuildings)
				FillRemainingBlockers(grid, placements, random);

			ReplaceLedgesAdjacentToBlockers(grid, placements);

			Debug.Log($"{nameof(BuildingPlacementGenerator)} placed {placements.Count} buildings.", this);
		}

		private int GetSeed()
		{
			int roadSeed = RoadGenerator != null ? RoadGenerator.Seed : 0;
			return roadSeed + Settings.SeedOffset;
		}

		private IEnumerator WaitForRoadGridIfNeeded()
		{
			if (RoadGenerator == null)
				RoadGenerator = GetComponent<RoadGridGenerator>();

			if (Application.isPlaying == false ||
				RoadGenerator == null ||
				RoadGenerator.GenerateOnStart == false)
			{
				yield break;
			}

			while (true)
			{
				if (RoadGenerator.TryGetWorldGridSnapshot(out WorldGridSnapshot roadGrid) && roadGrid.IsValid)
					yield break;

				yield return null;
			}
		}

		private BuildingCell[,] CreateBuildingGrid(WorldGridSnapshot roadGrid)
		{
			var grid = new BuildingCell[roadGrid.Width, roadGrid.Height];
			for (int x = 0; x < roadGrid.Width; x++)
			{
				for (int y = 0; y < roadGrid.Height; y++)
				{
					roadGrid.TryGetCell(new Vector2Int(x, y), out WorldGridCell roadCell);
					grid[x, y] = new BuildingCell
					{
						Position = roadCell.Position,
						HeightLevel = roadCell.HeightLevel,
						IsRoad = roadCell.IsRoad,
						IsBoundaryExit = roadCell.IsBoundaryExit,
						IsLedge = roadCell.IsLedge,
						IsHeightChangeRoad = roadCell.IsHeightChangeRoad,
						Environment = roadCell.Environment,
					};
				}
			}

			return grid;
		}

		private void FillEdgeBlockers(BuildingCell[,] grid, List<PlacedBuilding> placements, System.Random random)
		{
			int guard = grid.GetLength(0) * grid.GetLength(1);
			while (HasEmptyEdgeCell(grid) && guard > 0)
			{
				if (TryPlaceBestBuilding(grid, placements, BuildingCategory.SimpleBlocking, true, false, true, true, random) == false)
					break;

				guard--;
			}
		}

		private void PlaceComplexBuildings(BuildingCell[,] grid, List<PlacedBuilding> placements, System.Random random)
		{
			int emptyCells = CountEmptyCells(grid);
			int largeCellTarget = Mathf.RoundToInt(emptyCells * Mathf.Clamp01(Settings.LargeBuildingPreference));
			int largeCellsPlaced = 0;

			if (Settings.LargeBuildingPreference > 0f)
			{
				largeCellsPlaced += PlaceComplexBuildingsByArea(grid, placements, random, 4, largeCellTarget, largeCellsPlaced);
				largeCellsPlaced += PlaceComplexBuildingsByArea(grid, placements, random, 2, largeCellTarget, largeCellsPlaced);
			}

			PlaceComplexBuildingsByArea(grid, placements, random, 1, int.MaxValue, 0);
		}

		private int PlaceComplexBuildingsByArea(BuildingCell[,] grid, List<PlacedBuilding> placements, System.Random random, int area, int largeCellTarget, int largeCellsPlaced)
		{
			int placedCells = 0;
			int guard = grid.GetLength(0) * grid.GetLength(1);

			while (guard > 0)
			{
				if (area > 1 && largeCellsPlaced + placedCells >= largeCellTarget)
					break;

				if (TryPlaceBestBuilding(grid, placements, BuildingCategory.Complex, false, true, true, false, random, area) == false)
					break;

				placedCells += area;
				guard--;
			}

			return placedCells;
		}

		private void ReplaceLedgesAdjacentToBlockers(BuildingCell[,] grid, List<PlacedBuilding> placements)
		{
			BuildingDefinition replacement = FindLedgeReplacementBlocker();
			if (replacement == null)
				return;

			WorldHeightSnapshot heightSnapshot = default;
			bool hasHeightSnapshot = RoadGenerator != null
				&& RoadGenerator.HeightGenerator != null
				&& RoadGenerator.HeightGenerator.TryGetHeightSnapshot(out heightSnapshot)
				&& heightSnapshot.IsValid;

			// Interior ledges resolve first, then pit-corner cleanup may add more blockers, and finally
			// boundary ledges run against the finished blocker layout.
			bool preserveBuriedLedgeTunnels = GetPreserveBuriedLedgeTunnels();
			int maxDeadEndBuriedLedgeLength = GetMaxDeadEndBuriedLedgeLength();
			int maxBuriedLedgeTunnelLength = GetMaxBuriedLedgeTunnelLength();
			int replaced = 0;
			replaced += SweepLedges(grid, placements, replacement, hasHeightSnapshot, heightSnapshot, boundaryPhase: false, preserveBuriedLedgeTunnels, maxDeadEndBuriedLedgeLength, maxBuriedLedgeTunnelLength);
			replaced += ReplacePitCornerLedges(grid, placements, replacement, heightSnapshot, preserveBuriedLedgeTunnels, maxDeadEndBuriedLedgeLength, maxBuriedLedgeTunnelLength);
			// Boundary ledges are map-edge sealing pieces, not tunnel candidates.
			replaced += SweepLedges(grid, placements, replacement, hasHeightSnapshot, heightSnapshot, boundaryPhase: true, preserveBuriedLedgeTunnels: false, maxDeadEndBuriedLedgeLength: 0, maxBuriedLedgeTunnelLength: 0);

			if (replaced > 0)
				Debug.Log($"{nameof(BuildingPlacementGenerator)} replaced {replaced} buried ledge cell(s) with blocking buildings.", this);
		}

		private int SweepLedges(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			BuildingDefinition replacement,
			bool hasHeightSnapshot,
			WorldHeightSnapshot heightSnapshot,
			bool boundaryPhase,
			bool preserveBuriedLedgeTunnels,
			int maxDeadEndBuriedLedgeLength,
			int maxBuriedLedgeTunnelLength)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			var candidates = new List<Vector2Int>();

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					// Only operate on pure ledge cells. Height-change roads (ramps) keep IsLedge=true because
					// the road generator promotes the ledge in place; they must not be turned into blockers.
					if (grid[x, y].IsLedge == false || grid[x, y].IsHeightChangeRoad)
						continue;

					Vector2Int position = new Vector2Int(x, y);
					if (ShouldReplaceLedge(grid, position, hasHeightSnapshot, heightSnapshot, boundaryPhase) == false)
						continue;

					candidates.Add(position);
				}
			}

			return ReplaceCandidateLedgeGroups(grid, placements, replacement, candidates, preserveBuriedLedgeTunnels, maxDeadEndBuriedLedgeLength, maxBuriedLedgeTunnelLength);
		}

		private bool ShouldReplaceLedge(BuildingCell[,] grid, Vector2Int position, bool hasHeightSnapshot, WorldHeightSnapshot heightSnapshot, bool boundaryPhase)
		{
			bool isBoundary = false;
			if (hasHeightSnapshot && heightSnapshot.TryGetCell(position, out WorldHeightCell heightCell))
			{
				isBoundary = heightCell.IsBoundaryLedge;
			}
			else
			{
				isBoundary = IsEdgeCell(grid.GetLength(0), grid.GetLength(1), position);
			}

			// Each ledge is resolved in exactly one phase.
			if (isBoundary != boundaryPhase)
				return false;

			if (isBoundary)
				return IsBoundaryLedgeSurroundedByBlockers(grid, position);

			return IsInteriorLedgeBuriedInBlockerClump(grid, position);
		}

		private bool IsInteriorLedgeBuriedInBlockerClump(BuildingCell[,] grid, Vector2Int position)
		{
			bool touchesBlockingBuilding = false;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighborPosition = position + DirectionOffsets[i];
				if (IsInBounds(grid, neighborPosition) == false)
					return false;

				BuildingCell neighbor = grid[neighborPosition.x, neighborPosition.y];
				if (neighbor.IsRoad)
					return false;

				if (neighbor.Building != null)
				{
					if (neighbor.Building.Category != BuildingCategory.SimpleBlocking)
						return false;

					touchesBlockingBuilding = true;
					continue;
				}

				if (neighbor.IsLedge)
					continue;

				return false;
			}

			return touchesBlockingBuilding;
		}

		private bool IsBoundaryLedgeSurroundedByBlockers(BuildingCell[,] grid, Vector2Int position)
		{
			int sealedNeighborCount = 0;
			int outOfBoundsCount = 0;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighborPosition = position + DirectionOffsets[i];
				if (IsInBounds(grid, neighborPosition) == false)
				{
					outOfBoundsCount++;
					continue;
				}

				if (IsBlockingBuildingAt(grid, neighborPosition))
					sealedNeighborCount++;
			}

			return outOfBoundsCount == 1 && sealedNeighborCount == 3;
		}

		private int ReplacePitCornerLedges(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			BuildingDefinition replacement,
			WorldHeightSnapshot heightSnapshot,
			bool preserveBuriedLedgeTunnels,
			int maxDeadEndBuriedLedgeLength,
			int maxBuriedLedgeTunnelLength)
		{
			if (heightSnapshot.IsValid == false)
				return 0;

			int replaced = 0;
			bool changed;
			int guard = grid.GetLength(0) * grid.GetLength(1);

			do
			{
				changed = false;
				var candidates = new List<Vector2Int>();
				for (int x = 0; x < grid.GetLength(0); x++)
				{
					for (int y = 0; y < grid.GetLength(1); y++)
					{
						Vector2Int position = new Vector2Int(x, y);
						BuildingCell cell = grid[x, y];
						if (cell.IsLedge == false || cell.IsHeightChangeRoad)
							continue;
						if (IsPitCornerLedgeFacingBlockers(grid, heightSnapshot, position) == false)
							continue;

						candidates.Add(position);
					}
				}

				int replacedThisPass = ReplaceCandidateLedgeGroups(grid, placements, replacement, candidates, preserveBuriedLedgeTunnels, maxDeadEndBuriedLedgeLength, maxBuriedLedgeTunnelLength);
				replaced += replacedThisPass;
				changed = replacedThisPass > 0;
				guard--;
			} while (changed && guard > 0);

			return replaced;
		}

		private bool IsPitCornerLedgeFacingBlockers(BuildingCell[,] grid, WorldHeightSnapshot heightSnapshot, Vector2Int position)
		{
			if (heightSnapshot.TryGetCell(position, out WorldHeightCell cell) == false)
				return false;
			if (cell.IsLedge == false || (cell.LedgeShape != HeightTileShape.InnerCorner && cell.LedgeShape != HeightTileShape.OuterCorner))
				return false;

			RoadDirection lowSideA = (RoadDirection)((cell.LedgeRotationSteps + 2) % DirectionOffsets.Length);
			RoadDirection lowSideB = (RoadDirection)((cell.LedgeRotationSteps + 3) % DirectionOffsets.Length);
			return IsSealedForPitCornerAt(grid, heightSnapshot, position + DirectionOffsets[(int)lowSideA])
			       && IsSealedForPitCornerAt(grid, heightSnapshot, position + DirectionOffsets[(int)lowSideB]);
		}

		private int ReplaceCandidateLedgeGroups(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			BuildingDefinition replacement,
			List<Vector2Int> candidates,
			bool preserveBuriedLedgeTunnels,
			int maxDeadEndBuriedLedgeLength,
			int maxBuriedLedgeTunnelLength)
		{
			if (candidates.Count == 0)
				return 0;

			var remaining = new HashSet<Vector2Int>(candidates);
			int replaced = 0;
			while (remaining.Count > 0)
			{
				Vector2Int start = default;
				foreach (Vector2Int candidate in remaining)
				{
					start = candidate;
					break;
				}

				List<Vector2Int> group = CollectCandidateLedgeGroup(remaining, start);
				if (ShouldPreserveCandidateLedgeGroup(grid, group, preserveBuriedLedgeTunnels, maxDeadEndBuriedLedgeLength, maxBuriedLedgeTunnelLength))
					continue;

				for (int i = 0; i < group.Count; i++)
				{
					ReplaceLedgeWithBlocker(grid, placements, replacement, group[i]);
					replaced++;
				}
			}

			return replaced;
		}

		private List<Vector2Int> CollectCandidateLedgeGroup(HashSet<Vector2Int> remaining, Vector2Int start)
		{
			var group = new List<Vector2Int>();
			var queue = new Queue<Vector2Int>();
			remaining.Remove(start);
			queue.Enqueue(start);

			while (queue.Count > 0)
			{
				Vector2Int current = queue.Dequeue();
				group.Add(current);

				for (int i = 0; i < DirectionOffsets.Length; i++)
				{
					Vector2Int neighbor = current + DirectionOffsets[i];
					if (remaining.Remove(neighbor) == false)
						continue;

					queue.Enqueue(neighbor);
				}
			}

			return group;
		}

		private bool ShouldPreserveCandidateLedgeGroup(
			BuildingCell[,] grid,
			List<Vector2Int> group,
			bool preserveBuriedLedgeTunnels,
			int maxDeadEndBuriedLedgeLength,
			int maxBuriedLedgeTunnelLength)
		{
			if (group.Count <= Mathf.Max(0, maxDeadEndBuriedLedgeLength))
				return true;

			if (preserveBuriedLedgeTunnels == false || IsBuriedLedgeTunnelGroup(grid, group) == false)
				return false;

			int maxTunnelLength = Mathf.Max(0, maxBuriedLedgeTunnelLength);
			return maxTunnelLength == 0 || group.Count <= maxTunnelLength;
		}

		private bool IsBuriedLedgeTunnelGroup(BuildingCell[,] grid, List<Vector2Int> group)
		{
			var groupCells = new HashSet<Vector2Int>(group);
			var anchorPositions = new HashSet<Vector2Int>();
			var contactCells = new List<Vector2Int>();

			for (int i = 0; i < group.Count; i++)
			{
				Vector2Int position = group[i];
				for (int direction = 0; direction < DirectionOffsets.Length; direction++)
				{
					Vector2Int neighborPosition = position + DirectionOffsets[direction];
					if (groupCells.Contains(neighborPosition))
						continue;
					if (IsInBounds(grid, neighborPosition) == false)
						continue;

					bool isAnchor = IsOpenTunnelAnchorAt(grid, neighborPosition)
						|| IsOpenTunnelLedgeAnchorAt(grid, neighborPosition, groupCells);
					if (isAnchor == false)
						continue;

					if (anchorPositions.Add(neighborPosition))
						contactCells.Add(position);
				}
			}

			if (anchorPositions.Count < 2)
				return false;
			if (group.Count == 1)
				return true;

			return HasSeparatedTunnelContacts(groupCells, contactCells, Mathf.Max(1, Mathf.CeilToInt(group.Count * 0.5f)));
		}

		private bool HasSeparatedTunnelContacts(HashSet<Vector2Int> groupCells, List<Vector2Int> contactCells, int requiredDistance)
		{
			for (int i = 0; i < contactCells.Count; i++)
			{
				for (int j = i + 1; j < contactCells.Count; j++)
				{
					if (GetCandidateGroupDistance(groupCells, contactCells[i], contactCells[j], requiredDistance) >= requiredDistance)
						return true;
				}
			}

			return false;
		}

		private int GetCandidateGroupDistance(HashSet<Vector2Int> groupCells, Vector2Int start, Vector2Int target, int stopAtDistance)
		{
			if (start == target)
				return 0;

			var visited = new HashSet<Vector2Int> { start };
			var queue = new Queue<CandidateDistance>();
			queue.Enqueue(new CandidateDistance(start, 0));

			while (queue.Count > 0)
			{
				CandidateDistance current = queue.Dequeue();
				for (int i = 0; i < DirectionOffsets.Length; i++)
				{
					Vector2Int neighbor = current.Position + DirectionOffsets[i];
					if (groupCells.Contains(neighbor) == false || visited.Add(neighbor) == false)
						continue;

					int distance = current.Distance + 1;
					if (neighbor == target || distance >= stopAtDistance)
						return distance;

					queue.Enqueue(new CandidateDistance(neighbor, distance));
				}
			}

			return -1;
		}

		private bool IsOpenTunnelLedgeAnchorAt(BuildingCell[,] grid, Vector2Int position, HashSet<Vector2Int> ignoredCandidateGroup)
		{
			if (IsInBounds(grid, position) == false)
				return false;

			BuildingCell cell = grid[position.x, position.y];
			if (cell.IsLedge == false || cell.IsHeightChangeRoad)
				return false;

			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighborPosition = position + DirectionOffsets[i];
				if (ignoredCandidateGroup.Contains(neighborPosition))
					continue;
				if (IsOpenTunnelAnchorAt(grid, neighborPosition))
					return true;
			}

			return false;
		}

		private bool IsOpenTunnelAnchorAt(BuildingCell[,] grid, Vector2Int position)
		{
			if (IsInBounds(grid, position) == false)
				return false;

			BuildingCell cell = grid[position.x, position.y];
			if (cell.IsHeightChangeRoad)
				return true;
			if (cell.IsRoad)
				return true;
			if (cell.Building != null)
				return cell.Building.Category != BuildingCategory.SimpleBlocking;

			return cell.IsLedge == false;
		}

		private bool GetPreserveBuriedLedgeTunnels()
		{
			if (_hasRuntimeLedgeTunnelPruningSettings)
				return _runtimePreserveBuriedLedgeTunnels;

			return Settings != null && Settings.PreserveBuriedLedgeTunnels;
		}

		private int GetMaxDeadEndBuriedLedgeLength()
		{
			if (_hasRuntimeLedgeTunnelPruningSettings)
				return Mathf.Max(0, _runtimeMaxDeadEndBuriedLedgeLength);

			return Settings != null ? Mathf.Max(0, Settings.MaxDeadEndBuriedLedgeLength) : 0;
		}

		private int GetMaxBuriedLedgeTunnelLength()
		{
			if (_hasRuntimeLedgeTunnelPruningSettings)
				return Mathf.Max(0, _runtimeMaxBuriedLedgeTunnelLength);

			return Settings != null ? Mathf.Max(0, Settings.MaxBuriedLedgeTunnelLength) : 0;
		}

		private void ReplaceLedgeWithBlocker(BuildingCell[,] grid, List<PlacedBuilding> placements, BuildingDefinition replacement, Vector2Int position)
		{
			grid[position.x, position.y].IsLedge = false;
			grid[position.x, position.y].Building = replacement;
			grid[position.x, position.y].BuildingOrigin = position;

			placements.Add(new PlacedBuilding(replacement, position, Vector2Int.one, 0));

			if (RoadGenerator != null && RoadGenerator.HeightGenerator != null)
				RoadGenerator.HeightGenerator.SuppressLedgeAt(position);
		}

		private bool IsBlockingBuildingAt(BuildingCell[,] grid, Vector2Int position)
		{
			if (IsInBounds(grid, position) == false)
				return false;

			BuildingDefinition building = grid[position.x, position.y].Building;
			return building != null && building.Category == BuildingCategory.SimpleBlocking;
		}

		private bool IsSealedForPitCornerAt(BuildingCell[,] grid, WorldHeightSnapshot heightSnapshot, Vector2Int position)
		{
			if (IsBlockingBuildingAt(grid, position))
				return true;
			if (IsInBounds(grid, position) == false)
				return false;
			if (heightSnapshot.TryGetCell(position, out WorldHeightCell cell) == false)
				return false;

			return cell.IsLedge && cell.IsBoundaryLedge && grid[position.x, position.y].IsHeightChangeRoad == false;
		}

		private BuildingDefinition FindLedgeReplacementBlocker()
		{
			BuildingSet buildingSet = Settings.BuildingSet;
			if (buildingSet == null || buildingSet.Buildings == null)
				return null;

			BuildingDefinition fallback = null;
			for (int i = 0; i < buildingSet.Buildings.Length; i++)
			{
				BuildingDefinition definition = buildingSet.Buildings[i];
				if (definition == null || definition.Category != BuildingCategory.SimpleBlocking)
					continue;

				Vector2Int size = definition.GetRotatedFootprintSize(0);
				if (size.x == 1 && size.y == 1 && HasRoadRequirement(definition) == false)
					return definition;

				if (fallback == null && size.x == 1 && size.y == 1)
					fallback = definition;
			}

			return fallback;
		}

		private void FillRemainingBlockers(BuildingCell[,] grid, List<PlacedBuilding> placements, System.Random random)
		{
			int[] areas = { 4, 2, 1 };
			for (int i = 0; i < areas.Length; i++)
			{
				int guard = grid.GetLength(0) * grid.GetLength(1);
				while (guard > 0 && TryPlaceBestBuilding(grid, placements, BuildingCategory.SimpleBlocking, false, false, false, true, random, areas[i]))
				{
					guard--;
				}
			}
		}

		private bool TryPlaceBestBuilding(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			BuildingCategory category,
			bool requireEdgeCell,
			bool requireAdjacentRoad,
			bool checkSideRequirements,
			bool allowCooldownFallback,
			System.Random random,
			int requiredArea = 0)
		{
			List<BuildingPlacementCandidate> candidates = CollectCandidates(grid, placements, category, requireEdgeCell, requireAdjacentRoad, checkSideRequirements, true, requiredArea);
			if (candidates.Count == 0 && allowCooldownFallback)
				candidates = CollectCandidates(grid, placements, category, requireEdgeCell, requireAdjacentRoad, checkSideRequirements, false, requiredArea);

			if (candidates.Count == 0)
				return false;

			List<BuildingPlacementCandidate> exactFitCandidates = FilterExactFitCandidates(candidates);
			if (exactFitCandidates.Count > 0)
				candidates = exactFitCandidates;

			BuildingPlacementCandidate candidate = ChooseWeighted(candidates, random);
			PlaceBuilding(grid, placements, candidate);
			return true;
		}

		private List<BuildingPlacementCandidate> CollectCandidates(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			BuildingCategory category,
			bool requireEdgeCell,
			bool requireAdjacentRoad,
			bool checkSideRequirements,
			bool respectCooldown,
			int requiredArea)
		{
			var candidates = new List<BuildingPlacementCandidate>();
			BuildingSet buildingSet = Settings.BuildingSet;
			if (buildingSet == null || buildingSet.Buildings == null)
				return candidates;

			for (int i = 0; i < buildingSet.Buildings.Length; i++)
			{
				BuildingDefinition definition = buildingSet.Buildings[i];
				if (definition == null || definition.Category != category)
					continue;

				for (int rotationSteps = 0; rotationSteps < 4; rotationSteps++)
				{
					Vector2Int size = definition.GetRotatedFootprintSize(rotationSteps);
					if (requiredArea > 0 && size.x * size.y != requiredArea)
						continue;

					for (int x = 0; x <= grid.GetLength(0) - size.x; x++)
					{
						for (int y = 0; y <= grid.GetLength(1) - size.y; y++)
						{
							var candidate = new BuildingPlacementCandidate(definition, new Vector2Int(x, y), size, rotationSteps);
							if (IsCandidateValid(grid, placements, ref candidate, requireEdgeCell, requireAdjacentRoad, checkSideRequirements, respectCooldown))
								candidates.Add(candidate);
						}
					}
				}
			}

			return candidates;
		}

		private List<BuildingPlacementCandidate> FilterExactFitCandidates(List<BuildingPlacementCandidate> candidates)
		{
			var exact = new List<BuildingPlacementCandidate>();
			for (int i = 0; i < candidates.Count; i++)
			{
				if (candidates[i].ExactEmptyRegionFit)
					exact.Add(candidates[i]);
			}

			return exact;
		}

		private bool IsCandidateValid(
			BuildingCell[,] grid,
			List<PlacedBuilding> placements,
			ref BuildingPlacementCandidate candidate,
			bool requireEdgeCell,
			bool requireAdjacentRoad,
			bool checkSideRequirements,
			bool respectCooldown)
		{
			if (IsFootprintEmpty(grid, candidate) == false)
				return false;

			if (IsFootprintHeightValid(grid, candidate) == false)
				return false;

			if (IsEnvironmentValid(grid, candidate) == false)
				return false;

			if (requireEdgeCell && ContainsEdgeCell(grid, candidate) == false)
				return false;

			if (IsEdgeOnlySimpleBlocker(candidate) && ContainsEdgeCell(grid, candidate) == false)
				return false;

			if (requireAdjacentRoad && HasAdjacentRoad(grid, candidate) == false)
				return false;

			if (checkSideRequirements && AreSideRequirementsMet(grid, candidate) == false)
				return false;

			if (respectCooldown && IsRepeatCooldownMet(candidate, placements) == false)
				return false;

			candidate.ExactEmptyRegionFit = GetEmptyRegionSize(grid, candidate.Origin) == candidate.Area;
			return true;
		}

		private bool IsEdgeOnlySimpleBlocker(BuildingPlacementCandidate candidate)
		{
			return candidate.Definition.Category == BuildingCategory.SimpleBlocking
				&& HasRoadRequirement(candidate.Definition);
		}

		private bool HasRoadRequirement(BuildingDefinition definition)
		{
			return definition.North == BuildingSideRequirement.RequiresRoad
				|| definition.East == BuildingSideRequirement.RequiresRoad
				|| definition.South == BuildingSideRequirement.RequiresRoad
				|| definition.West == BuildingSideRequirement.RequiresRoad;
		}

		private bool IsFootprintEmpty(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
				{
					if (grid[x, y].IsOccupied)
						return false;
				}
			}

			return true;
		}

		private bool IsFootprintHeightValid(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			int heightLevel = grid[candidate.Origin.x, candidate.Origin.y].HeightLevel;
			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
				{
					if (grid[x, y].HeightLevel != heightLevel)
						return false;
				}
			}

			return true;
		}

		private bool IsEnvironmentValid(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
				{
					if (grid[x, y].Environment != candidate.Definition.Environment)
						return false;
				}
			}

			return true;
		}

		private bool ContainsEdgeCell(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
				{
					if (IsEdgeCell(width, height, new Vector2Int(x, y)))
						return true;
				}
			}

			return false;
		}

		private bool HasAdjacentRoad(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			int heightLevel = grid[candidate.Origin.x, candidate.Origin.y].HeightLevel;
			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				if (IsRoadAtHeight(grid, new Vector2Int(x, candidate.Origin.y - 1), heightLevel) || IsRoadAtHeight(grid, new Vector2Int(x, candidate.Origin.y + candidate.Size.y), heightLevel))
					return true;
			}

			for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
			{
				if (IsRoadAtHeight(grid, new Vector2Int(candidate.Origin.x - 1, y), heightLevel) || IsRoadAtHeight(grid, new Vector2Int(candidate.Origin.x + candidate.Size.x, y), heightLevel))
					return true;
			}

			return false;
		}

		private bool AreSideRequirementsMet(BuildingCell[,] grid, BuildingPlacementCandidate candidate)
		{
			return IsSideRequirementMet(grid, candidate, RoadDirection.North)
				&& IsSideRequirementMet(grid, candidate, RoadDirection.East)
				&& IsSideRequirementMet(grid, candidate, RoadDirection.South)
				&& IsSideRequirementMet(grid, candidate, RoadDirection.West);
		}

		private bool IsSideRequirementMet(BuildingCell[,] grid, BuildingPlacementCandidate candidate, RoadDirection direction)
		{
			BuildingSideRequirement requirement = candidate.Definition.GetRotatedRequirement(direction, candidate.RotationSteps);
			if (requirement == BuildingSideRequirement.Any)
				return true;

			List<Vector2Int> sideCells = GetSideNeighborCells(candidate, direction);
			int heightLevel = grid[candidate.Origin.x, candidate.Origin.y].HeightLevel;
			for (int i = 0; i < sideCells.Count; i++)
			{
				bool road = IsRoadAtHeight(grid, sideCells[i], heightLevel);
				if (requirement == BuildingSideRequirement.RequiresRoad && road == false)
					return false;

				if (requirement == BuildingSideRequirement.RequiresNoRoad && road)
					return false;

				if (requirement == BuildingSideRequirement.RequiresLedgeDown && IsLedgeRequirementMet(grid, candidate, sideCells[i], true) == false)
					return false;

				if (requirement == BuildingSideRequirement.RequiresLedgeUp && IsLedgeRequirementMet(grid, candidate, sideCells[i], false) == false)
					return false;

				bool complexBuilding = IsBuildingCategory(grid, sideCells[i], BuildingCategory.Complex);
				if (requirement == BuildingSideRequirement.RequiresComplexBuilding && complexBuilding == false)
					return false;

				if (requirement == BuildingSideRequirement.RequiresNoComplexBuilding && complexBuilding)
					return false;

				bool blockingBuilding = IsBuildingCategory(grid, sideCells[i], BuildingCategory.SimpleBlocking);
				if (requirement == BuildingSideRequirement.RequiresBlockingBuilding && blockingBuilding == false)
					return false;

				if (requirement == BuildingSideRequirement.RequiresNoBlockingBuilding && blockingBuilding)
					return false;
			}

			return true;
		}

		private bool IsBuildingCategory(BuildingCell[,] grid, Vector2Int position, BuildingCategory category)
		{
			if (IsInBounds(grid, position) == false)
				return false;

			BuildingDefinition building = grid[position.x, position.y].Building;
			return building != null && building.Category == category;
		}

		private bool IsLedgeRequirementMet(BuildingCell[,] grid, BuildingPlacementCandidate candidate, Vector2Int ledgePosition, bool requireDown)
		{
			if (IsInBounds(grid, ledgePosition) == false || grid[ledgePosition.x, ledgePosition.y].IsLedge == false)
				return false;

			int buildingHeight = grid[candidate.Origin.x, candidate.Origin.y].HeightLevel;
			int ledgeHeight = grid[ledgePosition.x, ledgePosition.y].HeightLevel;
			return requireDown ? buildingHeight > ledgeHeight : buildingHeight <= ledgeHeight;
		}

		private List<Vector2Int> GetSideNeighborCells(BuildingPlacementCandidate candidate, RoadDirection direction)
		{
			var cells = new List<Vector2Int>();
			switch (direction)
			{
				case RoadDirection.North:
					for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
						cells.Add(new Vector2Int(x, candidate.Origin.y + candidate.Size.y));
					break;
				case RoadDirection.East:
					for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
						cells.Add(new Vector2Int(candidate.Origin.x + candidate.Size.x, y));
					break;
				case RoadDirection.South:
					for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
						cells.Add(new Vector2Int(x, candidate.Origin.y - 1));
					break;
				case RoadDirection.West:
					for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
						cells.Add(new Vector2Int(candidate.Origin.x - 1, y));
					break;
			}

			return cells;
		}

		private bool IsRepeatCooldownMet(BuildingPlacementCandidate candidate, List<PlacedBuilding> placements)
		{
			int cooldown = Mathf.Max(Settings.RepeatCooldownDistance, candidate.Definition.RepeatCooldownDistance);
			if (cooldown <= 0)
				return true;

			for (int i = 0; i < placements.Count; i++)
			{
				PlacedBuilding placed = placements[i];
				if (placed.Definition != candidate.Definition)
					continue;

				if (GetFootprintDistance(candidate.Origin, candidate.Size, placed.Origin, placed.Size) <= cooldown)
					return false;
			}

			return true;
		}

		private int GetFootprintDistance(Vector2Int aOrigin, Vector2Int aSize, Vector2Int bOrigin, Vector2Int bSize)
		{
			int aMaxX = aOrigin.x + aSize.x - 1;
			int aMaxY = aOrigin.y + aSize.y - 1;
			int bMaxX = bOrigin.x + bSize.x - 1;
			int bMaxY = bOrigin.y + bSize.y - 1;

			int dx = aMaxX < bOrigin.x ? bOrigin.x - aMaxX : bMaxX < aOrigin.x ? aOrigin.x - bMaxX : 0;
			int dy = aMaxY < bOrigin.y ? bOrigin.y - aMaxY : bMaxY < aOrigin.y ? aOrigin.y - bMaxY : 0;
			return dx + dy;
		}

		private int GetEmptyRegionSize(BuildingCell[,] grid, Vector2Int start)
		{
			if (IsInBounds(grid, start) == false || grid[start.x, start.y].IsOccupied)
				return 0;

			var visited = new HashSet<Vector2Int>();
			var open = new Queue<Vector2Int>();
			open.Enqueue(start);
			visited.Add(start);

			while (open.Count > 0)
			{
				Vector2Int current = open.Dequeue();
				for (int i = 0; i < DirectionOffsets.Length; i++)
				{
					Vector2Int next = current + DirectionOffsets[i];
					if (IsInBounds(grid, next) == false || visited.Contains(next) || grid[next.x, next.y].IsOccupied)
						continue;

					visited.Add(next);
					open.Enqueue(next);
				}
			}

			return visited.Count;
		}

		private BuildingPlacementCandidate ChooseWeighted(List<BuildingPlacementCandidate> candidates, System.Random random)
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

		private void PlaceBuilding(BuildingCell[,] grid, List<PlacedBuilding> placements, BuildingPlacementCandidate candidate)
		{
			var placed = new PlacedBuilding(candidate.Definition, candidate.Origin, candidate.Size, candidate.RotationSteps);
			placements.Add(placed);

			for (int x = candidate.Origin.x; x < candidate.Origin.x + candidate.Size.x; x++)
			{
				for (int y = candidate.Origin.y; y < candidate.Origin.y + candidate.Size.y; y++)
				{
					grid[x, y].Building = candidate.Definition;
					grid[x, y].BuildingOrigin = candidate.Origin;
				}
			}
		}

		private void InstantiateBuildings(WorldGridSnapshot roadGrid, List<PlacedBuilding> placements)
		{
			var root = new GameObject(GeneratedRootName);
			root.transform.SetParent(transform, false);
			_generatedRoot = root.transform;

			for (int i = 0; i < placements.Count; i++)
			{
				PlacedBuilding placed = placements[i];
				Vector3 position = GetFootprintCenterWorld(roadGrid, placed.Origin, placed.Size);
				Quaternion rotation = Quaternion.Euler(0f, placed.RotationSteps * 90f, 0f);

				GameObject instance;
				if (placed.Definition != null && placed.Definition.gameObject != null)
				{
					position = GetBuildingWorldPosition(placed, position);
					instance = Instantiate(placed.Definition.gameObject, position, rotation, root.transform);
				}
				else
				{
					instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
					instance.transform.SetParent(root.transform, false);
					instance.transform.position = position;
					instance.transform.rotation = rotation;
					instance.transform.localScale = new Vector3(placed.Size.x * roadGrid.TileSize, roadGrid.TileSize, placed.Size.y * roadGrid.TileSize);
				}

				instance.name = placed.Definition != null ? $"{placed.Definition.name} ({placed.Origin.x},{placed.Origin.y})" : $"Missing Building ({placed.Origin.x},{placed.Origin.y})";
			}
		}

		private void SchedulePostGenerateWork()
		{
			if (Application.isPlaying == false)
			{
				if (RebuildNavMeshAfterGenerate)
					RebuildNavMesh();

				IsGenerationComplete = true;
				return;
			}

			if (_navMeshRebuildCoroutine != null)
				StopCoroutine(_navMeshRebuildCoroutine);

			_navMeshRebuildCoroutine = StartCoroutine(FinishGenerateAfterSceneSettles());
		}

		private IEnumerator FinishGenerateAfterSceneSettles()
		{
			yield return null;

			Physics.SyncTransforms();
			if (RebuildNavMeshAfterGenerate)
				RebuildNavMesh();

			IsGenerationComplete = true;
			_navMeshRebuildCoroutine = null;
		}

		private void RebuildNavMesh()
		{
			if (NavMeshSurface == null && FindNavMeshSurfaceIfMissing)
				NavMeshSurface = GetComponent<NavMeshSurface>() ?? FindObjectOfType<NavMeshSurface>();

			if (NavMeshSurface == null)
			{
				Debug.LogWarning($"{nameof(BuildingPlacementGenerator)} could not rebuild NavMesh because no {nameof(NavMeshSurface)} was assigned or found.", this);
				return;
			}

			NavMeshSurface.BuildNavMesh();
		}

		private void SpawnLoot()
		{
			if (SpawnLootAfterGenerate == false)
				return;

			if (LootSpawner == null)
				LootSpawner = GetComponent<WorldLootSpawner>();

			if (LootSpawner == null && FindLootSpawnerIfMissing)
				LootSpawner = FindObjectOfType<WorldLootSpawner>();

			if (LootSpawner == null)
				return;

			LootSpawner.BuildingGenerator = this;
			LootSpawner.SpawnLoot();
		}

		private Vector3 GetBuildingWorldPosition(PlacedBuilding placed, Vector3 footprintCenter)
		{
			if (placed.Definition != null
			    && placed.Definition.Category == BuildingCategory.SimpleBlocking
			    && placed.Definition.gameObject != null)
			{
				footprintCenter.y += placed.Definition.transform.position.y;
			}

			return footprintCenter;
		}

		private Vector3 GetFootprintCenterWorld(WorldGridSnapshot roadGrid, Vector2Int origin, Vector2Int size)
		{
			var center = new Vector2(origin.x + (size.x - 1) * 0.5f, origin.y + (size.y - 1) * 0.5f);
			int heightLevel = 0;
			if (roadGrid.TryGetCell(origin, out WorldGridCell cell))
				heightLevel = cell.HeightLevel;

			return roadGrid.CellToWorld(center, heightLevel);
		}

		private int CountEmptyCells(BuildingCell[,] grid)
		{
			int count = 0;
			for (int x = 0; x < grid.GetLength(0); x++)
			{
				for (int y = 0; y < grid.GetLength(1); y++)
				{
					if (grid[x, y].IsOccupied == false)
						count++;
				}
			}

			return count;
		}

		private bool HasEmptyEdgeCell(BuildingCell[,] grid)
		{
			int width = grid.GetLength(0);
			int height = grid.GetLength(1);
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					if (IsEdgeCell(width, height, new Vector2Int(x, y)) && grid[x, y].IsOccupied == false)
						return true;
				}
			}

			return false;
		}

		private bool IsRoad(BuildingCell[,] grid, Vector2Int position)
		{
			return IsInBounds(grid, position) && grid[position.x, position.y].IsRoad;
		}

		private bool IsRoadAtHeight(BuildingCell[,] grid, Vector2Int position, int heightLevel)
		{
			return IsInBounds(grid, position) && grid[position.x, position.y].IsRoad && grid[position.x, position.y].HeightLevel == heightLevel;
		}

		private bool IsInBounds(BuildingCell[,] grid, Vector2Int position)
		{
			return position.x >= 0 && position.y >= 0 && position.x < grid.GetLength(0) && position.y < grid.GetLength(1);
		}

		private bool IsEdgeCell(int width, int height, Vector2Int position)
		{
			return position.x == 0 || position.y == 0 || position.x == width - 1 || position.y == height - 1;
		}

		private void OnDrawGizmos()
		{
			if (DrawGizmos == false || _lastRoadGrid.IsValid == false)
				return;

			for (int i = 0; i < _lastPlacements.Count; i++)
			{
				PlacedBuilding placed = _lastPlacements[i];
				Gizmos.color = placed.Definition != null && placed.Definition.Category == BuildingCategory.Complex ? ComplexBuildingGizmoColor : SimpleBlockingGizmoColor;
				Vector3 position = GetFootprintCenterWorld(_lastRoadGrid, placed.Origin, placed.Size) + Vector3.up * 0.15f;
				Gizmos.DrawCube(position, new Vector3(placed.Size.x * _lastRoadGrid.TileSize * 0.8f, 0.25f, placed.Size.y * _lastRoadGrid.TileSize * 0.8f));
			}
		}

		private struct BuildingCell
		{
			public Vector2Int Position;
			public int HeightLevel;
			public bool IsRoad;
			public bool IsBoundaryExit;
			public bool IsLedge;
			public bool IsHeightChangeRoad;
			public RoadEnvironment Environment;
			public BuildingDefinition Building;
			public Vector2Int BuildingOrigin;
			public bool IsOccupied => IsRoad || IsLedge || IsHeightChangeRoad || Building != null;
		}

		private readonly struct CandidateDistance
		{
			public readonly Vector2Int Position;
			public readonly int Distance;

			public CandidateDistance(Vector2Int position, int distance)
			{
				Position = position;
				Distance = distance;
			}
		}

		private struct BuildingPlacementCandidate
		{
			public readonly BuildingDefinition Definition;
			public readonly Vector2Int Origin;
			public readonly Vector2Int Size;
			public readonly int RotationSteps;
			public bool ExactEmptyRegionFit;
			public int Area => Size.x * Size.y;

			public BuildingPlacementCandidate(BuildingDefinition definition, Vector2Int origin, Vector2Int size, int rotationSteps)
			{
				Definition = definition;
				Origin = origin;
				Size = size;
				RotationSteps = rotationSteps;
				ExactEmptyRegionFit = false;
			}
		}

		private readonly struct PlacedBuilding
		{
			public readonly BuildingDefinition Definition;
			public readonly Vector2Int Origin;
			public readonly Vector2Int Size;
			public readonly int RotationSteps;

			public PlacedBuilding(BuildingDefinition definition, Vector2Int origin, Vector2Int size, int rotationSteps)
			{
				Definition = definition;
				Origin = origin;
				Size = size;
				RotationSteps = rotationSteps;
			}
		}
	}
}
