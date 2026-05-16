# World Generation Loot Spawning

## Context

Generated road and building prefabs should stay normal, non-networked Unity objects.

Pickups are gameplay entities. They use Photon Fusion `NetworkObject`s, so they must not be placed as normal child objects inside generated building prefabs. They should be spawned by the host/server using `Runner.Spawn(...)`.

The loot spawning system runs after world generation:

```text
Road generation
Building placement
NavMesh rebuild
Loot spawning
```

## Goal

Building prefabs can contain marker components that describe possible pickup locations.

After buildings are placed, a loot generator gathers all markers, picks some of them based on a fill ratio, and spawns configured pickup prefabs through Fusion.

This keeps the rule clean:

```text
Generated environment = regular Unity Instantiate
Gameplay entities = Fusion Runner.Spawn
```

## Pickup Spawn Markers

Add a marker component to empty child objects inside building prefabs:

```csharp
PickupSpawnPoint
{
    enum PickupSpawnCategory
    {
        Weapon,
        Health
    }

    PickupSpawnCategory Category;
}
```

The category enum lives inside `PickupSpawnPoint`, so there is only one marker script file to maintain. The marker transform provides world position, rotation, and optional authored placement inside the building.

Examples:

```text
Rifle rack marker:
  Category = Weapon

Med cabinet marker:
  Category = Health
```

Markers are not networked. They are just authored hints inside static generated prefabs.

## Loot Settings

Use a settings asset for pickup pools and density:

```csharp
WorldLootSpawnSettings
{
    float PickupPointUsage;
    NetworkObject[] WeaponPickups;
    NetworkObject[] HealthPickups;
    int SeedOffset;
}
```

Suggested defaults:

```text
PickupPointUsage: 0.35
SeedOffset: 20000
```

`PickupPointUsage` controls how many valid markers are filled:

```text
0.0 = no pickups are spawned
0.5 = roughly half of all valid pickup markers are used
1.0 = every valid pickup marker is assigned a pickup
```

The value is a ratio of possible pickup points, not a guarantee that every category receives equal distribution.

## Loot Generator

Add a world loot spawner component:

```csharp
WorldLootSpawner
{
    BuildingPlacementGenerator BuildingGenerator;
    WorldLootSpawnSettings Settings;
    NetworkRunner Runner;
    bool ClearBeforeGenerate;
    bool FindRunnerIfMissing;
}
```

`WorldLootSpawner` does not generate the world by itself. It is called by `BuildingPlacementGenerator` after buildings are rebuilt at game startup.

It can still be called manually in Play Mode through context menus:

```text
Generate Loot
Clear Generated Loot
ClearBeforeGenerate
```

`BuildingPlacementGenerator` calls the loot spawner automatically after building placement and NavMesh rebuild. It exposes:

```csharp
WorldLootSpawner LootSpawner;
bool SpawnLootAfterGenerate;
bool FindLootSpawnerIfMissing;
```

The automatic call uses `WorldLootSpawner.SpawnLoot()`, which scans already generated buildings. If no generated building root exists, the loot spawner logs a warning and does nothing.

The spawner must only spawn networked pickups on the Fusion scene authority:

```text
if Runner.IsSceneAuthority == false:
    do nothing
```

This covers the host/server instance that owns scene generation and prevents clients from spawning duplicate pickups.

## Spawn Pipeline

### 1. Wait For World Generation

Loot spawning happens after buildings are instantiated.

The spawner scans the generated building root for `PickupSpawnPoint` components.

### 2. Filter Markers

For each marker:

1. Read its category.
2. Check that the matching pickup pool is not empty.
3. Add it to the valid marker list.

Markers with no matching pickup pool are skipped.

### 3. Pick Used Markers

Use the shared world seed plus `SeedOffset` so loot placement is repeatable for the same map:

```text
lootSeed = RoadGridGenerator.Seed + WorldLootSpawnSettings.SeedOffset
```

Shuffle valid markers.

Spawn count:

```text
spawnCount = round(validMarkerCount * PickupPointUsage)
```

Clamp:

```text
0 <= spawnCount <= validMarkerCount
```

### 4. Choose Pickup Prefabs

For each selected marker:

```text
Weapon marker -> choose from WeaponPickups
Health marker -> choose from HealthPickups
```

The first version can choose uniformly at random.

Later versions can add weights, rarity, team-specific loot, match settings, or escalating events.

### 5. Spawn Through Fusion

Spawn the chosen pickup prefab using Fusion's async spawn path:

```csharp
NetworkSpawnOp spawn = Runner.SpawnAsync(pickupPrefab, marker.transform.position, marker.transform.rotation);
```

Do not use regular `Instantiate(...)` for pickups.

Do not keep networked pickup prefabs as children of generated building prefabs.

The async spawn path is intentional. During skirmish startup, loot can be requested before Fusion has every network prefab synchronously available. `SpawnAsync(...)` lets Fusion queue each pickup spawn until its prefab is ready instead of throwing `FailedToLoadPrefabSynchronously`.

## Generated Root Handling

`BuildingPlacementGenerator` exposes its generated building root through `GeneratedRoot`.

The loot spawner should search under that root:

```text
Generated Buildings
  Building A
    PickupSpawnPoint
  Building B
    PickupSpawnPoint
```

This prevents the spawner from accidentally using marker components from prefab assets, editor helper objects, or disabled test layouts elsewhere in the scene.

## Future Extensions

The same marker/spawner pattern can later support:

- neutral survivor recruitment points,
- zombie spawn points,
- ammo pickups,
- rare weapon lockers,
- event objects,
- quest/interactable props.

Possible future marker categories inside `PickupSpawnPoint`:

```csharp
PickupSpawnPoint.PickupSpawnCategory
{
    Weapon,
    Health,
    Ammo,
    RareWeapon,
    Utility
}
```

Possible future spawn marker types:

```text
RecruitableSurvivorSpawnPoint
ZombieSpawnPoint
EventSpawnPoint
```

Keep these as separate marker components if their rules diverge too much. Do not force every gameplay spawn into the pickup marker if the behavior becomes meaningfully different.

## Implementation Plan

1. `PickupSpawnPoint` marker component is placed on child transforms inside building prefabs.
2. `PickupSpawnPoint.PickupSpawnCategory` stores `Weapon` and `Health`.
3. `WorldLootSpawnSettings` stores pickup pools and `PickupPointUsage`.
4. `WorldLootSpawner` exposes `ClearBeforeGenerate` and `FindRunnerIfMissing`.
5. `BuildingPlacementGenerator.GeneratedRoot` exposes the generated building root.
6. After buildings are generated, `BuildingPlacementGenerator` can call `WorldLootSpawner.SpawnLoot()`.
7. The spawner gathers `PickupSpawnPoint` markers under the generated root.
8. The spawner filters markers by category and configured pickup pools.
9. The spawner selects markers by `PickupPointUsage`.
10. On scene authority only, pickups are spawned with `Runner.SpawnAsync(...)`.
11. Generated building prefabs must not contain nested networked pickup objects.

## First Version Acceptance Criteria

- Building prefabs can contain non-networked pickup spawn markers.
- Markers can request `Weapon` or `Health`.
- Loot spawner can fill from `0` to `100%` of valid markers.
- Loot spawner can be run as the third generation layer after roads and buildings.
- Pickups are spawned through Photon Fusion, not regular Unity instantiation.
- No networked pickup prefabs are nested inside generated building prefabs.
- The same seed produces repeatable marker selection.
