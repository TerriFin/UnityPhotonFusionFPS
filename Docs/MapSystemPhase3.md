# Map System Phase 3 - Future Character States

## Goal

Add tactical state controls for selected survivors after the visual map, icons, selection, and basic orders are working.

This is a future phase. It should not be implemented before `Docs/MapSystemPhase1.md` and `Docs/MapSystemPhase2.md` are complete.

## Design Direction

The phase 2 map already provides local selection and state-authority command validation. Phase 3 should reuse that pipeline:

```text
local map UI selection
-> player chooses a state
-> request sent to state authority
-> server validates selected survivors
-> server applies state through a central survivor command/state service
```

Do not let clients directly mutate survivor AI state.

## Planned State Groups

The detailed AI behavior plans live in:

- `Docs/SurvivorNonCombatAI.md`
- `Docs/SurvivorLootingAI.md`
- `Docs/SurvivorInvestigationAI.md`
- `Docs/SurvivorCombatAI.md`

This phase is only the UI/control surface for changing those per-survivor settings.

### Fire Mode

Initial ideas:

```text
Automatic (default)
Prefer strong weapons
Prefer pistol
```

Possible meaning:

- `Automatic`: conserve ammunition against ordinary zombies, but use range-appropriate strong weapons against enemy survivors, dangerous zombie groups, close zombies, and overtime.
- `Prefer strong weapons`: use the best range-appropriate collected weapon against all targets.
- `Prefer pistol`: use the pistol against all targets.

See `Docs/SurvivorWeaponPreferenceAI.md`.

### Combat AI

Initial UI ideas:

```text
Defensive (default)
Passive
Aggressive
```

Possible mapping:

- `Defensive`: return fire, preserve current order, do not chase too far.
- `Passive`: avoid initiating combat, possibly only react to direct danger.
- `Aggressive`: pursue/pressure visible enemies more actively.

Under the hood these should map to combat settings such as zombie behavior, enemy survivor behavior, weapon usage, target priority, and lost-enemy behavior.

Combat activation should be a separate setting from combat style. If disabled, the survivor should continue its current non-combat player order even when it notices enemies.

### Non-Combat AI

Initial UI ideas:

```text
Investigative (default)
Passive
```

Possible mapping:

- `Investigative`: look toward noises/bullet impacts and possibly investigate later.
- `Passive`: ignore non-direct stimuli unless attacked or directly seeing an enemy.

Under the hood these should map to individual non-combat settings such as pickup collection, investigation, ally alerting, and assigned-area patrol behavior. These settings should be independently toggleable; preset modes are just UI conveniences.

The implementation should keep the base AI/behavior split intact. `SurvivorNonCombatAI` and future `SurvivorCombatAI` are the orchestrators. Focused behavior classes such as `SurvivorLootingAI` and `SurvivorInvestigationAI` own behavior-specific state and Inspector tuning.

These lists are intentionally not final. Keep the implementation data-driven enough to rename, remove, or add states.

## UI Model

The state UI can be:

- part of the full-screen map,
- a radial menu opened while the map is visible,
- a separate command panel that operates on the current map selection.

Do not hardcode the UI shape into the survivor AI classes.

Recommended flow:

```text
Player selects survivors on map.
Player chooses a state option.
Map UI sends selected survivor ids + chosen state.
State authority validates ownership/alive/inactive rules.
State service applies the state.
```

## Data Model

State should probably be represented as small enums.

Example:

```csharp
public enum FireMode
{
    Default,
    HoldFire,
    UseMostPowerful,
}

public enum CombatAIMode
{
    Normal,
    Aggressive,
    Defensive,
    None,
}

public enum NonCombatAIMode
{
    Investigative,
    Passive,
}

public struct SurvivorAISettings
{
    public bool CollectVisiblePickups;
    public bool InvestigateSuspiciousStimuli;
    public CombatAIMode CombatBehavior;
}
```

Whether these become `[Networked]` depends on UI needs:

- If only state authority needs them for behavior, they can start as local server-side fields.
- If clients need to display each survivor's current state, add a small networked enum state later.

Avoid networking complex AI objects or command classes.

## Relation To Existing Systems

The state system should not replace current AI commands.

Movement commands answer:

```text
What should this survivor move toward right now?
```

State controls answer:

```text
How should this survivor behave while executing orders or reacting to threats?
```

Examples:

- `SurvivorNonCombatAI` move assignments still own movement toward a map-clicked point.
- `FireMode` influences weapon choice and whether auto-shooting is allowed.
- `CombatAIMode` influences whether the survivor holds position or pushes toward visible enemies.
- `NonCombatAIMode` influences whether noises/bullet impacts create investigation behavior.
- Individual setting toggles must not replace the current movement assignment. Turning pickup collection off can cancel an active pickup detour, but it should not cancel a player-issued move/follow/assigned-area order.

## Command Service Extension

Recommended future API shape:

```csharp
SurvivorAICommandService.ApplyStateToSurvivors(
    PlayerRef owner,
    IReadOnlyList<int> characterIndices,
    SurvivorAIStateChange stateChange)
```

Validation should match phase 2 map orders:

- sender owns each survivor,
- survivor is alive,
- survivor is not the active possessed survivor unless the specific state is allowed for active characters,
- state value is valid.

## Out Of Scope Until This Phase

- Implementing weapon-choice AI.
- Implementing aggressive pursuit.
- Implementing passive/investigative state machines.
- Networked display of state icons.
- Radial menu art/polish.
- Command queues.

## Acceptance Criteria

This phase is ready when:

- Selected survivor states can be changed from the map UI.
- State authority validates and applies state changes.
- States influence survivor behavior without changing map selection or movement-order code.
- The active possessed survivor is handled deliberately rather than accidentally receiving AI-only state changes.
