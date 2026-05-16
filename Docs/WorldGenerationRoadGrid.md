# World Generation Road Grid

## Context

The first world-generation goal is to build a sensible city road grid from modular road tiles. Buildings, interiors, special districts, loot, zombies, and multi-level city logic come later.

The current target is one height level and two tile families:

- **Normal road tiles** - straight, corner, T intersection, 4-way intersection.
- **Exit/boundary road tiles** - road tiles on the edge of the map that visually imply roads leading outside the arena or blocked by boundary walls.

The generator should be configurable enough that future systems can add tile variations, special environments such as military quarantine zones, building placement, and road elevation without replacing the whole architecture.

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
    bool IsRoad;
    bool IsBoundaryExit;
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

Run-specific values live on the scene `RoadGridGenerator` component:

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

These values are shared by the road and building passes. Keeping them on the scene component makes it possible to adjust map size and seed from the same Inspector object used to regenerate the world.

Suggested defaults:

```text
Width: 12
Height: 12
Seed: 12345
RandomizeSeedOnGenerate: false
TileSize: 20
```

During a networked match, `Gameplay` owns the authoritative `WorldSeed`. The state authority initializes it once from the scene `RoadGridGenerator` settings:

- If `RandomizeSeedOnGenerate` is disabled, the host's configured `Seed` becomes the networked `WorldSeed`.
- If `RandomizeSeedOnGenerate` is enabled, the host derives `WorldSeed` from the Photon Fusion session name and map size.

At runtime, `RoadGridGenerator.GenerateOnStart` waits for the `Gameplay` network object to become valid and expose a non-zero networked `WorldSeed` before generating.

If a client already generated a local skirmish map before joining a host, `RoadGridGenerator` detects the host `WorldSeed`, regenerates the road grid from that seed, and asks building placement to regenerate from the new road grid. This prevents the client's skirmish map from surviving into the joined match.

After generating during play mode, `RoadGridGenerator` waits one frame before reporting `IsGenerationComplete`. This gives Unity time to destroy any old editor/generated road root so `Gameplay` does not pick stale road-exit spawn points from the previous map.

Outside a running Fusion session, such as editor preview generation, `RandomizeSeedOnGenerate` still uses a local random seed.

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
    RoadTileSet NormalRoadTiles;
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

7. Assign the tile set to `NormalRoadTiles`.
8. Add `RoadGridGenerator` to an empty GameObject in the scene.
9. Assign the settings asset, then set `Width`, `Height`, and `Seed` on the `RoadGridGenerator` component.
10. Use the component context menu:

```text
Generate Road Grid
Clear Generated Road Grid
```

The generator creates a child object named `Generated Road Grid` and places all generated road prefabs under it.

## Generation Pipeline

### 1. Initialize Empty Grid

Start with all cells empty.

```text
IsRoad = false
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

After road cells are chosen, calculate sockets from neighbors:

```text
If north neighbor is road: North = Road
If east neighbor is road: East = Road
...
If this is boundary exit: outward side = Exit
Otherwise missing neighbor = Closed
```

This makes tile selection deterministic and easy to inspect.

### 6. Collapse Tiles

For each road cell:

1. Build its socket signature.
2. Find all tile definitions that match.
3. Filter by environment.
4. Apply weight/repeat cooldown.
5. Choose one.
6. Instantiate it at the grid world position.

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

## Future Elevation

The first version is one height level.

To prepare for elevation later, keep grid coordinates as:

```csharp
Vector3Int CellPosition;
```

even if `y = 0` for now.

Future road sockets can become:

```csharp
North, East, South, West, Up, Down
```

or the horizontal sockets can include slope metadata:

```csharp
RoadSocket RoadLevel
RoadSocket RoadRampUp
RoadSocket RoadRampDown
```

Slope tiles should be required when connecting roads at different heights. Special buildings that connect levels should later declare which road heights they connect to.

## First Implementation Plan

1. `RoadSocket.cs` adds road socket, road environment, and road direction enums.
2. `RoadTileDefinition` is a prefab-root component that stores base sockets, environment, boundary flag, weight, and repeat cooldown.
3. `RoadTileSet` stores the list of available road tile definition components.
4. `RoadGenerationSettings` stores road-specific exit count, spacing settings, local density limits, density behavior, and tile set.
5. `RoadGridGenerator` owns run-specific map size, seed, generation, and prefab instantiation.
6. Empty grid initialization is implemented.
7. Exit placement uses randomized candidate selection with same-edge spacing and graceful count reduction.
8. Exit connection uses randomized A* toward the existing connected road network.
9. Optional extra roads are controlled by `ExtraRoadDensity`; mid density prioritizes complete road-to-road connectors, late density can add short stubs, and at `1.0` every legal connected road cell is filled.
10. Sockets are derived from the carved road graph.
11. Tile collapse expands all four rotations from each road definition, matches rotated sockets, and uses weighted seeded selection.
12. Generated prefabs are parented under a generated map root.
13. Gizmos draw road and exit cells after generation.

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
- Does not yet handle elevation.
