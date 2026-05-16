# Survivor Investigation AI

## Goal

`SurvivorInvestigationAI` is a non-combat behavior component that lets an unpossessed survivor react to suspicious stimuli such as sounds, bullet impacts, approximate shooter positions, and lost enemy positions.

It is not a top-level AI mode. `SurvivorNonCombatAI` owns the survivor's base non-combat assignment and decides when investigation is allowed to run. `SurvivorInvestigationAI` owns only investigation-specific movement, ally alerting, and looking around at the target point.

## Relationship To The AI System

The long-term survivor AI model is:

```text
SurvivorNonCombatAI / SurvivorCombatAI
-> choose which behavior, if any, should run
-> behavior component emits or helps build normal NetworkedInput
```

`SurvivorInvestigationAI` is one of those behavior components.

- It is assigned to the survivor prefab as a `MonoBehaviour`.
- It runs only on state authority through `SurvivorNonCombatAI`.
- It does not replace player orders.
- It does not directly move transforms.
- It uses `CharacterNavigator` and normal `NetworkedInput` movement.

## Editor Tunables

`SurvivorInvestigationAI` exposes investigation-specific values in the Inspector:

```text
InvestigationStoppingDistance
DirectFallbackDistance
AllyAlertRadius
LookDurationMin
LookDurationMax
LookRotationIntervalMin
LookRotationIntervalMax
MaxYawDegreesPerTick
```

These values belong on the behavior component, not in `SurvivorNonCombatAISettings`. The settings struct only says whether investigation is enabled for this survivor.

## Activation Rules

Investigation may start only when:

- `SurvivorNonCombatAISettings.InvestigateSuspiciousStimuli` is enabled.
- The survivor is unpossessed.
- The survivor is not currently fighting or aiming at a direct line-of-fire combat target.
- The survivor is holding position, returning from an optional detour, looting, or already investigating an older target.
- For assigned-area orders, the survivor has entered the assigned circle at least once.
- The survivor is not still travelling to a player-issued move destination or travelling into an assigned area for the first time.
- The survivor is not following another survivor.

Follow orders, unreached move orders, and first-time travel into an assigned area are treated as active player intent, so investigation does not interrupt them. After an assigned area has been reached once, investigation may temporarily pull the survivor outside the circle and the assigned area remains the fallback order.

## Stimuli

Investigation consumes immediate events from `CharacterSensor`, especially:

- Noise.
- Bullet impact.
- Approximate shooter position.
- Direct enemy sighting for one-hop ally alerts.
- Last known enemy position after combat line of fire is lost.

Sound and bullet-impact stimuli are immediate prompts, not remembered investigation targets. If the survivor cannot start investigating when the event happens, it ignores the event.

This prevents stale combat sounds from pulling survivors around later after the situation has changed.

## Behavior Flow

1. Receive a suspicious target point from `CharacterSensor` or combat handoff.
2. Ask `CharacterNavigator` for the nearest reachable NavMesh point around that source.
3. Move toward the reachable investigation point.
4. Once close enough, stop and look around for a random duration.
5. Periodically choose new random yaw targets during the look-around phase.
6. Return to the non-combat assignment anchor if no combat target is found.

The reachable point search is important for shots fired from unreachable places, such as the top of cars. The survivor should investigate nearby reachable ground instead of silently giving up.

## Ally Alerts

The same `InvestigateSuspiciousStimuli` setting controls same-team alerting. There is no separate `AlertNearbyAllies` setting.

If a survivor with investigation enabled notices a suspicious sound, bullet impact, or direct enemy sighting, it may alert nearby same-team survivors that also have investigation enabled. Alerted survivors receive the same investigation target.

Alert rules:

- Possessed survivors do not originate investigation alerts.
- Possessed survivors do not receive investigation alerts.
- Alerted survivors do not re-broadcast the alert.
- Allies that already have their own line-of-fire combat target ignore investigation alerts.
- Allies with only vague or proximity knowledge can still be alerted.
- Alerts are one-hop only to prevent alert chains from crossing the whole map.
- Alert range is controlled by `SurvivorInvestigationAI.AllyAlertRadius`.

## Lost Combat Investigation

If a survivor had line of fire to an enemy, then loses that line of fire while the enemy is still alive, `SurvivorNonCombatAI` can convert the enemy's last known position into a new investigation target.

This allows survivors to pursue and check the last known enemy position instead of immediately returning to their original assignment.

If the enemy is confirmed dead, the survivor returns to its previous non-combat assignment instead.

## Interruption Rules

Investigation can interrupt:

- Pickup collection.
- Returning from a pickup.
- Returning from an older investigation.
- An older investigation target.
- Lost-combat handoff when the previous combat target escaped and is still alive.

Investigation cannot interrupt:

- A currently unreached player move order.
- First-time travel into an assigned area.
- A follow order.
- Possessed player control.

If a survivor is already on an optional detour, such as investigating, returning from investigation, looting, or returning from loot, a fresh stimulus may replace that temporary behavior. This keeps gunshots and re-sighted enemies responsive even after the survivor has left its assigned area to check something out.

Investigation can be interrupted by:

- Combat AI activation.
- Player disabling `InvestigateSuspiciousStimuli`.
- A new player-issued hold, move, follow, or assigned-area order.

If investigation is disabled while active, the behavior stops immediately and returns the survivor to its assignment anchor.

## Network Model

Investigation runs only on state authority.

Do not network:

- Current investigation target.
- Alert recipient lists.
- Look-around timers.
- Path corners.

The authoritative result is normal survivor movement and rotation replicated by existing systems.

## Future Expansion

Future investigation behavior can add:

- Suspicion levels.
- Team radio/voice-range logic.
- Different reaction types for quiet noises versus gunfire.
- Investigation formations.
- Search patterns around the target point.

These should stay inside `SurvivorInvestigationAI` or another focused behavior component, with `SurvivorNonCombatAI` only deciding whether the behavior is allowed to run.
