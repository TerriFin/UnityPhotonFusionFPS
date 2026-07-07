# NavMesh Island Map (Cleanup Step 3 — the zombie movement "silver bullet")

## Purpose

Third step of the July 2026 cleanup pass. The zombie movement system (climbing, stuck recovery, unreachable-perch
handling) keeps all its edge-case behavior, but its most expensive primitive — *"is there a connected NavMesh
route between here and there?"* — was being answered over and over at runtime with rings of
`NavMesh.SamplePosition` probes plus `NavMesh.CalculatePath` calls. Those questions are **pure connectivity**
questions, and connectivity only changes when the NavMesh is rebaked. So it is now precomputed once per bake.

This is the "silver bullet" the design history was circling: NavMesh links failed as a *movement* model
(`Docs/ZombieAIDesignHistory.md`), but the *knowledge* the expensive probes were re-deriving — which patches of
NavMesh form one connected region — is static and computable in one pass. Zombies behave the same; the probes
just stopped costing anything.

## The core: `NavMeshIslandMap`

**File:** `Assets/Scripts/AI/Navigation/NavMeshIslandMap.cs` (static class)

Built lazily on first query, invalidated by the single place the NavMesh changes
(`BuildingPlacementGenerator.RebuildNavMesh` → `NavMeshIslandMap.Invalidate()` right after
`NavMeshSurface.BuildNavMesh()`).

Build pass (once per bake):

1. `NavMesh.CalculateTriangulation()` — every triangle of the baked mesh.
2. **Weld** vertices by quantized position — **anisotropic**: 0.05 m laterally, but ~0.4 m vertically
   (`VerticalWeldTolerance`, coarse Y buckets + neighbour-bucket probe with a real distance check). Recast
   connects bake-tile borders in the navmesh graph with a vertical tolerance around `walkableClimb`, so border
   vertices can disagree in height by several tenths of a metre while being one surface; a uniform tight weld
   fragmented the street into roughly one island per bake tile.
3. **Union-find** over welded vertices; each triangle unions its corners, so triangles sharing any welded vertex
   join one **island**. The street network becomes one huge island; every disconnected patch (car roof, crate
   top, isolated ledge) becomes its own small island. Per-island XZ-projected area is accumulated
   (`GetIslandArea`).
4. **Spatial grid** (8 m cells): each triangle is inserted into every cell its XZ AABB overlaps.
5. **T-junction merge.** Unity's tiled runtime bake can split an edge on one side of a tile border while the
   other side keeps it whole — the extra border vertex has no welded counterpart (it lies in the *middle* of the
   neighbouring triangle's edge), so welding alone leaves the two sides as separate islands. Every welded vertex
   is tested (via the grid) against nearby triangles' edges (≤ 0.1 m in XZ, ≤ `VerticalWeldTolerance` against
   the edge's interpolated height) and unioned with any triangle whose edge it lies on.
6. **Path-verified merge — the authoritative pass.** For every island still separate from the largest one, run
   one real `NavMesh.CalculatePath` from its representative triangle to the nearest triangle of the merged main
   component (expanding over a few passes so fragment chains merge as the component grows). A complete path
   proves connection regardless of geometry; a failed one is definitive disconnection, because the merged
   component is internally connected. Genuinely separate props cost only a tiny flood (the query starts on the
   small island's side). The geometric welds above exist only to keep the number of path probes small. This
   layered approach came out of a live regression: fragmented islands gave chasing zombies false "tiny island /
   no path" verdicts, which fed the stuck-recovery direct/random movement and made hordes ball up and jitter
   ~15 m from their targets instead of closing.

**Trust rules for negative verdicts.** "Different islands ⇒ no path" is trusted when **either side is a small
island** (`SmallIslandMaxArea`, 150 m² — the prop-perch case) **or both islands are `IsIslandVerified`** —
meaning the path-verified merge pass proved their separateness with a real path query. The verified case is what
makes street-vs-**rooftop** decisions free: inaccessible building roofs legitimately carry their own large
NavMesh islands on this map (84 of them on an 18×18 city), and without the verified flag every rooftop query
would fall back to an unreachable path flood. An *unverified* large-vs-large disconnect (possible residual
fragmentation) still falls back to a real NavMesh query. Independently: a zombie standing on any large island
can never be "stranded on a small elevated island" (verdict short-circuits to *not stuck* without the ring), and
a spawn marker on a large island is accepted without its ring check.

Known limitation of `IsIslandVerified`: verification proves disconnection from the **main component**, not
between two non-main islands. Two rooftops that are connected to each other (but not the street) yet failed to
weld geometrically would be falsely treated as mutually unreachable. Abutting same-height roofs bake as one
contiguous surface and weld normally, so this is theoretical; if roof-to-roof AI ever matters, add pairwise
verification.

Query (`TryGetIslandAt(position, horizontalTolerance, heightTolerance, out islandId)`): walk the few grid cells
around the position, find the closest triangle within the tolerances (closest-point-on-triangle in XZ,
barycentric-interpolated surface height for the vertical check — so overlapping levels like a ledge top above a
buried tunnel resolve to the correct floor), return its island id. Zero NavMesh API calls, zero allocation
(per-triangle query stamps dedupe multi-cell visits).

**The two rules every caller relies on:**

- **Different islands ⇒ no path exists, under any area mask.** Always safe as a fast negative.
- **Same island ⇒ a complete path exists — but only for agents whose mask covers all baked areas**
  (`NavMesh.AllAreas`). Restrictive-mask callers must treat equality as "unknown" and fall back. (Both zombies
  and survivors currently use `AreaMask: -1`, so in practice equality is decisive everywhere.)

Every converted call site keeps its original probing code as a fallback for when the map is unavailable (no
NavMesh baked yet).

## Converted call sites

### 1. `ZombieAI.IsOnSmallElevatedNavMeshIsland` — the audit's "~29 NavMesh queries" probe

Old: sample self, then 8 ring probes at `StuckSmallIslandRadius`; each surviving probe ran
`HasCompleteNavMeshPathToGoalHeight` = 1 + up to 25 samples **plus a `CalculatePath`** (which flood-searches the
whole island when disconnected — the most expensive kind of query).

New: sample self once (unchanged), look up the island under the zombie, then do the same 8-point ring as **island
id comparisons** — "does connected same-height NavMesh exist at ring distance" is exactly "does any ring point
resolve to my island". Identical semantics (corridor/walkway cases included, because the ring logic is preserved
rather than replaced by an area threshold), ~27 NavMesh queries per probe → 0.

### 2. `ZombieAI.HasCompleteNavMeshPathToGoalHeight` — close-combat / stuck-target guard

Only ever used as a boolean. For a full area mask, "complete path exists to NavMesh near the goal at that
height" is exactly "goal-adjacent NavMesh shares my island". The up-to-25-sample goal ring search collapses into
one height-aware grid lookup and the `CalculatePath` disappears. Restrictive masks fall through to the original
code.

### 3. `CharacterNavigator.CalculatePath` — skip the doomed unreachable flood

When a sampled destination lies on a **different** island (survivor on a car roof — the classic case), the direct
`NavMesh.CalculatePath` could never return complete; it would flood the pursuer's entire island before giving up,
*on every repath, for every pursuer*. The navigator now skips that call and falls directly into its existing
unreachable / midpoint-chain handling, which is exactly where the failed call would have landed anyway.
Deliberately **not** applied to the follow policy: followers want the partial path's corners
(`TryAcceptPartialPathToward`), so follow orders still run `CalculatePath`. `TryFindReachablePoint` got the same
fast negative inside its widening loop.

Observable navigator behavior (unreachable flagging, midpoint chaining, follow parking) is unchanged — see the
note added to `Docs/PathfindingSystem.md`.

### 4. Spawn-marker validation (`ZombieOrchestrator` + `NeutralSurvivorOrchestrator`)

`IsConnectedNavMeshLargeEnough` ran 12 ring probes × (sample + `CalculatePath`) per marker at collection time —
thousands of queries in one frame at match start / match-start reroll on a large map. Same ring, now island-id
comparisons. The `MinimumSpawnConnectedNavMeshRadius` semantics are preserved.

## Cost accounting

- Build: one `CalculateTriangulation` + welding + union-find + grid fill per NavMesh bake (a few ms on a large
  map — the bake itself dwarfs it). One `Debug.Log` reports triangle/island counts.
- Queries: dictionary lookups + a handful of triangle distance tests; no allocation.
- Worst-case runtime scenario before this change (overtime horde pressed against a wall-hugging or car-perched
  survivor): every zombie paying the throttled island probe (~27 queries each time) plus an unreachable path
  flood ~2.5×/second. After: the probes are grid lookups and the floods are skipped entirely; the only remaining
  per-repath NavMesh work for a disconnected target is the midpoint chain that produces actual approach movement.

## What deliberately did NOT change

- All movement *decisions*: climb triggers, road-direct chase, rescue surfaces, stuck recovery wander, melee
  hold, mantles. Tunables all keep their meaning; no new knobs were added.
- `IsElevatedOffNavMesh` (one sample) and `IsOnSmallPropSupport` (one raycast) — already cheap, and they answer
  physical questions ("am I above the mesh", "am I on a small collider top") the island map cannot.
- The follow policy and midpoint chain — they produce *paths*, not booleans, so they still use `CalculatePath`.
- `ZombieClimbSurfaces` shortcut evaluation — it compares actual path lengths, which needs a real path.

## Known approximations / edge cases

- **Weld epsilon (0.05 m) + shared-vertex union + T-junction merge (0.1 m)**: two islands whose meshes touch
  within those tolerances merge into one. Physically a zombie can step across a 10 cm gap, so this is harmless
  in practice (the nearest genuinely-disconnected surfaces — crate/car tops — sit a metre or more above the
  street, far outside the tolerance).
- **Point-touching triangles** union just like edge-sharing ones. NavMesh geometry rarely produces
  corner-touching-only regions; if one shows up, the failure mode is "zombie thinks it can path and the real
  `CalculatePath` still fails", which lands in the existing unreachable handling.
- **Residual fragmentation** (exotic geometry the T-junction pass misses) is contained by the small-island rail:
  false negatives between two large fragments degrade to the pre-island-map code path instead of breaking
  behavior. The build log flags it (see below).
- **Map unavailable** (queried before the first bake): every caller falls back to its original probing code, so
  behavior degrades to exactly the pre-change system rather than breaking.
- With Enter-Play-Mode domain reload disabled, a `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` reset
  clears stale state between editor play sessions.

## Verification

- `dotnet build Assembly-CSharp.csproj` — clean.
- Behavioral equivalence argued per call site above (ring semantics preserved 1:1; navigator fast path lands in
  the same branches the failed query would have). Runtime validation checklist for the next play session:
  1. Zombies still recover off car roofs / crate tops (island probe correctness).
  2. Zombies still climb terrain ledges as shortcuts and rescue-climb to perched survivors.
  3. Followers still park at the foot of a prop when the leader stands on a car, and resume when they jump down.
  4. Zombie/neutral spawn markers still populate the same locations (spawn validation equivalence — the
     collection log prints how many markers were filtered; compare against a pre-change run).
  5. Watch the `NavMeshIslandMap: built N island(s) … main island X m², Y other large …` log after generation.
     Healthy for this map is **one dominant main island** (the street network, ~30k m² on an 18×18 city) plus
     one large island per inaccessible building rooftop and many small prop/ledge islands, ending with
     "All separate islands are path-verified as genuinely disconnected." A non-zero *unverified* count is the
     only warning sign — those islands' large-vs-large negatives degrade to real path queries.
