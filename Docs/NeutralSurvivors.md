# Neutral Survivors

## Goal

Neutral survivors are unaffiliated survivor characters placed around the generated city. They give players a reason to spread out, scout, and take risks before another player or the zombie horde reaches them.

They behave like ordinary AI-controlled survivors in most ways:

- they use survivor movement, health, weapons, sensors, non-combat AI, combat AI, and map visibility rules where possible,
- their non-combat and combat behavior settings start enabled,
- they always patrol a configured area,
- they can fight zombies,
- they can be damaged and killed.

They are different from player-owned survivors in team ownership and hostility:

- they use the neutral team color,
- they do not attack player-owned survivors,
- they cannot damage player-owned survivors,
- player-owned AI survivors do not treat them as enemies,
- players can still shoot and kill them,
- they do not retaliate against survivor damage before being recruited.

When recruited, the neutral survivor becomes owned by the recruiting player and keeps:

- current health,
- current weapons,
- current ammo,
- current non-combat/combat settings unless the recruiting logic intentionally overrides them later.

## Gameplay Role

Neutral survivors are a strategic RTS/FPS objective.

A player can personally move into an area and recruit them, or later send AI-controlled survivors to do it. That creates a dynamic where players may find enemy-controlled survivors away from the main group while they are looting or trying to recruit neutral survivors.

First pass scope:

- neutral survivors spawn from authored map markers,
- neutral survivors patrol their configured area, set from the spawn marker they spawned to,
- neutral survivors fight zombies,
- player-controlled survivors recruit them by proximity,
- no automatic player-AI recruitment behavior yet.
- map/UI can highlight recruitable neutral survivors,

Future scope:

- AI-controlled player survivors can detect neutral survivors and move to recruit them,

## Neutral Ownership Model

Neutral survivors should not be treated as a normal connected player.

Recommended model:

- use `OwnerRef = PlayerRef.None` or a dedicated neutral owner concept,
- use `Gameplay.NeutralTeamColorIndex` for visuals,
- do not add neutral survivors to any player's `PlayerData.AliveCharacterMask`,
- do register them in a separate neutral survivor collection so systems can find them cheaply.

On recruitment:

1. State authority validates the recruiting survivor is player-owned and alive.
2. Neutral survivor is removed from the neutral collection.
3. Survivor is assigned to the recruiting player's team.
4. A new character index is appended to that player's team.
5. Player `CharacterCount`, `AliveCharacterMask`, and `IsAlive` are updated.
6. Existing gear and health are preserved.
7. Visual team color updates from neutral to the recruiting player's color.
8. The survivor receives the same order the non-controlled player survivor had if the recruited was a non-controlled player survivor. If the recruiter was a player possessed survivor, they should start following them.

Current team masks support up to 128 characters per player.

## Hostility Rules

Neutral survivors need clear faction behavior.

### Neutral Survivor Targets

Neutral survivors may attack:

- zombies.

Neutral survivors may not attack:

- player-owned survivors,
- other neutral survivors.

### Zombie Targets

Zombies should treat neutral survivors like any other survivor target:

- idle/investigating zombies can detect them,
- attacking zombies can chase them,
- hunting/overtime zombies can select them as closest alive survivor targets unless design later says overtime should prioritize player-owned teams only.

Open question:

- During overtime, should zombies hunt neutral survivors too, or should they prioritize player-owned survivors so neutral survivors do not distract the endgame horde? Answer: Yes. They are treated as normal survivors. (99% of the time there also are not any neutral survivors left in overtime)

### Player-Owned Survivor Targets

Player-owned AI survivors should not attack neutral survivors. They can still accidentally damage them, just like the player.

Player-controlled survivors can shoot neutral survivors manually. Neutral survivors do not retaliate against survivor-originated damage before recruitment.

Damage handling should prevent neutral survivors from damaging players even if a weapon raycast or area effect accidentally overlaps a player-owned survivor.

## Recruitment

Recruitment is proximity-based in the first pass.

Suggested marker/settings:

```csharp
NeutralSurvivorRecruitmentSettings
{
	float RecruitmentRadius;
	float RecruitmentCheckInterval;
}
```

Rules:

- only state authority performs recruitment checks,
- AI-controlled player survivors recruit in just the same way as the player possessed survivor, they just do not yet get the behavior to go to the detected neutral survivors like they do with pickups, for example.
- the recruiting survivor must be alive,
- the neutral survivor must be alive,
- recruitment happens once when the player survivor enters radius.

The proximity check can live on:

- the neutral survivor component, checking nearby player-controlled survivors periodically, or
- a neutral survivor orchestrator that batches recruitment checks.

Recommended first pass:

- run the periodic check from `NeutralSurvivorOrchestrator` so recruitment is batched,
- keep the interval modest, for example `0.25s`,
- avoid per-frame scans.
- use `Gameplay`'s neutral survivor registry as the source of truth for recruitment candidates, so neutral survivors remain recruitable even if they were spawned or registered by a different orchestrator instance.

## Spawn Markers

Neutral survivor spawn markers are authored inside generated map prefabs, similar to pickup and zombie spawn markers.

Suggested component:

```csharp
NeutralSurvivorSpawnPoint : MonoBehaviour
{
	public int MinSpawnCount = 1;
	public int MaxSpawnCount = 1;
	public float PatrolRadius = 8f;
}
```

Rules:

- `MinSpawnCount` and `MaxSpawnCount` are clamped so max is at least min.
- `PatrolRadius` defines the defend/patrol area assigned to spawned neutral survivors.
- The marker transform provides spawn position and rotation.
- The patrol center is the marker position unless later we add a separate patrol-center child transform.
- Spawn markers are not networked objects.
- Spawned neutral survivors are networked survivor entities spawned through Fusion.

Open question:

- Should each spawned neutral survivor get a slightly different patrol center inside the marker radius, or should they share the exact marker center and spread through assigned-area AI? Answer: they should all get the equivalent patrol order as a player selecting them all, and dragging a patrol area with right click in the map of the patrolRadius -size.

## Neutral Survivor Orchestrator

The neutral survivor orchestrator is a scene component similar to `ZombieOrchestrator` and `WorldLootSpawner`.

There should be exactly one `NeutralSurvivorOrchestrator` in the gameplay scene. If duplicate orchestrators are accidentally present, only one remains active and the duplicates disable themselves with an error log.

Responsibilities:

- find the Fusion runner,
- wait until generated map objects exist,
- collect `NeutralSurvivorSpawnPoint` markers from generated roots,
- select which markers are allowed to spawn,
- spawn neutral survivors on state authority,
- assign neutral team setup,
- assign patrol/defend area behavior,
- keep a local collection of active neutral survivors.

Suggested component:

```csharp
NeutralSurvivorOrchestrator : MonoBehaviour
{
	public NetworkRunner Runner;
	public Survivor NeutralSurvivorPrefab;
	public NeutralSurvivorSpawnSettings Settings;
	public BuildingPlacementGenerator BuildingGenerator;
	public RoadGridGenerator RoadGenerator;
	public bool SpawnOnStart = true;
	public bool FindRunnerIfMissing = true;
}
```

The orchestrator should scan generated roots, not prefab assets or disabled editor helper layouts.

First-pass generated roots:

- building generator generated root,
- road generator generated root

## Spawn Settings

Suggested settings asset:

```csharp
NeutralSurvivorSpawnSettings : ScriptableObject
{
	[Range(0f, 1f)]
	public float SpawnPointUsage = 1f;
	public float MinDistanceBetweenSelectedSpawnPoints = 0f;
	public int SeedOffset;
	public float SpawnNavMeshSampleDistance = 1.5f;
	public float MinimumSpawnConnectedNavMeshRadius = 8f;
}
```

`SpawnPointUsage`:

```text
0.0 = spawn no neutral survivors
1.0 = use every valid marker allowed by constraints
```

Selection flow:

1. Collect all marker components under generated roots.
2. Filter invalid markers.
3. Shuffle deterministically using world seed + `SeedOffset`.
4. Walk shuffled markers and select markers until reaching `round(validMarkerCount * SpawnPointUsage)`.
5. Respect `MinDistanceBetweenSelectedSpawnPoints`, including each marker's `PatrolRadius`.
6. For each selected marker, roll or choose a count between `MinSpawnCount` and `MaxSpawnCount`.
7. Spawn that many neutral survivors near the marker.
8. Assign each survivor to the marker's patrol area.

If the distance constraint rejects many markers, the final count may be lower than `SpawnPointUsage` would imply. This is intended; the constraint protects map readability and avoids clusters created by adjacent building tiles.

The selected-marker distance is reserved per generated scene, so repeated spawn passes cannot place separate neutral groups inside the configured minimum distance. Each reservation remembers the distance it was selected with. The spacing check also adds both markers' `PatrolRadius` values, so the configured distance separates the groups' patrol areas rather than only their invisible center points. The distance applies to selected spawn markers, not to individual survivors inside the same marker group.

## Spawn Validity

A neutral survivor spawn point is valid when:

- it is under a generated root,
- it can be sampled to NavMesh within `SpawnNavMeshSampleDistance`,
- it is on a sufficiently connected NavMesh island when `MinimumSpawnConnectedNavMeshRadius > 0`,
- its prefab reference and settings are valid,
- its selected spawn point does not violate `MinDistanceBetweenSelectedSpawnPoints`.

Use the same cheap connected-NavMesh validation style as zombie spawn points:

1. Sample marker position to NavMesh.
2. Probe several nearby points at `MinimumSpawnConnectedNavMeshRadius`.
3. Accept if at least one probe has a complete NavMesh path from the marker sample.

This prevents neutral survivors from spawning inside sealed prop pockets or tiny disconnected building islands.

## Patrol Assignment

Before recruitment, neutral survivors should patrol the marker's configured area.

Implementation should reuse `SurvivorNonCombatAI` and assigned-area behavior:

- all non-combat settings enabled,
- combat AI enabled,
- assigned area center = marker position snapped to NavMesh/height map,
- assigned area radius = marker `PatrolRadius`,
- neutral survivor can interrupt patrol to fight zombies,
- after zombie combat, they return to the assigned patrol area.

They should not investigate survivor-originated stimuli as hostile events. They can react to zombie-related combat if the existing stimulus system makes that distinction possible; otherwise, first pass can simply let them patrol and fight zombies they see.

Open question:

- Should neutral survivors investigate gunshots before recruitment, or should they ignore all non-zombie stimuli to keep them from wandering into player fights? Answer: they should not. They only care about zombie related stimuli.

## Combat Behavior

Neutral survivors should use survivor combat behavior against zombies.

First pass rules:

- combat AI can activate against zombies,
- combat AI does not activate against player-owned survivors,
- neutral survivor weapons cannot damage player-owned survivors,
- player-owned AI survivors do include neutral survivors in enemy sensor results. At this point we do not require it, but in the future we might have hostile non-recruitable enemies. So lets keep all the survivors "seeing" everything.

## Map And UI

First pass can use existing survivor map icon logic with neutral color.

Suggested behavior:

- neutral survivors visible on the map only when sensed by player-owned survivors, unless we intentionally want recruitable objectives always visible later,
- icon color uses neutral team color,
- after recruitment, icon changes to the recruiting player's team color.

Open question:

- Should neutral survivors appear on the map only when seen, or should they be global objectives once generated? Answer: only when seen.

## Networking

Neutral survivors are gameplay actors and should be spawned through Fusion on scene/state authority.

Do not network marker selection state separately. The authoritative result is the spawned survivor objects.

The host/state authority:

- selects markers,
- spawns survivor network objects,
- initializes neutral survivor state,
- processes recruitment,
- updates player team data on recruitment.

Clients receive:

- spawned survivor objects,
- neutral/team visual state,
- ownership/team changes after recruitment.

## Hosting Menu Integration

The match hosting menu should eventually expose a neutral survivor preset dropdown, similar to:

- building placement presets,
- road generation presets,
- loot spawning presets,
- zombie orchestrator presets.

First pass settings asset can be referenced from a catalog:

```csharp
MatchHostingSettingsCatalog
{
	NeutralSurvivorSpawnSettings[] NeutralSurvivorPresets;
}
```

Runtime match settings should select one preset and apply it to the scene orchestrator before generation/spawning.

This can be added after the base neutral survivor system works.

## Implementation Plan

1. Add `NeutralSurvivorSpawnPoint` marker.
2. Add `NeutralSurvivorSpawnSettings` asset type.
3. Add `NeutralSurvivorOrchestrator`.
4. Add neutral survivor runtime component/state.
5. Add neutral survivor registration collection.
6. Spawn neutral survivors from selected markers after world generation.
7. Initialize neutral color and neutral faction.
8. Assign marker patrol area through existing non-combat/assigned-area AI.
9. Add proximity recruitment by player-controlled survivors.
10. Append recruited survivor to the player's team while preserving health and gear.
11. Make zombies consider neutral survivors valid targets.
12. Make player-owned AI survivors ignore neutral survivors as enemies.
13. Ensure neutral survivor attacks/damage only affect zombies.
14. Add map icon/color behavior.
15. Add hosting-menu preset support.

## Current Implementation

The first pass is implemented with these runtime pieces:

- `NeutralSurvivorSpawnPoint` is the authored marker component placed inside generated road/building prefabs.
- `NeutralSurvivorSpawnSettings` controls marker usage, marker spacing, NavMesh validation, and recruitment radius/interval.
- `NeutralSurvivorOrchestrator` waits for road/building generation, collects markers from generated roots, spawns neutral survivor network objects on scene authority, assigns patrol areas, and batches recruitment checks.
- Neutral identity is represented by `Survivor.OwnerRef == PlayerRef.None` before recruitment.
- `Gameplay` keeps neutral survivors out of player `PlayerData` until recruitment.
- Recruitment appends the survivor to the recruiting player's team, assigns input authority to the survivor hierarchy, updates `CharacterCount` and `AliveCharacterMask`, and gives the recruited survivor either the recruiter's current non-combat order or a follow order if the recruiter is player-possessed.
- Survivor AI can still sense neutral survivors for map/UI purposes, but `SurvivorAIShooting` filters them out as auto-shoot targets.
- Neutral survivor weapons can only damage zombies.
- Zombies treat neutral survivors as normal survivor targets.
- Neutral survivor map icons use the neutral team color and appear only when sensed by player-owned survivors.

## First-Pass Acceptance Criteria

- Neutral survivor markers can be authored inside generated map prefabs.
- Orchestrator can spawn none, some, or all neutral survivor markers based on `SpawnPointUsage`.
- Marker selection respects minimum distance between selected markers.
- Spawned neutral survivors use neutral colors.
- Spawned neutral survivors patrol the configured marker radius.
- Neutral survivors fight zombies.
- Zombies fight neutral survivors.
- Player-owned survivor AI does not shoot neutral survivors.
- Neutral survivors do not shoot or damage player-owned survivors.
- A player-controlled survivor recruits a neutral survivor by entering recruitment range.
- Recruited survivors keep current health, weapons, and ammo.
- Recruited survivors become part of the recruiting player's team.
