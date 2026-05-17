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
			if (layerCount <= 1)
				return heights;

			var random = new System.Random(Seed);
			bool splitAlongX = random.Next(2) == 0;
			int length = splitAlongX ? width : height;
			int minBand = Mathf.Max(
				Settings.MinCellsBetweenHeightChanges + 2,
				splitAlongX ? Settings.MinUsableRegionWidth : Settings.MinUsableRegionHeight);

			List<int> sequence = BuildHeightSequence(layerCount, length, minBand, random);
			List<int> bandSizes = BuildBandSizes(length, sequence.Count, minBand, random);
			int[] boundaries = BuildBoundaries(bandSizes);

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					int axis = splitAlongX ? x : y;
					int band = GetBandIndex(axis, boundaries);
					heights[x, y] = sequence[Mathf.Clamp(band, 0, sequence.Count - 1)];
				}
			}

			return heights;
		}

		private List<int> BuildHeightSequence(int layerCount, int length, int minBand, System.Random random)
		{
			int maxBands = Mathf.Max(1, length / Mathf.Max(1, minBand));
			int targetBands = Mathf.Clamp(layerCount, 1, maxBands);

			if (layerCount == 2 && maxBands >= 3 && random.NextDouble() > Settings.RegionBalance)
				targetBands = 3;

			var sequence = new List<int>(targetBands);
			int current = layerCount == 2 && targetBands == 3 ? 1 : random.Next(2) == 0 ? 0 : layerCount - 1;
			sequence.Add(current);

			for (int i = 1; i < targetBands; i++)
			{
				int direction;
				if (current <= 0)
					direction = 1;
				else if (current >= layerCount - 1)
					direction = -1;
				else if (layerCount > 2 && targetBands == layerCount)
					direction = sequence[0] == 0 ? 1 : -1;
				else
					direction = random.Next(2) == 0 ? -1 : 1;

				current = Mathf.Clamp(current + direction, 0, layerCount - 1);
				sequence.Add(current);
			}

			return sequence;
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

		private WorldHeightCell[,] BuildHeightCells(int[,] heights)
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
					bool replaceable = isLedge && isBoundary == false && shape == HeightTileShape.Straight && HasRoadReplaceableSides(heights, position, highDirection, currentHeight, highHeight);

					cells[x, y] = new WorldHeightCell(
						position,
						currentHeight,
						isLedge,
						isBoundary,
						replaceable,
						shape,
						highDirection,
						currentHeight,
						highHeight);
				}
			}

			if (Settings.MinRoadReplaceableLedgesPerHeightRegion > 0 && HasEnoughRoadReplaceableLedges(cells) == false)
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

			highDirection = RoadDirection.North;
			highHeight = currentHeight;
			return false;
		}

		private HeightTileShape GetLedgeShape(int[,] heights, Vector2Int position, int currentHeight)
		{
			int highNeighborCount = 0;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighbor = position + DirectionOffsets[i];
				if (IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] > currentHeight)
					highNeighborCount++;
			}

			return highNeighborCount <= 1 ? HeightTileShape.Straight : HeightTileShape.InnerCorner;
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
			for (int x = 0; x < cells.GetLength(0); x++)
			{
				for (int y = 0; y < cells.GetLength(1); y++)
				{
					if (cells[x, y].CanBeReplacedByHeightChangeRoad)
					{
						int highLevel = cells[x, y].HighHeightLevel;
						countsByHighLevel.TryGetValue(highLevel, out int count);
						countsByHighLevel[highLevel] = count + 1;
					}
				}
			}

			int required = Mathf.Max(0, Settings.MinRoadReplaceableLedgesPerHeightRegion);
			for (int level = 1; level < Mathf.Max(1, Settings.HeightLayerCount); level++)
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
			int placementIndex = 0;

			for (int x = 0; x < cells.GetLength(0); x++)
			{
				for (int y = 0; y < cells.GetLength(1); y++)
				{
					WorldHeightCell cell = cells[x, y];
					if (cell.IsLedge == false)
						continue;

					HeightTileCandidate tile = ChooseTile(cell, recentPlacements, placementIndex, random);
					Vector3 position = CellToWorld(cell.Position, (cell.LowHeightLevel + cell.HighHeightLevel) * 0.5f);
					Quaternion rotation = tile.IsValid ? Quaternion.Euler(0f, tile.YRotationDegrees, 0f) : Quaternion.identity;

					GameObject instance;
					if (tile.IsValid && tile.Definition.gameObject != null)
					{
						instance = Instantiate(tile.Definition.gameObject, position, rotation, root.transform);
					}
					else
					{
						instance = new GameObject($"Missing Height Tile {cell.Position.x},{cell.Position.y}");
						instance.transform.SetParent(root.transform, false);
						instance.transform.position = position;
						instance.transform.rotation = rotation;
					}

					instance.name = tile.IsValid ? $"{tile.Definition.name} ({cell.Position.x},{cell.Position.y})" : instance.name;
					_ledgeInstances[cell.Position] = instance;

					if (tile.IsValid)
						recentPlacements[tile.Definition] = placementIndex;

					placementIndex++;
				}
			}
		}

		private HeightTileCandidate ChooseTile(WorldHeightCell cell, Dictionary<HeightTileDefinition, int> recentPlacements, int placementIndex, System.Random random)
		{
			HeightTileSet tileSet = Settings.LedgeTiles;
			if (tileSet == null || tileSet.Tiles == null || tileSet.Tiles.Length == 0)
				return default;

			var candidates = new List<HeightTileCandidate>();
			var cooldownCandidates = new List<HeightTileCandidate>();

			for (int i = 0; i < tileSet.Tiles.Length; i++)
			{
				HeightTileDefinition tile = tileSet.Tiles[i];
				if (tile == null || tile.Shape != cell.LedgeShape || tile.IsBoundaryTile != cell.IsBoundaryLedge)
					continue;

				foreach (int rotationSteps in GetAllowedRotationSteps(cell))
				{
					var candidate = new HeightTileCandidate(tile, rotationSteps);
					candidates.Add(candidate);

					int cooldown = tile.RepeatCooldownDistance > 0 ? tile.RepeatCooldownDistance : Mathf.Max(0, Settings.DefaultLedgeRepeatCooldownDistance);
					if (recentPlacements.TryGetValue(tile, out int lastPlacement) == false || placementIndex - lastPlacement > cooldown)
						cooldownCandidates.Add(candidate);
				}
			}

			List<HeightTileCandidate> pool = cooldownCandidates.Count > 0 ? cooldownCandidates : candidates;
			if (pool.Count == 0)
				return default;

			return ChooseWeighted(pool, random);
		}

		private static IEnumerable<int> GetAllowedRotationSteps(WorldHeightCell cell)
		{
			// Prefab default orientation: higher elevation faces North (rotationSteps 0).
			// rotationSteps = (int)HighDirection rotates the tile so the high side faces the cell's actual high neighbor.
			if (cell.LedgeShape == HeightTileShape.Straight)
			{
				yield return (int)cell.HighDirection;
				yield break;
			}

			// Corner shapes need a pair of high directions to pick a single rotation.
			// The current generator does not produce corner ledges yet, so until the cell carries
			// pair info, allow all rotations and accept that corners may face a random direction.
			for (int rotationSteps = 0; rotationSteps < 4; rotationSteps++)
				yield return rotationSteps;
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
