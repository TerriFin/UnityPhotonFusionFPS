# Raid Mode

## Goal

Raid mode turns a match into an asymmetric **FPS vs RTS** format. The host plays as a pure RTS commander
and every other player plays as a normal FPS survivor controller.

- The host keeps a full team but **never possesses (directly controls) a survivor**. They command their
  team exclusively through the tactical map and are permanently locked into it.
- Clients spawn with a smaller team (`RaidModeClientStartingSurvivors`) and play normally — possessing,
  cycling, and fighting in first person.

Without raid mode, the host is just another FPS player who happens to start with more survivors. Raid mode
enforces the commander role.

## Host Behavior

While a raid match is running, the host:

- has **no active character** — `PlayerData.ActiveCharacterIndex` is set to `-1` on spawn, so all of the
  host's survivors run AI and none can be controlled in first person;
- opens/closes the tactical map normally (Alt), like any other player — the host is no longer locked into it;
- **inspects** a survivor instead of possessing one. A third-person spectator camera follows the inspected
  survivor (which stays fully AI-controlled). Because the host possesses nobody, no survivor camera is live, so
  without this the main world camera would idle at the origin and clip through the city.
  - In the 3D view (map closed): **WASD orbits** the camera around the inspected survivor, and **Ctrl/Shift**
    switch to the previous/next living survivor.
  - On the map: select one survivor and press **Space** to switch the inspect target. Unlike a normal player's
    Space (which possesses and closes the map), the host's map **stays open**.
  - The inspected survivor is drawn **larger** on the map and minimap, exactly like the active character is for
    normal players.
  - Switching inspect target **glides** the camera between survivors (it only snaps the first time it turns on,
    to avoid swooping in from the origin). When the inspected survivor dies, the camera advances to the next
    living survivor automatically;
- opens the pause menu **on top of** the map with Escape; closing the menu reveals the map again;
- commands survivors with the normal map orders (right-click move, assigned area, follow, fire/AI toggles).

When the host's whole team dies, or when every other team is eliminated, the match finishes: the map closes
and the standard win/lose screen appears. This reuses the existing team-elimination win condition — the host
is just another team — so no separate raid end-game logic exists.

## Why `ActiveCharacterIndex = -1`

`-1` is the established "no active character" sentinel used throughout `Gameplay`:

- `Survivor.IsActiveCharacter()` compares `ActiveCharacterIndex == CharacterIndex`, so `-1` matches no
  survivor — every survivor falls through to the AI path.
- `Gameplay.UpdatePlayerObject` already no-ops while `ActiveCharacterIndex < 0`, so the host never gets a
  Fusion `PlayerObject`, and therefore no first-person camera, HUD, or input authority routing to a survivor.
- `SurvivorInput.ShouldProvideInput()` returns `false` for the host's survivors (no PlayerObject + no
  matching active index), so they never consume player input.

Because the host has no possessed survivor, there is nothing extra to suppress: input is already gated by the
possession model and by `GameMapView.IsAnyMapOpen` (which blocks FPS input whenever a map is open).

## Classes

### `RaidModeRules` (`Assets/Scripts/Gameplay/RaidModeRules.cs`)

Static helper holding the raid rule. `IsRaidControlledPlayer(Gameplay, PlayerRef)` returns true when the
given player is the raid host. (Named `RaidModeRules` rather than `RaidMode` because `Gameplay` already has a
`RaidMode` field, which would shadow a same-named type inside that class.)

State-authority-only: it relies on `Runner.LocalPlayer` being the host, which is the same assumption the
existing raid spawn logic (`Gameplay.GetStartingCharacterCount`) already makes. It is called only from
state-authority gameplay code:

- `Gameplay.SpawnTeam` — picks `-1` vs `0` for the new team's active character.
- `Gameplay.SwitchActiveCharacter` / `Gameplay.SwitchToCharacter` — early-return for the host, blocking
  every possession path (Shift/Ctrl cycling and the map's Space / double-click possess, which route through
  `RequestSwitchActiveCharacter`).

### `RaidModeController` (`Assets/Scripts/UI/RaidModeController.cs`)

Local, host-peer `MonoBehaviour` that owns the inspect UX. `GameUI` auto-adds it and ticks it each frame
before `GameMapView.Tick`.

- Detects the local raid host as `Gameplay.RaidMode && Gameplay.HasStateAuthority` (uniquely true on the host
  peer; clients have no state authority). This is the local-view counterpart to `RaidModeRules.IsRaidControlledPlayer`,
  which is the state-authority counterpart — both identify the same host.
- Tracks the **inspect target** (`InspectTarget`): the lowest-index alive owned survivor by default
  (auto-advances when the current one dies). `SetInspectTarget(Survivor)` is called by the map's Space handler.
  While the map is closed it reads **Ctrl/Shift** to cycle to the previous/next living survivor.
- Drives the host's third-person **spectator camera**. It lazily creates a runtime `CinemachineVirtualCamera`
  (world-space `CinemachineTransposer` body + `CinemachineComposer` aim) pointed at the inspect target. While the
  map is closed, **WASD** orbits the camera (A/D = yaw, W/S = pitch) by recomputing the transposer's follow
  offset each frame. A `CinemachineCollider` extension (PullCameraForward, colliding against Default +
  MapNonVisible) pulls the camera in off buildings instead of clipping through them. Target switches glide via
  transposer/composer damping; the camera only snaps (`PreviousStateIsValid = false`) the first time it turns
  on, to avoid swooping in from the origin. The vcam is
  disabled (and its priority dropped) whenever the local peer is not the raid host or the match is not running,
  so non-host peers never create it and the Cinemachine brain ignores it. Orbit distance/pitch limits/speeds,
  look-at offset, FOV, damping, and priority are inspector-tunable.
- Exposes `IsLocalRaidHost` and `InspectTarget` for other systems (`GameUI` uses them to draw the inspected
  survivor enlarged on the map/minimap; `GameMapSelectionController` uses them to route map-Space to inspect).

Its only serialized fields are the spectator-camera tunables; the per-frame inputs are passed into `Tick`, so
it needs no scene wiring.

### Map integration

- `GameMapView.RaidController` is set by `GameUI` so `GameMapSelectionController.HandleKeyboardPossess` can route
  the host's map-Space to `RaidModeController.SetInspectTarget` (switch inspect target, leave the map open)
  instead of the normal possess + `CloseMap()`.
- `GameMapIconController.InspectHighlightSurvivor` (set by `GameUI` for the host) enlarges the inspected survivor
  via the existing `GameMapIcon.SetActiveSurvivor` 1.35× highlight, instead of the networked active character.
  The same control runs for both the full map and the minimap.
- `GameMinimapView.OverrideFollowTarget` (set by `GameUI` for the host) makes the minimap follow the inspected
  survivor. The minimap normally follows the local `PlayerObject`, which the host does not have.
- `GameMapView.GetInitialCenterPosition` centers the opened map on the local team's first alive survivor when
  there is no possessed `PlayerObject`, so the host's map opens on their team rather than world origin.
- `GameMapView.LockedOpen` remains as a generic "hold the map open" capability but raid mode no longer engages
  it — the host opens/closes the map normally.

## Hosting Settings

Raid mode is configured from the host menu and flows through the normal settings pipeline
(`MatchHostingSettings` → `MatchRuntimeSettings` → `Gameplay`). See `Docs/MatchHostingSettings.md`.

```text
RaidMode                       enable raid mode for this match
RaidModeClientStartingSurvivors starting survivor count for every non-host player
```

The host's own team size still comes from `StartingCharacterCount` (the full RTS team). Both counts are
clamped to the 128-character team mask capacity.

## What Raid Mode Does NOT Change

- The win/lose condition and `UIGameOverView` (already correct without a possessed survivor).
- Client behavior — clients are unchanged FPS players.
- Non-raid matches — the host possesses and plays in first person exactly as before.
- No new RTS HUD is added; the host's FPS-only `PlayerView` simply stays hidden (no PlayerObject).
