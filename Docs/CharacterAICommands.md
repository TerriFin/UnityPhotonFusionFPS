# Survivor AI Commands

## Goal

Survivor AI is command-driven. Gameplay owns match state and team membership, but AI command selection and application live in `SurvivorAICommandService` so future behaviors do not get hardcoded into `Gameplay.cs`.

Current behaviors:

- `SurvivorNonCombatAI` owns the current non-combat assignment for uncontrolled survivors.
- Hold position keeps a survivor in place and can still use combat shooting when a direct enemy is visible.
- Follow assignment follows the target survivor, using `CharacterNavigator` path corners when available and direct movement as fallback.
- Move assignment moves a survivor toward a commanded world point through `CharacterNavigator`.
- Assigned-area assignment moves a survivor to a map-dragged circle and then patrols reachable points inside it.

This version intentionally has no avoidance, cover behavior, formation logic, or advanced tactical movement.

Long-term AI is split into two higher-level layers:

- Non-combat AI owns hold/follow/move/assigned-area assignments, investigation, pickup collection, and patrol movement. See `Docs/SurvivorNonCombatAI.md`.
- Combat AI temporarily takes over when the survivor detects a direct enemy target. See `Docs/SurvivorCombatAI.md`.

Current follow and move orders remain player-facing commands, but they are now represented as non-combat assignments instead of separate top-level AI modes. Temporary I/O hotkeys toggle AI behavior settings and should not replace the current follow/move/hold assignment.

Enemy awareness and combat-event perception are planned separately in `Docs/CharacterSensorySystem.md`. AI behaviors should consume sensor output, but command routing should stay independent from perception.

Automatic survivor shooting is documented in `Docs/SurvivorAIShooting.md`.

## Files

```text
Assets/Scripts/Survivor/Survivor.cs
Assets/Scripts/Survivor/SurvivorInput.cs
Assets/Scripts/Survivor/AI/SurvivorNonCombatAI.cs
Assets/Scripts/Survivor/AI/SurvivorLootingAI.cs
Assets/Scripts/Survivor/AI/SurvivorInvestigationAI.cs
Assets/Scripts/Survivor/AI/SurvivorAssignedAreaAI.cs
Assets/Scripts/Survivor/AI/SurvivorAIShooting.cs
Assets/Scripts/Survivor/AI/SurvivorAICommandService.cs
```

## Input Source Model

`Survivor.cs` owns the input-source interface:

```csharp
public interface ICharacterInputSource
{
    NetworkedInput GetInput(NetworkRunner runner);
}
```

The active survivor reads human input through Fusion. Inactive survivors on state authority use `_aiController.GetInput(Runner)`. AI returns the same `NetworkedInput` shape as a human, so movement, looking, weapon logic, and button handling stay in `Survivor.ProcessInput(...)`.

On the local client, the human input provider is the survivor currently assigned as `Runner.GetPlayerObject(Runner.LocalPlayer)`. `PlayerData.ActiveCharacterIndex` still defines which survivor is active for gameplay, but `SurvivorInput` should not rely only on that replicated dictionary to decide whether to subscribe to `NetworkEvents.OnInput`. Following the local `PlayerObject` prevents delayed or stale replicated team data from briefly stopping input collection for the active survivor.

`SurvivorInput.BeforeUpdate()` clears movement and button state before reading the current hardware frame. This keeps held commands, fire, or movement from surviving cursor unlocks, focus changes, or input-provider registration changes. Mouse look remains tick-aligned through `Vector2Accumulator`.

## AI Behaviors

`SurvivorNonCombatAI` is the default input source for inactive survivors. It is a survivor component and stores one current non-combat assignment: hold position, follow survivor, move to point, or assigned area. `SurvivorLootingAI` and `SurvivorInvestigationAI` are sibling components that hold the editable tuning and scratch state for those optional behaviors.

Hold position:

- Returns zero movement.
- Uses `SurvivorAIShooting` when there is a direct target with line of fire and the weapon/fire mode allows shooting.
- The roster combat movement toggle controls tactical repositioning only; shooting is controlled by the weapon/fire mode.
- Uses `CharacterSensor` investigation look input only when its non-combat settings allow investigation.

Follow assignment:

- Returns default input if the target is missing or dead.
- Falls back to hold position at the survivor's current position if the target dies.
- Uses `CharacterNavigator` to route toward the target when the follower has one.
- Falls back to direct movement if pathfinding is unavailable or no path corner is available.
- Emits a clamped yaw delta in `LookRotationDelta`.
- Emits forward movement while outside stopping distance.
- Can layer `SurvivorAIShooting` look/fire input on top of movement when a direct enemy has line of fire.
- Uses `CharacterSensor` investigation look input when it has reached stopping distance and can safely look around without walking away from the follow target.
- Does not directly move transforms.

Move assignment:

- Stores the destination as the assignment anchor.
- Sets that destination on `CharacterNavigator`.
- Steers toward the current path corner and keeps emitting movement until the destination is reached.
- Can layer `SurvivorAIShooting` look/fire input over movement when a direct enemy has line of fire.
- The combat movement toggle is separate from non-combat orders. Disabling it does not cancel follow, move, hold, or assigned-area orders; it stops tactical repositioning against both enemy survivors and zombies.
- Once the destination is reached, the move point remains a persistent guard anchor. Combat, investigation, pickup collection, or recruiting may temporarily pull the survivor away, but it returns to that anchor afterward.
- Does not directly move transforms.

`SurvivorNonCombatAI` can consume `SurvivorAIShooting` input when a direct target has line of fire. Hold position can aim/fire while standing still unless the weapon/fire mode is `HoldFire`. Movement assignments should continue their movement order while aiming/firing when possible, so survivors can return fire during long moves or while following. If shooting has no direct target, assignments fall back to sensor look input only when allowed by settings and when that does not break their movement responsibility. Shooting input can hold `Fire` for configurable bursts, so automatic weapons fire differently from semi-auto weapons without special AI weapon code.

When movement and shooting both want look control, direct combat aim has priority over path-corner look while the target has line of fire. Movement still uses the same `MoveDirection` input, so the character keeps advancing instead of stopping just because it is firing.

Because `Survivor.ProcessInput(...)` applies look rotation before converting `MoveDirection` into world movement, movement AI must convert the desired world path direction into local input when combat aim controls `LookRotationDelta`. This allows a survivor to keep moving toward the path corner while looking and firing at an enemy off to the side.

Inactive survivors normally move with `Survivor.AIMoveSpeed`, making ordered AI movement slower than direct player control. A follow assignment targeting the currently possessed survivor uses normal `MoveSpeed` only while the follower is within `Survivor.AIFollowFullSpeedRadius`; farther followers use `AIMoveSpeed` so a long-range follow order does not make them sprint across the whole map.

## Command Service

`SurvivorAICommandService` is the central place for survivor AI orders. `Gameplay` creates it with the current survivor cache and settings:

```csharp
public SurvivorAICommandService SurvivorAICommands { get; }
```

All nearby team orders should use one shared order radius:

```csharp
public float CommandRadius = 12f;
```

This replaces per-order radii such as `FollowCommandRadius`, `AutoShootCommandRadius`, or future `MoveCommandRadius`. The radius answers only "which uncontrolled same-team survivors hear/react to this order?" Order-specific values, such as maximum raycast distance for a move destination, should remain separate settings.

The service exposes generic application methods:

```csharp
public void ApplyNearbyTeamCommand(
    PlayerRef owner,
    int originCharacterIndex,
    SurvivorAICommand command)
```

This helper reads `SurvivorAICommandSettings.CommandRadius` internally so every movement-state order uses the same selection area. Combat fire permissions are no longer controlled by a temporary hotkey; they should become combat AI settings instead.

`SurvivorAICommand` wraps the behavior factory:

```csharp
SurvivorAICommand.Follow(targetSurvivor) // creates a SurvivorNonCombatAI follow assignment
SurvivorAICommand.MoveTo(destination)    // creates a SurvivorNonCombatAI move assignment
SurvivorAICommand.AssignedArea(center, radius) // creates a circular defend/patrol assignment
SurvivorAICommand.Idle()                 // creates a SurvivorNonCombatAI hold assignment for explicit hold use
```

This keeps command parameters with the command. Command factories receive the affected survivor and must pass it into AI behaviors that need local components. Movement-state commands reuse the survivor's stored non-combat settings; they do not reset settings to enabled or disabled. RTS map orders can add commands like `AssignedArea(Vector3 center, float areaRadius)` or future commands like `AttackTarget(NetworkObject target)` without adding those behaviors to `Gameplay.cs`.

`SurvivorAICommandService` also owns the M-key move order through `MoveNearbyTeamToLookPoint(...)` and the I/O-key all-settings toggle through `SetNearbyTeamNonCombatSettings(...)`.

The current hotkeys are temporary direct inputs for orders. In the future, a radial command menu should call the same command-service methods, still using the shared `CommandRadius` to decide which nearby uncontrolled survivors receive the selected order. Movement-state orders should be explicit set-state commands rather than toggles.

Current gameplay hotkeys:

- `F`: set nearby inactive teammates to follow the active survivor.
- `M`: set nearby inactive teammates to move to the active survivor's look point.
- `I`: disable all temporary non-combat behavior settings for nearby inactive teammates.
- `O`: enable all temporary non-combat behavior settings for nearby inactive teammates.
- `1`, `2`, `3`: switch the active survivor to pistol, rifle, or shotgun.
- `R`: reload the active survivor's current weapon.
- `Space`: jump.
- `Left Shift` / `Left Ctrl`: switch the directly controlled survivor.
- `Alt`: toggle the map overlay.
- While the map is open, `I` and `O` apply the same settings toggle to selected survivors, and `Left Shift` / `Left Ctrl` cycle selected survivor icons.

There is intentionally no current hold-fire hotkey. Hold fire should be implemented later as a combat AI/fire-mode setting rather than as a temporary nearby order.

## Follow Order

The F key means "set nearby uncontrolled survivors to follow me" for now. It is not a toggle. `SurvivorInput.BeforeUpdate()` sets:

```csharp
_accumulatedInput.Buttons.Set(EInputButton.CommandFollow, keyboard.fKey.isPressed);
```

Only the active survivor reacts to the command on state authority:

```csharp
_sceneObjects.Gameplay.SurvivorAICommands.SetNearbyTeamFollow(OwnerRef, CharacterIndex);
```

The follow order uses `SurvivorAICommandSettings.CommandRadius` and applies only to same-team survivors that are:

- Alive.
- Not the active survivor.
- Within radius of the active survivor.

Rules:

- Every affected survivor receives a `SurvivorNonCombatAI` follow assignment targeting the active survivor, regardless of previous AI state.
- Survivors already following the active survivor remain following.
- Survivors following a different survivor are retargeted to the current active survivor.
- Survivors outside radius and dead survivors are unchanged.

This avoids team-command desync after possession switches. If the player possesses a follower, the previously possessed survivor may become idle while other survivors continue following them. Pressing F again should put all nearby uncontrolled survivors into the same follow state instead of toggling some off.

## Temporary Settings Toggle

The I and O keys are temporary mass toggles for AI behavior settings:

- I = disable all current non-combat/combat-activation settings.
- O = enable all current non-combat/combat-activation settings.

They do not change the current player-given assignment. A follower keeps following, a survivor moving to a point keeps moving, and a holder keeps holding.

`SurvivorInput.BeforeUpdate()` sets:

```csharp
_accumulatedInput.Buttons.Set(EInputButton.CommandIdle, keyboard.iKey.isPressed);
_accumulatedInput.Buttons.Set(EInputButton.CommandEnableNonCombatAI, keyboard.oKey.isPressed);
```

Only the active survivor reacts to the commands on state authority:

```csharp
_sceneObjects.Gameplay.SurvivorAICommands.SetNearbyTeamNonCombatSettings(OwnerRef, CharacterIndex, false);
_sceneObjects.Gameplay.SurvivorAICommands.SetNearbyTeamNonCombatSettings(OwnerRef, CharacterIndex, true);
```

The settings toggle uses `SurvivorAICommandSettings.CommandRadius` and applies only to same-team survivors that are alive, inactive, and within radius. Turning settings off cancels temporary optional behaviors such as pickup collection and returns the survivor to its current assignment anchor. It does not cancel explicit move/follow orders.

The setting state is durable per survivor. A later F/M/hold assignment must preserve the stored setting state, so completing a move order does not re-enable pickup collection or investigation.

## Move Order

The M key means "set nearby uncontrolled survivors to move to the point I am aiming at" for now. It is also an explicit set-state order. `SurvivorInput.BeforeUpdate()` sets:

```csharp
_accumulatedInput.Buttons.Set(EInputButton.CommandMove, keyboard.mKey.isPressed);
```

Only the active survivor reacts to the command on state authority:

```csharp
_sceneObjects.Gameplay.SurvivorAICommands.MoveNearbyTeamToLookPoint(OwnerRef, CharacterIndex);
```

`MoveNearbyTeamToLookPoint(...)` raycasts from the active survivor camera/look direction using `MoveCommandMaxDistance` and `MoveCommandHitMask`. If it hits valid world geometry, same-team alive inactive survivors inside `CommandRadius` receive a `SurvivorNonCombatAI` move assignment.

Every affected survivor receives a move assignment for the new destination, regardless of previous AI state.

## Group Lane Spread

When a group command resolves to a static-destination assignment (move or assigned-area), `SurvivorAICommandService` distributes the affected survivors across lateral lanes so they fill the corridor instead of forming a conga line on the optimal path.

- The command service sorts the affected survivors by `CharacterIndex` and assigns them lane indices `0, +1, -1, +2, -2, ...` (centre, then alternating outward).
- All lane-spread tuning lives on `SurvivorAICommandSettings`, which is the serialized field on the `Gameplay` GameObject next to `CommandRadius`/`AssignedAreaMinRadius`:
  - `LaneSpacing` (default `0.9 m`) — meters between adjacent lanes. Set to `0` to disable the spread entirely.
  - `MaxLaneOffset` (default `2.5 m`) — maximum absolute sideways offset from the real path. This prevents very large groups from spreading into absurdly wide lines. Set to `0` or less to disable the cap.
  - `LaneOffsetTaperDistance` (default `4 m`) — fade the offset to `0` as the survivor approaches the final destination, so defending / arriving groups still converge instead of stopping in a fan shape.
  - `LaneOffsetCornerSoftenDistance` (default `1.5 m`) — soften the offset near the current path corner, so a perpendicular shove doesn't make a survivor cut a corner from the inside.
  - `LaneOffsetSampleDistance` (default `1.0 m`) — `NavMesh.SamplePosition` clamp distance applied to the offset target so a sideways shove never pushes the steering target into a wall.
  - `ValidateLaneOffsetPath` (default `true`) — after the offset target is sampled onto the NavMesh, `CharacterNavigator` does one cheap `NavMesh.Raycast` from the survivor to that offset target. If that straight NavMesh segment is blocked, it ignores the lane offset for that steering query and uses the original path corner.
  - `MoveStoppingDistanceIncreasePerExtraSurvivor` (default `0.2 m`) — move orders increase their arrival radius by this much for each extra survivor beyond the first ordered survivor.
  - `MoveStoppingDistanceMax` (default `3 m`) — cap for that group-scaled move arrival radius. The cap never shrinks a survivor below its base `DefaultMoveStoppingDistance`.
- The assignment helper writes the lane values onto `CharacterNavigator` each time it installs a new input source. The navigator carries them as runtime state (the prefab adds the navigator via `AddComponent` at startup, so these aren't authored per-prefab — retuning the Gameplay settings is the only place the values are edited).
- `CharacterNavigator.TryGetSteeringTarget` applies the lateral offset perpendicular to the current path segment direction, caps it by `MaxLaneOffset`, then `NavMesh.SamplePosition` clamps it back onto walkable mesh. Where the corridor narrows the offset gets clamped or rejected and the group files through; once the corridor widens again they spread back out automatically.
- Move-order arrival uses the per-order stopping distance, including the group-scaled value, plus the navigator's vertical reach tolerance. This lets larger groups accept a loose cluster around the point without falsely completing a move on the ground below an elevated target.
- `SurvivorAICommand` carries an `AllowsLaneSpread` flag. `MoveTo` and `AssignedArea` set it to `true`; `Idle` and `Follow` leave it `false`, and the assignment helper resets the navigator lane to `0` in those cases so a previous group's lane assignment never leaks into a follow target's path. Single-target commands also reset to `0` automatically because the group size is `1`.

This is intentionally cheap: one perpendicular vector, one `NavMesh.SamplePosition`, and optionally one `NavMesh.Raycast` per lane-spread steering query. It avoids extra path calculations while still preventing most indoor offsets that would make KCC steer through props.

## Possession Rules

The active survivor is always controlled by human input. When switching control:

- The old active survivor returns to default `SurvivorNonCombatAI`.
- The old active survivor resets vertical look pitch to neutral while preserving yaw, so unpossessed survivors do not keep staring up or down in the player's last view direction.
- The new active survivor returns to default `SurvivorNonCombatAI`.
- Existing followers do not automatically retarget; press F again to issue a new follow command.

## Network Model

AI assignment is intentionally not networked yet.

- AI runs only on state authority.
- Proxies receive resulting transform/KCC state from Fusion.
- Clients do not need to know which AI class is assigned.

If UI later needs to show command state to clients, add a small networked enum at that point.

## Out Of Scope

- Obstacle avoidance.
- Formation spacing.
- Attack/defend AI.
- RTS camera selection.
- Networked AI command display.
- Full control rebinding.
