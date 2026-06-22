# Zombie AI Design History

This note records why the current zombie movement AI is shaped the way it is. `Docs/ZombieAI.md` is the operational reference; this file is the reasoning trail.

## Goals

The zombie AI needs to create cheap, readable pressure at high counts:

- Idle zombies should spread through the map without climbing random scenery.
- Zombies with an explicit reason to move somewhere should use ordinary NavMesh routes when those routes are reasonable.
- Zombies should climb non-walkable terrain ledges as shortcuts when walking to the nearest ramp would be a bad detour.
- Survivors should not become safe by standing on cars, crates, fences, or similar reachable street props.
- Zombies should not routinely scale buildings, ceilings, roofs, or interior walls just because a survivor is nearby.
- If a zombie gets stranded on a small prop or NavMesh island, it should get itself back to useful ground.

## What Failed

### Pure route-length vs direct movement

The original system compared the NavMesh path length to a direct ray/line toward the goal. If the path was much longer, the zombie used direct movement and could climb.

This worked well for obvious street props and terrain ledges, but it was too broad. Indoors, zombies climbed walls and ceilings when survivors were above, behind walls, or standing with their back against geometry. In multi-level buildings the "shortest direct path" was often physically wrong, so the system created too many special cases.

### NavMesh links / predetermined climb spots

The biggest single effort this chat. The idea: make climbing part of the navigation graph so "climb a ledge vs walk to a ramp" is just A\* cost, and "where can I climb" is just "where a link exists." It is theoretically the cleanest possible model, and it is worth recording exactly why it still lost.

**The part that worked and is kept in reserve — the survivor/zombie split via NavMesh areas.** The obvious objection ("zombies and survivors share one baked NavMesh, so won't survivors climb too?") has a clean answer: put every climb link in a dedicated NavMesh area (we used a named `ZombieClimb` area at index 3) and give survivors a `CharacterNavigator.AreaMask` that excludes it. A path query's area mask filters off-mesh links by area, so on the very same NavMesh survivors route around the links (to ramps) while zombies route through them. This worked, and it is the answer if NavMesh-based climbing is ever revisited. It was removed only because the whole link approach was dropped.

**The chain of concrete failures that followed.** Each was fixed, but every fix revealed the next problem:

1. **Links were built but A\* routed around them.** A build-time diagnostic probe (path from a link's foot to its top) came back `pathLen 49.7` vs `straight 8.1` — A\* went the long way. Root cause: the modern `NavMeshLink` *component* connects in `OnEnable`, which fires the moment you `AddComponent` it — before we had set its runtime endpoints — so it registered a degenerate link at the world origin. Fix: stop using the component for runtime links; register them with the low-level `NavMesh.AddLink(NavMeshLinkData)` API, which takes explicit world-space endpoints and is in the query graph immediately.
2. **Zombies teleported into the player's face on ramps.** Walking up a ramp, a zombie would rise and then snap onto the survivor. Root cause is a fundamental NavMesh fact: `NavMesh.CalculatePath` places **no corner on a straight slope**, so the next path corner while on a ramp is the top of the ramp (or the survivor) — far above the zombie. Our climb trigger was "next corner is much higher than me," which a ramp satisfies, and the mantle then `SetPosition`'d the zombie onto that far corner. Fix: gate the climb on **steepness (rise:run), not height** — a walkable ramp bakes at no steeper than the NavMesh `agentSlope` (~1:1) while a real climb is near-vertical — plus a max-horizontal-run cap and a cooldown after a failed climb.
3. **Climbs oscillated (rise a little, drop, repeat).** Even with working links and the steepness gate. Root cause: the climb rises at `MoveSpeed * ClimbSpeedMultiplier` (~1.1 m/s), so a `HeightLevelWorldUnits`-tall ledge (~7.5 m) needs ~7 s, but the commit had a fixed ~5 s timeout that cut it off and dropped it. Fix: make the climb timeout **progress-based** — reset it whenever the zombie gains height, abandon only if it stops rising — with a generous absolute cap. This lesson carried straight into the current system (`ClimbStuckTimeout` / `ClimbMaxDuration` / `ClimbCooldown`).
4. **Coverage was poor on corner ledges.** The generator produced links for only ~10 of 42 ledges, because the endpoint geometry assumed one cardinal cliff direction; inner/outer-corner and boundary ledges have exposed faces on two sides and failed to sample. This drove the current surface generator's per-exposed-side / dual-half-face corner handling.
5. **Authoring/feel problems even when it worked.** Zombies visibly reoriented toward the discrete link *start point* instead of walking naturally into the wall; some climbs began before the body touched anything; and a target change mid-approach could cancel or jitter the climb. For a horde this read as robotic and fragile.

**The decisive blow was architectural, not a feel bug: props deliberately removed from the NavMesh cannot host a link.** Street cars/trucks carry a `NavMeshModifier` that cuts them out of the NavMesh specifically so RTS survivor pathing never walks onto them. A link needs NavMesh at both ends, so there is *no* way to put a link onto a car top — which means NavMesh links can never reach a survivor standing on a car, the exact "don't let players cheese on a prop" case the feature exists for. NavMesh links are an excellent model for terrain (NavMesh on both sides) and a non-starter for the off-NavMesh street props that matter most. So the link model was abandoned as the universal answer and kept only as terrain inspiration.

### Generic climb-anything fallback

We then pushed toward "if stuck or not getting closer, climb the obstacle in front." This helped many car and prop cases.

The problem was that it quickly became hard to define "the obstacle that matters." Zombies climbed the wrong cars, got stuck on small disconnected NavMesh islands, oscillated between prop tops and ground, or risked returning to the old building/roof problem. Each fix created another local rule, which made the movement logic too complicated and unpredictable.

### Author every climbable prop

`ZombieClimbableSurface` is useful for deliberate rescue climbs on reusable props or buildings, especially outside roads.

It is not a good answer for every street prop. There are too many cars, trucks, crates, and generated variants, and the desired street behavior is more systemic: if the target is on the street and visible, zombies should simply push toward it and climb street-scale blockers.

## Hard-Won Technical Facts (do not relearn)

These are Unity / NavMesh behaviors and tuning truths this work surfaced. They are independent of which climb model wins, so record them once:

- **`NavMesh.CalculatePath` emits no corner on a straight slope.** A far path corner can therefore be much higher than the agent with no wall between them (a ramp, or open ground toward an elevated target). You cannot infer "there is a climbable wall here" from corner height alone — gate on slope/steepness or on an explicit climb surface, never on a raw height delta.
- **The modern `NavMeshLink` component connects in `OnEnable`.** If you `AddComponent` it and set `startPoint`/`endPoint` afterward, it has already registered a degenerate link. For runtime-generated links use `NavMesh.AddLink(NavMeshLinkData)`, or fully configure the component while the GameObject is inactive.
- **A path query's `areaMask` filters off-mesh links by their area.** This is the clean lever for "zombies climb, survivors don't" on one shared NavMesh: zombie-only links in a dedicated area, survivors masked out of it. (Currently unused, but the technique is sound.)
- **`NavMeshModifier`-removed geometry is invisible to every NavMesh query.** Cars/crates cut out for RTS reasons cannot be reached, sampled, or linked through the NavMesh. Anything that must interact with them needs a non-NavMesh path: direct steering plus physics raycasts (this is exactly what road-direct chase is for).
- **Climb completion must be progress-based, not a fixed timer.** `climb_speed * ledge_height` routinely exceeds any "reasonable" fixed timeout, and a premature cut-off produces the classic rise/drop oscillation. Abandon a climb only when it stops gaining height.
- **Corner/boundary ledge tiles expose faces on more than one cardinal direction.** A single `HighDirection` per ledge cell under-covers them; generate one face per actually-exposed side (the current generator emits two half-length faces for corners).
- **Mantle to a known landing point, not a raycast-found lip.** Once a climb is committed, snapping the body to the precomputed surface-top landing is far more reliable across varied art than probing for the top edge each tick.
- **Street-obstacle vaults need a goal-progress check.** A road-direct landing should be on the target's support object or closer to the target in flat distance than the zombie's current position. Without this, local obstacle search can pick a visually nearby but strategically wrong car/truck and move the zombie away from the survivor.

## Current Compromise

The current system is a hybrid:

1. **Idle spread is NavMesh-only.** Idle zombies pick nearby reachable wander points, biased away from other zombies. They do not intentionally climb during normal spreading.
2. **Small-prop recovery is direct/random.** If an idle zombie is stranded on a car, truck, tiny NavMesh island, or off useful NavMesh, it walks in random horizontal directions until it falls back to normal ground.
3. **Explicit goals use normal routing first.** Investigation, attacking, and hunting all produce a concrete world-space goal. For most goals, zombies use `CharacterNavigator` and NavMesh corners.
4. **Terrain ledges are generated climb surfaces.** Non-walkable terrain ledges are registered automatically from the height map, so zombies can climb them as explicit-goal shortcuts instead of walking across the map to a ramp.
5. **Street goals use road-direct movement.** If the explicit goal is on a road tile, visible, and within `RoadDirectMaxDistance`, the zombie walks directly at it and climbs blockers up to `RoadDirectMaxObstacleHeight`. This intentionally handles cars, trucks, crates, and street fence lips without per-prop authoring.
6. **Non-road perches use rescue surfaces.** For props/buildings outside street-direct behavior, `ZombieClimbableSurface` marks deliberate rescue faces. Zombies use those when a survivor reaches an otherwise unreachable perch.

The key design boundary is: **road targets are allowed to feel aggressive and direct; non-road/building targets stay more authored and NavMesh-led.** This keeps street play from becoming prop cheese without reintroducing the "zombies climb every building wall" failure mode.

## Practical Authoring Rule

For road props, prefer no special component. Let road-direct movement handle them.

For building/ledge-tile props or roofs that survivors can reach and zombies must follow onto, add `ZombieClimbableSurface` to the reusable prefab/root.

For terrain elevation transitions, rely on generated terrain climb surfaces and make sure walkable ramps/ledges bake cleanly into NavMesh.
