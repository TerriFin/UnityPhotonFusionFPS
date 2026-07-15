# Zombie Traversal Design History — Complete Version Catalog

**Read this before touching zombie traversal.** Six versions of this system have been built since May 2026. Each one solved its predecessor's headline failure and then failed somewhere else — usually at the same place: street props (cars/crates) that are deliberately cut out of the NavMesh. This file records every version, its exact mechanism, what it got right, how it failed, and the cross-version lessons. `Docs/ZombieAI.md` documents only the *current* system operationally.

Sources: git history (commits cited per version), the original design notes this file replaces, and the July 2026 rework session log.

---

## Version timeline

| Ver | Date | Commits | Model | Fate |
|---|---|---|---|---|
| V0 | May 24–28 | `508a483`..`fa2fe5f` | Pure NavMesh, no climbing | Superseded: props/roofs = safe camping |
| V1 | Jun 1–2 | `376688d`, `1bef551` | Route-length vs direct + climb-anything probes | Failed indoors: wall/ceiling climbing |
| V1.x | Jun 6–12 | `380a87e`, `1e9c487` | V1 + special-case patches (roof check, probe caps) | Patch accumulation, still fragile |
| V2 | Jun 15 | `4a8bffc` | + `ZombieTerrain`: height-snapshot ledge shortcuts; buildings = NavMesh + capped climb | Ledges good; prop/edge cases persisted |
| V3 | ~Jun 17–21 | *(never committed)* | NavMesh links in a zombie-only area | Dead end: links can't reach off-mesh props |
| V4 | Jun 22 – Jul 8 | `606420f`..`e6e082c` | Climb-surface registry + road-gated direct chase + authored rescue faces | Failed rooftops; rescue never authored |
| V5 | Jul 9–12 | *(uncommitted)* | Connectivity-gated: same-plane chase + goal-height-capped pursuit + descend | 8 fix rounds; car-hood climb still unreliable |
| V6 | Jul 15 | *(uncommitted)* | **Reactive wall climbing**: path mode / direct mode + climb-on-contact, one cap formula, jam reflex | **Current.** Runtime validation pending |

---

## V0 — Pure NavMesh (May 24–28; `508a483`, `9917633`, `fa2fe5f`)

**Model.** Idle wander picks reachable NavMesh points biased away from other zombies; attacking/hunting walk NavMesh paths to the target; a small `DirectFallbackDistance` closes the last step. No climbing of any kind.

**Worked.** Everything on connected NavMesh — streets, interiors with doors, ramps. Cheap, predictable, never froze. This walking core survives unchanged (structurally) in every later version.

**Failed.** Any survivor on a car, crate, ledge, or roof was permanently safe. Unacceptable for the core design goal ("survivors must not become safe by standing on props").

---

## V1 — Route-length vs direct, climb anything (Jun 1–2; `376688d`, `1bef551`)

**Model.** Climbing introduced (`CanClimbUnreachableTargets`, then generalized to `CanClimbDirectGoals`). Unified policy: compare the NavMesh path length to the straight line; if the path is over `DirectRouteLengthMultiplier` (1.5×) longer (or absent) and the goal is within `MaxDirectTraversalDistance` (40 m), walk **direct** and climb whatever obstacle the forward probe finds (`ClimbObstacleProbeDistance`), mantling onto raycast-found tops. Climb execution (KCC gravity-off impulse + mantle snap) is born here and survives to today.

**Worked.** Street props and open terrain ledges — the obvious cheese cases died immediately. The route-vs-direct *idea* (climb when walking is a big detour) also survives to today as the terrain-shortcut rule.

**Failed.** Indoors and around buildings the "shortest direct line" is physically meaningless: zombies climbed interior walls and ceilings when survivors were above / behind walls / leaning on geometry; multi-storey buildings made the direct line cross floors. Each fix was a new special case.

---

## V1.x — The special-case era (Jun 6–12; `380a87e`, `1e9c487`)

**Patches.** Probe capped short of the goal (`ClimbObstacleBeyondGoalBuffer`); then a whole "Roof Avoidance" subsystem (`AvoidClimbingToRoofedGoals`, an up-cast `RoofCheckHeight` probe over the goal, `ShelteredGoalClimbMinRise`) so zombies would ground-route to sheltered survivors instead of scaling the building.

**Lesson.** This is the canonical shape of failure for this problem: a general inference rule (route-vs-direct) meets arbitrary geometry, and every counterexample becomes a bespoke check. The roof up-cast is notable as the first *goal-classification* probe — the idea returns in V5 (support-aware connectivity), where its inverse ordering bug also returns (see V5 round 8).

---

## V2 — ZombieTerrain ledge service (Jun 15; `4a8bffc`)

**Model.** Kept V1's route-vs-direct + capped final climb, but added a **deterministic data source**: `ZombieTerrain`, a static service holding the generated `WorldHeightSnapshot`, answering "what height level is here" and "where is the nearest climbable ledge stepping up a level toward the goal". Terrain ledges (guaranteed single-step, overhang-free by generation) became first-class climb targets; buildings explicitly excluded ("reach those through the NavMesh plus a capped final climb").

**Worked.** Terrain ledge climbing from generated data was reliable — the height map is truth, no raycast inference needed. This is the ancestor of the (still never-broken) terrain climb surfaces.

**Failed.** Everything not terrain still ran V1.x rules with all their edge cases. Scrapped days later in favor of the "theoretically cleanest" model.

---

## V3 — NavMesh links (≈Jun 17–21; built and abandoned, never committed)

**Model.** Make climbing part of the navigation graph: every terrain ledge gets a zombie-only `NavMeshLink` in a dedicated `ZombieClimb` NavMesh area (index 3); survivors exclude that area via `CharacterNavigator.AreaMask`, so one shared bake routes zombies through climbs and survivors around them. Climb-vs-detour becomes pure A\* cost; "where can I climb" becomes "where a link exists". A path corner rising steeply is the climb trigger; the zombie climbs and mantles onto the link's known top.

**The part that worked (keep in reserve).** The area-mask faction split is sound and proven: a path query's `areaMask` filters off-mesh links by area, so zombie-only links on a shared NavMesh work exactly as intended.

**The chain of concrete failures (each fixed, each revealing the next):**

1. **Links built but A\* routed around them.** The modern `NavMeshLink` *component* connects in `OnEnable` — before runtime endpoints were set — registering a degenerate link at the origin (diagnostic: `pathLen 49.7` vs `straight 8.1`). Fix: the low-level `NavMesh.AddLink(NavMeshLinkData)` API with explicit world endpoints.
2. **Zombies teleported onto players from ramps.** `NavMesh.CalculatePath` emits **no corner on a straight slope**, so "next corner much higher than me" is true on any ramp, and the mantle `SetPosition`'d the zombie onto that far corner. Fix: gate climbs on **steepness (rise:run)**, never raw height delta.
3. **Climbs oscillated (rise, drop, repeat).** A fixed ~5 s timeout undercut the real climb time (`climb_speed × ledge_height` ≈ 7 s). Fix: **progress-based** stuck detection — abandon only when height gain stops. This survived into every later version.
4. **Corner ledges under-covered** (~10 of 42 ledges got links): endpoint geometry assumed one cardinal cliff direction. Drove the current generator's per-exposed-side / dual-half-face corner handling.
5. **Feel problems:** zombies visibly reoriented to discrete link start points; climbs began before touching the wall; mid-approach retargets cancelled climbs. Robotic and fragile at horde scale.

**The decisive, architectural blow:** street cars/trucks carry `NavMeshModifier` and are cut from the NavMesh **so RTS survivor pathing never walks onto them**. A link needs NavMesh at both ends → *no link can ever land on a car* → the model cannot reach the exact cheese case the feature exists for. Links are excellent for terrain (mesh both sides) and a dead end for off-mesh props.

---

## V4 — Surface registry + road-gated chase + rescue faces (Jun 22 – Jul 8; `606420f`, `937b6d8`, `3abe461`, `5df3152`, `b224828`, `b9721b2`, `e6e082c`)

**Model.** Three cooperating mechanisms (plus NavMesh walking):
1. **Generated terrain climb surfaces** (`ZombieClimbSurfaces.BuildTerrain` from the height snapshot; broad face segments per non-walkable ledge, corner-aware). Used as *shortcut* climbs when the direct line crosses a face and the path detour exceeds `TerrainClimbShortcutMinPathSavings`. Direct descendant of V2's ledge service; **this mechanism has never produced a reported failure.**
2. **Road-direct chase**: if the goal was on a **road grid cell**, visible, within 18 m — walk straight at it and vault probed blockers. Handled cars/crates/fences *on streets* without authoring.
3. **Rescue surfaces**: `ZombieClimbableSurface` prefab component registering bounds-derived climb faces, valid only when a face top matched the target height ±2.5 m *and* the landing fell within 2.5 m flat of the target.

Support work in this era: 3D corner advancement for switchback ramps (`937b6d8` — later the cause of a V5 freeze, see below), `NavMeshIslandMap` (`3abe461`+) — precomputed NavMesh connectivity that made "is there a path" questions ~free, overtime hunting player-commitment, and the knob→constant consolidation passes.

**Worked.** Streets felt aggressive; terrain ledges climbed reliably; the island map held up everywhere and is load-bearing in V5.

**Failed.**
- **Rooftops** (the trigger for V5): not road cells → no chase; no rescue face could put a landing within 2.5 m of a mid-roof survivor. In practice **no prefab in the project ever carried `ZombieClimbableSurface`** — the rescue path was dead weight from day one. Overtime zombies stood under roof-campers.
- Investigation of unreachable points never timed out; pre-overtime unreachable-target attacking just stared.
- Hidden cap bug: the advertised 2.25 m street vault cap was dead code — `Mathf.Max(2.25, RoadDirectMaxObstacleHeight=5)` made the real cap 5 m; only the road gate hid the consequences.
- The road gate itself was a *proxy* for the real question (NavMesh connectivity) and over-restricted (no chase in plazas/courtyards) while implicitly hiding several V5-era bugs by keeping the chase away from towers and elevated goals.

---

## V5 — Connectivity-gated chase + goal-height-capped pursuit (Jul 9–12; uncommitted) — CURRENT

**Model.** One cached question routes everything (`NavMeshIslandMap`, ~0.2 s jittered): **is the explicit goal NavMesh-connected from here?**
- **Connected & same walking plane (|Δy| ≤ 0.75)** → *direct chase*: beeline + vaults up to a real 2.25 m.
- **Connected & elevated/below** → *navigator* (ramps, stairs) + terrain shortcut surfaces.
- **Disconnected** → *climb pursuit* (≤ 13 m): steer straight at the goal, climb probed obstacles, landing capped at `goal.y + 1.5` (the roof rule: survivor on roof → climb to roof; survivor inside floor 2 → window height, roof rejected). No LOS gate. Multi-stage via the cached verdict refreshing after each mantle.
- **Connected below + zombie off-mesh on a prop** → *descend*: walk straight off the prop at the target from tick one, progress-checked with a fast (1 s) random sidestep.
- Pre-overtime give-up (10 s no investigation progress → Idle). Deleted: road-grid plumbing, the rescue path, the two stuck-input special cases.

**The eight fix rounds (Jul 9–10), each a real mechanism — recorded so none is re-learned:**

1. **Chase shadowing:** un-gated chase fired every tick for sensed survivors on connected ledge tops and returned before the terrain-shortcut code could run — zombies pinned at 7.5 m walls with a 2.25 m vault cap. → chase height gate.
2. **Vertical tolerance freeze:** the connectivity verdict's 2.0 m goal-height tolerance made car roofs ≤ 2 m "connected" yet unwalkable — pursuit stood down, zombies froze on hoods. → support-aware verdict (judge at the surface the goal stands on, tight 0.75 m).
3. **Tower jitter / spin:** chase serving any altitude gap fought the navigator on the watchtower (beeline into ramp railings) and spun over targets below (flat-steering to the survivor's XZ on the platform above them). → symmetric same-plane gate: **chase never handles any altitude difference, in either direction.**
4. **Mid-climb drops:** `StartAttack` re-fires for the *same* survivor every `AttackRetargetInterval` (1 s), and sight-loss fires `StartInvestigation`; both cleared active climbs unless the new goal was near the old landing — a target strolling along the ledge dropped climbers off the wall. → preserve climbs while the goal is still above the zombie.
5. **Horizontal tolerance freeze:** the island lookup searched 2 m *horizontally* around the goal, so NavMesh on a *neighboring structure* at similar height (terrain beside a parked car) faked "connected". → tight flat tolerance (1.25 m) for supported goals. *Same bug class as round 2, other axis: every tolerance in a reachability verdict is an attack surface.*
6. **Standing-on-own-path freeze:** `937b6d8`'s 3D corner advancement (correct for switchbacks) means a path corner ~1 m *below* a zombie standing on a prop never advances, while the flat steering check reads "arrived" → look-input forever. Predates V5; the goal being connected kept every V5 mechanism out of the way. → off-mesh corner step-off.
7. **Timer sabotage:** the step-off/sidestep no-progress tracker was reset by `ClearExplicitGoalRoute` — which the 1 s attack-retarget calls — so any stall timer ≥ 1 s could never fire while a target stayed sensed. → tracker is never reset by goal/state changes (displacement is goal-agnostic); it self-heals staleness instead. **Any timer reset by the retarget path is effectively capped at 1 s.**
8. **Hollow-collider probe kill:** the round-2-era overhang up-cast ran *before* the plain from-top landing probe. On hollow-shelled cars (thin wall/roof colliders around an open interior) the inset probe base sits in the interior; the up-cast hits the car's own roof from below and redirected the landing probe underneath it — silently disabling *every* climb on such props (solid-collider props were unaffected, masking the bug). → from-top probe first, overhang up-cast only as fallback. **Collider topology (solid vs hollow shell) is a hidden test dimension for all probe logic.**

**Retired Jul 15:** after round 8 the car-hood climb was *still* observed failing in playtests. Rather than a ninth inference patch, the user rebooted the design philosophy entirely (V6): stop *deciding* whether to climb from probes/verdicts, make climbing a *reaction to touching a wall*. V5's connectivity verdicts, landing probes, chase/pursuit/descend modes, and stall trackers were all deleted.

---

## V6 — Reactive wall climbing (Jul 15; uncommitted) — CURRENT

**The brief (user's design):** always move toward the target; walk the NavMesh while pathfinding can get closer, and when it cannot, walk straight at the goal and **climb whatever wall the body touches**. No manual components. No goal-progress checks. Slow/choppy/dumb is fine; never standing still. The one rule to enforce: never climb above the target's height. Authoring contract: walls have no hooks — always climbable to the top; NavMesh routes preferred over climbing.

**Model.** Two modes selected purely from existing navigator signals (zero reachability queries):
- **Path mode** (a usable steering corner exists): walk corners; never climb — except the near-goal *vault* (≤ 6 m from the goal, direct line blocked by a LOW obstacle: chest ray hits, vault-height ray misses → hop the car/fence).
- **Direct mode** (unreachable / no path / reached-short / frozen corner while off-mesh): walk flat at the goal; on wall contact (one 0.6 m chest ray; must be steep and facing) → committed climb with **one cap formula**: `capY = max(goal.y + 1.5, startFeet.y + 2.25)` — the roof rule and the vault allowance in a single expression. Climb ends by cap (fall + cooldown), crest (ray clears → one forward-down probe → mantle — the only landing probe left, taken from a spot the body physically reached), stall, or rise-scaled duration.
- **Jam reflex**: the sole never-still net — nonzero input with ~zero real velocity for 0.75 s → random sidestep 1–2.5 s. Raw physics, goal-agnostic, never reset by goal/state changes.
- Kept: terrain shortcut surface registry (still never-failed), climb execution/mantle, melee/targeting/hunting, investigation give-up (now deliberately ticking through climbs — only the capped yo-yo accumulates it), idle systems.

**Why this can't fail the old ways:** every misread signal degrades into *movement* (walk, climb, fall, sidestep) instead of a mode standing down into a freeze — the V2/V5 failure shape (verdict wrong → nothing owns the tick) is structurally impossible; there is no landing inference to be lied to by hollow colliders; the roof rule is pure arithmetic with no probe ordering to invert. Pressure-tested against all 13 catalogued failures before implementation; four amendments from that review are built in (below-cap trigger, give-up ticking through climbs, midpoint path-loss bridge, normal/facing gates on the touch ray).

**Accepted costs:** capped climbs at blank walls yo-yo (fall/shuffle/climb — visible motion, converges sideways, bounded pre-overtime by give-up); some comedic wall-hopping over courtyard walls within the navigator's 12 m unreachable radius; en-route obstacles farther than 6 m from the goal are pathed around, not vaulted.

**Runtime validation pending** as of Jul 15. Key checks: car (roof/behind/hood-cab), watchtower (pure path mode both directions), roof camper full ascent, interior floor-2 survivor capping at window height (THE roof rule), ramps not triggering the wall ray (normal gate), KCC lateral slide along walls, never-still audit, horde perf (path mode 0 rays, direct 1, climbing 1–2).

## Hard-Won Technical Facts (do not relearn)

Unity/NavMesh behaviors and tuning truths, independent of which model wins:

- **`NavMesh.CalculatePath` emits no corner on a straight slope.** A far corner can be far above the agent with nothing between them. Never infer "climbable wall here" from corner height — gate on steepness or explicit data.
- **The modern `NavMeshLink` component connects in `OnEnable`,** before runtime endpoints are set. Use `NavMesh.AddLink(NavMeshLinkData)` for runtime links.
- **A path query's `areaMask` filters off-mesh links by area** — the clean lever for "zombies climb, survivors don't" on one shared bake. (Unused today, but proven.)
- **`NavMeshModifier`-removed geometry is invisible to every NavMesh query.** Cars/crates cut out for RTS pathing can never be reached, sampled, or linked through the NavMesh. Anything touching them needs physics or registered data.
- **Climb completion must be progress-based, not a fixed timer** — and the absolute cap must scale with the committed rise, or tall walls recreate the rise/drop oscillation at building scale.
- **Corner/boundary ledge tiles expose faces on more than one cardinal direction.** Generate one face per exposed side.
- **Mantle to a known landing point, not a raycast-found lip.**
- **Street-obstacle vaults need a goal-progress check** (landing on the goal's support object or flat-closer to the goal), or zombies vault the wrong car.
- **Raycasts never hit backfaces / colliders they start inside.** Every probe that can start inside geometry (insets, tall origins, goals inside characters) silently changes meaning with collider topology — hollow-shelled props behave differently from solid ones for the *same* code.
- **3D corner advancement + flat arrival checks disagree** when the agent stands above its own path; each check alone is correct, together they deadlock.
- **`SamplePosition`/island lookups have both vertical AND horizontal slop.** A reachability verdict is only as strict as its loosest axis; neighboring structures' mesh within the search radius fakes connectivity.
- **Sensor `LastKnownPosition` is feet-level** (`transform.position`), and support probes from `goal + 0.35` start inside the target's capsule — character colliders must be skipped in all such rays.
- **Any state cleared by `ClearExplicitGoalRoute` is effectively reset every `AttackRetargetInterval` (1 s)** while a target stays sensed, because the retarget re-fires `StartAttack` for the same enemy.

## Recurring failure patterns (the meta-lessons)

Looking across all six versions, the same five failures keep recurring in new clothes:

1. **Runtime geometric inference against arbitrary prop geometry eventually lies.** V1's route ray, V1.x's roof probe, V5's landing probes and connectivity tolerances — every raycast/sample-based *decision* mechanism has produced at least one silent, geometry-dependent failure. The two mechanisms that have **never** broken are the two that consume *generated, deterministic data*: plain NavMesh walking and the terrain climb-surface registry (V2→V4→V5, unchanged concept). Data beats inference here, every time.
2. **The street props are the boundary of every abstraction.** Cut from the NavMesh (for good RTS reasons), too numerous to hand-author, hollow-collidered, parked against arbitrary neighbors — every version's headline failure involved a car. Any V6 should confront this head-on rather than inferring around it. The obvious candidate, given pattern 1: **extend the registry model to props** — auto-register climb faces + top landings from prop bounds at spawn time (exactly what `ZombieClimbableSurface` computes, minus the hand-authoring — world generation spawns these prefabs and could register them wholesale), making prop climbs as deterministic as terrain ledges. Nobody has tried this yet; V4 rejected per-prop *hand*-authoring, which is a different thing.
3. **Blind direct movement toward a goal is a freeze waiting for the right geometry.** Every "walk straight at it" recovery eventually pressed a body into a wall/cab/railing. Direct movement must always be displacement-checked with a randomized escape.
4. **Mode shadowing:** with several movement modes in one decision chain, an always-true early return silently disables everything below it (V5 round 1; V4's road gate hid the same class by accident). Every new gate needs an explicit answer to "what does this steal ticks from?"
5. **Patches accumulate until the version is unexplainable, then gets rewritten.** V1 → V1.x roof checks; V4 → tolerance-tuned rescue rules; V5 → eight rounds of gates and trackers. The moment fixes start needing fixes, the model is wrong at a level no parameter can reach — that recognition triggered V2→V3, V3→V4, V4→V5.

## Current file map (V5)

- `Assets/Scripts/Zombies/ZombieAI.cs` — states, routing (pursuit/chase/descend/navigator), probes, trackers, `LogTraversalDebug`
- `Assets/Scripts/Zombies/ZombieClimbSurfaces.cs` — terrain climb-surface registry + spatial grid (shortcut faces only)
- `Assets/Scripts/Zombies/ZombieClimbableSurface.cs` — optional authored shortcut faces (Rescue usage inert)
- `Assets/Scripts/AI/Navigation/NavMeshIslandMap.cs` — precomputed connectivity (see `Docs/NavMeshIslandMap.md`)
- `Assets/Scripts/AI/Navigation/CharacterNavigator.cs` — shared pathing (3D corner advancement, midpoint chain, unreachable flagging)
- Operational reference: `Docs/ZombieAI.md`
