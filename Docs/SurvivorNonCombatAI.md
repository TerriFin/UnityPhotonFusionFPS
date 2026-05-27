# Survivor Non-Combat AI

## Goal

Non-combat AI controls an unpossessed survivor when it has no active combat target. It decides the survivor's base non-combat assignment: hold position, follow another survivor, move to an ordered point, or patrol inside an assigned area.

Optional non-combat behaviors, such as looting and investigation, are implemented as separate behavior components that `SurvivorNonCombatAI` orchestrates. Combat AI is allowed to interrupt non-combat AI whenever the survivor detects a valid enemy. When combat ends, non-combat AI resumes the previous assignment if it is still valid.

## Relationship To Current AI

`SurvivorNonCombatAI` is the current non-combat assignment controller. It is a `MonoBehaviour` on the survivor object, implements `Survivor.ICharacterInputSource`, and emits normal `NetworkedInput`.

Player-facing orders are now represented as assignments inside `SurvivorNonCombatAI` instead of replacing the survivor's whole AI input source with separate top-level idle/follow/move classes.

Looting and investigation are separate behavior components on the same survivor object:

- `SurvivorLootingAI` owns pickup target selection, pickup movement, and pickup return state.
- `SurvivorInvestigationAI` owns suspicious-position movement, ally alerts, and the investigation look-around phase.

`SurvivorNonCombatAI` coordinates those components and keeps shared assignment state such as hold/follow/move anchors and idle looking. All three components run only on state authority through the survivor's AI input path.

The matching behavior docs are:

- `Docs/SurvivorLootingAI.md`
- `Docs/SurvivorInvestigationAI.md`
- `Docs/SurvivorAssignedAreaAI.md`

The intended expandable pattern is:

```text
base AI controller
-> owns assignment/state continuity
-> checks toggleable settings
-> selects one behavior component to run
-> behavior component owns focused behavior state and tuning
```

Combat AI should follow the same pattern later: `SurvivorCombatAI` will be the base combat controller, with combat-specific behavior components for weapon usage, target priority, cover, pursuit, and zombie/survivor-specific movement as those systems become real.

## Responsibilities

Non-combat AI should:

- Preserve the current player order, such as idle, follow, or move.
- Resume the current order after combat if the order is still valid.
- Decide whether optional behaviors are allowed to run based on the current assignment and settings.
- Orchestrate behavior priority, such as combat over investigation and investigation over looting.
- Move around inside an assigned area after reaching it when assigned-area patrol is implemented.
- Emit normal `NetworkedInput`; it should not directly move transforms.

It should not contain the detailed logic for looting, investigation, weapon choice, firing, enemy priority, or cover behavior. Looting and investigation belong to their behavior components. Weapon choice, firing decisions, target priority, and cover behavior belong to combat AI and its future behavior components.

## Settings

These settings are runtime behavior toggles. They should eventually be stored per survivor and editable through the future map/RTS UI.

```csharp
public struct SurvivorNonCombatAISettings
{
	public bool CollectVisiblePickups;
	public bool InvestigateSuspiciousStimuli;
	public bool AllowCombatAIActivation;
}
```

Suggested defaults:

```text
CollectVisiblePickups: true
InvestigateSuspiciousStimuli: true
AllowCombatAIActivation: true
```

If all optional settings are disabled, the survivor behaves like the current idle AI.

Tuning values that designers should edit in the Inspector live on components instead of inside the runtime toggle struct:

- `SurvivorNonCombatAI`: default follow/move stopping distances, direct fallback distance, movement-look yaw clamp, idle look interval, and idle look turn speed.
- `SurvivorLootingAI`: pickup stopping distance and direct fallback distance.
- `SurvivorInvestigationAI`: investigation stopping distance, direct fallback distance, ally alert radius, look-around duration, look-around rotation interval, and investigation turn speed.
- `SurvivorAssignedAreaAI`: area stopping distance, patrol-point stopping distance, patrol sample attempts, and wait duration range.
- `SurvivorAICommandSettings`: command radius, map move raycast settings, and assigned-area min/max radius.

The survivor prefab may include these components explicitly so their values are visible and configurable. If a component is missing at runtime, `SurvivorNonCombatAI` adds it as a fallback.

First implementation state:

- `SurvivorNonCombatAISettings.Default` is the "all optional non-combat behaviors enabled" preset used by the O-key command.
- `SurvivorNonCombatAISettings.Passive` disables all optional non-combat behaviors.
- The I-key command applies `Passive` to affected survivors without changing their current player-given assignment.
- The O-key command applies `Default` to affected survivors without changing their current player-given assignment.
- `Survivor` stores the current settings separately from the current assignment. Creating a new hold, follow, or move assignment must reuse those stored settings instead of resetting them to `Default`.
- If a setting is disabled while that behavior is currently interrupting hold/area behavior, the interrupt ends immediately and the survivor returns to its assignment anchor.
- Default unpossessed survivor AI uses `Default`, preserving the ability to look toward sensed sounds or suspicious positions and allowing pickup collection while idle/assigned.
- Move and assigned-area travel move at `Survivor.AIMoveSpeed`. Follow assignments targeting the currently possessed survivor move at normal `MoveSpeed` only while inside `Survivor.AIFollowFullSpeedRadius`; farther followers use `AIMoveSpeed` so they do not sprint across the whole map after a long-range follow order.
- Idle survivors with investigation enabled slowly look around when no higher-priority behavior is active. They choose a new random yaw using the `SurvivorNonCombatAI` idle-look interval and turn at `IdleLookMaxYawDegreesPerTick`, giving enemies and pickups a chance to enter their vision without making them snap around.
- Full `SurvivorCombatAI` is not implemented yet. For now, the handoff is still the existing `SurvivorAIShooting` helper layered into `SurvivorNonCombatAI`.

## Assignments

Non-combat AI should track one current assignment:

```csharp
public enum ENonCombatAssignment
{
	HoldPosition,
	FollowSurvivor,
	MoveToPoint,
	AssignedArea
}
```

Suggested assignment data:

```csharp
public struct SurvivorNonCombatAssignment
{
    public ENonCombatAssignment Type;
    public Vector3 AnchorPosition;
    public NetworkObject FollowTarget;
    public float Radius;
}
```

Rules:

- A hold order sets `HoldPosition` at the survivor's current position.
- Move order sets `MoveToPoint` at the clicked/aimed position.
- Follow order sets `FollowSurvivor` with the target survivor.
- Map drag-circle orders set `AssignedArea` with an anchor and radius.
- When a `MoveToPoint` order reaches its destination, it becomes `HoldPosition` at that destination. At that point optional hold behaviors such as pickup collection and investigation can run again.
- If a follow target dies, the survivor falls back to `HoldPosition` at its current position.
- If combat interrupts an assignment, the assignment remains stored.
- After combat, the survivor resumes the stored assignment unless it became invalid.
- If looting, investigation, or combat movement starts after a survivor has entered an assigned area once, assigned-area patrol pauses until that temporary behavior finishes and returns control.
- Player orders have a one-time completion gate before AI movement behaviors may override them. A move order is completed when the survivor reaches the destination. An assigned-area order is completed when the survivor enters the circle at least once. Follow remains continuous player intent and does not unlock looting, investigation, or combat movement.
- While a player order still requires travel, the survivor keeps moving toward that player-given target. Combat aim and fire may still merge into that movement so the survivor can strafe and shoot, but combat movement, looting, investigation, and lost-enemy pursuit cannot replace the ordered movement.
- While a player movement order still requires travel, combat aim/fire must not record a delayed lost-enemy investigation target. If the survivor sees or shoots at an enemy during that journey and then reaches the ordered destination after the enemy is gone, it should not walk back across the map to investigate the old last-known position.
- After a move order completes, it becomes `HoldPosition`. After an assigned-area order completes once, temporary AI behaviors may pull the survivor outside the circle. The assigned area remains stored as the fallback order, but the survivor does not need to return to the circle between every AI detour.

Temporary behavior settings are separate from these player-given assignments. Toggling all non-combat settings off or on should not convert a follower into a holder or cancel a move order. It should only stop currently running optional behaviors such as pickup collection or future investigation movement.

Completing a move order also must not change settings. For example, if pickup collection was disabled with I, a later move order can still become `HoldPosition` after arrival, but pickup collection remains disabled until the player explicitly enables it again with O.

## Investigation

Investigation is implemented by `SurvivorInvestigationAI`.

`SurvivorNonCombatAI` is responsible for deciding whether investigation can start:

- `InvestigateSuspiciousStimuli` must be enabled.
- The survivor must be unpossessed.
- The survivor must not have a direct line-of-fire combat target.
- Investigation must not break an unreached move order, unreached assigned-area travel step, or active follow order.
- For assigned-area orders, investigation may start after the survivor has entered the assigned circle once, even if a previous AI behavior has since pulled it outside the circle.

Once allowed, `SurvivorInvestigationAI` owns the investigation target, reachable NavMesh resolution, ally alerts, look-around phase, and return state.

Fresh investigation stimuli may redirect an already-running optional detour. For example, if a survivor is already investigating an older sound or returning from pickup collection, a newer gunshot can replace that temporary target even if the survivor has moved outside its assigned area. This does not let investigation break an unreached player move order or active follow order.

See `Docs/SurvivorInvestigationAI.md`.

## Pickup Collection

Pickup collection is implemented by `SurvivorLootingAI`.

`SurvivorNonCombatAI` is responsible for deciding whether looting can start:

- `CollectVisiblePickups` must be enabled.
- The survivor must be unpossessed.
- The survivor must not have a direct line-of-fire combat target.
- Looting must not break an unreached move order, unreached assigned-area travel step, or active follow order.
- For assigned-area orders, looting may start after the survivor has entered the assigned circle once, even if a previous AI behavior has since pulled it outside the circle.
- Investigation has priority over looting.

Once allowed, `SurvivorLootingAI` owns visible pickup filtering, usefulness checks, pickup movement, pickup chaining, and return state.

See `Docs/SurvivorLootingAI.md`.

## Assigned Area Patrol

Assigned-area patrol is inherent to an `AssignedArea` player order, not a generic "defend mode" toggle. The map UI creates it by holding right-click and dragging a radius from the ordered point. The radius is clamped by `SurvivorAICommandSettings.AssignedAreaMinRadius` and `AssignedAreaMaxRadius`. If the dragged circle is smaller than the minimum radius, the order collapses back to a normal `MoveToPoint`.

When a survivor has an assigned area, it uses the assignment anchor as a local patrol area after it has reached that area.

Suggested behavior:

1. Pick a reachable point within the assigned-area radius, default about `8m`.
2. Move there using `CharacterNavigator`.
3. Wait for a random time, default `4-8s`.
4. Pick another reachable point.

For `HoldPosition`, the anchor is where the survivor was told to hold.
For `AssignedArea`, the anchor is the explicit area center.
For `FollowSurvivor`, wandering should usually be disabled or constrained around the follow target only after the follower has reached stopping distance.

See `Docs/SurvivorAssignedAreaAI.md`.

## Combat Handoff

Non-combat AI is active only while combat AI is not taking over.

Handoff rules:

- If `AllowCombatAIActivation` is enabled and `CharacterSensor` or `SurvivorAIShooting` reports a valid enemy target, combat AI takes over.
- If `AllowCombatAIActivation` is disabled, player-given non-combat orders keep running even if the survivor notices enemies.
- Non-combat AI keeps its current assignment data while suspended.
- If the enemy dies, combat AI releases control and non-combat AI resumes the previous assignment.
- If an enemy survivor is alive but breaks line of fire, non-combat AI can investigate the last known enemy survivor position before returning to the previous assignment.
- Zombies do not create lost-combat investigation tasks. Survivors may alert allies when they directly notice a zombie, but after the zombie is gone or dead they return to their previous assignment instead of walking to the zombie's last known position.
- Lost-combat investigation is treated as a combat handoff. It may start even if combat pulled the survivor outside an assigned area, as long as investigation is enabled and the enemy is not confirmed dead.
- Lost-combat investigation does not override an unreached player movement order. If the survivor is still following, moving to a clicked point, or travelling into an assigned defend area for the first time, that order remains the movement priority.
- Player movement orders also clear any remembered lost-combat target from before the order was issued. This prevents a survivor from obeying a new move/assigned-area command and then resuming an old investigation after arrival.
- Unreached player movement still allows combat aim/fire to merge into the ordered movement. This lets survivors strafe and shoot while continuing toward the ordered destination, without letting combat cover movement or lost-target pursuit pull them away.
- If no assignment remains valid, set `HoldPosition` at the current survivor position.

This means a follower can be attacked, fight, then continue following afterward. A moving survivor can return fire, finish the fight, then continue toward the ordered destination.

## Network Model

Non-combat AI runs on state authority only.

- Player-selected settings and the current assignment should be stored as authoritative survivor state when the UI needs to show or edit them remotely.
- Prefer compact enums and small fields for settings/assignment state so they can be made `[Networked]` cleanly when the UI needs to display them.
- Temporary working state stays local: direct enemy memories, path corners, investigation timers, wander timers, pickup candidates, and cached destinations should not be networked.
- AI emits normal `NetworkedInput`.
- Fusion replicates resulting movement, rotation, pickups, and combat.

The important distinction is authoritative versus noisy. Player intent should be authoritative and durable. AI scratch data should stay private to state authority.

## Implementation Direction

Recommended first implementation path:

1. Add a `SurvivorNonCombatAI` component/input source that owns hold/follow/move assignments. Done.
2. Let `SurvivorAICommandService` create non-combat assignments for idle, follow, and move orders. Done.
3. Preserve current movement and shooting behavior while moving or standing still. Done.
4. Add per-survivor setting storage for future UI editing. Done locally on state authority.
5. Add pickup collection. Done.
6. Add investigation movement and one-hop ally alerting. Done.
7. Split looting and investigation into editor-configurable behavior components. Done.
8. Add assigned-area patrol. Done.

Keep `CharacterNavigator` as the pathfinding helper. Non-combat AI should ask it for path corners and then emit ordinary movement input.

Future non-combat behaviors should follow the looting/investigation pattern:

1. Add a focused `MonoBehaviour` behavior component.
2. Put behavior-specific Inspector tunables on that component.
3. Keep behavior scratch state inside that component.
4. Add only the minimal orchestration and setting gate to `SurvivorNonCombatAI`.
5. Keep player assignment continuity in `SurvivorNonCombatAI`.

## Out Of Scope

- Weapon selection.
- Enemy prioritization.
- Taking cover.
- Zombie-specific behavior.
- Map UI for changing settings.
- Networked display of AI settings.
