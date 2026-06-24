# Zombie AI

## Goal

Zombies are simple hostile characters designed to scale to high counts. They should be cheaper than survivors while still spreading through the map, reacting to noise, attacking survivors, and creating pressure that players cannot avoid by standing on props or ledges.

The global spawn and difficulty system is documented in `Docs/ZombieOrchestrator.md`. The traversal design history and failed approaches are documented in `Docs/ZombieAIDesignHistory.md`.

## Design Principles

- Zombies are not survivors with fewer buttons.
- Reuse focused systems where useful: `CharacterSensor`, `CharacterNavigator`, `CharacterSeparation`, health, Fusion spawning, and KCC movement.
- Run zombie AI only on state authority.
- Keep networked state minimal.
- Favor cheap, believable pressure over expensive tactical intelligence.
- Handle traversal through one general movement model instead of accumulating prop-specific or ledge-specific patches.

## Prefab Components

Expected zombie prefab components:

```text
NetworkObject
KCC / SimpleKCC movement component
ZombieCharacter
ZombieAI
ZombieAnimator
Health
CharacterSensor
CharacterNavigator
CharacterSeparation
HitboxRoot / hitboxes as needed
Animator / visual controller
```

Responsibilities:

```text
ZombieCharacter
-> networked identity, health, stats, attacks, and KCC movement

ZombieAI
-> state machine, target choice, and desired movement

ZombieAnimator
-> reads zombie movement/state and updates Animator parameters
```

`ZombieCharacter` is the lightweight survivor substitute. It deliberately omits survivor inventory, player ownership, possession, map selection, team commands, weapon switching, and survivor UI logic.

## Stats

Zombie stats are assigned by the orchestrator on spawn:

```csharp
ZombieStats
{
	float MaxHealth;
	float Damage;
	float MoveSpeed;
	float AlertRadius;
	float AttackRange;
	float AttackCooldown;
}
```

During normal match time:

- A zombie keeps the stats it spawned with.
- New zombies become stronger as match progress increases.

During overtime:

- All living zombies receive overtime stats immediately.
- Existing zombies preserve their current health percentage when max health changes.
- Newly spawned zombies receive overtime stats, with an additional flat max-health bonus for each completed 10 seconds of overtime as configured by `ZombieOrchestratorSettings.OvertimeNewZombieHealthIncreasePer10Seconds`.
- Existing zombies are not repeatedly upgraded by that progressive spawn-health bonus.
- Zombies switch to hunting behavior.

## Sensor Setup

Zombies use `CharacterSensor` with zombie-tuned values.

Suggested starting values:

```text
ProximityAwarenessRadius: 2
NoiseAwarenessRadius: 20
BulletImpactAwarenessRadius: 0 or low
VisionDistance: 12
VisionAngle: 70
SensorInterval: 0.25-0.5
MaxKnownEntries: 4
```

Zombies should react strongly to noise and less strongly to precise vision than survivors. This lets gunfire pull zombies through the city without making every zombie run expensive checks every frame.

## Behavior States

```csharp
public enum EZombieAIState
{
	Idle,
	Investigating,
	Attacking,
	Hunting
}
```

Suggested timing defaults:

```text
IdleWanderIntervalMin: 10
IdleWanderIntervalMax: 14
AttackRetargetInterval: 2.5
HuntingRetargetInterval: 5
HuntingRetargetIntervalJitter: 1
HuntingInitialRetargetStaggerMax: 3
```

These intervals are intentionally coarse. Zombies mostly commit to their current action and reconsider periodically.

### Idle

Idle zombies have no explicit goal.

Rules:

- Intermittently choose a nearby reachable wander point.
- Prefer wander points that move away from nearby zombies.
- Sample wander candidates with enough vertical reach to find connected ramp/ledge surfaces above or below the zombie, then require a complete NavMesh path to the sampled point. Ramps and walkable ledges therefore spread zombies between elevations without using climb logic.
- If an idle wander destination produces no meaningful flat progress for `IdleWanderStuckTimeout`, abandon it and retry later. This prevents one bad ramp lip, crowd knot, or tiny navmesh pocket from trapping an idle zombie indefinitely.
- Use `CharacterSeparation` only as the close-range anti-overlap layer.
- If a survivor is detected, switch to Attacking.
- If suspicious sound or impact stimulus is received while no live survivor target is being chased, switch to Investigating.

Idle spread should slowly diffuse zombie clusters through roads, alleys, and rooms. Candidate sampling is deliberately small and staggered so idle movement remains cheap.

### Investigating

Investigation receives one world-space point from noise, an alert, or the last known position of an escaped living target.

Rules:

- Keep the raw suspicious point as an explicit goal.
- Move through the shared explicit-goal routing policy.
- Alert nearby zombies to the same point unless this zombie was itself alerted.
- If a survivor is detected during investigation, switch to Attacking immediately.
- If the point is reached without finding a survivor, switch to Idle there.
- Do not return to the original idle position.
- Do not periodically reconsider the investigation point.

### Attacking

Attacking is active while a zombie directly detects a living survivor.

Rules:

- Choose a direct target, normally the closest detected survivor.
- Move through the shared explicit-goal routing policy.
- Alert nearby zombies to the target position.
- Periodically reconsider direct targets using `AttackRetargetInterval`.
- Ignore suspicious sound and bullet-impact investigation stimuli while the current survivor target is alive.
- Apply melee damage inside `ZombieStats.AttackRange`.
- If the target dies and no other direct target exists, switch to Idle.
- If the living target escapes detection, investigate its last known position immediately.

Zombies do not need cover checks, weapon logic, strafing, or tactical range behavior.

### Hunting

Hunting begins in overtime.

Rules:

- Ignore normal detection gating for the *global* target.
- On first acquisition, pick a random player that still owns at least one alive survivor and **commit** to that player, then target that player's closest survivor. Picking the player first (rather than the globally nearest survivor) shares overtime pressure evenly between players regardless of how their survivors are spread across the map. A player hiding survivors in a corner no longer offloads their share of the horde onto better-positioned teams. Even sharing comes from each newly spawned zombie's random commitment, not from constant re-rolling.
- **Stay committed to that player.** Periodic retargets only re-pick the closest survivor *of the committed player* (so the zombie follows the nearest as survivors move or die); they do **not** re-roll the player. Re-roll the player only once the committed team has no alive survivors left (then commit to a new random player). Re-rolling the player every interval made in-transit zombies flip target teams mid-journey, so a horde caught between two holed-up teams oscillated back and forth and never arrived.
- Exclude neutral survivors from the global pick. They are not owned by a player, so counting them would unbalance pressure. They are still attacked through normal sensing (next rule).
- A directly sensed enemy always overrides the committed target. If a hunting zombie senses any survivor on the way to its assigned target — neutral survivors included — it attacks that survivor instead. This keeps neutrals threatened and stops zombies from walking past a survivor they can clearly see. The committed player is left untouched, so the zombie resumes heading to its team once the sensed enemy is killed or lost.
- Stagger the first acquisition so the horde does not scan for survivors on one tick. A target that dies mid-hunt re-picks immediately without waiting for the stagger.
- Add interval jitter so the horde does not permanently synchronize.
- Keep the current target between checks unless it dies or a closer enemy is sensed.
- Move through the shared explicit-goal routing policy.
- Use overtime stats.
- Ignore alerts because all zombies already have hunting knowledge.

## Explicit-Goal Routing

Investigating, Attacking, and Hunting all have a concrete world-space goal. Normal walking still uses `CharacterNavigator` and NavMesh corners. Climbing is not represented as NavMesh links or special NavMesh areas. Zombies use registered **climb surfaces** for terrain/building-style traversal, plus a separate road-only direct chase mode for nearby visible survivors.

How it works:

1. **Normal route first for non-road goals.** The navigator calculates the ordinary walking path to the explicit goal. If walking is the best option, the zombie follows path corners exactly like before. NavMesh corner advancement uses full 3D distance so vertically stacked switchback-ramp corners are not skipped from the floor below. The navigator's close-range reached check also requires a clear NavMesh segment, so it keeps routing around railings and platform dividers instead of stopping through them.
2. **Terrain ledge shortcuts.** `ZombieOrchestrator` builds broad climbable face segments from `WorldHeightSnapshot` for every non-walkable ledge. When the direct line to the goal crosses one of those generated faces and the normal NavMesh path is at least `TerrainClimbShortcutMinPathSavings` metres longer than direct movement, the zombie ignores the ramp detour and takes the generated ledge face instead. While still outside `ClimbSurfaceEngageDistance`, it closes on the direct-line approach point at that face; once close enough, it climbs or drops through the face toward the survivor/goal. This applies to investigation targets too, so sound and bullet-impact stimuli can still pull zombies over ledges instead of sending them across the map to the nearest ramp. Walkable ledges and ramp road tiles are skipped because ordinary NavMesh already handles them.
3. **Road direct chase.** When a zombie has a directly sensed survivor target, or an explicit visible investigation/last-known goal, the goal resolves to a road cell in `WorldGridSnapshot`, and flat distance is within `RoadDirectMaxDistance`, the zombie ignores path corners and walks directly toward it. The zombie itself does not have to be on a road cell, so it can step from a nearby prop/navmesh island back into street-prop chase behavior. The direct sensor hit is trusted as the visibility gate; non-sensed explicit points still use a world ray, so a blocker such as a pole or wall prevents road-direct and lets normal pathing go around. In this road-only mode it may climb local blockers such as cars, trucks, crates, and street fence lips up to `RoadDirectMaxObstacleHeight` metres. Before committing to a road-direct mantle, the AI rejects landings that already have an efficient normal NavMesh path from the zombie. This keeps ramps, slopes, and other walkable height transitions as ordinary walking instead of short climb/mantle snaps. A candidate street-obstacle landing must either be on the same support object as the goal or make flat-distance progress toward the goal, which prevents zombies from vaulting onto a wrong smaller car that is farther from the survivor. If the zombie is already stranded on a different prop support than the goal, candidate landings on that wrong support are rejected so it first walks/drops toward the useful ground or target prop. If the zombie becomes higher than the goal by more than `RoadDirectClimbMaxZombieHeightAboveTarget`, road direct chase keeps walking but stops climbing, so it drops back down instead of living on the prop.
4. **Rescue climbs.** Props and buildings can register climb surfaces through `ZombieClimbableSurface`. These are normally `Rescue` surfaces: zombies ignore them during ordinary movement, but once an explicit survivor target is elevated and the navigator has already reached the closest reachable ground (or reports the target unreachable), the zombie may climb a nearby registered face whose top height matches the target within `RescueLandingHeightTolerance` and whose landing point is within `RescueLandingFlatTolerance` of the target. Use these for props on building/ledge tiles where street direct chase is intentionally unavailable.
5. **Explicit-goal stuck fallback.** If a zombie is already stranded off NavMesh or on a tiny elevated NavMesh island and has an explicit goal (survivor, overtime target, sound, or bullet-impact investigation), it first checks whether a complete NavMesh path to the goal exists. A complete route always wins, even on a small elevated platform. Only when no complete path exists does it walk directly toward the goal to step/drop back to useful ground and re-evaluate through the normal road/surface/path rules. If the goal has no useful flat direction, it falls back to the idle random unstuck direction.
6. **Idle stuck fallback.** If there is no visible survivor and an idle zombie is off NavMesh or on a tiny elevated NavMesh island (`StuckSmallIslandRadius`), it walks in a random direction for a short time. This is deliberately simple: it is just a way to fall off a prop and return to normal thinking.

If no generated or prefab-authored climb surfaces exist, zombies still use ordinary NavMesh walking and melee. Street props are handled by road direct chase only when there is a nearby visible survivor.

### Authoring contract (no cheese)

A zombie reaches a survivor given that every survivor-reachable elevated place has either ordinary NavMesh access, a registered climb surface whose landing height matches that place, or is a street obstacle within road direct chase range and visibility. Terrain ledges are automatic. Reusable tall props and buildings still opt in once at the prefab/root level with `ZombieClimbableSurface`; ordinary street cars/crates/fence lips are expected to be handled by road direct chase without per-prop authoring.

### Surface / authoring setup

- Terrain ledge climb surfaces are generated automatically from `WorldHeightSnapshot` by `ZombieClimbSurfaces.BuildTerrain`, called by `ZombieOrchestrator.RefreshClimbSurfaces`. The generated face sits on the ledge tile center line where the low and high halves meet; corner ledges generate two half-length faces for their actual exposed sides.
- Walkable ledges (stairs / gentle ramps flagged `AllowsTraversalWithoutRoad`) get no climb surface because both factions can already walk them.
- Perch props such as crates, dumpsters, fences, balconies, and roof access points on building/ledge tiles can add `ZombieClimbableSurface` on the reusable prefab root when they need reliable rescue climbing. Street cars/trucks normally do not need this component.
- Roof-capable building prefabs should add `ZombieClimbableSurface` on the building root if players can reach the roof and zombies must be able to follow. Keep those surfaces `Rescue` unless the building exterior should also be used as a normal shortcut.
- `ZombieClimbableSurface` builds four climb faces from child collider bounds by default. If a prefab's collider bounds are too broad or too narrow, enable `UseManualLocalBounds` and size the local box to the climbable volume.

Configurable terrain-surface knobs live on `ZombieOrchestrator` (`BuildTerrainClimbSurfaces`, `TerrainClimbShortcutMinPathSavings`, `TerrainClimbSurfaceWidthFactor`, `TerrainClimbLandingInset`). Runtime behavior knobs live on `ZombieAI` (`IdleWanderNavMeshSampleDistance`, `IdleWanderStuckTimeout`, `IdleWanderStuckMinProgress`, `UseClimbSurfaces`, `UseTerrainShortcutClimbs`, `UseRescueClimbs`, `ClimbSurfaceEngageDistance`, `ClimbDirectApproachDistance`, `ExplicitGoalClimbRefreshInterval`, `ClimbRouteSideTolerance`, `ClimbMinRise`, `RescueMinTargetHeight`, `RescueLandingHeightTolerance`, `RescueLandingFlatTolerance`, `ClimbMaxDuration`, `ClimbMantleMaxSnapHeight`, `ClimbMantleMaxHorizontalSnapDistance`, `ClimbSpeedMultiplier`, `ClimbCommitDuration`, `UseRoadDirectMovement`, `RoadDirectMaxDistance`, `RoadDirectMaxObstacleHeight`, `RoadDirectClimbMaxZombieHeightAboveTarget`, `RoadDirectClimbMinRise`, `RoadDirectClimbProbeDistance`, `RoadDirectClimbMaxHeight`, `RoadDirectClimbLandingInset`, `RoadDirectClimbMinSurfaceNormalY`, `RoadDirectWalkableLandingSampleDistance`, `RoadDirectWalkableLandingPathTolerance`, `StuckSmallIslandRadius`). `ClimbDirectApproachDistance` limits nearby rescue climb searches, while terrain shortcut ledges are searched along the full direct segment to the explicit goal. `ExplicitGoalClimbRefreshInterval` throttles the expensive surface scan / approach-validation step per zombie and is jittered so hordes do not all refresh on the same tick. `TerrainClimbLandingInset`, `ZombieClimbableSurface.LandingInset`, and `RoadDirectClimbLandingInset` control how far inward the mantle endpoint is placed.

## Climb Execution

Once `BuildExplicitGoalMoveInput` commits to a registered climb surface or road direct chase commits to a street-obstacle mantle, `ZombieAI.WantsToClimb` drives the ascent and `ZombieCharacter` turns it into movement:

- While climbing, `ZombieCharacter` disables gravity, scales horizontal speed by `ClimbSpeedMultiplier`, and applies an upward KCC impulse. The zombie presses horizontally into the cliff face and rises.
- The climb goal is the surface landing point: the direct-route contact point projected to the surface top and inset slightly onto the landing side. Once the zombie is within `ClimbMantleMaxSnapHeight` vertically and `ClimbMantleMaxHorizontalSnapDistance` horizontally, it mantles directly onto that point.
- `ZombieCharacter.MantleTo` spreads the hoist over `MantleAnimationDuration` (default `0.25s`, ease-out cubic) so the body visibly clambers up instead of teleporting. `MantleAnimationDuration = 0` reverts to the instant `KCC.SetPosition` behaviour.
- A short commit timer (`ClimbCommitDuration`) keeps the climb impulse from flickering off across a state transition that happens mid-climb (Investigating -> Attacking when the zombie crests the ledge and finally sees the survivor, for example). Resetting it on the state change would drop the zombie back below the ledge and loop.
- While actively climbing, investigation stimuli from shots/noise do not clear the current climb. The zombie finishes the ledge commitment first, then resumes normal target evaluation.
- While Attacking or Hunting, a zombie already inside `ZombieStats.AttackRange` holds position and faces the target during attack cooldown instead of pushing forward, so a packed crowd does not try to scale the wall behind a survivor it can already hit. This hold is skipped for a target above the zombie that still requires climbing. A zombie actively climbing can melee a survivor directly above it (capped by the larger of attack range and `ClimbMantleMaxSnapHeight`) without canceling the climb, so a survivor on the ledge lip cannot make it hang helplessly below.

A full terrain step is `HeightLevelWorldUnits` tall; the zombie scales most of it by sustained climb impulse against the cliff face, then mantles the last part onto the surface landing point.

Useful movement / climb tunables (`ZombieAI`):

```text
StoppingDistance
DirectMoveDistance
ReachablePointSampleDistance
IdleWanderNavMeshSampleDistance
IdleWanderStuckTimeout
IdleWanderStuckMinProgress
ExplicitGoalStoppingDistance
ExplicitGoalHeightTolerance
UseClimbSurfaces
UseTerrainShortcutClimbs
UseRescueClimbs
ClimbSurfaceEngageDistance
ClimbDirectApproachDistance
ExplicitGoalClimbRefreshInterval
ClimbRouteSideTolerance
ClimbMinRise
RescueMinTargetHeight
RescueLandingHeightTolerance
RescueLandingFlatTolerance
ClimbStuckTimeout
ClimbMaxDuration
ClimbCooldown
ClimbSpeedMultiplier
ClimbCommitDuration
ClimbMantleMaxSnapHeight
ClimbMantleMaxHorizontalSnapDistance
UseRoadDirectMovement
RoadDirectMaxDistance
RoadDirectMaxObstacleHeight
RoadDirectClimbMaxZombieHeightAboveTarget
RoadDirectClimbMinRise
RoadDirectClimbProbeDistance
RoadDirectClimbMaxHeight
RoadDirectClimbLandingInset
RoadDirectClimbMinSurfaceNormalY
RoadDirectWalkableLandingSampleDistance
RoadDirectWalkableLandingPathTolerance
StuckSmallIslandRadius
```

`ExplicitGoalStoppingDistance` is a final-goal/attack-position tuning value. While following a NavMesh path, `ZombieAI` clamps the steering-corner stop distance below `CharacterNavigator.CornerReachDistance`, so a zombie does not stop just short of a corner the navigator has not advanced yet (which otherwise looks like it is staring through a wall until another zombie bumps it).

Recommended first-pass defaults:

```text
StoppingDistance: 1.35
DirectMoveDistance: 0.25
ReachablePointSampleDistance: 6
IdleWanderNavMeshSampleDistance: 8
IdleWanderStuckTimeout: 3
IdleWanderStuckMinProgress: 0.25
ExplicitGoalStoppingDistance: 0.2
ExplicitGoalHeightTolerance: 0.75
UseClimbSurfaces: true
UseTerrainShortcutClimbs: true
UseRescueClimbs: true
ClimbSurfaceEngageDistance: 1.25
ClimbDirectApproachDistance: 18
ExplicitGoalClimbRefreshInterval: 0.2
ClimbRouteSideTolerance: 1.5
ClimbMinRise: 0.75
RescueMinTargetHeight: 0.75
RescueLandingHeightTolerance: 2.5
RescueLandingFlatTolerance: 2.5
ClimbStuckTimeout: 1.5
ClimbMaxDuration: 15
ClimbCooldown: 2
ClimbSpeedMultiplier: 0.5   (climb rises at MoveSpeed x this; raise for a faster climb)
ClimbCommitDuration: 0.75
ClimbMantleMaxSnapHeight: 2.0
ClimbMantleMaxHorizontalSnapDistance: 1.5
UseRoadDirectMovement: true
RoadDirectMaxDistance: 18
RoadDirectMaxObstacleHeight: 5
RoadDirectClimbMaxZombieHeightAboveTarget: 0.75
RoadDirectClimbMinRise: 0.2
RoadDirectClimbProbeDistance: 1.25
RoadDirectClimbMaxHeight: 2.25
RoadDirectClimbLandingInset: 0.75
RoadDirectClimbMinSurfaceNormalY: 0.45
RoadDirectWalkableLandingSampleDistance: 1
RoadDirectWalkableLandingPathTolerance: 0.75
StuckSmallIslandRadius: 4
```

Terrain surface generation defaults (`ZombieOrchestrator`):

```text
BuildTerrainClimbSurfaces: true
TerrainClimbShortcutMinPathSavings: 4
TerrainClimbSurfaceWidthFactor: 0.9 (x TileSize)
TerrainClimbLandingInset: 0.75
```
## Idle Off-NavMesh Recovery

A zombie can finish direct traversal on a prop and then lose its goal. Idle retains one small cleanup fallback:

1. Periodically sample NavMesh at the zombie's current position using `StuckSampleRadius`.
2. Treat the zombie as elevated when the sample fails, the sampled point is more than `StuckMinHeightAboveNavMesh` below it, it is on a tiny elevated NavMesh island, or it is standing on a small support collider such as a car/truck/prop top.
3. While elevated and Idle, move in a random horizontal direction for `StuckRandomWanderDurationMin..Max`; if still elevated, keep choosing new random directions until it falls back to normal ground.
4. Resume ordinary idle wandering once the zombie returns to NavMesh-covered ground.

Recommended defaults:

```text
StuckSampleRadius: 0.6
StuckMinHeightAboveNavMesh: 0.6
StuckCheckInterval: 0.5
StuckRandomWanderDurationMin: 1
StuckRandomWanderDurationMax: 2.5
```

This is intentionally idle-only. Zombies with an explicit goal use the explicit-goal routing policy described above instead.

## Alerting

Zombie alerts mirror survivor investigation alerts:

- A zombie that notices a suspicious sound or survivor can alert nearby zombies.
- Alerted zombies receive the same point as an investigation goal.
- Alerted zombies do not re-alert others from that alert.
- Alerts are local only, not global broadcasts.

Suggested tunables:

```text
AlertRadius
MaxAlertRecipients
AlertCooldown
```

`AlertRadius` comes from the orchestrator's difficulty curve. Early zombies alert a small area. Late-match zombies form larger local hordes.

## Attack Model

First-pass attacks are simple melee:

- Face the target inside attack range.
- Apply damage on state authority after cooldown.
- Keep animation events visual-only for now.
- Normal melee uses full 3D distance from the zombie's transform to the survivor's transform.
- While actively climbing, allow the same horizontal melee range against a survivor directly above the zombie. The vertical allowance is capped by the larger of normal attack range and `ClimbMantleMaxSnapHeight`. This prevents a survivor standing on the ledge lip from making the zombie hang helplessly below them.

Suggested tunables:

```text
ZombieStats.AttackRange
ZombieStats.AttackCooldown
ZombieStats.Damage
```

## Animation Hooks

`ZombieAnimator` is a non-networked visual bridge. It reads replicated KCC and health state plus local AI attack events.

Default Animator parameters:

```text
MoveSpeed: float
IsAlive: bool
IsGrounded: bool
Attack: trigger
```

Recommended controller setup:

- Drive Idle and Walk from `MoveSpeed`.
- Trigger Attack with `Attack`.
- Enter Death when `IsAlive == false`.
- Do not allow `Any State -> Death` to transition to itself, or the death clip restarts forever.

## Network Model

Zombie AI runs only on state authority.

Network only the result needed by clients:

- transform/KCC state,
- health/death state,
- stats if clients need them,
- minimal visual state if required later.

Do not network:

- sensor memories,
- investigation targets,
- route targets,
- path corners,
- alert recipient lists,
- per-frame AI decisions.

## Performance Rules

Zombies are expected to exist in much larger numbers than survivors.

Keep zombie AI cheap:

- sensor intervals slower than survivors,
- short vision range,
- capped sensor memories,
- no cover checks,
- no weapon logic,
- cached explicit-goal routing,
- cached and jittered climb-surface decisions (`ExplicitGoalClimbRefreshInterval`),
- cached character separation direction (`CharacterSeparation.RefreshInterval`),
- staggered target updates,
- one-hop capped alerts,
- no path calculations while Idle unless wandering,
- future movement LOD for distant zombies if needed.

The future target scale is hundreds of zombies. Avoid adding anything that is `O(zombies * characters)` every tick without throttling.

## Relationship To Survivors

Zombies reuse:

- `CharacterSensor`,
- `CharacterSensorEvents`,
- `CharacterNavigator`,
- `CharacterSeparation`,
- `Health`,
- faction/team helpers.

Zombies do not reuse:

- survivor possession,
- survivor inventory,
- survivor map selection,
- survivor AI command service,
- survivor non-combat assignments,
- survivor combat cover/weapon behavior.
