# Zombie Orchestrator

## Goal

Zombies are the match pressure system. They start sparse, weak, and slow, then become more numerous and more dangerous as the match timer advances. When the configured match length is passed, the match does not end immediately. Instead, all zombies enter an overtime hunting phase where they become faster than survivors and relentlessly hunt the nearest living survivor until only one team remains.

The orchestrator owns global zombie pressure:

- how many zombies may exist right now,
- how many to spawn this pulse,
- which spawn locations are valid,
- which parts of the map should receive more zombies,
- what stats newly spawned zombies receive,
- when all zombies switch to overtime super stats.

Individual zombie movement and attacking behavior is documented in `Docs/ZombieAI.md`.

Configured hosted matches may override the scene's `ZombieOrchestratorSettings` reference before spawning starts. The hosting menu selects an authored preset from `MatchHostingSettingsCatalog`.

## Core Rules

- Zombie spawning runs only on scene/state authority.
- Spawn markers are regular non-networked components authored inside generated building prefabs.
- Actual zombies are gameplay entities and must be spawned through Fusion.
- Existing zombies do not scale up during normal match time.
- Newly spawned zombies receive stats based on current match progress.
- When overtime starts, every alive zombie immediately receives super stats.
- Overtime zombies always have a target to hunt: each picks a random alive player, then that player's nearest survivor, so overtime pressure is shared evenly between players regardless of positioning.

## Spawn Markers

Building prefabs can contain zombie spawn marker transforms, similar to pickup spawn points.

Suggested marker:

```csharp
ZombieSpawnPoint
{
	public int MaxSpawnCountPerPulse = 4;
	public float NonForcedSurvivorBlockRadius = 12f;
}
```

`NonForcedSurvivorBlockRadius` controls whether the spawn is forced:

```text
0 or less = forced spawn, never blocked by survivor proximity
above 0 = non-forced spawn, blocked while an alive player-owned survivor is inside the radius
```

Only player-owned survivors block non-forced spawns. Neutral survivors never suppress zombie spawning — they are meant to be threatened by the horde.

Forced spawn points:

- Always valid if the spawn point itself is usable.
- Used for obvious zombie sources such as graveyards, infestations, sewer entrances, or other high-threat locations.
- Function as fallback points so the orchestrator can always keep pressure on the match.

Non-forced spawn points:

- Skipped when an alive player-owned survivor is inside `NonForcedSurvivorBlockRadius`.
- Used for less obvious locations where spawning zombies in front of the player would feel unfair, such as warehouses, alleys, apartments, or back rooms.
- Still useful for repopulating cleared areas when no survivors are nearby.

The marker stores placement intent only. It should not contain a networked zombie prefab as a child.

## Orchestrator Settings

Suggested first-pass settings:

```csharp
ZombieOrchestratorSettings
{
	NetworkObject ZombiePrefab;

	int StartMaxZombies;
	int EndMaxZombies;
	float InitialPopulation;
	float MatchDurationSeconds;
	bool ScaleDuringSkirmish;

	float StartSpawnRatePerMinute; // per connected player
	float EndSpawnRatePerMinute;   // per connected player
	float SpawnPulseInterval;

	float SpawnNavMeshSampleDistance;
	float MinimumSpawnConnectedNavMeshRadius;

	float StartHealth;
	float EndHealth;
	float StartDamage;
	float EndDamage;
	float StartMoveSpeed;
	float EndMoveSpeed;
	float StartAlertRadius;
	float EndAlertRadius;

	float OvertimeSpawnRatePerMinute; // per connected player

	float OvertimeHealth;
	float OvertimeDamage;
	float OvertimeMoveSpeed;

	float UnderpopulatedRegionBias;
	int RegionGridSize;
	int SeedOffset;
	int MatchStartSeedOffset;
}
```

Suggested defaults:

```text
StartMaxZombies: 100
EndMaxZombies: 400
InitialPopulation: 0
MatchDurationSeconds: match setting
StartSpawnRatePerMinute: 12   (per connected player)
EndSpawnRatePerMinute: 60     (per connected player)
SpawnPulseInterval: 5s
OvertimeSpawnRatePerMinute: 60 (per connected player)
SpawnNavMeshSampleDistance: 1.5
MinimumSpawnConnectedNavMeshRadius: 8
UnderpopulatedRegionBias: 0.65
RegionGridSize: 3 or 4
```

`StartMaxZombies` and `EndMaxZombies` are separate because the match should not start with the full zombie budget even if the final cap is high.

`InitialPopulation` controls how many zombies are seeded immediately when the orchestrator starts spawning:

```text
0.0 = no initial zombies; the map fills only through normal spawn pulses
1.0 = try to spawn up to StartMaxZombies immediately
```

The initial population pass still respects each spawn point's `MaxSpawnCountPerPulse`, survivor-blocking rules, NavMesh validation, and regional spawn selection. If the map does not have enough valid spawn points or per-point capacity, fewer zombies spawn.

## Skirmish vs Match Start

The orchestrator spawns zombies during skirmish (the initial population and, if `SpawnDuringSkirmish` is set, ongoing pulses). When the match transitions out of skirmish (`Gameplay.State` reaches `Running`), it performs a one-time **match-start reroll**, mirroring the neutral survivor and loot spawners:

1. Despawn the entire skirmish horde (every active zombie) so the live match starts from a clean slate.
2. Re-seed the spawn RNG with `+ MatchStartSeedOffset` so the match layout differs from the skirmish preview.
3. Re-run the initial population pass and resume normal spawn pulses.

This is what keeps the match from simply inheriting the zombies that wandered around during skirmish. Player proximity is still respected: the re-rolled initial population uses the same survivor-blocking rules, so it does not seed zombies on top of just-spawned player survivors, and `Gameplay.SpawnTeam` additionally clears zombies near each player spawn. The trigger is polled via `Gameplay.IsRunning`, which is safe to read before the gameplay `NetworkObject` is spawned (it returns `false` until then instead of throwing).

`UnderpopulatedRegionBias` controls how strongly the orchestrator prefers cleared areas:

```text
0.0 = choose valid spawns uniformly
0.65 = most spawns prefer underpopulated regions, but some still go elsewhere
1.0 = always prioritize the most underpopulated valid regions
```

## Time Scaling

Normal match progress:

```text
progress = clamp01(matchElapsed / MatchDurationSeconds)
```

By default, skirmish mode forces progress to `0`. This means skirmish-spawned zombies use the starting cap, starting spawn rate, and starting stats while players wait for the match. `ScaleDuringSkirmish` can be enabled for play-mode testing, causing skirmish zombies to scale using elapsed scene time.

Current max zombies:

```text
currentMaxZombies = lerp(StartMaxZombies, EndMaxZombies, progress)
```

Newly spawned zombie stats:

```text
health = lerp(StartHealth, EndHealth, progress)
damage = lerp(StartDamage, EndDamage, progress)
moveSpeed = lerp(StartMoveSpeed, EndMoveSpeed, progress)
alertRadius = lerp(StartAlertRadius, EndAlertRadius, progress)
```

Spawn rate is **per connected player**: every additional player raises the effective rate by the configured per-player value, so a two-player match spawns twice as fast as a one-player match, and a twenty-player match twenty times as fast.

```text
ratePerPlayer       = lerp(StartSpawnRatePerMinute, EndSpawnRatePerMinute, progress)
spawnRatePerMinute  = ratePerPlayer × connectedPlayerCount
```

There is no separate per-pulse cap anymore — the per-player rate is the only control over how fast zombies arrive. The total population is still bounded by the current max-zombies cap. Disconnected players are not counted (the count is clamped to a minimum of 1), so a player drop reduces pressure rather than continuing to spawn the original headcount's worth of zombies.

During overtime the spawn rate is no longer derived from `EndSpawnRatePerMinute`. It uses the dedicated `OvertimeSpawnRatePerMinute` instead (also per connected player). This lets overtime pressure be tuned independently of where the normal end-of-match curve lands.

Existing zombies keep their spawn-time stats during normal time. This lets old zombies stay weak while newer zombies naturally become more dangerous.

## Overtime

When `matchElapsed >= MatchDurationSeconds`:

- Overtime begins.
- The zombie cap becomes `EndMaxZombies`.
- The per-player spawn rate switches to `OvertimeSpawnRatePerMinute`, independent of the normal spawn-rate curve.
- All alive zombies immediately receive overtime stats.
- Existing zombies preserve their current health percentage when their max health changes. For example, a zombie at `50%` health becomes `50%` of `OvertimeHealth`, not fully healed.
- Newly spawned zombies receive overtime stats.
- Zombie AI switches to hunting behavior.
- Zombies do not need normal sensory discovery or alerting to find a target. Each hunting zombie picks a random player that still has at least one alive survivor, then targets that player's closest survivor. Neutral survivors are excluded from this global pick so pressure is balanced per player, but a hunting zombie that directly senses any survivor — neutral included — attacks it instead. Hunting target selection is documented in `Docs/ZombieAI.md`.

Overtime does not instantly end the match. It turns the city into a closing pressure system and asks which team survives longest.

## Spawn Pulse

The orchestrator runs on a fixed pulse, not every tick.

Each pulse:

1. Count alive zombies.
2. Compute current max zombies.
3. If alive zombies are at or above the current max, do nothing.
4. Compute spawn budget from the per-player spawn rate × current connected player count (overtime uses `OvertimeSpawnRatePerMinute`).
5. Gather valid forced and non-forced spawn points.
6. Score valid spawn points by regional underpopulation.
7. Spawn up to the budget, respecting each spawn point's `MaxSpawnCountPerPulse`.

The spawn rate scales linearly with the number of currently connected players: each connected player adds `Start/EndSpawnRatePerMinute` (or `OvertimeSpawnRatePerMinute`) worth of zombies-per-minute to the budget. A four-player match arrives four times as fast as a one-player match using the same settings asset. Disconnected players are not counted; the rate follows the live match population so a player drop reduces pressure rather than continuing to spawn the original headcount's worth of zombies. The current max-zombies cap still bounds the total alive population per pulse.

Example:

```text
Current zombies: 50
Current max zombies: 200
Pulse budget: 30
East side: cleared
West side: crowded
UnderpopulatedRegionBias: 0.65

Expected result:
roughly 20 zombies prefer valid east-side spawns
roughly 10 zombies spawn elsewhere
```

If no valid underpopulated spawn exists, the orchestrator falls back to any valid forced spawn.

If there are not enough spawns available to spawn all/any zombies, do not spawn them.

## Player Spawn Safety

`Gameplay` clears zombies around a player's assigned spawn point after that player's team is spawned. This protects the initial spawn and late-joining players who are placed into a spread-out spawn after the match has already started.

Scene tuning:

```csharp
Gameplay
{
	public ZombieOrchestrator ZombieOrchestrator;
	public float ZombieSpawnClearRadius = 24f;
}
```

`ZombieSpawnClearRadius` is intentionally an inspector value, not a hosting-menu value yet. The clear runs on scene authority and despawns any currently alive zombie inside that flat radius from the assigned player spawn.

## Regional Pressure

The first implementation divides the collected zombie spawn marker bounds into a coarse grid, for example `3x3` or `4x4`.

For each region:

- Count alive zombies currently inside the region.
- Count usable spawn points inside the region.
- Estimate the desired zombie share for that region.
- Prefer regions with fewer zombies than their share.

This does not need to be perfect. The goal is to avoid putting all new zombies into already crowded areas while still keeping some randomness. Also to incentivize clearing "full" areas. Even if there is a single spawner, killing zombies in an area and keeping it cleared "pushes" zombies to other areas, as all new zombies do not spawn into the cleared area.

Later this can be upgraded to region bounds from world-generation snapshots so regions without roads or complex buildings can be discarded more deliberately. The current spawn-marker approach is cheaper and works as long as zombie spawners are authored only in places that should participate in zombie pressure.

Avoid expensive spatial work:

- Do not run per-zombie path checks during spawn selection.
- Do not count exact zombies per spawn point every frame.
- Recompute region counts only during spawn pulses.
- Use simple lists and reusable buffers.

## Spawn Validity

A spawn point is valid when:

- It belongs to the generated world currently in use.
- Its zombie prefab pool is configured.
- The point is on or near reachable NavMesh.
- The NavMesh island under the point is large enough to be useful.
- The point has not exceeded `MaxSpawnCountPerPulse`.
- If `NonForcedSurvivorBlockRadius` is above `0`, no alive player-owned survivor is inside that radius. Neutral survivors do not count against this.

Spawn point collection filters out markers on tiny disconnected NavMesh islands. This handles generated alleys or building pockets where the marker is technically on NavMesh, but that NavMesh is cut off from the playable city by walls, blocking buildings, or prop arrangements.

The first-pass check is intentionally simple:

```text
1. Sample the marker position to NavMesh using SpawnNavMeshSampleDistance.
2. If MinimumSpawnConnectedNavMeshRadius <= 0, accept any sampled NavMesh.
3. Probe several points around the marker at MinimumSpawnConnectedNavMeshRadius.
4. Accept the spawn only if at least one probe is on the same connected NavMesh island through a complete NavMesh path.
```

This is not a perfect island-area calculation. It is a cheap "can this spawn reach a meaningful amount of nearby NavMesh?" test that runs when spawn points are collected and again during spawn candidate construction in case the NavMesh changed.

Non-forced blocker checks should be cheap:

- Use the known survivor registry if available.
- Ignore survivor sensor entries whose Fusion `NetworkObject` has not completed spawning yet. Generated/spawned components may register their sensors before `Spawned()` runs, and networked health or ownership must not be read during that window.
- Use squared distance.
- Check only during spawn pulses.

Do not use expensive visibility/path checks against every survivor or zombie to decide whether a spawn point is valid in the first version.

## Fusion Spawn Model

Zombies are networked gameplay actors, so the orchestrator must spawn them through Fusion:

```csharp
Runner.SpawnAsync(ZombiePrefab, spawnPosition, spawnRotation);
```

The orchestrator should spawn only on scene/state authority:

```text
if Runner.IsSceneAuthority == false:
	do nothing
```

Every peer loads the scene with its own orchestrator, but only the scene authority runs spawn pulses; clients receive zombies through replication. The orchestrator also guards against duplicate components in the same scene: on `Awake` it keeps a single primary instance and disables any extras with an error log, so an accidentally duplicated orchestrator cannot double-spawn zombies.

Spawn-time stats can be applied in the spawn callback or immediately after the spawned object is available.

Temporary orchestrator state should not be networked:

- spawn point lists,
- region scores,
- current spawn budget,
- shuffled spawn queues.

The authoritative result is the spawned zombie objects and their replicated movement/combat state.

## Generated World Integration

Generation order:

```text
Height map
Road generation
Building placement
NavMesh rebuild
Loot spawning
Zombie spawn point collection
Terrain climb surface registration
Zombie orchestrator starts
```

The orchestrator gathers `ZombieSpawnPoint` components from generated world roots, not from prefab assets or disabled editor helpers. It currently scans both the generated building root and the generated height/ledge root, so zombie spawners can live in building prefabs or authored ledge/height tiles.

After generated spawn points exist, the orchestrator also builds terrain zombie climb surfaces from `WorldHeightSnapshot` and caches the generated `WorldGridSnapshot` for road-tile checks. The terrain climb surfaces are broad ledge-face permissions used by `ZombieAI` for direct ledge climbs; they are not NavMesh links and are needed only on scene authority because zombie AI runs only there. Reusable prop/building rescue surfaces register themselves through `ZombieClimbableSurface`.

Suggested component references:

```csharp
	ZombieOrchestrator
{
	BuildingPlacementGenerator BuildingGenerator;
	HeightMapGenerator HeightGenerator;
	ZombieOrchestratorSettings Settings;
	NetworkRunner Runner;
	Gameplay Gameplay;
	bool FindRunnerIfMissing;
	bool CollectSpawnPointsOnStart;
	bool SpawnDuringSkirmish;
}
```

Like loot spawning, zombie spawn markers live in static generated environment prefabs. The zombies themselves are spawned network objects.

When the gameplay timer expires, `Gameplay` starts zombie overtime if a usable `ZombieOrchestrator` exists. If no usable orchestrator exists, the old timer-end behavior still finishes the match.

## Performance Goals

The orchestrator must stay cheap enough for hundreds of zombies.

Good:

- spawn pulses every few seconds,
- coarse region scoring,
- capped spawn count per pulse,
- capped spawn count per spawn point,
- reusable buffers,
- scene authority only.

Avoid:

- per-frame spawn point scans,
- per-spawn full map pathfinding,
- networked spawn scoring state,
- spawning all missing zombies in one frame,
- putting all zombies on one non-forced marker.

## Implementation Direction

Recommended first implementation path:

1. Add `ZombieSpawnPoint` marker with `MaxSpawnCountPerPulse` and `NonForcedSurvivorBlockRadius`.
2. Add a `ZombieOrchestratorSettings` asset or component data container.
3. Add `ZombieOrchestrator` as a scene component.
4. After world generation, collect spawn markers under the generated building and height/ledge roots.
5. During play, run spawn pulses on scene authority.
6. Linearly scale current cap, spawn rate, and spawn-time stats by match progress.
7. Spawn zombies through Fusion.
8. Add overtime stat application and hunting activation.

## Resolved Decisions

- Should the starting zombie count be spawned immediately at match start, or should zombies ramp in through normal spawn pulses? 
Answer: controlled by `InitialPopulation`. `0` keeps the old empty-start behavior, while higher values seed part of `StartMaxZombies` before normal spawn pulses take over.
- Should forced spawns ignore survivor proximity entirely, or should they still avoid spawning directly inside melee range?
Answer: forced spawns are forced spawns. They will be placed into places where it is conveyed that zombies spawn from, so they should spawn in even if there is a survivor directly on-top of them.
- Should zombie spawn points support different zombie prefab pools later, such as normal, fast, armored, or boss zombies?
Answer: Maybe, but it is not something we need to think about of right now. It is easy enough to add later.
- Should region scoring use the whole map grid, road/building cells, or just spawn-point clusters?
Answer: the first implementation uses spawn-point clusters because it is simple, cheap, and does not require another world snapshot contract. If this is not enough later, upgrade it to use road/complex-building world regions and discard regions that only contain blocking buildings and ledges.
