# Zombie AI

## Goal

Zombies are simple hostile characters designed to scale to high counts. They should be cheaper than survivors while still spreading through the map, reacting to noise, attacking survivors, and creating pressure that players cannot avoid by standing on props or ledges.

The global spawn and difficulty system is documented in `Docs/ZombieOrchestrator.md`.

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
- Newly spawned zombies receive overtime stats.
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
- Use `CharacterSeparation` only as the close-range anti-overlap layer.
- If a survivor is detected, switch to Attacking.
- If suspicious sound or impact stimulus is received, switch to Investigating.

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

Investigating, Attacking, and Hunting all have a concrete world-space goal. They share one movement policy:

1. Ask `CharacterNavigator.TryFindReachablePoint(...)` for a reachable NavMesh point near the raw goal and the length of its complete route.
2. Compare the complete NavMesh route against direct distance multiplied by `DirectRouteLengthMultiplier`.
3. If that normal route is too indirect and the raw goal is within `MaxDirectTraversalDistance`, move directly toward the raw goal. This comparison happens even when NavMesh sampling landed slightly below or beside the raw goal, so a short ledge climb can beat a distant ramp route.
4. Otherwise, if the raw goal is not represented by reachable NavMesh, follow the NavMesh route to the nearest reachable approach point first, then move directly toward the raw goal once it is within `MaxDirectTraversalDistance`.
5. If no route can be built from the zombie's current position, move directly toward the raw goal only when it is within `MaxDirectTraversalDistance`.

Route decisions are cached for `ExplicitGoalRouteRefreshInterval`. Direct traversal still follows the latest goal direction, but expensive NavMesh resolution does not run every tick.

`MaxDirectTraversalDistance` is a hard leash around reckless traversal. If the straight-line world distance to the raw goal is greater than this value, the zombie will not enter direct movement or climbing. This prevents distant alerts from making zombies scale buildings across the map. A zombie may still pathfind closer to an off-NavMesh goal and begin direct traversal once it enters the allowed range.

This makes the policy configurable:

- A low `DirectRouteLengthMultiplier` makes zombies reckless and likely to climb walls or drop from ledges.
- A high value makes zombies prefer roads, ramps, and doors unless the target is genuinely unreachable.
- `MaxDirectTraversalDistance` limits how far away direct traversal may begin.
- `ReachablePointSampleDistance` controls how far around an off-NavMesh goal the approach search may snap.
- `ExplicitGoalRouteRefreshInterval` controls route resolution cost for moving targets.

## Direct Traversal And Climbing

Direct traversal is deliberately crude and threatening:

- Walking toward a lower goal naturally drops the zombie from ledges.
- A single forward raycast at approximately knee height in front of a directly moving zombie detects walls, low props, and ledge faces and enables climbing.
- Climb and mantle probes ignore colliders that belong to zombies or survivors. Packed hordes should press against each other through ordinary separation, not mistake another character for climbable geometry or hoist onto one.
- A nearby higher goal also enables climbing.
- While Attacking or Hunting, a zombie that is already inside `ZombieStats.AttackRange` holds position and faces the target during attack cooldown instead of continuing to push forward. This prevents packed zombies from trying to climb the wall behind a survivor they can already hit.
- Climb obstacle probes are capped before the current goal point. A wall behind the survivor is not treated as climbable progress toward the survivor.
- A short commitment timer prevents the climb impulse from flickering off while the zombie rises past an obstacle edge. The commit deliberately persists across state transitions (Investigating → Attacking when the zombie crests the ledge and finally sees the survivor, for example). Resetting it on the state change would cause the climb impulse to switch off the same tick the survivor becomes visible, the zombie would drop back below the ledge, re-engage the climb on the next obstacle hit, and loop.
- A forward/down mantle probe lets the authoritative KCC hoist the body onto the ledge once a valid top surface is reachable.

Climbing is not limited to cars. It is a general ability available whenever Investigating, Attacking, or Hunting chooses direct traversal.

`ZombieCharacter` converts `ZombieAI.WantsToClimb` into reduced horizontal speed, disabled gravity, and upward KCC impulse. The traversal mask includes both `Default` and `MapNonVisible`, because generated world geometry can use either layer.

Mantling rejects same-height ground so zombies near ledges do not repeatedly snap forward onto ordinary ground. The climb sequence pairs the knee-height obstacle probe with the mantle's forward/down probe:

- While the knee probe hits a wall, the zombie's knee is still below the obstacle's top edge — keep climbing.
- The mantle's forward/down probe checks for the top surface during the climb. It originates at `zombie.y + ClimbMantleMaxSnapHeight + 0.35`, casts down `ClimbMantleMaxSnapHeight + 1.1`, and accepts surfaces with `heightDifference` (the gap between the zombie's feet and the surface) in the `[0.08, ClimbMantleMaxSnapHeight]` range.

The downward probe starts generously high so it can reliably discover the top surface. Do not require the knee ray to clear completely before allowing the mantle: the KCC capsule can stall while that ray still grazes the ledge face, causing the zombie to peek over the top and fall back down without hoisting.

`ZombieCharacter.MantleAnimationDuration` (default `0.25s`) spreads the remaining displacement over multiple ticks with an ease-out cubic so the body visibly hoists onto the ledge instead of teleporting. Setting `MantleAnimationDuration = 0` reverts to the instant `KCC.SetPosition` behaviour.

Useful movement tunables:

```text
StoppingDistance
DirectMoveDistance
ReachablePointSampleDistance
ExplicitGoalStoppingDistance
ExplicitGoalRouteRefreshInterval
DirectRouteLengthMultiplier
MaxDirectTraversalDistance
ExplicitGoalHeightTolerance
CanClimbDirectGoals
ClimbSpeedMultiplier
ClimbObstacleProbeDistance
ClimbCommitDuration
ClimbMantleMaxSnapHeight
```

`ExplicitGoalStoppingDistance` is treated as a final-goal/attack-position tuning value.
While following a NavMesh path, `ZombieAI` clamps the steering-corner stop distance below
`CharacterNavigator.CornerReachDistance`. This prevents zombies from stopping just short
of a corner that the navigator has not advanced yet, which otherwise looks like the zombie
is staring through a wall until another zombie bumps it forward.

Recommended first-pass defaults:

```text
StoppingDistance: 1.35
DirectMoveDistance: 0.25
ReachablePointSampleDistance: 6
ExplicitGoalStoppingDistance: 0.2
ExplicitGoalRouteRefreshInterval: 0.5
DirectRouteLengthMultiplier: 1.5
MaxDirectTraversalDistance: 40
ExplicitGoalHeightTolerance: 0.75
CanClimbDirectGoals: true
ClimbSpeedMultiplier: 0.5
ClimbObstacleProbeDistance: 1.25
ClimbCommitDuration: 0.75
ClimbMantleMaxSnapHeight: 2.0
```

## Idle Off-NavMesh Recovery

A zombie can finish direct traversal on a prop and then lose its goal. Idle retains one small cleanup fallback:

1. Periodically sample NavMesh at the zombie's current position using `StuckSampleRadius`.
2. Treat the zombie as elevated when the sample fails or the sampled point is more than `StuckMinHeightAboveNavMesh` below it.
3. While elevated and Idle, move in a random horizontal direction for `StuckRandomWanderDurationMin..Max`.
4. Resume ordinary idle wandering once the zombie returns to NavMesh-covered ground.

Recommended defaults:

```text
StuckSampleRadius: 0.6
StuckMinHeightAboveNavMesh: 0.6
StuckCheckInterval: 0.5
StuckRandomWanderDurationMin: 1
StuckRandomWanderDurationMax: 2.5
```

This is intentionally idle-only. Zombies with an explicit goal use the general route-versus-direct policy instead.

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
