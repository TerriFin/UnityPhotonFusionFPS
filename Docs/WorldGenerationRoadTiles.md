# World Generation Road Tiles

## Context

Road tiles are the prefabs the road grid (`Docs/WorldGenerationRoadGrid.md`) places on road cells. The road tiles show a city after the apocalypse: dark asphalt with a worn white centre line, sidewalks with curbs, street furniture (lamp posts, trees, benches, bus shelters, planters, signs, traffic lights), a little rubbish, and wrecked or abandoned vehicles. Each tile tells one small story of how traffic ended (a pile-up, a truck that rolled in a curve, a T-bone in a junction), and every shape also has a quiet tile where nothing much happened.

## Art style

The tiles follow the generated buildings' style (`Docs/BuildingGenerator.md`, `GeneratedBuildingImporter`), so streets and buildings read as one world:

- **Boxes only.** Every prop, car and piece of furniture is built from boxes, like the buildings' furniture and details (trees are cubes there). No cylinders, wedges or rounded shapes: wheels, hydrants, barrels, cones and poles are boxes or stacks of boxes. Boxes are normally turned only about the vertical; tilts are kept for things that have fallen or been knocked over (the rolled truck, the flattened lamp post, open doors and hoods, a leaning pallet).
- **Flat matte colour.** One smoothness for everything, 0.18, the buildings' value. Colours are mid-valued and from the buildings' palette family (brick red, cream, teal, mustard, muted blue and green, wood brown, the buildings' dark `#33383B` for windows).
- **The sidewalk is the lot surface.** Sidewalks are `#A4A4A4`, the colour of the buildings' asphalt lot, so a tile and a lot beside it meet without a visible change. The road asphalt (`#4A4D50`) is darker than the old hand-made roads (`#6C6C6C`).
- **Chunky detail, not fine detail.** No hairline decals (cracks, glass shards, paper specks, sidewalk seams). Stains, patches, potholes and manholes are flat rectangles, as the buildings draw oil stains. Rubbish is what the buildings' back alleys use: flattened cardboard, loose planks, cartons, rubbish bags, in small clusters with gaps between them.

The tiles are generated from code, not modelled by hand. The designs live in C# and an editor builder turns them into prefabs, so a design change is a code change and every tile follows the same rules.

Status: the full set, about as many tiles as the old hand-made set: 9 straights, 8 curves, 5 T intersections, 4 four-ways, 4 dead ends, 2 boundary exits and 1 ramp. Every hand-made road tile has been replaced in the tile set.

## Files

```text
Assets/Editor/RoadTiles/RoadKit.cs          geometry kit, palette, car archetypes, street props (no UnityEngine)
Assets/Editor/RoadTiles/RoadTileDesigns.cs  the tile designs (no UnityEngine)
Assets/Editor/RoadTiles/RoadTileBuilder.cs  Unity: builds prefabs, meshes, palette material; updates the tile set
Assets/Prefabs/WorldGeneration/Roads/Generated/   output (prefabs, "<name> Meshes.asset", RoadPalette.asset/.mat, BuildVersion.txt)
```

`RoadKit.cs` and `RoadTileDesigns.cs` deliberately do not reference UnityEngine, so the same designs can be compiled outside Unity (e.g. into a WPF renderer) to preview and check them.

## Building

`RoadTileBuilder` is `[InitializeOnLoad]`: after a script reload it compares `RoadTileDesigns.Version` with `Generated/BuildVersion.txt` and rebuilds everything when they differ. **Bump `Version` whenever a design or the kit changes.** It can also be run from `SimpleFPS > World Generation > Rebuild Road Tiles`.

For each design (`TileModel`) the builder writes `Generated/<Name>.prefab`:

- root: `RoadTileDefinition` with the design's sockets (`Road`/`Closed`; `Exit` on the north side of a boundary tile), `IsBoundaryTile`, `IsHeightChangeRamp`, `Weight` and `RepeatCooldown`;
- `Ground`: asphalt, curbs and sidewalks as one mesh with a non-convex `MeshCollider`;
- `Props`: everything else (cars, furniture, markings, litter) as one render-only mesh;
- `Colliders`: one `BoxCollider` per solid prop (each car body, lamp post, tree trunk, sign, traffic light, hydrant, bin, bench, shelter panel, crate, barrier, wall…). Litter, bags, markings, tree canopies and open doors have none;
- `SpawnPoint` (boundary exits only): a child with the gameplay `SpawnPoint` component, which `Gameplay` uses to place player teams, as the old boundary prefabs had.

All objects are on the Default layer, so the `NavMeshSurface` (Default-layer physics colliders) bakes the walkable space around the wrecks.

Both meshes use one material, `RoadPalette.mat` (URP Lit, `_Smoothness` = `RoadPalette.Smoothness` 0.18, metallic 0). Its base map `RoadPalette.asset` is a 1-pixel-high strip with one texel per `RoadPalette` colour (point filtered, no mipmaps). Every vertex's UV points at its colour's texel. A tile is therefore two draw calls however many colours it uses.

### Tile set

The builder puts the generated tiles into `Assets/Prefabs/WorldGeneration/RoadTiles.asset` (used by every road generation preset). A generated tile **replaces the hand-made tiles of the same shape** in the set: the shape is the socket pattern in any rotation plus environment, boundary and ramp flags. Every shape now has generated tiles, so the hand-made straights, curves, T and 4-way intersections, dead ends, boundary exits (`RoadBoundary`) and the ramp (`LedgeRoad`) have all left the set. The old prefabs stay on disk. The builder logs which tiles it replaced.

Quiet tiles (one per shape: `RoadStraight_Quiet`, `RoadCurve_Quiet`, `RoadT_Quiet`, `RoadFourWay_Quiet`, `RoadEnd_Quiet`) have `Weight` 2, the rest 1, so open streets come up more often and the city doesn't read as wall-to-wall wrecks. `RepeatCooldown` is 1 for all.

## Shared layout (every tile)

Tiles are 15 x 15, centred on the origin, x east, z north (Unity). Traffic drives on the right.

| Part | Where | Height |
|---|---|---|
| Asphalt `#4A4D50` | 9 wide: x -4.5..4.5 for a north-south road | top y -0.1, slab down to -0.2 |
| Curb `#B8B7B2` | 0.3 wide beside the asphalt (4.5..4.8) | top y 0 |
| Sidewalk `#A4A4A4` | curb to tile edge (4.8..7.5) | top y 0 |
| Centre line | dashed white, 0.14 wide, 2.5 dashes every 5 centred on 0 and ±5, so 1.25 short of each edge | decal |

These match the old prefabs' road width, sidewalk width and heights, so new and old tiles can sit side by side.

- Curve (sockets south and west): the road is a quarter ring around the tile's south-west corner (-7.5, -7.5) between radii 3 and 12, with curbs at 2.7..3 and 12..12.3, a quarter-disc sidewalk inside and sidewalk to the tile edge outside. The centre line runs at radius 7.5 (three 2.2 dashes, 1.25 short of each edge).
- T intersection (sockets east, south and west): a sidewalk along the north edge; the two south corners are quarter discs of radius 3 (curb 2.7..3) centred on the tile corners, so the curbs round the corners; crosswalks across each arm (0.45 stripes, 0.9 apart, 1.8 long, centred 6 from the middle), stop lines, a short worn centre line.
- 4-way: the same rounded corners at all four tile corners, crosswalks across every arm, stop lines.
- Dead end (socket south): the road ends at z 3 in a curb and a full-width sidewalk to the north edge.
- Boundary exit (socket south `Road`, north `Exit`): a straight whose north part is walled off across the full 15 width, at least 4.9 high so nobody climbs out (shipping containers stacked two high, or concrete wall panels). Each has one `SpawnPoint` 0.25 above the road facing south, with about 3 m clear round it for the team (`SpawnClusterRadius` 1.5).
- Ramp (`IsHeightChangeRamp`, sockets north and south): matches the old `LedgeRoad`. The road climbs 7.5 (one height level) from the south edge (low, y -3.75) to the north edge (high, y +3.75), pivot halfway, as `RoadGridGenerator` places ramps; the slope is 26.6°. Road, curbs and sidewalks are slabs whose sides fill the wedge down to the low end; a 0.4 wide, 1.0 high concrete parapet runs along each outer edge (the cells beside a ramp are cliffs), with a pitched box collider. Markings and props on the slope use frames pitched to it; litter is never placed on the slope.
- Decals lie flat just above the surface, at fixed lifts so they never flicker: patches 0.008, oil 0.012, markings 0.016, pothole/manhole centres 0.02, cloth 0.03.

## Cars

Every vehicle is built from a `CarType` archetype, so cars are consistent across the game. Sizes are real-world, set against the survivors (1.8 tall, `JumpForce` 6.5 with `UpGravity` 18 on the prefabs: feet rise about 1.17, `StepHeight` 0.5). The old hand-made box car stood 1.4 above the road and could be jumped onto; hatchbacks and sedans match it:

| Type | Length | Width | Hood/trunk (belt) | Roof | Climbable |
|---|---|---|---|---|---|
| Hatchback | 4.1 | 1.8 | 0.79 | 1.39 | jump straight onto the roof |
| Sedan | 4.8 | 1.85 | 0.77 | 1.34 | jump straight onto the roof |
| Wagon | 4.9 | 1.9 | 0.88 | 1.53 | via the hood |
| Van | 5.3 | 2.0 | 1.11 | 2.03 | onto the hood, then the roof |
| Pickup | 5.5 | 2.0 | 0.98 (bed walls 1.40) | 1.68 | via hood or bed |
| Box truck | 7.4 | 2.4 | 1.35 (cab) | 3.35 cargo box | no |
| Bus | 10.5 | 2.5 | 1.25 | 2.8 | no |

Heights are above the road surface; wheel radius 0.31..0.5, ground clearance 0.6 of the wheel radius.

Construction, all boxes: a lower body box; a painted cabin box set back from the hood (and trunk on sedans) with dark window panels just proud of its sides, front and back (`Cabin`); square tyres with a dark hub; dark bumpers; headlights and taillights. The bus is one long body with a window band split by pillars, a small roof unit and two doors on the kerb side. A `CarLook` says how this car ended up: paint colour, a `Stripe` of colour along both sides (ambulance, bus, works van), the truck's `Cargo` box colour (army trucks get canvas), cracked glass (lighter grey panels), burnt (rust body, dark cabin and windows, sitting on rusted hubs, no lights), `Stripped` (wheels gone, body on the ground), crushed front/rear (shortened, broken headlights), a red and blue light bar (`Police`, also used on the ambulance), and open parts: doors (`FL`, `FR`, `RL`, `RR`, hinged at the front edge, flung 50..70°; on a bus `FR` opens its kerb-side doors), `Trunk`, `Hood`, `Rear` (van/truck rear doors, pickup tailgate). Colliders follow the silhouette so cars can be climbed: a body box up to the belt line over the full length, plus a cabin box (van: the cab and cargo body; truck: the cab and the cargo box; pickup: plus the bed walls and tailgate; bus: one box). A car on its roof is the same car in a frame rolled 180° and raised by `CarType.Top`.

Paints, from the buildings' palette family: red, blue, beige, white, green, grey, mustard, teal, and army green.

## Street props

All boxes (see Art style). Lamp post (square post 6.4 high, arm 1.6 over the road), street tree (square trunk, canopy of cubes 2.2 up so people walk under it, in a soil grate; can be laid over as a fallen tree with a crown collider), concrete planter with shrubs, bollard, bench, bus shelter (dark glass back and side panel, roof, bench, the stop sign on its own pole), hydrant (stacked boxes), signs as flat plates on a square pole (stop, yield, speed limit, curve warning, dead end, no entry, road works), dead traffic light, newspaper box, rubbish bags (upright or lying) and bag piles, wheelie bin (upright or tipped), crate, pallet, traffic cone (stepped), concrete road barrier (2 long, 0.82 high, stepped, red/white band), oil drum or burnt-out fire barrel, suitcase burst open with clothes, loose tyre, concrete debris, mattress, sofa, shopping cart, stretcher, portable toilet, site generator, dirt pile, tarp on four poles (2.05 high, walkable under), sandbag wall, shipping container (6 m, ribbed), tall concrete wall panels (4.9 high). Surface wear: potholes, asphalt patches, oil stains, scorch marks, skid marks, manholes, all flat rectangles. Litter, in the buildings' back-alley style: flattened cardboard (most of it), loose planks, cartons and the odd bag, three to a cluster, placed only on level ground, away from curbs and out of footprints already taken.

Kept sparse so the street reads as abandoned rather than as a landfill: 2 pieces of litter on quiet tiles, 3..5 on the others, and a bag pile of 2..3 on some tiles. Hydrants are on about one tile in five, not on every one; furniture varies from tile to tile (benches, trees, planters, bus shelters, bins, newspaper boxes).

Prop positions in designs are hand-placed; scatter (litter, debris, bag piles, stain shapes) comes from a seeded `System.Random` per tile, so a build is always identical.

## Design rules

- Follow the Art style section: boxes, palette colours, no fine decals.
- Nothing leaves the tile: the kit records any prop vertex outside ±7.5 as a problem; the builder logs it as a warning.
- Every open side must stay walkable to every other open side for a navigation agent (radius 0.35): wrecks may block lanes, but there is always a way through, between cars or along a sidewalk.
- Colliders of different objects should not overlap by more than a few centimetres: crashes are placed touching, not interpenetrating.
- Stories, not scatter: vehicles are placed as the result of an event (rear-end chain, swerve onto the curb, rollover in a curve, T-bone in a junction, abandoned with doors open, burnt out) with matching oil, skid marks and debris.
- Variety: every shape has one quiet tile (empty or almost empty, `Weight` 2), and the others differ in story, vehicles and furniture, so neighbouring tiles don't repeat props.
- Boundary exits: the wall must be continuous across the whole tile and at least 4.9 high with nothing climbable in front of it, and the spawn area must stay clear.

## Current tiles

| Prefab | Shape | Story |
|---|---|---|
| `RoadStraight_Pileup` | straight N-S | Northbound rear-end pile-up (hatchback into sedan, doors and trunk open); a van swerved onto the east sidewalk and bent a lamp post; a burnt-out wagon in the southbound lane with a suitcase burst open. |
| `RoadStraight_Quiet` | straight | Quiet: one hatchback parked at the curb, trees, lamps, a bench. |
| `RoadStraight_BusStop` | straight | A bus stopped at the shelter on the east sidewalk, kerb-side doors open; luggage on the sidewalk; a hydrant. |
| `RoadStraight_Parked` | straight | Almost quiet: cars still parked along both curbs (one door open), trees, a bin. |
| `RoadStraight_Roadworks` | straight | An open trench in the southbound lane fenced with barriers, the spoil heap, cones, the crew's van (orange stripe, rear doors open), generator, portable toilet, a road works sign. |
| `RoadStraight_Ambulance` | straight | An ambulance with rear doors open, its stretcher and bags in the street; a police car pulled up askew in the other lane, doors open. |
| `RoadStraight_Checkpoint` | straight | An army checkpoint: barriers as a chicane, a sandbag post with crates on the west sidewalk, the army pickup at the east curb, a fire barrel. |
| `RoadStraight_Camp` | straight | Evacuees' camp on the east sidewalk: tarps on poles over mattresses, crates, a shopping cart, a fire barrel; a wagon loaded to the roof. |
| `RoadStraight_FallenTree` | straight | A street tree uprooted across the northbound lane (root plate on the sidewalk), branches and leaves about; the hatchback that stopped at it; a hydrant. |
| `RoadCurve_Rollover` | curve S-W | A box truck rolled onto its side against the outer curb, flattened a lamp post along the sidewalk and spilled crates; a green hatchback abandoned with both front doors open; cones, diesel. |
| `RoadCurve_Quiet` | curve | Quiet: trees and lamps round the outside, a bench, a planter. |
| `RoadCurve_Spinout` | curve | A sedan left the curve over the outer curb into a lamp post (bent), skid marks behind it; a hatchback pulled up against the inner curb. |
| `RoadCurve_Delivery` | curve | A delivery van half on the outer sidewalk, rear doors open; boxes on a pallet and dropped in the road; a hydrant, a bench. |
| `RoadCurve_BusStop` | curve | Almost quiet: a bus shelter on the outer sidewalk, one sedan in the inner lane. |
| `RoadCurve_Detour` | curve | The outer lane closed with a curved line of barriers and cones, a police car parked behind it; a hatchback abandoned in the inner lane; a road works sign. |
| `RoadCurve_HeadOn` | curve | Two cars met head-on across the centre line and burned out; scorch marks, debris. |
| `RoadCurve_Queue` | curve | Three cars queued round the outer lane, abandoned with doors open; a suitcase dropped in the inner lane. |
| `RoadT_Gridlock` | T E-S-W | A pickup turning left out of the side street was T-boned by a taxi and shoved into a van on the west arm; the car queued behind it abandoned; a burnt sedan at the east stop line; crosswalks, dead traffic lights, a stop sign. |
| `RoadT_Quiet` | T | Quiet: one sedan parked at the north curb, traffic lights, a bench, a planter, a tree. |
| `RoadT_BusStop` | T | A bus at the shelter on the north sidewalk, doors open; a sedan stopped after turning out of the side street; a hydrant. |
| `RoadT_Checkpoint` | T | The side street sealed with barriers (a gap on foot) and sandbags, a no-entry sign; the army pickup on the through road, crates, a fire barrel. |
| `RoadT_Flipped` | T | A van ran the light and flipped a hatchback onto its roof; the traffic light it took down lies across the west crosswalk; a hydrant. |
| `RoadFourWay_Roadblock` | 4-way | An evacuation roadblock: concrete barriers close the north and east arms (a gap on foot in the north line), a police car parked behind them with its doors open, a red sedan that rammed the east barriers and shoved one askew, cars abandoned in the queues on the south and west arms, a cold fire barrel. |
| `RoadFourWay_Quiet` | 4-way | Quiet: lights, lamps, a planter, a knocked-over cone. |
| `RoadFourWay_BusCrash` | 4-way | A bus diagonally across the junction with a sedan driven into its side; passengers' luggage strewn about. There is a way round both ends of the bus. |
| `RoadFourWay_AidPoint` | 4-way | An aid point: an army truck backed in with its rear open, crates and boxes on pallets, cots under a canvas awning, a generator, barriers on the corners. |
| `RoadEnd_Barricade` | dead end, open to the south | Residents barricaded the end with a wagon broadside and a sedan nosed against it, pallets and a mattress propped on their south sides; the barricade was overrun (a pallet knocked flat). Behind it their camp on the sidewalk: a tarp, a sleeping bag, fire barrels, crates, a suitcase. A dead-end sign at the entrance. |
| `RoadEnd_Quiet` | dead end | Quiet: bollards along the end, a bench between planters, trees. |
| `RoadEnd_Dump` | dead end | Things dumped at the end of the street: a hatchback stripped of its wheels, a sofa, a mattress, tyres, bags, bins. |
| `RoadEnd_Parking` | dead end | Three cars parked nose-in in marked bays (the middle one burnt), a shopping cart of belongings, a suitcase, a hydrant. |
| `RoadBoundary_Quarantine` | boundary exit | Almost quiet: shipping containers stacked two high across the road, concrete panels over the sidewalks, two barriers, cones, a no-entry sign. Spawn at the middle. |
| `RoadBoundary_Exodus` | boundary exit | A concrete wall across the road; the lead car of the queue out of the city rammed it, the one behind was abandoned; luggage, sandbags, a no-entry sign. Spawn in the southbound lane (x -2.2, z -1.2). |
| `RoadRamp_Stalled` | ramp | Almost quiet: a hatchback stalled on the climb with its hood open, an oil trail down the slope, a cone, lamps. |

## Adding a tile

1. Add a method to `RoadTileDesigns` returning `k.Model`, built with a `RoadKit` (sockets, seed), and yield it from `All()`.
2. Build the shape's ground with its helper (`StraightGround` + `CentreLine`, `CurveGround`, `TGround`, `FourWayGround`, `DeadEndGround`; these also set `k.Surface`, the walking height), or for a new shape set `k.Surface` and use `GroundBox` / `GroundArc` / `GroundBeyondArc` / `GroundSlope`. Then wear, cars and props. Curve positions are easiest as `OnCurve(radius, angle)` / `CurveCar` / `OuterSidewalk`. Set `k.Model.Boundary`, `Ramp`, `Weight` and `SpawnPoints` where needed.
3. Bump `RoadTileDesigns.Version`. Unity rebuilds and updates the tile set on the next script reload.
