# Pathfinding System

## Goal

Add pathfinding for AI-controlled characters without replacing the existing Fusion/KCC movement path.

Unity NavMesh should be used for path planning only. Characters still move by producing normal `NetworkedInput`, and `Survivor.ProcessInput(...)` still owns actual movement through SimpleKCC.

Implemented first pass:

- `CharacterNavigator` uses `NavMesh.CalculatePath`.
- `SurvivorNonCombatAI` follows path corners for move assignments using normal `NetworkedInput`.
- `SurvivorNonCombatAI` optionally uses path corners for follow assignments instead of direct walking.

Current player commands route through `SurvivorNonCombatAI`.

## Core Rule

Do not let `NavMeshAgent` move survivor transforms.

Use Unity's NavMesh APIs to calculate path corners, then steer the character toward those corners using the same input shape as player movement:

```csharp
input.LookRotationDelta = ...
input.MoveDirection = Vector2.up;
```

This keeps movement authoritative, network-friendly, and consistent with possessed characters.

## Files

Expected implementation files:

```text
Assets/Scripts/AI/Navigation/CharacterNavigator.cs
Assets/Scripts/Survivor/AI/SurvivorNonCombatAI.cs
Assets/Scripts/Survivor/Survivor.cs
Assets/Scripts/Survivor/SurvivorInput.cs
Assets/Scripts/Survivor/AI/SurvivorAICommandService.cs
```

Create the `Assets/Scripts/AI/Navigation/` folder when implementing the navigator.

## CharacterNavigator

`CharacterNavigator` should be a reusable component, not survivor-specific where possible.

Responsibilities:

- Store the current destination.
- Calculate a `NavMeshPath` with `NavMesh.CalculatePath`.
- Cache path corners.
- Track the current corner index.
- Recalculate paths on an interval, not every tick.
- Expose the next steering target.
- Report whether the destination is reached, invalid, or unreachable.
- Resolve off-NavMesh target positions into nearby reachable NavMesh points when requested.

Suggested inspector fields:

```csharp
public float RepathInterval = 0.25f;
public float CornerReachDistance = 0.35f;
public float DestinationReachDistance = 1.25f;
public float DestinationChangeRepathDistance = 0.5f;
public float SampleMaxDistance = 2f;
public float ReachablePointSampleMaxDistance = 6f;
public int AreaMask = NavMesh.AllAreas;
```

Suggested public API:

```csharp
public bool HasDestination { get; }
public bool HasPath { get; }
public bool IsPathPending { get; }
public bool IsDestinationReached { get; }
public Vector3 Destination { get; }

public void SetDestination(Vector3 destination);
public void ClearDestination();
public bool TryGetSteeringTarget(Vector3 currentPosition, out Vector3 steeringTarget);
public bool TryFindReachablePoint(Vector3 currentPosition, Vector3 targetPosition, out Vector3 reachablePoint);
public void Tick(Vector3 currentPosition);
```

`Tick(...)` should:

1. Return early if there is no destination.
2. Recalculate the path only when the repath timer has elapsed.
3. Use `NavMesh.SamplePosition(...)` for the current position and destination before calculating the path.
4. Call `NavMesh.CalculatePath(...)`.
5. Treat `NavMeshPathStatus.PathComplete` as valid.
6. Optionally allow `PathPartial` for investigation commands later, but start strict for move/follow behavior.
7. Advance the corner index when close enough to the current corner.

`TryFindReachablePoint(...)` is used before setting destinations for suspicious investigation targets. It samples nearby NavMesh around the raw target and verifies a complete path from the survivor. This is useful when a player creates a stimulus from a position that is valid for players but not AI-walkable, such as firing from the top of a car.

`NavMeshPath` must be created in `Awake()` or another Unity lifecycle method, not in a field initializer or MonoBehaviour constructor. Unity's native NavMesh path initialization is not allowed during script construction.

The component should not call `transform.position`, `NavMeshAgent.SetDestination`, or any movement API.

## Move Assignment

The move assignment is currently implemented inside `SurvivorNonCombatAI`, which implements:

```csharp
Survivor.ICharacterInputSource
```

It should:

- Store the controlled survivor.
- Store the destination.
- Use `CharacterNavigator` for path corners.
- Emit look rotation toward the next steering target.
- Emit forward movement while not at destination.
- Stop when destination is reached.
- Consume `SurvivorAIShooting` while moving or stopped when a direct enemy has line of fire.

Suggested behavior:

```text
No survivor / dead survivor -> default input
No navigator -> direct fallback toward destination
Destination reached -> idle/look/shoot as appropriate
Visible combat target -> keep move input, aim/fire at target
Path available -> steer toward next path corner
Path unavailable -> stop, or direct fallback only if close enough
```

Movement input should stay simple at first:

```csharp
input.MoveDirection = Vector2.up;
```

Do not add strafing, sprinting, jumping, cover seeking, or obstacle recovery in the first pass.

## Follow Assignment Path Option

The follow assignment currently walks toward the followed survivor.

Change it so it can optionally use `CharacterNavigator`:

```csharp
public bool UsePathfinding = true;
```

Recommended behavior:

- If `UsePathfinding` is true and a `CharacterNavigator` exists, set the target survivor position as the destination.
- Repath through `CharacterNavigator` at its own interval.
- Steer toward the current path corner instead of directly toward the target survivor.
- Keep the existing direct movement fallback if no navigator exists or pathfinding fails.
- Preserve the existing stopping-distance behavior.
- Allow shooting/look combat input while moving when a direct enemy has line of fire.
- Preserve investigation/noise look behavior once within stopping distance.

This avoids breaking the current follow command while allowing pathfinding to be enabled per prefab or per AI behavior.

## Input Steering

Both move and follow assignments should use the same steering helper pattern:

```csharp
Vector3 toTarget = steeringTarget - survivor.transform.position;
toTarget.y = 0f;

float desiredYaw = Quaternion.LookRotation(toTarget).eulerAngles.y;
float currentYaw = survivor.KCC.GetLookRotation(false, true).y;
float yawDelta = Mathf.DeltaAngle(currentYaw, desiredYaw);

input.LookRotationDelta = new Vector2(0f, Mathf.Clamp(yawDelta, -maxYawDegreesPerTick, maxYawDegreesPerTick));
input.MoveDirection = Vector2.up;
```

If the steering target is extremely close, advance to the next corner or stop.

Group move and assigned-area orders can apply a lateral lane offset to the current path corner so squads spread across roads instead of stacking into one line. The lane offset is still only a steering target; it does not create a separate path. To keep this usable indoors, `CharacterNavigator` caps the total lane offset and can reject an offset target with a single `NavMesh.Raycast` if the straight NavMesh segment from the survivor to the offset target crosses a blocked edge. This is much cheaper than recalculating a separate path per survivor lane.

When combat and path steering both want to control `LookRotationDelta`, direct visible combat target aim should win. The movement AI should still emit movement input. Because KCC movement is relative to the current look rotation, movement AI should convert the desired world path direction into local `MoveDirection` after applying the combat look delta. If there is no direct target with line of fire, use the steering target for look rotation as usual. Do not use noise, bullet-impact, or last-known-position memories as firing targets while moving; those remain investigation look targets.

## Survivor Integration

`Survivor.Spawned()` should find or add the navigator in the same style as `CharacterSensor` and `SurvivorAIShooting`:

```csharp
Navigator = GetComponent<CharacterNavigator>();
if (Navigator == null)
{
    Navigator = gameObject.AddComponent<CharacterNavigator>();
}
```

Add a property:

```csharp
public CharacterNavigator Navigator { get; private set; }
```

This keeps AI behavior construction simple and avoids repeated `GetComponent` calls.

## Command Integration

Add a command shape to `SurvivorAICommand`:

```csharp
SurvivorAICommand.MoveTo(Vector3 destination)
```

The first player-facing command should use the `M` key:

```text
Player points at the world and presses M.
The active survivor raycasts from the camera/look direction.
State authority finds same-team uncontrolled survivors inside command radius.
Matching survivors receive a `SurvivorNonCombatAI` move assignment toward the hit point.
```

Suggested settings:

```csharp
public float MoveCommandMaxDistance = 80f;
public LayerMask MoveCommandHitMask;
```

Move orders should use the shared `SurvivorAICommandSettings.CommandRadius` to decide which nearby uncontrolled survivors receive the order. Do not add a separate `MoveCommandRadius`.

`MoveCommandHitMask` should include walkable world geometry, usually the same environment layers used by the map and NavMesh. It should not include players, hitboxes, pickups, UI, or first-person-only visuals.

When RTS selection exists, selected survivors should receive move assignments with individual destination offsets instead of all sharing the exact clicked point.

First implementation does not need full RTS selection. It can use the same nearby-team pattern as follow/idle/shooting commands:

- Add `EInputButton.CommandMove`.
- Set it from `SurvivorInput` when `M` is pressed.
- Handle it in `Survivor.ProcessInput(...)` only for the active survivor on state authority.
- Add `SurvivorAICommandService.MoveNearbyTeamToLookPoint(...)`.
- Use a camera/look raycast to choose the destination.
- Assign a `SurvivorNonCombatAI` move assignment to same-team, alive, inactive survivors inside `CommandRadius`.

The current `F`, `I`, `M`, and `G` hotkeys are temporary order inputs. `F`, `I`, and `M` should set movement state explicitly rather than toggling it. When the radial command menu is added later, it should call the same command-service methods and keep using the shared `CommandRadius` for nearby uncontrolled survivor selection.

Do not build the RTS camera or selection system as part of the first pathfinding pass.

## Sensor And Combat Integration

Pathfinding should not replace sensing or shooting.

Expected flow:

```text
CharacterSensor remembers enemy/noise/bullet impact.
AI command decides movement intent.
CharacterNavigator provides route corners.
SurvivorAIShooting decides whether to aim/fire.
Survivor.ProcessInput moves/fires through existing systems.
```

Movement orders and shooting should not be mutually exclusive. A survivor following a path can keep advancing along its route while `SurvivorAIShooting` rotates and fires at a directly visible enemy. Moving survivors should take longer to lock onto enemies, but their extra inaccuracy should come later from the shared weapon sway/handling system, not from pathfinding AI.

Later, bullet impact reaction can become:

```text
RecordBulletImpact(...)
-> InvestigatePositionAI(ApproximateSourcePosition)
-> CharacterNavigator routes toward that position
-> SurvivorAIShooting takes over if a visible enemy appears
```

Do not make path state `[Networked]`. AI runs on state authority, and Fusion replicates the resulting movement.

## NavMesh Setup Assumptions

The scene should have a baked NavMesh before this system is expected to work.

For the current Deathmatch warehouse:

- Floors should be walkable.
- Walls, cargo containers, and large props should carve/block navigation.
- Pickups should not block navigation.
- The agent radius should roughly match survivor collision width.
- The agent height should roughly match survivor height.
- The agent step height and slope should match what SimpleKCC can actually traverse.

If NavMesh says a path is valid but KCC cannot physically move through it, adjust the NavMesh agent settings or scene blockers rather than forcing the AI through.

## Performance Rules

- Do not calculate paths every Fusion tick.
- Repath at intervals, usually `0.2s-0.5s`.
- Stagger future zombie repaths so they do not all calculate on the same frame.
- Keep path state local to state authority.
- Use direct fallback sparingly, mostly for short distances.
- Avoid per-character `NavMeshAgent` movement unless a future non-survivor actor explicitly needs it.

## Out Of Scope For First Pass

- RTS camera and selection UI.
- Formations.
- Cover selection.
- Door opening.
- Jump/climb/elevator/off-mesh traversal.
- Dynamic obstacle carving for every small prop.
- Zombie horde/group pathing.
- Movement LOD for distant zombies.
- Networked command display.

## First Implementation State

- `CharacterNavigator` exists and uses `NavMesh.CalculatePath`.
- `Survivor.Spawned()` finds or adds `CharacterNavigator`.
- `SurvivorNonCombatAI` move assignments follow path corners using normal `NetworkedInput`.
- The `M` key nearby move command creates a `SurvivorNonCombatAI` move assignment.
- `SurvivorNonCombatAI` follow assignments use navigator path corners when available and direct movement as fallback.
