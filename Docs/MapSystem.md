# Map System

The tactical map is split into three implementation phases so each part can ship as a working feature before the next layer is added.

## Documents

1. `Docs/MapSystemPhase1.md`  
   Opens a full-screen map, renders the generated city from above, supports zoom/pan, unlocks the cursor, suppresses normal gameplay input, hides characters and loot, and centers on the active possessed survivor when opened.

2. `Docs/MapSystemPhase2.md`  
   Adds survivor/enemy icons, local selection, and right-click movement/follow orders for selected survivors.

3. `Docs/MapSystemPhase3.md`  
   Future phase for selected-survivor state controls such as fire mode, combat AI, and non-combat AI.

## Architecture Summary

The recommended foundation is a real top-down orthographic camera rendered into a UI `RenderTexture`.

```text
Top-down camera = visual map background
UI overlay = survivor/enemy/zombie/neutral icons, selection box, command feedback
Map camera raycasts = clicked world positions
```

This lets the first phase show the actual generated roads, buildings, vehicles, and static props immediately. Later phases add UI overlays and command logic without replacing the map background.

