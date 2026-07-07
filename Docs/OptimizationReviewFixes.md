# Optimization Work Review — Fixes (Cleanup Step 2)

## Purpose

Second step of the July 2026 cleanup pass: an audit **of** the previously shipped optimization work
(`Docs/NetworkOptimizationAudit.md` + `Docs/NetworkOptimizationImplementation.md`) — does it work, does it do
anything in a silly way, can it easily be better? Verdict up front: the nine implemented optimizations are
structurally sound (early-outs, caches, and policies all verified in source), but the review found **three real
problems next to them** — two per-frame scene-scan patterns the audit never looked at, and one correctness hole
in the Area-of-Interest work. All three are fixed.

## What was reviewed and found OK

- **Road-grid snapshot cache** (impl. 1): keyed on `_lastGrid` reference equality; every caller shares the
  immutable snapshot. Correct.
- **Stuck-cache / island-gate** (impl. 2–3): all call sites go through `IsStuckElevatedCached()`; the
  `ShouldVerifyElevatedIsland` gate reads live navigator state; cheap checks short-circuit. Correct.
- **Dynamic-target and follow navigation policies** (impl. 4, 7): `SetDestination` (static orders) is untouched;
  dynamic/follow flags are cleared on order changes. Correct.
- **Spawn-pulse spreading** (impl. 5): budget semantics preserved; pulses drain within a few frames; the
  blocked-pulse remainder reset survives via `_pulseSpawnedCount`. Correct.
- **Climb-surface spatial index** (impl. 6): `BuildTerrain` early-outs on (seed, config) equality, so the
  every-frame `RefreshClimbSurfaces()` poll from `ZombieOrchestrator.Update` is cheap; `SetRoadGrid` is a struct
  copy. Correct.
- **Shared per-shot weapon stream** (impl. 8): counters/slots replay correctly under prediction and late join
  (`Spawned()` fast-forwards `_visibleFireCount`/`_visibleHitCount`); pellet reconstruction saves/restores
  `Random.state`. Correct. (Tracer visuals are `Instantiate`/`Destroy` per pellet — a pooling candidate noted for
  the general-sweep step, not a defect.)
- **AOI update loop** (impl. 9): struct enumerators, no per-tick GC, server-only guard. Correct except the hole
  below.

## Fix 1 — Defeated spectators received no world state under AOI (correctness)

**Files:** `Assets/Scripts/Gameplay/Gameplay.cs` (+ addendum in `NetworkOptimizationImplementation.md` §9)

`UpdateAreaOfInterest` built interest spheres **only around a player's living survivors**. A defeated player has
none, so with AOI enabled they received no survivor/zombie/pickup state at all — while spectate mode
(`Docs/SpectateMode.md`) promises exactly them an orbit camera following any surviving team and a fully revealed
map. Both would have shown an empty world. The hole also covered the brief pre-spawn window after connecting.

**Fix:** the loop now tracks whether any sphere was added for the player; if none, it adds a single whole-map
sphere. Center and radius derive from the generated road-grid bounds (the same math the map camera controller
uses) plus a 50 m margin for elevated corner tiles; if no road generator exists the radius falls back to 1000 m.
Cost: two null checks and a `sqrt` per defeated player per tick — negligible, and only while someone is defeated.

## Fix 2 — `FindObjectOfType<Gameplay>()` every rendered frame in both world generators (performance)

**Files:** `Assets/Scripts/WorldGeneration/RoadGridGenerator.cs`,
`Assets/Scripts/WorldGeneration/HeightMapGenerator.cs`

Both generators watch for a networked world-seed change in `Update()` (the client "regenerate when the host seed
arrives" path). Each called `FindObjectOfType<Gameplay>()` **every rendered frame for the whole match, on every
peer** — including the host, which early-outs immediately after paying for the scan. `FindObjectOfType` walks all
loaded objects, so this was a hidden per-frame cost that grows with scene size (and these are the two scripts
that make the scene big in the first place).

**Fix:** both cache the found `Gameplay` in a `_gameplay` field behind a `GetGameplay()` helper (Unity's
overloaded null re-triggers the find if the object is ever destroyed). The seed-wait coroutines and
`TryGetNetworkedWorldSeed` helpers were routed through the same cache.

## Fix 3 — `FindObjectsOfType<Survivor>()` every frame for map icons (performance + GC)

**Files:** `Assets/Scripts/Survivor/Survivor.cs`, `Assets/Scripts/UI/Map/GameMapIconController.cs`

`GameMapIconController.Tick → UpdateOwnIcons → CollectSurvivors` ran `FindObjectsOfType<Survivor>()` — a
full-scene scan **plus a fresh array allocation** — every frame the minimap is visible, which is all of normal
gameplay (and a second time per frame while the full map is open, since each view has its own icon controller).

**Fix:** `Survivor` now maintains a static `ActiveSurvivors` registry (added in `Spawned`, removed in
`Despawned`), mirroring the existing `ZombieCharacter.ActiveZombies` pattern. `CollectSurvivors` copies from the
registry into the caller's reused list — no scan, no allocation. The registry contains player-owned **and**
neutral survivors, exactly matching what the old scan returned for spawned survivors.

## Not implemented here (known, deliberate)

- **Audit item 8 — spatial partitioning for `CharacterSensor` / `CharacterSeparation`** remains the largest
  outstanding runtime item from the original audit (est. hundreds of thousands of registry checks/second at
  target actor counts). It is a bigger structural change and is queued for its own step.
- **KCC position compression** stays a deferred A/B toggle, as the implementation doc already records.

## Verification

- `dotnet build Assembly-CSharp.csproj` — compiles clean after each fix.
- The AOI fallback only engages when a player has zero living survivors; normal players' sphere set is
  byte-identical to before.
- Map icons draw from the registry with identical filtering (`IsSpawnedSurvivor`, ownership, alive checks all
  unchanged downstream).
