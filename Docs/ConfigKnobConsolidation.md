# Config Knob Consolidation (Cleanup Step 1)

## Purpose

First step of the codebase cleanup/optimization pass (July 2026). The problem: several gameplay values could be
set from two or more different places, so tweaking one of them in the editor silently did nothing (or worse,
worked only in some play modes). Goal: every tunable value has exactly **one** authoritative, editor-facing knob,
without losing any manual control.

Each consolidation below lists where the value used to live, where it lives now, and why the change is
behavior-neutral.

---

## 1. Zombie stats — `ZombieOrchestratorSettings` is the single authority

**Files:** `ZombieCharacter.cs`, `ZombieOrchestrator.cs`, `ZombieOrchestratorSettings.cs`

### Before

Zombie stats were split across two assets with no visible rule:

- `ZombieOrchestratorSettings` (the lobby-selectable preset asset) owned health / damage / move speed / alert
  radius, and the orchestrator stomped the spawned zombie's `Stats` with those on every spawn.
- The `BlockZombie` **prefab**'s serialized `ZombieCharacter.Stats` secretly supplied `AttackRange`,
  `AttackCooldown`, and (overtime only) `AlertRadius`. Its `MaxHealth`/`Damage`/`MoveSpeed` fields were **dead
  knobs** — always overwritten by the orchestrator — so tuning them on the prefab did nothing.

### After

- `ZombieOrchestratorSettings` gained `AttackRange` (default **1.3**), `AttackCooldown` (default **1.1**), and
  `OvertimeAlertRadius` (default **8**). The defaults equal the values previously serialized on the
  `BlockZombie` prefab, so every existing settings asset (which deserializes missing fields to the code
  defaults) produces identical zombies.
- `ZombieCharacter.Stats` is now `[NonSerialized]` — the prefab no longer shows stat fields at all. The field
  initializer only covers zombies spawned outside the orchestrator (test scenes); its `AttackRange` default was
  updated 1.2 → 1.3 to match the old prefab value.
- `ZombieOrchestrator.GetCurrentStats()`/`GetOvertimeStats()` read the new settings fields instead of doing
  `GetComponent<ZombieCharacter>()` on the prefab asset per call (also a micro-optimization: that lookup ran for
  every spawned zombie).

**Rule going forward:** all zombie *stat* tuning happens in the `ZombieOrchestratorSettings` assets
(`Assets/Settings/ZombieSpawningPresets/*`, plus the scene-assigned `ZombieOrchestrationSettings`). The prefab
keeps only physical/feel knobs that are not stats (gravity, acceleration, despawn delay, mantle duration).

---

## 2. Map size / seed / tile size — `HeightMapGenerator` is the single knob

**Files:** `RoadGridGenerator.cs`, `MatchRuntimeSettings.cs`

### Before

`Width`, `Height`, `Seed`, `RandomizeSeedOnGenerate`, and `TileSize` existed as editable fields on **both**
`HeightMapGenerator` and `RoadGridGenerator`, and `MatchRuntimeSettings` wrote `MapWidth`/`MapHeight` to **both**
components on scene load — three places for one value. The road generator's copies were already overwritten at
runtime (`Awake` copied them from the height generator, and `ApplyHeightSnapshotRunValues` stamped them again
from the generated height snapshot), so editing them on `RoadGridGenerator` was a trap: it only did anything in a
scene with no `HeightMapGenerator`.

### After

- The five mirrored fields on `RoadGridGenerator` are `[HideInInspector]`. They still exist (internals and the
  no-height-generator fallback use them) but are no longer editor-facing knobs. A comment on the fields explains
  the mirroring.
- `RoadGridGenerator.Generate()` now calls `SyncRunValuesFromHeightGenerator()` (extracted from `Awake`) so
  **edit-mode** context-menu generation also follows the height generator — previously edit mode used the road
  generator's own copies because `Awake` does not run there.
- `MatchRuntimeSettings.ApplyToScene` no longer writes `Width`/`Height` to the road generator; only
  `HeightMapGenerator` receives map size from the lobby settings.

Verified external readers (`GameMapCameraController` map bounds, seed reads in loot/building/zombie/neutral
spawners) all read these values **after** generation, when the snapshot has stamped them — behavior unchanged.

---

## 3. Map "see everything" — one knob, `GameMapView.RevealMode`

**Files:** `GameMapView.cs` (unchanged — already the knob), `GameMinimapView.cs`, `GameMapAwarenessTracker.cs`,
doc update in `MapSystemPhase2.md`

### Before

Two independent reveal toggles: `GameMapView.RevealMode` (Auto/ForceOff/ForceOn) for the full map, plus a
separate `GameMinimapView.RevealEverything` bool for the minimap. Because a revealing minimap wrote into
awareness memory, the minimap also had to keep a *private* tracker while revealing (plus a third knob,
`SharedAwarenessTracker`, to control tracker sharing).

### After

- `GameMinimapView.RevealEverything` and `GameMinimapView.SharedAwarenessTracker` are **removed**.
- The single knob is `GameMapView.RevealMode`. The minimap reveals exactly when it is `ForceOn`
  (`GameMinimapView.ShouldRevealEverything()` reads it through the `MainMapView` reference, which `GameUI`
  auto-wires). The `Auto` spectator-reveal case never applies to the minimap since defeated spectators have no
  minimap (`Suppressed`).
- The minimap now **always** adopts the full map's `GameMapAwarenessTracker`. The old anti-leak rule existed
  only because the two views could reveal independently; with one knob they reveal together, so sharing is
  always correct.
- `GameMapAwarenessTracker.Tick` gained a same-frame guard (`_lastTickFrame`) so the shared tracker never
  duplicates its sensor scan when both views tick it in one frame.

Stale serialized leftovers (`SharedAwarenessTracker: {fileID: 0}` in `GameplayView.prefab`, a `RevealEverything`
override in the binary Deathmatch scene) are ignored by Unity once the fields are gone — harmless.

---

## 4. Lobby presets vs. inspector values — made visible instead of silent

**Files:** `MatchRuntimeSettings.cs`

The remaining *intentional* two-place pattern: scene components (`Gameplay.GameDuration`,
`HeightMapGenerator.Width`, orchestrator `Settings` assets, …) hold values for direct-scene play, and
`MatchRuntimeSettings.ApplyToScene` overrides them from the lobby's `MatchHostingSettings` when a match is
hosted through the menu. This is by design (the host must be able to configure a match), but the override was
silent — the classic "I changed the inspector value and nothing happened" trap.

`ApplyToScene` now logs **one** summary line stating the applied map size and every preset index, ending with
"These override the corresponding inspector values for this match." No behavior change.

**Rule going forward:** inspector values on generators/orchestrators are the *direct-scene-play defaults*;
anything the lobby exposes wins in a hosted match, and says so in the log.

---

## Verification

- `dotnet build Assembly-CSharp.csproj` — compiles clean.
- Grepped for all readers of removed/changed members: no remaining references to
  `GameMinimapView.RevealEverything`, `SharedAwarenessTracker`, or prefab `Stats` reads in the orchestrator.
- Zombie stat values before/after are identical for all existing settings assets (new fields deserialize to the
  code defaults, which were chosen to match the old prefab values).

## Editor follow-ups (optional, not required for correctness)

- The `BlockZombie` prefab still carries its old serialized `Stats` block; Unity ignores it now. Opening and
  re-saving the prefab will drop the stale data.
- If a zombie preset should have different melee reach/cooldown per preset (e.g. `FewTAAAAANKS`), those knobs
  are now available per preset asset.
