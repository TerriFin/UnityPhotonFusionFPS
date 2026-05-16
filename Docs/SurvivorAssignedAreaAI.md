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
DirectFallbackDistance
PatrolPointSampleAttempts
WaitDurationMin
WaitDurationMax
```

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

1. Resolve a reachable entry point inside the assigned circle before accepting the order.
2. Move to that reachable entry point until inside the circle.
3. While travelling to the circle, do not start looting or investigation detours.
4. Once inside the circle, optional behaviors can run again.
5. If no higher-priority behavior is active, pick a reachable random patrol point inside the circle.
6. Move to that patrol point.
7. Wait for a random duration between `WaitDurationMin` and `WaitDurationMax`.
8. Pick another random patrol point.

The dragged center may be on non-navigable terrain such as a rooftop, car, prop, or building corner. That is valid as long as the circle contains at least one reachable NavMesh point.

For selected map orders, state authority resolves one shared reachable patrol-point set for the selected group. The first probe is the circle center. If that is not reachable, it checks fixed points near the north, east, south, west, and diagonal sides of the circle. These probes use one sampled survivor/NavMesh start point and one scratch path, and they test the circle point's X/Z at NavMesh height so a rooftop or car click can still find reachable ground inside the circle. If none of those probes are reachable, the command is ignored immediately and selected survivors keep their current assignments.

Once the area is known to contain reachable terrain, the generator samples additional points inside the circle. The target count is based on the radius rounded up, so a radius of `3.14` tries to keep about `4` patrol points and a radius of `8.51` tries to keep about `9`. Each survivor receives the same point set but picks its own entry/patrol target, so the group can spread out without doing per-survivor area searches.

Combat still has priority. If combat AI activation is enabled and a valid direct combat target exists, combat input can take over the same way it does for other non-combat assignments.

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
