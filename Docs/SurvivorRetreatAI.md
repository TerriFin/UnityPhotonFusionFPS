# Survivor Retreat AI

## Goal

`SurvivorRetreatAI` lets an injured, unpossessed player-owned survivor abandon its current assignment and receive a persistent assigned-area patrol order for its team's home base.

Retreat is a five-state mode:

```csharp
public enum ESurvivorRetreatMode
{
	NoRetreat = 0,
	RetreatAt25Percent = 1,
	RetreatAt50Percent = 2,
	RetreatAt75Percent = 3,
}
```

Starting player survivors default to `NoRetreat`. Recruited survivors inherit their recruiter's mode.

This mode is independent from combat behavior, weapon preference, looting, investigation, and recruiting.

## Threshold

The selected percentage is compared against current maximum health:

```text
health ratio = CurrentHealth / MaxHealth
retreat when health ratio < selected threshold
```

The comparison is strict. A survivor at exactly `50%` health with `RetreatAt50Percent` does not retreat until later accepted damage lowers it below `50%`.

`NoRetreat` has no health threshold and never starts a retreat.

## Trigger Events

Retreat eligibility is evaluated only when:

- accepted damage changes the survivor's health,
- the retreat mode is changed to a percentage mode,
- the authoritative home base is moved or resized.

Unpossessing a survivor does not perform a retreat check.

An evaluation starts retreat only when all of these are true:

- The mode is not `NoRetreat`.
- The survivor is alive and player-owned.
- The survivor is unpossessed.
- Its health ratio is strictly below the selected threshold.
- It is outside the current home-base footprint.
- The home-base area can produce a valid assigned-area patrol order.

Do not trigger from blocked damage, healing, neutral-survivor damage, sensor stimuli, or damage received while possessed.

## Mode Changes

Changing the mode is an authoritative gameplay event.

Selecting `25%`, `50%`, or `75%` immediately evaluates the survivor. This lets an already-injured unpossessed survivor retreat without taking another hit.

Selecting `NoRetreat` cancels an assignment only when that assignment was created by `SurvivorRetreatAI`:

1. Clear the retreat-assignment origin marker.
2. Replace the retreat-created assigned-area patrol with a persistent move/guard order at the survivor's current position.
3. Cancel retreat-owned entry travel and patrol state.

It does not cancel a manually issued patrol, even when that patrol uses the home-base circle.

Changing between percentage modes does not cancel an already active retreat. The new threshold controls immediate eligibility and later trigger events.

Changing the mode while possessed stores the mode but does not interrupt direct control or schedule an unpossess check.

## Home Base Changed Event

After state authority accepts a new home-base center or radius, it reevaluates every living survivor owned by that player once.

An injured unpossessed survivor with a percentage retreat mode is redirected to the new home base when it remains below its selected threshold and outside the new footprint. This includes survivors travelling to or patrolling the previous home base.

Healthy, possessed, `NoRetreat`, dead, neutral, and already-inside survivors keep their current assignments.

## Retreat Assignment

Retreat uses the normal assigned-area system:

- build the same reachable patrol-point set as a player-issued patrol,
- travel to the home-base area,
- patrol it after arrival,
- retain ordinary combat targeting and shooting,
- prevent tactical combat movement from replacing the travel movement.

The assignment carries a local retreat-origin marker. Any new explicit player order replaces it naturally.

Repeated damage does not rebuild the same retreat assignment when its center and radius already match the current home base.

If the home base cannot produce a valid assigned-area patrol set, the survivor keeps its current assignment.

## Combat Interaction

Retreat is strategic movement and outranks tactical combat movement:

1. Possessed player input.
2. A new explicit player movement order.
3. Active retreat travel to the home-base area.
4. Existing player assignments.
5. Combat movement.
6. Optional non-combat detours.

The survivor may still aim, choose weapons, and fire while retreating. `CombatBehavior.None` does not disable retreat.

Once inside the home base, the survivor patrols normally and continues using its selected combat behavior.

## Possession

Retreat never overrides direct control.

- Damage while possessed does not install a retreat order.
- Possessing a survivor with an existing retreat assignment temporarily gives the player control.
- That stored assignment may resume later as an ordinary assignment.
- Unpossessing never performs a fresh threshold evaluation.
- Selecting `NoRetreat` while possessed still replaces a retreat-created stored assignment with a guard point.

## Recruitment

Neutral survivors do not retreat before recruitment.

A newly recruited player survivor inherits the recruiter's retreat mode and uses its new owner's home base. The inherited mode is applied after the movement assignment is copied, so a recruit already below the inherited threshold may immediately retreat.

## Roster UI

Every survivor card and the bulk behavior bar contain the same cycling control:

```text
No Retreat
25%
50%
75%
```

The compact labels are:

```text
NONE
25%
50%
75%
```

The control cycles:

```text
NONE -> 25% -> 50% -> 75% -> NONE
```

Bulk target set:

```text
selected alive owned survivors, if selection is not empty
otherwise all alive owned survivors
```

The bulk display shows the most common mode in that target set. Ties prefer `NoRetreat`, then the first mode in enum order.

## Networking

`Survivor.RetreatMode` is a replicated enum. State authority validates and applies individual or bulk requests.

The home-base center and radius remain authoritative per-player values.

Do not network:

- retreat eligibility scratch state,
- health calculations,
- patrol candidates,
- path corners,
- retry timers,
- the local retreat-assignment origin marker.

## Acceptance Criteria

- Retreat has `25%`, `50%`, `75%`, and `No Retreat` modes.
- `No Retreat` is the default for starting survivors.
- Newly recruited survivors inherit the recruiter's mode.
- Thresholds use health percentage, not absolute HP.
- The threshold comparison is strict `<`.
- Accepted damage can trigger retreat for an eligible unpossessed survivor.
- Selecting a percentage mode immediately evaluates an already-injured survivor.
- Selecting `NoRetreat` cancels only a retreat-created assignment and installs a current-position guard order.
- Unpossessing never triggers retreat evaluation.
- Moving the home base redirects qualifying injured survivors without requiring more damage.
- Survivors already inside the home base do not retreat.
- New player orders replace retreat.
- Combat can continue while retreating.
- Individual and bulk roster controls use validated authoritative enum requests.
