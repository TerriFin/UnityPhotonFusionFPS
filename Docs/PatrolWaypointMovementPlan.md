# Plan: Patrol Waypoints for Move Orders & Zombie Wandering

**Status: IMPLEMENTED (July 2026) — kept as the design record.** The operational documentation now lives in
`Docs/CharacterAICommands.md` ("Move-order waypoint fallback") and `Docs/ZombieAI.md` (Idle → "Waypoint
wander"); those files are authoritative if this one ever drifts. Implementation followed this plan with the
answered open questions at the bottom (ground-floor entry accepted; no stickiness bonus in the first pass;
neutral survivors excluded — their movement targets are already NavMesh-valid spawn points). One addition not
in the original text: `NavMeshIslandMap.MainIslandId` was exposed so the "directly resolvable" test can
distinguish the main street network from verified-disconnected rooftop islands.

## Problem

Two symptoms, one root cause: **top-down resolution stops at roofs, and nothing redirects indoors.**

1. **Survivor move orders onto roofed buildings do nothing useful.** The map click resolves through `GameMapCameraController.TryMapViewportToWorld` (`Assets/Scripts/UI/Map/GameMapCameraController.cs:103`) — a straight top-down `Physics.Raycast` whose hit point *is* the order destination. Clicking a building hits its **roof**; the roof point travels verbatim through `Gameplay.RPC_RequestMapMoveOrder` (`Assets/Scripts/Gameplay/Gameplay.cs:1728`) into `SurvivorAICommand.MoveTo`. The navigator then resolves the roof point to the nearest mesh it can reach — the street outside — and the team goes and "hangs out next to the building".
2. **Zombies rarely venture inside buildings.** Idle wander (`ZombieAI.TryChooseIdleWanderPoint`) picks flat random offsets and resolves them with `NavMesh.SamplePosition` + a complete-path check (`TryResolveIdleWanderCandidate`). Random flat offsets almost always resolve to ground-level outdoor mesh; interior floors and upper levels are never *sampled*, so they are never *visited*. This is the same discovery problem the survivor assigned-area patrol already solved with `PatrolWaypoint` markers.

We already author `PatrolWaypoint` markers inside buildings (doors, stairwells, windows, roofs — `Assets/Scripts/WorldGeneration/PatrolWaypoint.cs`), and assigned-area patrols already consume them. This plan extends the same authored data to **move orders** and **zombie wandering** — consistent with the design-history lesson that deterministic authored/generated data has never failed where runtime inference has.

## Feature A — Move-order waypoint fallback (survivors)

**Rule (user's design):** when a move order's destination *cannot be directly resolved* (it landed on a roof), shift the order to the nearest `PatrolWaypoint` within an X/Z radius — **ignoring Y entirely**, because a building has multiple floors and any of its stacked waypoints marks the same "go into this building" intent.

**Where:** state authority, in `Gameplay.RPC_RequestMapMoveOrder`, just before `ApplySelectedTeamCommand` — one interception point for all senders (host and clients), and downstream systems (order markers, group spread, stopping-distance relaxation in `SurvivorAICommandService`) see the corrected destination with zero changes.

**"Cannot be directly resolved" test** — reuse `NavMeshIslandMap` (`Assets/Scripts/AI/Navigation/NavMeshIslandMap.cs`), which already classifies exactly this:

- `TryGetIslandAt(destination, ~1 m, ~1 m)` resolves to the **main island** → the order is fine; pass through unchanged (streets, plazas, walkable connected roofs).
- Resolves to a **small or verified-disconnected island** (inaccessible rooftops are precisely the 84 verified islands the map catalogs) **or resolves to nothing** → unresolvable → waypoint fallback.
- Island map unavailable (no bake yet) → pass through unchanged (today's behavior).

**Waypoint fallback:** nearest active `PatrolWaypoint` by **XZ distance** within `MoveOrderWaypointSnapRadius`; ties between vertically stacked waypoints (same XZ) prefer the **lowest Y** (ground-floor entry — survivors path from outside, and interior waypoints chain upward from there). Found → replace the destination. Not found → pass the original through (survivors park outside, exactly today's behavior — no regression).

**Deliberately NOT reused:** `SnapAssignedAreaCenterToHeightMap` (`Gameplay.cs:1936`) snaps Y to the terrain cell height — for a building tile that puts the destination at ground level *inside the walls*, which the navigator resolves no better than the roof point. Waypoints are the authored truth for "inside this building"; the height-map snap stays what it is, the assigned-area center fix.

**Knob:** `MoveOrderWaypointSnapRadius` on `SurvivorAICommandSettings` (`Assets/Scripts/Survivor/AI/SurvivorAICommandService.cs` — lives beside `MoveCommandMaxDistance`). Default **12** — covers clicking mid-roof on a 2×2-tile building and still reaching a waypoint at its edge; tune down if orders near buildings get "sucked in" too eagerly.

## Feature B — Zombie wander waypoint snapping (with anti-black-hole)

**Rule (user's design):** while choosing an idle wander point, a candidate that lands near a patrol waypoint snaps **to** the waypoint. Once a waypoint has been used in a sweep, later candidates landing on the same waypoint are ignored. Because buildings hold clusters of waypoints, a zombie that enters keeps finding nearby waypoints on later sweeps and naturally "sticks" to points of interest — until crowding pushes it out.

**Where:** `ZombieAI.TryChooseIdleWanderPoint` / `TryResolveIdleWanderCandidate` (`Assets/Scripts/Zombies/ZombieAI.cs`). Idle-only; investigation/attack/hunt routing is untouched.

Per sweep (the existing 3-candidate loop, every `IdleWanderIntervalMin..Max` seconds, staggered per zombie):

1. **Leaving incentive comes first.** Before any snapping, find the nearest waypoint to the *zombie itself* (XZ, within `WaypointCrowdRadius`). If it exists and its cluster is **crowded** (see 3), suppress waypoint snapping for this whole sweep. Candidates stay plain outdoor points, and the existing `GetAwayFromNearbyZombies` bias — which already pushes wander directions away from packed zombies — walks it out of the building and keeps it from snapping straight back in. This is the "incentivized to leave, and not to go back inside" behavior with zero new movement logic.
2. **Candidate snap.** For each raw candidate position, `PatrolWaypoint.TryGetNearestInRadiusXZ(candidate, WanderWaypointSnapRadius, out waypoint)` (new API, mirrors `CollectInsideCircle`). If found and not yet used this sweep and not crowded → the candidate becomes the waypoint's position, then flows through the **existing** `TryResolveIdleWanderCandidate` reachability check unchanged (waypoints sit within ~1 m of their floor's NavMesh by authoring contract, well inside the 8 m sample distance; the complete-path requirement stays, so an unreachable waypoint is skipped, not walked at).
3. **Crowding check** (the anti-black-hole boundary): count zombies within `WaypointCrowdRadius` (XZ) of the waypoint via a linear scan of `ZombieCharacter.ActiveZombies` — the same cost class as `GetAwayFromNearbyZombies`, which already scans that list once per sweep; at 3 candidates per 3–7 s per zombie this is within the idle budget (no new spatial structure needed; note `SpatialHash2D` exists if profiling ever disagrees). Count ≥ `MaxZombiesNearWaypoint` → no snap for this waypoint.
4. **Same-sweep dedupe:** remember the waypoint chosen by an earlier candidate in this sweep; further candidates snapping to it are ignored (the user's "ignore future checks that land on the same patrol point within the same sweep").
5. Scoring is unchanged — snapped candidates compete on the existing away-from-zombies score. No artificial bonus initially; if playtests want stronger stickiness, a score bonus is the tuning hook to add, not a bigger snap radius (a bigger radius turns whole streets into snap zones).

**Knobs** (inspector fields on `ZombieAI`, per the knob convention — these are designer-facing boundaries the user explicitly wants tweakable):

```text
UseWaypointWander: true          master toggle
WanderWaypointSnapRadius: 6      candidate-to-waypoint XZ snap distance
WaypointCrowdRadius: 10          XZ radius for occupancy counting (roughly a building footprint)
MaxZombiesNearWaypoint: 4        at/above this count the waypoint stops attracting AND starts expelling
```

Black-hole math sanity: a building with 10 waypoints and cap 4 holds at most ~4 zombies attracted *by waypoints* at a time (crowding counts zombies, not waypoints, within the shared radius — a dense waypoint cluster shares one occupancy pool, so more waypoints do NOT raise capacity). Zombies above the cap stop snapping in, and residents stop snapping deeper and get walked out by the avoid-zombies bias.

## New API

`PatrolWaypoint.TryGetNearestInRadiusXZ(Vector3 center, float radius, out Vector3 position)` — linear scan of `ActiveWaypoints` (authored counts, small; same pattern as `CollectInsideCircle`), XZ distance only, nearest wins, stacked-XZ ties prefer lowest Y. Used by both features.

## Out of scope (deliberately)

- Assigned-area orders: already waypoint-aware via the patrol set; center already height-snapped.
- Zombie *explicit-goal* routing (V6 reactive climbing): untouched — this is idle wandering and survivor orders only.
- Waypoint reachability validation at runtime: stays the author's responsibility (existing contract in `PatrolWaypoint.cs`).
- Neutral survivor wandering: same mechanism would apply, but keep the first pass zombie-only; extend later if it plays well.

## Implementation steps

1. `PatrolWaypoint.cs`: add `TryGetNearestInRadiusXZ`.
2. `SurvivorAICommandService.cs` (settings class): add `MoveOrderWaypointSnapRadius = 12`.
3. `Gameplay.cs`: in `RPC_RequestMapMoveOrder`, resolve destination through a new `ResolveMoveOrderDestination(Vector3)` helper (island-map test → waypoint fallback → pass-through). No other RPC changes; **no `[Networked]` state touched** (the RPC already runs on state authority).
4. `ZombieAI.cs`: the four knobs; sweep-local snap state in `TryChooseIdleWanderPoint`; crowding count helper; leaving-incentive pre-check.
5. Docs: fold final behavior into `Docs/ZombieAI.md` (Idle section) and `Docs/CharacterAICommands.md` (move order resolution); update `Docs/SurvivorAssignedAreaAI.md` cross-reference if wording overlaps.

## Verification

Compile, then in-editor:

1. **Move order onto a roofed building with waypoints** (watchtower / complex buildings): survivors path inside to the ground-floor waypoint instead of parking at the wall. Order marker shows the shifted point.
2. **Move order onto a building with NO waypoints**: behavior identical to today (park outside) — no regression.
3. **Move order onto open street / walkable roof with ramp access**: destination unchanged (main-island pass-through).
4. **Zombie idle population drift**: seed a block with waypoint-rich buildings; within a few wander cycles zombies appear inside, on multiple floors, and loiter ("stick") around waypoint clusters.
5. **Anti-black-hole**: force-spawn 10+ zombies at one building; occupancy stabilizes around `MaxZombiesNearWaypoint`, surplus zombies visibly leave and do not immediately re-enter; no zombie stands still while doing so (V6 never-still audit still holds).
6. **Perf spot-check**: overtime-scale zombie count; idle sweep cost unchanged in profile (the added scans ride existing per-sweep budgets).

## Open questions

1. **Move-order floor choice**: nearest-XZ with lowest-Y tie-break sends orders to the ground floor. If ordering onto a *specific* floor ever matters, the map UI has no way to express it — accept ground-floor entry for now? - Yes
2. **Stickiness strength**: no score bonus for snapped candidates in the first pass. If zombies feel too transient indoors, add `WaypointScoreBonus` rather than growing the snap radius. - yes
3. Should **neutral survivors** get Feature B once validated? - Neutral survivors move between "dynamic" neutral survivor spawns. If these are ever in a multi-level building, they should use pathfinding to move between them. Because the move order is to the neutral spawn, if it is on an elevated floor, the neutral survivors should already be able to pathfind to it.
