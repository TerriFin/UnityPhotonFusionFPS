# Networked Simulated Projectile System

> **Architecture update (weapon rework).** The projectile event streams now live **once on the survivor-level
> `Weapons` manager**, not on each `Weapon`. Only one weapon fires at a time, so one shared per-shot fire stream
> (`[Networked, Capacity(8)] NetworkArray<ProjectileSpawnData>`) and one shared per-pellet-hit stream
> (`[Networked, Capacity(16)] NetworkArray<ProjectileHitData>`) replace the six per-weapon arrays that previously
> dominated a survivor's snapshot. A multi-pellet shot writes **one** fire event tagged with its `WeaponType`;
> every peer reconstructs the identical pellet spread deterministically from the shot's seed
> (`SpawnTick * survivorId`). `Weapon.Fire()` still performs ammo/cooldown checks and then calls
> `Weapons.RegisterShot(...)`; the manager owns hit detection, damage, and visuals. The core trajectory math,
> determinism, and the prediction/resimulation rules below are unchanged — read the sections below for those, but
> treat "the streams/sim live on `Weapon`" as historical. See `Docs/NetworkOptimizationImplementation.md` §8 for
> the rework rationale and the exact networked layout.

## Context

The FPS template ships with hitscan shooting: bullets travel instantaneously via `Runner.LagCompensation.Raycast()` and hit their target the same tick they are fired. This works cleanly for networking but does not match the game's vision — a chaotic arena where machine guns spray streams of visible, physical-feeling bullets and players can read the battlefield from tracer fire.

This document specifies a drop-in replacement: **deterministic simulated projectiles**. Bullets travel at a configurable speed and arc under gravity without ever spawning a Unity physics object (`Rigidbody`, `Collider`). The networking cost is a single fire event (~28 bytes); positions are never synced. Hit detection on the server uses the same `Runner.LagCompensation.Raycast()` call as before, just deferred to the tick the bullet actually arrives.

The system must comfortably handle hundreds of simultaneous bullets (multiple players, AI characters, shotgun pellets) without frame budget issues on clients or the server.

---

## Core Concept: Deterministic Trajectory

A bullet is fully described by its **initial conditions**. Given these values, any peer can calculate the bullet's world position at any network tick or render time without additional data from the network:

```
elapsed  = (currentTick - spawnTick) * Runner.DeltaTime
position = origin + direction * BulletSpeed * elapsed
position.y -= 0.5 * GravityScale * 9.81 * elapsed²
```

- `origin` and `direction` are set at the moment of firing (`Weapons.FireTransform.position` + `Weapons.FireTransform.forward`, after dispersion is applied).
- `BulletSpeed` and `GravityScale` are weapon inspector properties — identical on all peers.
- `spawnTick` is the Fusion tick the shot was registered on the state authority.
- No `Rigidbody` is created. No position is ever written to a `[Networked]` property after the fire event.

The direction vector must already incorporate any dispersion (random spread). Because the existing code seeds `Random` with `Runner.Tick * Object.Id.Raw` before computing dispersion, this remains deterministic across peers — keep that seed logic unchanged.

---

## Data Structures

### ProjectileSpawnData

Replaces the existing `ProjectileData` struct. Stores the initial conditions of one bullet.

```csharp
public struct ProjectileSpawnData : INetworkStruct
{
    public Vector3 Origin;     // world position of the muzzle at fire time
    public Vector3 Direction;  // normalized, already includes dispersion
    public int     SpawnTick;  // Runner.Tick when the bullet was created
}
```

Size on the wire: 3 × 4 + 3 × 4 + 4 = **28 bytes**.

### ProjectileHitData

New struct. Written by the state authority when a bullet hits something. Clients read it to play impact effects.

```csharp
public struct ProjectileHitData : INetworkStruct
{
    public Vector3     HitPosition;
    public Vector3     HitNormal;
    public NetworkBool ShowEffect;  // false for player body hits, true for geometry
}
```

### ActiveProjectile (non-networked, server only)

A plain C# struct (or class) kept in a local `List<>` on the state authority. Never goes over the network.

```csharp
private struct ActiveProjectile
{
    public Vector3 Origin;
    public Vector3 Direction;
    public int     SpawnTick;
    public int     SlotIndex;  // index into _spawnData / _hitData circular buffers
}
```

### Changes to Networked Properties in Weapon.cs

Remove:
```csharp
[Networked, Capacity(32)] NetworkArray<ProjectileData> _projectileData  // REMOVE
```

Add:
```csharp
[Networked, Capacity(16)] NetworkArray<ProjectileSpawnData> _spawnData  // circular buffer of fire events
[Networked, Capacity(16)] NetworkArray<ProjectileHitData>   _hitData    // circular buffer of confirmed hits
[Networked]               int                              _hitCount    // incremented on each confirmed hit
```

`_fireCount` stays. It is incremented each time a shot is registered, same as today. Its modulo (% 64) gives the slot index for both `_spawnData` and `_hitData`.

The capacity of 64 is sufficient for sustained full-auto fire. At 900 RPM on a 64 Hz simulation that is ~14 rounds per tick from a single weapon; bullets at 400 m/s travel 100 m in 0.25 s (16 ticks). The maximum concurrent in-flight bullets from one weapon is ~14 × 16 = ~224, well within the circular buffer before old entries are overwritten — and by the time a slot is overwritten the server has already resolved that bullet's hit or miss.

---

## Server-Side Bullet Simulation (State Authority)

All of the following runs only on the peer that has `HasStateAuthority` for the weapon.

### Registering a new bullet (inside FireProjectile)

Replace the current immediate `Runner.LagCompensation.Raycast()` call with:

1. Compute `firePosition` and `fireDirection` (including dispersion) exactly as today.
2. Write to the circular buffer:
   ```csharp
   int slot = _fireCount % 64;
   _spawnData.Set(slot, new ProjectileSpawnData
   {
       Origin    = firePosition,
       Direction = fireDirection,
       SpawnTick = Runner.Tick
   });
   ```
3. Add to the local `_activeProjectiles` list:
   ```csharp
   _activeProjectiles.Add(new ActiveProjectile
   {
       Origin    = firePosition,
       Direction = fireDirection,
       SpawnTick = Runner.Tick,
       SlotIndex = slot
   });
   ```
4. Increment `_fireCount` (as today).

Do NOT call `ApplyDamage()` here. Do NOT call any raycast here.

### Stepping active projectiles (FixedUpdateNetwork, state authority)

Add this block to `FixedUpdateNetwork()`, after the existing fire/reload logic:

```csharp
if (HasStateAuthority)
{
    float dt          = Runner.DeltaTime;
    int   currentTick = Runner.Tick;

    for (int i = _activeProjectiles.Count - 1; i >= 0; i--)
    {
        var proj    = _activeProjectiles[i];

        // Skip the spawn tick. Survivor.FixedUpdateNetwork runs before Weapon's, so the just-fired
        // projectile would otherwise be stepped with tPrev = -dt — prevPos lands behind the muzzle
        // and the first raycast scans geometry the bullet never crossed (usually a wall behind the
        // shooter). The bullet starts moving on spawnTick + 1.
        if (currentTick <= proj.SpawnTick)
            continue;

        float tPrev = (currentTick - 1 - proj.SpawnTick) * dt;
        float tCurr = (currentTick     - proj.SpawnTick) * dt;

        // Bullet has exceeded its maximum range — treat as miss and discard.
        float maxLifetime = MaxHitDistance / BulletSpeed;
        if (tCurr >= maxLifetime)
        {
            _activeProjectiles.RemoveAt(i);
            continue;
        }

        Vector3 prevPos = EvaluateTrajectory(proj, tPrev);
        Vector3 currPos = EvaluateTrajectory(proj, tCurr);
        Vector3 delta   = currPos - prevPos;
        float   stepLen = delta.magnitude;

        if (stepLen < 0.001f)
            continue;

        var hitOptions = HitOptions.IncludePhysX | HitOptions.IgnoreInputAuthority;
        if (Runner.LagCompensation.Raycast(prevPos, delta / stepLen, stepLen,
            Object.InputAuthority, out var hit, HitMask, hitOptions))
        {
            // Record hit for all clients.
            int slot = proj.SlotIndex;
            _hitData.Set(slot, new ProjectileHitData
            {
                HitPosition = hit.Point,
                HitNormal   = hit.Normal,
                ShowEffect  = hit.Hitbox == null  // geometry = show decal; player = no decal
            });
            _hitCount++;

            // Apply damage exactly as today.
            if (hit.Hitbox != null)
                ApplyDamage(hit.Hitbox, hit.Point, proj.Direction);

            _activeProjectiles.RemoveAt(i);
        }
    }
}
```

`EvaluateTrajectory` is a small helper (static or private) that centralises the trajectory formula so both server simulation and client visuals use identical math:

```csharp
private Vector3 EvaluateTrajectory(in ActiveProjectile proj, float elapsed)
{
    Vector3 pos = proj.Origin + proj.Direction * BulletSpeed * elapsed;
    pos.y -= 0.5f * GravityScale * 9.81f * elapsed * elapsed;
    return pos;
}
```

An overload (or the same method with different input types) must also be accessible from `Render()` using a `ProjectileSpawnData` struct instead of `ActiveProjectile` — the math is identical.

### State authority reconstruction after resimulation

Photon Fusion re-runs `FixedUpdateNetwork` during resimulation (rollback and replay). The `_activeProjectiles` list is not networked, so after a resimulation the list may be stale. To handle this:

- On `Spawned()`, clear `_activeProjectiles`.
- In `FixedUpdateNetwork()`, before processing the list, check `Runner.IsResimulation`. During resimulation do NOT modify `_activeProjectiles` — skip the stepping block entirely. Hit detection during resimulation would incorrectly re-apply damage and fire duplicate events. The state authority's forward tick is the only one that matters for gameplay.

---

## Client-Side Visual Simulation (Render)

`Render()` runs every frame on all peers, including the local player. It is non-authoritative and drives purely cosmetic effects.

### Tracking new shots

The existing pattern of comparing `_fireCount` against a locally cached `_renderedFireCount` still works. When `_fireCount > _renderedFireCount`:

```csharp
while (_renderedFireCount < _fireCount)
{
    int slot = _renderedFireCount % 64;
    var spawnData = _spawnData[slot];

    // Only spawn a visual if the bullet was fired recently enough to still be in flight.
    float elapsed = (Runner.LocalRenderTime - spawnData.SpawnTick * Runner.DeltaTime);
    float maxLifetime = MaxHitDistance / BulletSpeed;
    if (elapsed < maxLifetime)
    {
        SpawnBulletVisual(slot, spawnData);
    }

    _renderedFireCount++;
}
```

`Runner.LocalRenderTime` is used in `Render()` (not `Runner.Tick`) to get smooth sub-tick interpolated time.

### Driving bullet visual position

`ProjectileVisual` needs a significant rewrite. Instead of lerping from muzzle to a fixed hit point (old behaviour), it now:

1. Stores the `ProjectileSpawnData` assigned to it.
2. Stores its `SlotIndex`.
3. In `Update()` or from `Render()`:

```csharp
float elapsed = (float)(Runner.LocalRenderTime - spawnData.SpawnTick * Runner.DeltaTime);
transform.position = EvaluateTrajectory(spawnData, elapsed);
// Optional: orient the visual along the velocity direction.
transform.forward = DirectionAtTime(spawnData, elapsed);
```

`DirectionAtTime` returns the tangent of the trajectory (first derivative):
```
tangent.x = direction.x * BulletSpeed
tangent.y = direction.y * BulletSpeed - GravityScale * 9.81 * elapsed
tangent.z = direction.z * BulletSpeed
```
Normalise this to get a forward vector for bullet orientation.

### Detecting hits and playing impact effects

Cache `_hitCount` in a local `_renderedHitCount`. When `_hitCount > _renderedHitCount`:

```csharp
while (_renderedHitCount < _hitCount)
{
    int slot = _renderedHitCount % 64;
    var hitData = _hitData[slot];

    // Play impact effect if needed.
    if (hitData.ShowEffect)
        PlayHitEffect(hitData.HitPosition, hitData.HitNormal);

    // Destroy the visual flying for this slot, if still alive.
    DestroyBulletVisual(slot);

    _renderedHitCount++;
}
```

Also destroy any visual whose calculated lifetime (`elapsed >= MaxHitDistance / BulletSpeed`) has expired without a recorded hit, to prevent orphaned visuals from floating forever.

---

## New Weapon Inspector Properties

Add to the Weapon serialized fields (next to `MaxHitDistance`):

```csharp
[SerializeField] private float BulletSpeed   = 300f;  // metres per second
[SerializeField] private float GravityScale  = 0.2f;  // 0 = flat laser, 1 = real-world gravity
```

Suggested starting values:

| Weapon Type | BulletSpeed | GravityScale | Feel |
|---|---|---|---|
| Pistol | 180 m/s | 0.3 | Visible arc at range |
| Rifle / SMG | 400 m/s | 0.1 | Barely perceptible drop |
| Shotgun | 120 m/s | 0.4 | Short range, arcing pellets |
| Thrown grenade | 20 m/s | 1.0 | Lob trajectory |

`MaxHitDistance` now doubles as the bullet lifetime limit: the bullet despawns after travelling this distance (i.e. `elapsed >= MaxHitDistance / BulletSpeed`).

---

## Integration Points — What Changes, What Does Not

### Files modified

**`Assets/Scripts/Weapons/Weapon.cs`**
- Remove `ProjectileData` struct.
- Remove `[Networked, Capacity(32)] NetworkArray<ProjectileData> _projectileData`.
- Add `ProjectileSpawnData` and `ProjectileHitData` structs (can live at the bottom of this file).
- Add `[Networked, Capacity(16)]` arrays and `_hitCount` property.
- Add `List<ActiveProjectile> _activeProjectiles` (non-networked private field).
- Add `BulletSpeed` and `GravityScale` serialized fields.
- Rewrite `FireProjectile()` to write spawn data instead of raycasting.
- Add bullet stepping block in `FixedUpdateNetwork()`.
- Rewrite the projectile visual section of `Render()` to use the new counters.
- Add `EvaluateTrajectory()` helper.

**`Assets/Scripts/Weapons/ProjectileVisual.cs`**
- Remove fixed start/end lerp.
- Add `Initialize(ProjectileSpawnData data, int slot, Weapon weapon)` method.
- Each frame, compute and apply position using `EvaluateTrajectory`.
- Expose a `SlotIndex` property for the weapon's `DestroyBulletVisual(slot)` lookup.

> **Superseded by the weapon rework:** the per-weapon split above (streams + simulation in `Weapon.cs`) is
> historical. The streams, `FireProjectile`/`RegisterShot`, the bullet stepping, `ApplyDamage`, `EvaluateTrajectory`,
> and the visual spawn/terminate loops now live in `Assets/Scripts/Weapons/Weapons.cs` as one shared per-shot
> system; `Weapon.cs` retains only ammo/cooldown state, config, and the per-weapon reload/fire visual hooks.

### Files unchanged

- `Assets/Scripts/Weapons/Weapons.cs` — **now the owner** of the shared projectile streams, bullet simulation, and
  visuals (was previously a thin manager). `RegisterShot()` is the entry point weapons call on a successful fire.
- `Assets/Scripts/Survivor/Survivor.cs` — calls `Weapons.Fire()` identically.
- `Assets/Scripts/Survivor/SurvivorInput.cs` — input flow unchanged.
- `Assets/Scripts/Survivor/Health.cs` — `ApplyDamage()` signature unchanged.
- `Assets/Scripts/Survivor/BodyHitbox.cs` — hitbox system unchanged.
- `Assets/Scripts/Gameplay/Gameplay.cs` — kill tracking and game loop unchanged.
- `Assets/Scripts/UI/UICrosshair.cs` — crosshair hit feedback needs one small change: instead of triggering in `FireProjectile()`, trigger it from `Render()` when `_hitCount` changes for a bullet the local player fired. Check `HasInputAuthority`.

---

## Performance Notes

### Bandwidth
Each fire event is 28 bytes (see struct sizes above). A full-auto rifle at 900 RPM = 15 rounds/sec. Across 20 players all firing = 300 events/sec = ~8.4 KB/s added to game state. This is negligible relative to player movement and other networked state.

### Server CPU
The server steps every active bullet every tick. At 64 Hz with 1 000 simultaneous bullets (e.g. 10 full-auto rifles, each with ~100 bullets in flight), that is 64 000 `LagCompensation.Raycast()` calls per second. Each call is a single line cast into the hitbox tree — the same cost as the existing hitscan, just distributed across ticks.

If this becomes a bottleneck:
- Introduce a per-frame sweep budget (e.g. max 500 raycasts/tick). When over budget, defer remaining bullets to the next tick. Bullets miss their exact tick but the error is sub-frame.
- For bullets that have travelled less than 5 m since last tick and are heading towards empty space, skip the raycast and fast-forward several ticks at once.

### Client CPU
Trajectory evaluation is three multiplications and one subtraction per bullet per frame — no physics queries. 1 000 bullets is trivially cheap. The main cost is `GameObject` overhead from `ProjectileVisual` instances; consider a pool.

### Buffer overflow
The 16-slot circular buffers mean that if a weapon fires more than 16 bullets before any are resolved, the oldest spawn data gets silently overwritten (oldest visual is orphaned; gameplay result on the server is already determined). In practice this cannot happen at normal fire rates and bullet speeds — at 900 RPM and a 0.25 s bullet flight time, at most ~4 bullets are ever in flight from one weapon simultaneously.

**Why 16, not more:** Fusion reserves the full allocated capacity of every `NetworkArray` in snapshot state regardless of how many slots are currently in use. The current expanded hit data makes one weaved `Weapon` 279 words / 1,116 bytes. With each survivor carrying three weapon behaviours, buffer capacity has a large effect on fixed state, spawning, and late joining. Do not raise this capacity without profiling first. See `Docs/NetworkOptimizationAudit.md` for the current measured breakdown and recommended replacement architecture.

---

## Out of Scope

These systems are unrelated to the shooting mechanism and must not be touched during implementation:

- Reload logic (`Weapon.Reload()`)
- Weapon switching (`Weapons.SwitchWeapon()`)
- Ammo pickup and weapon collection
- Survivor spawn / death / respawn
- Kill feed and statistics (`Gameplay.cs`)
- Lobby and connection (`MenuConnectionBehaviour.cs`)
- Scene files (`Deathmatch.unity`, `Startup.unity`)
