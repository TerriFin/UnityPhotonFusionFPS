# Raid Mode Client Alliance

## Status

Implemented. The runtime behavior, authoritative damage protection, AI relationships, dynamic phasing, and
supporting feature documentation are in place.

## Goal

While the raid host still has at least one living survivor, every non-host player team forms a temporary
combat alliance:

- client survivors do not acquire or attack other client survivors;
- client weapon fire cannot damage allied client survivors, whether the shooter is possessed or AI-controlled;
- allied client survivors receive the same relevant friendly behavior as survivors owned by one player;
- the raid host and every client remain hostile to each other.

When the raid host loses its final survivor and enters defeated spectate mode, the alliance ends immediately.
Every surviving client team then becomes hostile to every other client team and the existing last-team-standing
win condition continues normally.

This is a **combat alliance**, not an ownership merge. Each client remains a separate player team throughout the
match.

## Rule Matrix

The authoritative relationship is derived from the current raid phase:

```text
client alliance active =
    gameplay is running
    AND raid mode is enabled
    AND the host player can be resolved
    AND the host is connected
    AND the host PlayerData.IsAlive is true
```

| Pair | Host alive in raid mode | Host defeated/disconnected | Non-raid match |
| --- | --- | --- | --- |
| Same owner's survivors | Allied | Allied | Allied |
| Two different client owners | Allied | Hostile | Hostile |
| Host survivor and client survivor | Hostile | Host team is eliminated | Hostile |
| Player survivor and zombie | Hostile | Hostile | Hostile |
| Neutral survivor relationships | Existing behavior | Existing behavior | Existing behavior |

If the host cannot be resolved or its `PlayerData` is unavailable, fail closed to the existing per-owner
relationship. This must never accidentally make the raid host friendly with clients.

Host disconnection also dissolves the alliance because the host is no longer connected/alive. If only one client
team remains when the host dies, the existing `CheckWinCondition()` ends the match immediately; there is no
unnecessary PvP phase.

## Ownership That Must Not Change

The alliance must not replace or rewrite `Survivor.OwnerRef`, `Object.InputAuthority`, `CharacterIndex`, or
`PlayerData`. In particular, allied clients do **not** gain:

- possession or command access to another client's survivors;
- a shared roster, home base, spawn point, team color, score, or kill count;
- shared recruitment ownership;
- shared map visibility, fog-of-war knowledge, or selectable map units;
- a combined win-condition entry.

Keeping ownership separate is what allows the clients to compete for positioning, loot, recruits, and survival
while direct combat between them is temporarily disabled.

The raid client rescue mechanic is the explicit exception, and it is not part of the passive combat alliance:
when a non-host client is wiped while the raid host is alive, `Gameplay` may transfer one valid non-possessed
allied survivor (or a later allied recruit) into that client's roster so the human can keep playing. That transfer
is documented separately in `Docs/RaidModeClientRescue.md`.

## Implemented Integration Points

The implementation uses these existing and updated paths:

- `Gameplay.CharacterKilled()` sets the defeated owner's replicated `PlayerData.IsAlive` to false before it calls
  `CheckWinCondition()`.
- `SpectatorController` already uses that state to move a defeated raid host from `RaidCommander` to
  `DefeatedSpectator`.
- `Gameplay.RaidHostPlayer` replicates the host identity to every peer.
- `CharacterFactionUtility` centralizes survivor alliance, auto-attack, enemy-noise, and weapon-damage decisions.
- `CharacterSensor.IsEnemy()` and remembered-target cleanup use the centralized relationship.
- `SurvivorCombatMovementAI` and `SurvivorInvestigationAI` use the relationship when finding allies.
- `CharacterSeparation` uses the relationship for friendly separation and phasing and refreshes persistent
  collision-ignore pairs when the raid phase changes.
- `Weapons.StepActiveProjectiles()` performs authoritative lag-compensated projectile collision and
  `Weapons.ApplyDamage()` calls `CharacterFactionUtility.CanSurvivorWeaponDamage()`.

One subtle weapon detail matters: `HitOptions.IgnoreInputAuthority` makes a player's bullets ignore their own
survivors at query time. Different allied clients have different input authorities, so merely rejecting damage
would make bullets stop on an allied client. The implemented projectile query therefore skips temporary allies
to match same-owner behavior.

## Implementation

### 1. Centralize the raid phase in `RaidModeRules`

`Assets/Scripts/Gameplay/RaidModeRules.cs` exposes:

```text
TryGetRaidHostPlayer(Gameplay, out PlayerRef)
IsClientAllianceActive(Gameplay)
ArePlayerTeamsAllied(Gameplay, PlayerRef first, PlayerRef second)
```

Implemented behavior:

- State authority assigns `Gameplay.RaidHostPlayer` from `Runner.LocalPlayer` when `Gameplay` spawns. The
  installed Fusion API does not expose its internal host lookup through `NetworkRunner`, so this single
  replicated `PlayerRef` gives every peer an unambiguous host identity.
- Preserve the existing rule that the host is never part of the client alliance.
- Return allied for identical real-player owners independently of raid mode.
- Return temporarily allied for two distinct, real, non-host players only while the alliance is active.
- Read the existing replicated `PlayerData.IsAlive`; no `[Networked]` alliance phase, RPC, or timer is added.
- `IsRaidControlledPlayer()` shares the replicated host-resolution helper without changing raid possession
  behavior.

The alliance state then changes in the same authoritative operation that eliminates the host. Replication of the
existing `PlayerData` propagates that phase to client-side systems such as local collision filtering.

### 2. `CharacterFactionUtility` relationship boundary

`Assets/Scripts/Gameplay/CharacterFactionUtility.cs` exposes the central survivor relationship query:

```text
AreSurvivorsAllied(Survivor first, Survivor second)
```

It:

1. preserve same-owner friendship;
2. preserve current neutral-survivor behavior;
3. resolve `Gameplay` from the survivors' runner/`SceneObjects`;
4. delegate different-player raid relationships to `RaidModeRules`.

The existing utility methods consume the same relationship:

- `CanSurvivorAutoAttack()` rejects allied client survivors;
- `CanSurvivorWeaponDamage()` rejects same-owner and temporarily allied survivor targets while preserving
  zombie, neutral, and world-object behavior;
- `IsEnemyNoiseSourceForSurvivor()` ignores fire/noise from allied client survivors.

`CanPlayerDamageSurvivor()` provides the player-owner form used by the final health guard, because
`Health.ApplyDamage()` receives a `PlayerRef` instigator rather than the attacking `Survivor`.

Do not replace ownership checks that govern rosters, commands, possession, map selection, recruitment, home bases,
or scoring. Those are ownership rules, not combat-faction rules.

### 3. Perception and target selection

In `Assets/Scripts/AI/Sensory/CharacterSensor.cs`:

- the direct survivor enemy decision uses the centralized relationship;
- survivor-versus-zombie and zombie-versus-survivor behavior remains unchanged;
- remembered entries are rejected if their survivor is no longer an enemy under the current relationship.

The second point prevents a stale remembered target from driving investigation or combat if the alliance becomes
active during match initialization/restart. When the host is defeated, client survivors can discover one another
on the next normal sensor scan (currently at most the configured `SensorInterval`, normally 0.2 seconds).

`SurvivorAIShooting.TryGetDirectTarget()` and the recruiting interruption checks already call
`CanSurvivorAutoAttack()`, so they inherit the new rule without separate raid branches.

### 4. Friendly behavior without shared control

`AreSurvivorsAllied()` replaces direct owner equality only in behavior that already treats nearby units as
allies:

- `Assets/Scripts/Survivor/AI/SurvivorCombatMovementAI.cs`
  - includes temporarily allied client survivors in ally-spacing calculations;
- `Assets/Scripts/Survivor/AI/SurvivorInvestigationAI.cs`
  - allows nearby temporarily allied AI survivors to receive the existing investigation/look alert.

These behaviors stop crossing player boundaries automatically when the host dies. Do not broaden follow orders,
map orders, retreat home bases, roster settings, or any other player-issued command to allied owners.

### 5. Friendly phasing and separation

In `Assets/Scripts/AI/Navigation/CharacterSeparation.cs`:

- uses the centralized survivor relationship in `CanPhaseWith()`;
- temporarily phases and gently separates survivors belonging to different allied clients;
- keeps host/client, post-host-defeat client/client, neutral/player, and survivor/zombie collision rules unchanged.

`Physics.IgnoreCollision()` state is persistent, so a dynamic relationship check in the KCC callback is not
enough. The component caches the last observed raid-alliance phase and calls `RefreshCollisionPairs()` plus
`InvalidateSeparationCache()` when it changes, restoring solid collision between client teams after the host
dies.

No scene, prefab, or layer change is needed.

### 6. Allied bullets behave like same-owner bullets

`Assets/Scripts/Weapons/Weapons.cs` preserves the existing fast single-hit raycast for ordinary shots and uses an
allied-hit fallback:

1. Perform the current lag-compensated `Raycast`.
2. Resolve a hit survivor from either `LagCompensatedHit.Hitbox` or its PhysX `Collider`/`GameObject`.
3. If the first hit is not allied, process it exactly as today.
4. If the first hit is allied, use `LagCompensation.RaycastAll()` for that projectile segment, with a reusable
   scratch list, and choose the nearest hit that is not an allied survivor.
5. If no non-allied hit remains, keep the projectile alive for the next simulation step.

This fallback avoids the extra all-hit query for the normal case while allowing a bullet to pass through one or
more allied client survivors and still hit a wall, zombie, raid-host survivor, or post-alliance enemy behind
them. Continue using `HitOptions.IgnoreInputAuthority` for same-owner filtering.

Only the selected blocking hit may:

- terminate the projectile/tracer;
- write `ProjectileHitData`;
- show hit/critical/kill feedback;
- report a bullet-impact sensory event;
- apply damage.

Keep `CanSurvivorWeaponDamage()` as an authoritative guard even after query filtering. Add a final owner-alliance
guard near the start of `Health.ApplyDamage()` for player-instigated damage against a survivor. That defense in
depth ensures a future player-owned damage path cannot bypass the alliance merely because it does not originate
from `Weapons`.

Zombie attacks and environmental damage use `PlayerRef.None` and must remain unaffected.

### 7. Define the exact transition semantics

Relationship checks use the state at the moment an authoritative target, collision, or damage decision is made:

- a projectile that hits another client while the host is alive passes through and deals no damage;
- a projectile fired before the host's death but resolved after `hostData.IsAlive` becomes false may hit and
  damage that now-hostile client;
- host/client projectiles remain lethal throughout;
- AI stops treating other clients as allies as soon as the authoritative relationship query observes the host's
  defeat, then acquires them through the normal sensor cadence;
- ignored client/client physics pairs are restored when the replicated phase change reaches each peer.

No grace timer exists. If a later playtest shows that already-in-flight bullets make the transition feel
unfair, snapshotting the alliance phase per shot can be considered separately rather than silently adding it now.

### 8. Feature documentation

The implemented behavior is synchronized across `Docs/RaidMode.md`, `Docs/CharacterSensorySystem.md`,
`Docs/ProjectileSystem.md`, `Docs/CharacterSeparationSystem.md`, `Docs/SurvivorCombatAI.md`, and
`Docs/SurvivorCombatMovementAI.md`.

Do not modify `Docs/HumanToDo.md`.

## Implemented File Scope

Runtime files changed:

```text
Assets/Scripts/Gameplay/Gameplay.cs
Assets/Scripts/Gameplay/RaidModeRules.cs
Assets/Scripts/Gameplay/CharacterFactionUtility.cs
Assets/Scripts/AI/Sensory/CharacterSensor.cs
Assets/Scripts/AI/Navigation/CharacterSeparation.cs
Assets/Scripts/Survivor/AI/SurvivorCombatMovementAI.cs
Assets/Scripts/Survivor/AI/SurvivorInvestigationAI.cs
Assets/Scripts/Weapons/Weapons.cs
Assets/Scripts/Survivor/Health.cs
```

Documentation changed:

```text
Docs/RaidModeClientAlliance.md
Docs/RaidMode.md
Docs/CharacterSensorySystem.md
Docs/ProjectileSystem.md
Docs/CharacterSeparationSystem.md
Docs/SurvivorCombatAI.md
Docs/SurvivorCombatMovementAI.md
```

No scene, prefab, menu, hosting-setting, or Photon package change was required.

## Network Model

Implementation proceeded with the user's explicit permission to modify network-facing code. The only added
replicated state is `Gameplay.RaidHostPlayer`, one `PlayerRef` assigned once by state authority. No alliance phase
field or RPC was added. AI, projectile collision, damage, death, and win evaluation remain state-authoritative.

## Verification Plan

### Build and static checks

- `dotnet build PhotonFusionFPSTest.sln --no-restore` passes with zero warnings and zero errors.
- Search remaining direct `OwnerRef` equality checks and confirm each one is deliberately either an ownership rule
  or an unchanged zombie grouping rule.
- Confirm `RaidHostPlayer` is the only new `[Networked]` field and no RPC, session property, scene edit, or prefab
  edit was introduced.

### Multiplayer behavior

Use at least one host and two clients; one host plus one client cannot exercise the post-host PvP phase because
the existing win condition ends the match when the host dies.

While the host is alive:

- host AI targets and damages both client teams;
- possessed and AI client survivors target/damage host survivors and zombies;
- possessed client fire cannot damage another client team's possessed or AI survivor;
- AI client survivors never select another client survivor as a combat target;
- allied fire/noise does not trigger enemy investigation;
- ally alerts and combat spacing can include another client team without granting command access;
- client survivors phase/separate like friendlies;
- bullets pass through allied clients and can still hit a hostile raid survivor, zombie, or wall behind them;
- no allied hit marker, damage reaction, kill feed entry, or kill statistic is produced.

After the host's final survivor dies:

- the host enters existing defeated spectate mode;
- surviving client teams become valid AI and sensory targets;
- possessed and AI client fire damages other client teams;
- client teams hard-collide rather than phase;
- client kills update the existing kill feed/statistics;
- the last living client team wins through the unchanged win condition.

Regression cases:

- non-raid matches retain normal per-owner hostility;
- same-owner survivors remain friendly in every mode;
- neutral survivor recruitment/damage rules remain unchanged;
- zombies remain hostile to every survivor and can still damage allied clients;
- host disconnection dissolves the alliance;
- a late-joining client, if the existing connection rules allow one, joins the alliance while the host is alive;
- restarting a match does not leave stale enemy memories or ignored collision pairs;
- an in-flight client projectile follows the documented hit-time transition rule.

For three or more local instances, follow `Docs/LocalMultiplayerTesting.md`: use a separate machine or dedicated
server when possible, and do not treat single-PC scheduling-related RTT as a networking regression.
