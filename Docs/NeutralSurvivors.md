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
- AI-controlled player survivors detect sensed neutral survivors and walk over to recruit them (see `SurvivorRecruitingAI.md`),
- map/UI can highlight recruitable neutral survivors,

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
4. The recruited survivor is inserted directly after the recruiting survivor in that player's character order.
5. Later character indices and the alive mask are shifted up by one so cycling and roster order keep the recruit grouped with the recruiter.
6. Existing gear and health are preserved.
7. Visual team color updates from neutral to the recruiting player's color.
8. Temporary neutral-only stat overrides are restored to the survivor prefab's normal values.
9. The survivor receives a post-recruitment order:
   - if the recruiter is player-possessed, follow the recruiter,
   - if the recruiter is AI-controlled and had a player move order, inherit the same persistent move/guard anchor,
   - if the recruiter is AI-controlled and had an assigned patrol/defend area, inherit the same area,
   - otherwise inherit the closest equivalent current non-combat assignment.
   - copy the recruiter's non-combat toggles, weapon preference, combat behavior, and retreat mode.

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
- AI-controlled player survivors recruit in just the same way as the player possessed survivor; they also actively walk to sensed neutral survivors via `SurvivorRecruitingAI` (see `SurvivorRecruitingAI.md`),
- the recruiting survivor must be alive,
- the neutral survivor must be alive,
- recruitment happens once when the player survivor enters the spherical 3D radius. Vertical separation counts, so survivors on stacked floors do not recruit through ceilings.

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
	public bool DynamicSpawn;
}
```

Rules:

- `MinSpawnCount` and `MaxSpawnCount` are clamped so max is at least min.
- `PatrolRadius` defines the defend/patrol area assigned to spawned neutral survivors.
- The marker transform provides spawn position and rotation.
- The patrol center is the marker position unless later we add a separate patrol-center child transform.
- `DynamicSpawn` controls whether survivors stay and patrol here or roam between dynamic spawns (see Roaming). It is an editor toggle on the marker, so the two spawn flavours (street vs complex-building) can be authored per marker.
- Spawn markers are not networked objects.
- Spawned neutral survivors are networked survivor entities spawned through Fusion.

There are two intended marker flavours:

- **Street spawns** (`DynamicSpawn = true`): small, frequent groups that make the streets feel alive and roam between dynamic spawns.
- **Complex-building spawns** (`DynamicSpawn = false`): larger, hunkered-down groups that stay and defend their authored area, usually near pickups.

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
	public Survivor NeutralSurvivorPrefab; // optional override; falls back to Gameplay.SurvivorPrefab
	public NeutralSurvivorSpawnSettings Settings;
	public BuildingPlacementGenerator BuildingGenerator;
	public RoadGridGenerator RoadGenerator;
	public bool SpawnOnStart = true;
	public bool FindRunnerIfMissing = true;
}
```

Neutral survivors are not a different prefab from player survivors — the only difference is the runtime `OwnerRef` (`PlayerRef.None` vs a real player) and the team color it resolves to. `NeutralSurvivorPrefab` is therefore an optional override: leave it empty to spawn the same prefab as `Gameplay.SurvivorPrefab`. A single shared survivor prefab is the intended setup.

The orchestrator should scan generated roots, not prefab assets or disabled editor helper layouts.

First-pass generated roots:

- building generator generated root,
- road generator generated root

## Spawn Settings

Suggested settings asset:

```csharp
NeutralSurvivorSpawnSettings : ScriptableObject
{
	public int DesiredNeutralSurvivorCount = 20;
	public float MinDistanceBetweenSelectedSpawnPoints = 0f;
	public float MinDistanceToActivePlayerSpawns = 0f;
	public int SeedOffset;
	public int MatchStartSeedOffset = 7919;
	public float SpawnNavMeshSampleDistance = 1.5f;
	public float MinimumSpawnConnectedNavMeshRadius = 8f;
	public float RecruitmentRadius = 3f;
	public float RecruitmentCheckInterval = 0.25f;

	public bool ApplyNeutralStatOverrides = true;
	public float NeutralMovementSpeed;
	public float NeutralVisionDistance;
	public float NeutralAllAroundDetectionRange;
	public float NeutralSensorInterval;
	public float NeutralHorizontalAimErrorDegrees;
	public float NeutralVerticalAimErrorDegrees;
}
```

Roam dwell timing lives on the `NeutralSurvivor` component (`RoamDwellTimeMin`/`RoamDwellTimeMax`), not in this settings asset, so it is tuned on the survivor prefab alongside the survivor's other per-character knobs (see Roaming).

`MinDistanceToActivePlayerSpawns`:

```text
0.0 = no player-spawn constraint
>0  = skip any marker whose center is within this distance (plus the marker's PatrolRadius)
      of a player spawn point currently assigned to a connected player
```

Only player spawns that are actually in use are considered. Unused spawn points never block neutral spawns. The distance, like the inter-marker spacing, adds the marker's `PatrolRadius` so the neutral patrol area — not just its center — stays clear of the player spawn.

Raid mode is a special case: the host team spawns at the valid neutral survivor marker closest to the generated
map center, but that center marker is not registered as an active player spawn for this neutral-spawn distance
check. Instead, the chosen marker itself is reserved: it is skipped by neutral spawning, and already-spawned
neutral survivors from that exact marker are despawned. Zombies are still cleared around the host's actual center
spawn, while nearby different neutral markers remain allowed so the commander starts from the middle without
hollowing out the broader central neutral layout.

`MatchStartSeedOffset` is added to the world seed for the match-start re-roll (see Spawn Timing), so the layout the real match begins with differs from the skirmish preview.

`DesiredNeutralSurvivorCount` is the total number of neutral survivors the orchestrator tries to place across the map. It selects spaced markers and accumulates each marker's rolled `MinSpawnCount..MaxSpawnCount` count until the desired total is reached. This gives the host precise control over the headcount instead of an abstract marker ratio.

The hosting menu can override `DesiredNeutralSurvivorCount` with a separate numeric **preferred neutral survivor count** field. The selected preset still supplies every other neutral survivor setting. This allows the same "weak civilians", "trained guards", or "many but helpless" preset to be reused with different match sizes without duplicating assets.

Neutral stat overrides make unrecruited neutral survivors intentionally weaker than owned survivors. The `NeutralSurvivor` runtime component snapshots the survivor prefab's current values, applies the preset's weaker neutral values, and restores the snapshot after recruitment:

- `NeutralMovementSpeed` temporarily replaces `Survivor.AIMoveSpeed`. Neutral survivors are always AI-controlled, so direct player `MoveSpeed` is not overridden.
- `NeutralVisionDistance` temporarily replaces `CharacterSensor.VisionDistance`.
- `NeutralAllAroundDetectionRange` temporarily replaces `CharacterSensor.ProximityAwarenessRadius`.
- `NeutralSensorInterval` temporarily replaces `CharacterSensor.SensorInterval`.
- `NeutralHorizontalAimErrorDegrees` / `NeutralVerticalAimErrorDegrees` temporarily replace all AI shooting inaccuracy values while neutral. Neutral survivors only fight zombies right now, but both shooting buckets are set to the same value so future neutral targets do not need a separate preset split.

Selection flow:

1. Collect all marker components under generated roots.
2. Filter invalid markers.
3. Shuffle deterministically using world seed + `SeedOffset`.
4. Walk shuffled markers; for each marker that passes the constraints, roll its `MinSpawnCount..MaxSpawnCount` count and select it, accumulating survivors until reaching `DesiredNeutralSurvivorCount`.
5. Respect `MinDistanceBetweenSelectedSpawnPoints`, including each marker's `PatrolRadius`.
6. Respect `MinDistanceToActivePlayerSpawns` against every in-use player spawn, including the marker's `PatrolRadius`.
7. The marker that crosses the desired total is capped so the total lands exactly on `DesiredNeutralSurvivorCount`.
8. Spawn each selected marker's survivors near the marker.
9. Assign each survivor to the marker's patrol area.

If the constraints (spacing, player-spawn distance, NavMesh validity) leave too few usable markers to reach `DesiredNeutralSurvivorCount`, fewer survivors spawn. This is intended; the count is a target, not a guarantee.

The selected-marker distance is reserved per generated scene, so repeated spawn passes cannot place separate neutral groups inside the configured minimum distance. Each reservation remembers the distance it was selected with. The spacing check also adds both markers' `PatrolRadius` values, so the configured distance separates the groups' patrol areas rather than only their invisible center points. The distance applies to selected spawn markers, not to individual survivors inside the same marker group.

## Spawn Timing

Player spawn points only become "in use" once a player has actually been placed at them, so the player-spawn constraint depends on when neutral survivors are selected. The orchestrator runs two passes on scene authority:

1. **Skirmish pass** — runs as soon as the generated world is ready (typically during skirmish, when only the host is placed). It spawns neutral survivors using whatever player spawns are in use at that moment, so `MinDistanceToActivePlayerSpawns` only avoids the host's spawn at this stage.
2. **Match-start pass** — runs once the match transitions to `Running`, when every participating player has been assigned a spawn. It despawns the skirmish-pass neutrals, drops their spacing reservations, re-seeds with `MatchStartSeedOffset`, and re-selects against **all** in-use player spawns. This is the authoritative layout the match plays with.

If the world is already `Running` the first time the orchestrator spawns, it skips the skirmish pass and produces the match-start layout directly.

Late joiners (players who connect after the match-start pass) are not re-pruned in the current version — their neutral survivors may spawn closer. This is acceptable until a "all clients join simultaneously" lobby flow exists.

Skirmish must not grant an advantage: at match start `Gameplay.StartGameplay` despawns and re-spawns every connected player's team from scratch, so any neutral survivor recruited during skirmish (which had joined the host's team) is wiped along with it. The neutral re-roll independently despawns the still-neutral skirmish survivors. Net result: nothing carried over from skirmish — neither recruited survivors nor picked-up loot (loot is re-rolled too; see `WorldGenerationLootSpawning.md`).

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

## Roaming (Dynamic Spawns)

Survivors spawned from a marker with `DynamicSpawn = true` do not stay in one patrol area. They roam between dynamic spawn areas, making the streets feel alive, meeting players in random places (better recruit targets), and getting culled faster by wandering through zombie-dense alleys where zombies spawn precisely because no players are nearby.

Roam destinations are **every valid dynamic-spawn marker**, whether or not it was chosen to spawn survivors. The orchestrator builds this list once after collecting markers and shares it with every roamer.

Per-survivor roam cycle (driven by `NeutralSurvivor` on state authority, where AI assignment is local):

1. Start by dwelling at the spawn area for a random time in `[RoamDwellTimeMin, RoamDwellTimeMax]` (serialized on the `NeutralSurvivor` component).
2. When the dwell elapses, **independently** pick a different random dynamic spawn area and head there. Each survivor chooses on its own timer, so a group spreads out over time.
3. On arrival, dwell again, then repeat. Recruitment, death, or being assigned a player order ends roaming.

The move to the next area is issued through `SurvivorNonCombatAI.RoamArea` — an assigned-area order that starts **already satisfied**, so the survivor has full autonomy *while travelling*, not just on arrival:

- it fights zombies with its combat AI on the way (and at the destination),
- it detours for visible pickups it does not already have (looting is allowed during the travel because the order is "satisfied"),
- after combat or looting it continues toward the chosen area, then patrols it.

If a chosen area cannot be reached (no NavMesh path set could be built), the survivor idles briefly and picks a different one. A roaming neutral is still a normal recruit target the whole time, so players (and AI recruiters via the recruit detour) can chase one down mid-roam.

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

Runtime match settings select one preset and apply it to the scene orchestrator before generation/spawning.

The hosting menu also exposes a separate neutral survivor count input. That value overrides the selected preset's `DesiredNeutralSurvivorCount`; all other neutral survivor stats and spawn constraints come from the preset dropdown.

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
10. Insert recruited survivor directly after the recruiter while preserving health and gear.
11. Make zombies consider neutral survivors valid targets.
12. Make player-owned AI survivors ignore neutral survivors as enemies.
13. Ensure neutral survivor attacks/damage only affect zombies.
14. Add map icon/color behavior.
15. Add hosting-menu preset support.

## Current Implementation

The first pass is implemented with these runtime pieces:

- `NeutralSurvivorSpawnPoint` is the authored marker component placed inside generated road/building prefabs.
- `NeutralSurvivorSpawnSettings` controls marker usage, marker spacing, distance from in-use player spawns, the match-start seed offset, NavMesh validation, recruitment radius/interval, and temporary neutral-only stat overrides.
- `NeutralSurvivorOrchestrator` waits for road/building generation, collects markers from generated roots, spawns neutral survivor network objects on scene authority, assigns patrol areas, and batches recruitment checks. It spawns a skirmish-pass layout when the world is ready and re-rolls a match-start layout (re-seeded, pruned against every in-use player spawn) once the match reaches `Running` — see Spawn Timing.
- `NeutralSurvivorOrchestrator.SetRuntimeDesiredNeutralSurvivorCount` lets the hosting menu override only the preferred count without mutating the selected preset asset.
- The `NeutralSurvivor` runtime component snapshots and applies neutral stat overrides after spawn, then restores the snapshot when recruited.
- Neutral identity is represented by `Survivor.OwnerRef == PlayerRef.None` before recruitment.
- `Gameplay` keeps neutral survivors out of player `PlayerData` until recruitment.
- Recruitment inserts the survivor directly after the recruiter in the recruiting player's team order, assigns input authority to the survivor hierarchy, updates `CharacterCount` and `AliveCharacterMask`, restores neutral stat overrides, and assigns the post-recruitment order described in Neutral Ownership Model.
- Because recruitment changes `OwnerRef`/`CharacterIndex` through networked replication (no Spawned/Despawned), each peer re-syncs its local character lookup in `Survivor.Render()` via `Gameplay.ReregisterSurvivor`, so recruited survivors appear in cycling, AI commands, and death-switching on every machine. See `TeamCharacterSystem.md`.
- Survivor AI can still sense neutral survivors for map/UI purposes, but `SurvivorAIShooting` filters them out as auto-shoot targets.
- Non-possessed player-owned survivors actively recruit: `SurvivorRecruitingAI` walks them to sensed neutral survivors (toggled by `RecruitNeutralSurvivors`, on in `Default`). See `SurvivorRecruitingAI.md`.
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
