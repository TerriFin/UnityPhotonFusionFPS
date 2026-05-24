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
```

Recommended defaults:

```text
IdleWanderIntervalMin: 10s
IdleWanderIntervalMax: 14s
AttackRetargetInterval: 2.5s
HuntingRetargetInterval: 2.5s
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
- Use direct movement only at short range.
- Alert nearby zombies to the target or target position.
- Periodically re-check known direct targets, default every `2.5s`.
- Keep chasing the current target between retarget checks unless it dies or becomes invalid.
- If inside attack range and cooldown is ready, deal damage.
- If the target dies, choose another known target or switch to idle/investigate.
- If the target escapes but is not dead, investigate the last known position.

Zombies do not need cover, weapon logic, strafing, or tactical range behavior.

Attack retargeting should only consider targets the zombie is currently aware of through sensor memory or proximity/vision. It should not perform a global survivor search during normal attacking.

### Hunting

Hunting begins in overtime.

Rules:

- Ignore normal "do I know about an enemy?" gating.
- Periodically choose the nearest alive survivor globally, default every `2.5s`.
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
- Use direct movement only when close to the target or when no path exists and the target is very near.
- Do not calculate paths while idle unless a real movement target exists.
- Consider future movement LOD for far-away zombies.

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
