# World Generation Building Placement

## Context

Building placement runs after height generation and road generation.

The height generator decides cell elevation and ledges. The road generator decides which valid cells are roads. The building placer consumes that combined road/height grid and fills the remaining empty cells with building footprints. This should stay separate from `RoadGridGenerator` so height generation, road generation, building placement, special districts, and future interior logic can evolve independently.

The first implementation does not need final building art. It can use placeholder block prefabs to validate placement quality.

## Goals

- Place buildings only in cells that are not roads.
- Never place buildings on ledge cells or height-change road cells.
- Keep first-version building footprints on a single flat height level.
- Support `1x1`, `1x2`, `2x1`, and `2x2` footprints for the first version.
- Place complex buildings near roads.
- Place simple blocking buildings at map edges and in leftover/backfill spaces.
- Let building definitions declare which footprint sides require road adjacency.
- Prefer larger buildings according to a configurable density/priority setting.
- Prevent the same building definition from repeating too close to itself.
- Fill all remaining empty cells with simple blocking buildings.
- Keep the system ready for larger footprints, enterable buildings, parks, alleys, and special environments later.

## Building Categories

### Complex Buildings

Complex buildings are placed near roads.

They are the buildings that can later contain:

- alleys,
- entrances,
- interiors,
- recruitable survivors,
- loot,
- special gameplay spaces.

Even when a complex building can be walked around, it still has authored sides. A storefront, park gate, alley entrance, or building entrance should not face a solid wall unless that specific prefab was authored for it.

Complex buildings therefore need road requirements per side.

Examples:

```text
Corner shop 1x1:
  North requires road
  East requires road

Park 2x2:
  North requires road
  East requires road
  South requires road
  West optional

Apartment block 1x2:
  Long front side requires road
  Back side optional
```

### Simple Blocking Buildings

Simple buildings are hard blockers.

They are used for:

- map edges,
- spaces behind complex buildings,
- leftover cells that cannot fit a complex building,
- temporary city mass until proper building art exists.

They do not need alley or entrance rules. Their job is to make the city boundary and unused space feel closed.

## Grid Input

The building placer receives a generated grid from the road pass.

If both generators are set to generate on match start, building placement waits until the road grid exists before filling buildings. This prevents clients from building against an old or locally randomized road layout.

When a client replaces its local skirmish road grid after joining a host, the road generator calls building placement again so buildings line up with the host-seeded road grid.

During runtime spawning, `Gameplay` waits for generated roads/buildings to report generation complete before connecting players to survivor teams. This prevents skirmish mode from using stale `SpawnPoint` objects that existed in an editor-generated map before the fresh runtime map replaced it.

Conceptually:

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

The building placer should not care which road tile prefab was chosen. It only needs to know:

- which cells are occupied by roads,
- which cells are occupied by ledges or height-change roads,
- which cells are empty,
- which height level each empty cell occupies,
- where roads are adjacent to an empty plot,
- which environment/district each cell belongs to.

For the first version, `RoadEnvironment.Normal` is the only environment.

Height generation is documented separately in `Docs/WorldGenerationHeightMap.md`.

For the first height implementation:

- ledge cells are always blocked for buildings,
- height-change road cells are road cells and blocked for buildings,
- a building footprint must occupy one height level,
- road-side requirements should only count roads on compatible adjacent height,
- a road on the far side of a ledge does not count as adjacent to the building,
- buildings never bridge height gaps,
- building side requirements may require a neighboring ledge that goes up or down relative to the building footprint.

## Unity Configuration

Use prefab-root components for building definitions, and ScriptableObjects for building sets/settings.

Current scripts:

```text
Assets/Scripts/WorldGeneration/
    WorldGridCell.cs
    BuildingCategory.cs
    BuildingSideRequirement.cs
    BuildingDefinition.cs
    BuildingSet.cs
    BuildingPlacementSettings.cs
    BuildingPlacementGenerator.cs
```

The exact names can change during implementation, but the data shape should stay close to this.

### BuildingCategory

```csharp
public enum BuildingCategory
{
    Complex,
    SimpleBlocking
}
```

### BuildingSideRequirement

For each side of a building footprint:

```csharp
public enum BuildingSideRequirement
{
    Any,
    RequiresRoad,
    RequiresNoRoad,
    RequiresLedgeDown,
    RequiresLedgeUp
}
```

`RequiresLedgeDown` means this side must face an adjacent ledge where the building side is on the higher elevation and the ledge drops away from it. This supports buildings such as balconies, overlooks, cliffside storefronts, and parks that intentionally face a drop.

`RequiresLedgeUp` means this side must face an adjacent ledge where the building side is on the lower elevation and the ledge rises away from it. This supports buildings that are meant to sit at the base of a ledge.

`RequiresNoRoad` is useful for backs of buildings that should not face roads. It does not forbid ledges unless a later explicit `RequiresNoLedge` rule is added.

### BuildingDefinition

Each building prefab root gets a `BuildingDefinition` component:

```csharp
BuildingDefinition
{
    BuildingCategory Category;
    Vector2Int FootprintSize;
    RoadEnvironment Environment;

    BuildingSideRequirement North;
    BuildingSideRequirement East;
    BuildingSideRequirement South;
    BuildingSideRequirement West;

    int Weight;
    int RepeatCooldownDistance;
}
```

Building definitions are prefab components, not separate ScriptableObject assets. The generator instantiates the `BuildingDefinition.gameObject`.

`FootprintSize` is in grid cells.

For the first version:

```text
1x1
1x2
2x1
2x2
```

The generator creates placement candidates for all four cardinal rotations from the same `BuildingDefinition`: `0`, `90`, `180`, and `270` degrees.

When a candidate is rotated:

- `90` and `270` degree rotations swap the effective footprint width and height.
- side requirements rotate with the building.
- instantiated rotation is the candidate rotation: `0`, `90`, `180`, or `270` degrees.

Example:

```text
Original 1x2 building:
  FootprintSize = 1x2
  North requires road

90 degree candidate:
  Effective footprint = 2x1
  East requires road
```

This keeps authoring manageable: one building asset can produce all valid rotations, and the road-facing rules still line up with the rotated prefab.

Prefab authoring convention: a building prefab's default rotation must match the footprint and side requirements written in its `BuildingDefinition`. If a prefab appears rotated incorrectly, fix the prefab/model orientation or side requirements rather than adding a per-building rotation offset.

### BuildingSet

```csharp
BuildingSet
{
    BuildingDefinition[] Buildings;
}
```

The set can contain both complex and simple blocking building definition components. Drag the `BuildingDefinition` component from the prefab root into the set.

### BuildingPlacementSettings

```csharp
BuildingPlacementSettings
{
    BuildingSet BuildingSet;
    float LargeBuildingPreference;
    int RepeatCooldownDistance;
    int SeedOffset;
    bool FillMapEdgesWithBlockingBuildings;
    bool FillRemainingEmptyCellsWithBlockingBuildings;
}
```

Suggested defaults:

```text
LargeBuildingPreference: 0.5
RepeatCooldownDistance: 2
SeedOffset: 10000
FillMapEdgesWithBlockingBuildings: true
FillRemainingEmptyCellsWithBlockingBuildings: true
```

`LargeBuildingPreference` controls how aggressively the placer tries larger footprints first:

```text
0.0 = use only 1x1 buildings where possible
0.5 = try to fill roughly half of available space with larger buildings before smaller ones
1.0 = place as many largest buildings as possible before moving down in size
```

This value should not guarantee exact surface area percentages. It is a placement preference, not a strict quota.

The building pass uses the shared world seed plus `SeedOffset` for its own random stream. After height generation is implemented, the shared map size and base seed live on `HeightMapGenerator`. If no height generator exists, the building pass can keep using the road generator's flat-map fallback values.

## Gameplay Spawns

Buildings should not contain networked pickup prefabs directly.

Generated buildings are regular Unity objects. Pickups are Photon Fusion gameplay objects and should be spawned by a separate loot spawning pass with `Runner.SpawnAsync(...)`.

See `Docs/WorldGenerationLootSpawning.md` for the pickup marker and loot generator design.

`BuildingPlacementGenerator` can call an assigned `WorldLootSpawner` after buildings are placed and the NavMesh has been rebuilt:

```csharp
WorldLootSpawner LootSpawner;
bool SpawnLootAfterGenerate;
bool FindLootSpawnerIfMissing;
```

If `LootSpawner` is not assigned, the building generator first checks for a `WorldLootSpawner` on the same GameObject. If `FindLootSpawnerIfMissing` is enabled, it can also find one elsewhere in the scene.

`WorldLootSpawner` does not generate roads or buildings by itself. The normal runtime path is `BuildingPlacementGenerator.Generate()` -> `WorldLootSpawner.SpawnLoot()`. The loot spawner only scans the already generated building root and spawns pickups on the Fusion scene authority.

## NavMesh Rebuild

The building generator can rebuild a Unity `NavMeshSurface` after it places buildings.

`BuildingPlacementGenerator` exposes:

```csharp
NavMeshSurface NavMeshSurface;
bool RebuildNavMeshAfterGenerate;
bool FindNavMeshSurfaceIfMissing;
```

Recommended setup:

1. Add or keep a `NavMeshSurface` in the scene.
2. Configure its layer/object filters so roads and walkable surfaces are included, and blocking buildings/props are included as obstacles or non-walkable geometry.
3. Assign that surface to `BuildingPlacementGenerator.NavMeshSurface`.
4. Leave `RebuildNavMeshAfterGenerate` enabled.

When buildings are generated, the generator instantiates the building prefabs first, then calls:

```csharp
NavMeshSurface.BuildNavMesh();
```

During play mode the rebuild is delayed by one frame. This gives Unity time to finish destroying the previous generated road/building objects and register the newly instantiated objects before the surface is baked. `BuildingPlacementGenerator.IsGenerationComplete` becomes true after this delayed pass, and `Gameplay` uses that as part of its spawn readiness check.

This keeps pathfinding aligned with the generated road/building layout. If no surface is assigned and `FindNavMeshSurfaceIfMissing` is enabled, the generator tries to find one in the scene.

## Placement Model

The building placer owns a second occupancy grid:

```csharp
BuildingCell
{
    bool IsRoad;
    bool IsOccupiedByBuilding;
    BuildingDefinition Building;
    Vector2Int BuildingOrigin;
}
```

Road cells start occupied and cannot receive buildings.

When a building is placed, every cell in its footprint is marked with:

```text
IsOccupiedByBuilding = true
Building = selected definition
BuildingOrigin = origin cell
```

`BuildingOrigin` is only the grid anchor used for footprint bookkeeping, usually the bottom-left occupied cell before rotation is applied.

The prefab is instantiated once per placed building, but it is not positioned at the origin cell. It is positioned at the center of the full occupied footprint.

## Placement Pipeline

### 1. Copy Road Grid

Create a building occupancy grid from the generated road grid.

```text
Road cells = blocked
Ledge cells = blocked
Height-change road cells = blocked
Empty cells = buildable
```

Each buildable cell also stores its `HeightLevel`.

### 2. Place Edge Blocking Structures

If `FillMapEdgesWithBlockingBuildings` is enabled, place simple blocking buildings along the outer edge of the map wherever the cell is not already a road or exit.

This creates the hard arena wall.

Rules:

- Use only `SimpleBlocking` definitions.
- Prefer the largest fitting blockers.
- Do not overwrite road exits.
- Do not require road adjacency for edge blockers, if selected blocker has road adjacency, it is made specifically for being an edge blocker and should be rotated so that the side faces the road. Since map edges are the only place where blocker buildings are placed directly next to a road, this is the only place where they can be placed.

### 3. Place Complex Buildings Near Roads

Complex buildings are placed in empty cells adjacent to roads.

For each candidate origin and building definition:

1. Check the footprint is in bounds.
2. Check every footprint cell is empty.
3. Check every footprint cell has the same `HeightLevel`.
4. Check the environment matches.
5. Expand all four rotations into placement candidates.
6. Rotate the effective footprint and side requirements for that candidate.
7. Check side requirements.
8. Check repeat cooldown distance against the source `BuildingDefinition`.
9. If valid, add it to the candidate pool.

Side requirements are evaluated along the whole side of the footprint.

Example for a `2x2` building with `North = RequiresRoad`:

```text
Both cells directly north of the footprint must be road cells.
```

A side with `Any` does not care what is next to it.

This lets future park/building entrances line up with roads while still allowing solid backs and corner-specific assets.

### 4. Prefer Exact Empty Plots

Before random placement, detect empty regions surrounded by roads or blockers.

If an empty region exactly fits a building footprint, prioritize filling it with a matching building.

Examples:

```text
1x2 empty pocket -> prefer 1x2 building
2x2 empty pocket -> prefer 2x2 building
```

This reduces awkward leftover cells and makes the city read as deliberately packed.

The first implementation can approximate this by scanning for candidate footprints that leave no isolated single-cell holes around them.

### 5. Place Larger Buildings According To Preference

Sort complex building candidates by footprint area:

```text
2x2 first
1x2 / 2x1 next
1x1 last
```

Use `LargeBuildingPreference` to decide how hard to favor each size.

Suggested first implementation:

1. Compute the number of empty buildable cells after edge blockers.
2. Estimate a large-building target:

```text
targetLargeCells = emptyBuildableCells * LargeBuildingPreference
```

3. Try to place `2x2` buildings until the target is reached or no valid `2x2` candidates remain.
4. Try `1x2` and `2x1` buildings.
5. Finish with `1x1` buildings.

At `LargeBuildingPreference = 1.0`, the placer should try every larger footprint before smaller ones.

At `LargeBuildingPreference = 0.0`, the placer should skip larger footprints and use `1x1` buildings only.

At `0.5`, the result should feel like a mix: larger blocks where they fit, smaller blocks in irregular spaces.

### 6. Fill Remaining Empty Cells

If `FillRemainingEmptyCellsWithBlockingBuildings` is enabled, fill every leftover empty cell with simple blocking buildings.

Rules:

- Prefer the largest fitting simple blocker.
- Fall back to `1x1`.
- Ignore road-side entrance rules.
- Keep simple blockers with any `RequiresRoad` side on map edges only.
- Still respect repeat cooldown when there are alternatives.
- If no prefab exists, create a debug placeholder or log a warning.

This guarantees there are no unintended walkable voids behind buildings.

### 7. Replace Buried Ledge Cells

After all buildings are placed, scan remaining ledge cells and remove only the ledges that have become part of sealed blocking mass.

This pass runs after the road generator has already decided which straight ledges become height-change roads. Height-change ramps still carry `IsLedge = true` because the road generator promotes the ledge in place, so every cleanup check must also require `IsHeightChangeRoad == false`.

The intended flow is:

1. `HeightMapGenerator` creates ledges according to height constraints.
2. `RoadGridGenerator` may override individual straight, road-replaceable ledge pieces with height-change roads. A ramp is kept only if the road generator can also place a road cell on the opposite side. If that continuation cannot be made, the ramp candidate is reverted back to the original ledge.
3. `BuildingPlacementGenerator` replaces non-boundary ledges with simple blocking buildings only when all four cardinal neighbors are either simple blocking buildings, height-change roads, or other non-ramp ledges, and at least one neighbor is a simple blocking building or height-change road.
4. Inner/outer corner ledges receive a pit check. If the two low/down-side cardinal neighbors of the corner are both sealed by simple blocking buildings, height-change roads, or boundary ledge pieces, the corner is replaced by a simple blocking building.
5. Boundary ledges are replaced last, after pit-corner cleanup has added any new blockers. A boundary ledge is replaced only when it is surrounded by sealed neighbors: exactly three cardinal neighbors are simple blocking buildings or height-change roads, and the fourth side is outside the map.

Ledges that touch a normal road, a complex building, or an open/unfilled cell on any cardinal side are preserved. Height-change roads are the exception: a ramp cell itself is never replaced, but it counts as a sealed neighbor for adjacent ledges. This prevents ramp-adjacent ledge edge pieces from creating small unreachable pockets.

The pass runs in two phases against the live grid:

**Phase 1 — non-boundary ledges.** Iterate every cell where `IsLedge == true`, `IsHeightChangeRoad == false`, and the height snapshot says the cell is not a boundary ledge. Replace the cell only if every in-bounds cardinal neighbor is a `SimpleBlocking` building, a height-change road, or another non-ramp ledge, with at least one `SimpleBlocking` or height-change-road neighbor.

**Phase 2 — pit-corner check.** After buried interior ledges are resolved, scan inner/outer corner ledges. Corner ledges have two low/down-side cardinal directions, derived from their rotation:

```text
rotation 0 -> down sides South + West
rotation 1 -> down sides West + North
rotation 2 -> down sides North + East
rotation 3 -> down sides East + South
```

If both down-side neighbors are sealed by simple blocking buildings, height-change roads, or boundary ledge pieces, replace the corner ledge with a simple blocker. Repeat this scan until no more corners are replaced. This closes tiny pits formed by diagonal ledge corners facing blocker/ramp/edge-ledge mass without deleting corner ledges that still open toward playable space.

**Phase 3 — boundary ledges.** Iterate the remaining boundary ledges after phase 2 has updated the grid. Replace a boundary ledge only when it has exactly one out-of-bounds cardinal side and the other three cardinal neighbors are sealed by `SimpleBlocking` buildings or height-change roads. Running this pass last lets edge ledges see blockers created by pit-corner cleanup.

When a ledge is flagged for replacement, the generator:
1. Picks a `1x1` simple blocker from `BuildingSet` (preferring one without a `RequiresRoad` side, since the replacement is interior fill, not an edge facade).
2. Replaces the ledge cell with that blocker: clears `IsLedge`, sets `Building`/`BuildingOrigin`, and adds a new `PlacedBuilding` to the placement list.
3. Calls `HeightMapGenerator.SuppressLedgeAt(position)` so the previously instantiated ledge prefab is destroyed before the navmesh rebuild.

The replacement blocker reuses the ledge cell's existing `HeightLevel` (which is the low side). The chosen blocker prefab will sit at that height like any other simple blocker. If a high-side blocker triggered the replacement, the replacement still sits at the low height — this is intentional, simpler, and good enough for sealing the unreachable area.

If no `SimpleBlocking` `1x1` definition exists in the building set, the cleanup pass is a no-op and a fallback is not synthesized.

### 8. Instantiate Buildings

Instantiate one prefab per placed building.

Position:

```csharp
worldPosition = gridOrigin + footprintCenter * tileSize
```

For `1x1`, the center is the cell center.

For `1x2` and `2x1`, the center is the midpoint between the two occupied cells.

For `2x2`, the center is where the four occupied cells meet.

For larger future buildings, such as `4x4`, the center is the center of the full footprint.

Recommended convention:

```text
Prefab pivot is at the footprint center.
```

This makes rotation and larger footprints easier to reason about. The placement origin is still useful internally, but it does not define the prefab transform position.

Complex buildings use the generated footprint-center world position directly.

Simple blocking buildings keep their authored prefab Y position. Their X/Z position comes from the generated footprint center, but their Y is not snapped to the road grid. This allows blockout wall/building prefabs with centered pivots to remain vertically aligned as authored instead of being half-buried in the ground.

## Repeat Cooldown

`RepeatCooldownDistance` prevents the same building definition from being placed too close to itself.

Rotated candidates still count as the same building. The cooldown key is the source `BuildingDefinition`, not the rotated placement candidate.

Default:

```text
2 tiles
```

Suggested rule:

```text
Reject candidate if the minimum Manhattan distance between the candidate footprint cells and any already placed footprint cells using the same BuildingDefinition is <= RepeatCooldownDistance.
```

Example:

```text
ApartmentBlock placed at 0 degrees.
Another ApartmentBlock at 90 degrees still counts as the same building and is rejected inside the cooldown distance.
```

The check must use full footprints, not only origin cells. This matters for larger future buildings: a `4x4` building with a cooldown of `10` should enforce a 10-cell gap from any edge of that placed footprint, not from only its anchor/origin cell.

If no alternative exists, simple blockers may relax this rule as a fallback. It is better to fill the map with a repeated blocker than leave holes.

Complex buildings always respect repeat cooldown. If no valid complex building of the current footprint size can be placed without violating cooldown, the generator stops trying that size and moves down to the next smaller footprint. This prevents rare or loot-heavy feature buildings from repeating too close together.

## Road Requirement Details

Road requirements are checked outside the footprint.

For complex buildings, side requirements control authored entrances, alleys, parks, storefront fronts, and other gameplay-facing edges.

For simple blocking buildings, a `RequiresRoad` side means the prefab is an edge-of-map facade or boundary piece. Those simple blockers may only be placed on footprints that touch the map edge. Interior filler blockers should use `Any` or `RequiresNoRoad` sides instead, so the generator does not place road-facing boundary facades in the middle of the map.

For a footprint:

```text
origin = bottom-left cell
size = width x height
```

North side checks:

```text
x = origin.x .. origin.x + width - 1
y = origin.y + height
```

South side checks:

```text
x = origin.x .. origin.x + width - 1
y = origin.y - 1
```

East side checks:

```text
x = origin.x + width
y = origin.y .. origin.y + height - 1
```

West side checks:

```text
x = origin.x - 1
y = origin.y .. origin.y + height - 1
```

If a required-road side goes out of bounds, it fails unless that side is later explicitly allowed to face an exit/boundary.

When height data exists, required-road sides should only count roads on the compatible adjacent height. A normal building entrance should not face a road one level above or below.

Different-height roads are separated from the building by ledge cells. If a building side faces a ledge, the building is adjacent to the ledge cell, not to the road cell beyond that ledge. Therefore a road on the opposite side of a ledge never satisfies `RequiresRoad`; the building must use `RequiresLedgeDown` or `RequiresLedgeUp` instead.

Ledge side requirements are evaluated along the whole side of the footprint, similar to road requirements:

```text
RequiresLedgeDown:
    every checked side cell must face a ledge cell,
    the building footprint cell is on the ledge's high side,
    the opposite side of the ledge is lower.

RequiresLedgeUp:
    every checked side cell must face a ledge cell,
    the building footprint cell is on the ledge's low side,
    the opposite side of the ledge is higher.
```

For large buildings, every cell along the required side should satisfy the ledge requirement unless the building definition later adds partial-side markers.

## Height Requirements

The first height-aware building placement remains simple:

```text
one building footprint = one flat height level
```

Rules:

- Reject any footprint containing more than one `HeightLevel`.
- Reject any footprint containing a ledge cell.
- Reject any footprint containing a height-change road cell.
- Allow building sides to require adjacent ledges, but never allow the building footprint itself to occupy the ledge.
- Instantiate complex buildings at the generated footprint center with:

```csharp
worldY = HeightLevel * HeightLevelWorldUnits
```

- Simple blocking buildings keep their authored Y offset relative to that height level. Their X/Z still comes from the footprint center.

Buildings placed by the building generator do not bridge height gaps. Any traversal or structure that connects two height layers belongs to the height/ledge system, such as special ledge tiles, stairs, ramps, or ledge-built structures.

## Future Special Environments

Building definitions should carry `RoadEnvironment`.

For now:

```text
Normal
```

Later, special districts can provide different building sets:

- military quarantine,
- industrial,
- residential,
- commercial,
- survivor camps,
- zombie-heavy zones.

The building placer should filter definitions by the environment of the footprint cells. A future building can require all footprint cells to share the same environment, or allow mixed edges if needed.

## Future Larger Buildings

The first version supports max `2x2`.

Future expansion should only require adding larger `FootprintSize` values:

```text
2x3
3x3
4x4
```

The same validity rules still work:

- footprint in bounds,
- cells empty,
- side requirements,
- environment match,
- repeat cooldown.

Large special buildings may later add extra rules:

- must be near a main road,
- must be inside a special district,
- requires road on two opposite sides,
- requires a ledge going up/down on one or more sides,
- requires a minimum empty buffer.

## Future Interiors And Navigation

Complex buildings are the path toward enterable structures.

When interiors exist, building definitions can add:

```csharp
bool IsEnterable;
BuildingEntrance[] Entrances;
NavMeshSurface InteriorNavMesh;
```

The road-side requirement system already prepares for this by making sure authored entrances face roads or walkable alleys.

Simple blocking buildings should normally remain non-enterable and act as solid boundary mass.

## Implementation Plan

1. Add building category and side requirement enums.
2. Add `BuildingDefinition` prefab-root component.
3. Add `BuildingSet` ScriptableObject that stores building definition components.
4. Add `BuildingPlacementSettings` ScriptableObject.
5. Add `BuildingPlacementGenerator` component or integrate it as a separate component beside `RoadGridGenerator`.
6. Expose generated road/height occupancy from `RoadGridGenerator` in a small read-only data shape.
7. Copy road, ledge, height-change road, and height-level data into a building occupancy grid.
8. Place edge simple blockers.
9. Generate valid complex building candidates from road adjacency, side requirements, and all four rotations.
10. Prioritize exact empty plots.
11. Place larger complex buildings according to `LargeBuildingPreference`.
12. Place smaller complex buildings.
13. Fill remaining empty cells with simple blockers.
14. Replace buried ledge cells with simple blocker buildings and suppress the corresponding ledge prefab on the height generator.
15. Instantiate building prefabs under a generated building root.
16. Add gizmos for complex buildings, simple blockers, and rejected/empty cells.

## First Version Acceptance Criteria

- Building placement is separate from road generation.
- Road cells are never overwritten.
- Edge empty cells are filled with simple blockers.
- Complex buildings can require roads on specific sides.
- Supports `1x1`, `1x2`, `2x1`, and `2x2` footprints.
- Larger buildings are preferred based on `LargeBuildingPreference`.
- Complex building definitions do not repeat within `RepeatCooldownDistance`; if none fit, placement moves down to smaller footprints.
- Simple blockers may relax repeat cooldown only as a fill-the-map fallback.
- Remaining empty cells are filled with simple blockers.
- Placeholder cube/block prefabs are enough for validation.
- No interiors, loot, zombies, or special environments are required yet.
