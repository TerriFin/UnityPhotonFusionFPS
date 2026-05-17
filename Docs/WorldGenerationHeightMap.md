# World Generation Height Map

## Context

Height generation is the new first world-generation pass.

Current world generation order should become:

```text
HeightMapGenerator
-> RoadGridGenerator
-> BuildingPlacementGenerator
-> WorldLootSpawner
```

The height pass decides the elevation of each map cell before roads are generated. It also places non-road ledge tiles where one elevation meets another. The road generator later consumes the height map, respects the ledge cells, and may replace a ledge with a special straight height-change road tile when it needs a road to climb or descend.

If the height generator is not present, not run, or configured with only one height layer, the rest of the world generation behaves like the current flat map.

## Goals

- Generate interesting but workable map elevation before road placement.
- Support a configurable number of height layers.
- Default to one height layer so existing flat maps still generate.
- Prevent impossible cliffs: adjacent and diagonal cells may differ by at most one height level.
- Avoid tiny elevation islands that only shrink playable space.
- Place ledge tiles on elevation transitions.
- Let roads cross height transitions only through a special straight ramp/height-change road tile.
- Keep height generation separate from road and building placement.
- Keep the system ready for future special ledges, stairs, ledge-built structures, and multi-level arenas.

## Height Model

Each grid cell gets an integer height level:

```csharp
HeightCell
{
    Vector2Int Position;
    int HeightLevel;
    bool IsLedge;
    bool IsBoundaryLedge;
    Direction HighSide;
    Direction LowSide;
}
```

`HeightLevel` is a logical layer, not a Unity Y value.

Unity Y position is:

```csharp
worldY = HeightLevel * HeightLevelWorldUnits
```

For example:

```text
HeightLevelWorldUnits = 4
HeightLevel 0 -> Y 0
HeightLevel 1 -> Y 4
HeightLevel 2 -> Y 8
```

The actual labels can be normalized to `0..HeightLayerCount - 1`. A "valley" is still possible with two layers by making the surrounding region height `1` and the valley height `0`.

## Adjacency Rules

There can never be a two-level difference between neighboring cells.

For every cell:

```text
abs(cell.HeightLevel - north.HeightLevel) <= 1
abs(cell.HeightLevel - east.HeightLevel) <= 1
abs(cell.HeightLevel - south.HeightLevel) <= 1
abs(cell.HeightLevel - west.HeightLevel) <= 1
```

The same rule applies to diagonals:

```text
abs(cell.HeightLevel - northEast.HeightLevel) <= 1
abs(cell.HeightLevel - southEast.HeightLevel) <= 1
abs(cell.HeightLevel - southWest.HeightLevel) <= 1
abs(cell.HeightLevel - northWest.HeightLevel) <= 1
```

This keeps the generated terrain terrace-like and prevents impossible height jumps.

## Ledge Cells

A ledge cell is a cell occupied by the height transition itself.

Ledge cells:

- cannot contain buildings,
- cannot contain normal flat road tiles,
- cannot contain simple filler blockers,
- can be replaced by the road generator with a height-change road tile,
- are placed by the height generator before road generation.

The authored ledge prefabs use this convention:

```text
Default rotation:
  higher elevation is North
  lower elevation is South

Prefab origin (pivot):
  sits at the midpoint between the lower and higher elevation surfaces
  the cliff/ramp geometry extends up toward the high side and down toward the low side
```

The generator rotates ledge tiles in the same four cardinal rotations used by roads/buildings. For a straight ledge the rotation is fully determined by the cell:

```csharp
rotationSteps = (int)cell.HighDirection   // North=0, East=1, South=2, West=3
```

For a ledge cell:

```text
HighSide = direction toward higher elevation
LowSide = opposite direction
```

The ledge prefab's world Y should be the midpoint between the two heights it bridges:

```csharp
worldY = (LowHeightLevel + HighHeightLevel) * 0.5 * HeightLevelWorldUnits
```

This matches the authoring convention where the prefab's pivot sits halfway between the lower and higher plateaus. The upper half of the prefab covers the high side at `HighHeightLevel * HeightLevelWorldUnits`, and the lower half covers the low side at `LowHeightLevel * HeightLevelWorldUnits`.

## Ledge Tile Types

First version ledge tiles:

- normal straight ledge,
- boundary straight ledge,
- inner corner ledge,
- outer corner ledge.

The straight ledge default orientation is:

```text
North = high side
South = low side
```

Inner and outer corner ledges should follow the same base-orientation rule: the `HeightTileDefinition` describes which sides are high and low at rotation `0`, and the generator rotates the definition to match the local height pattern.

When an elevation transition reaches the map edge, use boundary ledge tiles. For the first version, map-edge ledges should be straight. The height transition is considered to continue out of the map.

Future special ledge tiles can include:

- stairs,
- ladders,
- ramps without roads,
- multi-cell structures such as parking garages,
- enterable structures built into the ledge,
- collapsed road/terrain pieces,
- special district boundary pieces.

Special ledge tiles should use the same weight and repeat cooldown logic as roads/buildings.

Special ledge tiles may occupy more than one grid cell. For example, a multi-level parking garage might occupy a `2x2` footprint and provide traversal between height layers. These are still height/ledge system pieces, not buildings placed by the building generator.

Only `1x1` straight ledge tiles are valid candidates for road-generator replacement with the special height-change road tile. Larger ledge structures cannot be overridden by roads.

## Unity Configuration

Use a scene component plus settings/tile-set assets, mirroring the road and building generators.

Planned scripts:

```text
Assets/Scripts/WorldGeneration/
    HeightTileShape.cs
    HeightTileDefinition.cs
    HeightTileSet.cs
    HeightGenerationSettings.cs
    HeightMapGenerator.cs
    WorldHeightCell.cs
```

### HeightMapGenerator

`HeightMapGenerator` is the new first generator and owns shared run-specific values:

```csharp
HeightMapGenerator
{
    int Width;
    int Height;
    int Seed;
    bool RandomizeSeedOnGenerate;
    float TileSize;
    float HeightLevelWorldUnits;

    bool GenerateOnStart;
    bool ClearBeforeGenerate;

    HeightGenerationSettings Settings;
}
```

Suggested defaults:

```text
Width: 12
Height: 12
Seed: 12345
RandomizeSeedOnGenerate: false
TileSize: 20
HeightLevelWorldUnits: 4
GenerateOnStart: true
ClearBeforeGenerate: true
```

Because height generation becomes the first pass, `Width`, `Height`, `Seed`, `RandomizeSeedOnGenerate`, and `TileSize` should move from `RoadGridGenerator` to `HeightMapGenerator` during implementation. The road and building generators should read those values from the generated height snapshot when available.

If no `HeightMapGenerator` exists, `RoadGridGenerator` should keep its current fallback values and generate a flat height `0` map.

During a networked match, `Gameplay` should use the height generator as the source for the authoritative world seed. Clients should regenerate height, roads, and buildings from the host seed exactly like the current road/building flow.

### HeightGenerationSettings

Height-specific settings live in a settings asset:

```csharp
HeightGenerationSettings
{
    int HeightLayerCount;
    int MinCellsBetweenHeightChanges;
    int MinUsableRegionWidth;
    int MinUsableRegionHeight;
    int MinUsableRegionArea;
    int SmoothingPasses;
    float RegionBalance;
    int MaxGenerationAttempts;
    int DefaultLedgeRepeatCooldownDistance;
    int MinRoadReplaceableLedgesPerHeightRegion;

    HeightTileSet LedgeTiles;
}
```

Suggested defaults:

```text
HeightLayerCount: 1
MinCellsBetweenHeightChanges: 3
MinUsableRegionWidth: 3
MinUsableRegionHeight: 3
MinUsableRegionArea: 9
SmoothingPasses: 2
RegionBalance: 0.5
MaxGenerationAttempts: 100
DefaultLedgeRepeatCooldownDistance: 2
MinRoadReplaceableLedgesPerHeightRegion: 5
```

Meaning:

- `HeightLayerCount`: number of possible elevation layers. `1` means flat map.
- `MinCellsBetweenHeightChanges`: minimum flat distance before another elevation transition can happen.
- `MinUsableRegionWidth`: smallest width a non-ledge height region should have.
- `MinUsableRegionHeight`: smallest height a non-ledge height region should have.
- `MinUsableRegionArea`: smallest useful plateau/valley area.
- `SmoothingPasses`: how aggressively small noisy features are removed.
- `RegionBalance`: how strongly generation tries to split the map evenly between available height layers.
- `MaxGenerationAttempts`: retry budget before falling back to a simpler valid height map.
- `DefaultLedgeRepeatCooldownDistance`: fallback cooldown used when a specific ledge tile does not override repeat distance.
- `MinRoadReplaceableLedgesPerHeightRegion`: minimum number of meaningful `1x1` straight ledge cells that can be replaced by height-change roads for each non-base height region.

For `HeightLayerCount = 2`, the generator should usually split the map into two roughly similar-sized sections, but it may also produce a valley/hill shape if constraints allow it.

For `HeightLayerCount = 3`, it should produce three usable elevation regions where possible. The regions do not need to be equal, but no height should exist only as tiny unusable noise.

### HeightTileDefinition

Each ledge prefab root gets a `HeightTileDefinition` component:

```csharp
HeightTileDefinition
{
    HeightTileShape Shape;
    Vector2Int FootprintSize;
    bool IsBoundaryTile;
    bool AllowsTraversalWithoutRoad;
    bool CanBeReplacedByHeightChangeRoad;
    int Weight;
    int RepeatCooldownDistance;
}
```

Possible shapes:

```csharp
enum HeightTileShape
{
    Straight,
    InnerCorner,
    OuterCorner
}
```

`AllowsTraversalWithoutRoad` is for future special ledges such as stairs or ramps. It can exist in the first data model but does not need gameplay behavior yet.

`CanBeReplacedByHeightChangeRoad` should only be enabled for `1x1` straight ledge tiles. It tells the road generator that this ledge cell may be suppressed and replaced by the special road ramp tile.

`RepeatCooldownDistance` is per ledge prefab. This lets unique pieces, such as a `2x2` parking garage, prevent another copy of the same source tile from appearing within a larger radius. If the value is `0` or negative, the generator should use `DefaultLedgeRepeatCooldownDistance` from `HeightGenerationSettings`.

All height tile definitions are considered rotatable in all four cardinal directions. Rotated candidates still count as the same source tile for repeat cooldown.

### HeightTileSet

```csharp
HeightTileSet
{
    HeightTileDefinition[] Tiles;
}
```

All ledge variants — normal interior, map-edge boundary, future stairs/ramps, environment-specific — live in the same `HeightTileSet`. The generator picks among them by matching tile flags against the cell:

- `HeightTileDefinition.IsBoundaryTile` must equal `cell.IsBoundaryLedge` (map-edge cells get boundary tiles, interior cells get interior tiles).
- `HeightTileDefinition.Shape` must equal `cell.LedgeShape`.

A second tile set would only be needed if there were a reason to swap entire palettes at runtime (e.g. per-district themes), which is not currently planned.

## Height Generation Approach

The first implementation should favor large, usable regions over noisy terrain.

Recommended pipeline:

### 1. Initialize Flat Grid

Create every cell at height `0`.

If `HeightLayerCount <= 1`, stop here. No ledge cells are created.

### 2. Generate Large Height Regions

Create one or more regions for each available height layer.

The first version can use seeded region growth or Voronoi-style seeds:

1. Pick a small number of region seeds.
2. Assign each seed a target height layer.
3. Grow regions across the grid with seeded random weights.
4. Prefer contiguous large regions.
5. Use `RegionBalance` to decide how much to favor equal-size height layers.

With two height layers, a common output should look like:

```text
lower half of map: height 0
upper half of map: height 1
```

but this can bend, curve, or create a central valley if enough usable space remains.

### 3. Cull Unusable Features

Remove or merge height regions that are too small.

Use:

```text
MinUsableRegionWidth
MinUsableRegionHeight
MinUsableRegionArea
```

Any region that cannot support at least a small usable flat section should be merged into the most compatible neighboring region.

This is important because a `2x2` hill consumes map space but cannot support meaningful roads/buildings. The default goal is that every generated height region can fit at least one flat road/building opportunity after ledges are placed.

### 4. Enforce Height Difference Rule

Scan cardinal and diagonal neighbors.

If any pair differs by more than one level, adjust intermediate cells or lower/raise one region until:

```text
abs(deltaHeight) <= 1
```

Since the first version should usually create adjacent height bands, this rule should mostly be a validation step.

### 5. Enforce Distance Between Height Changes

Apply `MinCellsBetweenHeightChanges`.

If two separate ledge lines would be too close, smooth one of them away or merge the narrow strip into a neighboring region.

This prevents unusable one-cell terraces and narrow zigzagging elevation strips.

### 6. Mark Ledge Cells

After final heights are known, mark cells that sit on a transition between height levels.

For each candidate transition:

1. Find the low side and high side.
2. Mark the transition cell as `IsLedge`.
3. Store `LowHeightLevel`, `HighHeightLevel`, and direction to the high side.
4. Choose a straight, inner-corner, outer-corner, or boundary ledge tile based on neighboring transition pattern.

Ledge cells are occupied by height geometry and are not buildable.

Different-height playable cells should not be directly adjacent as ordinary road/building cells. A ledge cell must sit between the two height levels. This means a building beside a ledge is adjacent to the ledge cell, not to a different-height road cell on the far side of the ledge.

When selecting ledge tiles, the generator may choose larger ledge structures if their full footprint fits the transition pattern. The occupied footprint becomes ledge occupancy and blocks roads/buildings. Large ledge structures may allow traversal later, but they are not road-ramp override candidates.

### 7. Validate Road-Replaceable Ledges

After ledge cells are marked and before tile instantiation, validate that each useful height region has enough possible road transitions.

A road-replaceable ledge candidate is:

```text
1x1
straight
non-boundary unless edge exits explicitly allow boundary ramps
has one clear low-side cell
has one clear high-side cell
both side cells belong to usable height regions
```

Each non-base height region should have at least `MinRoadReplaceableLedgesPerHeightRegion` meaningful road-replaceable ledges. If a region cannot provide enough candidates, generation should modify or remove that height region before road generation begins.

This prevents height regions that look nice but cannot realistically connect to the road graph. It also prevents large special ledge structures from accidentally consuming every possible transition point.

### 8. Instantiate Ledge Prefabs

For each ledge cell:

1. Build the required ledge shape.
2. Find matching `HeightTileDefinition` candidates.
3. Expand all four rotations.
4. Filter boundary/non-boundary tile sets.
5. Apply weight and the selected tile's repeat cooldown, falling back to `DefaultLedgeRepeatCooldownDistance` if needed.
6. Instantiate the selected prefab at:

```csharp
xz = cell center
y = (LowHeightLevel + HighHeightLevel) * 0.5 * HeightLevelWorldUnits
rotation = (int)HighDirection * 90 degrees   // straight ledges
```

Corner ledges need a pair of high directions to choose a unique rotation. Until `WorldHeightCell` stores that pair, the generator does not produce corner ledges and corner-tile rotation falls back to all four orientations.

## Road Generator Interaction

`RoadGridGenerator` consumes the height map.

If no height map exists:

```text
HeightLevel = 0 for every cell
IsLedge = false for every cell
```

Rules:

- Roads can connect normally between same-height neighboring road cells.
- Roads cannot connect directly across a non-road ledge.
- Roads cannot be adjacent across different height levels without a ledge cell between them.
- Roads can cross a ledge only by replacing a `1x1` straight road-replaceable ledge cell with the special height-change road tile.
- The height-change road tile is treated like a straight road tile for graph connectivity.
- The height-change road tile default orientation is lower side South, higher side North, with its pivot at the midpoint between the lower and higher plateaus (same authoring convention as the ledge prefabs it replaces).
- Its rotation is constrained to `(int)cell.HighDirection`, not just socket matching — a ramp's sockets are symmetric, so without this constraint it can spawn flipped 180 degrees.
- Its world Y is:

```csharp
(LowHeightLevel + HighHeightLevel) * 0.5 * HeightLevelWorldUnits
```

The road ramp cell must connect both ends:

```text
low-side road -> ramp cell -> high-side road
```

It cannot be a stub/dead-end itself. It may immediately lead to a stub road on the high or low side, but the ramp tile must have valid road connection on both low and high sides.

When the road generator chooses to use a ledge cell as a road ramp:

1. Remove/destroy/suppress the ledge prefab that height generation placed there.
2. Mark the cell as a road cell.
3. Mark the cell as a height-change road.
4. Collapse it using the configured height-change road tile.

The road generator must never suppress or replace larger ledge structures. If a selected ledge tile occupies more than `1x1`, it remains height geometry and blocks normal road placement.

Normal road cells should be placed at:

```csharp
HeightLevel * HeightLevelWorldUnits
```

## Building Generator Interaction

`BuildingPlacementGenerator` consumes both road occupancy and height data.

Rules:

- Buildings cannot be placed on ledge cells.
- Buildings cannot be placed on height-change road cells.
- A building footprint must be on one flat height level.
- Complex building road requirements should match roads at the same height level.
- Complex building ledge requirements may require an adjacent ledge going up or down relative to the building.
- Simple blocking buildings should be placed at the cell height they occupy.

Buildings placed by the building generator do not bridge height levels. Traversal or architecture that connects height layers belongs to the height/ledge system, using special ledge tiles, stairs, ramps, or ledge-built structures.

Those are out of scope for the first pass.

## Data Handoff

The height generator should expose a snapshot:

```csharp
WorldHeightSnapshot
{
    int Width;
    int Height;
    int Seed;
    float TileSize;
    float HeightLevelWorldUnits;
    HeightCell[,] Cells;
}
```

The road generator should include height data in its own output:

```csharp
WorldGridCell
{
    Vector2Int Position;
    int HeightLevel;
    bool IsRoad;
    bool IsBoundaryExit;
    bool IsLedge;
    bool IsHeightChangeRoad;
    RoadEnvironment Environment;
}
```

Building placement should use this combined road/height grid.

## Networking

Only the seed and generator settings need to match between host and clients.

Runtime state should stay deterministic:

- Host/state authority chooses the networked world seed.
- Every peer generates the same height map from that seed.
- Every peer then generates roads/buildings from the same height snapshot.

Do not network:

- every height cell,
- ledge tile choices,
- generated road paths,
- building placements.

Those should be deterministic outputs of the shared seed and settings.

## Implementation Plan

1. Add `HeightTileDefinition`, `HeightTileSet`, `HeightGenerationSettings`, and `HeightMapGenerator`.
2. Move shared run values (`Width`, `Height`, `Seed`, `RandomizeSeedOnGenerate`, `TileSize`) from `RoadGridGenerator` to `HeightMapGenerator`.
3. Keep `RoadGridGenerator` fallback values for flat-map compatibility when no height generator is assigned.
4. Generate flat height map when `HeightLayerCount <= 1`.
5. Generate larger height regions when `HeightLayerCount > 1`.
6. Cull/merge unusably small height features.
7. Enforce cardinal and diagonal height-difference rules.
8. Enforce minimum distance between height changes.
9. Mark ledge cells and instantiate ledge prefabs.
10. Expose `WorldHeightSnapshot`.
11. Update `RoadGridGenerator` to consume the snapshot.
12. Add special straight height-change road tile support.
13. Update building placement to reject ledge/ramp cells and require flat footprints.

## First Version Acceptance Criteria

- `HeightLayerCount = 1` produces the same flat behavior as the current map generator.
- `HeightLayerCount = 2` can split the map into two usable elevation regions.
- No cardinal or diagonal neighbors differ by more than one height level.
- Tiny unusable hills/valleys are smoothed away or merged.
- Ledge prefabs are placed at elevation transitions.
- Boundary ledges are used where elevation transitions meet the map edge.
- Road generation can optionally replace a straight ledge cell with a height-change road tile.
- Larger ledge structures are preserved and cannot be overridden by roads.
- Each useful height region has enough road-replaceable straight ledges, or the region is modified/removed.
- Buildings never spawn on ledge or height-change road cells.
- Seeded generation is deterministic.

## Implementation Decisions

- Height levels are normalized to `0..HeightLayerCount - 1`.
- The ledge/ramp cell is positioned at the lower height and points toward the high side.
- Non-road ledge cells are ledges, not building cells. They can be used as ledge geometry, but ordinary buildings are not placed on them.
- Road exits are allowed on any height layer. The generator should rely on road-replaceable ledge availability and pathfinding to connect them, then polish edge cases as they appear in testing.
- Separate height regions do not need to be force-connected unless they contain city entry roads that need to connect to the rest of the road graph.
- Boundary ledges use straight boundary pieces only. Like road entrance tiles, they do not need special corner pieces in the first version.
