# Survivor Combat AI

## Goal

Combat AI controls an unpossessed survivor while it has an active enemy threat. It decides how the survivor fights: whether to stand ground, advance, retreat, seek cover, choose weapons, prioritize enemies, and whether to chase enemies that leave vision.

Non-combat AI owns normal assignments such as idle, follow, move, pickup collection, and investigation. Combat AI temporarily interrupts those assignments and releases control when the threat is gone.

## Relationship To Existing Shooting

`SurvivorAIShooting` is the current low-level shooting helper. It can aim, apply lock-on delay, add aim error, check line of fire, and press `Fire`.

Combat AI should sit above it as the base combat controller:

- Combat AI chooses target and desired combat movement.
- Combat AI chooses weapon behavior.
- Combat AI orchestrates focused combat behavior components.
- `SurvivorAIShooting` handles imperfect aim/fire timing against the chosen target.
- `Survivor.ProcessInput(...)` still owns actual movement and weapon firing.

This keeps shooting mechanics shared with the existing weapon path instead of letting AI call weapon code directly.

## Behavior Component Pattern

Combat AI should mirror the non-combat AI architecture:

```text
SurvivorCombatAI
-> owns combat activation, current threat, and combat handoff
-> checks combat settings
-> selects one or more focused combat behaviors
-> behavior components own detailed logic and Inspector tunables
```

This is the same expandable shape used by:

- `SurvivorNonCombatAI`
- `SurvivorLootingAI`
- `SurvivorInvestigationAI`

Future combat behaviors should be separate components where practical, for example:

- weapon usage behavior,
- target priority behavior,
- zombie movement behavior,
- enemy survivor movement behavior,
- lost enemy pursuit behavior,
- cover behavior.

`SurvivorCombatAI` should orchestrate between those behaviors instead of becoming one giant script.

The first concrete behavior component is:

- `SurvivorCombatMovementAI`: owns survivor-vs-survivor combat movement, including dynamic cover scoring, spreading away from allies, and preferred weapon range movement.
- `SurvivorWeaponPreferenceAI`: owns the four-state Automatic / Prefer Strong Weapons / Prefer Pistol / Hold Fire weapon-fire choice. See `Docs/SurvivorWeaponPreferenceAI.md`.

Planned player-facing combat controls are documented separately:

- `Docs/SurvivorCombatBehaviorAI.md`: Normal / Aggressive / Defensive / None target-priority and tactical-movement profiles.
- `Docs/SurvivorRetreatAI.md`: independent percentage-based retreat-to-home-base mode.
- `Docs/HomeBaseSystem.md`: per-player map area used by retreat and future base features.

## Combat Activation

Combat AI becomes active when the survivor has a valid combat target.

Valid first-pass targets:

- Directly visible enemy from `CharacterSensor`.
- Very close enemy from proximity awareness.

Noise, bullet impacts, and approximate shooter positions are not direct combat targets by themselves. They belong to non-combat investigation unless the survivor turns and actually sees an enemy.

Combat AI releases control when:

- The current target is dead.
- No valid direct enemy remains.
- The AI shutoff behavior decides not to follow a lost target.

When combat ends, non-combat AI resumes its stored assignment.

## Settings

These settings should eventually be stored per survivor and editable through the map/RTS UI. They are player-facing behavior toggles/modes. Detailed tuning values should live on focused combat behavior components, the same way non-combat looting and investigation tuning lives on `SurvivorLootingAI` and `SurvivorInvestigationAI`.

```csharp
public enum EZombieCombatBehavior
{
    StandGround,
    AdvanceBeforeShooting,
    StandGroundRetreatIfClose
}

public enum ESurvivorCombatBehavior
{
    StandGround,
    AdvanceWhileShooting,
    TakeNearestCover
}

public enum ESurvivorWeaponPreference
{
    Automatic,
    PreferStrongWeapons,
    PreferPistol
}

public enum EEnemyPriority
{
    PreferZombies,
    PreferEnemySurvivors,
    Smart
}

public enum ELostEnemyBehavior
{
    StopWhenLost,
    FollowLastKnownPosition
}
```

Suggested settings container:

```csharp
public struct SurvivorCombatAISettings
{
    public ESurvivorWeaponPreference WeaponPreference;
    public EEnemyPriority EnemyPriority;
    public ELostEnemyBehavior LostEnemyBehavior;
}
```

Suggested defaults:

```text
WeaponPreference: Automatic
EnemyPriority: Smart
LostEnemyBehavior: StopWhenLost
```

Current implementation:

- The four-state combat behavior replaces the old combat-movement boolean: `None` is the disabled state, while `Normal`, `Aggressive`, and `Defensive` enable their respective tactical movement.
- `None` does not use tactical movement against enemy survivors or close-zombie retreat movement.
- Shooting is controlled by the independent weapon/fire mode.
- Weapon preference remains an independent four-state control.
- Like non-combat settings, combat settings are stored separately from the current assignment. Orders do not reset combat settings.

## Weapon Preference

Weapon selection and fire permission are a focused four-state behavior:

- `Automatic`: pistol against ordinary zombies, strong weapons against enemy survivors, close/large zombie threats, and overtime.
- `PreferStrongWeapons`: best range-appropriate usable weapon against every target.
- `PreferPistol`: pistol against every target.
- `HoldFire`: track direct targets but never switch weapons for AI combat or press `Fire`.

The detailed rules, Inspector thresholds, roster control, and authoritative request path are documented in `Docs/SurvivorWeaponPreferenceAI.md`.

## Target Priority

Combat AI should evaluate known direct enemies and choose one target.

Priority modes:

- `PreferZombies`: zombies rank above enemy survivors.
- `PreferEnemySurvivors`: enemy survivors rank above zombies.
- `Smart`: focus enemy survivors if no zombie is dangerously close; switch to a close zombie if it becomes an immediate threat.

Suggested ranking inputs:

- Enemy type.
- Distance.
- Whether the enemy has line of fire.
- Whether the enemy is already the current target.
- Whether a zombie is inside a danger radius.

Keep target switching slightly sticky so the survivor does not jitter between targets every sensor tick.

## Movement Behavior

Combat movement should be layered on top of ordinary `NetworkedInput`.

### Zombies

Zombie combat uses a simpler first-pass branch than enemy-survivor combat.

Regardless of combat behavior, survivors:

- Do not use cover sampling, ally spacing, or preferred weapon range movement against zombies.
- Do not move toward a zombie just because it is far away.
- Can aim and shoot from their current position when their weapon/fire mode allows it.

While combat behavior is `Normal`, `Aggressive`, or `Defensive`, survivors also:

- Back away only if the zombie enters `SurvivorCombatAI.ZombieRetreatDistance`.
- Keep aiming/firing while backing away when line of fire, weapon/fire mode, and weapon timing allow it.

If combat behavior is `None`, the survivor does not retreat from zombies for combat reasons, but can still turn toward and shoot them when its weapon/fire mode allows it.

### Enemy Survivors

`StandGround`:

- Aim and shoot from current position.
- Do not intentionally advance.

`AdvanceWhileShooting`:

- Move toward the enemy while firing when line of fire exists.
- Stop at preferred range.

`TakeNearestCover`:

- Find nearby cover and move there.
- Peek/shoot behavior can come later.

When combat behavior is `None`, none of these tactical movement modes run against enemy survivors. The survivor remains in place unless a player/non-combat order or home-base retreat moves it and may turn/shoot according to its weapon/fire mode.

The first version uses dynamic cover detection instead of requiring explicit cover markers. `SurvivorCombatMovementAI` samples a small number of reachable NavMesh points around the survivor and scores them. The best point balances:

- possible cover from the enemy,
- spreading away from nearby allies,
- staying near the preferred range of the current/best weapon,
- not moving too far from the survivor's current position.

Cover is approximate. Because survivors cannot crouch or peek yet, the actual firing point must still have line of fire to the enemy. Full-cover points where the survivor cannot shoot are rejected. A point receives partial-cover score only when nearby side probes are blocked while the center firing line stays clear, which nudges survivors toward edges instead of fully hiding behind them.

The first version does not implement true peeking or leaning. While moving, survivors keep using `SurvivorAIShooting`, so they can still shoot if they have line of fire. Until crouching or peeking exists, combat movement should not intentionally choose positions that fully block the survivor's own shot.

See `Docs/SurvivorCombatMovementAI.md`.

## Lost Enemy Behavior

If an enemy leaves vision but is not confirmed dead:

- `StopWhenLost`: release combat AI and return to non-combat AI.
- `FollowLastKnownPosition`: move toward the last known position, then release combat AI if no enemy is reacquired.

Following last known positions should use `CharacterNavigator` and should not require networking special state. It is just another state-authority AI movement decision.

Lost-enemy investigation currently applies only to enemy survivors. Zombies do not create lost-combat investigation tasks: if a zombie dies or leaves direct combat awareness, the survivor returns to its normal non-combat assignment unless another live direct enemy is present.

## Combat Handoff

Combat AI must preserve non-combat assignment continuity.

Example follow flow:

1. Survivor is following the player's active survivor.
2. Enemy appears.
3. Combat AI takes over and fights.
4. Enemy dies or disappears.
5. Non-combat AI resumes `FollowSurvivor` if the follow target is alive.
6. If the follow target is dead, non-combat AI falls back to `HoldPosition` at the survivor's current position.

Example move order flow:

1. Survivor is moving to a map order point.
2. Enemy appears.
3. The survivor keeps moving toward the map order point, but combat aim/fire can merge into that ordered movement.
4. Enemy is gone.
5. Non-combat AI resumes moving to the original point or starts lost-enemy investigation when that behavior is allowed.

Player movement orders have priority over combat movement, but not over basic self-defense. While a survivor is following a target, moving to a clicked point, or travelling into a newly assigned defend area for the first time, the survivor keeps the ordered movement vector. Combat AI may still provide aim and fire so the survivor can strafe and shoot while obeying the order. Combat cover movement, lost-enemy pursuit, looting, and investigation wait until the player movement gate is satisfied.

A move order satisfies the gate when the destination is reached. An assigned-area order satisfies it when the survivor enters the circle once. After that, combat movement and lost-enemy investigation may temporarily pull the survivor outside the area, and the assigned area remains the fallback order. Follow remains continuous player intent, so combat movement does not replace it.

## Interaction With Shooting Component

Combat AI should ask `SurvivorWeaponPreferenceAI` for the desired weapon and then ask `SurvivorAIShooting` for aim/fire input.

- Pass whether the survivor is moving so `MovingFirstShotDelayMultiplier` still applies.
- Respect line-of-fire checks so survivors do not shoot walls.
- Release fire immediately when the target is dead or blocked.
- Keep actual weapon switching in the existing `NetworkedInput` path.

## Network Model

Combat AI runs on state authority only.

- Player-selected combat settings should be stored as authoritative survivor state from the first implementation.
- Prefer compact enums and small fields for combat settings so they can be made `[Networked]` cleanly when the UI needs to display them.
- Temporary combat working state stays local: sensor data, chosen target cache, aim timing, path corners, cover candidates, and per-frame decisions should not be networked.
- AI emits `NetworkedInput`.
- Fusion replicates resulting movement, aim, projectile spawning, damage, and death through existing systems.

Avoid networking:

- Target lists.
- Sensor memories.
- Path corners.
- Per-frame AI decisions.

## Implementation Direction

Recommended first implementation path:

1. Add a `SurvivorCombatAI` base controller component used by the survivor's current AI input path. Done.
2. Move current `SurvivorAIShooting` consumption behind combat AI. Done.
3. Add `SurvivorCombatMovementAI` for survivor-vs-survivor movement. Done.
4. Add focused `SurvivorWeaponPreferenceAI` behavior and its four-state roster control. Done.
5. Add focused target-priority behavior when those rules grow.
6. Add lost-target behavior modes.
7. Add true cover peeking only after the rough dynamic cover movement proves useful.

Do not remove current orders while doing this. Orders become non-combat assignments; combat AI only interrupts them.

## Out Of Scope

- Explicit hand-authored cover point generation.
- True peeking/leaning from cover.
- Squad tactics beyond simple ally alerting.
