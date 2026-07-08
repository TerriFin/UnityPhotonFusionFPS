# Raid Mode Client Rescue

## Status

Implemented. Raid clients who lose their last survivor while the raid host is still alive can be kept in the
match by taking control of an allied client survivor or, later, the next allied client recruit.

## Goal

Raid mode should not throw client players out of the match too early while the raid boss is still the main
threat. If a non-host client is wiped while the raid host is alive:

1. The game looks for the closest alive, non-possessed survivor owned by another allied non-host client.
2. If one exists, that survivor is transferred to the eliminated client and immediately becomes their possessed
   active survivor.
3. If none exists, the client is eliminated normally and enters defeated spectate mode.
4. While the raid host remains alive, a later neutral survivor recruited by an allied client can be redirected to
   a waiting eliminated client, reviving and possessing them automatically.

This is not shared control. The rescued client really owns the stolen survivor after the transfer.

## Activation Rules

Rescue is allowed only while the existing raid client alliance is active:

```text
raid mode is running
AND the raid host can be resolved
AND the raid host is connected
AND the raid host PlayerData.IsAlive is true
```

The rescued player must be:

- a real connected player;
- not the raid host;
- out of living survivors;
- already part of the match (`CharacterCount > 0`);
- below the 128-character roster capacity.

The donor must be:

- a real connected non-host client;
- currently alive;
- allied with the eliminated client through the raid client alliance.

The stolen survivor must be:

- alive and player-owned;
- owned by the donor;
- not the donor's active/possessed survivor;
- safe to remove without wiping the donor's team. In normal play this means the donor keeps their possessed
  survivor.

The host's survivors are never stolen. Possessed client survivors are never stolen.

## Team-Wipe Rescue Flow

`Gameplay.CharacterKilled()` still marks the killed survivor dead and increments the victim's death count first.
If that was the victim client's final living survivor, `Gameplay` attempts raid rescue before it writes the final
victim `PlayerData` and before `CheckWinCondition()` runs.

The rescue search uses the killed survivor's final position as the origin and picks the closest valid allied
non-possessed client survivor. If the killed survivor cannot be found in the local cache, the first valid
candidate is accepted as a fallback.

When a candidate is found:

- the donor's roster removes that survivor and compacts later `CharacterIndex` values down by one;
- the donor's alive mask and active index are adjusted to match the compacted roster;
- input authority for the stolen survivor and its child `NetworkObject`s is reassigned to the rescued client;
- the survivor's networked `OwnerRef` and `CharacterIndex` are changed to the rescued client and an appended
  roster index;
- the rescued client's alive mask, `CharacterCount`, `IsAlive`, and `ActiveCharacterIndex` are updated;
- `Runner.SetPlayerObject()` points the rescued player at the stolen survivor;
- the survivor is reset to idle AI and neutral vertical look so possession starts cleanly.

Because ownership and input authority are transferred, every existing system continues to work through normal
team ownership: map icons, possession, commands, team color, damage rules, AI relationships, and scoring all see
the survivor as belonging to the rescued client.

## Recruitment Rescue Flow

If a client has already been eliminated because no stealable survivor existed, they can still return while the
raid host is alive.

`Gameplay.TryRecruitNeutralSurvivor()` checks for waiting eliminated non-host clients before assigning a neutral
to the recruiting survivor's owner. If it finds one, the neutral survivor is assigned directly to the waiting
client instead of to the recruiter:

- the recruited survivor is appended to the waiting client's roster;
- the waiting client becomes alive again;
- the recruit becomes their active possessed survivor;
- the recruiting client does not receive that recruit.

If multiple defeated clients are waiting, the recruited survivor goes to the waiting client whose existing team
survivor/corpse is closest to the recruit. This keeps the choice spatially understandable without adding a new
queue or replicated tie-breaker field.

Neutral survivor stat restoration still happens in `NeutralSurvivorOrchestrator` after recruitment succeeds.

## Network Model

No new `[Networked]` field, RPC, or scene/prefab change is used. The feature relies on existing replicated state:

- `PlayerData` alive mask / active index / character count / alive flag;
- survivor `OwnerRef` and `CharacterIndex`;
- Fusion input authority on the survivor hierarchy;
- the existing `Gameplay.RaidHostPlayer` identity and host `PlayerData.IsAlive` used by the raid alliance.

All rescue decisions run only on state authority.

## Limits

- A defeated client at the 128-character roster capacity cannot be rescued because there is no free character
  index to append the stolen/recruited survivor to.
- Rescue stops immediately when the raid host is defeated or disconnected. After that, clients are hostile and
  normal elimination/spectate rules apply.
- The donor is not allowed to be wiped by a rescue steal; if all possible allied survivors are possessed or are
  the donor's last living survivor, the eliminated client spectates until a later recruit is redirected.

## Verification

Use at least host + two clients.

- Client A and Client B alive, raid host alive, Client A loses final survivor, Client B has active + AI survivor:
  Client A steals Client B's closest AI survivor and immediately possesses it.
- Same setup, but Client B has only the possessed survivor: Client A enters defeated spectate.
- Client B later recruits a neutral while the raid host is alive: the new recruit is assigned to Client A and
  Client A leaves spectate by possessing it.
- Host survivors and possessed client survivors are never selected as steal candidates.
- After raid host death/disconnect, rescue no longer occurs and client eliminations behave normally.
