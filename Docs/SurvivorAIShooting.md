# Survivor AI Shooting

## Goal

Uncontrolled survivors can automatically shoot at enemies they detect through `CharacterSensor`, but they should be noticeably imperfect. They react slowly, shoot in delayed pulses, and aim with configurable error.

This feature is intentionally separate from the sensor system:

- `CharacterSensor` answers what the survivor knows about.
- `SurvivorAIShooting` decides whether the survivor can aim/fire at that information.
- The active AI input source, currently `SurvivorNonCombatAI`, decides when to consume that combat input.

## Files

```text
Assets/Scripts/Survivor/AI/SurvivorAIShooting.cs
Assets/Scripts/Survivor/AI/SurvivorNonCombatAI.cs
Assets/Scripts/Survivor/AI/SurvivorAICommandService.cs
Assets/Scripts/Survivor/Survivor.cs
Assets/Scripts/Survivor/SurvivorInput.cs
```

## Component

`SurvivorAIShooting` is a Unity component with editor-tunable values.

Important fields:

```csharp
public bool AutoShootEnabled = true;

public float FirstShotDelayMin = 0.5f;
public float FirstShotDelayMax = 1.4f;
public float MovingFirstShotDelayMultiplier = 1.5f;
public float FollowupShotDelayMin = 0.6f;
public float FollowupShotDelayMax = 1.8f;
public float TriggerHoldDuration = 0.35f;

public float HorizontalAimErrorDegrees = 8f;
public float VerticalAimErrorDegrees = 4f;
public float ZombieHorizontalAimErrorDegrees = 3f;
public float ZombieVerticalAimErrorDegrees = 1.5f;
public float AimTargetHeight = 1.4f;
public float AimErrorRefreshInterval = 0.8f;
public float FireAlignmentAngle = 10f;
public float MaxYawDegreesPerTick = 8f;
public float MaxPitchDegreesPerTick = 6f;

public float TargetSwitchHysteresis = 1.5f;
```

`Survivor.Spawned()` finds an existing component or auto-adds one if missing. Add it to prefabs directly when designer-tuned values are needed.

## Behavior

`SurvivorAIShooting.TryGetInput(...)`:

- Reads the closest directly detected enemy from `CharacterSensor` using vision/proximity memories, but keeps targeting **sticky** (see "Target Stickiness").
- Refuses dead survivor targets defensively even if sensor memory has not pruned them yet.
- Rotates toward an intentionally imperfect aim direction near the target's configured aim height.
- Uses separate aim error values for survivor targets and zombie targets.
- Waits a random first-shot delay after acquiring a target.
- Multiplies that first-shot delay by `MovingFirstShotDelayMultiplier` when movement AI asks for combat input while moving.
- Requires current line of fire before pressing `Fire`, so memory from noise, bullet impacts, or enemies behind blockers can make the survivor look but not shoot.
- Requests a desired weapon from `SurvivorWeaponPreferenceAI` before firing. Strong-weapon modes use each weapon's `AIWeaponStrength` and `AIEffectiveMaxRange` instead of a fixed weapon-type priority.
- Holds `EInputButton.Fire` for `TriggerHoldDuration` when aligned enough and the delay has elapsed.
- Waits a random follow-up delay before the next burst.

The component emits normal `NetworkedInput` with:

- `LookRotationDelta` for aiming.
- `EInputButton.Fire` for shooting.
- `EInputButton.Pistol`, `EInputButton.Rifle`, or `EInputButton.Shotgun` when it needs to switch weapons before shooting.

It does not call weapon code directly. `Survivor.ProcessInput(...)` still owns the actual `Weapons.Fire(...)` path.

Automatic weapons keep firing while `EInputButton.Fire` is held during the burst. Semi-auto weapons such as pistols and shotguns still behave like single shots because their weapon logic only fires on the press/allowed cadence.

If line of fire is lost during a burst, the component releases `Fire` immediately. It uses the survivor sensor's `VisionBlockers` mask for this check, from the current weapon fire transform to the enemy's configured aim height. Noise and bullet-impact memories are valid look/investigation targets for the active AI behavior, but they are not valid shooting targets.

## Target Stickiness

Target selection picks the closest valid direct enemy, but it does **not** switch off the current target every time another enemy becomes marginally closer. The survivor keeps its current target until either:

- the current target is no longer a valid direct enemy (dead, out of direct awareness, or not auto-attackable), or
- a different enemy is closer than the current target by more than `TargetSwitchHysteresis` metres.

This matters when a pack surrounds the survivor. Several zombies sit at near-equal distances, so a pure "closest enemy" choice flips between them on every sensor tick (every `SensorInterval`, default `0.2s`). Each flip changes the current target, which resets the first-shot delay (`FirstShotDelayMin..Max`, `0.5–1.4s`). Because the flip interval is shorter than that delay, the timer never elapses and the surrounded survivor never actually fires — it just keeps re-aiming. Stickiness locks onto one zombie long enough to complete the delay, fire, and continue, while still re-prioritising a zombie that pushes decisively closer (more than `TargetSwitchHysteresis` nearer than the current one). Set `TargetSwitchHysteresis` to `0` to restore the old always-closest behaviour.

## AI Weapon Selection

`SurvivorAIShooting` does not spend ammo directly. It emits the same weapon-switch and fire input buttons the player would use.

Rules:

- Only collected weapons are considered.
- A weapon is usable if it has ammo in the clip or reserve.
- `SurvivorWeaponPreferenceAI` decides whether the current situation calls for pistol or range-aware strong-weapon selection.
- Strong-weapon selection excludes weapons whose `AIEffectiveMaxRange` is shorter than the current target distance, then scores the remaining weapons by strength and range fit.
- If the current weapon is not the best usable weapon, the AI presses the matching weapon switch button and keeps aiming.
- Uncontrolled survivors pulse and retry a desired weapon switch, so a request temporarily blocked by reloading is not lost.
- The AI does not press `Fire` while switching.
- If all limited-ammo weapons are unavailable or empty, the pistol is selected as the fallback.
- If the current limited-ammo weapon has reserve ammo, normal weapon auto-reload can keep it in use.

See `Docs/SurvivorWeaponPreferenceAI.md` for the complete three-state behavior and scoring rules.

## Aim Error

Aim error is split into horizontal and vertical values:

- `HorizontalAimErrorDegrees` misses left/right.
- `VerticalAimErrorDegrees` misses high/low.
- `ZombieHorizontalAimErrorDegrees` is used instead when the direct target has `ZombieCharacter`.
- `ZombieVerticalAimErrorDegrees` is used instead when the direct target has `ZombieCharacter`.
- `AimTargetHeight` offsets the target position upward so AI aims around the torso instead of the ground.

`VerticalAimErrorDegrees` defaults lower than horizontal error so survivors are bad without constantly shooting far above or below the target. This also helps compensate for projectile drop making low shots feel too common when aiming at ground-level transforms.

Zombie targets should usually use smaller error values than survivor targets. Zombies move straight toward survivors at predictable speeds, so the same inaccuracy that feels good in survivor-vs-survivor firefights can make PvE shooting look strangely incompetent.

Aim error is stored as an angular offset (yaw and pitch in degrees) refreshed every `AimErrorRefreshInterval`, not as a positional offset in world space. The current direction to the target is computed each tick and the stored yaw/pitch rotation is applied on top of it. This keeps the angular error bounded by the configured values regardless of how far the target has moved since the last refresh — a target rushing from long range to point blank no longer turns a small error angle into a huge effective miss.

## AI Integration

`SurvivorNonCombatAI` hold assignments use shooting input whenever there is a direct target with line of fire. If there is no such target and investigation is enabled, it falls back to `CharacterSensor` look input, which lets the survivor rotate toward noises, bullet impacts, and last known positions.

Movement assignments should also be able to consume shooting input while moving. `SurvivorNonCombatAI` follow and move assignments should preserve their movement intent, but when `SurvivorAIShooting` has a direct target with line of fire they can use its look/fire input instead of only looking at the next path corner. This lets survivors return fire while following or moving to an order point instead of waiting until they stop.

If there is no direct target with line of fire, movement AI should keep steering normally and may use sensor look input only when it can do so without breaking the movement order.

## Moving Target Lock

Survivors may aim and shoot while moving, but moving should make them slower to lock onto a target.

Implemented approach:

- Keep AI aim error unchanged while moving.
- Do not add AI-only movement inaccuracy.
- Increase the first-shot lock delay while the AI is emitting movement input by passing `isMoving: true` into `SurvivorAIShooting.TryGetInput(...)`.
- Keep follow-up burst timing configurable through the existing follow-up delay fields.
- Let future weapon sway/recoil systems reduce accuracy for both player and AI movement through the shared weapon path.

This keeps the AI and player accuracy model aligned: movement penalties should eventually come from weapon handling, not from a separate AI-only miss system.

## Fire Permission

There is currently no player hotkey for toggling AI fire permission. `AutoShootEnabled` remains on the component as the local flag used by the shooting helper, but player-facing hold-fire behavior should be added later as a combat AI/fire-mode setting rather than as a temporary nearby command.

## Network Model

Shooting decisions are not networked as separate AI state.

- AI emits normal `NetworkedInput`.
- Weapon firing continues through existing authoritative `Weapons.Fire(...)`.
- Resulting projectiles, hits, and movement replicate through existing systems.

## Local Feedback

Hit-marker visuals, hit-marker sounds, kill-marker sounds, and empty-clip UI-style audio should only play for the local player's currently active survivor. Other survivors on the same team may still shoot and reload, but their successful hits should not trigger the player's crosshair feedback.

Health pickup popups follow the same rule through `Health`: they are shown only when the currently active local survivor receives the heal.

## Tuning Notes

For worse survivors:

- Increase `FirstShotDelayMin/Max`.
- Increase `FollowupShotDelayMin/Max`.
- Increase `TriggerHoldDuration` for longer automatic bursts.
- Increase `HorizontalAimErrorDegrees`.
- Increase `VerticalAimErrorDegrees`.
- Increase `ZombieHorizontalAimErrorDegrees` / `ZombieVerticalAimErrorDegrees` separately if survivors are too reliable against zombies.
- Lower `AimTargetHeight` if they should shoot lower, or raise it if they should bias toward chest/head height.
- Increase `FireAlignmentAngle` only if they should shoot before properly facing the target.

For zombies or non-gun enemies, do not use this component as-is. Create a separate attack component later that consumes the same `CharacterSensor` memory but emits melee/attack behavior instead of `Fire`.
