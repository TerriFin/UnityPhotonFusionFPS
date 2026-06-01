# Zombie AI

## Goal

Zombies are simple hostile characters designed to scale to high counts. They should be much cheaper than survivors while still creating pressure, reacting to noise, spreading through the map, and attacking any non-zombie survivor they can reach.

The global spawn/difficulty system is documented in `Docs/ZombieOrchestrator.md`.

## Design Principles

- Zombies are not survivors with fewer buttons.
- They should not use the full `Survivor` component unless it becomes clearly necessary.
- They should use shared reusable systems where useful: `CharacterSensor`, `CharacterNavigator`, `CharacterSeparation`, health, faction/team filtering, and Fusion spawning.
- They should run AI only on state authority.
- They should keep networked state minimal.
- They should favor cheap, believable behavior over expensive clever behavior.

## Suggested Components

Suggested zombie prefab components:

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
Animator / simple visual controller as needed
```

`ZombieCharacter` is the survivor substitute. It should own zombie-specific stats and movement/combat integration without carrying survivor inventory, player ownership, possession, map selection, team commands, weapon switching, or survivor UI logic.

Suggested split:

```text
ZombieCharacter
-> networked identity, health, stats, KCC movement entry point

ZombieAI
-> state machine and target choice

ZombieCharacter movement
-> converts AI input into KCC movement

ZombieAnimator
-> reads zombie movement/state and updates Animator parameters
```

The first implementation folds the motor into `ZombieCharacter` to keep the prefab/component count low. The important rule is that zombies should not inherit all survivor behavior by accident.

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

- All alive zombies receive overtime stats immediately.
- Existing zombies preserve their current health percentage when their max health changes. They do not heal to full unless they were already at full health.
- Newly spawned zombies receive overtime stats.
- Zombies switch to hunting behavior.

## Faction Rules

Zombies treat all non-zombie living characters as enemies.

Expected first-pass enemy types:

- player-owned survivors,
- neutral recruitable survivors later,
- possibly other survivor-like NPC factions later.

Zombies should not attack other zombies.

Zombie-vs-zombie phasing/separation should use `CharacterSeparation`:

- zombie vs zombie: no hard collision, light separation,
- zombie vs survivor: hard collision and attack behavior,
- survivor vs zombie: hard collision.

## Sensor Setup

Zombies use `CharacterSensor` with zombie-tuned values.

Suggested starting values:

```text
ProximityAwarenessRadius: 2m
NoiseAwarenessRadius: 20m
BulletImpactAwarenessRadius: 0m or low
VisionDistance: 12m
VisionAngle: 70 degrees
SensorInterval: 0.25-0.5s
MaxKnownEntries: 4
```

Zombies should react strongly to noise and less strongly to precise vision than survivors. This lets gunfire pull zombies through the city without making every zombie run expensive vision checks every frame.

## Behavior States

Zombies have four high-level behaviors:

```csharp
public enum EZombieAIState
{
	Idle,
	Investigating,
	Attacking,
	Hunting
}
```

Suggested behavior timing tunables:

```text
IdleWanderIntervalMin
IdleWanderIntervalMax
AttackRetargetInterval
HuntingRetargetInterval
HuntingRetargetIntervalJitter
HuntingInitialRetargetStaggerMax
```

Recommended defaults:

```text
IdleWanderIntervalMin: 10s
IdleWanderIntervalMax: 14s
AttackRetargetInterval: 2.5s
HuntingRetargetInterval: 5s
HuntingRetargetIntervalJitter: 1s
HuntingInitialRetargetStaggerMax: 3s
```

These intervals are intentionally coarse. Zombies should not constantly reconsider every possible target. They should mostly commit to their current action, then periodically reconsider.

### Idle

Idle zombies have no target and no suspicious point.

Rules:

- Do not stand perfectly still forever.
- Intermittently choose a nearby reachable wander point.
- Prefer wander points that move away from nearby zombies.
- Use `CharacterSeparation` only as the close-range anti-overlap layer.
- If they see or detect a survivor, switch to attacking.
- If they hear a gunshot/noise, switch to investigating.

Idle spreading should slowly diffuse zombie clusters through the map. If many zombies spawn or gather in one corner, they should naturally spill into nearby alleys, roads, and rooms over time even before they hear a sound.

Suggested idle spread behavior:

1. Wait for a random idle interval, default around `10-14s`.
2. Sample a small number of nearby candidate points.
3. Score candidates by distance from nearby zombies, with a small random term.
4. Pick the best reachable point.
5. Move there with `CharacterNavigator`.
6. Return to idle and repeat.

The first idle wander should happen quickly after spawn so zombies do not look frozen. Later wander attempts can use the longer idle interval. If no reachable wander point is found, retry after a short delay instead of waiting the full idle interval.

This is not full crowd simulation. It should be much cheaper than survivor assigned-area patrol:

- run only every few seconds per idle zombie,
- stagger the interval per zombie,
- sample only a small number of candidates,
- check nearby zombies through a cheap registry or spatial bucket,
- avoid pathfinding every frame,
- keep `CharacterSeparation` for immediate overlap cleanup.

### Investigating

Investigation is almost identical in shape to survivor investigation, but with zombie-specific return behavior.

Trigger examples:

- hears a gunshot,
- receives an alert from a nearby zombie,
- loses a survivor that is still alive,
- reaches an old combat target's last known position.

Rules:

- Resolve the suspicious point to reachable NavMesh.
- Move toward it with `CharacterNavigator`.
- Alert nearby zombies to the same point.
- Alerts are one-hop only; alerted zombies do not re-broadcast.
- If a survivor is seen during investigation, switch to attacking.
- If the zombie reaches the point and finds nothing, switch to idle at that location.
- Do not return to the original idle position.
- Do not periodically retarget while investigating. Investigation follows one point of interest unless direct survivor detection interrupts it.

This is intentionally different from survivor investigation. Survivors return to orders. Zombies just drift and repopulate the city from where the investigation ended.

### Attacking

Attacking is active when the zombie has a visible/proximity survivor target.

Rules:

- Choose a target, normally the closest direct survivor.
- Move toward the target using `CharacterNavigator` when pathing is needed.
- Attack movement must use `CharacterNavigator` steering. Zombies should not fall back to raw straight-line chasing when a path/steering target cannot be produced.
- Alert nearby zombies to the target or target position.
- Periodically re-check known direct targets, default every `2.5s`.
- Move toward a cached reachable NavMesh point near the sensor-confirmed last known target position, not the target's raw transform.
- The cached attack move point must be close enough to the survivor and close enough in height to be a plausible melee position.
- Refresh that reachable attack destination only every `ZombieAI.AttackDestinationRefreshInterval` seconds.
- If no reachable attack destination can be found, look at the target but do not move directly into intervening geometry.
- If the target is on a low unreachable prop, such as a car with no NavMesh on top, path to the closest reachable ground beside it and climb when close enough.
- If the zombie reaches its cached attack move point but still cannot attack, immediately discard that point. If it is still aware of a valid target, resolve a new attack move point; if it is not aware of any target, return to idle.
- If inside attack range and cooldown is ready, deal damage.
- If the target dies, choose another known target or switch to idle/investigate.
- If direct vision/proximity is lost and the target is not dead, immediately investigate the last known position.

Zombies do not need cover, weapon logic, strafing, or tactical range behavior.

Attack retargeting should only consider targets the zombie is currently aware of through direct sensor proximity/vision. It should not perform a global survivor search during normal attacking, and it should not keep tracking a survivor's live position after the survivor has escaped behind blocking geometry.

Useful attack movement tunables:

```text
DirectMoveDistance
ReachablePointSampleDistance
AttackMoveStoppingDistance
AttackMoveTargetMaxDistanceFromTarget
AttackMoveTargetMaxHeightDifference
AttackDestinationRefreshInterval
CanClimbUnreachableTargets
ClimbSpeedMultiplier
ClimbApproachMaxDistanceFromTarget
ClimbStartDistance
ClimbMinHeightDifference
ClimbMaxHeightDifference
ClimbMantleSnapDistance
ClimbMantleHeightTolerance
ClimbMantleForwardDistance
ClimbMantleProbeHeight
ClimbMantleProbeDistance
ClimbMantleMinSurfaceNormalY
```

`DirectMoveDistance` is still used by non-attack path fallback behavior, but attack movement is intentionally stricter. `AttackMoveStoppingDistance` is the distance from the resolved NavMesh attack move point where path movement can stop; it should stay small because `AttackRange` is checked separately by `ZombieCharacter.TryAttack()`. `AttackMoveTargetMaxDistanceFromTarget` prevents zombies from choosing a technically reachable point that is still too far from the survivor to attack from. `AttackMoveTargetMaxHeightDifference` prevents zombies from accepting a point below/above the survivor, such as the bottom of a ledge or beside a car the survivor is standing on.

Climbing is a first-pass pressure behavior for props that players can jump onto but zombies cannot path onto. It does not replace normal pathfinding. The zombie first resolves a reachable approach point near the target. If that point is within `ClimbApproachMaxDistanceFromTarget` horizontally but too low vertically, and the height gap fits the climb min/max range, the zombie moves beside the object and then climbs upward once it is within `ClimbStartDistance` of the approach point. Climb movement uses `ClimbSpeedMultiplier` of normal movement speed, and `ZombieCharacter` routes the upward motion through the KCC jump/impulse path while climb gravity is disabled. This is intended for cars and small props, not tall rooftops unless the max height and approach distance are deliberately increased.

The mantle settings finish the last lip of the climb. When a climbing zombie is close enough to the target horizontally and almost at the target height, it probes a short distance forward/down for a mostly flat surface. If found, the authoritative `ZombieCharacter` snaps the KCC to that surface and immediately re-evaluates attack/pathing from there. This prevents zombies from hanging on prop edges forever when the KCC cannot naturally crest the lip.

### Hunting

Hunting begins in overtime.

Rules:

- Ignore normal "do I know about an enemy?" gating.
- Periodically choose the nearest alive survivor globally, default around every `5s`.
- Stagger the first hunting target acquisition so every zombie does not scan the full survivor list on the same tick when overtime begins.
- Add small retarget interval jitter so large hordes do not all refresh targets together forever.
- Keep chasing the current hunting target between retarget checks unless it dies or becomes invalid.
- Move toward that survivor.
- If the target dies, pick the next nearest alive survivor.
- Use overtime stats.
- Ignore alert logic; all zombies already have hunting knowledge.

Hunting should still respect NavMesh/pathfinding. "Always knows the nearest survivor" does not mean teleporting or walking through unwalkable geometry.

## Alerting

Zombie alerts mirror survivor investigation alerts:

- A zombie that notices a suspicious sound or survivor can alert nearby zombies.
- Alerted zombies receive the same investigation/attack target position.
- Alerted zombies do not re-alert others from that alert.
- Alerts are local only, not global broadcasts.

Suggested tunables:

```text
AlertRadius
MaxAlertRecipients
AlertCooldown
```

`AlertRadius` is assigned from the orchestrator's current difficulty curve. Early zombies alert only a small area around themselves. Late-match zombies alert a much wider area so local fights can pull together larger hordes.

`MaxAlertRecipients` and `AlertCooldown` are important for scale. If 200 zombies hear one event, they should not all run alert loops on the same tick.

## Movement Model

Zombies should use the same general movement shape as survivors:

```text
AI decides desired target
CharacterNavigator provides path corner
ZombieCharacter emits KCC movement
KCC replicates final movement through Fusion
```

Do not use `NavMeshAgent` to move zombie transforms unless the project deliberately creates a separate non-KCC zombie movement model later.

Suggested pathing rules:

- Repath less often than survivors, for example `0.4-0.8s`.
- Stagger repath times by spawn index or random offset.
- Use direct movement only when extremely close to the target through `ZombieAI.DirectMoveDistance`.
- Use `ZombieAI.AttackDestinationRefreshInterval` to control how often a moving survivor target is resolved into a new reachable chase point.
- Do not calculate paths while idle unless a real movement target exists.
- Consider future movement LOD for far-away zombies.

Zombies can still get stuck if the NavMesh is valid for the baked agent but the KCC capsule cannot physically pass the same corner/gap. The zombie AI currently does not run a local stuck-recovery detour for NavMesh-walkable seams. If many zombies stick to props, ramps, or ledge seams, first check whether the baked NavMesh, generated colliders, and KCC capsule agree about what space is actually walkable.

## Off-NavMesh Stuck Recovery

Zombies that climb onto a prop the NavMesh does not cover (cars, crates, small structures) cannot pathfind off it. The behavior splits by AI state so zombies finish climbs and keep pressuring targets they can still reach, but eventually leave perches once they have nothing to do there.

Idle state (no target, no investigation):

1. Periodically sample the NavMesh at the zombie's current position using `NavMesh.SamplePosition` with `StuckSampleRadius`.
2. The zombie is treated as elevated/stuck when either the sample fails or the sampled NavMesh point is more than `StuckMinHeightAboveNavMesh` below the zombie.
3. While stuck, the zombie picks a random horizontal direction, clears any cached attack-move target and navigator destination, and moves in that direction for `StuckRandomWanderDurationMin..Max` seconds.
4. A new random direction is chosen each time the previous wander duration expires while still stuck.
5. As soon as the zombie returns to NavMesh-covered ground (sample succeeds and the height difference is within tolerance), the state machine resumes and normal idle wandering takes over.

Attacking and Hunting state (has a target):

- The elevated stuck check does NOT override active combat. Mid-climb zombies finish their climb, and zombies that successfully mantled onto a prop can keep attacking targets they can already reach from up there.
- If the zombie has a target but every navigator query and climb cache fails AND it is currently elevated off the NavMesh, it falls back to a direct horizontal move toward the target instead of just looking at it. This walks the zombie off the perch in the direction of its target rather than picking a random direction.
- Once back on the NavMesh, normal path-based attack movement resumes.

Investigation state intentionally cannot start from an off-NavMesh position (`StartInvestigation` requires `CharacterNavigator.TryFindReachablePoint` to succeed first), so investigation never traps a zombie on a perch.

When a zombie loses direct sight of its attack target — typically because the target jumped off a ledge — the transition from Attacking to Investigating runs `TryExtendInvestigationTargetPastLedge` on the cached last-known position before calling `StartInvestigation`. The helper probes one step at a time in the zombie's approach direction (same step / probe / fall tunables as the in-state ledge drop check). If the path forward from the last-known spot is clear and there is lower geometry within `LedgeDropMaxFallHeight`, the investigation target is rewritten to that lower hit point. The zombie then enters investigation already pointed at the lower terrain, and the existing per-state ledge drop check fires on the first investigation tick, committing the zombie off the ledge instead of stalling on top.

If no drop is found in the approach direction, the investigation target stays at the original last-known position and the zombie investigates normally.

Random wander (not "move toward target") is used in idle because the goal is to fall off the prop and re-enter the navmesh, not to find a path the NavMesh has already rejected. Direct-move toward target is preferred in combat because the zombie has a meaningful direction to commit to.

Suggested defaults:

```text
ZombieAI.StuckSampleRadius: 0.6
ZombieAI.StuckMinHeightAboveNavMesh: 0.6
ZombieAI.StuckCheckInterval: 0.5
ZombieAI.StuckRandomWanderDurationMin: 1
ZombieAI.StuckRandomWanderDurationMax: 2.5
```

## Ledge Drop Shortcut

Survivors prefer to route around ledges through ramps because falling is unsafe for them. Zombies have no such concern, so when a zombie has a goal on lower terrain it should drop straight off the ledge instead of routing through the same ramp the survivor would. This shortcut runs in Attacking, Hunting, and Investigating — the three states with a concrete goal point (attack target, hunting target, or investigation point seeded by noise/bullet impact/alert).

The check runs every tick on the goal point and decides:

1. Goal must be at least `LedgeDropMinHeightDrop` below the zombie's current Y. Same-level or higher goals route normally.
2. Step forward in the goal's horizontal direction in fixed `LedgeDropStepSize` increments, up to `LedgeDropProbeDistance` total. At each step:
   - `Physics.Raycast` at chest height from the zombie to the step point must be clear (no wall). A wall ends the search — the zombie can't reach a drop through it.
   - `Physics.Raycast` straight down from just above the step point. If the hit ground is at least `LedgeDropMinHeightDrop` below the zombie's Y (and at most `LedgeDropMaxFallHeight + 1` meters away), the step is the start of a drop and the check returns true.
3. If no step finds a drop within the probe distance, the check returns false and normal NavMesh path-finding takes over.

When the check succeeds, the zombie's cached attack-move target and navigator destination are cleared and the AI emits a direct horizontal move toward the goal. The KCC then steps the zombie off the ledge, gravity handles the fall, and on landing the normal NavMesh path-finding resumes from the lower terrain.

The check is physics-only — it does not call `NavMesh.Raycast`. NavMesh raycasts depend on subtle polygon-connectivity details and don't reliably stop at cliff edges when the upper and lower NavMesh are part of the same baked mesh. The Physics ground probe is also less sensitive to NavMesh sample radii.

The physics layer mask covers both `Default` and `MapNonVisible`. Some building geometry is on the `MapNonVisible` layer so that the minimap camera can cull it; both layers must be included or the ground probe will silently miss those buildings and report no drop.

The map generator guarantees that ledges with NavMesh on top are also connected to lower-terrain NavMesh, so once the zombie has dropped it can re-acquire a path.

Survivor AI does not use this shortcut. Survivors should continue to prefer ramps; if fall damage is added later, this stays the same.

Suggested defaults:

```text
ZombieAI.LedgeDropEnabled: true
ZombieAI.LedgeDropMinHeightDrop: 1
ZombieAI.LedgeDropProbeDistance: 4
ZombieAI.LedgeDropStepSize: 0.75
ZombieAI.LedgeDropMaxFallHeight: 8
```

Current stricter movement defaults:

```text
ZombieAI.StoppingDistance: 1.35
ZombieAI.DirectMoveDistance: 0.25
ZombieAI.ReachablePointSampleDistance: 6
ZombieAI.AttackDestinationRefreshInterval: 0.5
ZombieAI.HuntingRetargetInterval: 5
ZombieAI.HuntingRetargetIntervalJitter: 1
ZombieAI.HuntingInitialRetargetStaggerMax: 3
CharacterNavigator.RepathInterval: 0.35
CharacterNavigator.CornerReachDistance: 0.75
CharacterNavigator.DestinationReachDistance: 1.35
```

## Attack Model

First-pass attacks can be simple melee.

Suggested rules:

- If target is inside `AttackRange`, face target.
- If attack cooldown is ready, apply damage on state authority.
- Do not require animation events for the first version.
- Later, animation events can time the damage moment.

Suggested tunables:

```text
AttackRange
AttackCooldown
AttackDamage
AttackYawTolerance
```

Damage should be applied by the authoritative instance only.

## Animation Hooks

Zombie animation is handled by `ZombieAnimator`, a non-networked visual bridge. It reads replicated KCC/health state and local AI attack events.

`ZombieAnimator` can be placed on the zombie prefab root or on the visual child that owns the `Animator`. It searches parent and child objects for `ZombieCharacter`, `ZombieAI`, and `Animator`, but explicit references on the component are preferred for prefab clarity.

Default Animator parameters:

```text
MoveSpeed: float
IsAlive: bool
IsGrounded: bool
Attack: trigger
```

Recommended controller setup:

- `Idle` and `Walk` are driven by `MoveSpeed`.
- `Attack` is triggered by the `Attack` trigger.
- `Death` is entered when `IsAlive == false`.
- `IsGrounded` is available if jump/fall/death transitions need it later.
- `IsAlive` should default to `true`.
- `IsGrounded` should default to `true`.
- The `Any State -> Death` transition must not transition to itself. Otherwise `IsAlive == false` keeps re-entering Death and restarts the death clip forever.

If the death animation plays constantly and the Animator parameter values do not change in play mode, the usual cause is that `ZombieAnimator` cannot find the root `ZombieCharacter` or the visual `Animator`. Check the component references first.

## Network Model

Zombie AI runs only on state authority.

Networked data should be limited to:

- transform/KCC state,
- health/death state,
- current stats if clients need prediction/visuals,
- maybe simple animation state later.

Do not network:

- sensor memories,
- investigation targets,
- alert recipient lists,
- path corners,
- region/spawn scores,
- per-frame AI decisions.

Clients should see the result of state-authority movement and attacks, not run duplicate zombie brains.

## Performance Rules

Zombies are expected to exist in much larger numbers than survivors.

Keep zombie AI cheap:

- sensor intervals slower than survivors,
- short vision range,
- cap known entries,
- no cover checks,
- no weapon logic,
- no per-frame path recalculation,
- no per-zombie physics overlap unless capped/non-alloc,
- one-hop alerts only,
- stagger AI/path updates,
- reuse buffers.

The future target scale is hundreds of zombies, so first implementation should avoid adding anything that is `O(zombies * characters)` every tick without throttling.

## Relationship To Survivors

Zombies can reuse:

- `CharacterSensor`,
- `CharacterSensorEvents`,
- `CharacterNavigator`,
- `CharacterSeparation`,
- `Health`,
- faction/team helpers when implemented.

Zombies should not reuse:

- survivor possession,
- survivor inventory,
- survivor map selection,
- survivor AI command service,
- survivor non-combat assignment logic,
- survivor combat cover/weapon behavior.

## Implementation Direction

Recommended first implementation path:

1. Add `ZombieCharacter` as the lightweight networked zombie actor.
2. Add `ZombieAI` with idle, investigating, attacking, and hunting states.
3. Add `ZombieSpawnPoint` markers and `ZombieOrchestrator`.
4. Spawn zombies through the orchestrator with start/end stat scaling.
5. Use `CharacterSensor` for survivor detection and noise response.
6. Use `CharacterNavigator` for investigation/attack movement.
7. Use simple melee attack on state authority.
8. Add overtime stat application and hunting.
9. Stress test with high zombie counts before adding advanced behaviors.

## Open Questions

- Should zombies be able to climb or use special traversal later, or should roads/ramps/building entrances remain the only movement routes? 
Answer: They should be able to drop down from ledges/other possible falls. In the future we will have fall damage that uncontrolled survivor characters refuse to jump, but zombies should not care. This does not need to be implemented yet.
- Should neutral survivors attract zombies before being recruited?
Answer: Yes. The neutral survivors in the future will be spawned in pre-set quantities to pre-set tiles where they try to hold out for as long they can. If a player survivor goes near them, they join them. Neutral survivors fighting zombies is a "natural" way for them to announce their presence and make them easier for the players to find them. It also creates a dynamic system where the neutral survivors can help even if they do not directly help you. It also makes it so that if no one gets them in time, they will be lost. This is a big incentive to actually spread out and look for them ASAP.
- Should zombie attacks block or slow survivors through collision pressure, or only through damage?
Answer: zombie attacks should only damage.
- Should different zombie types exist from the start, or should the first version use one prefab with scaled stats?
Answer: There is only one type of zombie with differing stats at the beginning.
