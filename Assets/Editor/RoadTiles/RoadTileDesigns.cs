using System;
using System.Collections.Generic;

// The road tile designs. Each is a 15x15 tile centred on the origin, built from RoadKit, telling one small story
// of how traffic ended when everything stopped. Traffic drives on the right. Layout shared by every tile so
// neighbours always meet: asphalt at y -0.1 between x -4.5..4.5 (a 9-wide road, two lanes), a 0.3 curb, sidewalk
// to the tile edge with its top at y 0, a dashed white centre line with 2.5 dashes every 5 (1.25 short of each edge).
namespace SimpleFPS.EditorTools.Roads
{
    public static class RoadTileDesigns
    {
        // Bump when a design changes, so the editor rebuilds the prefabs.
        public const int Version = 4;

        public static IEnumerable<TileModel> All()
        {
            yield return StraightPileup();
            yield return StraightQuiet();
            yield return StraightBusStop();
            yield return StraightParked();
            yield return StraightRoadworks();
            yield return StraightAmbulance();
            yield return StraightCheckpoint();
            yield return StraightCamp();
            yield return StraightFallenTree();

            yield return CurveRollover();
            yield return CurveQuiet();
            yield return CurveSpinout();
            yield return CurveDelivery();
            yield return CurveBusStop();
            yield return CurveDetour();
            yield return CurveHeadOn();
            yield return CurveQueue();

            yield return TGridlock();
            yield return TQuiet();
            yield return TBusStop();
            yield return TCheckpoint();
            yield return TFlipped();

            yield return FourWayRoadblock();
            yield return FourWayQuiet();
            yield return FourWayBusCrash();
            yield return FourWayAidPoint();

            yield return DeadEndBarricade();
            yield return DeadEndQuiet();
            yield return DeadEndDump();
            yield return DeadEndParking();

            yield return BoundaryQuarantine();
            yield return BoundaryExodus();

            yield return RampStalled();
        }

        const double Road = 4.5, Curb = 4.8, Y = RoadKit.RoadY, Walk = RoadKit.WalkY;

        // ------------------------------------------------------------------ shared ground of each shape

        // A north-south straight: asphalt between the curbs, sidewalks to both edges.
        static void StraightGround(RoadKit k)
        {
            k.Surface = (x, z) => Math.Abs(x) <= Road ? Y : Walk;
            k.GroundBox(-Road, Road, -7.5, 7.5, Y, "asphalt");
            foreach (double s in new[] { -1.0, 1.0 })
            {
                k.GroundBox(s < 0 ? -Curb : Road, s < 0 ? -Road : Curb, -7.5, 7.5, Walk, "curb");
                k.GroundBox(s < 0 ? -7.5 : Curb, s < 0 ? -Curb : 7.5, -7.5, 7.5, Walk, "sidewalk");
            }
        }

        // The straight's dashed centre line: dashes at z -5, 0 and 5 in the given paint; null leaves one worn away.
        static void CentreLine(RoadKit k, string south = "lineWhite", string middle = "lineWhite", string north = "lineWhite")
        {
            if (south != null) k.Stripe(0, -5, 2.5, .14, 0, south);
            if (middle != null) k.Stripe(0, 0, 2.5, .14, 0, middle);
            if (north != null) k.Stripe(0, 5, 2.5, .14, 0, north);
        }

        // The rounded sidewalk corner of a junction at tile corner (sx * 7.5, sz * 7.5): a quarter disc of radius 3
        // (curb 2.7..3) and asphalt filling the rest of the 3x3 corner square.
        static void CornerSidewalk(RoadKit k, double sx, double sz)
        {
            double cx = sx * 7.5, cz = sz * 7.5, a0 = Math.Atan2(-sz, -sx) * 180 / Math.PI - 45, a1 = a0 + 90;
            k.GroundBeyondArc(cx, cz, 3, a0, a1, sx > 0 ? Road : -7.5, sx > 0 ? 7.5 : -Road, sz > 0 ? Road : -7.5, sz > 0 ? 7.5 : -Road, Y, "asphalt", 12);
            k.GroundArc(cx, cz, 2.7, 3, a0, a1, Walk, "curb", 12);
            k.GroundArc(cx, cz, 0, 2.7, a0, a1, Walk, "sidewalk", 8);
        }

        static double CornerDistance(double x, double z) =>
            Math.Sqrt((7.5 - Math.Abs(x)) * (7.5 - Math.Abs(x)) + (7.5 - Math.Abs(z)) * (7.5 - Math.Abs(z)));

        // The curve (south to west): a quarter ring round the south-west corner between radii 3 and 12, its centre
        // line dashed at radius 7.5.
        const double CurveX = -7.5, CurveZ = -7.5;

        static void CurveGround(RoadKit k)
        {
            double R(double x, double z) => Math.Sqrt((x - CurveX) * (x - CurveX) + (z - CurveZ) * (z - CurveZ));
            k.Surface = (x, z) => R(x, z) >= 3 && R(x, z) <= 12 ? Y : Walk;
            k.GroundArc(CurveX, CurveZ, 3, 12, 0, 90, Y, "asphalt", 24);
            k.GroundArc(CurveX, CurveZ, 2.7, 3, 0, 90, Walk, "curb", 12);
            k.GroundArc(CurveX, CurveZ, 12, 12.3, 0, 90, Walk, "curb", 24);
            k.GroundArc(CurveX, CurveZ, 0, 2.7, 0, 90, Walk, "sidewalk", 8);
            k.GroundBeyondArc(CurveX, CurveZ, 12.3, 0, 90, -7.5, 7.5, -7.5, 7.5, Walk, "sidewalk", 24);
            // Three 2.2 dashes, leaving 1.25 at each end like the straight.
            foreach (double s in new[] { 2.35, 5.89, 9.43 })
            {
                double a = s / 7.5;
                k.Stripe(CurveX + 7.5 * Math.Cos(a), CurveZ + 7.5 * Math.Sin(a), 2.2, .14, -a * 180 / Math.PI, s > 9 ? "lineWorn" : "lineWhite");
            }
        }

        // A point on the curve at radius r and angle a (degrees, 0 along the south edge, 90 along the west edge).
        static (double x, double z) OnCurve(double r, double a) =>
            (CurveX + r * Math.Cos(a * Math.PI / 180), CurveZ + r * Math.Sin(a * Math.PI / 180));

        // Facing the road from the outer sidewalk, toward the curve's centre.
        static double FacingCentre(double a) => Math.Atan2(-Math.Cos(a * Math.PI / 180), -Math.Sin(a * Math.PI / 180)) * 180 / Math.PI;

        // A vehicle on the curve at radius r, angle a, facing along the road. Counter-clockwise traffic (from the
        // south arm to the west arm) keeps right, so it drives the outer lane (r 7.5..12); clockwise the inner (3..7.5).
        static Frame CurveCar(double r, double a, bool counterClockwise, double turn = 0)
        {
            var (x, z) = OnCurve(r, a);
            return Frame.At(x, Y, z, (counterClockwise ? -a : 180 - a) + turn);
        }

        static Frame OuterSidewalk(RoadKit k, double r, double a)
        {
            var (x, z) = OnCurve(r, a);
            return k.On(x, z, FacingCentre(a));
        }

        // The T (east, south, west): the through road east-west, a sidewalk along the north edge, rounded corners on
        // the side street; crosswalks across each arm, stop lines, a worn centre line along the through road.
        static void TGround(RoadKit k)
        {
            double Corner(double x, double z) => Math.Min(Math.Sqrt((x - 7.5) * (x - 7.5) + (z + 7.5) * (z + 7.5)), Math.Sqrt((x + 7.5) * (x + 7.5) + (z + 7.5) * (z + 7.5)));
            k.Surface = (x, z) => z > Road || Corner(x, z) < 3 ? Walk : Y;
            k.GroundBox(-7.5, 7.5, -Road, Road, Y, "asphalt");
            k.GroundBox(-Road, Road, -7.5, -Road, Y, "asphalt");
            k.GroundBox(-7.5, 7.5, Road, Curb, Walk, "curb");
            k.GroundBox(-7.5, 7.5, Curb, 7.5, Walk, "sidewalk");
            foreach (double s in new[] { -1.0, 1.0 }) CornerSidewalk(k, s, -1);

            for (double c = -3.9; c <= 3.95; c += .9)
            {
                k.Stripe(c, -6.0, 1.8, .45, 0, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
                k.Stripe(6.0, c, 1.8, .45, 90, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
                k.Stripe(-6.0, c, 1.8, .45, 90, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
            }
            k.Stripe(2.25, -4.85, 4.3, .3, 90);
            k.Stripe(4.85, 2.25, 4.3, .3, 0, "lineWorn");
            k.Stripe(-4.85, -2.25, 4.3, .3, 0);
            foreach (double x in new[] { -3.0, 0.0, 3.0 }) k.Stripe(x, 0, 1.6, .14, 90, x == 0 ? "lineWorn" : "lineWhite");
        }

        // The 4-way: rounded corners at all four tile corners, crosswalks across every arm, stop lines.
        static void FourWayGround(RoadKit k)
        {
            k.Surface = (x, z) => CornerDistance(x, z) < 3 ? Walk : Y;
            k.GroundBox(-7.5, 7.5, -Road, Road, Y, "asphalt");
            k.GroundBox(-Road, Road, Road, 7.5, Y, "asphalt");
            k.GroundBox(-Road, Road, -7.5, -Road, Y, "asphalt");
            foreach (double sx in new[] { -1.0, 1.0 })
                foreach (double sz in new[] { -1.0, 1.0 }) CornerSidewalk(k, sx, sz);

            for (double c = -3.9; c <= 3.95; c += .9)
            {
                k.Stripe(c, -6.0, 1.8, .45, 0, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
                k.Stripe(c, 6.0, 1.8, .45, 0, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
                k.Stripe(6.0, c, 1.8, .45, 90, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
                k.Stripe(-6.0, c, 1.8, .45, 90, k.Random.NextDouble() < .3 ? "lineWorn" : "lineWhite");
            }
            k.Stripe(2.25, -4.85, 4.3, .3, 90);
            k.Stripe(-2.25, 4.85, 4.3, .3, 90, "lineWorn");
            k.Stripe(4.85, 2.25, 4.3, .3, 0);
            k.Stripe(-4.85, -2.25, 4.3, .3, 0);
        }

        // The dead end (open to the south): the road ends 3 north of the middle in a curb and a full-width sidewalk.
        const double EndZ = 3.0;

        static void DeadEndGround(RoadKit k)
        {
            k.Surface = (x, z) => Math.Abs(x) <= Road && z <= EndZ ? Y : Walk;
            k.GroundBox(-Road, Road, -7.5, EndZ, Y, "asphalt");
            foreach (double s in new[] { -1.0, 1.0 })
            {
                k.GroundBox(s < 0 ? -Curb : Road, s < 0 ? -Road : Curb, -7.5, EndZ + .3, Walk, "curb");
                k.GroundBox(s < 0 ? -7.5 : Curb, s < 0 ? -Curb : 7.5, -7.5, 7.5, Walk, "sidewalk");
            }
            k.GroundBox(-Road, Road, EndZ, EndZ + .3, Walk, "curb");
            k.GroundBox(-Curb, Curb, EndZ + .3, 7.5, Walk, "sidewalk");
        }

        // ================================================================== straights: north-south

        // A rear-end pile-up in the northbound lane: a hatchback ran into a sedan, a van tried to squeeze past on the
        // sidewalk and bent a lamp post. In the other lane a burnt-out wagon, its owner's suitcase burst open nearby.
        static TileModel StraightPileup()
        {
            var k = new RoadKit("RoadStraight_Pileup", true, false, true, false, 1101);
            StraightGround(k);
            k.Stripe(0, -5, 2.5, .14, 0);
            k.Stripe(0, -.6, 1.3, .14, 0); k.Stripe(0, .95, .6, .14, 0, "lineWorn");
            k.Stripe(0, 5, 2.5, .14, 0, "lineWorn");

            // Surface wear first, so litter and cars sit over it.
            k.Decal(k.On(3.1, -6.3), RoadKit.PatchLift, "asphaltPatch", (-1.1, -.6), (1.0, -.7), (1.1, .65), (-1.0, .6));
            k.Pothole(-1.7, 6.2, .45);
            k.Manhole(-2.6, 2.4);
            // Skid marks where the van left the lane.
            k.Stripe(2.55, -6.2, 2.0, .22, 14, "oil");
            k.Stripe(3.55, -6.55, 1.5, .22, 16, "oil");

            k.Car("Sedan", Frame.At(2.35, Y, 3.9, 4), CarType.Sedan,
                new CarLook { Paint = "paintBlue", Open = new[] { "FL", "Trunk" }, RearCrush = .25, CrackedGlass = true });
            k.Car("Hatchback", Frame.At(2.0, Y, -.25, -7), CarType.Hatchback,
                new CarLook { Paint = "paintRed", Open = new[] { "FL", "FR", "Hood" }, FrontCrush = .35, CrackedGlass = true });
            // Van with its right wheels up on the curb, nose against the lamp post.
            k.Car("Van", Frame.At(3.75, Y + .05, -4.4, 22, 0, 2.5), CarType.Van, new CarLook { Paint = "paintWhite", CrackedGlass = true });
            k.LampPost(Frame.At(5.45, Walk, -1.5, -90, -12, -8));
            k.Debris(5.6, -2.0, .5, 5);

            k.Oil(-2.6, -3.0, 1.9);
            k.Car("Burnt wagon", Frame.At(-2.6, Y, -3.0, 168), CarType.Wagon, new CarLook { Burnt = true, Open = new[] { "RL" } });
            k.Suitcase(-.7, -2.4, 30);
            k.LooseTyre(k.On(-1.3, 5.4).Local(0, 0, 0, 20));

            k.LampPost(Frame.At(-5.2, Walk, 3.0, 90));
            k.Tree(k.On(-6.2, -5.0, 30));
            k.Sign(Frame.At(5.3, Walk, 5.4, 180), "Speed");
            k.NewsBox(Frame.At(6.95, Walk, 2.6, -90));
            k.BagPile(-6.7, -.8, 2, .45);
            k.WheelieBin(Frame.At(-6.3, Walk, 5.4, -60), true);
            k.Litter(2, (x, z) => Math.Abs(x + 5.8) < 1.6 && Math.Abs(z - 4.6) < 1.8);
            k.Litter(5);
            return k.Model;
        }

        // Nothing happened here: the street was simply left. One car still parked at the curb.
        static TileModel StraightQuiet()
        {
            var k = new RoadKit("RoadStraight_Quiet", true, false, true, false, 1201);
            k.Model.Weight = 2;
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWorn", "lineWhite");
            k.Manhole(-2.0, 2.6);
            k.Patch(2.4, -.8, 2.2, 1.4, 6, "asphaltPatch", RoadKit.PatchLift);
            k.Car("Hatchback", Frame.At(3.35, Y, -4.3, 0), CarType.Hatchback, new CarLook { Paint = "paintTeal" });
            k.LampPost(Frame.At(-5.2, Walk, -3.0, 90));
            k.LampPost(Frame.At(5.2, Walk, 4.5, -90));
            k.Tree(k.On(6.2, -1.5, 20));
            k.Tree(k.On(-6.2, 3.6, 10));
            k.Bench(k.On(-6.7, -.4, 90));
            k.Litter(2);
            return k.Model;
        }

        // The last bus: it pulled in at the shelter, opened its doors and was never driven again.
        static TileModel StraightBusStop()
        {
            var k = new RoadKit("RoadStraight_BusStop", true, false, true, false, 1303);
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWhite", "lineWorn");
            k.Pothole(-2.4, -4.6, .4);
            k.BusStop(k.On(5.25, 1.6, -90));
            k.Oil(3.1, -2.0, .8);
            k.Car("Bus", Frame.At(3.1, Y, -.2, 0), CarType.Bus, new CarLook { Paint = "paintWhite", Stripe = "paintBlue", Open = new[] { "FR" } });
            // The passengers' luggage, left on the sidewalk.
            k.Suitcase(6.2, -4.4, 15);
            k.Box(k.On(6.6, 4.4, 30), -.22, .22, 0, .55, -.14, .14, "suitcase");

            k.LampPost(Frame.At(-5.2, Walk, 2.0, 90));
            k.LampPost(Frame.At(5.2, Walk, -6.2, -90));
            k.Tree(k.On(-6.2, -3.8, 15));
            k.Hydrant(Frame.At(-5.3, Walk, 5.8, 90));
            k.NewsBox(Frame.At(6.95, Walk, 5.6, -90));
            k.Litter(4);
            return k.Model;
        }

        // A residential street where people simply stopped driving: cars still parked along both curbs; one owner got
        // as far as opening the door.
        static TileModel StraightParked()
        {
            var k = new RoadKit("RoadStraight_Parked", true, false, true, false, 1407);
            StraightGround(k);
            CentreLine(k);
            k.Manhole(1.2, -2.8);
            k.Car("Hatchback", Frame.At(3.4, Y, -4.7, 0), CarType.Hatchback, new CarLook { Paint = "paintRed" });
            k.Car("Sedan", Frame.At(3.38, Y, 1.0, 0), CarType.Sedan, new CarLook { Paint = "paintGrey", Open = new[] { "FL" } });
            k.Car("Wagon", Frame.At(-3.38, Y, 4.25, 180), CarType.Wagon, new CarLook { Paint = "paintBeige" });
            k.Car("Pickup", Frame.At(-3.36, Y, -2.6, 180), CarType.Pickup, new CarLook { Paint = "paintBlue" });

            k.Tree(k.On(6.2, 5.4, 10));
            k.Tree(k.On(-6.2, 1.0, 15));
            k.Tree(k.On(-6.2, -5.9, 25));
            k.LampPost(Frame.At(5.2, Walk, -1.6, -90));
            k.LampPost(Frame.At(-5.2, Walk, 6.0, 90));
            k.WheelieBin(Frame.At(-6.9, Walk, -3.4, 90), false);
            k.Litter(3);
            return k.Model;
        }

        // The road crew had dug up the southbound lane when everything stopped: the trench is still open behind its
        // barriers, the spoil heaped beside it, their van, generator and portable toilet left on site.
        static TileModel StraightRoadworks()
        {
            var k = new RoadKit("RoadStraight_Roadworks", true, false, true, false, 1511);
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWorn", "lineWhite");
            // The trench: a dark pit in a rim of dug earth.
            k.Patch(-2.4, -1.2, 3.0, 5.8, 0, "dirt", RoadKit.PatchLift);
            k.Patch(-2.4, -1.2, 2.2, 5.0, 0, "dark", RoadKit.CrackLift);
            k.Barrier(k.On(-.45, -3.25, 90));
            k.Barrier(k.On(-.45, -1.2, 90));
            k.Barrier(k.On(-.45, .85, 92));
            k.Barrier(k.On(-3.5, -4.75, 0));
            k.Barrier(k.On(-1.5, -4.75, 0));
            k.Barrier(k.On(-3.5, 2.25, 0));
            k.Barrier(k.On(-1.5, 2.25, 2));
            k.DirtPile(k.On(-2.4, 3.6, 15), .75);
            k.Cone(k.On(.35, -5.6));
            k.Cone(k.On(.4, 3.4));
            k.Cone(k.On(1.2, 5.2, 40).Local(0, .16, 0, 0, 0, 85));

            k.Car("Works van", Frame.At(3.25, Y, 3.4, 0), CarType.Van, new CarLook { Paint = "paintWhite", Stripe = "cone", Open = new[] { "Rear" } });
            k.Generator(k.On(2.5, -1.6, 10));
            k.PortableToilet(k.On(-6.6, -5.6, 90));
            k.Sign(Frame.At(5.3, Walk, -5.6, 180), "Works");
            k.Pallet(k.On(6.3, 5.7, 10));
            k.Crate(k.On(6.3, 5.7, 10).Local(0, .14, 0, 8), .6);
            k.LampPost(Frame.At(-5.2, Walk, 4.5, 90));
            k.LampPost(Frame.At(5.2, Walk, -1.5, -90));
            k.Litter(3);
            return k.Model;
        }

        // An ambulance answered a call here: rear doors open, the stretcher left in the street, a police car pulled up
        // askew in the other lane with both doors open.
        static TileModel StraightAmbulance()
        {
            var k = new RoadKit("RoadStraight_Ambulance", true, false, true, false, 1613);
            StraightGround(k);
            CentreLine(k, "lineWorn", "lineWhite", "lineWhite");
            k.Patch(-1.8, 5.0, 1.8, 1.2, 12, "asphaltPatch", RoadKit.PatchLift);
            k.Pothole(1.4, 6.3, .35);

            k.Car("Ambulance", Frame.At(2.95, Y, 2.3, 0), CarType.Van,
                new CarLook { Paint = "paintWhite", Stripe = "signRed", Police = true, Open = new[] { "Rear", "FL" } });
            k.Stretcher(k.On(2.6, -3.3, 12));
            k.Box(k.On(1.5, -2.0, 30), -.25, .25, 0, .28, -.17, .17, "signRed");
            k.Box(k.On(3.9, -4.7, -20), -.2, .2, 0, .3, -.2, .2, "paintWhite");
            k.Car("Police car", Frame.At(-2.2, Y, -3.4, 162), CarType.Sedan, new CarLook { Paint = "paintWhite", Police = true, Open = new[] { "FL", "FR" } });

            k.LampPost(Frame.At(-5.2, Walk, 1.0, 90));
            k.LampPost(Frame.At(5.2, Walk, 6.0, -90));
            k.Bench(k.On(-6.7, 4.6, 90));
            k.Tree(k.On(6.2, -4.0, 70));
            k.WheelieBin(Frame.At(6.6, Walk, 1.4, -100), true);
            k.Litter(3);
            return k.Model;
        }

        // An army checkpoint: barriers set out as a chicane, a sandbag post on the west sidewalk, the soldiers' pickup
        // at the east curb. They left in a hurry; their crates are still stacked in the post.
        static TileModel StraightCheckpoint()
        {
            var k = new RoadKit("RoadStraight_Checkpoint", true, false, true, false, 1717);
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWorn", null);
            k.Manhole(2.6, -4.6);

            k.Barrier(k.On(-3.45, 2.6, 0));
            k.Barrier(k.On(-1.45, 2.6, 0));
            k.Barrier(k.On(1.45, -1.6, 0));
            k.Barrier(k.On(3.45, -1.6, 0));
            k.Cone(k.On(-.05, 2.6));
            k.Cone(k.On(.15, -1.6));
            k.Car("Army pickup", Frame.At(3.25, Y, 4.65, 180), CarType.Pickup, new CarLook { Paint = "army", Open = new[] { "FL" } });

            // The post: a sandbag wall along the curb and a wing back to the tile edge.
            k.Sandbags(k.On(-5.15, -3.0, 90), -1.3, 1.3, 4);
            k.Sandbags(k.On(-6.4, -4.55, 0), -1.0, 1.0, 4);
            k.Crate(k.On(-6.6, -3.3, 10), .6);
            k.Crate(k.On(-6.5, -2.45, -5), .5);
            k.Box(k.On(-5.95, -2.0, 25), -.25, .25, 0, .25, -.15, .15, "army");
            k.Barrel(k.On(-6.6, 1.0, 0), true);

            k.Sign(Frame.At(5.3, Walk, -6.0, 180), "Stop");
            k.LampPost(Frame.At(5.2, Walk, .6, -90));
            k.LampPost(Frame.At(-5.2, Walk, 4.6, 90));
            k.Litter(3);
            return k.Model;
        }

        // Evacuees camped on the sidewalk waiting for buses that never came: tarps on poles over mattresses, a shopping
        // cart of belongings, a cold fire barrel, a station wagon loaded to the roof.
        static TileModel StraightCamp()
        {
            var k = new RoadKit("RoadStraight_Camp", true, false, true, false, 1819);
            StraightGround(k);
            CentreLine(k, null, "lineWorn", "lineWhite");
            k.Oil(-1.6, -4.2, .8);

            k.TarpShelter(k.On(6.15, -2.3), 2.3, 3.2, "tarp");
            k.Mattress(k.On(5.6, -2.3, 2));
            k.Mattress(k.On(6.6, -2.4, -3));
            k.TarpShelter(k.On(6.15, 3.3), 2.3, 2.6, "canvas");
            k.Crate(k.On(6.5, 3.7, 15), .6);
            k.Box(k.On(5.7, 2.8, -10), -.25, .25, 0, .5, -.15, .15, "suitcase");
            k.Barrel(k.On(5.6, .9), true);
            k.ShoppingCart(k.On(5.6, -5.4, 160));

            var wagon = Frame.At(3.3, Y, 1.5, 180);
            k.Car("Wagon", wagon, CarType.Wagon, new CarLook { Paint = "paintGreen" });
            var roof = wagon.Local(0, CarType.Wagon.Top, 0);
            k.Box(roof, -.5, .45, 0, .32, -.9, .3, "suitcase");
            k.Box(roof.Local(.1, .32, -.4, 20), -.3, .3, 0, .28, -.25, .25, "clothBlue");
            k.Box(roof.Local(-.1, 0, .75, -8), -.45, .45, 0, .18, -.3, .3, "mattress");

            k.LampPost(Frame.At(-5.2, Walk, -1.5, 90));
            k.Tree(k.On(-6.2, 4.2, 5));
            k.Bench(k.On(-6.7, 1.6, 90));
            k.BagPile(-6.5, -5.6, 2, .4);
            k.Litter(4);
            return k.Model;
        }

        // A street tree came down across the northbound lane and nobody came to clear it; its root plate tore up the
        // east sidewalk. The hatchback that met it was abandoned with its door open.
        static TileModel StraightFallenTree()
        {
            var k = new RoadKit("RoadStraight_FallenTree", true, false, true, false, 1921);
            StraightGround(k);
            CentreLine(k);
            k.Pothole(-2.2, -5.8, .4);

            var plate = k.On(6.3, 1.2, 8);
            k.Box(plate, -.15, .15, 0, 1.0, -.6, .6, "soil");
            k.Collider("Root plate", plate, -.15, .15, 0, 1.0, -.6, .6);
            k.Tree(Frame.At(6.05, .35, 1.2, 8, 0, 82), 1.35, fallen: true);
            k.Box(k.On(2.2, 3.4, 70), -.05, .05, 0, .08, -.7, .7, "bark");
            k.Box(k.On(1.6, -.6, 20), -.3, .3, 0, .35, -.3, .3, "foliageDark");
            k.Box(k.On(.4, 2.6, 50), -.25, .25, 0, .3, -.25, .25, "foliage");

            k.Car("Hatchback", Frame.At(2.3, Y, -4.2, -6), CarType.Hatchback, new CarLook { Paint = "paintMustard", Open = new[] { "FL" } });
            k.Hydrant(Frame.At(-5.3, Walk, 3.0, 90));
            k.LampPost(Frame.At(-5.2, Walk, -3.5, 90));
            k.LampPost(Frame.At(5.2, Walk, -4.8, -90));
            k.Tree(k.On(-6.2, 5.6, 30));
            k.NewsBox(Frame.At(-6.95, Walk, .2, 90));
            k.Litter(3);
            return k.Model;
        }

        // ================================================================== curves: south to west

        // A box truck took the corner too fast and rolled onto its side against the outer curb, flattening a lamp
        // post and throwing its cargo; the hatchback behind it was abandoned with its doors open.
        static TileModel CurveRollover()
        {
            var k = new RoadKit("RoadCurve_Rollover", false, false, true, true, 2203);
            CurveGround(k);

            k.Pothole(-2.9, -5.6, .4);
            k.Manhole(1.6, -5.9);
            k.Oil(-1.0, -1.4, 1.7);
            k.Oil(1.2, -3.6, .8);

            // The truck on its right side: its roof faces the outer curb.
            var road = Frame.At(-1.59, Y, -.45, -35);
            var truck = CarType.BoxTruck;
            k.Car("Box truck", road.Local(-1.68, truck.Width / 2, 0, 0, 0, -90), truck, new CarLook { Paint = "paintRed", FrontCrush = .2, CrackedGlass = true });
            k.Crate(k.On(1.6, -4.8, 25), .7);
            k.Crate(k.On(.3, -5.6, -10), .6);
            k.Crate(k.On(2.6, -3.3, 60).Local(0, .3, 0, 0, 0, 80), .6);
            k.Box(k.On(.9, -6.6, 15), -.25, .25, 0, .35, -.3, .3, "cardboard");
            k.Box(k.On(2.2, -6.0, -35), -.2, .2, 0, .3, -.2, .2, "cardboard");
            k.Debris(1.4, -5.0, 1.3, 6);

            k.Car("Hatchback", Frame.At(-4.63, Y, -3.4, 120), CarType.Hatchback, new CarLook { Paint = "paintGreen", Open = new[] { "FL", "FR" } });
            k.Cone(k.On(2.9, -5.4, 10));
            k.Cone(k.On(4.4, -6.2, 0).Local(0, .16, 0, 70, 0, 85));

            // Lamp posts round the outer sidewalk, arms toward the road; the one the truck hit lies along the sidewalk.
            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.LampPost(Frame.At(-1.69, Walk, 3.9, -153).Local(0, .16, 0, 0, 0, 90));
            k.Debris(-1.4, 4.1, .5, 4);
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.Bench(Frame.At(4.4, Walk, 6.6, -139));
            k.WheelieBin(Frame.At(6.6, Walk, 4.2, -100), false);
            k.BagPile(5.9, 5.9, 3, .5);
            k.Litter(5);
            return k.Model;
        }

        // The curve as it was left: trees and lamps round the outside, a bench, a planter, nothing else.
        static TileModel CurveQuiet()
        {
            var k = new RoadKit("RoadCurve_Quiet", false, false, true, true, 2307);
            k.Model.Weight = 2;
            CurveGround(k);
            var (mx, mz) = OnCurve(9.5, 30);
            k.Manhole(mx, mz);
            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            foreach (double a in new[] { 35.0, 60.0 }) k.Tree(OuterSidewalk(k, 14.2, a));
            k.Bench(OuterSidewalk(k, 15.2, 48));
            k.Planter(k.On(5.6, 5.6, -45), 1.6);
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.Litter(2);
            return k.Model;
        }

        // A sedan lost the curve, jumped the outer curb and wrapped its nose round a lamp post; the hatchback coming
        // the other way pulled up against the inner curb and its driver ran.
        static TileModel CurveSpinout()
        {
            var k = new RoadKit("RoadCurve_Spinout", false, false, true, true, 2411);
            CurveGround(k);
            k.Pothole(-1.6, -5.9, .4);
            // Skid marks curving out toward the lamp post.
            foreach (var (r, a, yaw) in new[] { (10.3, 20.0, -14.0), (11.4, 22.0, -14.0), (10.5, 28.0, -10.0), (11.6, 29.0, -10.0) })
            {
                var (x, z) = OnCurve(r, a);
                k.Stripe(x, z, 1.8, .22, yaw, "oil");
            }
            k.Oil(2.4, -1.4, .9);

            var (lx, lz) = OnCurve(12.8, 40);
            k.LampPost(Frame.At(lx, Walk, lz, FacingCentre(40), -10));
            k.Car("Sedan", Frame.At(2.51, Y + .05, -1.62, -5, -1.2), CarType.Sedan,
                new CarLook { Paint = "paintRed", FrontCrush = .4, Open = new[] { "FL", "Hood" }, CrackedGlass = true });
            k.Debris(2.0, 1.6, .6, 4);

            k.Car("Hatchback", CurveCar(4.4, 55, false), CarType.Hatchback, new CarLook { Paint = "paintWhite", Open = new[] { "FL" } });

            k.LampPost(OuterSidewalk(k, 12.8, 82));
            k.Tree(OuterSidewalk(k, 14.2, 20));
            k.Tree(OuterSidewalk(k, 14.2, 68));
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.BagPile(6.2, 6.2, 2, .4);
            k.Litter(4);
            return k.Model;
        }

        // A delivery van stopped half on the outer sidewalk with its rear doors open; the driver had started unloading
        // onto a pallet and dropped the rest in the road.
        static TileModel CurveDelivery()
        {
            var k = new RoadKit("RoadCurve_Delivery", false, false, true, true, 2513);
            CurveGround(k);
            var (mx, mz) = OnCurve(5.4, 25);
            k.Manhole(mx, mz);

            var (vx, vz) = OnCurve(11.55, 50);
            k.Car("Delivery van", Frame.At(vx, Y + .05, vz, -50), CarType.Van, new CarLook { Paint = "paintMustard", Open = new[] { "Rear", "FL" } });
            var pallet = OuterSidewalk(k, 13.2, 30);
            k.Pallet(pallet);
            var top = pallet.Local(0, .14, 0);
            k.Box(top.Local(-.28, 0, -.2, 5), -.28, .28, 0, .45, -.24, .24, "cardboard");
            k.Box(top.Local(.3, 0, -.15, -8), -.26, .26, 0, .4, -.22, .22, "cardboard");
            k.Box(top.Local(.05, 0, .28, 12), -.3, .3, 0, .5, -.2, .2, "cardboard");
            k.Box(top.Local(-.2, .45, -.2, 30), -.22, .22, 0, .3, -.2, .2, "cardboard");
            k.Collider("Boxes", pallet, -.6, .6, 0, .9, -.5, .5);
            k.Box(k.On(1.3, -2.3, 25), -.25, .25, 0, .35, -.2, .2, "cardboard");
            k.Box(k.On(2.5, -1.9, 70), -.22, .22, 0, .3, -.2, .2, "cardboard");
            k.Box(k.On(1.9, -3.3, 10), -.35, .35, 0, .02, -.28, .28, "cardboard");

            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.Hydrant(OuterSidewalk(k, 12.8, 22));
            k.Tree(OuterSidewalk(k, 14.2, 66));
            k.Bench(OuterSidewalk(k, 17.5, 45));
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.Litter(4);
            return k.Model;
        }

        // A bus shelter on the outside of the curve, nobody waiting; one sedan left in the inner lane.
        static TileModel CurveBusStop()
        {
            var k = new RoadKit("RoadCurve_BusStop", false, false, true, true, 2617);
            CurveGround(k);
            var (px, pz) = OnCurve(9.0, 65);
            k.Patch(px, pz, 2.0, 1.3, 20, "asphaltPatch", RoadKit.PatchLift);

            k.BusStop(OuterSidewalk(k, 12.55, 45));
            k.Car("Sedan", CurveCar(5.5, 40, false), CarType.Sedan, new CarLook { Paint = "paintBlue" });
            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.Tree(OuterSidewalk(k, 14.2, 22));
            k.Tree(OuterSidewalk(k, 14.2, 68));
            k.WheelieBin(OuterSidewalk(k, 13.4, 29), false);
            k.Litter(2);
            return k.Model;
        }

        // Police closed the outer lane with a line of barriers and diverted traffic into the inner lane; their car is
        // still parked behind the barriers. A hatchback squeezing through was left where it stopped.
        static TileModel CurveDetour()
        {
            var k = new RoadKit("RoadCurve_Detour", false, false, true, true, 2719);
            CurveGround(k);
            k.Oil(-3.2, -2.6, .7);

            foreach (double a in new[] { 18.0, 32.0, 46.0, 60.0, 74.0 })
            {
                var (x, z) = OnCurve(9.7, a);
                k.Barrier(k.On(x, z, 90 - a));
            }
            foreach (double a in new[] { 7.0, 25.0, 84.0 })
            {
                var (x, z) = OnCurve(9.7, a);
                k.Cone(k.On(x, z));
            }
            k.Car("Police car", CurveCar(10.95, 46, true), CarType.Sedan, new CarLook { Paint = "paintWhite", Police = true, Open = new[] { "FR" } });
            k.Car("Hatchback", CurveCar(5.3, 28, true), CarType.Hatchback, new CarLook { Paint = "paintRed", Open = new[] { "FL" } });

            var (sx, sz) = OnCurve(12.9, 6);
            k.Sign(Frame.At(sx, Walk, sz, 180), "Works");
            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.Tree(OuterSidewalk(k, 14.2, 50));
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.Litter(3);
            return k.Model;
        }

        // Two cars met head-on across the centre line and both burned out where they stopped.
        static TileModel CurveHeadOn()
        {
            var k = new RoadKit("RoadCurve_HeadOn", false, false, true, true, 2823);
            CurveGround(k);
            k.Pothole(2.6, -5.2, .45);
            k.Scorch(-.87, -3.29, 1.6);
            k.Scorch(-3.51, -.83, 1.4);
            k.Car("Burnt sedan", Frame.At(-.87, Y, -3.29, -47), CarType.Sedan, new CarLook { Burnt = true, FrontCrush = .5 });
            k.Car("Burnt hatchback", Frame.At(-3.51, Y, -.83, 133), CarType.Hatchback, new CarLook { Burnt = true, FrontCrush = .5, Open = new[] { "FR" } });
            k.Debris(-2.3, -1.9, 1.0, 5);

            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.Tree(OuterSidewalk(k, 14.2, 30));
            k.Tree(OuterSidewalk(k, 14.2, 75));
            k.Sign(Frame.At(-6.0, Walk, -6.0, 45), "Curve");
            k.Litter(3);
            return k.Model;
        }

        // Traffic queued round the outer lane toward the city and was abandoned car by car, doors left open; someone
        // dropped a suitcase crossing the road.
        static TileModel CurveQueue()
        {
            var k = new RoadKit("RoadCurve_Queue", false, false, true, true, 2929);
            CurveGround(k);
            var (mx, mz) = OnCurve(5.0, 70);
            k.Manhole(mx, mz);
            var (ox, oz) = OnCurve(10, 45);
            k.Oil(ox, oz, .7);

            k.Car("Hatchback", CurveCar(10, 14, true), CarType.Hatchback, new CarLook { Paint = "paintBeige", Open = new[] { "FL" } });
            k.Car("Sedan", CurveCar(10, 45, true), CarType.Sedan, new CarLook { Paint = "paintGrey", Open = new[] { "FL", "RL" } });
            k.Car("Hatchback 2", CurveCar(10, 76, true), CarType.Hatchback, new CarLook { Paint = "paintTeal", Open = new[] { "FL", "FR" } });
            var (cx, cz) = OnCurve(5.2, 50);
            k.Suitcase(cx, cz, 70);

            foreach (double a in new[] { 14.0, 82.0 }) k.LampPost(OuterSidewalk(k, 12.8, a));
            k.Tree(OuterSidewalk(k, 14.2, 40));
            k.BagPile(6.3, 6.3, 2, .4);
            k.Litter(3);
            return k.Model;
        }

        // ================================================================== T: east, south and west

        // A pickup turning left out of the side street was T-boned by a taxi and shoved into the back of a van
        // waiting on the west arm; the car queued behind the pickup was left with its doors open.
        static TileModel TGridlock()
        {
            var k = new RoadKit("RoadT_Gridlock", false, true, true, true, 3307);
            TGround(k);

            k.Pothole(5.6, 1.4, .5);
            k.Manhole(3.0, -1.2);
            k.Decal(k.On(-2.0, 3.4), RoadKit.PatchLift, "asphaltPatch", (-1.4, -.7), (1.3, -.8), (1.4, .7), (-1.2, .8));

            // The collision in the middle of the junction.
            var pickup = Frame.At(-.3, Y, -.9, -50);
            k.Car("Pickup", pickup, CarType.Pickup, new CarLook { Paint = "paintBeige", Open = new[] { "FR" }, CrackedGlass = true });
            k.Crate(pickup.Local(.2, CarType.Pickup.Clearance + CarType.Pickup.BodyHeight, -1.8, 15), .55);
            k.Oil(-1.6, -2.6, 1.0);
            k.Car("Taxi", Frame.At(-2.3, Y, -3.29, 40), CarType.Sedan,
                new CarLook { Paint = "paintMustard", FrontCrush = .45, Open = new[] { "Hood", "FL" }, CrackedGlass = true });
            k.Debris(-1.0, -2.4, .9, 4);
            k.Car("Van", Frame.At(-4.66, Y, 2.35, -95), CarType.Van, new CarLook { Paint = "paintGrey", Open = new[] { "FR" } });
            k.Car("Hatchback", Frame.At(2.5, Y, -5.25, 4), CarType.Hatchback, new CarLook { Paint = "paintWhite", Open = new[] { "FL", "RL" } });
            k.Oil(4.9, 3.1, 1.3);
            k.Car("Burnt sedan", Frame.At(4.9, Y, 3.25, -88), CarType.Sedan, new CarLook { Burnt = true });

            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -135));
            k.TrafficLight(Frame.At(-3.5, Walk, 5.3, 180));
            k.Sign(Frame.At(-5.85, Walk, -5.85, 180), "Stop");
            k.LampPost(Frame.At(2.5, Walk, 5.2, 180));
            k.Planter(k.On(.5, 6.85), 1.4);
            k.Hydrant(Frame.At(6.5, Walk, -6.6, -135));
            k.Bench(Frame.At(-5.8, Walk, 6.75, 180));
            k.NewsBox(Frame.At(-4.4, Walk, 7.0, 180));
            k.BagPile(4.8, 6.6, 3, .5);
            k.WheelieBin(Frame.At(6.6, Walk, 5.4, 200), true);
            k.Litter(2, (x, z) => x > 3.5 && z > 5);
            k.Litter(5);
            return k.Model;
        }

        // A quiet junction: dead traffic lights, a bench and a planter on the corner, one car parked at the curb.
        static TileModel TQuiet()
        {
            var k = new RoadKit("RoadT_Quiet", false, true, true, true, 3401);
            k.Model.Weight = 2;
            TGround(k);
            k.Manhole(-1.5, -1.6);
            k.Patch(2.8, 2.2, 1.6, 1.1, -8, "asphaltPatch", RoadKit.PatchLift);
            k.Car("Sedan", Frame.At(-2.2, Y, 3.4, -90), CarType.Sedan, new CarLook { Paint = "paintGreen" });
            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -135));
            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.LampPost(Frame.At(2.5, Walk, 5.2, 180));
            k.Bench(k.On(-1.0, 6.75, 180));
            k.Planter(k.On(-3.4, 6.9), 1.6);
            k.Tree(k.On(5.6, 6.2, 10));
            k.Litter(2);
            return k.Model;
        }

        // The bus stopped at the shelter on the through road and was left there, doors open; a car that had just
        // turned out of the side street stopped behind the crosswalk.
        static TileModel TBusStop()
        {
            var k = new RoadKit("RoadT_BusStop", false, true, true, true, 3503);
            TGround(k);
            k.Pothole(-2.6, -2.2, .4);
            k.BusStop(k.On(2.6, 5.25, 180));
            k.Car("Bus", Frame.At(.3, Y, 3.12, -90), CarType.Bus, new CarLook { Paint = "paintMustard", Stripe = "paintTeal", Open = new[] { "FR" } });
            k.Car("Sedan", Frame.At(2.3, Y, -3.4, 15), CarType.Sedan, new CarLook { Paint = "paintRed", Open = new[] { "FL" } });
            k.Suitcase(-2.2, 5.9, 20);

            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -135));
            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.LampPost(Frame.At(-4.5, Walk, 5.2, 180));
            k.Tree(k.On(6.3, 6.2, 15));
            k.Hydrant(Frame.At(6.4, Walk, -6.5, -135));
            k.Litter(3);
            return k.Model;
        }

        // The side street was sealed off: barriers across it with a gap on foot, sandbags behind; the army pickup
        // that brought them is parked on the through road.
        static TileModel TCheckpoint()
        {
            var k = new RoadKit("RoadT_Checkpoint", false, true, true, true, 3607);
            TGround(k);
            k.Manhole(-2.4, 1.4);

            k.Barrier(k.On(-3.5, -5.1, 0));
            k.Barrier(k.On(-1.5, -5.15, 2));
            k.Barrier(k.On(1.5, -5.1, -2));
            k.Barrier(k.On(3.5, -5.1, 0));
            k.Sandbags(k.On(-2.6, -6.4), -1.6, 1.6, 3);
            k.Sandbags(k.On(2.6, -6.4), -1.6, 1.6, 3);
            k.Cone(k.On(-4.0, -3.9));
            k.Cone(k.On(4.1, -4.2, 30).Local(0, .16, 0, 0, 0, 85));
            k.Sign(Frame.At(-5.7, Walk, -5.7, 0), "NoEntry");

            k.Car("Army pickup", Frame.At(1.2, Y, 3.2, -90), CarType.Pickup, new CarLook { Paint = "army", Open = new[] { "FL" } });
            k.Crate(k.On(5.4, 6.4, 10), .6);
            k.Crate(k.On(6.2, 6.6, -12), .5);
            k.Barrel(k.On(-5.6, 6.2), true);

            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -135));
            k.LampPost(Frame.At(-2.5, Walk, 5.2, 180));
            k.Litter(3);
            return k.Model;
        }

        // A van ran the light and flipped a hatchback onto its roof in the middle of the junction; the traffic light
        // it took down on the way lies across the west crosswalk.
        static TileModel TFlipped()
        {
            var k = new RoadKit("RoadT_Flipped", false, true, true, true, 3709);
            TGround(k);
            k.Oil(-.4, .8, 1.2);
            k.Car("Flipped hatchback", Frame.At(-.6, Y + CarType.Hatchback.Top, .9, 100, 0, 180), CarType.Hatchback,
                new CarLook { Paint = "paintBlue", CrackedGlass = true });
            k.Car("Van", Frame.At(2.3, Y, -2.6, -15), CarType.Van, new CarLook { Paint = "paintRed", FrontCrush = .3, Open = new[] { "FL" }, CrackedGlass = true });
            k.Debris(.9, -.4, .7, 5);
            k.TrafficLight(Frame.At(-5.85, Y + .1, -4.4, 90, 0, 90));

            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -135));
            k.LampPost(Frame.At(2.5, Walk, 5.2, 180));
            k.Bench(Frame.At(-5.8, Walk, 6.75, 180));
            k.Hydrant(Frame.At(.6, Walk, 5.35, 180));
            k.BagPile(4.8, 6.6, 2, .4);
            k.Litter(4);
            return k.Model;
        }

        // ================================================================== 4-way

        // An evacuation roadblock: concrete barriers close the north and east arms, leaving a gap on foot by the police
        // car parked behind them. Cars queued on the south and west arms were abandoned; one tried to ram through.
        static TileModel FourWayRoadblock()
        {
            var k = new RoadKit("RoadFourWay_Roadblock", true, true, true, true, 4409);
            FourWayGround(k);

            k.Pothole(-2.8, -1.4, .45);
            k.Manhole(-1.2, 3.0);
            k.Decal(k.On(2.6, 2.4), RoadKit.PatchLift, "asphaltPatch", (-1.0, -.9), (1.1, -.8), (1.0, .9), (-1.1, .8));

            // Barriers across the north arm (a gap on foot at x -0.3..1.6) and the east arm (one shoved askew by the red sedan).
            k.Barrier(k.On(-3.4, 4.2, 0));
            k.Barrier(k.On(-1.3, 4.25, 6));
            k.Barrier(k.On(2.6, 4.2, -3));
            k.Barrier(k.On(4.2, 3.4, 90));
            k.Barrier(k.On(4.2, 1.3, 92));
            k.Barrier(k.On(5.1, -1.2, 115));
            k.Barrier(k.On(4.2, -3.4, 88));
            k.Cone(k.On(.6, 5.4, 0));
            k.Cone(k.On(1.5, 6.6, 30).Local(0, .16, 0, 0, 0, 85));

            k.Car("Police car", Frame.At(-.3, Y, 1.9, 75), CarType.Sedan, new CarLook { Paint = "paintWhite", Police = true, Open = new[] { "FL", "FR" } });
            k.Barrel(k.On(3.1, 3.0, 0), true);
            k.Oil(2.3, -1.7, .9);
            k.Car("Rammer", Frame.At(1.9, Y, -1.7, 84), CarType.Sedan,
                new CarLook { Paint = "paintRed", FrontCrush = .4, Open = new[] { "Hood", "FL" }, CrackedGlass = true });
            k.Debris(4.6, -1.8, .6, 4);
            k.Car("Hatchback", Frame.At(2.3, Y, -5.3, 2), CarType.Hatchback, new CarLook { Paint = "paintBeige", Open = new[] { "FL" } });
            k.Car("Van", Frame.At(-4.75, Y, -2.3, 90), CarType.Van, new CarLook { Paint = "paintBlue", Open = new[] { "FL" } });
            k.Suitcase(-3.6, -.4, 70);

            k.TrafficLight(Frame.At(5.85, Walk, 5.85, -135));
            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.LampPost(Frame.At(-5.9, Walk, 5.9, 135));
            k.LampPost(Frame.At(5.9, Walk, -5.9, -45));
            k.BagPile(6.7, 6.6, 2, .4);
            k.Litter(5);
            return k.Model;
        }

        // An empty junction: dead lights, lamps, a planter on one corner, a lone cone.
        static TileModel FourWayQuiet()
        {
            var k = new RoadKit("RoadFourWay_Quiet", true, true, true, true, 4503);
            k.Model.Weight = 2;
            FourWayGround(k);
            k.Manhole(1.4, -1.0);
            k.Patch(-2.2, 2.0, 1.8, 1.2, 15, "asphaltPatch", RoadKit.PatchLift);
            k.TrafficLight(Frame.At(5.85, Walk, 5.85, -135));
            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.LampPost(Frame.At(-5.9, Walk, 5.9, 135));
            k.LampPost(Frame.At(5.9, Walk, -5.9, -45));
            k.Planter(k.On(6.6, 6.6, -45), 1.2);
            k.Cone(k.On(2.0, 3.0, 20).Local(0, .16, 0, 0, 0, 85));
            k.Litter(2);
            return k.Model;
        }

        // A city bus swerved through the junction and a sedan drove into its side; the bus sits diagonally across the
        // middle, doors open, its passengers' bags strewn about.
        static TileModel FourWayBusCrash()
        {
            var k = new RoadKit("RoadFourWay_BusCrash", true, true, true, true, 4607);
            FourWayGround(k);
            k.Pothole(-2.8, 3.2, .4);
            k.Oil(1.0, -.5, 1.0);
            k.Car("Bus", Frame.At(-.3, Y, .5, 50), CarType.Bus, new CarLook { Paint = "paintBlue", Stripe = "paintWhite", Open = new[] { "FR" }, CrackedGlass = true });
            k.Car("Sedan", Frame.At(2.54, Y, -1.34, -40), CarType.Sedan, new CarLook { Paint = "paintGrey", FrontCrush = .5, Open = new[] { "FL" }, CrackedGlass = true });
            k.Debris(1.6, .2, .6, 4);
            k.Suitcase(-2.6, 3.0, 40);
            k.Suitcase(5.2, -.8, 120);
            k.TrashBag(k.On(-.8, -3.4, 30), 1, fallen: true);

            k.TrafficLight(Frame.At(5.85, Walk, 5.85, -135));
            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.LampPost(Frame.At(-5.9, Walk, 5.9, 135));
            k.LampPost(Frame.At(5.9, Walk, -5.9, -45));
            k.Litter(4);
            return k.Model;
        }

        // An aid distribution point in the junction: an army truck backed in, its crates unloaded onto pallets, cots
        // under a canvas awning, barriers at the corners. Then the crowds came.
        static TileModel FourWayAidPoint()
        {
            var k = new RoadKit("RoadFourWay_AidPoint", true, true, true, true, 4711);
            FourWayGround(k);
            k.Manhole(-1.0, 1.4);

            k.Car("Army truck", Frame.At(2.3, Y, 3.4, 0), CarType.BoxTruck, new CarLook { Paint = "army", Cargo = "canvas", Open = new[] { "Rear" } });
            var p1 = k.On(1.0, -2.6, 10);
            k.Pallet(p1);
            k.Crate(p1.Local(-.28, .14, -.2, 5), .55);
            k.Crate(p1.Local(.3, .14, .25, -10), .5);
            var p2 = k.On(3.4, -2.9, -20);
            k.Pallet(p2);
            k.Box(p2.Local(-.25, .14, 0, 4), -.28, .28, 0, .45, -.4, .4, "cardboard");
            k.Box(p2.Local(.3, .14, -.1, -6), -.25, .25, 0, .4, -.3, .3, "cardboard");
            k.Collider("Boxes", p2, -.6, .6, 0, .6, -.5, .5);
            k.Crate(k.On(-.4, -4.0, 40).Local(0, .3, 0, 0, 0, 80), .5);

            k.TarpShelter(k.On(-2.6, -2.6), 3.2, 3.0, "canvas");
            k.Mattress(k.On(-3.3, -2.6));
            k.Mattress(k.On(-1.9, -2.6, 4));
            k.Barrel(k.On(-3.4, 2.8), true);
            k.Generator(k.On(-1.2, 3.4, 30));
            k.Barrier(k.On(-4.3, 4.3, 45));
            k.Barrier(k.On(4.5, -4.5, -45));

            k.TrafficLight(Frame.At(-5.85, Walk, -5.85, 45));
            k.TrafficLight(Frame.At(5.85, Walk, -5.85, -45));
            k.LampPost(Frame.At(-5.9, Walk, 5.9, 135));
            k.LampPost(Frame.At(5.9, Walk, 5.9, -135));
            k.Litter(5);
            return k.Model;
        }

        // ================================================================== dead ends: open to the south

        // Residents barricaded the end of their street with two cars, pallets and a mattress and camped behind it.
        // The barricade was overrun: a pallet knocked flat, the camp's fire barrels cold, their things left behind.
        static TileModel DeadEndBarricade()
        {
            var k = new RoadKit("RoadEnd_Barricade", false, false, true, false, 5501);
            DeadEndGround(k);
            k.Stripe(0, -5, 2.5, .14, 0);
            k.Stripe(0, 0, 2.5, .14, 0, "lineWorn");

            k.Pothole(1.9, -3.4, .4);
            k.Manhole(-2.2, -5.4);

            // The barricade across the road: a wagon broadside and a sedan nosed against it, pallets and a mattress
            // propped against their south sides; one pallet lies flat where the barricade was pushed in.
            k.Car("Wagon", Frame.At(-2.2, Y, 1.0, 92), CarType.Wagon, new CarLook { Paint = "paintGrey", Open = new[] { "RL" }, CrackedGlass = true });
            k.Car("Sedan", Frame.At(2.9, Y, 1.6, 275), CarType.Sedan, new CarLook { Paint = "paintGreen", Open = new[] { "FR" } });
            k.Pallet(k.On(-3.2, -.25, 2).Local(0, .5, 0, 0, -75));
            k.Pallet(k.On(-1.2, -.3, -4).Local(0, .5, 0, 0, -72));
            k.Mattress(k.On(2.6, .2, 85).Local(0, .92, 0, 0, -70));
            k.Pallet(k.On(.9, -1.6, 30));
            k.Debris(.4, -.8, .8, 4);

            // The camp behind it, on the sidewalk at the end of the street.
            k.Decal(k.On(1.4, 5.4, 8), RoadKit.PatchLift, "tarp", (-1.3, -1.0), (1.3, -1.0), (1.3, 1.0), (-1.3, 1.0));
            k.Decal(k.On(1.0, 5.6, 12), RoadKit.LitterLift, "paintGreen", (-.4, -.95), (.4, -.95), (.4, .95), (-.4, .95));
            k.Barrel(k.On(-1.0, 5.0, 0), true);
            k.Barrel(k.On(-3.6, 6.6, 0), false);
            k.Crate(k.On(3.4, 6.4, 20), .6);
            k.Crate(k.On(3.1, 5.6, -15), .5);
            k.Suitcase(-2.4, 4.6, 110);

            k.Sign(Frame.At(5.3, Walk, -5.8, 180), "DeadEnd");
            k.LampPost(Frame.At(-5.2, Walk, -2.0, 90));
            k.LampPost(Frame.At(5.2, Walk, 4.6, -90));
            k.BagPile(-6.6, 6.6, 2, .4);
            k.Litter(5);
            return k.Model;
        }

        // A tidy cul-de-sac: bollards along the end, a bench between planters, trees.
        static TileModel DeadEndQuiet()
        {
            var k = new RoadKit("RoadEnd_Quiet", false, false, true, false, 5603);
            k.Model.Weight = 2;
            DeadEndGround(k);
            k.Stripe(0, -5, 2.5, .14, 0);
            k.Stripe(0, 0, 2.5, .14, 0, "lineWorn");
            k.Manhole(1.6, -3.0);
            foreach (double x in new[] { -3.6, -1.8, 0, 1.8, 3.6 }) k.Bollard(k.On(x, 3.75));
            k.Bench(k.On(-.2, 6.6, 180));
            k.Planter(k.On(-2.6, 6.8), 1.8);
            k.Planter(k.On(2.2, 6.8), 1.8);
            k.Tree(k.On(5.9, 5.8, 25));
            k.Tree(k.On(-6.2, -3.5, 10));
            k.LampPost(Frame.At(5.2, Walk, -1.0, -90));
            k.Sign(Frame.At(-5.3, Walk, -5.8, 180), "DeadEnd");
            k.Litter(2);
            return k.Model;
        }

        // People dumped what they could not carry at the end of the street: a sofa, a mattress, tyres, bags, and a
        // hatchback stripped of its wheels.
        static TileModel DeadEndDump()
        {
            var k = new RoadKit("RoadEnd_Dump", false, false, true, false, 5707);
            DeadEndGround(k);
            k.Stripe(0, -5, 2.5, .14, 0, "lineWorn");
            k.Pothole(-1.8, -4.4, .45);
            k.Oil(2.3, .2, 1.1);

            k.Car("Stripped hatchback", Frame.At(2.3, Y, .2, 95), CarType.Hatchback, new CarLook { Paint = "paintTeal", Stripped = true, Open = new[] { "Hood" }, CrackedGlass = true });
            k.Sofa(k.On(-1.6, 1.9, 170), "clothRed");
            k.Mattress(k.On(-3.5, -.6, 20));
            k.LooseTyre(k.On(-.4, -.6, 10));
            k.LooseTyre(k.On(-.4, -.6, 40).Local(0, .24, 0));
            k.LooseTyre(k.On(.8, -1.7));
            k.Crate(k.On(.6, 2.2, 25), .6);
            k.BagPile(-3.5, 1.7, 3, .5);
            k.WheelieBin(Frame.At(-6.0, Walk, 4.0, 160), true);
            k.WheelieBin(Frame.At(-5.4, Walk, 5.6, 180), false);

            k.Sign(Frame.At(5.3, Walk, -5.8, 180), "DeadEnd");
            k.LampPost(Frame.At(-5.2, Walk, -2.5, 90));
            k.Litter(4, (x, z) => z > -2.5);
            k.Litter(1);
            return k.Model;
        }

        // The residents' last cars, parked nose-in at the end of the street: one burnt where it stood, one with its door
        // open and a shopping cart of belongings beside it.
        static TileModel DeadEndParking()
        {
            var k = new RoadKit("RoadEnd_Parking", false, false, true, false, 5809);
            DeadEndGround(k);
            k.Stripe(0, -5, 2.5, .14, 0, "lineWorn");
            foreach (double x in new[] { -1.5, 1.5 }) k.Stripe(x, .7, 4.6, .12, 0);

            k.Car("Hatchback", Frame.At(-3.0, Y, .82, 0), CarType.Hatchback, new CarLook { Paint = "paintRed", Open = new[] { "FL" } });
            k.Scorch(0, .5, 1.5);
            k.Car("Burnt sedan", Frame.At(0, Y, .47, 0), CarType.Sedan, new CarLook { Burnt = true });
            k.Car("Wagon", Frame.At(3.0, Y, .42, 0), CarType.Wagon, new CarLook { Paint = "paintBlue", CrackedGlass = true });
            var cart = k.On(1.8, -3.2, 200);
            k.ShoppingCart(cart);
            k.Box(cart.Local(0, .43, 0), -.24, .24, 0, .35, -.38, .4, "clothBlue");
            k.Suitcase(-2.0, -3.4, 60);

            k.LampPost(Frame.At(5.2, Walk, -3.0, -90));
            k.Tree(k.On(-6.2, -2.0, 15));
            k.Hydrant(Frame.At(4.0, Walk, 4.2, 180));
            k.Sign(Frame.At(-5.3, Walk, -5.8, 180), "DeadEnd");
            k.Litter(3);
            return k.Model;
        }

        // ================================================================== boundary exits: the road leaves the map to the north

        // Player teams spawn on these tiles, so the area round each spawn point is kept clear.

        // The city sealed with shipping containers stacked two high across the road, concrete wall panels over the
        // sidewalks. Barely anything else: a couple of barriers and cones, a no-entry sign.
        static TileModel BoundaryQuarantine()
        {
            var k = new RoadKit("RoadBoundary_Quarantine", false, false, true, false, 6101);
            k.Model.Boundary = true;
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWorn", null);
            k.Container(Frame.At(-3.08, Y, 6.2), "paintRed");
            k.Container(Frame.At(3.08, Y, 6.2), "paintBlue");
            k.Container(Frame.At(-3.1, Y + 2.59, 6.1), "paintTeal");
            k.Container(Frame.At(3.1, Y + 2.59, 6.25), "paintMustard");
            k.ConcreteWall(Frame.At(0, Y, 6.2), -7.5, -6.15);
            k.ConcreteWall(Frame.At(0, Y, 6.2), 6.19, 7.5);

            k.Barrier(k.On(-1.6, 3.6, 0));
            k.Barrier(k.On(2.2, 3.7, -4));
            k.Cone(k.On(.6, 3.9));
            k.Cone(k.On(3.9, 2.8, 50).Local(0, .16, 0, 0, 0, 85));
            k.Sign(Frame.At(5.4, Walk, 3.9, 180), "NoEntry");
            k.LampPost(Frame.At(-5.2, Walk, -2.5, 90));
            k.LampPost(Frame.At(5.2, Walk, -5.5, -90));
            k.Model.SpawnPoints.Add(Frame.At(0, .25, 0, 180));
            k.Litter(2, (x, z) => x * x + z * z > 9);
            return k.Model;
        }

        // A concrete wall closes the road out of the city. The northbound queue of people trying to leave ends against
        // it: the lead car rammed the wall, the one behind was abandoned, luggage left on the sidewalks.
        static TileModel BoundaryExodus()
        {
            var k = new RoadKit("RoadBoundary_Exodus", false, false, true, false, 6203);
            k.Model.Boundary = true;
            StraightGround(k);
            CentreLine(k, "lineWhite", "lineWhite", null);
            k.ConcreteWall(Frame.At(0, Y, 6.4), -7.5, 7.5);
            k.Sandbags(k.On(6.15, 5.4), -1.2, 1.2, 2);
            k.Sign(Frame.At(-5.6, Walk, 5.2, 180), "NoEntry");

            var lead = Frame.At(2.3, Y, 3.77, 0);
            k.Car("Sedan", lead, CarType.Sedan, new CarLook { Paint = "paintBeige", FrontCrush = .45, Open = new[] { "FL", "FR" }, CrackedGlass = true });
            k.Box(lead.Local(0, CarType.Sedan.Top, -.1, 4), -.45, .45, 0, .3, -.6, .4, "suitcase");
            k.Debris(1.8, 5.4, .5, 3);
            k.Car("Hatchback", Frame.At(2.3, Y, -1.09, 0), CarType.Hatchback, new CarLook { Paint = "paintGreen", Open = new[] { "FL" } });
            k.Suitcase(-3.0, 4.2, 30);
            k.Suitcase(5.9, 1.5, 100);

            k.LampPost(Frame.At(5.2, Walk, -4.5, -90));
            k.Tree(k.On(-6.2, -5.4, 30));
            k.Model.SpawnPoints.Add(Frame.At(-2.2, .25, -1.2, 180));
            k.Litter(3, (x, z) => (x + 2.2) * (x + 2.2) + (z + 1.2) * (z + 1.2) > 9);
            return k.Model;
        }

        // ================================================================== ramp: up one height level to the north

        // Like the old LedgeRoad: the road climbs 7.5 (one height level) over the tile, south edge low, north edge high,
        // the pivot halfway up. Parapets close the sides (the neighbouring cells are cliffs). Nothing lies on the slope
        // but a car that stalled on the climb.
        const double Rise = 7.5, Slope = Rise / 15;

        static TileModel RampStalled()
        {
            var k = new RoadKit("RoadRamp_Stalled", true, false, true, false, 7701);
            k.Model.Ramp = true;
            double Lift(double z) => z * Slope;
            double pitch = -Math.Atan(Slope) * 180 / Math.PI;
            k.Surface = (x, z) => (Math.Abs(x) <= Road ? Y : Walk) + Lift(z);
            double bottom = RoadKit.Base - Rise / 2;
            k.GroundSlope(-Road, Road, -7.5, 7.5, Y + Lift(-7.5), Y + Lift(7.5), bottom, "asphalt");
            foreach (double s in new[] { -1.0, 1.0 })
            {
                k.GroundSlope(s < 0 ? -Curb : Road, s < 0 ? -Road : Curb, -7.5, 7.5, Walk + Lift(-7.5), Walk + Lift(7.5), bottom, "curb");
                k.GroundSlope(s < 0 ? -7.5 : Curb, s < 0 ? -Curb : 7.5, -7.5, 7.5, Walk + Lift(-7.5), Walk + Lift(7.5), bottom, "sidewalk");
                k.SlopedBox(s < 0 ? -7.5 : 7.1, s < 0 ? -7.1 : 7.5, -7.5, 7.5, Lift, 0, 1.0, "barrier");
                k.Collider("Parapet", Frame.At(s * 7.3, 0, 0, 0, pitch), -.2, .2, 0, 1.0, -7.9, 7.9);
            }
            Frame OnSlope(double x, double z, double yaw = 0) => Frame.At(x, k.Surface(x, z), z, yaw, yaw == 180 ? -pitch : pitch);
            foreach (var (z, paint) in new[] { (-5.0, "lineWhite"), (0.0, "lineWorn"), (5.0, "lineWhite") })
                k.Decal(OnSlope(0, z), RoadKit.LineLift, paint, (-.07, -1.25), (.07, -1.25), (.07, 1.25), (-.07, 1.25));

            // The stalled hatchback in the uphill lane, oil run down the slope behind it, a cone set out.
            k.Decal(OnSlope(2.4, -4.0), RoadKit.OilLift, "oil", (-.3, -1.6), (.35, -1.6), (.3, 1.6), (-.35, 1.6));
            k.Car("Hatchback", OnSlope(2.3, -1.0), CarType.Hatchback, new CarLook { Paint = "paintGrey", Open = new[] { "Hood", "FL" } });
            k.Cone(OnSlope(2.3, -5.4));

            k.LampPost(Frame.At(-5.2, Lift(-2.5), -2.5, 90));
            k.LampPost(Frame.At(5.2, Lift(3.5), 3.5, -90));
            return k.Model;
        }
    }
}
