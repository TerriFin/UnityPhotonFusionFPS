# Character Separation System

## Context

Teams can contain several survivors following the same leader or moving to the same order point. With hard character collision, the front survivors can block the back survivors and the group turns into a traffic jam. The desired behavior is that friendly characters can pass through each other while still gently pushing apart so they do not stack perfectly.

This system is intentionally separate from AI commands. It should remain active on any character that has the component, even if that character is idle, directly controlled by a player, or spawned without AI.

## Goals

- Same-team survivors can phase through each other.
- Enemy-team survivors still block each other.
- Future zombies can phase through other zombies.
- Characters that are too close apply a light, non-additive separation push.
- The separation logic lives in its own component, not inside `SurvivorNonCombatAI` assignments, combat AI, or future zombie AI.
- Network cost stays minimal: do not network push events, neighbor lists, or extra per-character separation state unless absolutely necessary.

## Non-Goals

- Do not implement full crowd simulation.
- Do not solve formations or tactical spacing.
- Do not make enemies phase through each other.
- Do not let clients apply unsynchronized local push forces.
- Do not add separation logic separately to every AI controller.

## Authority Model

Separation should be calculated only on state authority.

Clients should receive the final replicated character movement through Fusion, the same way they already receive normal survivor movement. The system should not send RPCs such as "push this character" and should not add new networked fields for temporary push forces.

Good:

```csharp
// State authority only
Vector3 separationVelocity = separation.GetSeparationVelocity();
MoveSurvivor(desiredMoveVelocity + separationVelocity, jumpImpulse);
```

Avoid:

```csharp
// Do not do this
RpcPushCharacter(otherCharacter, pushDirection);
```

## Component Shape

Add a reusable component, for example:

```csharp
CharacterSeparation
```

The component should be assignable to survivors now and zombies later.

Current inspector fields:

```csharp
public CharacterSeparationKind Kind = CharacterSeparationKind.Survivor;
public float Radius = 0.8f;
public float DesiredDistance = 0.65f;
public float PushSpeed = 1.5f;
public int MaxNeighbors = 8;
public float RefreshInterval = 0.1f;
```

The component should expose a method similar to:

```csharp
public Vector3 GetSeparationVelocity()
```

The component owns neighbor detection and team/faction filtering. The survivor movement path only consumes the returned velocity.

Survivors auto-add and activate `CharacterSeparation` in `Survivor.Spawned()` if the prefab does not already have one. This keeps the component assignable/configurable on prefabs while still making the prototype work without scene or prefab edits.

## Movement Integration

`CharacterSeparation` should not call `KCC.Move(...)` by itself. `Survivor.MoveSurvivor(...)` already owns KCC movement and acceleration. Calling KCC from multiple components in the same tick risks jitter and hard-to-debug movement order issues.

Instead, `Survivor` should have a small integration point:

```csharp
Vector3 separationVelocity = Separation != null ? Separation.GetSeparationVelocity() : Vector3.zero;
_moveVelocity = Vector3.Lerp(_moveVelocity, desiredMoveVelocity + separationVelocity, acceleration * Runner.DeltaTime);
KCC.Move(_moveVelocity, jumpImpulse);
```

This keeps separation separate from AI while still moving through the normal networked KCC path.

Idle characters need special attention. The current settled-character optimization skips `KCC.Move(...)` when inactive grounded characters have no input and near-zero velocity. `Survivor.FixedUpdateNetwork()` now checks `HasSeparationIntent()` before taking that skip so an overlapped idle character wakes up and can be pushed apart.

## Non-Additive Push

If several characters overlap the same survivor, the push force should not multiply by neighbor count.

Use averaged direction instead of additive force:

```csharp
Vector3 awaySum = Vector3.zero;
int neighborCount = 0;

foreach (CharacterSeparation neighbor in nearbyCharacters)
{
    if (ShouldSeparateFrom(neighbor) == false)
        continue;

    Vector3 away = transform.position - neighbor.transform.position;
    away.y = 0f;

    if (away.sqrMagnitude <= 0.0001f)
        away = transform.right;

    awaySum += away.normalized;
    neighborCount++;
}

if (neighborCount == 0)
    return Vector3.zero;

return awaySum.normalized * PushSpeed;
```

This means:

- One overlapping teammate pushes with `PushSpeed`.
- Five overlapping teammates still push with about `PushSpeed`.
- The push direction becomes the average direction away from the crowd.

## Collision / Phasing Rules

There are two related but separate parts:

1. **Collision filtering** decides whether characters physically block each other.
2. **Separation steering** decides whether characters gently move apart.

The desired filtering rules:

| Pair | Hard Collision | Separation |
| --- | --- | --- |
| Same-team survivor vs same-team survivor | No | Yes |
| Enemy survivor vs enemy survivor | Yes | No, unless later wanted |
| Survivor vs zombie | Yes | No, unless later wanted |
| Zombie vs zombie | No | Yes |

Unity layer masks alone are not enough for same-team phasing, because all survivors currently share the same character layer and team ownership is dynamic. `CharacterSeparation` therefore uses two layers of filtering:

1. `SimpleKCC.ResolveCollision` returns `false` for same-team survivor colliders so KCC movement does not resolve them as blockers.
2. `Physics.IgnoreCollision(...)` is still applied to the underlying colliders as a broad physics-side backup.

The phasing solution should be local physics configuration, not networked state. Every peer can derive the same phasing rule from replicated owner/team data:

```csharp
bool shouldPhase =
    bothAreSurvivors && sameOwnerTeam ||
    bothAreZombies;
```

If a neutral survivor is added later, it should not phase with player teams until it is recruited and receives that team's ownership/faction.

## Neighbor Detection

The first implementation uses a static registry of active `CharacterSeparation` components instead of per-character physics overlap queries. This avoids per-tick collider query allocations and keeps the logic independent from the exact KCC collider setup.

Filtering happens while iterating the registry:

- Ignore self.
- Ignore dead characters.
- Ignore characters outside the phasing/separation relationship.
- Ignore enemies for friendly separation.
- Clamp processing to `MaxNeighbors`.

At prototype scale, a flat registry is acceptable for survivors. For larger zombie counts, the same component can later switch to a grid or spatial buckets without changing AI code.

`RefreshInterval` throttles the registry scan per character. The component caches the last separation direction and refreshes it on a jittered interval, so a large horde does not perform an `active separators x active separators` scan every simulation tick. `RefreshInterval = 0` restores per-call calculation.

## Future Zombie Use

Zombies use the same separation component with zombie-tuned values:

```csharp
Radius: smaller
PushSpeed: lower
MaxNeighbors: lower
RefreshInterval: 0.1 or higher
```

Zombie-vs-zombie phasing should reduce horde traffic jams. Zombie-vs-survivor collision should remain solid enough for attacks and body pressure.

## Implementation Plan

1. `CharacterSeparation` lives at `Assets/Scripts/AI/Navigation/CharacterSeparation.cs`.
2. It exposes configurable kind, radius, desired distance, push speed, and max neighbor count.
3. It calculates separation velocity only when called by a state-authority survivor.
4. `Survivor` has a `CharacterSeparation Separation` property and auto-adds the component on spawn if missing.
5. `MoveSurvivor(...)` adds separation velocity before calling `KCC.Move(...)`.
6. The settled-character skip checks `HasSeparationIntent()` so overlap/separation wakes idle characters.
7. Same-team survivor phasing uses `SimpleKCC.ResolveCollision` plus pairwise `Physics.IgnoreCollision(...)`; enemy teams keep normal collision.
8. Zombies use the same component with `Kind = Zombie`.

## Testing Checklist

- Two same-team survivors can walk through each other.
- Same-team survivors gently separate instead of stacking.
- Four or more same-team survivors following the player no longer form a hard collision jam.
- Enemy-team survivors still block each other.
- Idle same-team survivors still separate when another teammate walks into them.
- No client-side-only pushing is visible; clients simply see state-authority movement.
- Network traffic does not increase beyond normal character transform/KCC replication.
