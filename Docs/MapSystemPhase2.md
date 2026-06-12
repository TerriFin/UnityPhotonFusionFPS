# Map System Phase 2 - Icons, Selection, And Orders

## Goal

Build on `Docs/MapSystemPhase1.md` by adding tactical information and direct RTS-style survivor commands.

This phase adds:

1. Own survivor icons.
2. Enemy survivor icons based on direct sensor awareness.
3. Pickup icons based on local survivor vision.
4. Local selection of own survivors.
5. Right-click move orders.
6. Right-click follow orders.
7. Right-click drag assigned-area orders.

The phase should remain fully playable without the future character-state controls described in `Docs/MapSystemPhase3.md`.

## Dependencies

Requires phase 1:

- full-screen map UI,
- map camera,
- map bounds,
- cursor unlock/input suppression,
- `WorldToMapUI(...)`,
- `TryMapUIToWorld(...)`.

Relevant existing systems:

```text
Docs/TeamCharacterSystem.md
Docs/CharacterAICommands.md
Docs/CharacterSensorySystem.md
Docs/TeamColorSystem.md
Docs/PathfindingSystem.md
```

## Expected Files

Implemented new files:

```text
Assets/Scripts/UI/Map/GameMapIcon.cs
Assets/Scripts/UI/Map/GameMapIconController.cs
Assets/Scripts/UI/Map/GameMapSelectionController.cs
Assets/Scripts/UI/Map/GameMapAwarenessTracker.cs
```

Expected touched files:

```text
Assets/Scripts/UI/Map/GameMapView.cs
Assets/Scripts/Gameplay/Gameplay.cs
Assets/Scripts/Survivor/AI/SurvivorAICommandService.cs
Assets/Scripts/AI/Sensory/CharacterSensor.cs
```

Do not add character-state UI in this phase.

## UI Structure

Phase 2 extends the phase 1 hierarchy:

```text
GameUI
  MapView
    RawImage (RenderTexture)
    IconRoot
    SelectionBox
    AssignedAreaCircle
```

The map camera remains the static visual background. Icons and selection are UI overlays.

The current implementation can create `IconRoot`, `SelectionBox`, and `AssignedAreaCircle` automatically at runtime if they are not assigned in the editor. The generated icons are simple colored UI squares, and the assigned-area preview is built from ordinary UI `Image` children with a generated circle sprite, so no icon or circle prefab is required for the first test pass.

## Own Survivor Icons

Show an icon for every alive survivor owned by the local player.

Icon data:

```text
World position = survivor transform position
Rotation = survivor yaw, optional
Color = owning team's team color
Visibility = alive and owned by local player
```

The active possessed survivor should use a special icon or visual treatment to help orient the player. The map already centers on the active survivor when opened in phase 1, but the icon makes their position clear while panning/zooming.

Team colors should reuse the team color index/material/palette system documented in `Docs/TeamColorSystem.md`.

Icons are local UI objects. They should not be networked. Each client builds its own icon set from locally replicated survivor objects and `Gameplay` team data.

The icon and awareness layers must ignore survivor objects until their Fusion `NetworkObject` is valid/spawned. This prevents map UI from reading `[Networked]` properties such as `OwnerRef` or `CharacterIndex` during the short join window before `Spawned()` has completed locally.

A survivor icon's kind (own / enemy / neutral) and colour are **recomputed every tick from the live survivor**, not cached at icon creation. A neutral survivor can be recruited mid-session — its networked `OwnerRef` flips from "no real player" to the recruiter — and the icon must follow it from the neutral appearance to the new owner's team appearance. (This is most visible to a defeated spectator using reveal-everything, where another team's recruits are drawn as enemy icons; before the per-tick refresh they stayed frozen as white neutral icons.)

## Enemy Survivor Icons

Enemy icons are visible only when one of the local player's survivors currently senses that enemy as a direct enemy.

Use `CharacterSensor` direct enemy awareness, not noise-only awareness:

```text
Show enemy icon when:
  local-team survivor has direct vision/proximity enemy memory

Do not show enemy icon when:
  survivor only heard gunfire
  survivor only heard bullet impact
  survivor turned toward approximate shooter location but did not see anyone
```

When the enemy is no longer directly sensed, keep the icon temporarily:

```csharp
public float EnemyIconForgetDelay = 2f;
```

Rules:

- Seeing the same enemy again refreshes the icon position, rotation, and delay. The "newer sighting" check uses the sensor's per-enemy `Tick` value, so anything the awareness tracker stores about an enemy stays frozen between sense ticks even if the live target keeps moving and rotating in the world.
- The icon position should be the enemy's last directly sensed position.
- The icon rotation should be the enemy's facing yaw captured at that same last-sighting tick. Reading the live `Survivor.transform.eulerAngles` would let the marker keep spinning after the target ducked out of view.
- If the enemy dies, remove the icon immediately (no fade).
- Otherwise, while the icon is in memory the awareness tracker stores `LastSenseTime` (real time of last sighting). Every render frame the icon controller derives opacity from `1 - (now - LastSenseTime) / ForgetDelay`. The marker stays fully opaque while at least one local sensor still reports the enemy via its known-list, then fades linearly to zero over `ForgetDelay` once nothing perceives it. The tracker removes the entry when opacity reaches zero.

This UI memory is client-local. It does not need to be `[Networked]`.

### Reveal everything (no fog of war)

`GameMapAwarenessTracker.Tick(gameplay, runner, revealAll)` takes a `revealAll` flag. When set, it seeds every survivor, zombie, and pickup into memory at its live position each tick (full opacity, never fades) before the normal sensor pass. Reveal is a debug tool and is **off by default for everyone in normal and raid play**. Each map view owns whether it reveals:

- `GameMapView.RevealMode` — `Auto` / `ForceOff` / `ForceOn` (default `Auto`). `Auto` reveals the full map **only for a defeated spectator** (see `Docs/SpectateMode.md`) and otherwise stays fog-of-war. `ForceOn`/`ForceOff` is a debug override that ignores the spectator state. (The toggle was moved here from `GameMapCameraController`, where it did not belong.)
- `GameMinimapView.RevealEverything` — a debug bool, **default off**. Defeated spectators have no minimap at all (`GameMinimapView.Suppressed`), so this is purely a debugging aid for the minimap.

Because the reveal writes into the awareness tracker's shared memory, a revealing minimap must use its **own** tracker rather than adopting the full map's via `TryAdoptSharedTracker` — otherwise the reveal would leak onto the (fog-of-war) full map, and toggling the full map's reveal would leak onto the minimap for the forget-delay window. `GameMinimapView` therefore skips tracker adoption while its `RevealEverything` is on.

`CharacterSensor` now exposes direct known enemies through `GetDirectKnownEnemies(...)`. Direct vision/proximity scans are allowed to run on the owning client for map UI purposes, while noise and bullet-impact recording still use the existing state-authority sensing path.

Potential implementation:

```text
GameMapAwarenessTracker
  scans local team's CharacterSensor components
  reads direct known enemies
  updates icon memory dictionary by NetworkId
```

The tracker should not make AI decisions. It only translates existing sensor information into local map UI.

## Pickup Icons

Pickup icons are a local testing/awareness overlay. They appear when one of the local player's survivors has seen the pickup through `CharacterSensor` pickup vision.

Rules:

- Pickup icons are circles.
- Weapon pickups use yellow.
- Health pickups use green.
- Inactive pickups can still be shown if seen, but use a translucent version of their normal color.
- Pickup icons are hidden when their world position is outside the current map camera viewport.
- Pickup icons are not selectable and do not receive map orders.
- The map can remember pickup icons briefly through `GameMapAwarenessTracker.PickupIconForgetDelay`.

This does not change AI pickup collection. AI collection still checks whether the pickup is active before moving toward it.

## Zombie Icons

Zombie icons are a local awareness overlay. They appear when one of the local player's survivors directly senses a zombie through `CharacterSensor`.

Rules:

- Zombie icons are circles, matching pickup icon size.
- Zombies use a small dark-green circle.
- Zombie icons are hidden when their last known position is outside the current map camera viewport.
- Zombie icons are not selectable and do not receive map orders.
- The map can remember zombie icons briefly through `GameMapAwarenessTracker.ZombieIconForgetDelay`.
- If the zombie dies or despawns, remove the icon immediately.

## Future Non-Survivor Icons

This phase now includes spotted zombie icons. Neutral survivors and other non-survivor map markers are still future work.

Later icon types can be added:

- neutral recruitable survivors,
- special objectives,
- evacuation/extraction zones,
- loud world events.

Neutral survivors and zombies should get separate icon definitions rather than being forced into survivor icon logic.

## Selection

Supported selection:

```text
Click own survivor icon -> select that survivor
Drag selection box -> select every own survivor icon inside box
Double-click own survivor icon -> select all own survivor icons currently visible on map
Click empty map area -> clear selection, unless modifier key behavior is added later
```

Selection is local UI state:

```text
SelectedSurvivors: local list of Survivor references or NetworkIds
```

Selection should include only:

- own team,
- alive survivors,
- currently rendered own survivor icons.

It should not select enemies, zombies, neutral survivors, dead survivors, or hidden icons.

Icons are hidden when their world position is outside the current map camera viewport. Hidden/off-map own survivors are removed from the local selection and are ignored by click, drag-select, double-click-select-all, and map order mask building.

The active possessed survivor is shown with a special icon/treatment for orientation, but it is not selectable. It should not enter click selection, drag-box selection, double-click select-all, keyboard cycling, or map order masks. The player controls the possessed survivor directly.

If the active survivor dies while the map is open, the map remains open. Normal gameplay ownership transfer chooses the next active survivor, and the map selection layer removes that new active survivor from selection if needed.

While the map is open, `Shift` and `Ctrl` are local selection shortcuts:

- `Shift`: select only the next selectable survivor by `CharacterIndex`.
- `Ctrl`: select only the previous selectable survivor by `CharacterIndex`.
- If nothing is selected, cycling starts from the current active survivor.
- If one survivor is selected, cycling starts from that selected survivor and replaces the selection.
- If multiple survivors are selected, `Shift`/`Ctrl` do nothing.
- Cycling skips dead, hidden/off-map, enemy, and active possessed survivors.

While the map is open with exactly one selectable survivor selected, `Space` possesses that survivor and closes the map:

- The request goes through `Gameplay.RequestSwitchActiveCharacter(targetCharacterIndex)`, which validates ownership, alive state, and the same switch cooldown the in-game `Shift`/`Ctrl` cycling uses.
- The map closes immediately after the request is dispatched so the player can drive the newly possessed survivor without a second key press.
- If zero or multiple survivors are selected, `Space` does nothing on the map and is consumed only when the possess actually fires.

While the map is open, `I` and `O` apply non-combat AI setting commands to the currently selected survivors:

- `I`: disable all current optional non-combat/combat-activation settings for selected inactive survivors.
- `O`: enable all current optional non-combat/combat-activation settings for selected inactive survivors.
- These toggles do not replace current move/follow/hold assignments.
- The active possessed survivor is still ignored by the order mask.

## Map Orders

Right-click orders apply to explicitly selected owned survivors. They do not use the nearby `CommandRadius` rule.

### Move Order

Right-click an empty world point:

```text
selected own inactive survivors -> SurvivorNonCombatAI move assignment toward clicked world point
```

The active possessed survivor is ignored even if selected.

The server/state authority must apply the actual AI command. The local map UI should send a command request through an RPC or through an equivalent state-authority request path.

Recommended first implementation:

```text
Map UI sends selected survivor ids + world point to Gameplay on state authority.
Gameplay validates ownership/alive state.
SurvivorAICommandService assigns a move assignment.
```

Do not trust the client selection blindly. Server validation must check:

- sender owns each selected survivor,
- survivor is alive,
- survivor is not the active possessed survivor,
- destination is within the generated map / commandable area,
- optionally destination is near NavMesh.

When many survivors move to one clicked point, later add small offsets around the target. For the first version, using the same target is acceptable if separation/friendly phasing keeps them moving.

### Follow Order

Right-click an own survivor icon while other own survivors are selected:

```text
selected own inactive survivors -> SurvivorNonCombatAI follow assignment targeting clicked own survivor
```

Validation:

- target survivor belongs to the same owner,
- target survivor is alive,
- selected survivors are alive and owned by sender,
- selected survivors are not the active possessed survivor,
- selected survivors do not include the target, or target is ignored from the selected list.

This is different from the current F-key nearby follow command. Map selection commands selected survivors, not every survivor inside `CommandRadius`.

### Assigned-Area Order

Hold right click on the map and drag a circle:

```text
selected own inactive survivors -> SurvivorNonCombatAI assigned-area patrol centered on drag start
```

Rules:

- The preview is always a circle, never an oval.
- Zooming is suppressed while the circle is being dragged.
- `Gameplay.AICommandSettings.AssignedAreaMinRadius` controls the smallest valid area.
- `Gameplay.AICommandSettings.AssignedAreaMaxRadius` controls the largest valid area.
- Dragging past the maximum clamps the radius.
- If the released circle is smaller than the minimum, the circle is hidden and the action falls back to the normal point/follow order under the cursor.
- State authority validates the radius again before assigning the order.
- The dragged center does not need to be on NavMesh. State authority resolves one shared reachable patrol-point set inside the circle for the selected group. If center/cardinal/diagonal probes find no reachable terrain, the selected survivors keep their previous assignments. If reachable terrain exists, each survivor receives the same point set and picks its own entry/patrol target.

## Relation To Existing Command System

Current hotkeys:

- F = nearby follow,
- I = disable optional non-combat/combat-activation settings,
- O = enable optional non-combat/combat-activation settings,
- M = nearby move-to-look-point,
- G = auto-shoot toggle.

Map orders should reuse AI command classes, but not the nearby-radius selection rule.

Existing command service behavior:

```text
hotkey order -> apply to nearby uncontrolled same-team survivors inside CommandRadius
```

Map behavior:

```text
map order -> apply to explicitly selected owned survivors
```

Recommended extension:

```csharp
SurvivorAICommandService.ApplyCommandToSurvivors(
    PlayerRef owner,
    IReadOnlyList<int> characterIndices,
    SurvivorAICommand command)
```

This keeps command assignment centralized while allowing map selection to bypass `CommandRadius`.

Implemented command path:

```text
GameMapSelectionController
-> Gameplay.RequestMapMoveOrder / RequestMapAssignedAreaOrder / RequestMapFollowOrder / RequestMapNonCombatSettings
-> RPC to state authority
-> SurvivorAICommandService.ApplySelectedTeamCommand / ApplySelectedTeamFollow / ApplySelectedTeamNonCombatSettings
```

The selected survivor list is sent as a 64-bit character-index mask. State authority validates ownership through the RPC source, checks alive state, and ignores the active possessed survivor.

## Networking Model

Local only:

- visible icon UI objects,
- visible pickup icon UI objects,
- selected survivor list,
- enemy icon fade timers,
- pickup icon fade timers,
- selection box state,
- command preview.
- assigned-area preview circle.

State authority:

- validating map orders,
- assigning survivor AI commands,
- actual movement/shooting/sensing.

Networked data already available:

- survivor owner/team,
- survivor alive/dead state,
- survivor transforms,
- team color index/material mapping,
- generated world objects exist in the scene on all peers.

Avoid:

- networking map UI state,
- networking every icon,
- letting client directly set AI on survivors,
- adding map-only NetworkObjects.

## Acceptance Criteria

- Own alive survivors show icons on the map.
- The active possessed survivor has a distinct icon/treatment.
- Enemy survivor icons appear only from direct sensor contact.
- Noise-only or bullet-impact-only memories do not create enemy icons.
- Enemy icons refresh while sensed and disappear after `EnemyIconForgetDelay`.
- Pickup icons appear when local survivors see pickups.
- Health pickups are green circles, weapon pickups are yellow circles.
- Inactive seen pickups are shown as translucent circles.
- Zombie icons appear as small dark-green circles when local survivors see zombies.
- Player can click, drag-select, and double-click-select own rendered survivor icons.
- Right-clicking an empty map point sends selected inactive survivors to that point.
- Holding and dragging right click creates an assigned-area order when the radius is large enough.
- Right-clicking an own survivor icon makes selected inactive survivors follow that survivor.
- The active possessed survivor does not receive AI orders from the map.
- State authority validates ownership and alive state before assigning commands.

## Editor Setup

Phase 2 can work without extra editor setup if `GameMapView` is assigned:

1. Open the object that has `GameMapView`.
2. Optionally add `GameMapIconController`, `GameMapSelectionController`, and `GameMapAwarenessTracker` to that same object.
3. If these components are not assigned, `GameMapView` creates/adds them automatically at runtime.
4. Optionally create an `IconRoot` child under the map `RawImage` and assign it to `GameMapIconController.IconRoot`.
5. Optionally create a `SelectionBox` child under the map `RawImage` and assign it to `GameMapSelectionController.SelectionBox`.
6. Optionally create an empty `AssignedAreaCircle` child under the map `RawImage` and assign it to `GameMapSelectionController.AssignedAreaCircle`. This is not required; if missing, the controller creates it.
7. Configure assigned-area min/max radius on `Gameplay.AICommandSettings`.
8. Test with no custom icon prefab first; the system creates simple colored square icons and a simple assigned-area preview circle.

## Next Phase

After icons, selection, and orders work, implement `Docs/MapSystemPhase3.md` for selected-survivor state controls.
