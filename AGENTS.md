# PhotonFusionFPSTest — AI Context

## What this project is
A multiplayer arena-based shooter prototype built on the Photon Fusion FPS template (Unity 2022.3.62f3 LTS).
The goal is to prototype a **PvPvPvPvE** format — where there are 2 to ~20 players, each controlling their own team with distinct team colors. Each team has one human player and one or more survivor characters. The player can seamlessly take direct control over any survivor on their team. While not under direct control, survivors have rudimentary AI capable of simple instructions and defending themselves to their best abilities.

## What the ultimate goal of this project is
A typical match in the finished project starts with 4 players. Each player spawns in a semi-randomly generated city-arena with 5 controllable survivors. Two players focus more on RTS-like gameplay and control their survivors through a camera high up. Two others take direct control over one of their survivors and tell the others to follow them.

In the city there are structures that spawn zombies. The amount and aggressiveness of these zombies increases as the match drags on, overwhelming at some point everybody.

There are weapons and neutral recruitable survivors in the city too. When a team-affiliated survivor gets close enough to a neutral survivor, that survivor will flip to their team. The zombies are always aggressive towards all survivors, and fighting can be heard through the city soon after the match starts.

In the lobby there are a bunch of variables the host can change. From the amount of starting characters and the quality of their gear, map size, map loot and recruitable characters amounts, random events options to spice the match up and how long the match should last (there is no 'hard' limit, this increases the zombie spawns/aggressiveness, meaning that a 15min match will end latest after 15 mins, because it will become unsustainable for everybody to survive. The last surviving team wins.)

The game should be semi-competitive for 2 players playing on a small, mirrored map. It should also be possible to setup a long match with 8 or more players on a huge map that might take an hour to finish. In the end, the game is an arena based FPS multiplayer game where each player controls a team of survivors instead of a single character, and where the arena is actively hostile to them all.

## Tech stack
- **Engine:** Unity 2022.3.62f3 LTS
- **Networking:** Photon Fusion (state sync, KCC addon)
- **Rendering:** Universal Render Pipeline (URP)
- **Input:** Unity New Input System
- **Camera:** Cinemachine
- **Namespace:** `SimpleFPS`

## Key scripts
- `Assets/Scripts/Gameplay/Gameplay.cs` — core match loop (Windows/Mac/Linux only, has `#error` guard blocking mobile/WebGL)
- `Assets/Scripts/Survivor/Survivor.cs` — networked survivor character controller
- `Assets/Scripts/Survivor/SurvivorInput.cs` — active-survivor input handling
- `Assets/Scripts/Weapons/Weapon.cs` — weapon base logic
- `Assets/Scripts/Weapons/Weapons.cs` — weapon inventory/switching
- `Assets/Scripts/UI/GameUI.cs` — in-game HUD
- `Assets/Scripts/Menu/MenuConnectionBehaviour.cs` — lobby/connection flow

## Scenes
- `Assets/Scenes/Startup.unity` — menu and connection screen
- `Assets/Scenes/Deathmatch.unity` — main gameplay scene (pre-baked lighting)

## Planning & design docs
See `Docs/` for feature specs, game design decisions, and architecture notes.

## Working rules
- **Document features to the Docs folder in the root of the project** (when working on a feature, like networking bullets, create or update a .md file in `Docs/` where the whole of the feature is explained in a way that it is reproducible by a human or an AI. Use common sense when deciding to create new .md files, there should be one for every "feature", but we do not need separate file for every class. Keep these files up-to-date, there is no point in keeping an explanation on how something used to work if it no longer works like that when you start your workflow.)
- **Read the relevant feature documentation from the Docs folder** (before starting work on a feature, check the `Docs/` folder for documenation on the feature or features that are related to the feature in question.)
- **Always ask before modifying networking code** (anything with `[Networked]` attributes or inside `Assets/Photon/`)
- **Don't refactor or clean up unless explicitly asked** — only change what the task requires
- **Ask before touching scenes** — scene files are binary-ish and merge badly
- **No new features beyond what's requested** — this is a prototype, keep scope tight
- **Prefer editing existing files** over creating new ones

## Git
- Initialized April 2026
- No remote yet
