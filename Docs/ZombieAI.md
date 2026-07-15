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

Timing values (July 2026 knob reduction — see `Docs/OptimizationAndKnobPass2.md`): only the designer-facing
intervals remain inspector fields on `ZombieAI`; the rest are code constants carrying the tuned values.

```text
Inspector knobs:            IdleWanderIntervalMin: 3, IdleWanderIntervalMax: 7,
                            AttackRetargetInterval: 1, HuntingRetargetInterval: 6,
                            TargetSwitchDistanceFactor: 0.5
Constants (ZombieAI.cs):    HuntingRetargetIntervalJitter: 2, HuntingInitialRetargetStaggerMax: 3,
                            InvestigationCommitDuration: 5, InvestigationRetriggerDistance: 3
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
- **Re-trigger on a cadence, not full deafness.** An ongoing investigation accepts a new noise/impact/alert
  stimulus only after `InvestigationCommitDuration` (default 5 s) has elapsed since the current point was
  accepted — so two shooters cannot ping-pong the zombie within a couple of seconds, but sustained fire keeps
  re-aiming it at the newest shot every ~5 s. Exception: a zombie within `InvestigationRetriggerDistance`
  (default 3 m, flat) of its current point accepts a fresh stimulus **immediately** — that point is essentially
  spent, and this only applies while investigating (an actively chasing zombie never diverts to stimuli).
  Directly spotting a survivor overrides everything instantly, and stimuli are still ignored during any
  active climb so a retarget cannot drop the zombie off a wall.
- *Relaxed reached:* standing flat-on-top of the point while the navigator reports arrival counts as
  investigated even when the point itself hangs above reach — bullet-impact stimuli aim at the shooter's
  *muzzle*, ~1.5 m above ground, which the height-aware reached check alone would never accept (zombies froze
  on the stimulus spot before this).
- If the point is reached without finding a survivor, switch to Idle there.
- **Give up on unreachable points.** If the zombie makes no flat+vertical progress toward the point of at
  least `InvestigationMinProgress` for `InvestigationGiveUpTimeout` (10 s), it abandons the point and
  returns to Idle wandering. The clock deliberately runs even while climbing: a productive climb resets it
  through real distance improvement, a stalled climb dies via `ClimbStuckTimeout`, and only a capped
  wall-climb yo-yo (unreachable interior point) accumulates the full 10 s — exactly the case that must give
  up. This is the pre-overtime "return to wandering" guarantee; overtime Hunting has no investigation path
  and never gives up.
- Do not return to the original idle position.
- Do not periodically reconsider the investigation point.

### Attacking

Attacking is active while a zombie directly detects a living survivor.

Rules:

- Choose a direct target, normally the closest detected survivor.
- **Sticky targeting:** while the current target is still directly seen/felt, another visible survivor only
  steals the zombie's attention when its distance is below `TargetSwitchDistanceFactor` (default 0.5 — must be
  at least twice as close) of the current target's distance. Applies to both the per-tick refresh and the
  periodic `AttackRetargetInterval` reconsideration, so two shooters at similar range no longer flip the target
  every tick. "Still seen" requires a *fresh* sighting (within ~3 sensor intervals): a direct-known memory
  entry survives ~4 s after sight breaks, and holding stickiness through that window made zombies chase a
  ghost's stale position while another survivor was visibly shooting them. Once the sighting is stale — or the
  target stops being directly sensed at all — the closest sensed survivor wins as before.
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
- Keep the current target between checks unless it dies or a closer enemy is sensed. The sensed override is
  sticky the same way Attacking is: while the current target is itself directly sensed, another visible survivor
  takes over only when below `TargetSwitchDistanceFactor` of its distance.
- Move through the shared explicit-goal routing policy.
- Use overtime stats.
- Ignore alerts because all zombies already have hunting knowledge.

## Explicit-Goal Routing (V6 — reactive wall climbing)

Investigating, Attacking, and Hunting all have a concrete world-space goal. V6's rule is one sentence: **always move toward the goal — walk the NavMesh while pathfinding can get closer, and when it cannot, walk straight at the goal and climb whatever wall the body touches, capped by the roof rule.** Climbing is a *reaction to contact*, never a decision made from landing probes or reachability verdicts — every earlier inference-based design failed on exactly those (see `Docs/ZombieAIDesignHistory.md`, V1–V5). No manual components are required for anything; zombies can climb basically any surface.

Per tick, in order:

1. **Reached** (flat + height) → melee/look.
2. **Active climb continues.** Surface climbs (terrain registry) keep their landing logic; wall climbs run cap/crest/stall checks (below). The navigator stays cleared during climbs.
3. **Navigator update** (`SetDynamicDestination`/`SetDestination` + `Tick`). A one-tick path-loss transient (the navigator drops its path when a midpoint chain's last corner is consumed) is bridged by a single forced repath so it cannot masquerade as "cannot get closer".
4. **Terrain shortcut surfaces** (generated registry — deterministic data, kept verbatim from earlier versions; the one mechanism that has never failed). Ledge shortcut climbs and drop-downs, exactly as before.
5. **Path mode** — pathfinding can get closer (a usable steering corner exists): walk the corners. NavMesh routes are preferred over climbing by contract. One exception: the **near-goal vault** — within `VaultTriggerMaxDistance` (6 m) of the goal, a LOW obstacle blocking the direct line (wall ray hits at chest height, a parallel ray at vault height misses) is hopped instead of pathed around: the car or fence between the zombie and the survivor.
6. **Direct mode** — pathfinding cannot get closer, derived purely from existing navigator signals (zero new NavMesh queries): destination unreachable, no path (off-mesh start, or arrived at the nearest navigable point short of the goal), or flat-arrived at a steering corner that 3D corner advancement can never consume while genuinely off the mesh (standing on a prop above one's own path). Walk straight at the goal; on wall contact → **wall climb**.
7. **Jam reflex** — the sole never-still safety net, applied to every move input: a nonzero move input producing ~zero real velocity for `JamTimeout` (0.75 s) means the body is physically blocked → random sidestep for 1–2.5 s, then resume. Raw physics, deliberately goal-agnostic, never reset by goal/state changes (anything reset by the route-clear path is silently capped at the 1 s attack-retarget cadence).

### Wall climbs

**Trigger** (direct mode always; path mode only as the near-goal vault): one short ray at `WallProbeHeight` (0.55) in the movement direction, length `WallProbeDistance` (0.6). The hit must be a real wall (`normal.y < WallMaxNormalY` — ramps and slopes belong to the navigator) that actually blocks us (`dot(-normal, moveDir) >= WallMinFacingDot` — no grazing triggers), and the zombie must be meaningfully below the cap.

**The one cap formula** (the roof rule + the vault allowance), computed at climb start:

```text
capY = max(goal.y + AboveGoalTolerance, climbStartFeetY + VaultMaxHeight)
```

Survivor on a roof → cap sits above the roof lip → full ascent. Survivor inside floor 2 → cap = floor2 + 1.5 → window-height entries where openings exist, **never the roof**. Same-level goal beyond a car → cap = feet + 2.25 → hops it; a building face toward a same-level goal caps out harmlessly and can never be scaled.

**Movement**: `WantsToClimb` + flat input toward the goal — the body presses into the face and rises (existing `ZombieCharacter` execution, unchanged); the lateral input component slides it along the wall toward under-the-goal.

**Termination** (all geometric, per tick):
- *Roof cap*: feet ≥ capY → gravity resumes + `ClimbCooldown`, so the fall / jam-sidestep / re-climb loop shifts position instead of vibrating at the cap band. Besieging an unreachable interior target yo-yos on the wall — dumb but alive, by design; pre-overtime the investigation give-up ends it, overtime persists.
- *Crest*: the wall ray stops hitting → one forward-down probe (`CrestProbeForwardDistance`/`CrestProbeDownDistance`) finds the just-crested surface (must be standable: `normal.y >= CrestLandingMinNormalY`) → existing networked mantle. This is the only landing probe left in the system, taken from a spot the body has physically reached — it cannot start inside hollow prop shells the way ground-based landing inference did. A deep opening with no floor keeps rising through the commit grace, then drops with cooldown. Thin walls resolve to the far-side ground = a visual vault over the top.
- *Stall / duration*: the existing progress-based stuck timeout and rise-scaled duration cap, unchanged.

The investigation give-up timer is deliberately **not** paused while climbing: a productive climb resets it through real distance improvement, a stalled climb dies via `ClimbStuckTimeout`, and only the capped yo-yo accumulates the 10 s — exactly the case that must give up.

### Authoring contract (no cheese)

Walls have no hooks/overhangs — always climbable to the top. Where a NavMesh route to the target exists it is preferred over climbing (path mode owns those ticks). Survivor-reachable places need either a walking route or wall faces at-or-below the survivor's height with standable tops within crest-probe reach. No per-prop or per-building components, ever. `ZombieClimbableSurface` remains only as an optional authored *shortcut* face (usage `TerrainShortcut`).

### Surface / authoring setup

- Terrain ledge climb surfaces are generated automatically from `WorldHeightSnapshot` by `ZombieClimbSurfaces.BuildTerrain`, called by `ZombieOrchestrator.RefreshClimbSurfaces`. The generated face sits on the ledge tile center line; corner ledges generate two half-length faces for their exposed sides.
- Walkable ledges (stairs / gentle ramps flagged `AllowsTraversalWithoutRoad`) get no climb surface because both factions can already walk them.
- `ZombieClimbableSurface` builds four climb faces from child collider bounds by default (manual local bounds available). Only the `TerrainShortcut` usage does anything; `Rescue` registrations are ignored.

Configurable terrain-surface knobs live on `ZombieOrchestrator` (`BuildTerrainClimbSurfaces`, `TerrainClimbShortcutMinPathSavings`, `TerrainClimbSurfaceWidthFactor`, `TerrainClimbLandingInset`). On `ZombieAI` only the capability toggles and feel values are inspector knobs (`UseClimbSurfaces`, `UseTerrainShortcutClimbs`, `UseWallClimbs`, `ClimbSpeedMultiplier`, `ClimbMantleMaxSnapHeight`); everything else is a code constant in `ZombieAI.cs` carrying the tuned value.

## Climb Execution

Once `BuildExplicitGoalMoveInput` commits to a registered climb surface, or a reactive wall climb starts on wall contact, `ZombieAI.WantsToClimb` drives the ascent and `ZombieCharacter` turns it into movement:

- While climbing, `ZombieCharacter` disables gravity, scales horizontal speed by `ClimbSpeedMultiplier`, and applies an upward KCC impulse. The zombie presses horizontally into the cliff face and rises.
- **Surface climbs** aim at the registry landing point: the direct-route contact point projected to the surface top and inset onto the landing side. Once the zombie is within `ClimbMantleMaxSnapHeight` vertically and `ClimbMantleMaxHorizontalSnapDistance` horizontally, it mantles directly onto that point. **Wall climbs** have no landing in advance — they end by cap, crest probe + mantle, stall, or duration (see Explicit-Goal Routing).
- `ZombieCharacter.MantleTo` spreads the hoist over `MantleAnimationDuration` (default `0.25s`, ease-out cubic) so the body visibly clambers up instead of teleporting. `MantleAnimationDuration = 0` reverts to the instant `KCC.SetPosition` behaviour.
- A short commit timer (`ClimbCommitDuration`) keeps the climb impulse from flickering off across a state transition that happens mid-climb (Investigating -> Attacking when the zombie crests the ledge and finally sees the survivor, for example). Resetting it on the state change would drop the zombie back below the ledge and loop.
- **An active climb survives goal movement while the goal stays above the zombie.** Attack retargeting re-fires `StartAttack` for the *same* survivor at its new position every `AttackRetargetInterval`, and sight loss hands off to `StartInvestigation` — both clear the explicit-goal route. `ShouldPreserveActiveClimbForTarget` keeps the climb whenever the new goal is still more than a step above the zombie (a target strolling along the ledge top used to drop climbers off the wall mid-ascent, who then fell, re-approached, and climbed the same face again). Only a goal that moved *below* the zombie falls back to the old landing-proximity rule and cancels the ascent.
- While actively climbing, investigation stimuli from shots/noise do not clear the current climb. The zombie finishes the ledge commitment first, then resumes normal target evaluation.
- While Attacking or Hunting, a zombie already inside `ZombieStats.AttackRange` holds position and faces the target during attack cooldown instead of pushing forward, so a packed crowd does not try to scale the wall behind a survivor it can already hit. This hold is skipped for a target above the zombie that still requires climbing. A zombie actively climbing can melee a survivor directly above it (capped by the larger of attack range and `ClimbMantleMaxSnapHeight`) without canceling the climb, so a survivor on the ledge lip cannot make it hang helplessly below.

A full terrain step is `HeightLevelWorldUnits` tall; the zombie scales most of it by sustained climb impulse against the cliff face, then mantles the last part onto the surface landing point.

The absolute climb cap scales with the committed rise: `max(ClimbMaxDuration, rise / climbSpeed * 1.5)`. At ~1 m/s climb speed a fixed 15 s cap would truncate any ≥ ~13 m building-wall ascent mid-climb and reintroduce the historic rise/drop oscillation; the progress-based `ClimbStuckTimeout` remains the real abort signal.

Movement / climb values. Only the first group is inspector-editable on `ZombieAI`; the second group are
`private const` fields in `ZombieAI.cs` (change them in code — they carry the tuned values):

```text
Inspector knobs (defaults = tuned prefab values):
StoppingDistance: 0.66           (also the explicit hunt/attack arrival distance)
MaxYawDegreesPerTick: 6
UseClimbSurfaces: true
UseTerrainShortcutClimbs: true
UseWallClimbs: true              (was UseClimbPursuit / UseRescueClimbs; FormerlySerializedAs migrates)
ClimbSpeedMultiplier: 0.45       (climb rises at MoveSpeed x this; raise for a faster climb)
ClimbMantleMaxSnapHeight: 1.75
LogTraversalDebug: false         (debug: logs this zombie's routing gates — climb kind/cap, cooldown, jam,
                                  navigator signals — twice a second while it has an explicit goal)

Constants in ZombieAI.cs:
DirectMoveDistance: 0.75
IdleWanderNavMeshSampleDistance: 8   (also serves as the reachable-point sample distance)
IdleWanderStuckTimeout: 3
IdleWanderStuckMinProgress: 0.25
ExplicitGoalHeightTolerance: 0.75
ClimbSurfaceEngageDistance: 1.25
ClimbDirectApproachDistance: 18
ExplicitGoalClimbRefreshInterval: 0.2
ClimbRouteSideTolerance: 1.6
ClimbMinRise: 0.75
ClimbStuckTimeout: 1.5
ClimbMaxDuration: 15             (floor; the real cap scales with the committed rise, see above)
ClimbCooldown: 2
ClimbCommitDuration: 0.75
ClimbMantleMaxHorizontalSnapDistance: 1.5
WallProbeHeight: 0.55            (wall-touch ray height)
WallProbeDistance: 0.6           (wall-touch ray length — climbing engages on contact, not at range)
WallMaxNormalY: 0.45             (hits flatter than this are ramps/slopes — the navigator's business)
WallMinFacingDot: 0.5            (the wall must actually block the movement direction, no grazing triggers)
AboveGoalTolerance: 1.5          (the roof rule: never rise past goal height + this)
VaultMaxHeight: 2.25             (small-vault allowance of the cap formula; also the path-mode low-obstacle
                                  height gate)
VaultTriggerMaxDistance: 6       (path mode vaults low obstacles only this close to the goal)
CrestProbeForwardDistance: 0.6
CrestProbeDownDistance: 1.5
CrestLandingMinNormalY: 0.45     (only mantle onto standable tops)
JamVelocityThreshold: 0.15       (jam reflex: input active but flat velocity below this = physically blocked)
JamTimeout: 0.75                 (blocked this long -> random sidestep for 1-2.5 s)
SupportProbeDistance: 6          (support-collider down-ray length, used by idle stuck recovery)
InvestigationGiveUpTimeout: 10
InvestigationMinProgress: 0.25
StuckSmallIslandRadius: 4
MaxAlertRecipients: 16
AlertCooldown: 2
```

The stopping distance is a final-goal/attack-position tuning value. While following a NavMesh path, `ZombieAI` clamps the steering-corner stop distance below `CharacterNavigator.CornerReachDistance`, so a zombie does not stop just short of a corner the navigator has not advanced yet (which otherwise looks like it is staring through a wall until another zombie bumps it).

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

These are all constants in `ZombieAI.cs` (no inspector knobs):

```text
StuckSampleRadius: 0.6
StuckMinHeightAboveNavMesh: 0.6
StuckCheckInterval: 0.5
ElevatedIslandCheckInterval: 1.5
StuckRandomWanderDurationMin: 1
StuckRandomWanderDurationMax: 2.5
```

This is intentionally idle-only. Zombies with an explicit goal use the explicit-goal routing policy described above instead.

Performance note (July 2026): the "tiny elevated NavMesh island" and "complete path to goal height" questions are
now answered by the precomputed `NavMeshIslandMap` (island-id lookups; see `Docs/NavMeshIslandMap.md`) instead of
rings of `NavMesh.SamplePosition` probes plus `NavMesh.CalculatePath`. The decisions and tunables described in
this document are unchanged — only their cost dropped.

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
