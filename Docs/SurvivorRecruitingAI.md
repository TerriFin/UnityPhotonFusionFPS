# Survivor Recruiting AI

## Goal

`SurvivorRecruitingAI` is a non-combat behavior component that lets an unpossessed, player-owned survivor walk over to a neutral survivor it has sensed in order to recruit it, then return to its player-given task.

It is the recruiting counterpart of `SurvivorLootingAI` and `SurvivorInvestigationAI`. It is not a top-level AI mode: `SurvivorNonCombatAI` owns the survivor's base assignment and decides when recruiting is allowed to start, persist, or stop. `SurvivorRecruitingAI` owns only the target selection, the "walk to the neutral" movement, and the return state.

## Relationship To The AI System

```text
SurvivorNonCombatAI
-> decides which temporary behavior, if any, may run (recruiting > investigation/looting)
-> behavior component emits normal NetworkedInput movement
```

- It is added to the survivor automatically by `SurvivorNonCombatAI` (like looting and investigation).
- It runs only on state authority through `SurvivorNonCombatAI`.
- It does not replace player orders; it is a temporary deviation that returns to the order anchor.
- It does not directly move transforms; it uses `CharacterNavigator` and normal `NetworkedInput`.

## Who Performs The Recruitment

This component does **not** perform the ownership/team transfer. The authoritative recruitment is still done by `NeutralSurvivorOrchestrator` (see `NeutralSurvivors.md`): on a periodic check, any player-owned survivor within `NeutralSurvivorSpawnSettings.RecruitmentRadius` of a neutral recruits it.

`SurvivorRecruitingAI` only provides the missing piece: it moves an AI survivor toward a sensed neutral so it enters that radius. When the orchestrator flips the target's ownership, the target's `IsNeutral` becomes false, the recruiting behavior detects the target is no longer valid, and the survivor returns to its task.

Because the orchestrator picks the closest player-owned survivor, the recruiting survivor normally is the one that gets credited, but any closer teammate or the possessed player can claim the neutral first — in which case the recruiter simply returns to its task.

## Detection

There is no special recruiting range. A survivor will go recruit a neutral whenever it can **sense** one through its own `CharacterSensor` (vision or proximity), the same senses that put the neutral on the map. Neutral survivors already appear in a player-owned survivor's sensor as "direct known enemies" (different `OwnerRef`); the recruiting behavior scans that list, keeps only living neutral survivors, and targets the closest.

Once committed, the survivor chases the neutral's live position (neutrals patrol), not a one-time snapshot.

## Editor Tunables

```text
RecruitStoppingDistance
DirectFallbackDistance
RecruitDetourDistance
```

These live on the behavior component, not in `SurvivorNonCombatAISettings`. The settings struct only says whether recruiting is enabled for this survivor. `RecruitDetourDistance` controls the in-travel detour (see Travel Detour); `0` disables it.

## The Toggle

`SurvivorNonCombatAISettings.RecruitNeutralSurvivors` enables recruiting, exactly like `CollectVisiblePickups` and `InvestigateSuspiciousStimuli`. It is on in `Default` and off in `Passive`, so the existing "non-combat AI on/off" player command toggles it together with the other non-combat behaviors. Disabling it while recruiting is active stops the behavior immediately and returns the survivor to its anchor.

Neutral survivors share this component but never recruit: `TryStart` requires `CharacterFactionUtility.IsPlayerOwnedSurvivor`.

## Activation Rules

There are two ways recruiting can **start**, both requiring `RecruitNeutralSurvivors` enabled, an unpossessed player-owned survivor, not being in combat (no line-of-fire target), and no enemy player sensed.

**Order satisfied (full sense range):**

- The player order is already satisfied — `HoldPosition`, or an assigned area the survivor has reached. A `MoveTo` order must have reached its goal (it becomes `HoldPosition`); a patrol order must have reached its area.
- The survivor is not in an **active** investigation. Returning from an investigation toward the order anchor is allowed to start recruiting.
- Any living neutral the survivor senses is a valid target (no distance limit beyond the senses).

**In travel (detour, distance-limited):** see Travel Detour.

## Travel Detour

A survivor *travelling* to a player order (still moving to a `MoveTo` point, into a not-yet-reached assigned area, or following) will not normally recruit — the order takes priority. But it would otherwise walk straight past neutrals it senses along the way. The detour fixes that: while travelling, if a recruitable neutral is within `RecruitDetourDistance` (flat distance), the survivor detours to recruit it, then resumes the original order. Recruited survivors inherit that same order, so they also detour while travelling to it.

Rules:

- `RecruitDetourDistance > 0` enables it (`0` disables). The detour only considers neutrals within that range; the order-satisfied recruiting above has no such limit.
- The detour is interrupted by the same thing as any recruitment — a sensed enemy player — after which the order resumes (combat merges into the ordered movement).
- Unlike order-satisfied recruiting, a travel detour that **loses** its target (killed, lost sight, unreachable) does **not** branch into an investigation; the survivor simply resumes the order. Finishing the player order takes priority over chasing a lost detour target.
- A short retry cooldown after each detour prevents a sensed-but-unreachable neutral from making the survivor start-and-abort a detour every tick.
- When a detour while heading into an assigned area carries the survivor into the area, it seamlessly becomes normal in-area (order-satisfied) recruiting.

## Priority And Persistence

Recruiting outranks looting and investigation. While a recruitment is **active**:

- New investigation stimuli (gunshots, bullet impacts, ally alerts) are ignored — the survivor does not divert to investigate.
- Looting is ignored — the survivor will not stop to grab a weapon or health it lacks.
- Zombies do not divert the survivor. It still aims and fires at zombies it can hit, but it never lets the zombie combat AI move it — no retreating or repositioning. It keeps beelining to the neutral. The priority is the recruit; zombies are just shot at in passing.

The only thing that stops an active recruitment is sensing an **enemy player survivor**. When that happens the recruitment is dropped and the combat AI takes over (the survivor returns to its anchor afterward). Enemy-player detection ignores a closer zombie, so a visible enemy player behind a zombie still interrupts recruiting.

## Behavior Flow

Each tick, `SurvivorRecruitingAI.Tick` reports one of three outcomes to `SurvivorNonCombatAI`:

1. `SurvivorNonCombatAI` confirms the activation rules and asks `SurvivorRecruitingAI` to start.
2. The behavior selects the closest sensed living neutral, records its position as the last known location, and paths to its live position.
3. **Pursuing** — while the target is alive, still neutral, and still sensed, it re-points the navigator at the neutral, walks toward it (shooting at zombies but never retreating), and keeps the last known location updated to the sensor's latest reading.
4. The orchestrator recruits the neutral once any player-owned survivor is in range.
5. **Recruited** — the target joined **our** team (recruited by us or one of our own survivors). Recruiting succeeded, so the behavior clears and the survivor returns to its order anchor.
6. **Lost** — the target died, the survivor lost sight of it (it dropped out of the sensor's memory), it became unreachable, or some **other** team recruited it. See *Losing The Target*.

After returning, the survivor resumes its order and may immediately target the next sensed neutral.

## Losing The Target

If the recruit is killed by anything, or the survivor loses sight of it, the behavior does **not** simply give up. Instead `SurvivorNonCombatAI` starts an **investigation** at the recruit's last known location. The survivor walks there and looks around, giving it (and, depending on what it sees there) a fresh chance to spot the recruit or other survivors before returning to its order.

Notes:

- "Lost sight" uses the survivor's sensor memory, so there is a short grace period (the standard sensor memory duration) before a briefly-occluded recruit is treated as lost. The survivor keeps pursuing during that window.
- The last known location is the most recent position the sensor reported for the target, not a cheated live position.
- The investigation handoff reuses the investigation behavior's movement and look-around, but it is part of recruiting: it runs whenever recruiting is enabled and **does not depend on the `InvestigateSuspiciousStimuli` setting**. Settings do not cross-cancel each other. (A closer combat threat can still preempt it, like any investigation.)
- If the target was taken by **another team**, it is now an enemy survivor. If it is still in the survivor's senses, `SurvivorNonCombatAI` hands off to combat first (an enemy player interrupts recruiting); otherwise the loss investigation sends the survivor to look where it last saw it.
- A successful recruitment onto our own team (the **Recruited** outcome) never triggers this — only death, loss of sight, or another team taking the recruit does.

## Recruited Survivor's Order

When the recruiter is an unpossessed player survivor, the freshly recruited survivor inherits the recruiter's current player-given assignment through the existing `Gameplay.ApplyRecruitmentOrder` -> `SurvivorNonCombatAI.CreateEquivalentAssignmentFor` path. Because recruiting is a temporary deviation that never changes the recruiter's underlying assignment, the recruited survivor gets the same hold/move/patrol/follow order the recruiter had. (If the recruiter was the possessed player, the recruit follows the player instead.)

## Network Model

Recruiting runs only on state authority. Do not network candidate lists, the current recruit target, or return scratch state. The authoritative result is the normal replicated survivor movement plus the orchestrator's ownership/team change on recruitment.

## Known Limitations / Future Expansion

- No target reservation: several AI survivors can path to the same neutral. The first into range recruits it; the others return to their tasks. Team-level reservation could be added later.
- Recruiting survivors do not defend themselves against zombies while en route, by design. A "shoot while moving without stopping" variant could be added if survivability becomes an issue.
