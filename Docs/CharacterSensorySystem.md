# Character Sensory System

## Goal

Add a reusable Unity component that lets AI-controlled characters know about nearby enemies and combat events. The same component should work for survivors, recruitable neutral survivors, and zombies, with different sensor values per prefab.

The sensor does not decide what to do. It only answers "what does this character currently know about?" The currently assigned AI behavior decides whether to look, move, attack, flee, or ignore that information.

## Implemented Component

The implemented component is `CharacterSensor`.

Location:

```text
Assets/Scripts/AI/Sensory/CharacterSensor.cs
Assets/Scripts/AI/Sensory/CharacterSensorEvents.cs
```

The component should be usable by any networked character type, not only `Survivor`.

Inspector fields:

```csharp
[Header("Awareness")]
public float ProximityAwarenessRadius = 4f;
public float NoiseAwarenessRadius = 16f;
public float BulletImpactAwarenessRadius = 10f;

[Header("Vision")]
public float VisionDistance = 18f;
[Range(1f, 180f)]
public float VisionAngle = 90f;
public float EyeHeight = 1.6f;
public LayerMask VisionBlockers;

[Header("Runtime")]
public bool DisableWhenPossessed = true;
public float SensorInterval = 0.2f;
public float MemoryDuration = 4f;
public int MaxKnownEntries = 8;
```

Recommended starting values:

| Character type | Proximity | Noise | Bullet impact | Vision distance | Vision angle |
|---|---:|---:|---:|---:|---:|
| Survivor | 4m | 10m | 12m | 22m | 90 degrees |
| Zombie | 2m | 20m | 0m | 12m | 70 degrees |

The exact values should live on the prefab so designers can tune them without code changes.

## Detected Information

The sensor keeps a small non-networked memory of known enemies and events.

Suggested data shape:

```csharp
public enum ESensoryStimulus
{
    Proximity,
    Vision
}

public readonly struct KnownEnemyInfo
{
    public readonly NetworkObject Object;
    public readonly Vector3 LastKnownPosition;
    public readonly Vector3 ApproximateSourcePosition;
    public readonly ESensoryStimulus Stimulus;
    public readonly int Tick;
}
```

Rules:

- `Object` is set when the enemy character itself was detected.
- `LastKnownPosition` is where the enemy or stimulus was last detected.
- `ApproximateSourcePosition` currently matches `LastKnownPosition` for direct memories. Bullet impacts are handled as immediate events instead of remembered entries.
- The memory is local to state authority and should not be `[Networked]`.
- Old entries should expire after a short configurable duration, for example 3-5 seconds.
- Memories that reference a dead survivor should be pruned before they are returned to AI behaviors.

Public API:

```csharp
public bool TryGetClosestKnownEnemy(out KnownEnemyInfo enemy);
public bool TryGetClosestDirectEnemy(out KnownEnemyInfo enemy);
public bool TryGetClosestVisiblePickup(out KnownPickupInfo pickup);
public void GetVisiblePickups(List<KnownPickupInfo> results);
public bool TryGetLookRotationDelta(float maxYawDegreesPerTick, out Vector2 lookRotationDelta);
public void RecordNoise(Vector3 noisePosition, NetworkObject source, float radius);
public void RecordBulletImpact(Vector3 impactPosition, Vector3 approximateShooterPosition, NetworkObject shooter);
```

## Detection Modes

### Proximity Awareness

The character notices all enemies inside `ProximityAwarenessRadius`, even if they are behind it.

Use this for close personal awareness: footsteps, movement, breathing room, and "someone is right next to me" behavior.

### Noise Awareness

The character notices noisy enemies/events inside `NoiseAwarenessRadius`.

Noise is event-based. Weapon fire currently calls `CharacterSensorEvents.ReportNoise(...)` on state authority. Future sprinting, zombie screams, explosions, or other sound emitters should call the same central event method.

Noise events are immediate investigation prompts, not remembered targets and not direct shooting targets. If an uncontrolled survivor can investigate at the moment it hears the event and its non-combat investigation setting is enabled, its AI may move toward the noise position to check it out, then return to its current assignment anchor. If it cannot investigate immediately, it ignores the event.

### Bullet Impact Awareness

The character notices bullet impacts inside `BulletImpactAwarenessRadius`.

Only survivors should react to this at first. Zombies can keep this radius at `0`.

Weapon projectile impacts currently call `CharacterSensorEvents.ReportBulletImpact(...)` on state authority. When a bullet impact is recorded near a survivor:

```csharp
Debug.Log($"Bullet impact heard near {name}. Approx shooter position: {approximateShooterPosition}");
```

The bullet impact should record the approximate shooter position even if the shooter is outside normal proximity, noise, or vision range. This gives later survivor AI enough information to turn, take cover, suppress, or call out danger.

Bullet-impact events are also immediate investigation prompts. The survivor should check the approximate shooter position only if it can start investigating immediately, is not already blocked by a player movement order or direct combat, and its non-combat investigation setting is enabled. If investigation is disabled, the survivor still briefly looks toward the approximate shooter position and broadcasts a look-only alert to nearby same-team survivors.

### Forward Vision

The character sees enemies in front of it inside `VisionDistance` and `VisionAngle`.

Detection order should be cheap-to-expensive:

1. Check distance.
2. Check field-of-view dot product.
3. Only then do a raycast/linecast against `VisionBlockers`.

Survivors should generally have longer forward vision than zombies. Zombies can compensate with stronger noise response.

### Pickup Vision

The character also detects visible pickups with the same forward-vision rules used for enemies:

1. Check distance against `VisionDistance`.
2. Check `VisionAngle`.
3. Linecast against `VisionBlockers`.
4. Remember only a capped number of pickups through `MaxKnownPickups`.

Pickups do not use proximity awareness, noise, or bullet-impact memory. They are only found through vision. `WeaponPickup` and `HealthPickup` register themselves in static pickup lists, so sensors do not need per-tick physics overlap queries to discover them.

Detected pickups are exposed through:

```csharp
public bool TryGetClosestVisiblePickup(out KnownPickupInfo pickup);
public void GetVisiblePickups(List<KnownPickupInfo> results);
```

`KnownPickupInfo` identifies whether the pickup is health or weapon, stores the pickup position, and stores `WeaponType` for weapon pickups. AI should still decide whether the pickup is useful and active before moving to it. Inactive pickups can remain visible to the map as translucent testing icons.

## Possessed Characters

If `DisableWhenPossessed` is true, the sensor should skip active human-controlled characters.

Reasoning:

- The player already sees through the camera.
- We do not need server-only AI perception for a character currently controlled by a human.
- It reduces unnecessary server work in larger matches.

Inactive same-team survivors, neutral survivors, and zombies keep sensors enabled.

## First Behavior

For the first implementation, uncontrolled characters should look at the closest enemy they currently detect.

Implemented approach:

- Add a helper on `CharacterSensor`: `TryGetClosestKnownEnemy(out KnownEnemyInfo enemy)`.
- Add a helper on `CharacterSensor`: `TryGetClosestDirectEnemy(out KnownEnemyInfo enemy)` for direct combat memories from `Vision` and `Proximity`.
- AI behaviors call these helpers when producing `NetworkedInput`.
- If an enemy is known, the AI emits a yaw `LookRotationDelta` toward `enemy.LastKnownPosition`.
- Movement remains owned by the current AI behavior.

This means:

- `SurvivorNonCombatAI` hold assignments idle in place while turning to face the closest detected direct enemy unless `SurvivorAIShooting` has a direct target to aim/fire at. Noise and bullet-impact prompts can start an immediate investigation detour if the survivor is free to react; even when investigation movement is disabled, they can still create a short reactive look toward the source and send look-only ally alerts. During combat, a reactive look can override the current aim only when the stimulus source is much closer than the current target.
- `SurvivorNonCombatAI` follow and move assignments keep their movement orders while moving. If `SurvivorAIShooting` has a direct target with line of fire, they may aim/fire while moving. Noise and bullet-impact events are immediate investigation prompts; if movement investigation is not allowed, the survivor may still turn toward the source briefly while preserving its movement order.
- Future attack/defend/flee behaviors can make stronger use of the same sensor data.

Do not put this look logic directly in `Gameplay.cs`.

## Bullet Impact Event Flow

Implemented first pass:

1. Projectile or weapon impact code knows `impactPosition`.
2. It also has enough context to estimate `approximateShooterPosition`, usually the projectile spawn/fire origin.
3. On state authority, weapon code calls:

```csharp
CharacterSensorEvents.ReportBulletImpact(impactPosition, approximateShooterPosition, shooterObject);
```

4. The helper finds sensors within `BulletImpactAwarenessRadius`.
5. Matching sensors immediately notify their survivor AI with the approximate shooter position and log it.

Avoid making bullet impacts `[Networked]` only for AI awareness. AI decisions happen on state authority, and resulting movement/rotation will replicate normally.

## Factions And Enemies

The sensor needs a way to know what counts as an enemy.

First implementation options:

- For survivors, use `OwnerRef` and treat survivors with a different `OwnerRef` as enemies.
- For zombies, add a simple faction/team component later, for example `CharacterFaction`.

Recommended direction:

```csharp
public enum ECharacterFaction
{
    Neutral,
    SurvivorTeam,
    Zombie
}
```

Survivors need both a faction and an owner/team id. Zombies can treat all non-zombies as enemies. Neutral recruitable survivors can start neutral and later change owner/team.

Do not solve the full faction system in the first sensor implementation unless the zombie work starts immediately.

## Network Model

The sensor runs only on state authority.

Good:

- Server/host calculates what AI characters know.
- AI produces normal `NetworkedInput`.
- Fusion replicates resulting movement/rotation.
- Sensor memory is not networked.
- Clients do not run duplicate perception for inactive characters.

Avoid:

- `[Networked]` lists of known enemies.
- Per-client perception decisions for AI.
- Every sensor scanning every other character every tick.
- Raycasting for vision before cheaper distance/FOV checks.

## Performance Notes

This plan is reasonable for the network because perception itself does not need network traffic. The important cost is server CPU.

Potentially heavy parts:

- Naive all-character-to-all-character checks every tick become `O(N^2)`.
- Vision raycasts are expensive if done for every possible target.
- Bullet impacts can become expensive if every impact loops over every sensor during heavy firefights.

Keep it light:

- Run sensors on a fixed interval like `0.2s`, not every simulation tick.
- Use `Physics.OverlapSphereNonAlloc` or a central registry with preallocated buffers.
- Filter by faction/team before raycasts.
- Use distance and FOV checks before line-of-sight raycasts.
- Make noise and bullet impacts event-driven.
- Cap remembered enemies/events per sensor, for example 8 entries.
- Remove entries for dead remembered survivors before selecting targets.
- Disable sensors for possessed characters.

At the current prototype scale, 2-20 players with a few survivors each is fine if sensors are throttled and event-based. The future zombie count is the real scaling concern, so zombies should use cheaper settings: shorter vision, fewer raycasts, slower sensor interval, and stronger noise events.

## Implementation Notes

- `Survivor.Spawned()` looks for an existing `CharacterSensor` and auto-adds one if missing.
- If the survivor prefab already has `CharacterSensor`, its inspector values are preserved.
- `Weapon.Fire(...)` reports a noise event once per shot on state authority.
- Projectile hit resolution reports bullet impacts on state authority.
- `WeaponPickup` and `HealthPickup` register themselves for lightweight pickup vision scans.
- `SurvivorNonCombatAI` hold assignments use sensor look input while staying still when investigation is enabled.
- `SurvivorNonCombatAI` receives `Noise` and `BulletImpact` events immediately from `CharacterSensor`; they are not stored in the normal known-enemy memory list.
- Direct `Vision` and `Proximity` memories are not investigation destinations for the survivor that sees the enemy, but that survivor can send a one-hop investigation alert to nearby same-team survivors that do not currently have their own direct enemy target.
- If AI shooting had line of fire to a direct enemy and then loses it while the enemy is still alive, non-combat AI can investigate the last known enemy position as a fresh investigation target.
- Movement AI can use direct combat aim/fire while moving, but should reserve sensor-only investigation look for stopped or safe moments so it does not accidentally walk away from the movement target.
- Later, attach the same component to zombies with zombie-tuned values.

## Out Of Scope For First Pass

- Full faction system.
- Networked UI indicators for detected enemies.
- Cover selection.
- Shooting decisions.
- Pathfinding.
- Shared squad awareness.
- Long-term memory or suspicion states.
