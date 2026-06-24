# Network Optimization Audit

## Purpose

This document records the current network replication footprint and the most promising optimization paths. It is an investigation, not an implementation plan. No runtime behavior described here has been changed.

The broader CPU, AI, and movement scaling guidance remains in `Docs/FutureScaleOptimizationNotes.md`.

Audit date: June 24, 2026.

## Executive Summary

The survivor weapon system is the clearest current replication inefficiency.

Every survivor prefab contains pistol, rifle, and shotgun `Weapon` network behaviours. Static weapon configuration such as damage, fire rate, visuals, and sounds is local prefab data and is not replicated. However, each of the three weapons reserves and replicates its own runtime state and two fixed-capacity projectile event buffers even when the weapon has not been collected.

Measured from Fusion's weaved network state:

- One complete survivor snapshot is **885 words / 3,540 bytes**.
- The three weapons plus the `Weapons` manager use **842 words / 3,368 bytes**.
- The weapon subsystem therefore accounts for about **95%** of a survivor's fixed network snapshot.
- One complete zombie snapshot is about **45 words / 180 bytes**.
- The global `Gameplay` object is **2,232 words / 8,928 bytes**, mostly due to its capacity-32 player dictionary.

These are fixed snapshot-state sizes, not bytes resent every tick. Fusion delta-compresses changed state. The sizes still affect snapshot history, object spawning, late joining, serialization work, and the amount of data that can become dirty.

The recommended first real optimization experiment is enabling Simple KCC position compression. The recommended first substantial architecture change is consolidating weapon projectile events into one survivor-level stream, preferably encoded once per shot rather than once per pellet.

## Current Network Configuration

`Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion` currently uses:

- 64 Hz client and server simulation.
- 32 Hz client and server send rates.
- `ReplicationFeatures.None`.
- No enabled Fusion scheduling or interest management.
- A configured Fusion player capacity of 10.

The menu currently exposes a maximum of 8 players, the Fusion simulation capacity is 10, the gameplay dictionary capacity is 32, and the long-term design target is up to 20 players. These capacities should eventually be made consistent, but the intended final player maximum should be settled before reducing them.

All inspected survivor, zombie, and pickup prefabs currently use `ObjectInterest: Global`. There are no game calls to `AddPlayerAreaOfInterest`, `SetPlayerAlwaysInterested`, or equivalent interest-management APIs.

## Measurement Method

Fusion stores network state in 32-bit words. The sizes below were read from the `NetworkBehaviourWeaved` metadata in the current weaved assemblies. One word is four bytes.

This measures the fixed state layout. It does not include packet headers, NetworkObject metadata, acknowledgements, retransmission, compression, or the fact that unchanged words are normally omitted from delta updates.

| Object or behaviour | Words | Bytes | Notes |
|---|---:|---:|---|
| Complete survivor root | 885 | 3,540 | KCC, hitbox, survivor, health, weapon manager, and three weapons |
| Three weapons and manager | 842 | 3,368 | About 95% of the survivor snapshot |
| One `Weapon` | 279 | 1,116 | Includes both capacity-16 projectile arrays |
| `Weapons` manager | 5 | 20 | Current/pending weapon references and switch timer |
| Survivor excluding weapons | 43 | 172 | KCC, hitbox, survivor state, and health |
| Complete zombie root | 45 | 180 | KCC, hitbox, health, and zombie state |
| `Gameplay` | 2,232 | 8,928 | One global object; capacity-32 `PlayerData` dictionary dominates |
| Weapon or health pickup | 1 | 4 | Respawn timer only |

At 100 survivors, the raw fixed survivor state is about 354 KB per complete snapshot, of which about 337 KB belongs to weapons. This is why the weapon layout matters even though Fusion does not resend the entire snapshot every network update.

## Weapon Replication Detail

Each survivor owns three `Weapon` network behaviours. Each weapon currently replicates:

- Collected and reloading flags.
- Clip and reserve ammo.
- Fire and hit counters.
- Fire/reload cooldown timer.
- `NetworkArray<ProjectileSpawnData>` with capacity 16.
- `NetworkArray<ProjectileHitData>` with capacity 16.

The spawn entry is 7 words: origin, direction, and spawn tick.

The hit entry is 10 words: position, normal, effect flag, fire slot, kill flag, and critical-hit flag.

Across three weapons, the six arrays alone reserve **816 words / 3,264 bytes** per survivor. This is about 97% of the weapon subsystem.

The shotgun fires 12 pellets per trigger pull. Reducing every buffer from 16 to 8 is therefore not safe. It would overwrite entries inside a single shotgun shot.

The current code writes one spawn event for every pellet. A shotgun trigger therefore dirties 12 spawn entries, representing 336 raw bytes of spawn data before counters and protocol overhead. Because pellet dispersion is deterministic, a future design can replicate one shot origin, direction, tick, weapon type, and seed, then reconstruct all 12 pellet directions locally.

## Opportunities, Easiest First

### 1. Add a Repeatable Fusion Traffic Benchmark

**Ease:** Very easy  
**Expected direct gain:** None  
**Expected value:** Very high confidence for every later change

Record at least:

- Server outgoing bytes per second.
- Client incoming bytes per second.
- State versus RPC traffic.
- Changed and culled NetworkObjects.
- Resimulation and packet-loss behavior.
- Spawn and late-join time.

Use repeatable scenarios: idle actors, all actors moving, rifle combat, shotgun combat, mass zombie movement, and a late join. Test standalone host/client and a dedicated server on separate machines where possible.

### 2. Enable Fusion Replication Scheduling

**Ease:** Very easy  
**Expected bandwidth gain:** None  
**Expected behavior under saturation:** Small to medium improvement

Changing from `ReplicationFeatures.None` to `Scheduling` allows Fusion to increase the priority of changed objects that were culled because a per-tick data limit was reached.

This does not reduce traffic. It can reduce starvation and visible update stalls when the game exceeds its per-tick replication budget. It should be tested independently before enabling interest management.

### 3. Test Simple KCC Position Compression

**Ease:** Easy  
**Expected gain:** Medium for moving crowds; potentially high with hundreds of zombies

`CompressNetworkPosition` is disabled on both survivor and zombie prefabs.

When enabled, Simple KCC quantizes network position to 1/1024 meter and stops writing the three-word residual position correction that otherwise changes while an actor moves. The fixed KCC snapshot remains 25 words, but up to 12 raw bytes stop becoming dirty on each movement update.

At a 32 Hz send rate, that is an upper-bound reduction of roughly 384 raw bytes per second per continuously moving actor before Fusion's delta compression and protocol overhead. The real result must be measured.

Test possessed movement, remote interpolation, ramps, stairs, jumping, zombie climbing, mantling, and large world coordinates before keeping this enabled.

### 4. Right-Size and Split Global Player Data

**Ease:** Easy to medium  
**Expected gain:** Low overall; useful for joins and global-state changes

The global `Gameplay` snapshot is about 8.9 KB. Its capacity-32 `NetworkDictionary<PlayerRef, PlayerData>` is the main contributor.

Possible later changes:

- Align dictionary capacity with the final supported player count.
- Separate nearly static identity data such as nickname and team color from frequently changed match data.
- Confirm with Fusion statistics whether updating one `PlayerData` entry dirties more of its 39-word value than expected.

This object exists only once, so it is lower priority than per-survivor and per-zombie costs.

### 5. Consolidate Weapon State and Projectile Buffers

**Ease:** Medium  
**Expected gain:** High fixed-state, spawn, and late-join reduction

Keep weapon components as local configuration and visual objects, but move replicated runtime data into one survivor-level weapon state:

- Collected weapon bitmask.
- Current and pending weapon type.
- Per-weapon ammo and cooldown data.
- One shared fire-event stream.
- One shared hit-event stream.

Only one weapon can fire at a time, so every weapon does not need its own inactive event history.

A straightforward shared stream, including a weapon type in spawn events, is estimated to save about **2.1 KB per survivor**, reducing the complete survivor snapshot by roughly **60%**. Exact savings depend on the final packed layout.

This is preferable to spawning each collected weapon as a separate NetworkObject. Dynamic weapon NetworkObjects add lifecycle, authority, interest, and object-header costs without solving projectile-event encoding as cleanly.

### 6. Replicate One Event Per Shot, Not Per Pellet

**Ease:** Medium  
**Expected gain:** High during shotgun combat

The state authority should continue expanding and simulating every pellet for authoritative hit detection. Proxies only need one shot event containing enough information to reconstruct the same deterministic spread.

For the current 12-pellet shotgun, this can reduce spawn-event dirty data by about 90% per trigger pull.

This fits naturally with the shared survivor-level event stream. It should preserve the existing deterministic seed behavior and must remain correct through prediction and resimulation.

An alternative is using unreliable RPCs for transient fire and hit effects instead of snapshot-backed arrays. That can remove almost all fixed projectile-event state, but dropped RPCs can produce missing tracers or impacts. A shared snapshot-backed stream is the safer first implementation; RPC transport can be evaluated afterward.

### 7. Add Interest Management

**Ease:** Hard  
**Expected gain:** Very high at large map and actor counts

Interest management is currently absent and all inspected actors are global. Once actor counts become large, this is likely the largest ongoing bandwidth optimization.

A likely policy is:

- Always replicate the local player's complete team.
- Always replicate the currently possessed survivor and global match state.
- Use area-of-interest replication for enemy survivors, zombies, and pickups.
- Preserve map intelligence through lightweight known-contact or last-known-position data rather than requiring every full actor object globally.

The gain is approximately proportional to the actors excluded for each client. If a client only needs 20% of world actors, actor-state traffic can potentially fall by around 80%. The actual result depends on movement, dirty-state frequency, AOI radius, and how the RTS map is designed.

This requires careful work around team switching, possession, sounds, minimap contacts, kill feed, and actors entering combat from outside interest.

### 8. Add Network Fidelity Tiers

**Ease:** Hard  
**Expected gain:** High after interest management

Do not lower the global 64 Hz simulation first. The project already sends state at 32 Hz.

Instead, consider relevance-based fidelity:

- Full update priority for possessed survivors and nearby combatants.
- Lower priority or slower state changes for distant zombies.
- Dormant state for settled actors.
- Simplified far-zombie representation where exact KCC movement is not required.

This should be designed together with AOI and the AI/movement tiers in `Docs/FutureScaleOptimizationNotes.md`.

## Lower-Priority Findings

- AI perception, pathfinding state, and target memory are generally not networked. This is already the correct architecture.
- Only the active local survivor registers as the player's Fusion input provider. Team size therefore does not multiply client input traffic.
- `NetworkedInput` is five words and is sent once per player, not once per survivor.
- Pickups have only a one-word network timer. Their overlap checks may matter for server CPU, but their replicated state is not a primary bandwidth issue.
- Packing individual weapon booleans or small enums would save only a few words. Do this as part of the weapon rework, not as an isolated optimization.
- Health hit position and direction use six words and change only when damage occurs. They are a possible later cosmetic-event optimization, not a current priority.
- Zombie mantle start/end positions reserve six words but change only during mantles. Zombie KCC movement and global relevance matter more.
- Replicated movement velocity is another later audit target. Removing it would require proving that rollback and proxy animation can derive equivalent velocity from KCC state.

## Recommended Implementation Order

1. Capture a repeatable baseline with Fusion statistics.
2. A/B test Simple KCC position compression.
3. Enable and verify Fusion scheduling.
4. Rework survivor weapons into compact inventory state plus one shared, per-shot event stream.
5. Re-measure combat, spawning, and late joining.
6. Design team-aware AOI and map-contact behavior.
7. Add network fidelity tiers only after AOI behavior is stable.

## Relevant Files

- `Assets/Scripts/Weapons/Weapon.cs`
- `Assets/Scripts/Weapons/Weapons.cs`
- `Assets/Scripts/Survivor/Survivor.cs`
- `Assets/Scripts/Survivor/SurvivorInput.cs`
- `Assets/Scripts/Survivor/Health.cs`
- `Assets/Scripts/Zombies/ZombieCharacter.cs`
- `Assets/Scripts/Gameplay/Gameplay.cs`
- `Assets/Prefabs/BlockSurvivor.prefab`
- `Assets/Prefabs/BlockZombie.prefab`
- `Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion`

