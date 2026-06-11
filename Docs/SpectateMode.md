# Spectate Mode (defeated players)

## Goal

When a player loses every survivor but the match is still running, they enter **spectate mode** — a view-only
version of the raid host's inspect mode. They watch the ongoing fight through the shared orbit camera and can
follow any surviving team, but they cannot influence the match.

A defeated raid host drops into this same mode (they lose command, gain free-team spectating).

Spectate and raid share almost all of their behavior; this doc covers the spectate specifics and what is shared.
See `Docs/RaidMode.md` for the raid-host side.

## Activation

Spectate mode is a **local** state, decided per peer from the player's replicated `PlayerData`:

```text
DefeatedSpectator = data.IsConnected && data.CharacterCount > 0 && data.IsAlive == false
```

i.e. the player had a team and now has no living survivor, while the match has not ended. It is determined in
`SpectatorController.DetermineMode` alongside the raid-host mode, so a defeated raid host falls out of
`RaidCommander` and into `DefeatedSpectator` automatically.

## Behavior

A defeated spectator:

- watches a survivor through the shared third-person **orbit camera**: WASD orbits, the camera glides between
  survivors, and a `CinemachineCollider` keeps it out of walls (identical to the raid host);
- has **no minimap** (`GameMinimapView.Suppressed`);
- sees the **full map reveal every entity** — all teams' survivors (tinted per team color), zombies, and pickups.
  This is the only automatic reveal in normal play (`GameMapView.RevealMode == Auto` → on for a defeated
  spectator). A `ForceOn`/`ForceOff` set in the inspector still overrides it;
- **cannot issue any orders.** Move/area/follow/AI-toggle inputs are skipped client-side and rejected
  server-side (`Gameplay.IsValidMapOrderSource` already requires `IsAlive`);
- **Ctrl/Shift** (in the 3D view) cycle the inspected survivor **within the team currently being watched** — the
  team of the current inspect target;
- switches the watched team by **opening the map, clicking any team's survivor, and pressing Space** (no
  possession, the map stays open).

When the inspected survivor dies, the camera advances to the next living survivor of the same team; if that team
is wiped, it falls back to any living player survivor.

## Shared architecture

Raid and spectate are two modes of one controller plus one camera, so they are "identical in function, one is
just in control of survivors":

### `SpectatorCamera` (`Assets/Scripts/UI/SpectatorCamera.cs`)

The shared, mode-agnostic orbit camera. It lazily creates a runtime `CinemachineVirtualCamera` (world-space
`CinemachineTransposer` + `CinemachineComposer` + `CinemachineCollider` set to `PullCameraForward`). `SetTarget`
points it at a survivor (glides to a new target, snaps only on first activation); `HandleOrbitInput` applies WASD
yaw/pitch; `Disable` turns it off. Used identically by both modes.

All framing/orbit/collision values are inspector-tunable on this component: `Distance`, `DefaultPitch`,
`MinPitch`/`MaxPitch`, `YawSpeed`/`PitchSpeed`, `LookAtOffset`, `FieldOfView`, `Damping`, `CameraPriority`,
`CollisionMask`, `CameraRadius`, `MinDistanceFromTarget`. `SpectatorController` declares
`[RequireComponent(typeof(SpectatorCamera))]`, so adding the controller to the `GameUI` object also adds this
component and surfaces those controls in the inspector.

### `SpectatorController` (`Assets/Scripts/UI/SpectatorController.cs`)

Local per-peer controller. Each frame it computes `Mode` (`None` / `RaidCommander` / `DefeatedSpectator`) and, if
active, drives the `SpectatorCamera` and the inspect target. The inspect logic is shared and only parameterized
by `IsInspectable`:

- `RaidCommander` — inspectable = alive survivor owned by the local player (own team).
- `DefeatedSpectator` — inspectable = any alive player-owned survivor (any team, never neutrals).

Ctrl/Shift cycle within the inspect target's owner team (own team for raid, the watched team for spectate);
`SetInspectTarget` (from map-Space) jumps to a clicked survivor; on target loss it stays in the watched team when
possible, else (spectator only) falls back to any inspectable survivor.

Public surface read by other systems: `Mode`, `IsActive`, `IsRaidCommander`, `CanInspectAnyTeam`,
`CanIssueCommands`, `InspectTarget`, `SetInspectTarget`.

### Map integration

- `GameMapSelectionController` reads `mapView.Spectator`:
  - `IsActive` → map-Space routes to `SetInspectTarget` and leaves the map open (no possession).
  - `CanInspectAnyTeam` → selection accepts any player-owned survivor whose icon is visible (own **or** revealed
    enemy icon, via `GameMapIconController.FindSelectableIconAt` / `IsSurvivorVisible`), instead of own team only.
  - `CanIssueCommands == false` → right-click orders and AI-toggle keys are skipped.
- `GameMapIconController.InspectHighlightSurvivor` (set by `GameUI`) enlarges the inspected survivor; it is now
  applied to enemy/other-team icons too, so a spectated other-team survivor is enlarged like an own one.
- `GameUI` ticks the `SpectatorController`, pushes the inspect highlight to the map/minimap icon controllers, sets
  `GameMinimapView.Suppressed` for defeated spectators, and keeps the minimap follow target on the inspected
  survivor only in `RaidCommander` mode.

## Differences from raid mode

| | Raid host (`RaidCommander`) | Defeated spectator (`DefeatedSpectator`) |
|---|---|---|
| Inspect/select pool | own team | any player team |
| Issue commands | yes | no |
| Minimap | yes (follows inspect) | none |
| Full-map reveal (`Auto`) | off (fog of war) | on (everything) |
| Map auto-opens at start | yes (closeable) | no (starts in 3D view) |
| Orbit camera, Ctrl/Shift, map-Space switch | yes | yes |
