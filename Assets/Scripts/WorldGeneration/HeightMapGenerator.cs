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
			int attempts = Mathf.Max(1, Settings.MaxGenerationAttempts);
			for (int attempt = 0; attempt < attempts; attempt++)
			{
				int[,] candidate = GenerateOrganicHeightLevels(width, height, layerCount, random, attempt);
				ApplySmoothing(candidate, layerCount);
				CullUnusableRegions(candidate);
				EnforceHeightDifferenceRule(candidate, layerCount);
				EnforceMapEdgeHeightContinuity(candidate);
				EnforceMinimumDistanceBetweenHeightChanges(candidate, layerCount);
				CullUnusableRegions(candidate);
				EnforceMapEdgeHeightContinuity(candidate);
				EnforceHeightDifferenceRule(candidate, layerCount);

				if (Settings.MinRoadReplaceableLedgesPerHeightRegion <= 0 || HasEnoughRoadReplaceableLedges(BuildHeightCells(candidate, false)))
					return candidate;
			}

			return GenerateBandHeightLevels(width, height, layerCount, random);
		}

		private int[,] GenerateBandHeightLevels(int width, int height, int layerCount, System.Random random)
		{
			int[,] heights = new int[width, height];
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

		private int[,] GenerateOrganicHeightLevels(int width, int height, int layerCount, System.Random random, int attempt)
		{
			int[,] heights = new int[width, height];
			List<HeightSeed> seeds = BuildHeightSeeds(width, height, layerCount, random);

			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					float warpedX = x + (Hash01(x, y, Seed + attempt * 37) - 0.5f) * 1.75f;
					float warpedY = y + (Hash01(x + 13, y - 7, Seed ^ (attempt * 7919)) - 0.5f) * 1.75f;
					float bestScore = float.MaxValue;
					int bestHeight = 0;

					for (int i = 0; i < seeds.Count; i++)
					{
						HeightSeed seed = seeds[i];
						float dx = warpedX - seed.Position.x;
						float dy = warpedY - seed.Position.y;
						float score = (dx * dx + dy * dy) * seed.Weight;
						if (score >= bestScore)
							continue;

						bestScore = score;
						bestHeight = seed.HeightLevel;
					}

					heights[x, y] = bestHeight;
				}
			}

			return heights;
		}

		private List<HeightSeed> BuildHeightSeeds(int width, int height, int layerCount, System.Random random)
		{
			float balance = Mathf.Clamp01(Settings.RegionBalance);
			int extraSeeds = Mathf.RoundToInt(Mathf.Lerp(layerCount * 3f, 1f, balance));
			int seedCount = Mathf.Clamp(layerCount + extraSeeds, layerCount, Mathf.Min(width * height, layerCount * 4 + 4));
			var seeds = new List<HeightSeed>(seedCount);

			for (int level = 0; level < layerCount; level++)
				seeds.Add(CreateHeightSeed(width, height, level, random));

			for (int i = seeds.Count; i < seedCount; i++)
				seeds.Add(CreateHeightSeed(width, height, random.Next(layerCount), random));

			return seeds;
		}

		private HeightSeed CreateHeightSeed(int width, int height, int heightLevel, System.Random random)
		{
			return new HeightSeed(
				new Vector2Int(random.Next(width), random.Next(height)),
				heightLevel,
				Mathf.Lerp(0.75f, 1.35f, (float)random.NextDouble()));
		}

		private void ApplySmoothing(int[,] heights, int layerCount)
		{
			int passes = Mathf.Max(0, Settings.SmoothingPasses);
			for (int pass = 0; pass < passes; pass++)
			{
				int width = heights.GetLength(0);
				int height = heights.GetLength(1);
				int[,] next = (int[,])heights.Clone();

				for (int x = 0; x < width; x++)
				{
					for (int y = 0; y < height; y++)
					{
						int[] counts = new int[layerCount];
						for (int i = 0; i < NeighborOffsets.Length; i++)
						{
							Vector2Int neighbor = new Vector2Int(x, y) + NeighborOffsets[i];
							if (IsInBounds(heights, neighbor))
								counts[heights[neighbor.x, neighbor.y]]++;
						}

						int bestHeight = heights[x, y];
						int bestCount = 0;
						for (int level = 0; level < counts.Length; level++)
						{
							if (counts[level] > bestCount)
							{
								bestCount = counts[level];
								bestHeight = level;
							}
						}

						if (bestCount >= 5)
							next[x, y] = bestHeight;
					}
				}

				CopyHeights(next, heights);
			}
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
			int highNeighborCount = 0;
			for (int i = 0; i < DirectionOffsets.Length; i++)
			{
				Vector2Int neighbor = position + DirectionOffsets[i];
				if (IsInBounds(heights, neighbor) && heights[neighbor.x, neighbor.y] > currentHeight)
					highNeighborCount++;
			}

			if (highNeighborCount == 0 && HasHigherDiagonalNeighbor(heights, position, currentHeight))
				return HeightTileShape.OuterCorner;

			return highNeighborCount <= 1 ? HeightTileShape.Straight : HeightTileShape.InnerCorner;
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

		private readonly struct HeightSeed
		{
			public readonly Vector2Int Position;
			public readonly int HeightLevel;
			public readonly float Weight;

			public HeightSeed(Vector2Int position, int heightLevel, float weight)
			{
				Position = position;
				HeightLevel = heightLevel;
				Weight = weight;
			}
		}
	}
}
