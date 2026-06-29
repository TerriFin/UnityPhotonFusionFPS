# Network & Runtime Optimization Implementation

## Purpose

This document is the implementation pair to `Docs/NetworkOptimizationAudit.md`. The audit is an investigation
that proposes optimizations; this file records the ones that have actually been implemented — what changed, in
which files, and why it is safe. Each section maps to a finding in the audit.

Optimizations are implemented one at a time. The order follows the audit's "Runtime Optimization Order", doing
the overtime-related runtime fixes first and the unreachable follow-island fix afterward. The network-replication
A/B-test items (KCC position compression, Fusion scheduling) are intentionally **not** implemented here.

Implementation started: June 27, 2026.

---

## 1. Cache the Road-Grid Snapshot (per-frame allocation removed)

**Audit finding:** "Secondary Scaling Costs → Per-Frame Road Snapshot Allocation" / "Runtime Optimization Order"
item 1.
**Status:** Implemented.
**Files:** `Assets/Scripts/WorldGeneration/RoadGridGenerator.cs`

### The problem

`ZombieOrchestrator.Update` runs every rendered frame and calls `RefreshClimbSurfaces`, which calls
`RefreshRoadGridSnapshot`, which calls `RoadGridGenerator.TryGetWorldGridSnapshot`. That method allocated and
filled a brand-new two-dimensional `WorldGridCell[width, height]` array on **every** call, then wrapped it in a
fresh `WorldGridSnapshot`. The result was continuous garbage that scales with map area (a large map's grid can be
hundreds × hundreds of cells), producing steady GC pressure for data that never changes between regenerations.

The same allocating method is also polled by `NeutralSurvivorOrchestrator` and `BuildingPlacementGenerator`, so
fixing it at the source benefits every caller.

### The change

`RoadGridGenerator` now caches the built `WorldGridSnapshot` and reuses it until the world is regenerated.

The snapshot is derived entirely from `_lastGrid` (the generated `RoadCell[,]`) plus `_lastTileSize`,
`_lastHeightLevelWorldUnits`, and `_lastOrigin`. All of those are written inside `Generate()`:
`ApplyHeightSnapshotRunValues` sets the tile/origin values, then `_lastGrid = grid` swaps in a *new* array
instance. Every generation therefore produces a distinct `_lastGrid` reference.

The cache keys on that reference:

```csharp
if (_hasCachedWorldGridSnapshot && ReferenceEquals(_cachedSnapshotGrid, _lastGrid))
{
    snapshot = _cachedWorldGridSnapshot;
    return true;
}
// ...build the WorldGridCell[,] once, store it...
_cachedWorldGridSnapshot = snapshot;
_cachedSnapshotGrid = _lastGrid;
_hasCachedWorldGridSnapshot = true;
```

When the world is regenerated, `Generate()` assigns a new array to `_lastGrid`, the `ReferenceEquals` check fails,
and the snapshot is rebuilt exactly once. Until then, all callers share the same immutable snapshot and its
backing array.

### Why it is safe

- `WorldGridSnapshot` and `WorldGridCell` are immutable `readonly struct`s; sharing the cached instance cannot let
  one caller mutate another's data.
- The cached content is byte-for-byte identical to what the old code rebuilt every call, because both are produced
  from the same `_lastGrid`/tile/origin values.
- Cache invalidation is tied to the only event that changes the grid — a new `_lastGrid` from `Generate()`. The
  ordering inside `Generate()` guarantees the tile/origin fields are already current when `_lastGrid` is swapped.
- `ClearGenerated()` does not reset `_lastGrid` today, so `TryGetWorldGridSnapshot` already kept returning the last
  grid after a clear; the cache preserves that existing behavior.

### Expected impact

Eliminates a per-frame heap allocation proportional to map area (one `WorldGridCell[,]` per frame → one per
regeneration). Removes the associated GC churn. No behavior change: the same snapshot data flows to
`ZombieClimbSurfaces.SetRoadGrid`, the neutral survivor orchestrator, and building placement.

---

## 2. Reuse the Cached Stuck Result in the Visible-Stuck Combat Path

**Audit finding:** "Primary Finding: Small-Island Recovery Is NavMesh-Heavy" (the "worse close-combat path") /
"Runtime Optimization Order" item 2.
**Status:** Implemented.
**Files:** `Assets/Scripts/Zombies/ZombieAI.cs`

### The problem

`ZombieAI` already has a throttled stuck check, `IsStuckElevatedCached()`, which only re-evaluates the expensive
topology probes once per `StuckCheckInterval` (0.5 s on the zombie prefab) and caches the boolean result:

```csharp
_isStuckElevated = IsElevatedOffNavMesh() || IsOnSmallElevatedNavMeshIsland() || IsOnSmallPropSupport();
```

`IsOnSmallElevatedNavMeshIsland()` is heavy — roughly 29 `NavMesh.SamplePosition` calls plus one
`NavMesh.CalculatePath` per evaluation.

However, the close-combat path `TryBuildVisibleStuckTargetInput` bypassed that cache and called the probes
directly:

```csharp
if (IsElevatedOffNavMesh() == false && IsOnSmallElevatedNavMeshIsland() == false)
    return false;
```

This method runs **every 64 Hz simulation tick** while a zombie attacks or hunts a directly sensed survivor on a
non-road tile (inside a building, on a ledge). A group of zombies reaching survivors there produced a sudden
NavMesh-query spike — exactly the close-combat hitch described in the audit.

### The change

`TryBuildVisibleStuckTargetInput` now calls the throttled `IsStuckElevatedCached()` instead of probing fresh:

```csharp
if (IsStuckElevatedCached() == false)
    return false;
```

The expensive island probe now runs at most once per `StuckCheckInterval` regardless of how many ticks the zombie
spends in close combat, and the answer is shared with the idle/explicit-goal stuck paths that already used the
cache.

### Why it is safe

- `IsStuckElevatedCached()` answers the same physical question ("is this zombie stranded on an elevated island or
  off the NavMesh?"). Reusing one cached answer per refresh window across all callers in the same tick is correct;
  a zombie's stuck state does not change meaningfully within 0.5 s.
- The cache's OR additionally includes `IsOnSmallPropSupport()`, so the visible-stuck trigger is marginally
  broader than before. This cannot cause an incorrect straight-line move: the very next guard,
  `HasCompleteNavMeshPathToGoalHeight(...)`, still bails out (`return false`) whenever the zombie actually has a
  usable NavMesh path to the target height. The direct move only happens when the zombie is both flagged stuck
  **and** has no real path — the intended condition.
- Cheap-first ordering is preserved: `IsElevatedOffNavMesh()` (one sample) short-circuits inside the cache before
  the expensive island probe ever runs.

### Expected impact

Caps the worst close-combat NavMesh spike: the per-tick (64 Hz) uncached island probe during melee against
directly sensed survivors on non-road tiles is replaced by a probe that fires at most ~2×/second per zombie. No
intended behavior change to when a stuck zombie goes direct.

---

## 3. Gate the Expensive Island-Connectivity Probe Behind Evidence of Failure

**Audit finding:** "Primary Finding: Small-Island Recovery Is NavMesh-Heavy" / "Runtime Optimization Order" item 3.
**Status:** Implemented.
**Files:** `Assets/Scripts/Zombies/ZombieAI.cs`

### The problem

Even after optimization 2 routed every caller through the cache, `IsStuckElevatedCached()` itself still ran the
expensive `IsOnSmallElevatedNavMeshIsland()` probe (~29 `NavMesh.SamplePosition` + one `NavMesh.CalculatePath`) on
its refresh tick for **every** zombie — including a perfectly normal zombie standing on broad, connected NavMesh.
The cheap signal `IsElevatedOffNavMesh()` is false for a grounded zombie, so the old `||` chain always fell
through to the heavy island probe:

```csharp
_isStuckElevated = IsElevatedOffNavMesh() || IsOnSmallElevatedNavMeshIsland() || IsOnSmallPropSupport();
```

At 120 zombies × ~2 checks/second the audit estimates this alone approaches ~7,000 NavMesh samples and ~240 path
calculations per second, almost all of it wasted on zombies that are not stuck at all.

### The change

The check is restructured into three branches, implementing the audit's four sub-recommendations for this finding
(one cached result everywhere, split cheap from expensive, gate behind evidence of failure, slower jittered
interval):

1. **Cheap, conclusive detection first.** `IsElevatedOffNavMesh()` (one sample) or `IsOnSmallPropSupport()` (one
   downward raycast + bounds test) settle the verdict as stuck without touching the island probe.
2. **Skip when there is evidence of progress.** A new gate `ShouldVerifyElevatedIsland()` reads navigator state. A
   zombie genuinely stranded on a small disconnected island has *no* complete NavMesh path off it, so the navigator
   reports `HasPath == false` or `IsDestinationUnreachable`. Conversely a zombie with a usable path — or one that
   has `IsDestinationReached` on connected mesh — is demonstrably not stranded, so the heavy probe is skipped and
   the result is "not stuck." This is the common grounded/hunting-zombie case and is where the savings come from.
3. **Verify on a slower, jittered cadence.** When the cheap checks are inconclusive *and* there is evidence of
   failure, `IsOnSmallElevatedNavMeshIsland()` runs — but no more often than `ElevatedIslandCheckInterval`
   (default 1.5 s, jittered ±15%), with the previous verdict (`_islandStuckResult`) reused in between. The jitter
   keeps a horde from probing on the same tick boundary.

`ShouldVerifyElevatedIsland()`:

```csharp
var navigator = _zombie != null ? _zombie.Navigator : null;
if (navigator == null || navigator.HasDestination == false) return true;  // nothing to disprove → verify
if (navigator.IsDestinationReached) return false;                          // arrived on connected mesh
if (navigator.IsDestinationUnreachable) return true;                       // no route → maybe an island
return navigator.HasPath == false;                                         // a path means connected mesh
```

### Why it is safe

- The genuine "stuck on a small elevated island" state always produces `HasPath == false` (or
  `IsDestinationUnreachable`) because `NavMesh.CalculatePath` cannot reach the goal from a disconnected island and
  the midpoint-chain fallback also fails. So the gate returns `true` exactly when the zombie might really be
  stranded — the probe is never skipped in the case it is meant to catch.
- Idle zombies with no active destination (`HasDestination == false`) still verify, preserving idle perch recovery.
- The navigator state read at a refresh tick reflects the previous tick's pathing attempt to the same goal
  (`BuildExplicitGoalMoveInput` sets and ticks the destination each frame), so it is a current, relevant signal.
- When a stranded zombie escapes (regains a path), the next refresh sees `ShouldVerifyElevatedIsland() == false`,
  clears the verdict, and resets the island timer — recovery is immediate, not delayed by the slower interval.
- Cheap detection still short-circuits, and `_islandStuckResult` only feeds the verdict in the gated branch.

### Expected impact

Removes the dominant sustained overtime NavMesh cost: ordinary grounded zombies with working paths no longer run
the ~29-sample island probe at all, and the zombies that do run it are throttled to ~0.66×/second (jittered)
instead of ~2×/second. New tunable `ElevatedIslandCheckInterval` on `ZombieAI` (default 1.5 s).

---

## 4. Decouple Moving Hunt-Target Updates From Immediate Repaths (+ stagger)

**Audit finding:** "Primary Finding: Dynamic Hunt Paths Recalculate Too Often" / "Runtime Optimization Order" item
5.
**Status:** Implemented.
**Files:** `Assets/Scripts/AI/Navigation/CharacterNavigator.cs`, `Assets/Scripts/Zombies/ZombieAI.cs`

### The problem

Every hunting/attacking zombie feeds its target's live position into `CharacterNavigator.SetDestination` every
tick. `SetDestination` wipes the current path and resets the repath timer to zero whenever the destination moves
more than `DestinationChangeRepathDistance` (0.33 m on the zombie prefab):

```csharp
_hasPath = false;
_nextRepathTime = 0f; // forces an immediate recalculation on the next Tick
```

A survivor running at ~6 m/s crosses 0.33 m about 18 times per second, so the configured `RepathInterval` (0.4 s)
was bypassed and **every pursuing zombie recalculated a full NavMesh path up to ~18×/second**, all clustered on
the same ticks because they share the target. The audit estimates this pushes path calculations into the low
thousands per second during an overtime chase.

### The change

A dynamic-target path was added to the navigator and the zombie hunt/attack call sites use it.

`CharacterNavigator`:

- New `SetDynamicDestination(Vector3)`. The first call seeds a normal path. Later calls update `_destination` in
  place (so the next scheduled repath retargets the live position) **without** clearing the path or resetting
  `_nextRepathTime`. The pursuer keeps following its current path and `Tick` repaths only on the `RepathInterval`
  cap. A move larger than `DynamicDestinationEmergencyRepathDistance` (default 3 m — teleport, large vertical jump)
  still forces an immediate repath.
- A `_dynamicTarget` flag distinguishes these orders. `SetDestination` (static orders: patrol points, move
  commands, investigation) sets it false and is **completely unchanged** in behavior.
- `Tick` now picks its repath delay via `GetNextRepathDelay()`, which applies `DynamicRepathJitter` (default ±20%)
  for dynamic targets so a horde sharing one target spreads its periodic repaths across ticks instead of all
  recalculating on the same tick. Static orders keep the exact fixed interval.

`ZombieAI`:

- `BuildExplicitGoalMoveInput` gained a `bool dynamicTarget = false` parameter that selects
  `SetDynamicDestination` vs `SetDestination`.
- `UpdateHunting` and `UpdateAttacking` pass `dynamicTarget: true` (live survivor target). `UpdateInvestigating`
  keeps the default (a fixed investigation point is genuinely static).

### Why it is safe

- Static move orders are byte-for-byte unchanged: `SetDestination` still wipes the path and forces an immediate
  repath, and static repaths still use the exact `RepathInterval`. Only zombie hunt/attack orders are dynamic.
- For a dynamic target the pursuer follows a path that is at most one `RepathInterval` stale. At 6 m/s that is
  ~2.4 m of drift over 0.4 s — well inside melee/approach tolerances — and the path leads to the target's recent
  vicinity, so steering still heads the right way. Big discontinuities (>3 m) repath immediately.
- `_isDestinationReached` is cleared on a dynamic goal move so a pursuer resumes if the target leaves a point it
  had arrived at. `_isDestinationUnreachable` is deliberately left for the next interval repath to re-evaluate, so
  an unreachable moving target cannot thrash the path solver every tick.
- The navigator is authority-side AI movement planning (not networked state) and already uses
  `Time.timeSinceLevelLoad`; the jitter uses `UnityEngine.Random` (fully qualified to avoid the `System.Random`
  ambiguity) consistent with the existing `ZombieAI` scheduling code.

### Expected impact

Caps moving-target repaths at ~`1/RepathInterval` per zombie (≈2.5/second) instead of ~18/second, and de-clusters
them across ticks. This is the audit's "very high overtime gain" item. New tunables on `CharacterNavigator`:
`DynamicDestinationEmergencyRepathDistance` (3 m) and `DynamicRepathJitter` (0.2).

---

## 5. Spread Spawn Pulses Across Ticks (+ cache marker validation, reset overtime timer)

**Audit finding:** "Primary Finding: Overtime Spawns Arrive As One-Frame Bursts" / "Runtime Optimization Order"
item 6.
**Status:** Implemented.
**Files:** `Assets/Scripts/Zombies/ZombieOrchestrator.cs`, `Assets/Scripts/Zombies/ZombieOrchestratorSettings.cs`

### The problem

`RunSpawnPulse` submitted the entire pulse budget in one `Update`. During overtime a single pulse can request ~23
zombies (per the audit's reproduction scale), so up to ~23 NetworkObjects were instantiated in one frame, each
dragging in Fusion replication, a fresh `CharacterSeparation` registry refresh, growing physics ignore-pairs, and
KCC/sensor/navigator/animator activation — a periodic hitch around each pulse.

Three secondary costs compounded it:

1. `BuildSpawnCandidates` re-ran `TryGetUsableSpawnPointPosition` for **every** marker **every** pulse
   (`NavMesh.SamplePosition` + up to 12 connectivity path probes per marker), even though marker positions and the
   baked NavMesh are static after generation.
2. `GetRegion` was recomputed per candidate per pulse.
3. `FindBestUnderpopulatedRegions` allocated two `int[regionCount]` arrays on every call, and it is called
   per-candidate.
4. The pulse timer was not reset when overtime started, so the first overtime pulse could land immediately or
   several seconds late — matching "survives overtime, then gets much worse around the first pulse."

### The change

**Spread across ticks.** `RunSpawnPulse` was split into `BeginSpawnPulse` (computes the budget and builds the
candidate set once, storing the budget in `_pulseSpawnsRemaining`) and `DrainSpawnPulse` (spends up to
`Settings.MaxSpawnsPerTick`, default 4, per `Update`). `Update` drains an in-progress pulse before starting a new
one, and a pulse always fully drains within a handful of frames — far inside the pulse interval — so pulses never
overlap. The original "spawned nothing this pulse → reset `_spawnRemainder`" hygiene is preserved via
`_pulseSpawnedCount`, so a fully blocked pulse still cannot accumulate a pent-up burst.

**Cache marker validation.** `_spawnPoints` is now a `List<SpawnPointEntry>` storing each marker plus its cached
NavMesh-validated `Position` and `Region`. Validation happens once in `CollectSpawnPoints` (regions in a second
pass, `CacheSpawnPointRegions`, after the spawn bounds are finalized). `BuildSpawnCandidates` reuses the cached
position/region and only performs the **dynamic** survivor-proximity `IsSpawnPointBlocked` check per pulse.

**Reuse region arrays.** `FindBestUnderpopulatedRegions` now reuses two `int[]` fields, resized only when the
region grid changes and cleared each computation — no per-call allocation.

**Reset overtime timer.** `StartOvertime` re-arms `_nextPulseTime = now + GetPulseInterval()` so the first overtime
pulse lands one interval after overtime begins. `RerollZombiesForMatchStart` also clears `_pulseSpawnsRemaining` so
an in-progress pulse cannot keep draining after the reroll wipes the horde.

### Why it is safe

- Budget semantics are unchanged: `BeginSpawnPulse` computes `min(GetSpawnBudget(), max − alive)` exactly as
  before, then `DrainSpawnPulse` spends precisely that many over consecutive ticks. The same `ChooseCandidateIndex`
  / per-marker `MaxSpawnCountPerPulse` logic selects each spawn.
- Marker validation is cached only for data the audit confirms is static (marker transforms + baked NavMesh).
  Should the NavMesh be regenerated, `CollectSpawnPoints` re-runs and rebuilds the cache. The dynamic
  survivor-proximity block stays per pulse, so spawn suppression near players is unaffected.
- `RunInitialPopulation` is intentionally left as a one-time burst (it runs pre-match / at the skirmish→match
  transition, and its default budget is 0 because `InitialPopulation` defaults to 0). Only the recurring pulse —
  the source of the periodic in-match hitch — is spread.

### Expected impact

Converts the per-pulse spawn burst into a smooth trickle of ≤`MaxSpawnsPerTick` instantiations per tick, removing
the periodic overtime hitch, and eliminates the per-pulse NavMesh re-validation, per-candidate region recompute,
and per-call region-array allocation. New tunable `MaxSpawnsPerTick` on `ZombieOrchestratorSettings` (default 4).

---

## 6. Spatially Index Climb Surfaces (+ make the refresh interval authoritative)

**Audit finding:** "Primary Finding: Climb Candidate Refresh Is Movement-Driven" / "Runtime Optimization Order"
item 7.
**Status:** Implemented.
**Files:** `Assets/Scripts/Zombies/ZombieClimbSurfaces.cs`, `Assets/Scripts/Zombies/ZombieAI.cs`

### The problem

`ZombieClimbSurfaces.TryFindDirectClimb` scanned **every** generated terrain climb surface (one per ledge tile,
so potentially thousands across a large map) on each call. The per-zombie climb-candidate cache that drives those
calls (`ZombieAI.ShouldRefreshClimbCandidate`) refreshed on its `ExplicitGoalClimbRefreshInterval` (0.2 s) **or**
whenever the zombie/goal moved 0.75 m — and at overtime speed (7.25 m/s) a zombie crosses 0.75 m about ten times a
second, so movement roughly doubled the refresh rate on top of the interval. The audit estimates ~1,000+ full
surface-registry scans per second across 120 hunting zombies, scaling with both zombie count and surface count.

The audit's fourth sub-point — "skip rescue searches while a complete ordinary path is making progress" — is
already enforced: `IsRescueClimbAllowed` only allows a rescue search when `navigator.HasPath == false` (or the
destination is reached/unreachable), so a zombie progressing on a complete path never searches rescue faces.

### The change

**Spatial index (the headline).** The static terrain surfaces are bucketed into a world-grid hash
(`Dictionary<long,List<int>>`) keyed by the cell containing each surface's center, with cell size tracking the
terrain tile size. The index is (re)built at the end of `BuildTerrain` and cleared by `ClearTerrain`.
`TryFindDirectClimb` now walks only the grid cells the origin→goal route passes through, plus a margin, and
evaluates only those buckets via `FindBestInTerrainGrid`. The margin is provably loss-free: a surface is only
accepted when the route passes within `(HalfLength + sideTolerance)` of its center, so every cell within that
radius of the swept segment is visited (`marginCells = ceil((maxHalfLength + sideTolerance)/cellSize) + 1`), and
the sweep is capped at the farthest distance a crossing can still be accepted so a distant goal does not pull in
the whole map. If the index is empty/unbuilt, the code falls back to the original full linear scan.

Runtime **component registrations** (authored rescue faces on props) intentionally stay a linear scan: they are
bounded and far fewer than per-tile terrain faces, and they register/unregister dynamically.

**Interval authoritative.** `ClimbCandidateGoalRefreshDistance` and `ClimbCandidatePositionRefreshDistance` were
raised from 0.75 m to 3 m, so ordinary hunting movement no longer forces an early cache refresh between intervals
(the zombie travels >0.75 m but <3 m within one 0.2 s interval). Only a discontinuous jump (target teleport, large
reposition) triggers an early refresh; the interval otherwise governs the cadence.

### Why it is safe

- The grid query is a strict superset of the surfaces the linear scan would accept (loss-free margin proof above),
  evaluated through the **same** `TryEvaluateSurface` logic, so climb decisions are unchanged — only fewer
  surfaces are tested.
- Each surface lives in exactly one bucket and cells are de-duplicated per query, so no surface is double-scored.
- The index rebuilds whenever `BuildTerrain` regenerates the surface set and clears on match end, matching the
  existing terrain lifecycle; the unbuilt/empty case falls back to the linear scan.
- Raising the movement thresholds cannot stall a needed refresh: the 0.2 s interval still fires, and a genuine
  large displacement still forces an early refresh.

### Expected impact

Turns each climb query from O(all terrain surfaces) into O(surfaces near the route) and roughly halves the climb
refresh rate during overtime (interval-driven ~5/sec instead of ~10/sec). This is the audit's "high overtime gain
on ledged maps." No new tunables; existing `ExplicitGoalClimbRefreshInterval` now drives the cadence as intended.

---

## 7. Unreachable Follow-Island Policy (follow leader on a disconnected NavMesh island)

**Audit finding:** "Unreachable Follow-Island Audit" / "Runtime Optimization Order" item 4.
**Status:** Implemented.
**Files:** `Assets/Scripts/AI/Navigation/CharacterNavigator.cs`, `Assets/Scripts/Survivor/AI/SurvivorNonCombatAI.cs`

### The problem

A follow order feeds the leader's exact live position into each follower's navigator every tick. A car or crate
has a small NavMesh island not connected to the street NavMesh. When the leader stands on that island, each
follower:

1. Sets the same unreachable elevated destination and rejects the partial path.
2. Because the target is beyond `UnreachableDistance`, runs the generic midpoint-chain fallback — up to **two
   bisections × nine surrounding probes = ~19 `NavMesh.CalculatePath` calls** per failed repath.
3. While idling at the foot of the prop (horizontally within `DestinationReachDistance`, vertically within
   `VerticalReachDistance`), runs the close-destination segment test — two `NavMesh.SamplePosition` + a
   `NavMesh.Raycast` — **every Fusion tick**.

Five-plus followers doing this in sync produced the hitch that started the instant the leader reached the island
and ended the instant the leader jumped down.

### The change

A dedicated **follow policy** was added to the navigator (distinct from the hunt dynamic-target policy from
optimization 4, because zombie hunt relies on the `IsDestinationUnreachable` / `HasPath == false` signals its
climb/rescue logic reads — applying the partial-path policy to hunts would break elevated-target rescue climbs).

- `SetFollowDestination(Vector3)` marks `_followPolicy` (and `_dynamicTarget`). The follow command in
  `SurvivorNonCombatAI.GetFollowInput` now calls it instead of `SetDestination`.
- In `CalculatePath`, when a follow-policy target has no complete path, `TryAcceptPartialPathToward` accepts the
  reachable portion of the just-computed `PathPartial` (its last corner is the closest reachable point toward the
  leader, requiring real progress over standing still) and the follower walks there and parks. The expensive
  midpoint chain is **never** run for a follow target. If no usable partial exists, the destination is marked
  unreachable. Either way the next repath is pushed out to `FollowUnreachableRetryInterval` (1 s, jittered) instead
  of the 0.4 s `RepathInterval`.
- The close "reached" segment test is throttled for follow targets (`IsCloseDestinationNavMeshSegmentClearThrottled`):
  it re-evaluates at most every `CloseSegmentCheckInterval` (0.3 s) or when the follower moves past
  `CloseSegmentRecheckDistance` (0.75 m), instead of every tick. Static and hunt orders keep the exact per-call
  test (early-out preserved when not within reach).
- The exact follow assignment is untouched (`_followTarget` and the live destination are kept), and a leader jump
  larger than `DynamicDestinationEmergencyRepathDistance` forces an immediate repath via `SetFollowDestination`, so
  normal following resumes the moment the leader returns to connected NavMesh.

### Why it is safe

- The policy is scoped to follow orders only. Zombie hunt (`SetDynamicDestination`), static move/patrol/hold orders
  (`SetDestination`), and the maze-map midpoint chain are all unchanged.
- Accepting a partial path is a behavior improvement, not a regression: followers walk to the nearest reachable
  point and stop instead of pressing blindly into the prop while burning ~19 path calcs per repath. When no partial
  progress exists, the old direct-approach fallback in `GetFollowInput` still applies.
- The throttled segment test only defers a "reached" transition by up to its interval, which is immaterial for
  follow spacing, and only runs when the follower is already within reach distance.
- `_followPolicy` is cleared by `SetDestination`/`ClearDestination` (the follow command clears the destination once
  the follower is within stopping distance), so no stale follow state leaks into other orders.

### Expected impact

Removes the follower island hitch: ~19 path calculations per follower per failed repath → at most one partial-path
calc on a 1 s jittered cadence, and the per-tick close-segment test → ~3/second. The leader jumping down resumes
following immediately. New tunables on `CharacterNavigator`: `FollowUnreachableRetryInterval` (1 s),
`CloseSegmentCheckInterval` (0.3 s), `CloseSegmentRecheckDistance` (0.75 m).

---

## 8. Consolidate Weapon Projectile Streams Into One Shared Per-Shot Stream

**Audit finding:** "Opportunities, Easiest First → 5. Consolidate Weapon State and Projectile Buffers" + "6.
Replicate One Event Per Shot, Not Per Pellet" / "Recommended Implementation Order" item 4.
**Status:** Implemented (projectile streams). Ammo/collected/reload consolidation intentionally deferred — see
"Scope" below.
**Files:** `Assets/Scripts/Weapons/Weapon.cs`, `Assets/Scripts/Weapons/Weapons.cs`

> ⚠️ This change alters `[Networked]` replication layout, prediction, and resimulation behavior. It needs a Unity
> recompile (Fusion re-weaves the network state automatically) and should be validated in a live match — rifle
> combat, shotgun combat, remote tracers/impacts, kill feed, and a late join.

### The problem

Every survivor carried three `Weapon` `NetworkBehaviour`s, and **each** weapon replicated its own runtime state
plus two fixed-capacity projectile buffers — a capacity-16 `NetworkArray<ProjectileSpawnData>` and a capacity-16
`NetworkArray<ProjectileHitData>` — even while the weapon was holstered and never firing. The audit measured the
six arrays alone at **816 words / 3,264 bytes per survivor**, about 95% of a survivor's fixed weapon snapshot, even
though only one weapon can fire at a time. On top of that, the shotgun wrote **one spawn event per pellet**, so a
single 12-pellet blast dirtied 12 spawn entries.

### The change

The projectile event system was moved off the individual weapons and onto the survivor-level `Weapons` manager as
a single shared stream, encoded **per shot** rather than per pellet.

- **`Weapon.cs`** keeps all weapon configuration (damage, fire rate, dispersion, pellet count, bullet speed/gravity,
  hit mask, visuals, sounds) and its small ammo state (`IsCollected`, `IsReloading`, `ClipAmmo`, `RemainingAmmo`,
  `_fireCooldown`). It **lost** the four projectile-related `[Networked]` members (`_fireCount`, `_hitCount`,
  `_spawnData`, `_hitData`) and the entire local simulation/visual machinery. `Fire()` keeps its ammo/cooldown
  checks and, on success, calls `Weapons.RegisterShot(this, origin, direction)`.
- **`Weapons.cs`** now owns one `[Networked, Capacity(8)] NetworkArray<ProjectileSpawnData>` (one slot per *shot*),
  one `[Networked, Capacity(16)] NetworkArray<ProjectileHitData>` (one slot per *pellet hit*), and `_fireCount` /
  `_hitCount`. It runs the authoritative bullet stepping (`StepActiveProjectiles`), applies damage, and drives all
  tracer/muzzle/impact visuals and crosshair feedback (`RenderProjectiles`). A spawn event stores
  `{ Origin, Direction, SpawnTick, WeaponType }`; the firing weapon's config (pellet count, dispersion, speed,
  gravity, damage, hit mask) is looked up by `WeaponType`.
- **Per-shot pellet reconstruction.** `ReconstructPelletDirections` seeds Unity `Random` with the shot's
  `SpawnTick * survivorId` (the same seed the original used) and replays the identical dispersion draws, so the
  state authority's hit-detection pellets and every peer's visual pellets are bit-identical from one networked
  event. A shotgun blast now costs **one** spawn event instead of twelve. The global `Random` state is
  saved/restored so reconstruction cannot perturb other systems.
- Hit events carry `(ShotSlot, PelletIndex)` to terminate the correct tracer; visuals are tracked in a local
  `ProjectileVisual[FireStreamCapacity * MaxPelletsPerShot]` indexed by `shotSlot * 20 + pelletIndex`, with
  slot-reuse cleanup.

### Why it is safe

- **Determinism preserved.** The seed (`SpawnTick * Object.Id.Raw`, the survivor's id — identical for the manager
  and the old weapon since both are behaviours on the survivor's NetworkObject) and the exact dispersion formula
  (`Quaternion.Euler(Random.insideUnitSphere * Dispersion)`) are unchanged, so the spread pattern feels identical.
- **Prediction/resimulation preserved.** Spawn data and `_fireCount`/`_hitCount` are written on every `Fire()` call
  (predicted on input authority, authoritative on state authority); the local hit-detection list and damage run
  only on `HasStateAuthority && Runner.IsForward`; the spawn-tick step skip is retained — all exactly as the
  original `Weapon` did, just relocated.
- **No external API churn.** `Weapons.CurrentWeapon` stays a `[Networked] Weapon`, and `Weapon.IsCollected`,
  `ClipAmmo`, `RemainingAmmo`, `IsReloading`, `HasAmmo`, `GetReloadProgress()` are unchanged, so every consumer
  (UI ammo readout, looting/weapon-preference AI, weapon pickup, reload animation) keeps working untouched.
- **No prefab edits.** Every serialized field name on both components was kept, and neither component was
  added/removed from the survivor NetworkObject, so prefab references and the behaviour list are intact; Fusion
  re-weaves the state layout on compile.

### Scope (what was deferred)

The audit's item 5 also lists consolidating the collected-weapon bitmask, current/pending weapon *type*, and
per-weapon ammo/cooldown onto the manager. That was deliberately **not** done: those members total only ~20 words
per survivor (vs the 816-word arrays), and consolidating them would force rewriting every external consumer that
reads `Weapon.IsCollected` / `ClipAmmo` / `RemainingAmmo` and `Weapons.CurrentWeapon` as a `Weapon` — high blast
radius for negligible gain. The high-value 97% (the projectile buffers) is captured here. The "unreliable RPC for
transient fire/hit effects" alternative from audit item 6 is also out of scope; the shared snapshot-backed stream
is the safer first implementation.

### Expected impact

The six per-weapon arrays (816 words / 3,264 bytes) are replaced by two shared streams (~208 words / ~832 bytes),
saving roughly **2.4 KB per survivor** of fixed snapshot state — the dominant contributor to a survivor's network
footprint, spawn cost, and late-join cost. Shotgun spawn-event dirty data drops ~90% per trigger pull (12 → 1).
New constants on `Weapons`: `FireStreamCapacity` (8), `HitStreamCapacity` (16), `MaxPelletsPerShot` (20).

---

## Follow-up: Hunting Zombies Stare at Wall-Hugging Survivors (regression fix)

**Reported after shipping optimizations 1–7.** Symptom: during overtime, hunting zombies that clearly sense a
survivor standing with its back to a wall stop a short distance away and just stare instead of closing in. It
worsened when the survivor was on a small height step ("higher gaps").
**Status:** Fixed.
**Files:** `Assets/Scripts/Zombies/ZombieAI.cs`

### Root cause

A hunting zombie reaches its target through `BuildExplicitGoalMoveInput`. When the navigator reported
`IsDestinationReached` — which only means the zombie is within `DestinationReachDistance` (~1.35 m) of the
*NavMesh-sampled* destination — the method returned `BuildLookInput` (a stare). For a survivor pressed against a
wall on a thin NavMesh strip, that sampled point can sit up to `SampleMaxDistance` (~2 m) off the real survivor and
across a NavMesh seam the path can't cross, so the zombie "arrived" while still well outside attack range and had
nothing left to do but look.

This stare point is long-standing, but two of the overtime optimizations reduced the fallbacks that used to paper
over it:

- Optimization 2 routed `TryBuildVisibleStuckTargetInput` through the throttled stuck cache instead of fresh
  probes, so the "go direct because I'm stranded" recovery became less responsive at the moment of arrival.
- Optimization 3's `ShouldVerifyElevatedIsland()` gate returned `false` on `IsDestinationReached`, which **skipped
  the stuck/island detection entirely** exactly when the zombie was navmesh-"reached" but short of the target.

The user's own NavMesh tweak (shrinking the non-walkable margin next to walls) created more of these thin-strip
seams, exposing the gap more often.

### The fix

1. **Removed the `IsDestinationReached` short-circuit from `ShouldVerifyElevatedIsland()`.** It was wrong: a
   navmesh-"reached" zombie can still be ~1.35 m short of a wall-hugging target. When reached, the navigator clears
   its path anyway, so the remaining `HasPath == false` check returns `true` (verify) — the correct behaviour. The
   only cost is that a genuinely-arrived zombie now runs the throttled island probe, which is negligible.
2. **Added `TryBuildCloseGapDirectInput` as the final fallback in `BuildExplicitGoalMoveInput`.** When there is no
   usable navmesh steering toward the goal (reached the nearest navigable point, or no complete path) and the goal
   is within `CloseGapDirectMaxDistance` (4 m) **and in clear line of sight**, the zombie steps straight toward it
   to close the final gap instead of staring. The bare `IsDestinationReached → BuildLookInput` early-return was
   removed so this path is reached.

### Why it is safe

- The close-gap step only runs when there is **no usable navmesh route at all** — if the zombie could path around
  an obstacle, `TryGetSteeringTarget` returns that path and this never executes. So it cannot override legitimate
  detours.
- The line-of-sight gate (`HasRoadDirectLineOfSight`) means the straight line is unobstructed, so stepping off the
  navmesh edge won't walk the zombie into geometry; that, not the distance cap, is the real safety. The distance
  cap is kept modest so it only bridges genuine navmesh-edge gaps.
- It does not clear the navigator, so the moment the zombie moves to a spot with a real path, normal path-following
  resumes. Investigation/melee-hold behaviour is unaffected (`HasReachedExplicitGoal` and `ShouldHoldMeleePosition`
  still gate those first).

### Expected impact

Hunting (and attacking) zombies now reliably close the last metre or two onto survivors standing on thin navmesh
strips against walls and on low steps, instead of stalling. New tunable on `ZombieAI`:
`CloseGapDirectMaxDistance` (4 m).
