# Future Scale Optimization Notes

## Purpose

This document is a future-facing note for scaling the game beyond a few controlled characters. It is not a feature implementation plan for right now. Future work should read this before adding systems that affect survivors, zombies, AI, movement, sensors, projectiles, or network replication.

The goal is to keep future optimization paths open. New features do not need to implement all of this immediately, but they should avoid making these options harder.

## Target Pressure

The long-term design may include:

- 2-20 players.
- Multiple survivors per player, potentially far more than the current default.
- Neutral recruitable survivors.
- AI zombies that can grow in number over the match.
- Survivors and zombies reacting to combat through sensors and AI.

The risky version is: every survivor and every zombie runs full KCC movement, full sensors, full AI, full weapon logic, and full network replication every tick.

That will not scale well.

## Local Testing Caveat

Host/client testing on one machine exaggerates performance problems.

The same computer may be running:

- Unity Editor overhead.
- Host/server simulation.
- Local client prediction/interpolation.
- Rendering.
- Physics.
- Fusion resimulation/catch-up ticks.

This can look like high ping even when the real issue is local simulation falling behind. Dedicated server or separate-machine tests are needed before drawing final networking conclusions.

That said, local lag is still useful: it reveals CPU-heavy systems early.

## Core Scaling Principle

Only relevant characters should run at full fidelity.

Recommended tiers:

| Tier | Example | Movement | AI | Sensors | Network |
|---|---|---|---|---|---|
| Full | Possessed survivor, close combatant | Full KCC every tick | Every tick or near-every tick | Fast | Fully replicated |
| Active AI | Nearby follower, enemy in fight | KCC while moving/fighting | Moderate tick rate | Moderate | Replicated |
| Idle nearby | Standing teammate | Skip KCC when settled | Slow tick rate | Slow | Replicated but quiet |
| Far AI | Distant zombie group | Simplified movement | Low tick rate | Event/noise only | Reduced interest |
| Dormant | Far inactive area | No per-tick movement | Disabled or very slow | Disabled/event only | Not replicated or low priority |

## Movement Optimization Options

### Keep Full KCC For Important Characters

Full KCC is appropriate for:

- The actively possessed survivor.
- Characters close enough to interact physically with players.
- Characters in direct combat.
- Characters whose precise collision matters.

### Skip KCC For Settled Characters

Already started: inactive grounded characters with near-zero velocity can skip `KCC.Move`.

Future work should preserve this behavior. Avoid adding systems that force every idle character to move or rotate through KCC every tick.

### Movement LOD For Zombies

Zombies probably should not all use full survivor-grade KCC every tick.

Options:

- Far zombies use cheaper transform/nav-style movement.
- Zombies upgrade to full KCC only near survivors or when attacking.
- Zombie crowds move as simple agents until they enter interaction range.
- Very far zombies are simulated as spawn pressure/area threat instead of individual actors.

If zombie movement is implemented later, design it so movement backend can vary by distance/relevance.

## AI Tick Rate Optimization

AI should not assume it runs every Fusion tick.

Recommended pattern:

- Separate "decision tick" from "movement execution".
- Store current intent/order.
- Recompute expensive decisions at intervals.
- Let movement continue using the last intent.

Example intervals:

- Possessed: human input every tick.
- Nearby combat AI: `0.05-0.1s`.
- Normal follower: `0.1-0.25s`.
- Zombie horde: `0.2-0.5s`.
- Far/dormant AI: `1s+` or disabled.

Future AI classes should avoid requiring every character to think every tick.

## Sensor Optimization Options

Current `CharacterSensor` already supports `SensorInterval`.

Future improvements:

- Use spatial partitioning instead of scanning all sensors.
- Use `Physics.OverlapSphereNonAlloc` with preallocated buffers.
- Filter faction/team before raycasts.
- Do distance and FOV checks before line-of-sight checks.
- Make noise and bullet impacts event-driven.
- Use slower sensor intervals for zombies.
- Disable sensors for possessed characters.
- Disable or heavily throttle sensors for far/dormant characters.

Avoid adding sensor features that require every sensor to raycast against every character every tick.

## Network Optimization Options

### Avoid Networked AI Memory

AI perception and memory should generally not be `[Networked]`.

State authority should decide AI behavior, then Fusion replicates the resulting movement/projectiles/hits.

Only network AI state if clients need UI feedback, and then use compact state such as enums or small IDs.

### Interest Management

Eventually, not every client should receive every survivor/zombie at full priority.

Options:

- Distance-based interest.
- Area/sector-based interest.
- Combat relevance-based interest.
- Team visibility rules.
- Lower update priority for far zombies.

Future systems should avoid assuming every client always knows every AI actor.

### Minimize Dirty Networked Data

Avoid networked fields that change every tick unless necessary.

Good:

- Inputs.
- Transform/KCC state already handled by Fusion.
- Compact gameplay state changes.

Risky:

- Networked lists of known enemies.
- Networked timers for every AI thought.
- Networked random aim offsets every shot.
- Networked per-zombie debug/status strings.

## Projectile And Combat Optimization Options

Projectile simulation can become expensive with many AI shooters.

Options:

- Lower AI fire rates.
- Use burst limits per team/area.
- Use cheaper hitscan or simplified projectiles for far AI.
- Disable visual projectile work for distant combat.
- Aggregate far combat into area danger instead of individual bullets.

Future AI shooting should remain tunable and throttled. Do not assume every AI can fire as often as a human-controlled survivor.

## Data-Oriented Future Path

If character counts grow high enough, consider moving some systems away from MonoBehaviour-per-character logic.

Possible later steps:

- Central AI manager processes batches of simple agents.
- Central sensor registry/spatial grid.
- Zombie horde manager for far zombies.
- Object pooling for zombies, projectiles, and effects.
- Reduced GameObject/component count for far actors.

Do not start here prematurely, but keep future systems loosely coupled enough that this remains possible.

## Profiling Checklist

Before optimizing, measure:

- Unity Profiler CPU timeline.
- Fusion stats and resimulation/catch-up behavior.
- KCC cost per character.
- Sensor scan/raycast cost.
- Weapon/projectile simulation cost.
- Animator cost for many characters.
- Rendering/skinning cost.
- Network traffic and dirty state frequency.

Test scenarios:

- Editor host + client on same machine.
- Standalone host + standalone client on same machine.
- Dedicated/headless server + separate client.
- Multiple clients on separate machines if possible.

## Guidance For Future Features

When adding a new survivor/zombie/AI feature, ask:

- Does this require every character to update every tick?
- Can this run at a configurable interval?
- Can it be disabled for possessed, far, idle, or dormant characters?
- Does it create networked state that changes often?
- Can state authority decide it without clients duplicating the work?
- Does it rely on full KCC for all actors?
- Can it work with simplified far-away actors later?
- Does it make interest management harder?

If the answer is risky, implement the smallest version now but leave seams for LOD, throttling, or central management later.

## Current Recommendation

For the near future, keep the current full-fidelity model for:

- Possessed survivors.
- A small number of nearby uncontrolled survivors.
- Early prototype zombies.

As soon as zombie counts or team sizes increase, prioritize:

- AI tick throttling.
- Sensor spatial filtering.
- Zombie movement LOD.
- Interest management.
- Dedicated server testing outside the Unity Editor.
