# Match Hosting Settings

## Goal

The startup menu supports two launch paths:

1. **Quick Play** keeps the existing behavior. It attempts to join a visible random room. If none exists, it creates a host room using the scene's inspector defaults.
2. **Host Configured Match** always creates a new host room immediately, even if another joinable room exists. The host chooses the room settings before launching into skirmish mode.

Players who use Quick Play may join a configured room. Their local generated map and match rules must match the host's chosen profile.

## Runtime Flow

Configured rooms publish one compact Photon session property when the host creates the room:

```text
msp = packed scalar match values + selected preset indices
```

The room does not serialize ScriptableObjects or generated map data. Every build contains the same authored preset assets in the same catalog order. Each peer reads the selected indices from the room properties and swaps its local generator references before procedural generation starts.

`MatchRuntimeSettings` applies the room profile:

```text
MenuConnection creates or joins room
-> MatchRuntimeSettings.Configure(session properties)
-> gameplay scene loads
-> MatchRuntimeSettings applies profile during scene load
-> Gameplay.Spawned applies profile again before host world-seed initialization
-> height generation starts
```

Applying twice is intentional and harmless. The scene-load hook updates generator components before their `Start()` methods run. The `Gameplay.Spawned()` hook makes host seed initialization reliable.

## Configurable Values

The first hosting menu exposes:

```text
map width
map height
starting survivors per player
game length: 5, 10, 15, or 20 minutes
fog density
raid mode
starting survivors for non-hosts in raid mode
preserve buried ledge tunnels
max dead-end buried ledge length
max buried ledge tunnel length
height-generation preset
road-generation preset
building-placement preset
loot-spawning preset
zombie-orchestrator preset
preferred neutral survivor count
neutral-survivor preset
```

Preset dropdowns contain a `Scene Default` entry. Selecting it keeps the corresponding inspector-assigned settings asset from the gameplay scene.

Starting survivor counts are clamped to the current team mask capacity of 128 characters per player.

## Main Classes

### `MatchHostingSettings`

Serializable room profile containing the scalar settings and preset indices. It validates menu input and converts the profile to and from Photon session properties.

Fusion limits custom session properties, so the hosted match profile is packed into one string property named `msp`. Fog density is encoded as an integer inside that packed string. The conversion preserves five decimal places.

### `MatchHostingSettingsCatalog`

ScriptableObject shared by the startup menu. It contains:

```text
default hosted settings
ordered height-generation presets
ordered road-generation presets
ordered building-placement presets
ordered loot-spawning presets
ordered zombie-orchestrator presets
ordered neutral-survivor presets
```

All builds must use the same catalog asset and preset order. The room transmits indices, not asset names or asset contents.

### `MatchHostingMenuController`

Menu-facing `FusionMenuUIScreen` for regular Unity UI or TextMeshPro controls. It:

- populates dropdown options from the catalog,
- loads the catalog's default hosted values,
- disables the raid-client survivor input while raid mode is off,
- starts a configured host room from the current UI values.
- exposes `OnStartGameButtonPressed()` and `OnBackButtonPressed()` for the Photon menu prefab `SendMessage` button pattern.

### `MatchHostingMenuNavigation`

Small component added to the existing main-menu screen root. Its `OnHostGameButtonPressed()` method opens `MatchHostingMenuController` through the existing Photon menu-screen controller.

### `MatchRuntimeSettings`

Static runtime bridge that applies a configured room profile to:

```text
Gameplay
HeightMapGenerator
RoadGridGenerator
BuildingPlacementGenerator
WorldLootSpawner
ZombieOrchestrator
NeutralSurvivorOrchestrator
RenderSettings.fogDensity
```

Rooms without the configured-profile marker are left unchanged. This is how Quick Play fallback hosting continues to use scene defaults.

The buried ledge tunnel settings are applied to the scene `BuildingPlacementGenerator` as runtime overrides. They do not mutate the selected `BuildingPlacementSettings` asset, so the same preset can be reused with different hosted-match toggle values.

## Networking Rules

- Session properties are authored only while a configured host creates its room.
- Clients read the host's room properties before generating their local city.
- Quick Play does not filter rooms by configuration. It can join any visible configured or default room.
- Configured hosting always creates a unique room and never attempts random matchmaking first.
- The existing rule that running rooms become hidden after the configured delay remains unchanged.

## Editor Setup

The catalog and UI wiring are authored in the startup scene. No gameplay-scene modifications are required for the first version.

Create presets by duplicating the existing settings assets, modifying their values, and adding them to the catalog arrays. Dropdown labels use asset names, so descriptive asset names become the player-facing configuration names.

The host-settings view should be registered in the `MenuUI` screen list, like the existing main, scenes, settings, loading, and gameplay views. A copied `FusionMenuViewScenes` prefab is a useful layout starting point, but its `FusionMenuUIScenes` component should be replaced with `MatchHostingMenuController`. Its existing Back button already sends `OnBackButtonPressed` and can be kept unchanged.

For buried ledge tunnel controls, hook these `MatchHostingMenuController` fields:

```text
PreserveBuriedLedgeTunnels -> Toggle
MaxDeadEndBuriedLedgeLength -> TMP_InputField
MaxBuriedLedgeTunnelLength -> TMP_InputField
PreferredNeutralSurvivorCount -> TMP_InputField
NeutralSurvivorPreset -> TMP_Dropdown
```

`PreserveBuriedLedgeTunnels` keeps long buried ledge runs that connect two meaningful open/playable anchors. `MaxDeadEndBuriedLedgeLength` keeps short dead-end buried ledge runs as visual stubs; dead-end runs longer than this value are filled with blocking buildings. `MaxBuriedLedgeTunnelLength` caps preserved connector tunnel length; `0` means unlimited.

Zombie orchestrator presets should normally leave `MatchDurationSeconds` at `0`. That makes zombie escalation follow the hosting menu's selected game length through `Gameplay.GameDuration`. A positive orchestrator override intentionally decouples zombie escalation from the match timer.

Neutral survivor presets are `NeutralSurvivorSpawnSettings` assets. The dropdown is optional; leaving it unassigned in the hosting UI keeps the gameplay scene's orchestrator settings.

`PreferredNeutralSurvivorCount` is separate from the neutral survivor preset dropdown. It overrides the selected preset's `DesiredNeutralSurvivorCount` for this hosted match only. Movement speed, sensor ranges, sensor update interval, recruitment rules, spacing rules, and neutral shooting inaccuracies still come from the selected `NeutralSurvivorSpawnSettings` preset.
