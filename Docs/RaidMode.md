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

## Host Spawn

In raid mode, the host team does not use the ordinary side-of-map `SpawnPoint` selection. Instead, `Gameplay`
asks `NeutralSurvivorOrchestrator` for the valid `NeutralSurvivorSpawnPoint` closest to the generated map
center and spawns the host team there. This lets the RTS commander start near the center and spread outward in
all directions, which is useful for mirrored maps where normal player spawns live on the map edges.

The center marker is reserved for the host team: it is skipped by neutral-survivor spawning, and any already
spawned neutral survivors from that exact marker are despawned so the host does not start by automatically
recruiting extra survivors. The marker is still not registered as an active player spawn for broad
neutral-survivor pruning, so nearby different neutral markers remain valid. Zombies are still cleared around the
raid host's actual spawn position. If no valid neutral survivor marker exists, the host falls back to the normal
spread `SpawnPoint` logic.

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

### `SpectatorController` + `SpectatorCamera`

The host's inspect UX is **one mode of a shared spectator system** that also powers defeated-player spectating.
Raid is the `RaidCommander` mode of `SpectatorController`; the orbit camera is the shared `SpectatorCamera`.
Both are documented in `Docs/SpectateMode.md`. In `RaidCommander` mode specifically:

- The mode is detected as `Gameplay.RaidMode && Gameplay.HasStateAuthority && localData.IsAlive` (uniquely true
  on the host peer while it still has survivors). This is the local-view counterpart to
  `RaidModeRules.IsRaidControlledPlayer`, which is the state-authority counterpart — both identify the same host.
- The inspect target is the lowest-index alive **own** survivor (auto-advances on death); Ctrl/Shift (map closed)
  cycle within the own team; map-Space sets the inspect target; WASD orbits the `SpectatorCamera`.
- The host's map **auto-opens once** at the start (their RTS tool) but stays closeable — unlike a lock.
- When the host loses its last survivor mid-match, `Mode` falls through to `DefeatedSpectator` and they spectate
  all teams without command (see `Docs/SpectateMode.md`).

### Map integration

- `GameMapView.Spectator` (a `SpectatorController`) is set by `GameUI`. `GameMapSelectionController` routes
  map-Space to `Spectator.SetInspectTarget` whenever `Spectator.IsActive` (raid host or defeated spectator),
  leaving the map open, instead of the normal possess + `CloseMap()`.
- `GameMapIconController.InspectHighlightSurvivor` (set by `GameUI`) enlarges the inspected survivor via the
  existing `GameMapIcon.SetActiveSurvivor` 1.35× highlight, on both the full map and minimap, and on own or
  other-team icons.
- `GameMinimapView.OverrideFollowTarget` (set by `GameUI` in `RaidCommander` mode) makes the host's minimap
  follow the inspected survivor, since the host has no possessed `PlayerObject` to follow. Defeated spectators
  have no minimap (`GameMinimapView.Suppressed`).
- `GameMapView.GetInitialCenterPosition` centers the opened map on the local team's first alive survivor when
  there is no possessed `PlayerObject`, so the host's map opens on their team rather than world origin.

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
