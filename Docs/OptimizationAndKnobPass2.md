# Optimization & Knob Reduction Pass 2 (July 2026)

## Purpose

Second combined pass (after the four-step July 6 cleanup, see `Docs/ConfigKnobConsolidation.md` …
`Docs/SpatialPartitioningAndSweep.md`): the game was still laggy with large actor counts, and many components
exposed inspector knobs whose values are either inferable or were tuned once and never vary. This pass closes
the two items the previous sweep deferred (tracer pooling, animation cost) plus the remaining per-tick hotspots
found in a fresh audit, and removes ~70 inspector knobs.

Everything below is compile-verified (`dotnet build Assembly-CSharp.csproj`, 0 errors). Runtime checklist at the
end.

---

## Performance changes

### 1. Settled idle zombies skip KCC stepping (`ZombieCharacter.FixedUpdateNetwork`)

Survivors already skipped `KCC.Move` when settled; zombies did not, so an idle horde paid full character-
controller physics sweeps at 64 Hz for standing still. A zombie now skips `ProcessInput` for the tick when it is
grounded with near-zero `_moveVelocity`, its AI produced no move/look/button input, it is not climbing, and
separation has no push for it. Any AI input, leaving the ground, or a separation push resumes full simulation
the same tick. The AI itself (`ZombieAI.GetInput`) still runs every tick, so sensing/decisions are unchanged —
only the pointless zero-input KCC step is skipped. Corpses were already handled (corpse settling, previous pass).

### 2. Animator culling for zombies and survivor third-person bodies

Both the zombie animator (`ZombieAnimator`, code-set on resolve) and the survivor third-person animator
(`Survivor.Spawned`) now use `AnimatorCullingMode.CullUpdateTransforms`: pose/transform writes stop while no
renderer of the character is visible to the local camera, which removes most animation cost for offscreen crowds
on every peer. This is gameplay-safe because **hitboxes on both prefabs live on a static, non-animated node**
(`Hitboxes` under the prefab root) — the animator only drives visual meshes. State machines keep advancing, so
triggers/params stay correct when a character re-enters view. `Survivor.Render` also caches the two animator
layer indices it used to look up by string every frame while dead.

### 3. Tracer visual pooling (`ProjectileVisual` + `Weapons`)

Previously every client `Instantiate`/`Destroy`ed one `ProjectileVisual` per pellet per shot (the deferred item
from the previous pass). Now:

- `ProjectileVisual.Get(prefab, position, rotation)` — static per-prefab pool (`Dictionary<prefab, Stack>`),
  reusing released instances; destroyed pool entries (scene unload) are skipped.
- `Release()` replaces every `Destroy(gameObject)` path (max-lifetime miss, hit termination, slot reuse in
  `Weapons.ClearShotVisuals`). Reuse resets `_terminated`, re-enables `ProjectileObject`, and clears cached
  `TrailRenderer`s so a recycled tracer does not draw a streak from its previous endpoint.
- **Generation tokens:** a tracer that self-releases at max lifetime can be recycled to a newer shot while a
  `Weapons` instance still holds the old array reference (the old `Destroy` flow made such references null;
  pooling does not). `ProjectileVisual.Generation` increments on every reuse and `Weapons` stores the generation
  alongside the reference — Terminate/Release only fire when generations match.
- Hit effects are spawned detached (no longer parented to the pooled tracer) with `Destroy(effect,
  LifeTimeAfterHit)` — visually identical.

### 4. Noise / bullet-impact events use the sensor spatial hash (`CharacterSensorEvents`)

`ReportNoise` (every shot) and `ReportBulletImpact` (every confirmed impact) iterated **all** sensors in the
game; on the host each call also paid a `GetComponent` faction lookup per sensor. Now they query the shared
sensor spatial hash with the largest noise/impact awareness radius any sensor has registered
(`CharacterSensor.MaxNoiseAwarenessRadius` / `MaxBulletImpactAwarenessRadius`, conservative static maxes tracked
in `OnEnable`; a per-event radius parameter can only shrink a receiver's radius, so the max stays a valid
bound). `RecordNoise`/`RecordBulletImpact` also reordered their checks cheapest-first: distance rejection now
runs before authority/aliveness reads and the faction lookup. Same staleness contract as all hash consumers
(rebuilds at most once per rendered frame).

### 5. `NetworkObject → Survivor/ZombieCharacter` lookup dictionaries

Hot paths resolved characters from `NetworkObject`s with `GetComponent` many times per tick per actor (zombie
target validation ran it up to ~6× per zombie-tick; sensor memory expiry per remembered entry per query).
`Survivor.GetFrom(NetworkObject)` and `ZombieCharacter.GetFrom(NetworkObject)` are dictionary lookups maintained
in `Spawned`/`Despawned` (falling back to `GetComponent` for unregistered objects). Adopted in: `ZombieAI`,
`ZombieCharacter.TryAttack`, `CharacterSensor` (memory validation, allied-expiry, noise source),
`CharacterFactionUtility`, `SurvivorAIShooting`, `SurvivorCombatAI`, `SurvivorNonCombatAI`,
`SurvivorInvestigationAI`, `SurvivorWeaponPreferenceAI`, `SurvivorRecruitingAI`, `GameMapAwarenessTracker`
(plus `sensor.Survivor` property instead of `GetComponent`), `SurvivorRosterController`.

### 6. Sensor memory expiry runs once per tick (`CharacterSensor`)

`ExpireOldEntries` / `ExpireOldPickupEntries` ran on **every** known-enemy/pickup query — the zombie AI issues
several per tick — each sweeping all remembered entries with faction/aliveness lookups. Both are now guarded by
the runner tick (nothing can expire between same-tick calls; every query loop still filters and prunes
dead/despawned entries itself, so correctness is unchanged).

---

## Knob reduction

Rule applied (extends `Docs/ConfigKnobConsolidation.md`): a value stays an inspector knob only if it is
designer-facing **and** actually varies (or plausibly should vary) per prefab/asset. Values that were identical
everywhere, inferable from context, or internal mechanics became code constants **carrying the tuned prefab
values** (the prefab was ahead of the code defaults in many places — the constants use the prefab numbers, so
behavior is identical). Unity silently drops the stale serialized lines from prefabs on next re-save; until
then they are ignored and harmless.

| Component | Before → After (visible knobs) | Notes |
|---|---|---|
| `ZombieAI` | ~60 → 16 | Kept: wander intervals/radii, retarget intervals, `TargetSwitchDistanceFactor`, `StoppingDistance`, `MaxYawDegreesPerTick`, 4 capability toggles, `ClimbSpeedMultiplier`, `ClimbMantleMaxSnapHeight`, `RoadDirectMaxDistance`. `ExplicitGoalStoppingDistance` merged into `StoppingDistance` (both were 0.66); `ReachablePointSampleDistance` merged into the `IdleWanderNavMeshSampleDistance` constant (both 8). Full constant list in `Docs/ZombieAI.md`. |
| `ZombieAnimator` | 8 → 0 | Component refs auto-resolve; parameter names are fixed hash constants; damp time constant. |
| `CharacterNavigator` | 23 → 5 | Kept the per-character movement tuning (`RepathInterval`, `CornerReachDistance`, `DestinationReachDistance`, `VerticalReachDistance`, `DestinationChangeRepathDistance` — survivor and zombie prefabs genuinely differ). `AreaMask` (always AllAreas) and `SampleMaxDistance` (always 2) became read-only properties so external readers compile unchanged; the midpoint/dynamic-repath internals became constants (`MidpointBisectionAttempts` = 2, the value both prefabs used, replacing the misleading code default of 3). |
| `CharacterSeparation` | 6 → 0 | `Kind` inferred from the `Activate` overload (the explicit assignment in `ZombieCharacter.Spawned` was removed); Radius/DesiredDistance/PushSpeed/MaxNeighbors/RefreshInterval were identical on both prefabs → constants. |
| `ZombieCharacter` | −1 | Removed the `Animator` reference field — assigned but never read by anything (`ZombieAnimator` resolves its own). |

Prefab values that now live in code constants were read from `Assets/Prefabs/BlockZombie.prefab` /
`BlockSurvivor.prefab` before conversion — where prefab and code default disagreed (e.g. `ClimbSpeedMultiplier`
0.45 vs 0.5, `MaxAlertRecipients` 16 vs 8, `ClimbRouteSideTolerance` 1.6 vs 1.5), the **prefab** value won.
`ZombieAI`'s kept knobs also had their code defaults updated to the prefab values, so an `AddComponent`-created
instance (test scenes) behaves like the tuned prefab. `CharacterNavigator`'s kept knobs keep their original code
defaults, since the two prefabs intentionally carry different values for them.

## Post-pass fix: fake-null `??` chains in `ZombieAnimator` (found in first play test)

Removing `ZombieAnimator`'s serialized reference fields exposed a latent bug in its resolve chain: in the
**editor**, `GetComponent<T>()` for a missing component returns Unity's fake-null placeholder (a non-null CLR
reference whose overloaded `==` reports null). The old `GetComponent<Animator>() ?? GetComponentInChildren<…>`
chain therefore short-circuited on the placeholder and never searched the children — zombies lost their
animations and spammed "could not resolve its references" warnings. The chain never ran before because the
prefab's serialized fields were wired. Fixed by replacing every `??` in `ResolveReferences` with explicit
`== null` checks (which go through Unity's overload), and the same pattern in
`CharacterFactionUtility.IsEnemyNoiseSourceForSurvivor`'s parent fallback.

**Project rule: never use `??` / `?.` on UnityEngine.Object references** — they bypass the overloaded null
check and mishandle both destroyed objects and the editor's missing-component placeholder.

## Docs updated in this pass

`ZombieAI.md` (knob/constant split), `PathfindingSystem.md` (navigator fields), `CharacterSeparationSystem.md`
(no inspector fields), `CharacterSensorySystem.md` (partitioned noise fan-out), `ProjectileSystem.md` (tracer
pool), `SpatialPartitioningAndSweep.md` (deferred items closed).

## Still deferred

- **Zombie AI decision-tick LOD** (think every N ticks by distance/relevance, movement continues on last
  intent) — the remaining big scale lever; a behavior-affecting design change, not attempted here.
- Interest-management tightening and dedicated-server profiling, per `Docs/FutureScaleOptimizationNotes.md`.

## Runtime checklist (not yet play-verified)

- Idle zombies stand still without drifting; they wake instantly when a survivor approaches (sensor → attack
  input) and when crowded (separation push).
- Offscreen zombies/survivors animate correctly the moment they enter view (no T-poses); death animations play.
- Tracers look unchanged during sustained fights; no tracer "streak" artifacts on rapid fire (trail clear); hit
  sparks still appear; no runaway object counts (pool reuse working).
- Zombies still hear gunfire at the same ranges (26 m zombie noise radius) and survivors still react to nearby
  impacts.
- Zombie melee, climbing, road-direct chasing, stuck recovery all behave as before (their tuning values moved
  but did not change).
- Inspector: BlockZombie/BlockSurvivor components show only the reduced knob sets; re-saving the prefabs drops
  stale serialized fields.
