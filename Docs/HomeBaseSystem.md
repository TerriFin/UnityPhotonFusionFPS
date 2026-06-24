# Home Base System

## Goal

Each player has one persistent circular home-base area on the tactical map.

The home base:

- starts at the player's assigned match spawn,
- starts with a radius of `3m`,
- is always shown to its owner as a light-green circle,
- can be moved and resized through the same right-click drag gesture as a survivor assigned-area patrol,
- provides the destination used by `SurvivorRetreatAI`,
- can later become the anchor for additional home-base features.

The home base is not a physical building and does not automatically order survivors to move. It is authoritative per-player area data plus a local map marker.

## Data

The authoritative home base needs:

```csharp
public struct PlayerHomeBase
{
	public Vector3 Center;
	public float Radius;
}
```

Initial values:

```text
Center: player's assigned match spawn position
Radius: 3m
```

The center and radius are stored once per player. Do not duplicate them onto every survivor.

The home base should initialize when the player's match spawn is assigned. Late joiners receive a home base at their own assigned spawn. Starting a fresh match with new spawn assignments resets each player's home base to the new spawn and default radius.

## Map Marker

The full-screen map always displays the local player's home base when it has been initialized.

Visual rules:

- Draw a persistent light-green circle at approximately 75% opacity (25% transparent).
- Use the same light-green, semi-transparent styling for the placement preview while the home base is being dragged.
- Keep it visible regardless of survivor selection.
- Scale it from world radius using the same conversion as assigned-area previews.
- Keep the shape circular while panning and zooming.
- Clip or hide only the portions outside the visible map viewport in the same manner as other map overlays.
- Do not draw enemy players' home bases.

Recommended hierarchy:

```text
GameUI
  MapView
    HomeBaseCircle
    AssignedAreaCircle
```

`HomeBaseCircle` is persistent. `AssignedAreaCircle` remains the temporary right-drag preview. They should not be the same UI object because the home marker must remain visible while another patrol area is being previewed.

During a home-base drag, the temporary preview may use a brighter or more transparent light green. On authoritative acceptance, the persistent circle moves to the accepted center and radius.

## Input Gesture

Home-base placement uses the map's existing assigned-area gesture when no survivors are selected.

```text
no selectable survivors selected
+ right-click or right-drag on valid map space
-> move/resize the player's home base
```

With one or more survivors selected, right-click behavior is unchanged:

- point click issues a move/follow order,
- right-drag issues an assigned-area patrol order.

With no survivors selected:

- A simple right click creates the minimum-sized home-base area at that world point. (based off of the minimum patrol area size)
- Holding right click and dragging creates a larger home-base circle centered on the drag start.
- Releasing commits the home-base center and radius.
- This action does not issue an order to any survivor directly. If an injured survivor has retreat behavior on and is hanging around the home base, moving the home base causes them to start moving there. This happens through the behavior, however, so no "direct" orders are required.

The click radius is:

```text
SurvivorAICommandSettings.AssignedAreaMinRadius
```

The initial spawn radius remains `3m`. If shared radius limits later make `3m` invalid, initialization should clamp it into the same valid range.

## Drag Behavior

Home-base dragging reuses assigned-area preview behavior:

- The drag starts only on valid map space.
- The preview remains a circle, never an oval.
- Zooming is suppressed while dragging.
- Radius is clamped between `AssignedAreaMinRadius` and `AssignedAreaMaxRadius`.
- Once a valid drag starts, moving the cursor outside the map or over the roster does not cancel it.
- A large mouse motion may clamp directly to maximum radius.

There is one deliberate difference from a selected-survivor assigned-area command:

- With selected survivors, a drag below minimum falls back to a point move/follow order.
- With no survivors selected, a click or sub-minimum drag creates a home base with exactly `AssignedAreaMinRadius`.

## Validation

The client sends the requested center and radius to state authority.

State authority validates:

- the RPC source is a real player,
- the center is inside commandable map bounds,
- the radius is finite and clamped to the shared assigned-area limits,
- the circle can produce a valid assigned-area patrol set.

Home-base reachability uses the same rules as `SurvivorAssignedAreaAI`:

- center/cardinal/diagonal NavMesh probes,
- reachable patrol-point generation,
- authored `PatrolWaypoint` inclusion,
- acceptance when the circle contains valid patrol terrain even if the exact center is not navigable.

If validation fails, keep the previous home base unchanged and do not raise the home-base-changed event.

## Home Base Changed Event

After state authority accepts a player-requested change, it stores the new center/radius and raises an authoritative home-base-changed event for that player.

Recommended conceptual flow:

```text
Gameplay.RequestSetHomeBase(center, radius)
-> state authority validates request
-> state authority stores accepted PlayerHomeBase
-> state authority raises HomeBaseChanged(owner, oldArea, newArea)
-> SurvivorRetreatAI reevaluates that owner's survivors once
```

The event is not a network broadcast for cosmetic UI. It is an authoritative gameplay notification used to react to the accepted state change without polling every survivor.

On the event, a survivor is immediately assigned to patrol the new home base only when:

- it belongs to that player,
- it is alive and unpossessed,
- its retreat mode is a percentage mode rather than `NoRetreat`,
- its current health percentage is strictly below the selected threshold,
- it is outside the new home-base footprint.

This includes injured survivors travelling to or patrolling the old home base. They are redirected to the newly accepted area immediately.

Healthy survivors, possessed survivors, retreat-disabled survivors, and survivors already inside the new area keep their current assignments.

Initial home-base setup at match spawn does not need to raise this gameplay event. The event applies to accepted changes after initialization.

## Relationship To Patrol Orders

The home base is defined using the same center/radius shape as an `AssignedArea` order, but placing the marker does not assign anyone to it.

When a survivor retreats:

1. Read the owner's current home-base center and radius.
2. If the survivor is already inside the home-base footprint, do nothing.
3. Otherwise create the same `AssignedArea(center, radius)` assignment that a selected-survivor patrol order would create.
4. The survivor travels into the circle and patrols it normally.

The resulting survivor assignment is an ordinary persistent assigned-area patrol:

- it uses the same entry/path behavior,
- it uses the same patrol points and waits,
- it allows the same combat and optional behaviors after entering,
- it remains until replaced by another player order.

The assignment carries a local retreat-origin marker so selecting `NoRetreat` can distinguish it from a patrol the player manually issued over the same home-base circle. `NoRetreat` replaces only the retreat-created assignment with a persistent move/guard order at the survivor's current position.

Moving the home-base marker does not issue a general team order. Its changed event redirects only injured, unpossessed, retreat-enabled survivors below their retreat threshold and outside the new area. Everyone else keeps their current assignment.

## Survivor Already Inside

Home-base containment uses the same flat XZ footprint test as ordinary assigned-area patrol.

If a survivor with retreat enabled takes damage while already inside:

- do not replace its current assignment,
- do not restart patrol generation,
- do not force it toward the center,
- let it continue using its selected combat behavior.

The home base is the destination of retreat, not a combat-safe zone. It does not grant healing, immunity, improved cover, or any automatic combat modifier.

## Selection Interaction

The command is based on the current valid map selection:

- If at least one alive selectable owned survivor is selected, right-click commands those survivors.
- If the selection is empty, right-click edits the home base.
- Dead, hidden, off-map, enemy, and possessed survivors do not count as selectable command targets.

Clearing selection therefore deliberately exposes the home-base gesture.

Starting a selection box or interacting with the roster remains left-button behavior and does not move the home base.

## Networking

Home-base placement is network-facing player state.

Recommended request:

```csharp
Gameplay.RequestSetHomeBase(Vector3 center, float radius);
```

State authority validates and stores the accepted value, then raises the authoritative home-base-changed event.

The local owner needs the accepted home-base state for the persistent map marker. State authority and survivor AI need it for retreat decisions. Store only center and radius; patrol candidates, preview geometry, and drag state remain local or transient.

Do not network:

- preview circle vertices,
- drag start and current pointer position,
- generated patrol points as home-base state,
- map UI color or visibility,
- per-survivor copies of center/radius.

The exact storage location can be a compact per-player structure associated with `Gameplay` or another authoritative team-state owner. Avoid adding a separate NetworkObject per home base unless later physical base gameplay requires one.

## Future Expansion

The same area may later support:

- home-base construction,
- healing or resupply rules,
- defensive spawning,
- team rally behavior,
- extraction or evacuation,
- base-specific map icons and UI.

Those features should consume the shared home-base center/radius. They must preserve the rule that moving the marker is not a general team order; only the explicit retreat-threshold response may redirect survivors automatically.

## Acceptance Criteria

- Every player starts with a home base at their assigned spawn.
- The initial radius is `3m`.
- The local player's home base is always shown as a light-green circle on the full map.
- Enemy home bases are not shown.
- Right-click with no survivor selection places a minimum-radius home base.
- Right-drag with no survivor selection creates a larger home base using patrol-area radius limits.
- Dragging outside the map after a valid start does not cancel placement.
- Right-click and right-drag with survivors selected retain their existing order behavior.
- State authority validates the center, radius, ownership, and patrol-area reachability.
- Invalid placement leaves the old home base unchanged.
- Moving the home base does not issue a general team order.
- A successful home-base change immediately redirects qualifying injured retreat-enabled survivors to the new area.
- The redirect requires a strict health-ratio comparison against the survivor's selected `25%`, `50%`, or `75%` threshold.
- Possessed survivors and survivors already inside the new area are unchanged.
- Retreat creates a normal assigned-area patrol using the current home-base center and radius.
- Survivors already inside the home base do not retreat and continue their selected combat behavior.
- Home-base state is stored once per player.
- Invalid home bases (whole area unreachable, no valid patrol points found) are discarded
- After a player loses all their survivors, their home base circle marker is removed (to avoid it cluttering spectating mode)
- If possible without too much hassle, if the game continues after a player loses and goes to spectate and starts seeing the whole map, also show other player home bases to the lost spectating player.
