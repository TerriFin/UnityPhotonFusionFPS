# FOR HUMAN ONLY, DO NOT MODIFY THIS FILE
feel free to read, might give good context :)

## Design Direction:
 - Prefer modular AI behavior components.
 - Avoid hardcoding zombie/survivor distinctions where faction/team logic can handle it.
 - World gen should stay layered: height -> roads -> buildings -> loot -> special encounters.

## To-do list:
- Zombies
- Separate human combat AI vs Zombies
- Zombies get stronger, more numerous as game goes on (after time limit supercharge them and make them home in on survivors)
- Neutral, recruitable survivors
- Server startup options (map generation sliders, game modifiers, game duration)
- Disconnect/Join cleanup (error messages, game crashes, spectate mode)
- Map show survivor info (list your survivors and their statuses, allow toggling of individual behaviors)
- Overall polishing of survivor AI (at this point all behaviors should be in, can polish)
- Rework weapon system (all characters "have" all weapons at the same time, unnecessary)
- Pickup system (no loot respawning, drop loot when dying, zombies can drop with a small chance?)
- Loot distribution system (toggleable setting for non-combat AI? Player can "take" weapon from non-controlled characters?)
- Large, detailed points of interests
- Hostile, non-recruitable survivors
- Garrison system for non-controlled human AIs (pre-set waypoints within structures? Solves multi-level structure problem?)
- Actual models for everything

## Nice to have ideas:
- New weapons
- Control groups for map
- "Play as zombie" spectate mode alternative
- Different tile sets for different environments
- Outside city areas
- River/Train track running across the map
