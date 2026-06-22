using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	[System.Flags]
	public enum ZombieClimbSurfaceUsage
	{
		None = 0,
		TerrainShortcut = 1 << 0,
		Rescue = 1 << 1,
		All = TerrainShortcut | Rescue,
	}

	public readonly struct ZombieClimbSurface
	{
		public readonly ZombieClimbSurfaceUsage Usage;
		public readonly Vector3 Center;
		public readonly Vector3 Axis;
		public readonly Vector3 ClimbDirection;
		public readonly float HalfLength;
		public readonly float BaseY;
		public readonly float TopY;
		public readonly float LandingInset;
		public readonly float ShortcutMinPathSavings;
		public readonly Object Owner;

		public bool IsValid => Usage != ZombieClimbSurfaceUsage.None &&
		                       HalfLength > 0.05f &&
		                       TopY > BaseY + 0.05f &&
		                       Axis.sqrMagnitude > 0.5f &&
		                       ClimbDirection.sqrMagnitude > 0.5f;

		public ZombieClimbSurface(
			ZombieClimbSurfaceUsage usage,
			Vector3 center,
			Vector3 axis,
			Vector3 climbDirection,
			float halfLength,
			float baseY,
			float topY,
			float landingInset,
			float shortcutMinPathSavings,
			Object owner)
		{
			Usage = usage;
			Center = center;
			Axis = NormalizeFlat(axis, Vector3.right);
			ClimbDirection = NormalizeFlat(climbDirection, Vector3.forward);
			HalfLength = Mathf.Max(0f, halfLength);
			BaseY = Mathf.Min(baseY, topY);
			TopY = Mathf.Max(baseY, topY);
			LandingInset = Mathf.Max(0f, landingInset);
			ShortcutMinPathSavings = Mathf.Max(0f, shortcutMinPathSavings);
			Owner = owner;
		}

		private static Vector3 NormalizeFlat(Vector3 value, Vector3 fallback)
		{
			value.y = 0f;
			if (value.sqrMagnitude < 0.0001f)
				return fallback;

			return value.normalized;
		}
	}

	public readonly struct ZombieClimbCandidate
	{
		public readonly ZombieClimbSurface Surface;
		public readonly Vector3 ContactPoint;
		public readonly Vector3 LandingPoint;
		public readonly Vector3 ClimbDirection;
		public readonly float DistanceToFace;
		public readonly bool IsRescue;
		public readonly bool RequiresClimb;

		public ZombieClimbCandidate(
			ZombieClimbSurface surface,
			Vector3 contactPoint,
			Vector3 landingPoint,
			Vector3 climbDirection,
			float distanceToFace,
			bool isRescue,
			bool requiresClimb)
		{
			Surface = surface;
			ContactPoint = contactPoint;
			LandingPoint = landingPoint;
			ClimbDirection = climbDirection;
			DistanceToFace = distanceToFace;
			IsRescue = isRescue;
			RequiresClimb = requiresClimb;
		}
	}

	public static class ZombieClimbSurfaces
	{
		public struct TerrainBuildConfig
		{
			public float WidthFactor;
			public float LandingInset;
			public float ShortcutMinPathSavings;
		}

		private struct SurfaceRegistration
		{
			public Object Owner;
			public ZombieClimbSurface[] Surfaces;
		}

		private static readonly List<ZombieClimbSurface> _terrainSurfaces = new();
		private static readonly List<SurfaceRegistration> _registrations = new();
		private static int _builtTerrainSeed;
		private static bool _hasBuiltTerrain;
		private static float _builtWidthFactor;
		private static float _builtLandingInset;
		private static float _builtShortcutMinPathSavings;
		private static WorldGridSnapshot _roadGrid;
		private static bool _hasRoadGrid;

		public static int TerrainSurfaceCount => _terrainSurfaces.Count;
		public static int ComponentRegistrationCount => _registrations.Count;

		public static void BuildTerrain(WorldHeightSnapshot snapshot, TerrainBuildConfig config)
		{
			if (snapshot.IsValid == false)
				return;

			float widthFactor = config.WidthFactor > 0f ? Mathf.Clamp(config.WidthFactor, 0.1f, 1f) : 0.9f;
			float landingInset = config.LandingInset > 0f ? config.LandingInset : 1.0f;
			float shortcutMinPathSavings = Mathf.Max(0f, config.ShortcutMinPathSavings);

			if (_hasBuiltTerrain &&
			    _builtTerrainSeed == snapshot.Seed &&
			    Mathf.Approximately(_builtWidthFactor, widthFactor) &&
			    Mathf.Approximately(_builtLandingInset, landingInset) &&
			    Mathf.Approximately(_builtShortcutMinPathSavings, shortcutMinPathSavings))
				return;

			_terrainSurfaces.Clear();

			float halfLength = Mathf.Max(0.25f, snapshot.TileSize * widthFactor * 0.5f);
			float cornerHalfLength = Mathf.Max(0.25f, snapshot.TileSize * widthFactor * 0.25f);
			float quarterTile = snapshot.TileSize * 0.25f;

			for (int x = 0; x < snapshot.Width; x++)
			{
				for (int y = 0; y < snapshot.Height; y++)
				{
					if (snapshot.TryGetCell(new Vector2Int(x, y), out WorldHeightCell cell) == false)
						continue;
					if (cell.IsLedge == false || cell.AllowsTraversalWithoutRoad)
						continue;
					if (cell.HighHeightLevel <= cell.LowHeightLevel)
						continue;

					var highDirections = new List<RoadDirection>(4);
					for (int direction = 0; direction < 4; direction++)
					{
						var roadDirection = (RoadDirection)direction;
						if (HasHigherCardinalNeighbor(snapshot, cell, roadDirection))
							highDirections.Add(roadDirection);
					}

					if (highDirections.Count == 0)
					{
						if (cell.LedgeShape == HeightTileShape.OuterCorner)
						{
							highDirections.Add((RoadDirection)(cell.LedgeRotationSteps % 4));
							highDirections.Add((RoadDirection)((cell.LedgeRotationSteps + 1) % 4));
						}
						else
						{
							highDirections.Add(cell.HighDirection);
						}
					}

					for (int i = 0; i < highDirections.Count; i++)
					{
						Vector3 centerOffset = Vector3.zero;
						float surfaceHalfLength = halfLength;
						if (cell.LedgeShape == HeightTileShape.InnerCorner && highDirections.Count == 2)
						{
							RoadDirection other = highDirections[1 - i];
							centerOffset = -DirectionToOffset(other) * quarterTile;
							surfaceHalfLength = cornerHalfLength;
						}
						else if (cell.LedgeShape == HeightTileShape.OuterCorner && highDirections.Count == 2)
						{
							RoadDirection other = highDirections[1 - i];
							centerOffset = DirectionToOffset(other) * quarterTile;
							surfaceHalfLength = cornerHalfLength;
						}

						AddTerrainSurface(snapshot, cell, highDirections[i], centerOffset, surfaceHalfLength, landingInset,
							shortcutMinPathSavings);
					}
				}
			}

			_builtTerrainSeed = snapshot.Seed;
			_builtWidthFactor = widthFactor;
			_builtLandingInset = landingInset;
			_builtShortcutMinPathSavings = shortcutMinPathSavings;
			_hasBuiltTerrain = true;

			Debug.Log($"{nameof(ZombieClimbSurfaces)}: registered {_terrainSurfaces.Count} generated terrain climb surface(s).");
		}

		public static void ClearTerrain()
		{
			_terrainSurfaces.Clear();
			_hasBuiltTerrain = false;
			_builtTerrainSeed = 0;
		}

		public static void SetRoadGrid(WorldGridSnapshot snapshot)
		{
			_roadGrid = snapshot;
			_hasRoadGrid = snapshot.IsValid;
		}

		public static void ClearRoadGrid()
		{
			_roadGrid = default;
			_hasRoadGrid = false;
		}

		public static bool TryGetRoadCell(Vector3 worldPosition, out WorldGridCell cell)
		{
			cell = default;
			return _hasRoadGrid && _roadGrid.TryGetCell(worldPosition, out cell);
		}

		public static void Register(Object owner, List<ZombieClimbSurface> surfaces)
		{
			if (owner == null)
				return;

			Unregister(owner);

			if (surfaces == null || surfaces.Count == 0)
				return;

			var validSurfaces = new List<ZombieClimbSurface>(surfaces.Count);
			for (int i = 0; i < surfaces.Count; i++)
			{
				if (surfaces[i].IsValid)
					validSurfaces.Add(surfaces[i]);
			}

			if (validSurfaces.Count == 0)
				return;

			_registrations.Add(new SurfaceRegistration
			{
				Owner = owner,
				Surfaces = validSurfaces.ToArray(),
			});
		}

		public static void Unregister(Object owner)
		{
			if (owner == null)
				return;

			for (int i = _registrations.Count - 1; i >= 0; i--)
			{
				if (_registrations[i].Owner == null || _registrations[i].Owner == owner)
					_registrations.RemoveAt(i);
			}
		}

		public static bool TryFindDirectClimb(
			Vector3 origin,
			Vector3 goal,
			bool allowShortcut,
			bool allowRescue,
			float pathSavings,
			float maxShortcutDistanceToFace,
			float maxRescueDistanceToFace,
			float sideTolerance,
			float minClimbHeight,
			float rescueLandingHeightTolerance,
			float rescueLandingFlatTolerance,
			out ZombieClimbCandidate candidate)
		{
			candidate = default;
			if (allowShortcut == false && allowRescue == false)
				return false;

			Vector3 flatToGoal = goal - origin;
			flatToGoal.y = 0f;
			float directDistance = flatToGoal.magnitude;
			if (directDistance < 0.05f)
				return false;

			Vector3 routeDirection = flatToGoal / directDistance;
			float maxShortcutDistance = Mathf.Max(0.1f, maxShortcutDistanceToFace);
			float maxRescueDistance = Mathf.Max(0.1f, maxRescueDistanceToFace);
			float tolerance = Mathf.Max(0f, sideTolerance);
			float minRise = Mathf.Max(0.05f, minClimbHeight);
			float heightTolerance = Mathf.Max(0.05f, rescueLandingHeightTolerance);
			float flatTolerance = Mathf.Max(0.05f, rescueLandingFlatTolerance);
			float bestScore = float.MaxValue;
			bool found = false;

			FindBestInList(_terrainSurfaces, origin, goal, routeDirection, directDistance, allowShortcut, allowRescue,
				pathSavings, maxShortcutDistance, maxRescueDistance, tolerance, minRise, heightTolerance, flatTolerance,
				ref candidate, ref bestScore, ref found);

			for (int i = _registrations.Count - 1; i >= 0; i--)
			{
				var registration = _registrations[i];
				if (registration.Owner == null)
				{
					_registrations.RemoveAt(i);
					continue;
				}

				if (registration.Surfaces == null || registration.Surfaces.Length == 0)
					continue;

				for (int s = 0; s < registration.Surfaces.Length; s++)
				{
					TryEvaluateSurface(registration.Surfaces[s], origin, goal, routeDirection, directDistance,
						allowShortcut, allowRescue, pathSavings, maxShortcutDistance, maxRescueDistance,
						tolerance, minRise, heightTolerance, flatTolerance,
						ref candidate, ref bestScore, ref found);
				}
			}

			return found;
		}

		private static void FindBestInList(
			List<ZombieClimbSurface> surfaces,
			Vector3 origin,
			Vector3 goal,
			Vector3 routeDirection,
			float directDistance,
			bool allowShortcut,
			bool allowRescue,
			float pathSavings,
			float maxShortcutDistance,
			float maxRescueDistance,
			float sideTolerance,
			float minClimbHeight,
			float rescueLandingHeightTolerance,
			float rescueLandingFlatTolerance,
			ref ZombieClimbCandidate bestCandidate,
			ref float bestScore,
			ref bool found)
		{
			for (int i = 0; i < surfaces.Count; i++)
			{
				TryEvaluateSurface(surfaces[i], origin, goal, routeDirection, directDistance, allowShortcut, allowRescue,
					pathSavings, maxShortcutDistance, maxRescueDistance, sideTolerance, minClimbHeight,
					rescueLandingHeightTolerance, rescueLandingFlatTolerance,
					ref bestCandidate, ref bestScore, ref found);
			}
		}

		private static void TryEvaluateSurface(
			ZombieClimbSurface surface,
			Vector3 origin,
			Vector3 goal,
			Vector3 routeDirection,
			float directDistance,
			bool allowShortcut,
			bool allowRescue,
			float pathSavings,
			float maxShortcutDistance,
			float maxRescueDistance,
			float sideTolerance,
			float minClimbHeight,
			float rescueLandingHeightTolerance,
			float rescueLandingFlatTolerance,
			ref ZombieClimbCandidate bestCandidate,
			ref float bestScore,
			ref bool found)
		{
			if (surface.IsValid == false)
				return;

			bool canShortcutBase = allowShortcut &&
			                       (surface.Usage & ZombieClimbSurfaceUsage.TerrainShortcut) != 0 &&
			                       pathSavings >= surface.ShortcutMinPathSavings;
			bool canRescueBase = allowRescue &&
			                     (surface.Usage & ZombieClimbSurfaceUsage.Rescue) != 0 &&
			                     Mathf.Abs(surface.TopY - goal.y) <= rescueLandingHeightTolerance;
			if (canShortcutBase == false && canRescueBase == false)
				return;

			Vector3 originFromCenter = origin - surface.Center;
			originFromCenter.y = 0f;
			float originDepth = Vector3.Dot(originFromCenter, surface.ClimbDirection);
			float routeDepth = Vector3.Dot(routeDirection, surface.ClimbDirection);

			bool canShortcutClimb = canShortcutBase && goal.y > origin.y + minClimbHeight;
			TryEvaluateUpwardCrossing(surface, origin, goal, routeDirection, directDistance,
				canShortcutClimb, canRescueBase, originDepth, routeDepth, maxShortcutDistance, maxRescueDistance,
				sideTolerance, minClimbHeight, rescueLandingFlatTolerance, ref bestCandidate, ref bestScore, ref found);

			bool canShortcutDrop = canShortcutBase && goal.y < origin.y - minClimbHeight;
			TryEvaluateDownwardCrossing(surface, origin, routeDirection, directDistance,
				canShortcutDrop, originDepth, routeDepth, maxShortcutDistance, sideTolerance, minClimbHeight,
				ref bestCandidate, ref bestScore, ref found);
		}

		private static void TryEvaluateUpwardCrossing(
			ZombieClimbSurface surface,
			Vector3 origin,
			Vector3 goal,
			Vector3 routeDirection,
			float directDistance,
			bool canShortcut,
			bool canRescue,
			float originDepth,
			float routeDepth,
			float maxShortcutDistance,
			float maxRescueDistance,
			float sideTolerance,
			float minClimbHeight,
			float rescueLandingFlatTolerance,
			ref ZombieClimbCandidate bestCandidate,
			ref float bestScore,
			ref bool found)
		{
			if (canShortcut == false && canRescue == false)
				return;

			float rise = surface.TopY - origin.y;
			if (rise < minClimbHeight)
				return;
			if (origin.y > surface.TopY - minClimbHeight * 0.5f)
				return;
			if (routeDepth <= 0.05f)
				return;
			if (originDepth > sideTolerance)
				return;

			float maxDistance = canShortcut ? maxShortcutDistance : maxRescueDistance;
			float distanceToFace = Mathf.Max(0f, -originDepth / routeDepth);
			if (distanceToFace > maxDistance || distanceToFace > directDistance + sideTolerance)
				return;

			if (TryBuildCrossingCandidate(surface, origin, routeDirection, distanceToFace, sideTolerance,
				    surface.ClimbDirection, surface.TopY, true, canRescue && canShortcut == false,
				    out ZombieClimbCandidate candidate) == false)
				return;

			if (candidate.IsRescue && FlatDistanceSqr(candidate.LandingPoint, goal) > rescueLandingFlatTolerance * rescueLandingFlatTolerance)
				return;

			float score = distanceToFace + (canRescue ? 0f : 0.25f);
			if (score >= bestScore)
				return;

			bestScore = score;
			bestCandidate = candidate;
			found = true;
		}

		private static void TryEvaluateDownwardCrossing(
			ZombieClimbSurface surface,
			Vector3 origin,
			Vector3 routeDirection,
			float directDistance,
			bool canShortcut,
			float originDepth,
			float routeDepth,
			float maxShortcutDistance,
			float sideTolerance,
			float minClimbHeight,
			ref ZombieClimbCandidate bestCandidate,
			ref float bestScore,
			ref bool found)
		{
			if (canShortcut == false)
				return;
			if (origin.y < surface.TopY - minClimbHeight)
				return;
			if (routeDepth >= -0.05f)
				return;
			if (originDepth < -sideTolerance)
				return;

			float distanceToFace = Mathf.Max(0f, originDepth / -routeDepth);
			if (distanceToFace > maxShortcutDistance || distanceToFace > directDistance + sideTolerance)
				return;

			if (TryBuildCrossingCandidate(surface, origin, routeDirection, distanceToFace, sideTolerance,
				    -surface.ClimbDirection, surface.BaseY, false, false, out ZombieClimbCandidate candidate) == false)
				return;

			float score = distanceToFace + 0.5f;
			if (score >= bestScore)
				return;

			bestScore = score;
			bestCandidate = candidate;
			found = true;
		}

		private static bool TryBuildCrossingCandidate(
			ZombieClimbSurface surface,
			Vector3 origin,
			Vector3 routeDirection,
			float distanceToFace,
			float sideTolerance,
			Vector3 landingDirection,
			float landingY,
			bool requiresClimb,
			bool isRescue,
			out ZombieClimbCandidate candidate)
		{
			candidate = default;
			Vector3 crossing = origin + routeDirection * distanceToFace;
			Vector3 crossingFromCenter = crossing - surface.Center;
			crossingFromCenter.y = 0f;
			float lateral = Vector3.Dot(crossingFromCenter, surface.Axis);
			if (Mathf.Abs(lateral) > surface.HalfLength + sideTolerance)
				return false;

			float clampedLateral = Mathf.Clamp(lateral, -surface.HalfLength, surface.HalfLength);
			Vector3 contactPoint = surface.Center + surface.Axis * clampedLateral;
			contactPoint.y = Mathf.Clamp(origin.y, surface.BaseY, surface.TopY);

			Vector3 landingPoint = surface.Center + surface.Axis * clampedLateral + landingDirection * surface.LandingInset;
			landingPoint.y = landingY;

			candidate = new ZombieClimbCandidate(
				surface,
				contactPoint,
				landingPoint,
				landingDirection,
				distanceToFace,
				isRescue,
				requiresClimb);
			return true;
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static void AddTerrainSurface(
			WorldHeightSnapshot snapshot,
			WorldHeightCell cell,
			RoadDirection highDirection,
			Vector3 centerOffset,
			float halfLength,
			float landingInset,
			float shortcutMinPathSavings)
		{
			Vector3 climbDirection = DirectionToOffset(highDirection);
			Vector3 axis = new Vector3(-climbDirection.z, 0f, climbDirection.x);
			Vector3 center = snapshot.CellCenterWorld(cell.Position, cell.LowHeightLevel) + centerOffset;
			float baseY = snapshot.Origin.y + cell.LowHeightLevel * snapshot.HeightLevelWorldUnits;
			float topY = snapshot.Origin.y + cell.HighHeightLevel * snapshot.HeightLevelWorldUnits;

			_terrainSurfaces.Add(new ZombieClimbSurface(
				ZombieClimbSurfaceUsage.All,
				center,
				axis,
				climbDirection,
				halfLength,
				baseY,
				topY,
				landingInset,
				shortcutMinPathSavings,
				null));
		}

		private static bool HasHigherCardinalNeighbor(WorldHeightSnapshot snapshot, WorldHeightCell cell, RoadDirection direction)
		{
			Vector2Int neighborPosition = cell.Position + DirectionToGridOffset(direction);
			return snapshot.TryGetCell(neighborPosition, out WorldHeightCell neighbor) &&
			       neighbor.HeightLevel > cell.LowHeightLevel;
		}

		private static Vector2Int DirectionToGridOffset(RoadDirection direction)
		{
			switch (direction)
			{
				case RoadDirection.North: return new Vector2Int(0, 1);
				case RoadDirection.East: return new Vector2Int(1, 0);
				case RoadDirection.South: return new Vector2Int(0, -1);
				case RoadDirection.West: return new Vector2Int(-1, 0);
				default: return Vector2Int.up;
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
