# AI and performance review — 23 October 2026

## Scope and conclusion

Static review of the current working tree, including the existing uncommitted waypoint/navigation changes. The main paths inspected were zombie decisions, traversal, spawning, sensors, navigation, separation, survivor combat selection, projectiles, and map awareness. This is not a runtime profile or an exhaustive review of every script. No gameplay code, prefabs, scenes, networking, or human roadmap were changed.

Read alongside `ZombieAI.md`, `ZombieOrchestrator.md`, `ZombieAIDesignHistory.md`, `PathfindingSystem.md`, `CharacterSensorySystem.md`, and the previous optimization reports.

**Follow-up status:** five behavior fixes remain applied. Finding 4's overtime target separation was
explicitly reverted at the user's request, restoring lingering pursuit of local targets until the next
hunt refresh. The other descriptions record the pre-fix problems and reproduction cases. Current behavior is documented in `ZombieAI.md`,
`CharacterSensorySystem.md`, and `ProjectileSystem.md`. The broader architecture, assignment-balancing,
performance, and documentation recommendations remain proposals; random team assignment was retained.

Initial six-fix validation (before the overtime reversion): `dotnet build Assembly-CSharp.csproj --no-restore --verbosity quiet` passed with
0 warnings and 0 errors after restoring the generated project assets. A temporary .NET harness under
`Temp/AIRegression` extracted the production decision/attack/projectile method bodies and passed 354
assertions using engine/network doubles. Cases covered freshness and legacy sensor queries, unchanged
target/climb retention, hunting interruption/resumption and ownership/death changes, blocked/clear melee
and cooldown, long/slow detours versus stationary or oscillating climbs, and full/partial/sub-tick final
projectile segments (hits and misses). These are logic checks, not Unity physics or Fusion integration
tests. The runtime reproduction cases below remain the playtest checklist.

After the overtime reversion, the build again passed with 0 warnings/errors. The temporary harness was
updated to check lingering local pursuit, return at the next hunt refresh, and immediate replacement of
dead local targets; all 355 assertions passed, including the other five fixes' logic checks.

The existing walking + generated terrain shortcuts + reactive wall climbing model is worth keeping. Its complexity reflects real geometry failures documented in six iterations. The best simplification is to separate **what to pursue**, **which team owns the long-term assignment**, and **how to move**, and stop resetting movement when only target information was refreshed. A new behavior-tree framework or another complete traversal rewrite would add risk without addressing the concrete problems below.

## Behavior findings — five fixed; finding 4 restored by request

### 1. A stale sighting can still defeat a fresh visible target

Evidence: `CharacterSensor.cs:174`, `:182`, `:294`; `ZombieAI.cs:1540`.

The sensor's "direct" queries filter by the original stimulus type (vision/proximity), not whether the sighting is still fresh. They rank remembered positions until memory expires. `TryGetStickyDirectEnemy` checks freshness only after selecting the nearest remembered enemy, and returns immediately when that enemy is already `_target`.

Consequently, if A's stale remembered position remains nearer than freshly visible B, A stays selected and the freshness check never runs. If a different stale candidate is nearest, the same selection mechanism can also promote it. The BlockZombie prefab currently uses a 0.4-second sensor interval and 3-second memory; the code defaults and some documentation use different values.

Reproduce: let a zombie see A outside proximity range, then have A disappear behind a corner while B remains visible farther away than A's remembered position. Observe target identity and the sighting tick, not just facing.

Recommendation: distinguish fresh observations from remembered locations **before ranking candidates**. Keep memory for investigation. Preserve a small freshness grace for staggered scans. Apply stickiness only among eligible fresh targets; do not globally change the existing sensor API without checking survivor callers that deliberately use memory.

### 2. Melee can damage through thin walls

Evidence: `ZombieCharacter.cs:206`; `CharacterSensor.cs:561`; `ZombieAI.cs:1597`.

Zombie proximity sensing deliberately ignores occlusion. `TryAttack` then checks cooldown and distance, including the extended climbing reach, and applies damage without a geometry check. A survivor within melee range across a wall is therefore hittable. Hunting can acquire such a target without any sensor visibility at all.

Reproduce: place the zombie and survivor within the configured melee range on opposite sides of a thin solid wall. Also test a climbing zombie below a floor edge.

Recommendation: keep permissive awareness, but check world obstruction when a melee strike is actually due. Use an appropriate attack origin/target point and the runner's physics scene. If a strike is obstructed, the distance-only `ShouldHoldMeleePosition` must also allow movement; otherwise fixing damage leaves the zombie standing at the wall. This change touches networked character code and requires the project's approval step before implementation.

### 3. A periodic attack refresh re-enters the same state and clears routing

Evidence: `ZombieAI.cs:442`, `:454`, `:550`, `:1390`, `:1620`; `CharacterNavigator.cs:191`.

`TryRefreshDirectAttackTarget` already updates target information every AI tick and handles actual target changes. Every `AttackRetargetInterval`, `UpdateAttacking` additionally calls `StartAttack`, even for the same survivor. That invalidates the climb-candidate cache and clears the navigator, forcing a new route when movement resumes.

This also runs the climb-preservation rule unnecessarily. A wall climb is preserved only if the target is still more than 0.5 m above the zombie: a same-level target beyond a vault can cancel the climb at the refresh boundary. Whether that produces a visible drop or immediate reacquisition depends on geometry and timing; it needs a playtest.

Recommendation: state entry should happen only on entry or a meaningful goal change. Refresh observation data in place; run alerts on their existing cooldown independently. Let traversal own climb completion. This removes both avoidable repaths and part of the reason for the current timer-preservation exceptions.

### 4. Local overtime overrides can outlive their sensory evidence — retained by request

Evidence: `ZombieAI.cs:479`, `:515`, `:539`.

The committed team and temporary local enemy both use `_target` for pursuit. When the local enemy is no longer returned by the sensor, `EnsureCommittedHuntTarget` keeps any living `_target` until `_nextRetargetTime`, without checking whether it belongs to the committed team. Meanwhile `UpdateHunting` follows its live transform. The refresh interval is 6 seconds with up to 2 seconds of jitter.

Reproduce: a zombie committed to team A encounters B just after a global refresh. B leaves sensory memory while staying alive. The zombie can continue following B's actual position until the next refresh instead of immediately returning to its assignment. Zombies with a continuously sensed enemy also delay their first team assignment, because assignment acquisition is only in the no-sensed-enemy branch.

The proposed separation of assigned and local targets was implemented, then explicitly reverted. Keep
the shared pursuit target and delayed return to the committed team unless the user requests otherwise.

### 5. Investigation timeout can abandon a valid detour

Evidence: `ZombieAI.cs:392`, `:422`.

The progress metric is flat distance plus vertical distance to the destination, compared with its best-ever value. Walking along a valid route that first goes away from the goal does not count as progress. A sufficiently long U-shaped corridor or switchback can therefore exhaust the 10-second give-up timer while the zombie is moving correctly.

Recommendation: while following a usable path, judge progress along that route or physical movement/corner advancement. Keep a bounded failure policy for repeated unsuccessful climbs and loops. Do not remove the unreachable-investigation timeout: the design history explains why it exists.

### 6. Projectiles omit their final travel interval

Evidence: `Weapons.cs:350`–`:365`.

When `tCurr >= maxLifetime`, the projectile is removed before raycasting from `tPrev` to the lifetime endpoint. The last interval is skipped even when lifetime lands exactly on a tick boundary. Effective range falls short by up to one tick of travel.

Reproduce: fire a zero-gravity test projectile at a target in the last simulation step before `MaxHitDistance`. Compare with an otherwise identical target one step closer.

Recommendation: clamp the final step endpoint to `maxLifetime`, resolve the remaining segment, then expire the projectile. Preserve the spawn-tick skip and authority/forward-tick guards. This is a small correctness fix in networked weapon code, subject to approval before implementation.

## A simpler target-selection design

Use three responsibilities, initially within the existing files if that keeps the change small:

1. **Orchestrator:** spawn budget and long-term overtime team assignments.
2. **Zombie decisions:** choose one intent using fresh sensing, assignment, and investigation memory.
3. **Traversal:** execute a destination through the existing navigator, terrain shortcut, contact climb, and jam recovery rules. Target refreshes do not restart it.

The priority rule can be this small:

```text
fresh sensed enemy -> pursue/attack that enemy, with stickiness
otherwise, overtime assignment -> pursue assigned team's survivor
otherwise, valid investigation -> pursue remembered point
otherwise -> wander or wait
```

Climbing is an execution state, not a competing reason to select a target. It should commit long enough to complete safely unless the goal is invalid or there is a deliberate cancellation. Keep existing animation-facing AI states if useful; eliminating enum values is not the objective.

This preserves the useful generic pieces already present: sensing, navigation, separation, faction checks, and movement execution. Share eligibility and movement contracts where useful; do not make zombies carry survivor loot, cover, commands, or inventory machinery. The roadmap's eventual hostile survivor type is a reason to centralize hostility rules, not a reason to build a universal AI framework now.

### Balance assignments explicitly, without rerouting the whole horde

The existing random eligible-player pick (`ZombieAI.cs:1924`) is unbiased per player, regardless of survivor count. It does **not** guarantee an even finite distribution. Spawn-region balancing (`ZombieOrchestrator.cs:647`) balances geography, not teams. These are separate jobs.

The smallest stronger assignment policy is:

- Maintain living survivors grouped by owner, plus a count of living hunters committed to each eligible owner.
- Assign a newly hunting or unassigned zombie to the eligible team with the fewest committed hunters; randomize ties and increment the count immediately.
- Keep that assignment through local encounters and normal retargets. Only reassign when the team loses its last eligible survivor; decrement counts when a zombie dies/despawns or releases its assignment.
- Periodically select a survivor within the assigned team. Keep the current valid survivor unless another is meaningfully better, to avoid oscillation within one team too.
- Handle ownership transfers/recruitment and raid rescue when maintaining eligibility. Raid allies still have separate owners under the current design.

Use the existing orchestrator and survivor registry/cache; these counts need not become networked data. Pre-overtime can retain noise-driven, opportunistic behavior.

**Define fairness as assigned pressure initially.** Equal assignments do not mean equal damage or arrival rates: travel distance, defensive terrain, killing speed, neutral encounters, and local overrides all matter. A central team can still intercept hunters assigned elsewhere. Measure assigned, locally engaged, and stalled hunters separately before adding adaptive spawning. Do not promise exact delivered pressure while also requiring every zombie to attack any survivor it encounters.

## Performance opportunities

These are source-supported work reductions, not measured frame-time savings.

| Priority | Evidence | Recommendation |
| --- | --- | --- |
| First | `ZombieAI.cs:1727`, `:1777`, `:1800`, `:1818` scan all zombies for nearby crowding, wandering scores, and alerts. Multiple whole-horde scans happen per wander selection. | Use a shared local spatial query for these bounded-radius operations. Preserve the exact distance metric, self inclusion, and alert limit. Preserve or deliberately choose recipient order: the current alert limit selects the first eligible registry entries. |
| First | `ZombieAI.cs:1924`, `:1955`, `:1972` scan `ActiveSensors`, including every zombie, to find players and survivors. | Start with `Survivor.ActiveSurvivors` or the existing per-owner cache. The assignment design above removes repeated team discovery altogether. |
| First | `SurvivorCombatAI.cs:113` accepts an enemy/line-of-fire result, but its shooting branches call `SurvivorAIShooting.TryGetInput`, which selects the target and linecasts again (`:78`, `:205`, `:391`). | Pass the already selected target and result through this decision. Prefer explicit reuse to a broad cache with complicated invalidation. |
| Next | `CharacterNavigator.cs:217` recalculates a valid stationary destination on every repath interval. Far blocked destinations can invoke the midpoint fallback (`:518`) with multiple synchronous path queries. | Reuse a valid static route while making progress; repath on goal/topology changes, exhaustion, or a real stall. Profile dynamic repaths, then consider a bounded shared path-query budget for horde spikes. Keep the established midpoint and off-mesh recovery behavior. |
| Next | `ZombieOrchestrator.cs:621`, `:647` rebuild region counts and count all living zombies for individual biased spawn selections. | Snapshot once per drain batch, increment counts as zombies spawn, and update candidate availability as markers exhaust. A per-pulse snapshot is cheaper but less current if draining takes a long time. |
| Profile first | `CharacterSeparation.cs:319` onward sets/restores collider pairs against the whole registry at activation/despawn, with nested collider loops. | Measure spawn/death bursts separately from movement. If material, reduce irrelevant collider-pair work or use suitable collision layers for zombie-zombie phasing. Do not remove hitbox/projectile interactions or allied-survivor collision rules. |
| Later | `ProjectileVisual.cs:149` still instantiates/destroys impact effects although tracers are pooled. | Pool effects if combat profiling shows particle creation/GC is significant; correctly reset particle state on reuse. |

Do not blindly run all AI at a slower rate. Split target selection from per-tick steering, climb contact/crest checks, melee timing, and movement. New sensor information or an invalid target should wake a decision promptly. The existing settled-KCC skips, tracer pooling, spatially filtered sensors, and map tracker frame guard are already useful optimizations; they should not be proposed again as missing features.

### Spatial hash correctness before extending its use

`SpatialHash2D.cs:30` keys freshness only on `Time.frameCount`. Within that frame, a newly registered object is absent and a moving object stays in its previous cell. Exact filtering uses its current position. If it crosses into a queried area from a cell outside the queried set, it can be missed. Existing validity guards handle stale despawned entries, but not these missing candidates.

Thus the documentation's claim of exact equivalence to live full scans is too strong. Choose an explicit consistency contract: update membership for movement/registration, or use a controlled simulation snapshot with appropriate invalidation and conservative bounds for motion. A tick stamp alone does not solve arbitrary movement after the snapshot within that tick. Test cell crossings and spawns during multiple simulation steps in one rendered frame before reusing the hash for more gameplay-critical queries.

### NavMesh allocation detail

Repeated `.corners` reads are not separate allocations per read in Unity 2022.3: the managed property caches its array until cleared. Verified against [Unity's 2022.3 reference source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/AI/NavMeshPath.bindings.cs). There can still be corner-array allocation when paths are rebuilt; consider reusable `GetCornersNonAlloc` buffers only if allocation profiling warrants it, with overflow handling. Avoid claiming an allocation for every access in `GetPathLength`.

## Documentation issues

- `ZombieAIDesignHistory.md` labels both V5 and V6 "CURRENT" and its closing file map still says V5. V6 describes runtime validation as pending; the user's current good experience is useful but does not establish which regression cases were tested.
- `ProjectileSystem.md` retains old per-weapon schemas below its architecture-update note and contains incorrect rate arithmetic: 900 RPM is 15 shots/second, about 0.234 shots per 64 Hz tick, not 14 shots per tick. Do not size event streams from that example.
- Some future-scale/older audit notes recommend optimizations already shipped. Treat the implementation reports and current code as the status reference.
- `MaxSpawnsPerTick` actually limits work per `Update` call, hence rendered frame, in `ZombieOrchestrator`. Its name/tooltips describe simulation ticks. If a pulse ever takes longer than the pulse interval to drain, the current cadence also delays the next pulse; the "always drains before next pulse" comment is an assumption, not an enforced bound.

## Suggested implementation and validation order

1. Fix fresh-versus-remembered target selection and redundant attack re-entry. Test target occlusion, switching, same-level vaults, and moving roof targets.
2. Fix melee obstruction together with blocked-melee movement, and the projectile final segment. Obtain approval for edits to networked classes first.
3. Overtime target separation was reverted by request. Do not reapply it as a bug fix. Any future assignment-balancing work should preserve the requested lingering local pursuit.
4. Reuse survivor combat decisions, replace remaining global proximity scans, and reduce region recounts. Validate the spatial snapshot contract first.
5. Profile before further traversal changes or movement LOD.

Runtime matrix: open streets; long indoor detours; ramps/switchbacks; same-level fence/car vaults; solid and hollow vehicle colliders; roof targets; interior upper-floor targets; descent from props; unreachable noise; crowded entrances; targets moving or dying during a climb; and overtime with 2, 4, and many widely separated teams.

For performance, record main-thread frame time, authoritative simulation time, path queries, sensory/line-of-fire queries, allocations, and spawn/death spikes in a standalone host. Compare identical seeds and actor counts before/after. Record assignment counts separately from actual engagements. No Unity playtests, build, or profiler measurements were run for this review.
