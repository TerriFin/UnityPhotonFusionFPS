# Survivor Combat Movement AI

## Goal

`SurvivorCombatMovementAI` is a combat behavior component that moves an unpossessed survivor while it is fighting another survivor.

It is not a top-level AI mode. `SurvivorCombatAI` owns combat activation, target choice, combat settings, and handoff back to non-combat AI. `SurvivorCombatMovementAI` owns only tactical movement suggestions: dynamic cover, ally spacing, and preferred weapon range.

## Relationship To Combat AI

The intended combat structure is:

```text
SurvivorCombatAI
-> chooses enemy target
-> checks SurvivorCombatAISettings
-> asks SurvivorCombatMovementAI for movement when combat behavior is Normal, Aggressive, or Defensive
-> asks SurvivorAIShooting for aim/fire
-> merges movement and shooting into normal NetworkedInput
```

`SurvivorCombatMovementAI` should be assigned to the survivor prefab so its tuning values are visible in the Inspector. If missing, `SurvivorCombatAI` may add it at runtime as a fallback.

## First-Pass Behavior

Against enemy survivors, combat movement tries to balance three goals:

1. Seek cover while exposing as little as possible.
2. Spread away from nearby allied survivors.
3. Stay inside the preferred distance range for the selected weapon.

The behavior is intentionally imperfect. The player should still get better results by manually controlling survivors, and the AI should be cheap enough to run for many survivors.

## Dynamic Cover Detection

The first version does not require explicit cover marker objects.

Instead, the component samples a configurable number of candidate points around the survivor:

1. Create candidate offsets around the survivor.
2. Project each candidate to reachable NavMesh using `CharacterNavigator` / `NavMesh`.
3. Reject unreachable candidates.
4. Reject candidates where the survivor would not have line of fire to the enemy.
5. Score each remaining candidate using partial cover, ally spacing, preferred range, and movement cost.
6. Move toward the highest-scoring candidate if it is meaningfully better than standing still.

Cover scoring should use the same broad blocker concept as vision, but full cover is not useful yet because the survivor has no crouch, lean, or peek pose. A candidate must have a clear center line of fire to the enemy. It receives partial cover score when nearby side probes are blocked by `CharacterSensor.VisionBlockers`, which means the survivor is near an edge while still able to shoot.

This is rough cover, not peeking. Until a real peek/crouch system exists, the movement AI should prefer edge-adjacent firing positions instead of positions that fully hide the survivor and break their own shot.

## Performance Controls

The behavior must be configurable so stress tests can make it cheaper.

Inspector tunables:

```text
ReevaluateInterval
InitialReevaluationStaggerMax
ReevaluateIntervalJitter
CandidateCount
MaxCachedAllies
SearchRadius
NavMeshSampleDistance
StoppingDistance
MinimumMoveDistance
DestinationRefreshDistance
DirectFallbackDistance
RequireCandidateLineOfFire
PartialCoverProbeOffset
RequiredScoreImprovement
TargetLossBlacklistDuration
TargetLossBlacklistRadius
TargetLossRecentDestinationTime
CoverWeight
AllySpacingWeight
PreferredRangeWeight
MoveCostWeight
AllySpacingRadius
```

Guidelines:

- `ReevaluateInterval` controls how often expensive candidate scoring runs. Higher is cheaper but less reactive.
- `InitialReevaluationStaggerMax` spreads first-time combat movement searches across a random delay, reducing spikes when many survivors enter combat together.
- `ReevaluateIntervalJitter` adds a small random delay to repeated searches so survivors do not stay synchronized forever.
- `CandidateCount` controls how many points are tested per reevaluation. Lower is cheaper and dumber.
- `MaxCachedAllies` caps how many nearby allies are cached for spacing scores during one reevaluation.
- `SearchRadius` controls how far the AI is willing to reposition in one tactical move.
- `NavMeshSampleDistance` controls how far candidates may snap to nearby reachable ground.
- `StoppingDistance` controls how close the survivor gets to the chosen combat movement point.
- `MinimumMoveDistance` prevents tiny jittery repositioning.
- `DestinationRefreshDistance` prevents resetting the navigator for nearly identical destinations.
- `DirectFallbackDistance` allows a short direct move if path steering fails near the combat destination.
- `RequireCandidateLineOfFire` rejects full-cover candidates where the survivor could not shoot from the chosen point.
- `PartialCoverProbeOffset` controls how far to check beside a clear firing point for nearby blocking geometry.
- `RequiredScoreImprovement` prevents moving unless the new point is clearly better than the current position.
- `TargetLossBlacklistDuration` controls how long a combat destination is avoided after it appears to make the survivor lose line of fire.
- `TargetLossBlacklistRadius` controls how close future candidates can be to that temporarily bad destination before they are rejected.
- `TargetLossRecentDestinationTime` controls how recently a destination must have been selected to be blamed for target loss even if the survivor has not fully arrived there yet.

Good cheap defaults should be modest, for example about `8-12` candidates every `0.75-1.25s`.

Current implementation state:

- Combat movement only runs while the survivor has a direct enemy with line of fire.
- It uses `CharacterNavigator` and `NavMesh.CalculatePath` for candidate reachability.
- Candidate search state is local to state authority and is not networked.
- Reevaluations are staggered per survivor so large groups do not all run cover searches on the same simulation tick.
- Nearby ally positions are cached once per reevaluation and reused for all candidate scores, instead of scanning every active survivor for every candidate.
- The current roster has one legacy combat-movement toggle. The planned replacement is the four-state None / Normal / Aggressive / Defensive combat behavior.
- `None` stops survivor-vs-survivor tactical movement but does not stop target selection, turning, or shooting.
- Zombie retreat is a lightweight safety behavior owned by `SurvivorCombatAI`; it is disabled by `None` and does not use this component.
- Candidates that fully block the survivor's own shot are rejected by default.
- If a chosen combat destination appears to cause the survivor to lose its target, that destination is temporarily blacklisted. Lost-target investigation still starts immediately, but when combat resumes the movement AI avoids picking the same bad cover point again.
- Combat movement is suppressed while a player movement order is still being fulfilled. Follow movement, unreached move orders, and first-time travel into an assigned defend area keep their ordered movement direction. Combat aim/fire may still merge into that movement, but tactical cover/range/spacing movement resumes only after the move destination is reached or the assigned area has been entered once.

## Preferred Weapon Ranges

Each `Weapon` exposes `AIEffectiveMaxRange`. Combat movement should use the currently selected weapon's value as its outer preferred range rather than keeping a second hardcoded maximum-range table by weapon type.

Minimum preferred range may remain part of combat-movement tuning because it describes movement comfort rather than whether the weapon is effective. Maximum effective range belongs to the weapon itself and is shared with `SurvivorWeaponPreferenceAI`.

See `Docs/SurvivorWeaponPreferenceAI.md`.

Preferred range scoring:

- If too close, prefer candidate points farther from the enemy.
- If too far, prefer candidate points closer to the enemy.
- If inside range, avoid moving only for range reasons.
- If far outside the preferred range, the score stays directional instead of flattening out. A candidate that moves closer to the preferred range is better even if it cannot reach the preferred range in one reposition.

## Ally Spacing

The movement behavior should discourage clumping.

For each candidate point, nearby same-team survivors add a spacing penalty. The penalty is strongest when the candidate is very close to an ally and fades out by `AllySpacingRadius`.

This should help when several survivors respond to the same alert from behind a corner. They may still travel along the same route, but once they reach the fight they should try to occupy different nearby positions.

## Input Merging

Combat movement emits only movement intent.

`SurvivorCombatAI` merges it with shooting:

- Movement comes from `SurvivorCombatMovementAI`.
- Look/fire/buttons come from `SurvivorAIShooting`.
- If both want look rotation, shooting aim wins while there is a direct line-of-fire target.
- If combat behavior is `None`, this component emits no movement. `SurvivorCombatAI` may still turn the survivor toward an enemy and shoot, but it does not retreat from zombies for combat reasons.

## Network Model

Combat movement runs only on state authority.

Do not network:

- candidate points,
- selected cover destination,
- cover scores,
- nearby ally lists,
- path corners,
- timing state.

The authoritative result is normal movement, rotation, and firing through existing survivor replication.

## Future Expansion

Later versions can add:

- explicit cover marker support,
- peeking positions,
- crouching or stance changes,
- suppressive movement,
- squad role spacing,
- separate zombie combat movement.

For now, keep it rough, cheap, and understandable.
