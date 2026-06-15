# Survivor Assigned Area AI

## Goal

`SurvivorAssignedAreaAI` is a non-combat behavior component that makes an unpossessed survivor defend/patrol inside a player-assigned circular area.

It is not a top-level AI mode. `SurvivorNonCombatAI` owns the current `AssignedArea` assignment and decides when this behavior can run. `SurvivorAssignedAreaAI` owns only assigned-area movement, random patrol points, and waiting between patrol points.

## Relationship To The AI System

The long-term survivor AI model is:

```text
SurvivorNonCombatAI / SurvivorCombatAI
-> choose which behavior, if any, should run
-> behavior component emits or helps build normal NetworkedInput
```

`SurvivorAssignedAreaAI` is one of those behavior components.

- It is assigned to the survivor prefab as a `MonoBehaviour`.
- It runs only on state authority through `SurvivorNonCombatAI`.
- It does not directly move transforms.
- It uses `CharacterNavigator` and normal `NetworkedInput` movement.

## Editor Tunables

`SurvivorAssignedAreaAI` exposes assigned-area-specific values in the Inspector:

```text
AreaStoppingDistance
PatrolPointStoppingDistance
PatrolPointVerticalStoppingDistance
DirectFallbackDistance
PatrolPointSampleAttempts
WaitDurationMin
WaitDurationMax
```

`PatrolPointVerticalStoppingDistance` is the vertical tolerance for "reached a patrol point", kept separate from the horizontal `PatrolPointStoppingDistance`. See [Height-Aware Arrival](#height-aware-arrival).

The shared command settings own the drag-circle radius limits:

```text
SurvivorAICommandSettings.AssignedAreaMinRadius
SurvivorAICommandSettings.AssignedAreaMaxRadius
```

`GameMapSelectionController` still has fallback values for standalone setup, but when a `Gameplay` instance is available it reads the limits from `Gameplay.AICommandSettings`. State authority validates the same values before applying an assigned-area order.

## Assignment Creation

Assigned-area orders are created from the map:

```text
hold right click on map
-> drag circular radius
-> release right click
-> selected inactive survivors receive AssignedArea(center, radius)
```

The preview must stay a circle. Dragging farther than the maximum radius clamps the radius instead of stretching the shape. Zooming is disabled while the circle is being dragged so the preview radius stays stable.

If the dragged radius is smaller than `AssignedAreaMinRadius`, the circle is hidden. Releasing right click while no circle is visible falls back to the normal point order at the current mouse cursor position.

Right-clicking an own survivor icon without a visible circle still issues the existing follow order.

The preview object does not need manual editor setup. `GameMapSelectionController` creates `AssignedAreaCircle` automatically under the map `RawImage` if none is assigned. The object is disabled while the drag radius is below the minimum and re-enabled once the radius becomes valid; disabling the preview object does not block it from being re-enabled because the controller keeps the `RectTransform` reference.

## Behavior Flow

1. Resolve a patrol-point set that is reachable **from the middle of the circle** before accepting the order. The survivor's own distance to the circle is irrelevant — a survivor across the map chains to the area through the navigator (the same midpoint-bisection fallback move orders use), so an order is only rejected when the circle itself contains no reachable NavMesh.
2. Move toward an entry point inside the circle until inside it. A far entry point is reached by chaining, exactly like a move order; once the survivor is within the radius of the center it switches to patrolling.
3. While travelling to the circle, do not start looting or investigation detours.
4. Once inside the circle, optional behaviors can run again.
5. If no higher-priority behavior is active, pick a reachable random patrol point inside the circle.
6. Move to that patrol point.
7. Wait for a random duration between `WaitDurationMin` and `WaitDurationMax`.
8. Pick another random patrol point.

The dragged center may be on non-navigable terrain such as a rooftop, car, prop, or building corner. That is valid as long as the circle contains at least one reachable NavMesh point.

For selected map orders, state authority resolves one shared reachable patrol-point set for the selected group. The first probe is the circle center. If that is not reachable, it checks fixed points near the north, east, south, west, and diagonal sides of the circle. **Reachability is evaluated from the middle of the circle, not from the survivor's position**: the start point is the circle center snapped onto the NavMesh (sampled out to the radius so a center landing on a rooftop, car, or building corner still resolves to walkable ground inside the circle), and each probe tests the candidate's X/Z at NavMesh height. Anchoring at the center means patrol points only have to be mutually reachable inside the area, so a far survivor with no in-budget path to the area is not wrongly rejected — it chains there like a move order. If none of those center-relative probes are reachable, the circle genuinely contains no reachable ground, the command is ignored immediately, and selected survivors keep their current assignments.

Once the area is known to contain reachable terrain, the generator samples additional points inside the circle. The target count is based on the radius rounded up, so a radius of `3.14` tries to keep about `4` patrol points and a radius of `8.51` tries to keep about `9`. Each survivor receives the same point set but picks its own entry/patrol target, so the group can spread out without doing per-survivor area searches. Prefab-authored `PatrolWaypoint` markers inside the circle are forced into this set first and can replace the automatic sampling entirely — see [Manual Patrol Waypoints](#manual-patrol-waypoints).

Combat still has priority. If combat AI activation is enabled and a valid direct combat target exists, combat input can take over the same way it does for other non-combat assignments.

## Manual Patrol Waypoints

The automatic sampler only finds ground reachable from the circle centre, so it cannot discover the upper floors, windows, or rooftops of complex multi-level buildings (a watchtower, an apartment block). `PatrolWaypoint` fixes this. It is a prefab-authored marker placed as a child of a building prefab — exactly like the neutral-survivor / zombie / pickup spawn markers — so every generated map instance carries its own garrison points.

Selection happens in `SurvivorAssignedAreaAI.TryBuildReachablePointSet` when an assigned-area order is created:

1. Collect every active `PatrolWaypoint` whose **XZ** falls inside the circle. Height is ignored, so a top-down patrol circle over a building footprint captures all of its vertically stacked waypoints at once (each floor's windows, the roof).
2. Compute the desired point count for the circle: `targetCount = ceil(radius)`.
3. **If there are at least as many manual waypoints as `targetCount`**, the patrol set is *exactly* those manual waypoints — all of them, even if there are far more than `targetCount` (32 windows in a radius-5 circle gives 32 patrol points). Automatic sampling is skipped entirely.
4. **If there are fewer manual waypoints than `targetCount`**, keep all of them and auto-sample reachable points to fill the remainder with the normal golden-spiral generator.
5. **If there are no manual waypoints**, behavior is unchanged from before this feature.

`TryBuildReachablePointSet` / `TryBuildAssignedAreaPatrolPoints` take an optional `preferAuthoredWaypoints` flag. When it is set, rule 4 changes: if the circle contains **any** authored waypoints, the patrol set is those waypoints alone and the auto-fill is skipped entirely (it does not pad the set out to `targetCount`). This is used for **neutral-survivor garrisons** (`NeutralSurvivorOrchestrator`, `NeutralSurvivor` roaming): a neutral holding a building has a wide default `PatrolRadius` (~8, so `targetCount` ~8), and without this flag a building with only a few waypoints would have its patrol set diluted by ground points auto-sampled around the spawn — so the neutral mostly milled at the base instead of garrisoning the windows/rooftops. Player map orders leave the flag `false`, keeping the fill-the-rest behavior. Areas with no authored waypoints patrol normally either way.

Manual waypoints are taken **as-is, with no reachability test**. That is the whole point: they intentionally name places the centre-anchored ground sampler rejects (rooftops behind a non-navigable centre). Placement is the author's responsibility — put each waypoint on the walkable surface (within roughly `CharacterNavigator.SampleMaxDistance`, ~1 m, of that floor's NavMesh so the navigator resolves the intended level) and make sure a survivor can actually path to it. There is no per-target give-up timer, so a genuinely unreachable waypoint will leave a survivor stuck trying to climb to it.

Because manual waypoints can make the area valid on their own, an order over a footprint with authored waypoints is accepted even when the circle centre has no reachable NavMesh (e.g. a hollow tower base). With no manual waypoints, the unchanged centre/edge reachability gate still rejects circles that contain no reachable ground.

Survivors still pick patrol points **uniformly at random** from the set and wait between them. With more waypoints than survivors they roam the building's points randomly rather than each claiming a distinct one; uneven coverage is expected, not a bug. True per-waypoint assignment would be a separate feature.

This selection runs for every assigned-area consumer, including roaming neutral survivors (`RoamArea`), so neutrals in a building with authored waypoints will garrison its POIs too.

## Height-Aware Arrival

Patrol points can be far above or below the survivor (a rooftop point directly over the survivor's ground position). "Reached" therefore cannot be a flat XZ test, or a survivor standing on the ground under a rooftop waypoint would count as having arrived and never climb.

- `SurvivorAssignedAreaAI` treats a patrol point as reached only when the survivor is within `PatrolPointStoppingDistance` horizontally **and** within `PatrolPointVerticalStoppingDistance` vertically. The same combined test guards the direct-walk fallback, so a survivor never tries to walk straight into the wall beneath an overhead point — it holds and lets the navigator path up next tick.
- `IsInsideArea` (the "am I at the area yet" footprint gate) stays a flat XZ test on purpose: a survivor is considered to have arrived at the area as soon as it is inside the footprint cylinder at any height, after which it patrols the (possibly vertical) points.
- `CharacterNavigator` is made height-aware to match (see `Docs/PathfindingSystem.md`): its destination-reached test gained a vertical tolerance, and its destination-change detection became a 3D compare so switching between two patrol points stacked on different floors actually repaths up/down instead of being ignored as the "same" point.

Keep the vertical tolerances below a building's floor height so stacked floors are distinguished, but above survivor pivot/step variance (default `2 m`).

## Behavior Priority

Within an assigned area:

```text
combat
-> investigation
-> return from optional behavior
-> looting
-> assigned-area patrol/wait
-> idle look
```

Before the survivor reaches the assigned area, looting and investigation do not start. The player-given order is still in progress, so optional distractions should wait until the survivor has arrived.

Once an optional behavior has started from inside the assigned area, the assigned-area order pauses until that behavior finishes. This means a survivor can leave the circle to collect a useful pickup or investigate a suspicious position, complete that behavior's movement/look-around/return state, and only then resume defending the assigned area. The assigned-area patrol must not pull the survivor back early just because the temporary target is outside the circle.

## Network Model

Assigned-area orders are requested by the local map UI and validated on state authority.

Do not network:

- current patrol target,
- wait timers,
- sampled patrol candidates,
- path corners,
- preview circle UI state.

Only the assignment intent needs to be authoritative:

```text
selected survivor mask
area center
area radius
```

State authority validates the order against reachable NavMesh before replacing current AI assignments.

The authoritative result is normal survivor movement and rotation replicated by existing systems.

## Future Expansion

Future assigned-area behavior can add:

- smarter spacing between multiple survivors,
- cover-aware patrol points,
- line-of-sight weighted patrol points,
- patrol sub-modes such as static guard versus roaming guard,
- area shape variants if the game later needs them.

For now, the order is intentionally circular only.
