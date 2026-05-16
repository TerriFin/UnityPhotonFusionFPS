# Map System Phase 1 - Visual Map

## Goal

Add the first playable version of the tactical map.

When the player presses the map key:

1. A full-screen map overlay opens.
2. The generated city is shown from above.
3. Static world geometry is visible: roads, buildings, vehicles, props, terrain, floors, and sidewalks.
4. Characters, zombies, neutral survivors, hitboxes, first-person visuals, projectiles, pickups, and UI are not rendered by the map camera.
5. Mouse cursor unlocks.
6. Normal first-person movement/look/fire/order input is suppressed.
7. The map camera centers on the active possessed survivor.
8. The player can zoom the map.
9. If zoomed in, the player can pan the map with WASD.
10. Pressing the map key again closes the map and restores normal controls.

This phase does not include character icons, enemy icons, selection, or orders. Those are covered in `Docs/MapSystemPhase2.md`.

## Key Choice

Use `Alt` as the temporary map key.

`Ctrl` is already used by the current character switching design in `Docs/TeamCharacterSystem.md`, so the first map pass should avoid that conflict.

The long-term input should be a named map action in the input system rather than hardcoded keyboard checks.

## Recommended Architecture

Use a real top-down orthographic camera rendered into a UI `RenderTexture`.

Reasons:

- It immediately shows the actual generated city.
- It works with procedural generation because it looks at instantiated world objects.
- It does not require maintaining a separate symbolic map renderer.
- Characters and loot can be hidden by culling mask.
- Later UI icons and selection boxes can be layered over the rendered map.

Alternative:

Build a symbolic 2D map from `RoadGridGenerator` and `BuildingPlacementGenerator` data. This is not recommended for the first pass because it would not automatically show authored props, vehicles, debris, or special building detail.

## Expected Files

Implemented files:

```text
Assets/Scripts/UI/Map/GameMapView.cs
Assets/Scripts/UI/Map/GameMapCameraController.cs
```

Touched files:

```text
Assets/Scripts/Survivor/SurvivorInput.cs
Assets/Scripts/UI/GameUI.cs
```

If map bounds need direct access to world generation data, this phase may also reference:

```text
Assets/Scripts/WorldGeneration/RoadGridGenerator.cs
Assets/Scripts/WorldGeneration/BuildingPlacementGenerator.cs
```

## UI Structure

Suggested hierarchy:

```text
GameUI
  MapView
    RawImage (RenderTexture)
```

`GameMapView` owns:

- open/close visibility,
- cursor lock state handoff,
- local `IsMapOpen` state,
- reference to the `GameMapCameraController`,
- UI map image/RenderTexture assignment.

Phase 1 should avoid selection, command, or icon logic.

## Map Camera Setup

Create a dedicated map camera:

```text
GameMapCamera
  Camera
  GameMapCameraController
```

Camera settings:

```text
Projection: Orthographic
Rotation: straight down, e.g. X = 90
Culling mask: static world layers only
Clear flags/background: match UI style
Output: RenderTexture shown by map UI
Enabled only while map is open
```

The map camera should include:

- generated roads,
- generated buildings,
- static vehicles,
- static props,
- terrain/floor/sidewalks.

The map camera should exclude:

- survivors,
- zombies,
- neutral survivors,
- hitboxes,
- first-person visuals,
- weapon viewmodels,
- projectiles,
- pickups,
- UI.

Pickups should not be shown in phase 1. They are gameplay objects and showing them on the static map would make loot too easy to scout. If loot visibility is desired later, add explicit loot icons with their own rules.

If the current project layers do not cleanly separate static map geometry from characters and gameplay objects, add a simple map culling layer plan before implementation. Do not solve this by disabling live character objects.

## Camera Bounds

The map camera needs world bounds for the generated arena.

Recommended:

```text
Primary bounds = RoadGridGenerator.Width / Height / TileSize / transform position
Fallback bounds = renderer bounds under generated road/building roots
```

The default full-map view should frame the whole generated city.

When opening the map, center the view on the active possessed survivor if possible. If the active survivor is missing or dead, fall back to the center of the generated map bounds.

Zoom should clamp between:

```text
MinZoom = close enough to inspect streets
MaxZoom = whole map visible
```

Panning should clamp so the camera cannot drift outside the generated city.

## Input Model

Map input is local UI input. It should not be networked.

When map is closed:

```text
SurvivorInput reads normal gameplay input.
Cursor is locked.
Map camera is disabled.
Map UI is hidden.
```

When map is open:

```text
SurvivorInput suppresses movement/look/fire/order input.
Cursor is unlocked and visible.
Map camera is enabled.
Map UI is visible.
WASD pans the map.
Mouse wheel zooms the map.
Alt closes the map.
```

Implementation should expose a simple local state:

```csharp
public bool IsMapOpen { get; }
```

`SurvivorInput.BeforeUpdate()` should check this before accumulating normal movement, look, fire, reload, weapon switch, and hotkey command input. This prevents the active survivor from walking while the player is panning the map with WASD.

The current implementation exposes this through `GameMapView.IsAnyMapOpen`. `SurvivorInput` sends empty input while the map is open, so Fusion does not keep applying stale movement or fire buttons.

## World To Map Conversion

Phase 1 should create the helpers needed later, even if only click-to-world debugging uses them at first.

Recommended helper APIs:

```csharp
public Vector2 WorldToMapUI(Vector3 worldPosition);
public bool TryMapUIToWorld(Vector2 uiPosition, out Vector3 worldPosition);
```

With the camera approach:

- `WorldToMapUI` uses `MapCamera.WorldToViewportPoint(...)` and maps viewport coordinates into the `RawImage` rect.
- `TryMapUIToWorld` maps the UI point into viewport coordinates and raycasts from the map camera down into the world.

Phase 2 right-click orders should reuse these helpers.

## Networking Model

Everything in phase 1 is local-only:

- map open/closed,
- zoom and pan,
- cursor state,
- map camera state,
- UI visibility.

Do not network map UI state.

## Editor Setup

Scene setup is intentionally left in the editor:

1. Add a hidden/full-screen map panel under `GameUI`.
2. Add a `RawImage` to that panel and assign a `RenderTexture`.
3. Add `GameMapView` to an always-active UI object, preferably `GameUI`, and assign `MapRoot` to the disabled full-screen panel. Putting `GameMapView` directly on the disabled panel is supported, but the separate always-active controller setup is easier to reason about.
4. Assign `MapRoot` to the actual full-screen map panel that should be enabled/disabled when Alt is pressed.
5. Assign `MapImage` and `CameraController` on `GameMapView`.
6. Assign the same `GameMapView` component to `GameUI.MapView`.
7. Create a top-down `GameMapCamera` with `GameMapCameraController`.
8. Assign the camera to `GameMapCameraController.MapCamera`.
9. Assign `RoadGenerator` if the scene has one. Otherwise the controller will try to find it.
10. Set the camera culling mask so it includes static map geometry and excludes survivors, hitboxes, weapon viewmodels, projectiles, pickups, and UI.
11. Assign the camera target texture to the same `RenderTexture` used by the map `RawImage`.

`GameMapView` will try to auto-wire the `RawImage`, camera target texture, and camera controller if they are missing, and it logs setup warnings if something important cannot be found. Explicit assignments are still preferred because they make the scene easier to understand.

## Acceptance Criteria

- Pressing `Alt` opens and closes the map.
- Opening the map unlocks the cursor.
- Closing the map restores normal cursor/gameplay behavior.
- The active survivor does not move, look, shoot, reload, switch weapons, or issue hotkey orders while the map is open.
- The map camera shows the generated static city from above.
- Survivors and pickups are not visible in the map camera.
- The view starts centered on the active possessed survivor.
- Mouse wheel zoom works and is clamped.
- WASD pans while the map is open and is clamped to the map bounds.

## Next Phase

After this works, implement `Docs/MapSystemPhase2.md` to add icons, selection, and map orders.
