# Survivor Looting AI

## Goal

`SurvivorLootingAI` is a non-combat behavior component that lets an unpossessed survivor collect useful sensed pickups when its current non-combat assignment allows it.

It is not a top-level AI mode. `SurvivorNonCombatAI` owns the survivor's base non-combat assignment and decides when looting is allowed to run. `SurvivorLootingAI` owns only the looting-specific target selection, movement target, and return state.

## Relationship To The AI System

The long-term survivor AI model is:

```text
SurvivorNonCombatAI / SurvivorCombatAI
-> choose which behavior, if any, should run
-> behavior component emits or helps build normal NetworkedInput
```

`SurvivorLootingAI` is one of those behavior components.

- It is assigned to the survivor prefab as a `MonoBehaviour`.
- It runs only on state authority through `SurvivorNonCombatAI`.
- It does not replace player orders.
- It does not directly move transforms.
- It uses `CharacterNavigator` and normal `NetworkedInput` movement.

## Editor Tunables

`SurvivorLootingAI` exposes looting-specific values in the Inspector:

```text
PickupStoppingDistance
DirectFallbackDistance
```

These values belong on the behavior component, not in `SurvivorNonCombatAISettings`. The settings struct only says whether pickup collection is enabled for this survivor.

## Activation Rules

Looting may start only when:

- `SurvivorNonCombatAISettings.CollectVisiblePickups` is enabled.
- The survivor is unpossessed.
- The survivor is not currently fighting or aiming at a direct line-of-fire combat target.
- The survivor is holding position or already free inside an assigned area.
- The survivor is not still travelling to a player-issued move destination.
- The survivor is not following another survivor.
- A sensed pickup is useful and active. The sensor finds pickups through forward vision or through close proximity with a clear blocker line.

Follow orders and unreached move orders are treated as active player intent, so looting does not interrupt them.

Once a move order reaches its destination and becomes `HoldPosition`, looting can run again if the setting is enabled.

## Useful Pickups

A pickup is useful when:

- It is a health pickup and the survivor is missing health.
- It is a weapon pickup and the survivor does not have that weapon.
- It is a weapon pickup and the survivor has that weapon but is missing reserve ammo for it.

Inactive pickups are ignored for movement decisions. They may still be shown differently on the map by awareness UI, but the AI should not path to them.

## Behavior Flow

1. Ask `CharacterSensor` for visible pickups. In code, "visible" includes forward vision and close line-of-sight proximity.
2. Filter to active and useful pickups.
3. Pick the closest useful sensed pickup.
4. Move toward it using `CharacterNavigator`.
5. If the pickup becomes unavailable or stops being useful, pick another useful sensed pickup if one is already known.
6. If no useful sensed pickup remains, return to the non-combat assignment anchor.

If another useful sensed pickup is known when the current pickup is consumed, the survivor should chain directly into the next pickup instead of returning to its anchor first.

## Interruption Rules

Looting can be interrupted by:

- Combat AI activation.
- Investigation behavior.
- Player disabling `CollectVisiblePickups`.
- A new player-issued hold, move, follow, or assigned-area order.

If pickup collection is disabled while looting is active, the looting behavior stops immediately and returns the survivor to the assignment anchor.

Completing a player move order must not re-enable pickup collection. Only explicit player setting changes can toggle the setting.

## Network Model

Looting runs only on state authority.

Do not network:

- Pickup candidate lists.
- Current pickup target.
- Return-to-anchor scratch state.
- Path corners.

The authoritative result is the normal survivor movement and pickup interaction replicated by existing systems.

## Future Expansion

Future looting behavior can add:

- Pickup priority weights.
- Risk checks before looting.
- Team-level loot reservation.
- Weapon preference rules from combat AI settings.

These should stay inside `SurvivorLootingAI` or another focused behavior component, with `SurvivorNonCombatAI` only deciding whether the behavior is allowed to run.
