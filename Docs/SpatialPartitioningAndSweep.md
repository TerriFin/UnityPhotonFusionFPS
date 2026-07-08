# Spatial Partitioning & General Sweep (Cleanup Step 4)

## Purpose

Fourth step of the July 2026 cleanup pass: implement the last big-ticket item from the original runtime audit
(`Docs/NetworkOptimizationAudit.md`, "Runtime Optimization Order" item 8 — spatially partition sensors and
separation) plus the smaller wins collected during the sweep.

## 1. `SpatialHash2D<T>` — shared frame-cached spatial index

**File:** `Assets/Scripts/AI/SpatialHash2D.cs` (new)

A small generic XZ spatial hash over an existing registry list. Callers keep their static registries
(`CharacterSensor.ActiveSensors`, `CharacterSeparation`'s separator list, `WeaponPickup`/`HealthPickup
.ActivePickups`) as the source of truth; the hash is a throwaway index rebuilt **at most once per rendered
frame**, and only on frames where something actually queries. Queries return a superset (everything in the
covered cells); callers keep all their exact distance/eligibility/line-of-sight filtering, so results are
identical to the old full scans — just fed far fewer candidates. Height is ignored (XZ cells), which matches the
flat-distance checks all the converted scans already used. `RebuildIfStale` also prunes destroyed entries from
the source registry, preserving the housekeeping the old loops did inline.

Cost model: rebuild is O(actors) dictionary inserts per frame (a few hundred), replacing O(actors²) pair checks
per second. Buckets are pooled; queries allocate nothing.

**Staleness contract (learned from a live crash):** because the hash rebuilds at most once per rendered frame
and several Fusion ticks run per frame, query results can contain entries whose NetworkObject **despawned
earlier in the same frame** — even though the live registry already dropped them. Reading a `[Networked]`
property (e.g. `Health.CurrentHealth`) on such an entry throws `InvalidOperationException`. Every hash consumer
must therefore check `Object != null && Object.IsValid` (and, for separation, the other separator's
`_isRegistered`) **before** touching networked state. `CharacterSeparation.IsAlive`/`ShouldSeparateFrom`,
`CharacterSensor.IsAliveCharacter`, and `CharacterSensor.IsDeadRememberedCharacter` (same throw via remembered
enemies that despawn, independent of the hash) all guard this way now; pickups were already safe
(`IsVisibleForSensor` checks validity itself).

### Converted: `CharacterSeparation.CalculateSeparationDirection`

Was: every separator scanned the **entire** separator registry per refresh (~0.1 s jittered) — the audit's
~289,000 pair checks/second at 170 actors, over two million at the design-target counts. Now: 4 m cells, query
radius = `Radius` (0.8 m) → each refresh reads one or two buckets. Push semantics, weights, `MaxNeighbors`
cutoff, and the faction phasing rules are untouched (neighbor ordering inside the radius can differ, which only
reorders which of >8 equally-close neighbors contribute to a soft push force).

The collision-ignore pair setup (`RefreshCollisionPairs`) deliberately still walks all separators — physics
ignore pairs must exist for every phaseable pair regardless of current distance.

### Converted: `CharacterSensor.ScanCharacters` / `ScanWeaponPickups` / `ScanHealthPickups`

Was: every sensor tested **every** other sensor and **every** pickup in the game per scan tick (~85k character
candidate checks/second at audit scale, before line-of-sight raycasts). Now: 16 m cells, query radius =
`max(VisionDistance, ProximityAwarenessRadius)` — a strict superset of anything the scan could sense, since
`CanSeePosition` rejects beyond `VisionDistance` and proximity rejects beyond `ProximityAwarenessRadius`. All
faction, aliveness, LOS, and vision-cone filtering is unchanged. Noise/bullet-impact sensing is event-driven and
was never part of the scan, so it is unaffected.

On a small map where everything is within vision range this degrades gracefully to roughly the old cost; the win
scales with map size — exactly the "huge map, 8+ players, hour-long match" target.

## 2. Corpse settling (`ZombieCharacter.HandleDead`)

The audit noted dead zombies keep running `KCC.Move` (gravity stepping) for the whole 8 s `DespawnAfterDeath`
window. At overtime kill rates that is a horde's worth of corpses × 64 Hz of pointless character-controller
stepping. `HandleDead` now marks a corpse **settled** once it is grounded with no residual velocity and stops
stepping its KCC (and re-applying its layer/hitbox changes) for the remaining despawn delay. A corpse that dies
mid-air or mid-climb still falls normally first — settling only engages after it lands and stops. The flag is
defensively reset in `Spawned()` so instance reuse can never leak corpse state into a live zombie.

## 3. Smaller sweep results

- Reviewed the map/awareness UI path: after the step-2 registry fix it is registry-driven and change-guarded
  throughout; `UIGameplayInfo` only rebuilds strings when the displayed second changes. No changes needed.
- `NeutralSurvivorOrchestrator` recruitment ticking is interval-throttled; no changes needed.

## Deferred (noted, not implemented)

- ~~**Tracer visual pooling.**~~ Done July 2026 — per-prefab pool in `ProjectileVisual` with generation-token
  safety; see `Docs/OptimizationAndKnobPass2.md`.
- **Zombie AI fidelity tiers** (audit item 9) — distance-based AI *decision* cadence remains the biggest
  remaining scale lever (a design change). The animation half was addressed in July 2026 via
  `AnimatorCullingMode.CullUpdateTransforms` plus the zombie settled-KCC skip (same doc as above).

## Verification

- `dotnet build Assembly-CSharp.csproj` — clean.
- Equivalence: every converted scan applies its original filtering to a candidate superset; only candidates that
  could never pass the distance checks are skipped.
- Runtime checklist for the next play session: friendly units still un-stack (separation), zombies still acquire
  targets at the same ranges (sensor), AI still finds pickups (looting), corpses fall and settle normally, and a
  spawned-mid-air corpse does not freeze in place.
