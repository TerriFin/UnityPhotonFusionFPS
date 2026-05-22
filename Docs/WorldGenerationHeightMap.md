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

- cannot receive buildings during normal footprint placement,
- cannot contain normal flat road tiles,
- cannot contain simple filler blockers during normal placement,
- can be replaced by the road generator with a height-change road tile,
- can later be replaced by the building generator with a simple blocking building only if the ledge is buried inside blocking mass,
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

Inner and outer corner ledges follow deterministic base-orientation rules:

```text
Inner corner rotation 0: high sides are North + East
Outer corner rotation 0: higher elevation touches the North-East diagonal
```

The generator rotates those definitions to match the local height pattern. Outer corners are created for low cells that touch higher elevation only diagonally. This fills the visible gap between two neighboring inner-corner transitions.

When an elevation transition reaches the map edge, use boundary ledge tiles. For the first version, map-edge ledges should be straight. The height transition is considered to continue out of the map.

Boundary corner ledge candidates are intentionally suppressed in the current implementation. If an edge cell would become an inner/outer corner ledge, it is left as a normal empty edge cell so building placement can fill it with a simple blocker. This keeps the map edge limited to edge roads, straight boundary ledges, and blocking buildings.

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
    int PreferredLedgeCount;
    int MinCellsBetweenHeightChanges;
    int MinUsableRegionWidth;
    int MinUsableRegionHeight;
    int MinUsableRegionArea;
    float LedgePathRandomness;
    int MaxGenerationAttempts;
    int DefaultLedgeRepeatCooldownDistance;
    int MinRoadReplaceableLedgesPerHeightRegion;

    HeightTileSet LedgeTiles;
}
```

Suggested defaults:

```text
HeightLayerCount: 1
PreferredLedgeCount: 1
MinCellsBetweenHeightChanges: 3
MinUsableRegionWidth: 3
MinUsableRegionHeight: 3
MinUsableRegionArea: 9
LedgePathRandomness: 0.65
MaxGenerationAttempts: 100
DefaultLedgeRepeatCooldownDistance: 2
MinRoadReplaceableLedgesPerHeightRegion: 5
```

Meaning:

- `HeightLayerCount`: number of possible elevation layers. `1` means flat map.
- `PreferredLedgeCount`: target number of separate ledge paths to place. The generator keeps partial success if it cannot place all requested ledges.
- `MinCellsBetweenHeightChanges`: minimum grid distance between distinct generated ledge paths.
- `MinUsableRegionWidth`: smallest width a non-ledge height region should have.
- `MinUsableRegionHeight`: smallest height a non-ledge height region should have.
- `MinUsableRegionArea`: smallest useful plateau/valley area.
- `LedgePathRandomness`: how strongly organic ledge paths are allowed and required to bend. `0` allows very direct paths; `1` strongly favors noisy paths and rejects mostly straight ledges.
- `MaxGenerationAttempts`: total retry budget for placing ledges. If the budget runs out, already placed ledges are kept.
- `DefaultLedgeRepeatCooldownDistance`: deprecated. Tiles use their own `RepeatCooldownDistance` directly, where `0` means no cooldown.
- `MinRoadReplaceableLedgesPerHeightRegion`: minimum number of meaningful `1x1` straight ledge cells that can be replaced by height-change roads for each non-base height region.

For `HeightLayerCount = 2`, every accepted ledge can only create height `0` and height `1` regions. Setting `PreferredLedgeCount` higher than `1` can create several separate hills/valleys that reuse those two height levels.

For higher `HeightLayerCount` values, later ledges may split already-raised plateaus to create taller stepped regions. For example, `HeightLayerCount = 5` and `PreferredLedgeCount = 4` can theoretically create a five-level hill if each ledge splits the previous high plateau.

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

`RepeatCooldownDistance` is per ledge prefab and is interpreted literally: `0` means "no cooldown, always available", and a positive value means "this tile cannot be placed again within that many ledge-cell placements of its previous placement". This lets unique pieces, such as a `2x2` parking garage, prevent another copy of the same source tile from appearing within a larger radius while leaving the common variants always available.

`DefaultLedgeRepeatCooldownDistance` is no longer applied by the generator; the previous fallback behavior silently put every tile with `RepeatCooldownDistance = 0` on the default cooldown, which made the cooldown-violating fallback pool fire constantly and let specialty tiles repeat far more often than their own cooldown should have allowed. The field is kept on the settings asset for backward compatibility but is unused.

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

The implementation favors large, usable regions over noisy terrain.

Height generation is now ledge-path-first. Instead of painting a noisy height field and trying to repair bad ledges afterward, the generator creates the elevation transition lines first. The height regions are then derived from those accepted ledge paths.

This makes `MinCellsBetweenHeightChanges` a real generation constraint:

```text
Two distinct ledge paths cannot be within this many cells of each other.
```

That means separate ledges cannot cross, touch, or form 3-layer intersections unless the spacing value is intentionally reduced enough to allow that. A single ledge path may still occupy adjacent cells along its own length, because that is one continuous ledge.

Recommended pipeline:

### 1. Initialize Flat Grid

Create every cell at height `0`.

If `HeightLayerCount <= 1` or `PreferredLedgeCount <= 0`, stop here. No ledge cells are created.

### 2. Generate Large Height Regions

Create organic ledge paths one at a time.

Until either `PreferredLedgeCount` ledges have been placed or `MaxGenerationAttempts` is exhausted:

1. Find an existing usable height region that can be split.
2. Pick start and end sides for an organic ledge path.
3. Pathfind a connected line between those region-boundary sides. The path expansion considers all eight cardinal and diagonal neighbors, so a single ledge can run on a diagonal or zig-zag. Diagonal steps into or out of a map-edge cell are rejected, because boundary ledge tiles are only authored as Straight and a diagonal step at the edge would force the endpoint cell into a corner shape that the boundary ledge set cannot represent. Steps that would make the new cell cardinally adjacent to any prior path cell other than the immediate predecessor are also rejected, because that pattern is the path doubling back through a 1-cell gap. The corner ledge tile set can render U-turns only when the parallel runs are at least 2 cells apart; tighter notches/bumps would leave malformed corner pieces.
4. Reject the path if it comes too close to any previously accepted ledge path.
5. Treat the accepted path as the low-side ledge cells.
6. Temporarily remove the path from the region, find the connected areas it creates, and raise one valid area by one height level. Reject any candidate component whose cells are cardinally or diagonally adjacent to a cell outside the current region with `height < region.HeightLevel`. Raising such a component would put a `H + 1` cell next to an `H - 1` cell (typically a previous pass's path) and create a 2-level cliff. If both components touch a lower region, the candidate path is rejected and a different path is tried so the raised side ends up on the safe interior of the region.
7. Validate that both resulting regions are still usable.
8. Count how many cells in the accepted path become road-replaceable straight ledges (cardinal-step cells with valid low/high sides). Reject the path if that count is below `MinRoadReplaceableLedgesPerHeightRegion`.

With two height layers, this often creates one organic map-spanning transition:

```text
one side of ledge: height 0
other side of ledge: height 1
```

The generator preserves partial success. If it places one ledge and then fails to place the second after spending the remaining attempt budget, the final height map keeps the first ledge.

With three or more height layers, the generator can split an existing raised region again, while still respecting ledge spacing. If it cannot create a valid organic path for every requested ledge, it leaves the missing ledges out instead of throwing the whole height map away or falling back to a straight split.

The generated path is not a prefab choice yet. It is only a grid path that later becomes straight, inner-corner, or outer-corner ledge cells during ledge marking.

The path start and end can be on:

- opposite region edges,
- adjacent region edges,
- two separate points on the same region edge.

This allows long splits, corner cuts, bays, peninsulas, and curved ledges that enter and leave through the same side. Same-edge paths must start and end far enough apart to make a usable split.

Candidate start/end points are chosen inside one contiguous same-height plateau. Existing ledges split the map into separate plateau regions, so a new ledge is never pathfound through a different plateau or across an existing ledge. Boundary sides with only a one-cell usable run are culled, which prevents tiny mouths/entrances from being used as ledge endpoints.

Endpoint candidates that sit on the map edge get an additional clearance check: they are rejected if they are within `max(3, MinCellsBetweenHeightChanges + 1)` chebyshev cells of any previously accepted path's map-edge endpoint. This is on top of the global `MinCellsBetweenHeightChanges` spacing, and is what prevents two different ledges from sharing the same boundary edge tile or visually crowding into the same one.

### 3. Cull Unusable Features

Reject height splits that would create unusable regions.

Use:

```text
MinUsableRegionWidth
MinUsableRegionHeight
MinUsableRegionArea
```

Any generated split that cannot support at least a small usable flat section on both sides is rejected and regenerated.

This is important because a `2x2` hill consumes map space but cannot support meaningful roads/buildings. The default goal is that every generated height region can fit at least one flat road/building opportunity after ledges are placed.

The current implementation treats each contiguous same-height area as a region. After a candidate ledge path is applied, every resulting same-height region must pass the configured area/width/height checks.

### 4. Enforce Height Difference Rule

Generate height levels one step at a time.

Each accepted path raises one side of an existing region by exactly one height level. This means cardinal and diagonal neighbors can only differ by one level:

```text
abs(deltaHeight) <= 1
```

Legacy repair helpers still exist in code, but the ledge-path-first generator avoids creating illegal jumps in the first place.

### 5. Enforce Distance Between Height Changes

Apply `MinCellsBetweenHeightChanges` while choosing ledge paths.

If a candidate ledge path would come too close to an already accepted ledge path, it is rejected before it changes the height map.

This prevents:

- crossing ledges,
- back-to-back cliffs,
- tiny terraces between height changes,
- 3-layer intersection knots that road generation cannot plan around.

The spacing test uses grid distance between distinct accepted ledge paths. A long continuous ledge line can still bend through adjacent cells along its own path.

For path-first generation, ledges enter and exit through valid region-boundary sides. On the first split, those boundaries are usually map edges. Later splits can exit the boundary of an existing height region instead. Adjacent-edge and same-edge paths are allowed as long as the path creates usable connected regions.

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

Each accepted ledge path must contribute at least `MinRoadReplaceableLedgesPerHeightRegion` meaningful road-replaceable cells of its own. Diagonal-step path cells classify as inner/outer corner ledges and are intentionally non-replaceable, so a path that runs entirely or mostly diagonally needs enough cardinal-step segments to satisfy the requirement; otherwise the candidate path is rejected.

This prevents height regions that look nice but cannot realistically connect to the road graph. It also prevents large special ledge structures, and now diagonal ledge segments, from accidentally consuming every possible transition point. Each split therefore guarantees its own road-replaceable budget instead of pooling the count across the whole map.

If the path-first generator cannot satisfy this validation after its retry budget, the failed ledge is left out. If no valid ledges remain, `BuildHeightCells` falls back to a flat height map.

### 8. Instantiate Ledge Prefabs

For each ledge cell:

1. Build the required ledge shape.
2. Find matching `HeightTileDefinition` candidates.
3. Expand all four rotations.
4. Filter boundary/non-boundary tile sets.
5. Apply weight and the selected tile's repeat cooldown. `RepeatCooldownDistance = 0` means the tile is always available; a positive value strictly excludes the tile from the pool for that many ledge-cell placements after its last placement.
6. Instantiate the selected prefab at:

```csharp
xz = cell center
y = (LowHeightLevel + HighHeightLevel) * 0.5 * HeightLevelWorldUnits
rotation = (int)HighDirection * 90 degrees   // straight ledges
```

Corner ledges store a deterministic `LedgeRotationSteps` value on `WorldHeightCell`. The current inner-corner convention assumes the prefab at rotation `0` has its high sides facing North and East, then rotates to match the local pair of high-side neighbors.

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

Road generation enforces this before road prefab selection. If a road pass promotes a ledge into a ramp from only one side, it immediately tries to mark the opposite plateau cell as road. The continuation cell must:

- be in bounds,
- not be a map-edge cell (an edge stub against the boundary is rejected so it cannot leave a one-cell tail at the map border),
- not already be a road, ledge, or height-change road,
- be at the matching plateau's height level,
- pass the normal road-placement rules.

If any of those conditions fails, the ramp promotion is reverted. If the continuation road was already placed when a later check fails, that orphan road is also reverted so only the ledge remains.

Roads may only override a ledge cell when that ledge is road-replaceable (a `1x1` straight ledge with valid cardinal low/high sides). Every road-placement entry point — exits, exit-inner cells, pathfinding, stubs, ramp continuations — refuses to mark a non-replaceable ledge as a road, so straight boundary ledges, corner ledges, and diagonal-step inner/outer corner ledges never get covered by a normal road tile.

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
4. Generate flat height map when `HeightLayerCount <= 1` or `PreferredLedgeCount <= 0`.
5. Generate organic ledge paths while under `PreferredLedgeCount` and `MaxGenerationAttempts`.
6. Reject ledge paths that create unusably small height regions.
7. Reject ledge paths that violate `MinCellsBetweenHeightChanges` against previously accepted ledges.
8. Keep already accepted ledges when later ledges cannot satisfy the constraints.
9. Mark ledge cells and instantiate ledge prefabs.
10. Expose `WorldHeightSnapshot`.
11. Update `RoadGridGenerator` to consume the snapshot.
12. Add special straight height-change road tile support.
13. Update building placement to reject ledge/ramp cells and require flat footprints.

## First Version Acceptance Criteria

- `HeightLayerCount = 1` produces the same flat behavior as the current map generator.
- `PreferredLedgeCount = 0` produces the same flat behavior as the current map generator.
- `HeightLayerCount = 2` can create multiple separate ledges that reuse two height levels.
- No cardinal or diagonal neighbors differ by more than one height level.
- Tiny unusable hills/valleys are rejected before they become final height regions.
- Separate ledge paths do not cross, touch, or violate `MinCellsBetweenHeightChanges`.
- If generation cannot place every preferred ledge, the successfully placed ledges remain.
- Ledge prefabs are placed at elevation transitions.
- Boundary ledges are used where elevation transitions meet the map edge.
- Road generation can optionally replace a straight ledge cell with a height-change road tile.
- Larger ledge structures are preserved and cannot be overridden by roads.
- Each useful height region has enough road-replaceable straight ledges, or the region is modified/removed.
- Buildings never spawn on ledge or height-change road cells.
- Seeded generation is deterministic.

## Implementation Decisions

- Height levels are normalized to `0..HeightLayerCount - 1`.
- `HeightLayerCount` caps how high terrain can go; `PreferredLedgeCount` controls how many separate height transitions the generator tries to place.
- The ledge/ramp cell is positioned at the lower height and points toward the high side.
- Non-road ledge cells are ledges, not building cells. They can be used as ledge geometry, but ordinary buildings are not placed on them.
- Road exits are allowed on any height layer. The generator should rely on road-replaceable ledge availability and pathfinding to connect them, then polish edge cases as they appear in testing.
- Separate height regions do not need to be force-connected unless they contain city entry roads that need to connect to the rest of the road graph.
- Boundary ledges use straight boundary pieces only. Like road entrance tiles, they do not need special corner pieces in the first version.
