# Survivor Weapon Preference AI

## Implementation Status

Implemented.

- `SurvivorWeaponPreferenceAI` is attached to the survivor prefabs and runs weapon decisions on state authority.
- `SurvivorAIShooting` supplies the current direct target and converts the selected weapon into normal weapon input.
- The preference is stored as a replicated enum on `Survivor`.
- Normal survivors start in `Automatic`.
- Neutral survivors start in `PreferStrongWeapons`; when recruited, they inherit the recruiter's weapon preference.
- Individual and bulk roster controls cycle through `AUTO`, `STRONG`, `PISTOL`, and `HOLD`.

## Goal

`SurvivorWeaponPreferenceAI` is a focused combat behavior component that chooses which weapon an unpossessed player-owned survivor should use.

It provides one player-configurable four-state weapon/fire mode:

```csharp
public enum ESurvivorWeaponPreference
{
	Automatic = 0,
	PreferStrongWeapons = 1,
	PreferPistol = 2,
	HoldFire = 3,
}
```

`Automatic` is the default.

The behavior applies only while the survivor is AI-controlled. A possessed survivor remains fully controlled by the player, but its stored preference can still be edited from the roster and takes effect when that survivor becomes unpossessed.

## Architecture

Weapon preference should follow the same modular behavior pattern as combat movement:

```text
SurvivorCombatAI
-> owns combat activation and movement orchestration
-> asks SurvivorAIShooting for aim/fire input

SurvivorWeaponPreferenceAI
-> owns weapon-choice rules and Inspector tuning
-> reads the survivor's preference mode, current target, sensed threats, and overtime state
-> scores the survivor's usable weapons using their own AI strength/range metadata
-> returns the best usable desired weapon for the current target distance

SurvivorAIShooting
-> owns the current direct shooting target
-> asks SurvivorWeaponPreferenceAI for the desired weapon
-> converts the desired weapon into the existing weapon-switch input button
-> continues to own aiming, firing cadence, accuracy, and target selection
```

The weapon preference component should be attached directly to the survivor prefab. `SurvivorCombatAI` may add it at runtime as a fallback if it is missing, matching the current combat movement behavior pattern.

The behavior runs only on state authority. It must not perform network RPCs or mutate weapon state directly. It chooses a desired weapon and lets the existing input/weapon pipeline perform the switch.

## Preference Modes

### Automatic

Automatic is the smart default.

Against an enemy survivor:

- use the best range-appropriate strong weapon,
- ignore weapons whose AI effective maximum range is shorter than the target distance,
- balance weapon strength against how closely the weapon's effective range fits the current distance,
- use the pistol if no stronger usable weapon remains.

Against zombies:

- normally use the pistol,
- switch to the strongest usable weapon when at least one directly known live zombie is inside `CloseZombieDistance`,
- switch to the strongest usable weapon when at least `NearbyZombieCountThreshold` directly known live zombies are inside `NearbyZombieCountRadius`,
- switch to the strongest usable weapon for all targets during zombie overtime.

The close-zombie and zombie-count conditions are independent. Either one enables strong weapons.

Only directly known sensor targets should count. Do not add a separate physics overlap scan for this behavior. Reuse `CharacterSensor.GetDirectKnownEnemies(...)`, filter dead targets, and classify zombies through `ZombieCharacter`.

Automatic weapon choice is based on the current combat situation:

- current target is an enemy survivor: strong weapon,
- current target is a zombie and zombie danger threshold is met: strong weapon,
- current target is a zombie and no danger threshold is met: pistol,
- overtime is active: strong weapon.

Every "strong weapon" decision uses the range-aware selection rules in `Range-Aware Strong Weapon Selection`.

If there is no direct combat target, the behavior does not force another weapon switch. The survivor can keep the weapon it last used until combat or another explicit weapon decision occurs.

### Prefer Strong Weapons

This replaces the current fixed rifle, shotgun, pistol priority:

- always choose the best range-appropriate strong weapon,
- exclude weapons that cannot effectively cover the current target distance,
- fall back to the pistol when stronger weapons are unavailable or empty,
- applies against zombies and enemy survivors,
- does not change during overtime because it is already using the strongest option.

### Prefer Pistol

This is an explicit ammunition-saving order:

- use the pistol against every target,
- do not switch to stronger weapons because of zombie distance, zombie count, enemy-survivor targets, or overtime,
- fall back to another usable weapon only if the pistol is genuinely unavailable or cannot fire.

The current game gives every survivor an infinite-ammo pistol, so the fallback is primarily defensive against future weapon-system changes.

### Hold Fire

This is an explicit "do not shoot under any circumstance" order:

- keep detecting and tracking direct enemies so combat movement, ally alerts, and look behavior can still react,
- do not switch weapons for AI combat,
- never press `Fire`,
- applies against zombies and enemy survivors.

## Weapon AI Stats

Each `Weapon` component should expose two designer-authored AI values:

```csharp
[Header("AI Evaluation")]
[Min(0f)]
public float AIWeaponStrength = 1f;

[Min(0.1f)]
public float AIEffectiveMaxRange = 20f;
```

`AIWeaponStrength`:

- expresses the overall value of using the weapon when it is suitable,
- can account for damage, fire rate, pellet count, reload burden, ammunition value, or any other designer judgment,
- is intentionally hand-authored rather than derived from raw weapon statistics,
- higher values make the AI prefer the weapon more strongly.

`AIEffectiveMaxRange`:

- is the furthest distance at which AI considers the weapon an effective strong-weapon choice,
- is separate from `Weapon.MaxHitDistance`,
- does not change projectile lifetime, hit detection, or the distance from which a player may fire,
- lets short-range weapons be excluded before strength is evaluated.

`MaxHitDistance` remains the hard simulation limit. `AIEffectiveMaxRange` is only tactical metadata. It should normally be less than or equal to `MaxHitDistance`.

These fields live on each weapon prefab/component so new weapon types work without adding another weapon-type switch statement to survivor AI.

## Range-Aware Strong Weapon Selection

When a mode asks for a strong weapon:

1. Collect weapons that are collected and have ammunition.
2. Measure distance from the survivor's firing position to the current target position.
3. Reject any weapon where `targetDistance > AIEffectiveMaxRange`.
4. Score every remaining weapon using both strength and range fit.
5. Select the highest score.
6. If no usable weapon is within effective range, select the usable weapon with the greatest `AIEffectiveMaxRange` as a best-effort fallback.

Recommended first-pass score:

```text
rangeHeadroom = AIEffectiveMaxRange - targetDistance
rangeFit = 1 / (1 + rangeHeadroom)
weaponScore = AIWeaponStrength * rangeFit
```

Because out-of-range weapons are already rejected, `rangeHeadroom` is never negative during normal scoring.

This gives a weapon a higher range-fit value when its effective maximum range is close to the actual target distance. Strength can still make a generally superior weapon beat a weaker alternative when both are reasonable choices.

Example:

```text
Rifle:
  AIWeaponStrength = 20
  AIEffectiveMaxRange = 60

Shotgun:
  AIWeaponStrength = 10
  AIEffectiveMaxRange = 8

Pistol:
  AIWeaponStrength = 2
  AIEffectiveMaxRange = 30
```

At `10m`:

- shotgun is rejected because `10 > 8`,
- rifle and pistol remain,
- rifle's greater strength can make it the preferred choice.

At `7m`:

- shotgun is eligible and has only `1m` of unused range,
- rifle has `53m` of unused range,
- the shotgun receives a much better range-fit score and is preferred.

If the shotgun is unavailable at `7m`, the rifle can still beat the pistol because its strength is substantially higher while both remain within effective range.

Ties should prefer the current weapon to avoid pointless switching. If the current weapon is not tied for best, use stable weapon order as the final tie-breaker so the choice does not oscillate between simulation ticks.

## Inspector Settings

The behavior component should expose the values that control Automatic mode:

```csharp
[DisallowMultipleComponent]
public sealed class SurvivorWeaponPreferenceAI : MonoBehaviour
{
	[Header("Automatic Zombie Escalation")]
	public float CloseZombieDistance = 4f;
	public int NearbyZombieCountThreshold = 4;
	public float NearbyZombieCountRadius = 10f;
}
```

Rules:

- `CloseZombieDistance` is measured from the survivor to directly known live zombies.
- `NearbyZombieCountThreshold` is the number of directly known live zombies required to escalate.
- `NearbyZombieCountRadius` limits which directly known zombies count toward that threshold.
- zero or negative `CloseZombieDistance` disables the close-zombie escalation rule.
- zero or negative `NearbyZombieCountThreshold` disables the zombie-count escalation rule.

These are component tuning values, not per-survivor player settings. The player changes only the four-state weapon/fire mode during a match.

Weapon strength and effective range are configured on each `Weapon`, not duplicated on the behavior component.

## Overtime Detection

Automatic mode should read the existing authoritative zombie overtime state:

```text
Gameplay.ZombieOrchestrator.IsOvertime
```

Do not infer overtime from remaining match time inside every survivor. The orchestrator already owns the transition and exposes `IsOvertime`.

Overtime overrides Automatic's normal zombie rules and selects the strongest usable weapon immediately. It does not override explicit player choices such as `PreferPistol` or `HoldFire`.

## Combat Settings

Expose the mode through `SurvivorCombatAISettings` and replicate its authoritative value through `Survivor.WeaponPreference`:

```csharp
public struct SurvivorCombatAISettings
{
	public ESurvivorWeaponPreference WeaponPreference;
}
```

Defaults:

```csharp
public static SurvivorCombatAISettings Default => new SurvivorCombatAISettings
{
	WeaponPreference = ESurvivorWeaponPreference.Automatic,
};
```

Changing combat behavior must not reset weapon preference. Changing weapon preference must not reset combat behavior or any non-combat setting.

Ordinary player orders preserve the survivor's current preference. Newly spawned normal survivors start in `Automatic`, while recruited survivors inherit their recruiter's preference.

## Switching Flow

The current `SurvivorAIShooting` always calls `Weapons.TryGetBestUsableWeapon(WeaponPriority, ...)`. This fixed priority should be replaced by a request to the weapon preference behavior.

Suggested flow:

```text
1. SurvivorAIShooting identifies the current direct target.
2. SurvivorWeaponPreferenceAI evaluates the stored preference and target distance.
3. For strong-weapon decisions, it scores the collected usable weapons using `AIWeaponStrength` and `AIEffectiveMaxRange`.
4. It returns the desired usable Weapon or EWeaponType.
5. SurvivorAIShooting converts that type to Pistol/Rifle/Shotgun input.
6. Weapons.SwitchWeapon performs the existing authoritative switch.
7. SurvivorAIShooting resumes firing after the existing switch timer.
```

Do not call `Weapons.SwitchWeapon(...)` directly from the behavior component. Keeping switching in normal `NetworkedInput` preserves the existing survivor input path.

The decision should not interrupt an active switch. If `Weapons.IsSwitching` is true, keep the pending switch and reevaluate after it completes.

## Roster UI

Weapon preference is a mode, not a boolean toggle. It needs a four-state control on:

- every `SurvivorRosterEntry`,
- the roster's `BulkToggleBar` or equivalent bulk settings row.

Recommended compact labels:

```text
AUTO
STRONG
PISTOL
HOLD
```

A compact segmented control is clearest. If the existing card cannot fit four buttons, one button may cycle through the four states and update its icon/text. It must still clearly display the current state.

Individual control:

- reads `survivor.CombatAISettings.WeaponPreference`,
- selecting a mode sends an authoritative setting request for only that survivor,
- works for the possessed survivor because it edits stored future AI behavior.

Bulk control target set:

```text
selected alive owned survivors, if selection is not empty
otherwise all alive owned survivors
```

Bulk display:

- show the most common weapon preference in the current target set,
- if multiple modes are tied, display `Automatic` as the neutral default,
- refresh the displayed snapshot only when the map opens, selection changes, or the player changes the bulk control, matching the existing roster bulk-toggle rules.

Bulk input applies the chosen mode to every survivor in the current target set.

## State-Authority Request

Do not encode the mode through several boolean toggles. Add an enum-valued request path:

```csharp
Gameplay.RequestMapWeaponPreference(
	CharacterMask128 selectedCharacterMask,
	ESurvivorWeaponPreference preference);

SurvivorAICommandService.ApplySelectedTeamWeaponPreference(
	PlayerRef owner,
	CharacterMask128 selectedCharacterMask,
	ESurvivorWeaponPreference preference);
```

State authority validates:

- the sender owns the survivor,
- the survivor is alive,
- the survivor index is in the requested mask,
- the enum value is defined,
- possessed survivors are allowed because this changes stored settings rather than issuing a movement order.

The preference must use the same authoritative/replicated settings model as the existing combat settings so every client's roster displays the authoritative value.

## Performance

The behavior must remain cheap enough for large survivor counts.

- Run only for unpossessed survivors on state authority.
- Reuse the current sensor's direct-known-enemy list.
- Reuse a per-component list instead of allocating a new collection each evaluation.
- Iterate only the survivor's existing `Weapons.AllWeapons` array when scoring weapons.
- Compare squared distances.
- Do not perform raycasts, overlap queries, pathfinding, or map-wide zombie searches.
- Do not reevaluate while there is no combat target.
- Do not issue a switch input when the desired weapon is already active or pending.

## Interaction With Other Systems

### Combat Movement

Combat movement reads the current weapon to determine preferred range. `AIEffectiveMaxRange` should become the selected weapon's outer preferred range instead of duplicating hardcoded maximum ranges by weapon type. After a weapon switch, movement naturally uses the newly active weapon's effective range on its next tactical reevaluation.

Weapon preference does not directly command movement.

### Looting

Weapon preference does not decide whether a survivor wants a pickup. Looting continues to collect missing weapons or ammunition according to its own setting.

Having `PreferPistol` selected does not make stronger weapon pickups unwanted. The player may change the preference later.

### Manual Possession

The component does not override weapon input while the survivor is possessed. The player can use any collected weapon regardless of the stored preference.

### Neutral Survivors

Neutral survivors by default use option 2, "Strongest weapon available, always". When recruited, they copy the recruiting survivor's current weapon preference.

## Implemented Flow

1. `SurvivorAIShooting` identifies the current direct target.
2. `SurvivorWeaponPreferenceAI` evaluates the replicated preference, target type, target distance, directly known zombies, and overtime state.
3. Strong-weapon decisions score collected weapons that still have ammunition.
4. `SurvivorAIShooting` converts the result into the existing pistol/rifle/shotgun input button.
5. The existing weapon input path performs the authoritative switch.
6. Roster changes are sent to state authority through `Gameplay.RequestMapWeaponPreference`.

## Acceptance Criteria

- Newly spawned survivors default to `Automatic`.
- Automatic uses the pistol against ordinary zombie encounters.
- Automatic switches to a strong usable weapon when a zombie is too close.
- Automatic switches to a strong usable weapon when enough zombies are nearby.
- Automatic uses strong weapons against enemy survivors.
- Automatic uses strong weapons during overtime.
- Prefer Strong Weapons uses the best range-appropriate usable weapon against all targets.
- Prefer Pistol uses the pistol against all targets, including overtime.
- Weapons outside `AIEffectiveMaxRange` are excluded from ordinary strong-weapon selection.
- A close-range weapon is preferred when its range fit and strength make it the best choice.
- If every weapon is outside effective range, the longest-range usable weapon is selected as fallback.
- Empty or unavailable weapons are excluded, and uncontrolled survivors pulse the selected usable replacement request until the switch succeeds.
- Possessed survivors remain under manual weapon control.
- Individual roster controls update one survivor.
- Bulk roster controls update the selected survivors, or the whole team when none are selected.
- The behavior runs on state authority and adds no new physics scans or networked per-frame state.
