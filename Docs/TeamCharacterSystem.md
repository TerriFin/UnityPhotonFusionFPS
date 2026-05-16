# Team Character System — Replacing Respawning

## Context

The current system gives each player one character that respawns after death on a 5-second timer. The game's vision is a team-based format: each player owns a team of N characters (configurable, default 5), switches between them freely, and loses when all of them are dead. Characters can also be recruited mid-match, growing a player's team beyond the starting count. This document specifies the full replacement of the respawn system with that mechanic.

---

## Terminology

- **Player** — a human connection (`PlayerRef`). One per person in the match.
- **Survivor** — a networked survivor-prefab instance. Each player owns any number of them.
- **Active character** — the character the player is currently directly controlling.
- **Team** — all characters belonging to one player. A team is alive as long as any character in it is alive.

Team identity is also expected to have a shared visual color. The planned team-color material assignment is documented in `Docs/TeamColorSystem.md`; team-color marker parts should default to the neutral material in the prefab and swap to a player color at runtime.

Friendly character phasing and gentle overlap separation are planned as a separate movement-safety component, not as part of follow/move AI. See `Docs/CharacterSeparationSystem.md`.

---

## Configurable Starting Count

In `Gameplay.cs`, add one inspector field:

```csharp
[Header("Team Setup")]
public int StartingCharacterCount = 5;
```

This is the only place the number of starting characters needs to change. All spawning and tracking logic is driven by this value.

---

## Data Model Changes

### PlayerData struct (Gameplay.cs)

Add three fields:

```csharp
public int ActiveCharacterIndex;   // index of the currently controlled character
public int  AliveCharMaskLow;        // bits 0-31, serialized as int by Fusion
public int  AliveCharMaskHigh;       // bits 32-63, serialized as int by Fusion
public int CharacterCount;         // total characters this player has ever had (alive or dead), grows on recruitment
```

`IsAlive` keeps its existing name but changes meaning: a player is alive if `AliveCharacterMask != 0`. Update every place that reads or writes `IsAlive` to derive it from the mask rather than storing it directly.

Team-color work adds a small `TeamColorIndex` field here rather than trying to network `Material` references. That index maps to local material assets through the palette described in `Docs/TeamColorSystem.md`.

`Deaths` stays — it now counts individual character deaths across the match.

> **Note:** `AliveCharacterMask` supports up to 64 characters per player. It is backed by two `int` fields (`AliveCharMaskLow` / `AliveCharMaskHigh`) rather than a single `long` because Fusion 2's `INetworkStruct` serializer uses 4-byte word operations and does not natively support `long` fields. Using `long` directly causes the struct to appear dirty every tick, triggering constant retransmission and severe client ping spikes. The computed `AliveCharacterMask` property exposes a `long` interface to all calling code without any networking impact — Fusion only serializes the two backing `int` fields.

### Character lookup cache (non-networked, local to each peer)

Because the number of characters per player is variable, there is no fixed-size struct that could hold all their `NetworkId`s. Instead, every peer maintains a local (non-networked) lookup cache:

In `Gameplay.cs`:

```csharp
// Key: OwnerRef → (Key: CharacterIndex → Survivor component)
private readonly Dictionary<PlayerRef, Dictionary<int, Survivor>> _characterCache = new();
```

This cache is populated and cleared automatically via two new public methods:

```csharp
public void RegisterSurvivor(Survivor character)
{
    if (!_characterCache.TryGetValue(character.OwnerRef, out var dict))
    {
        dict = new Dictionary<int, Survivor>();
        _characterCache[character.OwnerRef] = dict;
    }
    dict[character.CharacterIndex] = character;
}

public void UnregisterSurvivor(Survivor character)
{
    if (_characterCache.TryGetValue(character.OwnerRef, out var dict))
        dict.Remove(character.CharacterIndex);
}
```

`Survivor.Spawned()` calls `RegisterSurvivor(this)` and `Survivor.Despawned()` calls `UnregisterSurvivor(this)`. Fusion calls these on every peer as objects appear and disappear in their local simulation via state replication, so the cache stays in sync on all machines without any explicit messages.

To look up a live character by owner and index:

```csharp
public Survivor GetSurvivor(PlayerRef owner, int index)
{
    return _characterCache.TryGetValue(owner, out var dict)
        && dict.TryGetValue(index, out var c) ? c : null;
}
```

### Survivor.cs — two networked properties on each survivor

```csharp
[Networked, HideInInspector] public PlayerRef OwnerRef       { get; set; }
[Networked, HideInInspector] public int       CharacterIndex { get; set; }
```

Both are set once immediately after spawn and never change. `OwnerRef` is the `PlayerRef` of the human who owns this character. `CharacterIndex` increments from 0 upward within a player's team across the whole match — recruited characters continue the sequence (e.g., if a player starts with indices 0–4, their first recruit is index 5).

### New input buttons (SurvivorInput.cs / EInputButton enum)

Add to `EInputButton`:

```csharp
PrevCharacter,   // Left Shift — switch to previous alive character
NextCharacter,   // Left Ctrl  — switch to next alive character
```

Add to `SurvivorInput.BeforeUpdate()`, alongside the existing fire/reload/weapon inputs:

```csharp
_accumulatedInput.Buttons.Set(EInputButton.PrevCharacter, Keyboard.current.leftShiftKey.isPressed);
_accumulatedInput.Buttons.Set(EInputButton.NextCharacter, Keyboard.current.leftCtrlKey.isPressed);
```

`SurvivorInput` exists on every spawned survivor because it is part of the survivor prefab, but only the active local character may subscribe to `NetworkEvents.OnInput`. If every local team member registers, Fusion receives multiple input providers for the same `PlayerRef`; with N characters the client polls hardware and calls `networkInput.Set(...)` N times per tick. `SurvivorInput` therefore registers and unregisters dynamically from Fusion's local `PlayerObject`. `PlayerData.ActiveCharacterIndex` remains the replicated gameplay state, but local input ownership should follow `Runner.GetPlayerObject(Runner.LocalPlayer)` so stale or delayed team data cannot make the client stop sending input for its active survivor.

---

## Spawning N Characters Per Player

### Remove

- `public float PlayerRespawnTime` inspector field
- `RespawnPlayer(PlayerRef, float)` coroutine
- The `StartCoroutine(RespawnPlayer(...))` call inside `PlayerKilled`

### Spawn point contract

Each `SpawnPoint` in the scene is placed so that a ~3m radius area around it is free of static obstacles. **All characters for a team spawn within this guaranteed area.** No per-character overlap checks are needed.

### Modify SpawnPlayer (Gameplay.cs)

Replace the single `Runner.Spawn` call with a loop:

```
basePoint = GetSpawnPoint()    // existing selection logic, unchanged
offsets   = GetClusterOffsets(StartingCharacterCount, clusterRadius: 1.5f)

for i in 0 .. StartingCharacterCount - 1:
    position  = basePoint.position + offsets[i]
    character = Runner.Spawn(SurvivorPrefab, position, basePoint.rotation, playerRef)
    character.GetComponent<Survivor>().OwnerRef       = playerRef
    character.GetComponent<Survivor>().CharacterIndex = i

playerData.ActiveCharacterIndex = 0
playerData.AliveCharacterMask   = (1L << StartingCharacterCount) - 1L  // all N bits set
playerData.CharacterCount       = StartingCharacterCount
playerData.IsAlive              = true
PlayerData.Set(playerRef, playerData)

// PlayerObject will be set automatically when Survivor.Spawned() fires on the first survivor
// and registers with the cache, then UpdatePlayerObject is called.
UpdatePlayerObject(playerRef, playerData)
```

**`GetClusterOffsets(int count, float clusterRadius)`** — returns `count` evenly-spaced positions on a circle of the given radius in the XZ plane:

```csharp
private static Vector3[] GetClusterOffsets(int count, float radius)
{
    if (count == 1) return new[] { Vector3.zero };

    var offsets = new Vector3[count];
    for (int i = 0; i < count; i++)
    {
        float angle = i * Mathf.PI * 2f / count;
        offsets[i] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
    }
    return offsets;
}
```

The default `clusterRadius` of 1.5m ensures all characters land well within the 3m guarantee, with room to spare even for large starting counts. For very high counts (20+ characters), reduce the default or add a concentric-ring variant — out of scope for now.

### Recruiting a character mid-match

When a character is recruited and flipped to a player's team (future system), call:

```csharp
public void RecruitCharacter(PlayerRef owner, Survivor character)
{
    // Called on state authority only.
    var data = PlayerData.Get(owner);
    int newIndex = data.CharacterCount;          // next sequential index
    character.OwnerRef       = owner;
    character.CharacterIndex = newIndex;
    data.AliveCharacterMask |= (1L << newIndex);
    data.CharacterCount++;
    data.IsAlive = true;
    PlayerData.Set(owner, data);
    // RegisterSurvivor is called automatically from Survivor.Spawned() / re-assignment.
}
```

This is not fully implemented here — it documents the extension point so the design is consistent.

---

## Control Model

### The isActive flag and input routing

Every character runs `FixedUpdateNetwork()`. The critical change is **how** input is sourced, not whether it is processed — `ProcessInput()` must be called for every character every tick, with the input coming from either the human player or the AI controller:

```csharp
// In Survivor.FixedUpdateNetwork(), replacing the current input-processing block:

if (!_sceneObjects.Gameplay.PlayerData.TryGet(OwnerRef, out var ownerData))
    return;

bool isActive = ownerData.ActiveCharacterIndex == CharacterIndex;

NetworkedInput input = default;

if (isActive)
{
    // Human-controlled: Fusion provides the real input from hardware (on client)
    // or from the received input packet (on server).
    Runner.TryGetInputForPlayer(Object.InputAuthority, out input);
}
else if (HasStateAuthority)
{
    // AI-controlled: ask the AI controller for a NetworkedInput.
    // Only runs on state authority — AI decisions are server-authoritative.
    // Clients do not predict inactive characters; they interpolate the authoritative result.
    input = _aiController.GetInput(Runner);
}
else
{
    return; // Non-active character on a non-authority client: nothing to do.
}

ProcessInput(input);
```

`ProcessInput` keeps the shared movement/fire/weapon path, but it receives whether the survivor is currently active. Active survivors move with `MoveSpeed`. Inactive survivors normally move with `AIMoveSpeed`, except a survivor following the currently possessed survivor uses `MoveSpeed` while inside `AIFollowFullSpeedRadius` so nearby followers can keep up without sprinting across the whole map.

### Input provider ownership

Only the active local character's `SurvivorInput` is registered with Fusion's `NetworkEvents.OnInput` callback. Inactive local characters keep their `SurvivorInput` component enabled, but `BeforeUpdate()` returns before reading keyboard/mouse state and `OnInput()` is not subscribed.

The active input provider is selected by checking Fusion's local `PlayerObject`, not by relying only on the replicated `PlayerData` dictionary. `Gameplay.UpdatePlayerObject(...)` points each player at their active survivor, and `SurvivorInput.ShouldProvideInput()` follows that object. The replicated `PlayerData.ActiveCharacterIndex` is used only as a short startup/switch fallback before `PlayerObject` is available locally.

`SurvivorInput.BeforeUpdate()` clears movement and button input before reading the current frame's hardware state. Mouse look still uses `Vector2Accumulator`, but stale movement/fire/order buttons must not survive a focus loss, cursor unlock, registration change, or missed input update. This prevents the host from continuing to receive old movement or firing state while the client believes it has stopped or is aiming elsewhere.

This is separate from `Survivor.FixedUpdateNetwork()` input routing. The active character still reads the single per-player `NetworkedInput` with `GetInput(out input)`, while inactive characters on state authority use their AI input source. Keeping exactly one human input provider per local player avoids duplicate callback work and avoids inflating the client input path as team size grows.

### First-person visuals

`SetFirstPersonVisuals(bool firstPerson)` in `Survivor.cs` activates for `isActive && HasInputAuthority`, so only the active character renders the first-person view.

### Animation movement velocity

`Survivor.Render()` drives the third-person Animator from `GetAnimationMoveVelocity()`.

The active local survivor and state-authority survivors use `KCC.RealVelocity`, because those instances are actually simulating KCC movement. Non-active client survivors and remote proxy survivors use the replicated `_moveVelocity` value instead, because their local `KCC.RealVelocity` can be zero or stale.

```csharp
Vector3 velocity = ShouldUseKCCAnimationVelocity() ? KCC.RealVelocity : _moveVelocity;
```

This keeps uncontrolled teammates and remote survivors animating correctly on clients without reading tiny render/interpolation corrections as intentional movement. `MoveSpeed` is damped before it reaches the Animator so the idle/walk blend does not flicker when a proxy is settling.

### Character switching input

Still inside the `isActive` block, add this after the existing weapon-switch checks in `ProcessInput`:

```csharp
bool nextPressed = input.Buttons.WasPressed(_previousButtons, EInputButton.NextCharacter);
bool prevPressed = input.Buttons.WasPressed(_previousButtons, EInputButton.PrevCharacter);

if ((nextPressed || prevPressed) && HasStateAuthority)
{
    int dir = nextPressed ? 1 : -1;
    _sceneObjects.Gameplay.SwitchActiveCharacter(OwnerRef, dir);
}
```

The `HasStateAuthority` guard means the switch only executes on the server's authoritative forward tick. The client sees the result via the networked `PlayerData` change. This matches the existing pattern for weapon switching.

**`Gameplay.SwitchActiveCharacter(PlayerRef owner, int direction)`:**

```csharp
public void SwitchActiveCharacter(PlayerRef owner, int direction)
{
    if (!PlayerData.TryGet(owner, out var data)) return;

    int next = FindNextAliveCharacter(data.AliveCharacterMask, data.CharacterCount,
                                      data.ActiveCharacterIndex, direction);
    if (next < 0 || next == data.ActiveCharacterIndex) return;

    data.ActiveCharacterIndex = next;
    PlayerData.Set(owner, data);
    UpdatePlayerObject(owner, data);
}
```

Before the previous active character is returned to AI control, reset its vertical look pitch to neutral while preserving yaw. This prevents unpossessed survivors from keeping the player's last up/down camera angle. The reset should happen through a survivor helper, for example `ResetVerticalLook()`, so KCC look state and the camera handle stay consistent.

---

## AI Input Architecture

### Principle

AI should remain just another source of `NetworkedInput`; it follows the same movement, fire, and weapon-switch code paths. The survivor may still apply different tuning, such as `AIMoveSpeed`, after input is read.

### ICharacterInputSource interface

Define this interface as a nested type at the bottom of `Survivor.cs`:

```csharp
public interface ICharacterInputSource
{
    NetworkedInput GetInput(NetworkRunner runner);
}
```

### Built-in AI sources

The first concrete AI sources live in `Assets/Scripts/Survivor/AI/`:

- `SurvivorNonCombatAI` is a survivor component that owns hold, follow, and move assignments for uncontrolled survivors.
- `SurvivorLootingAI` and `SurvivorInvestigationAI` are sibling components used by `SurvivorNonCombatAI` for pickup collection and suspicious-stimulus investigation.
- Follow assignments use `CharacterNavigator` path corners when available and direct movement as fallback.

See `Docs/CharacterAICommands.md` for the follow-command rules and implementation details.

### Field on Survivor.cs

```csharp
private ICharacterInputSource _aiController;
```

This field is not networked and is never serialized. AI assignment happens on state authority with `Survivor.SetAI(...)` and `Survivor.SetIdleAI()`.

`SetIdleAI()` should activate the survivor's `SurvivorNonCombatAI` component. If the prefab is missing the component, `Survivor` adds it at runtime as a fallback. Prefer adding `SurvivorNonCombatAI`, `SurvivorLootingAI`, and `SurvivorInvestigationAI` to the prefab so their tuning values are visible in the Inspector.

### Why AI runs only on state authority

Fusion clients predict their own input-authority objects. If clients also ran AI logic for inactive characters, every client would generate different (potentially diverging) AI decisions, causing constant mispredictions and corrections. By restricting AI to the state authority:

- AI decisions are deterministic and authoritative.
- Clients just interpolate the result — the same way they handle any non-input-authority object.
- No special handling needed for resimulation.

### Extending AI in the future

1. Create a new class implementing `ICharacterInputSource` (e.g., `DefendPointAI`).
2. Assign it through `Survivor.SetAI(...)` based on the character's team role or command state.
3. `ProcessInput` requires zero changes.

---

## Character Death Flow

### Rename / replace PlayerKilled

The existing `PlayerKilled` method is repurposed into `CharacterKilled`. The kill-feed RPC (`RPC_PlayerKilled`) and its parameters stay unchanged so the UI requires no changes.

**`Gameplay.CharacterKilled(killerRef, ownerRef, characterIndex, weaponType, isCritical)`:**

```csharp
public void CharacterKilled(PlayerRef killerRef, PlayerRef ownerRef, int characterIndex,
    EWeaponType weaponType, bool isCriticalKill)
{
    if (!HasStateAuthority) return;

    // Update killer stats.
    if (PlayerData.TryGet(killerRef, out var killerData))
    {
        killerData.Kills++;
        killerData.LastKillTick = Runner.Tick;
        PlayerData.Set(killerRef, killerData);
    }

    // Mark character as dead.
    var victimData = PlayerData.Get(ownerRef);
    victimData.AliveCharacterMask &= ~(1L << characterIndex);
    victimData.Deaths++;

    // Transfer control if the active character just died.
    if (victimData.ActiveCharacterIndex == characterIndex)
    {
        victimData.ActiveCharacterIndex = FindNextAliveCharacter(
            victimData.AliveCharacterMask, victimData.CharacterCount, characterIndex, 1);
        // -1 means no alive characters remain.
    }

    victimData.IsAlive = victimData.AliveCharacterMask != 0;
    PlayerData.Set(ownerRef, victimData);
    UpdatePlayerObject(ownerRef, victimData);

    RPC_PlayerKilled(killerRef, ownerRef, weaponType, isCriticalKill);
    CheckWinCondition();
    RecalculateStatisticPositions();
}
```

**`FindNextAliveCharacter`** — wraps through the player's full character count, not a hardcoded 5:

```csharp
private int FindNextAliveCharacter(int aliveMask, int characterCount, int startIndex, int direction)
{
    for (int i = 1; i <= characterCount; i++)
    {
        int candidate = ((startIndex + direction * i) % characterCount + characterCount) % characterCount;
        if ((aliveMask & (1 << candidate)) != 0)
            return candidate;
    }
    return -1;
}
```

**`UpdatePlayerObject`** — uses the cache, not a fixed struct:

```csharp
private void UpdatePlayerObject(PlayerRef playerRef, PlayerData data)
{
    if (data.ActiveCharacterIndex < 0) return;
    var character = GetSurvivor(playerRef, data.ActiveCharacterIndex);
    if (character != null)
        Runner.SetPlayerObject(playerRef, character.Object);
}
```

### Trigger in Health.cs

Change the call in `Health.ApplyDamage()` from:

```csharp
_sceneObjects.Gameplay.PlayerKilled(instigator, Object.InputAuthority, weaponType, isCritical);
```

to:

```csharp
var survivor = GetComponent<Survivor>();
_sceneObjects.Gameplay.CharacterKilled(
    instigator, survivor.OwnerRef, survivor.CharacterIndex, weaponType, isCritical);
```

---

## Win Condition

Replace the timer-only end with "last team standing", keeping the time limit as a hard cap.

Add `CheckWinCondition()` to `Gameplay.cs`, called at the end of `CharacterKilled()`:

```csharp
private void CheckWinCondition()
{
    if (State != EGameplayState.Running) return;

    int teamsAlive = 0;
    foreach (var kvp in PlayerData)
    {
        if (kvp.Value.IsAlive) teamsAlive++;
    }

    if (teamsAlive <= 1)
        StopGameplay(); // existing method, sets State = Finished
}
```

The existing `RemainingTime.Expired(Runner)` check in `FixedUpdateNetwork()` stays — it acts as the configurable hard time limit.

---

## Spectator Mode (minimal)

When a player's `AliveCharacterMask` reaches 0, their client enters spectator state:

- In `Survivor.Render()`: if `HasInputAuthority && !_sceneObjects.Gameplay.PlayerData[OwnerRef].IsAlive`, skip all input processing and first-person camera setup. The player sees the world from their last character's death position.
- Show a "You have been eliminated" overlay via `GameUI`. The exact UI is out of scope — only the condition trigger is specified here.

Full free-roam spectator camera is out of scope.

---

## Files Modified

| File | What changes |
|------|--------------|
| `Assets/Scripts/Gameplay/Gameplay.cs` | Remove respawn logic; add `StartingCharacterCount` field; add `_characterCache` dict; add `RegisterSurvivor`, `UnregisterSurvivor`, `GetSurvivor`; modify `SpawnPlayer` to spawn N survivors; add `CharacterKilled`, `SwitchActiveCharacter`, `CheckWinCondition`, `FindNextAliveCharacter`, `UpdatePlayerObject`, `GetClusterOffsets`; add `RecruitCharacter` extension point |
| `Assets/Scripts/Survivor/Survivor.cs` | Add `OwnerRef` and `CharacterIndex` networked props; call `RegisterSurvivor`/`UnregisterSurvivor` in `Spawned`/`Despawned`; replace `isActive` early-return with input-routing block in `FixedUpdateNetwork`; update `SetFirstPersonVisuals` condition; add `ICharacterInputSource`; add `_aiController` field and AI assignment helpers |
| `Assets/Scripts/Survivor/Health.cs` | Change `PlayerKilled` call to `CharacterKilled` with character index and owner ref |
| `Assets/Scripts/Survivor/SurvivorInput.cs` | Add `PrevCharacter` and `NextCharacter` to `EInputButton`; add Shift/Ctrl input in `BeforeUpdate`; dynamically register `OnInput` only for Fusion's local `PlayerObject`; clear stale frame input before reading current hardware state |

---

## Performance Notes

**Single input provider per local team.** Each survivor prefab instance has a `SurvivorInput`, and all characters owned by the local player currently have input authority. Only the active local `PlayerObject` is allowed to register with `NetworkEvents.OnInput`; otherwise each extra teammate adds another hardware poll and another `networkInput.Set(...)` call for the same player every tick. Duplicate or stale input providers are especially visible in host/client tests as aim divergence, delayed corrections, or rising reported RTT when `StartingCharacterCount` is increased.

**Local testing is inherently heavier than production.** When running host + client on the same machine, both simulations compete for the same CPU. The host runs all 10 characters' FixedUpdateNetwork (5 per player × 2 players) while the client also runs prediction and interpolation for its own 5 characters. This doubles the per-machine load compared to a real deployment where server and client are separate machines.

**Settled character optimisation.** Inactive characters that are grounded with near-zero velocity skip `ProcessInput` (and therefore `KCC.Move`) entirely each tick. The check `!isActive && KCC.IsGrounded && _moveVelocity.sqrMagnitude < 0.01f` catches this in `Survivor.FixedUpdateNetwork`. Characters settle within a few ticks of spawning, after which the per-tick KCC cost drops from N calls to 1 call (only the active character). This is the primary guard against Fusion's burst catch-up mode, where a single over-budget tick causes the next frame to run 2–3 ticks to compensate, creating visible ping spikes.

---

Follow AI is exempt from the settled-character skip whenever it emits movement or look input, so a settled teammate can still start following when commanded.

**Local multi-instance note.** The single input provider rule is a multi-character safeguard, not the full explanation for every ping spike. If three separate players with one character each show the same symptom on one PC, the likely cause is local multi-instance load or background throttling. See `Docs/LocalMultiplayerTesting.md` for the recommended local test setup.

---

## Out of Scope

- Real AI behaviour for uncontrolled characters (stub stands idle; interface is in place)
- Full free-roam spectator camera
- UI health bars showing each team member's status
- Character spawn animation or intro sequence
- Concentric-ring spawn layout for very large teams (20+ characters)
