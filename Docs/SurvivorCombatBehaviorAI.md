# Survivor Combat Behavior Modes

## Goal

Survivor combat behavior is a player-configurable four-state mode that tells an unpossessed survivor whether and how aggressively it should move during combat, and how strongly it should prioritize enemy player survivors over zombies.

It is separate from the weapon/fire mode:

- Combat behavior chooses tactical target priority, preferred fighting distance, and combat movement style.
- Weapon preference chooses which weapons the AI is allowed or encouraged to use and whether it may fire.
- `None` replaces the old separate `AllowCombatAIActivation` combat-movement toggle.

The default mode is `Normal`.

```csharp
public enum ESurvivorCombatBehavior
{
	Normal = 0,
	Aggressive = 1,
	Defensive = 2,
	None = 3,
}
```

The mode applies only while the survivor is AI-controlled. A possessed survivor remains under direct player control, but its stored mode can still be changed through the roster.

## Relationship To Existing Combat AI

The intended flow is:

```text
SurvivorCombatAI
-> evaluates directly known live enemies
-> applies the selected combat behavior's target priority
-> asks SurvivorWeaponPreferenceAI for the tactical weapon allowed by the fire mode
-> asks SurvivorCombatMovementAI for movement matching the selected behavior
-> asks SurvivorAIShooting for aim and fire input
-> merges everything into normal NetworkedInput
```

The new mode supersedes both the current one-size-fits-all tactical scoring and the separate combat-movement enable/disable toggle. It does not replace the existing combat controller, sensor, shooting, navigation, or weapon systems.

`None` is useful for posting a survivor at an exact position:

- It produces no tactical combat movement.
- It does not advance, back away from zombies, seek cover, adjust range, or otherwise reposition for combat.
- It still selects targets, turns, and shoots according to the independent weapon/fire mode.
- Explicit player movement orders and the separate retreat behavior are not tactical combat movement and remain allowed.

## Shared Target Priority

All four modes, including `None`, normally prioritize a directly known enemy player survivor over zombies.

A zombie overrides that preference only when it enters the selected mode's emergency zombie distance:

```text
emergency zombie distance =
    SurvivorCombatAI.ZombieRetreatDistance
    * mode ZombiePriorityDistanceMultiplier
```

The multiplier is intentionally greater than `1`. The survivor should switch attention to the zombie and have time to shoot before the zombie crosses the smaller retreat distance and forces backward movement.

Suggested initial values:

```text
None:       1.50
Normal:     1.50
Aggressive: 1.25
Defensive:  1.75
```

These are Inspector knobs, not player-facing settings. Aggressive may ignore zombies longer; Defensive may respond earlier.

Rules:

- If one or more enemy survivors are directly known and no zombie is inside the emergency distance, choose an enemy survivor.
- If a zombie enters the emergency distance, choose the nearest immediate zombie threat.
- If no enemy survivor is directly known, zombies remain ordinary valid combat targets.
- Target switching remains sticky so marginal distance changes do not reset aim timing every sensor refresh.
- An emergency zombie bypasses normal stickiness.
- After the emergency zombie dies or leaves the danger area, prefer a directly known enemy survivor again.
- Noise, bullet impacts, and last-known positions do not become shootable targets by themselves.

This priority rule applies in `None`, `Normal`, `Aggressive`, and `Defensive`. The mode changes the emergency distance and tactical movement, not the basic preference for fighting the opposing team.

## Weapon Range Metadata

Combat behavior needs both a useful minimum and maximum fighting distance.

Each weapon already exposes:

```csharp
public float AIWeaponStrength;
public float AIEffectiveMaxRange;
```

Add one local prefab/configuration value:

```csharp
public float AIPreferredMinRange;
```

This is not networked. It describes the closest distance the AI normally wants when deliberately using that weapon.

Examples:

```text
Shotgun: low minimum range
Pistol: medium minimum range
Rifle: high minimum range
```

`SurvivorCombatMovementAI` currently owns weapon-type-specific minimum range fields. The implementation may initially reuse those values, but the durable design is to move the minimum onto each weapon so future weapon types do not require another switch statement.

## Weapon Preference Interaction

Combat behavior must not make the separate weapon/fire control meaningless.

First determine the weapons allowed by `ESurvivorWeaponPreference`:

- `PreferPistol`: only the usable pistol is considered.
- `HoldFire`: do not automatically switch or fire; movement uses the currently held weapon's range metadata.
- `Automatic`: allow the combat behavior to make its normal tactical choice.
- `PreferStrongWeapons`: allow all collected usable weapons, with strength remaining important.

Then apply the combat behavior:

- `None`: do not switch weapons because of movement planning; shooting may still select a weapon through the ordinary weapon/fire rules.
- `Normal`: use the existing range-aware weapon selection.
- `Aggressive`: bias toward the highest-strength allowed usable weapon.
- `Defensive`: bias toward the allowed usable weapon with the greatest `AIEffectiveMaxRange`, even when a shorter-range weapon has greater raw strength.

Ties should prefer the current weapon, then use stable weapon order. Empty or uncollected weapons are never selected.

This means a Defensive survivor may prefer a pistol over a more powerful shotgun because the pistol lets it fight farther away. An explicit `PreferPistol` still wins in every behavior. `HoldFire` still means no AI firing under any circumstance.

## None

`None` replaces the old disabled combat-movement toggle state.

Against enemy survivors:

- Select and track targets normally.
- Turn and shoot when the weapon/fire mode permits it.
- Do not seek cover.
- Do not advance, open distance, or move to a preferred weapon range.
- Do not use ally-spacing movement.

Against zombies:

- Select and shoot zombies normally when no preferred enemy survivor is available or a zombie enters the `None` emergency distance.
- Do not back away when the zombie enters `ZombieRetreatDistance`.

`None` disables only AI-selected tactical combat movement. Persistent player assignments, investigation, looting, recruiting, home-base retreat, and other non-combat movement keep their own rules.

None uses `ZombiePriorityDistanceMultiplier = 1.5` by default.

## Normal

`Normal` preserves the current general combat behavior.

Against enemy survivors:

- Prefer partial cover and ally spacing.
- Avoid unnecessary movement when the current position is already useful.
- Move toward or away from the enemy only enough to enter the selected weapon's preferred range.
- Do not continuously rush a visible target.
- Preserve current target-loss and bad-destination safeguards.

Against zombies:

- Do not approach a zombie for combat reasons.
- Shoot from the current position when possible.
- Back away when a zombie enters `ZombieRetreatDistance`, if combat movement is enabled.

Normal uses `ZombiePriorityDistanceMultiplier = 1.5` by default.

## Aggressive

`Aggressive` is intended to let an RTS-focused player turn superior numbers into pressure against a directly controlled enemy survivor.

Against enemy survivors:

- Select or plan around the highest-strength allowed usable weapon.
- Prefer destinations that reduce distance toward that weapon's `AIPreferredMinRange`.
- Continue approaching while the enemy remains directly known and the survivor is outside that desired distance.
- Keep aiming and firing while moving.
- Retain ally spacing so a group does not collapse into one pile.
- Treat cover as secondary to forward pressure, but still reject unreachable or obviously invalid destinations.
- Stop closing once the desired minimum range is reached instead of trying to overlap the target.

For a shotgun, the desired minimum range should be very close. For a rifle, the survivor can stop much farther away.

Aggressive does not grant omniscient pursuit. If the enemy is no longer directly known, use the existing lost-target rules rather than chasing its live transform.

Against zombies:

- Do not deliberately approach them.
- Prefer the enemy survivor until a zombie enters the Aggressive emergency distance.
- Use the ordinary close-zombie retreat rule once the zombie becomes an immediate threat.

Aggressive uses a smaller `ZombiePriorityDistanceMultiplier`, suggested `1.25`, but it should remain greater than `1`.

## Defensive

`Defensive` actively tries to fight enemy survivors from the greatest practical distance.

Against enemy survivors:

- Select or plan around the allowed usable weapon with the greatest `AIEffectiveMaxRange`.
- Prefer destinations farther from the enemy while remaining within that weapon's effective range.
- If currently beyond effective range, approach only until the target can be engaged.
- If too close, open distance.
- Keep line of fire where possible.
- Continue using cover and ally spacing.
- Do not flee indefinitely: movement is limited by normal combat search radius, reachable NavMesh, line-of-fire requirements, and the weapon's effective range.

Defensive is not the damage-triggered "go home" behavior. That is the separate percentage-based retreat mode documented in `Docs/SurvivorRetreatAI.md`.

Against zombies:

- Do not approach them.
- Switch attention to nearby zombies earlier than the other modes.
- Back away through the existing zombie retreat behavior when combat movement is enabled.

Defensive uses a larger `ZombiePriorityDistanceMultiplier`, suggested `1.75`.

## Player Order Precedence

Player-given movement intent remains stronger than tactical combat behavior.

Priority:

1. Directly possessed player input.
2. A new explicit player movement order.
3. An active retreat-to-home-base assigned-area order.
4. An unreached persistent move order or assigned-area entry.
5. Tactical movement from `Normal`, `Aggressive`, or `Defensive`.
6. Optional non-combat detours.

While a player movement order owns movement, combat behavior may still choose a target, weapon, look direction, and fire input. It must not replace the ordered movement vector.

A follow order is the exception to this priority list. When combat behavior is not `None` and the follower directly perceives an enemy survivor as its selected combat target, tactical survivor-vs-survivor movement temporarily owns movement. The follow assignment resumes after direct perception is lost. `None` and zombie targets continue using follow movement while combat aim/fire is layered over it.

Once a move destination is reached or an assigned area has been entered, the selected combat behavior may reposition the survivor and the stored assignment remains its fallback anchor.

## Inspector Tuning

Suggested focused settings:

```text
NoneZombiePriorityDistanceMultiplier
NormalZombiePriorityDistanceMultiplier
AggressiveZombiePriorityDistanceMultiplier
DefensiveZombiePriorityDistanceMultiplier

AggressiveForwardPressureWeight
AggressiveCoverWeightMultiplier
AggressiveStoppingTolerance

DefensiveRangeWeight
DefensiveCoverWeightMultiplier
DefensiveStoppingTolerance
```

Keep expensive candidate count, reevaluation interval, path validation, line-of-fire probes, and ally caching in `SurvivorCombatMovementAI`. The behavior mode should adjust scoring weights and desired range, not create three independent pathfinding systems.

## Roster UI

Combat behavior is a four-state mode on:

- Every survivor roster card.
- The bulk behavior row.

Recommended compact labels:

```text
NORMAL
AGGRO
DEFENSIVE
NONE
```

It may use a segmented control or one compact cycling control, matching the existing weapon preference control.

Bulk target set:

```text
selected alive owned survivors, if selection is not empty
otherwise all alive owned survivors
```

Bulk display should show the most common mode. Ties fall back to `Normal`.

Changing combat behavior must not change:

- Weapon/fire preference.
- Looting, investigation, recruiting, or retreat toggles.
- Current player assignment.

There is no separate combat-movement boolean after this mode is implemented.

## Settings And Network Model

Store the mode in `SurvivorCombatAISettings`:

```csharp
public struct SurvivorCombatAISettings
{
	public ESurvivorWeaponPreference WeaponPreference;
	public ESurvivorCombatBehavior CombatBehavior;
}
```

Default:

```text
CombatBehavior: Normal
```

The authoritative mode should be replicated as one compact enum so every client's roster can display it.

Implemented migration:

- `AllowCombatAIActivation` is removed from `SurvivorNonCombatAISettings`.
- Its boolean roster toggle and `ESurvivorAISetting` entry are replaced by the combat behavior control.
- `CombatBehavior.None` is the disabled tactical-movement state.
- New/default survivors use `CombatBehavior.Normal`.
- Newly recruited survivors inherit the recruiter's combat behavior.
- Do not create or retain a separate behavior document for disabled combat movement; `None` is fully specified here.

Do not network:

- Current target candidates.
- Chosen tactical weapon cache.
- Cover candidates or scores.
- Desired range.
- Path corners.
- Reevaluation timers.

Those remain state-authority working state. AI continues to emit ordinary `NetworkedInput`.

## Acceptance Criteria

- New player-owned survivors default to `Normal`.
- All four modes prefer a directly known enemy survivor over zombies outside their emergency zombie distance.
- Each mode has its own tunable zombie-priority multiplier.
- A dangerously close zombie can immediately override an enemy-survivor target.
- None preserves target selection and shooting but emits no tactical combat movement.
- Normal broadly preserves current tactical movement.
- Aggressive survivors close toward the preferred minimum range of their selected tactical weapon.
- Shotgun-equipped Aggressive survivors close much farther than rifle-equipped survivors.
- Defensive survivors prefer the longest-range allowed usable weapon and try to maintain greater distance.
- Defensive can prefer a pistol over a stronger shotgun.
- Shooting and explicit weapon preferences remain independently controllable.
- Player movement orders remain stronger than tactical combat movement.
- The behavior runs on state authority and adds no per-frame network state.
