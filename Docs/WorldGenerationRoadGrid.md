# World Generation Road Grid

## Context

The road-grid pass builds a sensible city road network from modular road tiles. It now runs after the optional height-map pass and before building placement.

The flat-map fallback still uses one height level and two tile families:

- **Normal road tiles** - straight, corner, T intersection, 4-way intersection.
- **Exit/boundary road tiles** - road tiles on the edge of the map that visually imply roads leading outside the arena or blocked by boundary walls.

When `HeightMapGenerator` is present, road generation consumes its height snapshot. When no height map exists, every cell is treated as height `0`, matching the current behavior.

Height generation is documented separately in `Docs/WorldGenerationHeightMap.md`.

The generator should be configurable enough that future systems can add tile variations, special environments such as military quarantine zones, building placement, and additional elevation traversal without replacing the whole architecture.

## Core Approach

Use a hybrid of graph generation and WFC-style tile resolution.

Pure WFC is good at local socket matching, but poor at guaranteeing global goals like:

- exactly N exits,
- every exit connected to every other exit,
- no adjacent parallel road lanes,
- minimum/maximum spacing between disconnected roads.

So the first version should work in two layers:

1. **Road graph generation** decides which grid cells contain road connections.
2. **Tile collapse** chooses a concrete road tile prefab that matches those connections.

This still behaves like WFC at the tile level, but the global road network is easier to reason about and debug.

## Grid Model

The map is a rectangular grid:

```csharp
width x height
```

Each cell stores:

```csharp
RoadCell
{
    int HeightLevel;
    bool IsRoad;
    bool IsBoundaryExit;
    bool IsLedge;
    bool IsHeightChangeRoad;
    RoadSocket North;
    RoadSocket East;
    RoadSocket South;
    RoadSocket West;
    RoadEnvironment Environment;
}
```

For version one:

```csharp
RoadEnvironment.Normal
```

is the only environment.

The four sockets can be reduced to:

```csharp
Closed
Road
Exit
```

If generated height data is available, normal road cells also carry their height level. A ledge cell can become a road only if it is replaced by a straight height-change road tile.

For normal internal road cells, sockets are only `Closed` or `Road`. Edge cells may have one outward `Exit` socket.

## Unity Configuration

Use prefab-root definition components plus ScriptableObject sets/settings rather than hardcoding tile prefabs.

Current scripts live under:

```text
Assets/Scripts/WorldGeneration/
```

The first implementation adds:

```text
RoadSocket.cs
RoadTileDefinition.cs
RoadTileSet.cs
RoadGenerationSettings.cs
RoadGridGenerator.cs
```

### RoadTileDefinition

Each tile prefab root gets a `RoadTileDefinition` component:

```csharp
RoadTileDefinition
{
    RoadSocket North;
    RoadSocket East;
    RoadSocket South;
    RoadSocket West;
    RoadEnvironment Environment;
    bool IsBoundaryTile;
    int Weight;
    int RepeatCooldown;
}
```

Road tile definitions are prefab components, not separate ScriptableObject assets. The generator instantiates the `RoadTileDefinition.gameObject`.

Road tile definitions should not need duplicate assets just to support rotated placement.

Every road tile definition is considered rotatable in all four cardinal directions: `0`, `90`, `180`, and `270` degrees.

When a candidate is rotated:

- the base sockets rotate with the tile,
- instantiated rotation is the candidate rotation: `0`, `90`, `180`, or `270` degrees,
- repeat cooldown uses the source `RoadTileDefinition`, so rotated versions still count as the same visual tile.

Example:

```text
Original corner tile:
  North = Road
  East = Road

90 degree candidate:
  East = Road
  South = Road
```

This means one corner-road definition can cover all four corner orientations, one T-intersection definition can cover all four T orientations, and one dead-end definition can cover all four dead-end directions.

Prefab authoring convention: a road prefab's default rotation must match the base sockets written in its `RoadTileDefinition`. If a prefab appears rotated incorrectly, fix the prefab/model orientation or socket definition rather than adding a per-tile rotation offset.

`RepeatCooldown` is for future variation control:

```text
Do not place this exact visual variant again within N cells / N placements.
```

The first version can store the value without enforcing it strongly.

The implementation applies `RepeatCooldown` during tile collapse. If every matching tile is inside cooldown, the generator falls back to the full matching pool so generation does not fail just because visual variation is unavailable.

### RoadGridGenerator Run Settings

Run-specific values currently live on the scene `RoadGridGenerator` component:

```csharp
RoadGridGenerator
{
    int Width;
    int Height;
    int Seed;
    bool RandomizeSeedOnGenerate;
    float TileSize;
    RoadGenerationSettings Settings;
}
```

After height generation is implemented, shared run-specific values should move to the scene `HeightMapGenerator` component:

```text
Width
Height
Seed
RandomizeSeedOnGenerate
TileSize
```

`RoadGridGenerator` should keep fallback values for editor/testing compatibility when no height generator is assigned, but the normal pipeline should read map size, tile size, seed, and height data from the generated height snapshot.

Suggested defaults:

```text
Width: 12
Height: 12
Seed: 12345
RandomizeSeedOnGenerate: false
TileSize: 20
```

During a networked match, `Gameplay` owns the authoritative `WorldSeed`. The state authority initializes it once from the first world generator in the pipeline:

- When `HeightMapGenerator` exists, it provides the configured seed/randomize setting.
- When no height generator exists, `RoadGridGenerator` provides the flat-map fallback seed/randomize setting.
- If `RandomizeSeedOnGenerate` is disabled, the configured `Seed` becomes the networked `WorldSeed`.
- If `RandomizeSeedOnGenerate` is enabled, the host derives `WorldSeed` from the Photon Fusion session name and map size.

At runtime, `RoadGridGenerator.GenerateOnStart` waits for the `Gameplay` network object to become valid and expose a non-zero networked `WorldSeed` before generating.

If a client already generated a local skirmish map before joining a host, `RoadGridGenerator` detects the host `WorldSeed`, regenerates the road grid from that seed, and asks building placement to regenerate from the new road grid. This prevents the client's skirmish map from surviving into the joined match.

After generating during play mode, `RoadGridGenerator` waits one frame before reporting `IsGenerationComplete`. This gives Unity time to destroy any old editor/generated road root so `Gameplay` does not pick stale road-exit spawn points from the previous map.

Outside a running Fusion session, such as editor preview generation, `RandomizeSeedOnGenerate` still uses a local random seed.

Road generation then uses the height snapshot seed, or the flat fallback seed, so all peers produce the same road and building layout.

### RoadGenerationSettings

Use one generator settings asset:

```csharp
RoadGenerationSettings
{
    int RequestedExitCount;
    int MinExitSpacing;
    int MinRoadSpacing;
    int MaxRoadSpacing;
    bool PreventSolidRoadBlocks;
    int MaxRoadCellsIn3x3;
    float ExtraRoadDensity;
    float StubRoadStartDensity;
    bool RequireDiagonalSpaceForStubRoads;
    int MinConnectingRoadLength;
    int MaxPathAttempts;
    RoadTileSet RoadTiles;
}
```

Suggested defaults:

```text
RequestedExitCount: 4
MinExitSpacing: 2
MinRoadSpacing: 1
MaxRoadSpacing: 2
PreventSolidRoadBlocks: true
MaxRoadCellsIn3x3: 5
ExtraRoadDensity: 0.15
StubRoadStartDensity: 0.75
RequireDiagonalSpaceForStubRoads: false
MinConnectingRoadLength: 4
MaxPathAttempts: 200
```

Height-change ramps live in the same `RoadTiles` set as normal road tiles. Each `RoadTileDefinition` carries an `IsHeightChangeRamp` flag — `cell.IsHeightChangeRoad` is matched against this flag, so the generator picks ramps for height-change cells and normal tiles for flat cells. The ramp's default authoring orientation is:

```text
South = lower elevation
North = higher elevation
```

Ramp rotation is constrained to `(int)cell.HighDirection` rather than left to socket matching, because a ramp's N/S sockets are symmetric and would otherwise allow the tile to spawn flipped 180 degrees.

For tiny maps, the generator should clamp requests rather than fail loudly. A `3x3` map cannot satisfy the same constraints as a larger city grid.

## Road Tile Sockets

Every road tile is described by the directions it connects to:

```text
Straight NS: N + S
Straight EW: E + W
Corner NE: N + E
Corner ES: E + S
T NES: N + E + S
Intersection: N + E + S + W
Boundary Exit N: outward N exit + inward S road
```

Tile selection is then simple:

```text
Expand each RoadTileDefinition into all four rotated candidates.
Rotate candidate sockets and compare them to the RoadCell sockets.
Choose by weight and repeat cooldown.
Instantiate prefab with the candidate rotation.
```

This is the first WFC/collapse point: the road graph determines the required sockets, and the tile set collapses that cell to a valid prefab.

## Unity Setup Workflow

1. Create road prefabs from the Blender tiles.
2. Add `RoadTileDefinition` to the empty root object of each road prefab.
3. For each prefab's definition component, assign:

```text
Base North/East/South/West sockets
Environment = Normal
IsBoundaryTile
IsHeightChangeRamp
Weight
RepeatCooldown
```

4. Create a `RoadTileSet` asset through:

```text
Create > SimpleFPS > World Generation > Road Tile Set
```

5. Add the `RoadTileDefinition` components from the road prefabs to the tile set.
6. Create a `RoadGenerationSettings` asset through:

```text
Create > SimpleFPS > World Generation > Road Generation Settings
```

7. Assign the tile set to `RoadTiles`.
8. Add `RoadGridGenerator` to an empty GameObject in the scene.
9. Assign the settings asset. For flat fallback generation, set `Width`, `Height`, and `Seed` on the `RoadGridGenerator` component. With height generation enabled, set shared map size and seed on `HeightMapGenerator` instead.
10. Use the component context menu:

```text
Generate Road Grid
Clear Generated Road Grid
```

The generator creates a child object named `Generated Road Grid` and places all generated road prefabs under it.

## Generation Pipeline

### 0. Read Height Snapshot

Before road cells are chosen, read the optional `WorldHeightSnapshot` from `HeightMapGenerator`.

If no height snapshot exists:

```text
HeightLevel = 0
IsLedge = false
```

for every road grid cell.

If a height snapshot exists:

- same-height cells can connect normally,
- non-road ledge cells block road/building placement,
- different-height roads cannot be directly adjacent; a ledge cell must sit between the two levels,
- a `1x1` straight road-replaceable ledge cell can be replaced by a height-change road tile,
- normal roads are placed at `HeightLevel * HeightLevelWorldUnits`.

### 1. Initialize Empty Grid

Start with all cells empty.

```text
IsRoad = false
HeightLevel = height snapshot level, or 0 if flat
IsLedge = height snapshot ledge flag, or false if flat
IsHeightChangeRoad = false
All sockets = Closed
Environment = Normal
```

### 2. Choose Exit Cells

Pick edge cells for exits.

Rules:

- Try to place `RequestedExitCount`.
- Do not place exits directly next to each other on the same edge.
- Prefer positions that point inward to at least one viable interior cell.
- Avoid corners on very small maps unless needed.
- If constraints make the requested count impossible, reduce the exit count and log/debug-report it.

For each accepted exit:

```text
Mark edge cell as road.
Set outward socket = Exit.
Set inward socket = Road.
```

Example:

```text
North edge exit:
  North = Exit
  South = Road
```

### 3. Connect All Exits

Create a connected road network by pathing between exits.

Recommended first version:

1. Pick one exit as the root.
2. For every other exit, carve a path from it to the nearest already-connected road cell.
3. Use randomized A* or weighted path search.
4. Reject path candidates that violate road spacing too badly.
5. Retry with different random weights until `MaxPathAttempts`.

The path cost should prefer:

- continuing straight,
- connecting to existing roads,
- staying away from adjacent parallel roads,
- leaving 1-2 empty tiles between unrelated roads,
- occasional turns/intersections for interesting shapes.

The path cost should punish:

- adjacent parallel roads,
- paths immediately beside existing unconnected roads,
- overusing intersections,
- too many sharp zigzags.

When height data exists, path cost should also account for elevation:

- Same-height movement is cheapest.
- Crossing a straight ledge is allowed only if the ledge is a `1x1` road-replaceable ledge that can become a height-change road.
- A height-change road must connect low side and high side; it cannot be a stub itself.
- Crossing corners/inner/outer ledges is not allowed in the first version.
- Crossing larger ledge structures is not allowed through the road generator.
- Crossing a ledge should have extra cost so roads do not zigzag up/down unnecessarily.

### 4. Add Optional Extra Roads

After all exits are connected, optionally add roads based on `ExtraRoadDensity`.

Density first tries to add complete connector roads. Connector roads:

- start from existing road cells,
- end at another existing road cell,
- respect road spacing,
- must add at least `MinConnectingRoadLength` new road cells,
- are skipped if they would exceed the current density target.

Density is evaluated against the legal road capacity for the current seed:

1. Build the required connected road skeleton between exits.
2. Temporarily saturate every legal connected road cell to measure the maximum possible road count.
3. Reset back to the required skeleton.
4. Fill toward:

```text
requiredRoadCount + (maxLegalRoadCount - requiredRoadCount) * ExtraRoadDensity
```

This makes density a gradual slider instead of a jump between sparse branches and full saturation. At `ExtraRoadDensity = 1.0`, the generator still fills every legal connected road cell. At values like `0.5`, it should produce something between the sparse skeleton and max-density output.

Short dead-end roads are only allowed after `ExtraRoadDensity` reaches `StubRoadStartDensity`. This keeps low and mid density focused on useful road-to-road connectors instead of one-cell side teeth. At high density, these short end roads help fill smaller pockets where no longer connector fits.

`RequireDiagonalSpaceForStubRoads` controls whether one-connection stub roads are allowed to end while touching other road cells diagonally. When enabled, a candidate with exactly one cardinal road neighbor is rejected only if it has a diagonal road neighbor and cannot legally continue into another empty cell. This lets late-density stubs grow into longer branches or connect into nearby roads, while still preventing final dead ends from stopping with awkward diagonal corner contact.

The temporary max-capacity pass does not apply the diagonal stub rule. That pass only measures how many road cells the map can legally support under the core anti-carpet rules. The final late-density stub fill applies `RequireDiagonalSpaceForStubRoads`, so enabling the setting can reduce actual stub placement without incorrectly making the density system think no extra connector roads are possible.

Road tile sets should include dead-end definitions with exactly one `Road` socket for these late-density stub roads. If no matching dead-end tile exists, the generator can still mark the cell, but prefab collapse will report a missing tile for that socket shape.

### 5. Derive Sockets

After road cells are chosen, validate height-change roads before calculating sockets.

Height-change ramp cells are repaired as a small grid-level transaction before prefab selection:

- if the ramp already has compatible road cells on both its low side and high side, keep it,
- if it has a compatible road on only one side, try to mark the opposite plateau cell as a road,
- the continuation cell must be in bounds, **not a map-edge cell**, not already a road or ledge, at the matching plateau's height level, and pass the normal road-placement rules,
- if the opposite cell cannot legally become a road, demote the ramp back to a normal non-road ledge,
- if the continuation road was already placed before a later check fails, revert that continuation alongside the ramp so no orphan stub is left behind,
- only surviving two-sided ramps suppress their ledge prefab during collapse.

This prevents an invalid `road -> ramp -> building` layout and an invalid `road -> ramp -> map edge` layout. The final layout is either `road -> ramp -> road`, or the ramp candidate remains a normal ledge.

Roads may only override a ledge cell when that ledge is road-replaceable (a `1x1` straight ledge with valid cardinal low/high sides). Every road-placement entry point — exits, exit-inner cells, pathfinding, stubs, ramp continuations — refuses to mark a non-replaceable ledge as a road. Exit candidates whose boundary cell or inner cell falls on a non-replaceable ledge are filtered out during exit selection, so straight boundary ledges, corner ledges, and diagonal-step inner/outer corner ledges produced by the height pass never get covered by a normal road tile.

After that, calculate sockets from neighbors:

```text
If north neighbor is road at compatible height: North = Road
If east neighbor is road: East = Road
...
If this is boundary exit: outward side = Exit
Otherwise missing neighbor = Closed
```

For height-change road cells:

```text
low-side socket = Road
high-side socket = Road
side sockets = Closed
```

The height-change road tile is still matched as a straight road tile, but it uses the height-change road tile set and its rotation is derived from the low/high sides.

This makes tile selection deterministic and easy to inspect.

### 6. Collapse Tiles

For each road cell:

1. Build its socket signature.
2. Find all tile definitions that match.
3. Filter by environment.
4. Apply weight/repeat cooldown.
5. Choose one.
6. Instantiate it at the grid world position and cell height.

For normal road cells:

```csharp
worldY = HeightLevel * HeightLevelWorldUnits
```

For height-change road cells:

```csharp
worldY = LowerHeightLevel * HeightLevelWorldUnits
```

When a ledge cell becomes a height-change road, the road generator should suppress or remove the ledge prefab placed by the height generator for that cell.

Empty cells stay empty for now. Building placement will fill them later.

## Road Spacing Rules

The desired rule is:

```text
Unconnected roads should have 1-2 empty cells between them.
Roads should not run directly parallel in adjacent cells.
```

The first version should enforce this as a pathing cost rather than a hard absolute rule in every case. Hard rejection can make small maps impossible.

Use:

```text
MinRoadSpacing = 1
MaxRoadSpacing = 2
PreventSolidRoadBlocks = true
MaxRoadCellsIn3x3 = 5
```

Suggested interpretation:

- Adjacent parallel roads are strongly discouraged or rejected.
- Roads farther than `MaxRoadSpacing` from any other road are allowed only when needed for connecting exits.
- A road touching another road through a valid socket is allowed.
- A road beside another road with no connecting socket is bad.
- Fully filled `2x2` road squares are rejected when `PreventSolidRoadBlocks` is enabled.
- Any `3x3` local window with more than `MaxRoadCellsIn3x3` road cells is rejected. The default of `5` allows normal 4-way intersections but prevents solid road carpets.

This allows intersections and turns while preventing ugly side-by-side road strips.

## Exit Count Edge Cases

Small maps need graceful degradation.

For a `3x3` map:

- The center cell is the only interior connector.
- More than two exits can easily force impossible adjacency.
- The generator should clamp exit count or relax corner/spacing rules.

Recommended behavior:

```text
Try requested exit count.
If impossible, reduce exit count by one and retry.
If still impossible, relax edge/corner preference.
If still impossible, generate the smallest connected valid road network and report the reduced count.
```

This keeps the generator usable for debug maps.

## Future Special Environments

Future environments such as a military quarantine zone should layer on top of the same grid system.

Add environment definitions later:

```csharp
RoadEnvironmentDefinition
{
    RoadEnvironment Id;
    RoadTileSet TileSet;
    int MinSize;
    int MaxSize;
    RoadTileDefinition EntranceTile;
    bool RequiresEntranceFromNormalRoad;
}
```

Generation order later:

1. Generate normal connected road network.
2. Pick candidate region for special environment.
3. Check min/max size.
4. Replace part of the normal road graph with environment-specific cells.
5. Force the first connection from normal road into special environment to use an entrance/checkpoint tile.
6. Collapse environment cells using their own tile set.

If no valid region exists, skip the environment.

This keeps the first version simple while preserving a path to military zones, quarantine areas, industrial districts, or faction-specific blocks.

## Future Building Placement

Buildings are documented as a separate second pass in `Docs/WorldGenerationBuildingPlacement.md`.

The road generator should only provide road occupancy and environment data to that system.

The road generator only needs to mark:

```text
Road cells
Empty buildable cells
Boundary cells
Environment tags
```

Building WFC can then run on empty cells and use road adjacency as constraints:

- storefronts face roads,
- alleys require side access,
- large buildings require rectangular footprints,
- special environment buildings require matching environment tags.

Do not mix building placement into the first road generator.

## Height Map Integration

Height generation is now documented as its own first pass in `Docs/WorldGenerationHeightMap.md`.

Road generation should treat the height map as an input constraint:

- Roads on the same height connect normally.
- Roads on different heights can connect only through a straight height-change road tile placed on a ledge cell.
- Different-height road cells cannot be adjacent without a ledge/ramp cell between them.
- The height-change road tile replaces an existing `1x1` straight road-replaceable ledge cell from the height pass.
- Larger ledge structures are never replaced by roads.
- The height-change road tile cannot be a stub/dead-end; it must connect both low and high sides.
- Road exits can later be constrained by height, but the first version can allow exits on any valid generated height unless configured otherwise.
- If height generation is missing or has one layer, road generation stays flat.

Special structures that connect height levels belong to the height/ledge system, not ordinary road generation or ordinary building placement.

## First Implementation Plan

1. `RoadSocket.cs` adds road socket, road environment, and road direction enums.
2. `RoadTileDefinition` is a prefab-root component that stores base sockets, environment, boundary flag, weight, and repeat cooldown.
3. `RoadTileSet` stores the list of available road tile definition components.
4. `RoadGenerationSettings` stores road-specific exit count, spacing settings, local density limits, density behavior, and tile set.
5. `RoadGridGenerator` currently owns run-specific map size, seed, generation, and prefab instantiation. After height generation is implemented, shared run-specific values move to `HeightMapGenerator`, and `RoadGridGenerator` keeps fallback values for flat/editor generation.
6. Empty grid initialization is implemented.
7. Exit placement uses randomized candidate selection with same-edge spacing and graceful count reduction.
8. Exit connection uses randomized A* toward the existing connected road network.
9. Optional extra roads are controlled by `ExtraRoadDensity`; mid density prioritizes complete road-to-road connectors, late density can add short stubs, and at `1.0` every legal connected road cell is filled.
10. Sockets are derived from the carved road graph.
11. Tile collapse expands all four rotations from each road definition, matches rotated sockets, and uses weighted seeded selection.
12. Generated prefabs are parented under a generated map root.
13. Gizmos draw road and exit cells after generation.

Height-map integration adds these road-specific implementation steps:

1. Read `WorldHeightSnapshot` before exit placement.
2. Treat ledge cells as blocked unless selected as a height-change road.
3. Allow paths to cross only `1x1` straight road-replaceable ledge cells using a height-change road tile.
4. Place normal road tiles at their cell height.
5. Place height-change road tiles at the lower height and rotate them to connect low/high sides.

## Debug Tools

The generator should expose:

- seed,
- regenerate button/editor context menu,
- clear generated map button,
- draw grid gizmos,
- log requested vs actual exit count,
- log failed path attempts.

These are important because procedural generation bugs are hard to reason about from final prefabs alone.

## First Version Acceptance Criteria

- Generates a connected road network from configurable map size.
- Places up to the configured number of edge exits.
- Avoids directly adjacent exits.
- Connects every placed exit to the same road network.
- Avoids adjacent unconnected parallel road strips where possible.
- Leaves empty cells for future buildings.
- Uses prefab-root tile definition components instead of hardcoded prefabs.
- Can regenerate with a seed.
- Does not yet place buildings.
- Does not yet place special environments.
- Flat generation still works when no height map is present.
- With a height map, roads respect ledge cells and use height-change road tiles for valid single-level transitions.
