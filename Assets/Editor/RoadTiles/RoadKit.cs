using System;
using System.Collections.Generic;

// Geometry kit for the road tiles: primitives, the shared car and street-prop designs, and the ground pieces.
// Deliberately free of UnityEngine so the same designs can be built and previewed outside Unity;
// RoadTileBuilder turns a TileModel into meshes, colliders and a prefab. Coordinates are Unity's:
// x east, y up, z north, metres, tile centred on the origin.
namespace SimpleFPS.EditorTools.Roads
{
    public struct Vec
    {
        public double X, Y, Z;
        public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, double s) => new Vec(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec Cross(Vec a, Vec b) => new Vec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    // A position with orthonormal axes. Angles in degrees, applied like Unity's Quaternion.Euler(pitch, yaw, roll):
    // roll about forward, then pitch about right, then yaw about up. Yaw 90 turns forward (+z) to east (+x).
    public struct Frame
    {
        public Vec Origin, Right, Up, Forward;

        public static Frame At(double x, double y, double z, double yaw = 0, double pitch = 0, double roll = 0) =>
            new Frame { Origin = new Vec(0, 0, 0), Right = new Vec(1, 0, 0), Up = new Vec(0, 1, 0), Forward = new Vec(0, 0, 1) }.Local(x, y, z, yaw, pitch, roll);

        public Vec Point(double x, double y, double z) => Origin + Right * x + Up * y + Forward * z;
        public Vec Point(Vec v) => Point(v.X, v.Y, v.Z);

        public Frame Local(double x, double y, double z, double yaw = 0, double pitch = 0, double roll = 0)
        {
            Vec Turn(Vec v)
            {
                double r = roll * Math.PI / 180, p = pitch * Math.PI / 180, w = yaw * Math.PI / 180;
                v = new Vec(v.X * Math.Cos(r) - v.Y * Math.Sin(r), v.X * Math.Sin(r) + v.Y * Math.Cos(r), v.Z);
                v = new Vec(v.X, v.Y * Math.Cos(p) - v.Z * Math.Sin(p), v.Y * Math.Sin(p) + v.Z * Math.Cos(p));
                return new Vec(v.X * Math.Cos(w) + v.Z * Math.Sin(w), v.Y, -v.X * Math.Sin(w) + v.Z * Math.Cos(w));
            }
            Frame self = this;
            Vec Axis(Vec local) { var t = Turn(local); return self.Right * t.X + self.Up * t.Y + self.Forward * t.Z; }
            return new Frame { Origin = Point(x, y, z), Right = Axis(new Vec(1, 0, 0)), Up = Axis(new Vec(0, 1, 0)), Forward = Axis(new Vec(0, 0, 1)) };
        }
    }

    public sealed class MeshBuffer
    {
        public readonly List<Vec> Vertices = new List<Vec>();
        public readonly List<int> Triangles = new List<int>();
    }

    // A box collider: Size centred on Center, in Frame's axes.
    public struct ColliderBox
    {
        public string Name;
        public Frame Frame;
        public Vec Center, Size;
    }

    public sealed class TileModel
    {
        public string Name;
        public bool North, East, South, West;
        public bool Boundary;   // a map exit: the north socket is RoadSocket.Exit (North stays false: the tile walls it off)
        public bool Ramp;       // a height-change ramp, low at the south edge and high at the north
        public int Weight = 1, RepeatCooldown = 1;
        public readonly List<Frame> SpawnPoints = new List<Frame>(); // player team spawns (boundary exits)
        // Ground carries the walkable surfaces and gets a mesh collider; props are render-only, blocked by Colliders.
        public readonly SortedDictionary<string, MeshBuffer> Ground = new SortedDictionary<string, MeshBuffer>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, MeshBuffer> Props = new SortedDictionary<string, MeshBuffer>(StringComparer.Ordinal);
        public readonly List<ColliderBox> Colliders = new List<ColliderBox>();
        public readonly List<string> Problems = new List<string>();
    }

    // Every colour a road tile may use, in the generated buildings' style: flat, matte (the builder gives the whole
    // palette the buildings' smoothness), mid-valued colours from the same family as their palettes. The sidewalk is the
    // buildings' lot surface (#A4A4A4) so tiles and lots meet without a seam; the asphalt is darker than the old roads.
    public static class RoadPalette
    {
        public const float Smoothness = .18f; // GeneratedBuildingImporter's value
        public static readonly Dictionary<string, string> Colors = new Dictionary<string, string>
        {
            ["asphalt"] = "#4A4D50", ["asphaltPatch"] = "#56595B", ["oil"] = "#353739", ["dark"] = "#33383B",
            ["lineWhite"] = "#D8D5CB", ["lineWorn"] = "#9C9B95",
            ["sidewalk"] = "#A4A4A4", ["curb"] = "#B8B7B2", ["manhole"] = "#3E4245",
            ["paintRed"] = "#A2483A", ["paintBlue"] = "#4D6B88", ["paintBeige"] = "#D1BE98", ["paintWhite"] = "#D9D4C7",
            ["paintGreen"] = "#58744B", ["paintGrey"] = "#85888A", ["paintMustard"] = "#C1973D", ["paintTeal"] = "#3F6E6A",
            ["burnt"] = "#3D3632", ["rust"] = "#7A4E36", ["glass"] = "#33383B", ["glassCracked"] = "#66747A",
            ["trim"] = "#2E3236", ["tyre"] = "#26282A", ["headlight"] = "#E2DCC0", ["taillight"] = "#9A3A30",
            ["truckBox"] = "#C9C5B8",
            ["lampMetal"] = "#4E5B60", ["lampLens"] = "#E2DCC0", ["signPole"] = "#85888A",
            ["signRed"] = "#B03A30", ["signWhite"] = "#E0DDD3", ["signYellow"] = "#D2A83E",
            ["lightRed"] = "#6E2A24", ["lightAmber"] = "#6E5622", ["lightGreen"] = "#2C5236",
            ["hydrant"] = "#B03A30", ["cone"] = "#D2702F",
            ["trashBag"] = "#2A2C2E", ["trashBagGreen"] = "#34453A", ["binGreen"] = "#4E7A4C",
            ["cardboard"] = "#A0835C", ["wood"] = "#92734F", ["debris"] = "#8E8B84",
            ["clothRed"] = "#8E4A42", ["clothBlue"] = "#4D5E7A", ["barrier"] = "#B4B0A6", ["mattress"] = "#CFC6B0", ["tarp"] = "#4A7A96", ["suitcase"] = "#6A4E3C",
            ["foliage"] = "#45684F", ["foliageDark"] = "#3C5B45", ["bark"] = "#5E4A38", ["soil"] = "#5A4A3A", ["dirt"] = "#7A6A55",
            ["sandbag"] = "#A39573", ["army"] = "#5B6249", ["canvas"] = "#6F7458",
        };
    }

    // A vehicle archetype. Every car in the game is built from one of these, so sizes and proportions stay consistent:
    // a lower body box, a cabin box with dark window panels and box wheels, like the buildings' box-built props.
    // Real-world dimensions (survivors are 1.8 tall and jump about 1.17): hatchbacks and sedans top out near 1.4, so they
    // can be jumped onto like the old box cars; their hood and trunk are a step up, and from a van's hood its roof.
    public sealed class CarType
    {
        public string Kind;            // "Car", "Van", "Pickup", "Truck"
        public double Length, Width, BodyHeight, CabinHeight, CabinRear, CabinFront, WheelBase, WheelRadius;
        public double Clearance => WheelRadius * .6;

        public static readonly CarType Hatchback = new CarType { Kind = "Car", Length = 4.1, Width = 1.8, BodyHeight = .6, CabinHeight = .55, CabinRear = -1.9, CabinFront = .55, WheelBase = 2.6, WheelRadius = .31 };
        public static readonly CarType Sedan = new CarType { Kind = "Car", Length = 4.8, Width = 1.85, BodyHeight = .58, CabinHeight = .52, CabinRear = -1.15, CabinFront = .9, WheelBase = 2.9, WheelRadius = .32 };
        public static readonly CarType Wagon = new CarType { Kind = "Car", Length = 4.9, Width = 1.9, BodyHeight = .68, CabinHeight = .6, CabinRear = -2.3, CabinFront = .85, WheelBase = 2.95, WheelRadius = .34 };
        public static readonly CarType Van = new CarType { Kind = "Van", Length = 5.3, Width = 2.0, BodyHeight = .9, CabinHeight = .85, CabinRear = 1.0, CabinFront = 1.75, WheelBase = 3.3, WheelRadius = .35 };
        public static readonly CarType Pickup = new CarType { Kind = "Pickup", Length = 5.5, Width = 2.0, BodyHeight = .75, CabinHeight = .65, CabinRear = -.75, CabinFront = .75, WheelBase = 3.4, WheelRadius = .38 };
        public static readonly CarType BoxTruck = new CarType { Kind = "Truck", Length = 7.4, Width = 2.4, BodyHeight = 1.05, CabinHeight = .85, CabinRear = 2.15, CabinFront = 3.2, WheelBase = 4.4, WheelRadius = .5 };
        public static readonly CarType Bus = new CarType { Kind = "Bus", Length = 10.5, Width = 2.5, BodyHeight = .95, CabinHeight = 1.55, WheelBase = 6.2, WheelRadius = .5 };

        // Roof height above the road of an undamaged car, cabin or bus (not a truck's cargo box).
        public double Top => Kind == "Truck" ? Clearance + BodyHeight + CabinHeight + .08 : Clearance + BodyHeight + CabinHeight
            + (Kind == "Van" ? .07 : Kind == "Bus" ? 0 : .05);
    }

    // How one particular car has ended up.
    public sealed class CarLook
    {
        public string Paint = "paintGrey";
        public bool Burnt;                       // charred shell on its rims, no glass
        public bool CrackedGlass;
        public string[] Open = new string[0];    // "FL", "FR", "RL", "RR", "Trunk", "Hood", "Rear" (van/truck rear doors)
        public double FrontCrush, RearCrush;     // metres lost at the front/rear in a crash
        public bool Police;                      // red and blue light bar on the roof (police cars, ambulances)
        public string Stripe;                    // a band of colour along both sides (ambulance, bus, works van)
        public string Cargo = "truckBox";        // a box truck's cargo box
        public bool Stripped;                    // wheels taken: the body sits on the ground
    }

    public sealed class RoadKit
    {
        public const double Half = 7.5, RoadY = -.1, WalkY = 0, Base = -.2;
        public readonly TileModel Model;
        public readonly Random Random;
        // Walking surface height at a point: the road (RoadY) or a sidewalk/curb (WalkY). Set by each design.
        public Func<double, double, double> Surface = (x, z) => RoadY;
        // Boxes litter keeps out of (cars, piles, posts); colliders add themselves.
        readonly List<(Frame frame, Vec center, Vec size)> occupied = new List<(Frame, Vec, Vec)>();
        bool ground;

        public RoadKit(string name, bool north, bool east, bool south, bool west, int seed)
        {
            Model = new TileModel { Name = name, North = north, East = east, South = south, West = west };
            Random = new Random(seed);
        }

        public double Next(double min, double max) => min + Random.NextDouble() * (max - min);
        public string Pick(params string[] options) => options[Random.Next(options.Length)];

        MeshBuffer Buffer(string material)
        {
            if (!RoadPalette.Colors.ContainsKey(material)) throw new ArgumentException("Unknown road material " + material);
            var target = ground ? Model.Ground : Model.Props;
            if (!target.TryGetValue(material, out var buffer)) target[material] = buffer = new MeshBuffer();
            return buffer;
        }

        // A convex polygon face, wound so it faces away from `inside` (Unity: clockwise seen from the front).
        void Face(string material, Vec inside, params Vec[] corners)
        {
            var normal = Vec.Cross(corners[1] - corners[0], corners[2] - corners[0]);
            var centre = new Vec(0, 0, 0);
            foreach (var c in corners) centre += c * (1.0 / corners.Length);
            if (Vec.Dot(normal, centre - inside) < 0) Array.Reverse(corners);
            var buffer = Buffer(material);
            int first = buffer.Vertices.Count;
            foreach (var c in corners)
            {
                if (!ground && (Math.Abs(c.X) > Half + .001 || Math.Abs(c.Z) > Half + .001))
                    Model.Problems.Add($"{material} vertex outside the tile at x {c.X:0.00}, z {c.Z:0.00}");
                buffer.Vertices.Add(c);
            }
            for (int i = 1; i < corners.Length - 1; i++) buffer.Triangles.AddRange(new[] { first, first + i, first + i + 1 });
        }

        // A hexahedron from its bottom and top quads (each listed in the same order around).
        void Hexa(string material, Vec[] bottom, Vec[] top, bool withBottom = true)
        {
            var inside = new Vec(0, 0, 0);
            foreach (var v in bottom) inside += v * .125;
            foreach (var v in top) inside += v * .125;
            Face(material, inside, top[0], top[1], top[2], top[3]);
            if (withBottom) Face(material, inside, bottom[0], bottom[1], bottom[2], bottom[3]);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                Face(material, inside, bottom[i], bottom[j], top[j], top[i]);
            }
        }

        // An axis-aligned box in the frame's axes.
        public void Box(Frame f, double x0, double x1, double y0, double y1, double z0, double z1, string material) =>
            Hexa(material, new[] { f.Point(x0, y0, z0), f.Point(x1, y0, z0), f.Point(x1, y0, z1), f.Point(x0, y0, z1) },
                new[] { f.Point(x0, y1, z0), f.Point(x1, y1, z0), f.Point(x1, y1, z1), f.Point(x0, y1, z1) });

        // A flat convex polygon lying on a surface, facing up. Points are (x, z) in the frame; y is height above it.
        public void Decal(Frame f, double y, string material, params (double x, double z)[] points)
        {
            var corners = new Vec[points.Length];
            for (int i = 0; i < points.Length; i++) corners[i] = f.Point(points[i].x, y, points[i].z);
            Face(material, f.Point(0, y - 1, 0), corners);
        }

        public void Collider(string name, Frame f, double x0, double x1, double y0, double y1, double z0, double z1)
        {
            var center = new Vec((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
            var size = new Vec(x1 - x0, y1 - y0, z1 - z0);
            Model.Colliders.Add(new ColliderBox { Name = name, Frame = f, Center = center, Size = size });
            occupied.Add((f, center, size));
        }

        public void Occupy(Frame f, double x0, double x1, double z0, double z1) =>
            occupied.Add((f, new Vec((x0 + x1) / 2, 0, (z0 + z1) / 2), new Vec(x1 - x0, 1, z1 - z0)));

        // Whether (x, z) lies under any occupied box, seen from above (the box's most upright axis is ignored).
        public bool Occupied(double x, double z, double pad = 0)
        {
            foreach (var (f, center, size) in occupied)
            {
                var c = f.Point(center);
                var d = new Vec(x - c.X, 0, z - c.Z);
                var axes = new[] { (f.Right, size.X), (f.Up, size.Y), (f.Forward, size.Z) };
                int upright = 0;
                for (int i = 1; i < 3; i++) if (Math.Abs(axes[i].Item1.Y) > Math.Abs(axes[upright].Item1.Y)) upright = i;
                bool inside = true;
                for (int i = 0; i < 3 && inside; i++)
                {
                    if (i == upright) continue;
                    var flat = new Vec(axes[i].Item1.X, 0, axes[i].Item1.Z);
                    double length = Math.Sqrt(Vec.Dot(flat, flat));
                    inside = Math.Abs(Vec.Dot(d, flat) / length) < axes[i].Item2 / 2 * length + pad;
                }
                if (inside) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- ground

        public void GroundBox(double x0, double x1, double z0, double z1, double top, string material)
        {
            ground = true;
            var f = Frame.At(0, 0, 0);
            Hexa(material, new[] { f.Point(x0, Base, z0), f.Point(x1, Base, z0), f.Point(x1, Base, z1), f.Point(x0, Base, z1) },
                new[] { f.Point(x0, top, z0), f.Point(x1, top, z0), f.Point(x1, top, z1), f.Point(x0, top, z1) }, withBottom: false);
            ground = false;
        }

        // A slab under a ramp: its top rises linearly from y0 at z0 to y1 at z1; its bottom is flat at `bottom`, so the
        // sides fill the wedge down to the low end.
        public void GroundSlope(double x0, double x1, double z0, double z1, double y0, double y1, double bottom, string material)
        {
            ground = true;
            Hexa(material, new[] { new Vec(x0, bottom, z0), new Vec(x1, bottom, z0), new Vec(x1, bottom, z1), new Vec(x0, bottom, z1) },
                new[] { new Vec(x0, y0, z0), new Vec(x1, y0, z0), new Vec(x1, y1, z1), new Vec(x0, y1, z1) }, withBottom: false);
            ground = false;
        }

        // A prop whose bottom and top follow a slope (a parapet along a ramp): y0..y1 above baseY(z), with vertical ends
        // so nothing pokes past z0..z1.
        public void SlopedBox(double x0, double x1, double z0, double z1, Func<double, double> baseY, double y0, double y1, string material) =>
            Hexa(material, new[] { new Vec(x0, baseY(z0) + y0, z0), new Vec(x1, baseY(z0) + y0, z0), new Vec(x1, baseY(z1) + y0, z1), new Vec(x0, baseY(z1) + y0, z1) },
                new[] { new Vec(x0, baseY(z0) + y1, z0), new Vec(x1, baseY(z0) + y1, z0), new Vec(x1, baseY(z1) + y1, z1), new Vec(x0, baseY(z1) + y1, z1) });

        static (double x, double z) Polar(double cx, double cz, double r, double degrees) =>
            (cx + r * Math.Cos(degrees * Math.PI / 180), cz + r * Math.Sin(degrees * Math.PI / 180));

        // A ring sector around (cx, cz) between radii r0..r1 and angles a0..a1 (degrees, counter-clockwise from east).
        public void GroundArc(double cx, double cz, double r0, double r1, double a0, double a1, double top, string material, int segments = 16)
        {
            ground = true;
            for (int i = 0; i < segments; i++)
            {
                double b0 = a0 + (a1 - a0) * i / segments, b1 = a0 + (a1 - a0) * (i + 1) / segments;
                var p = new[] { Polar(cx, cz, r0, b0), Polar(cx, cz, r1, b0), Polar(cx, cz, r1, b1), Polar(cx, cz, r0, b1) };
                Hexa(material, Array.ConvertAll(p, q => new Vec(q.x, Base, q.z)), Array.ConvertAll(p, q => new Vec(q.x, top, q.z)), withBottom: false);
            }
            ground = false;
        }

        // The part of the rectangle x0..x1, z0..z1 farther than r from (cx, cz), between angles a0..a1, as fans of
        // quads between the arc and the rectangle's edge (the outside of a curve, or the asphalt round a corner).
        public void GroundBeyondArc(double cx, double cz, double r, double a0, double a1, double x0, double x1, double z0, double z1, double top, string material, int segments = 16)
        {
            ground = true;
            (double x, double z) Edge(double degrees)
            {
                double dx = Math.Cos(degrees * Math.PI / 180), dz = Math.Sin(degrees * Math.PI / 180), t = double.MaxValue;
                if (dx > 1e-9) t = Math.Min(t, (x1 - cx) / dx); else if (dx < -1e-9) t = Math.Min(t, (x0 - cx) / dx);
                if (dz > 1e-9) t = Math.Min(t, (z1 - cz) / dz); else if (dz < -1e-9) t = Math.Min(t, (z0 - cz) / dz);
                return (cx + dx * t, cz + dz * t);
            }
            var angles = new List<double>();
            for (int i = 0; i <= segments; i++) angles.Add(a0 + (a1 - a0) * i / segments);
            // Split where the rays switch from one rectangle edge to the next, so every quad is convex.
            foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) })
            {
                double angle = Math.Atan2(z - cz, x - cx) * 180 / Math.PI;
                for (int k = -1; k <= 1; k++)
                    if (angle + 360 * k > a0 + 1e-6 && angle + 360 * k < a1 - 1e-6) angles.Add(angle + 360 * k);
            }
            angles.Sort();
            for (int i = 0; i < angles.Count - 1; i++)
            {
                if (angles[i + 1] - angles[i] < 1e-6) continue;
                var p = new[] { Polar(cx, cz, r, angles[i]), Edge(angles[i]), Edge(angles[i + 1]), Polar(cx, cz, r, angles[i + 1]) };
                Hexa(material, Array.ConvertAll(p, q => new Vec(q.x, Base, q.z)), Array.ConvertAll(p, q => new Vec(q.x, top, q.z)), withBottom: false);
            }
            ground = false;
        }

        // ---------------------------------------------------------------- markings and surface wear (decals)

        // Heights above the surface, far enough apart not to flicker at a distance.
        public const double PatchLift = .008, OilLift = .012, LineLift = .016, CrackLift = .02, LitterLift = .03;

        public Frame On(double x, double z, double yaw = 0) => Frame.At(x, Surface(x, z), z, yaw);

        public void Stripe(double x, double z, double length, double width, double yaw, string material = "lineWhite") =>
            Decal(On(x, z, yaw), LineLift, material, (-width / 2, -length / 2), (width / 2, -length / 2), (width / 2, length / 2), (-width / 2, length / 2));

        // A flat rectangle (the buildings draw stains and patches as flat boxes too).
        public void Patch(double x, double z, double width, double depth, double yaw, string material, double lift) =>
            Decal(On(x, z, yaw), lift, material, (-width / 2, -depth / 2), (width / 2, -depth / 2), (width / 2, depth / 2), (-width / 2, depth / 2));

        // An oil stain: a few overlapping turned rectangles.
        public void Oil(double x, double z, double radius)
        {
            Patch(x, z, radius * 1.6, radius * 1.2, Next(0, 90), "oil", OilLift);
            for (int i = 0; i < 2; i++)
                Patch(x + Next(-radius, radius) * .6, z + Next(-radius, radius) * .6, radius * Next(.6, .9), radius * Next(.5, .8), Next(0, 90), "oil", OilLift);
        }

        // Scorched ground round a burnt-out wreck.
        public void Scorch(double x, double z, double radius)
        {
            for (int i = 0; i < 3; i++)
                Patch(x + Next(-radius, radius) * .4, z + Next(-radius, radius) * .4, radius * Next(1.2, 1.8), radius * Next(1.0, 1.5), Next(0, 90), "burnt", PatchLift);
        }

        public void Pothole(double x, double z, double radius)
        {
            double yaw = Next(0, 90);
            Patch(x, z, radius * 2.4, radius * 2.0, yaw, "asphaltPatch", PatchLift);
            Patch(x, z, radius * 1.5, radius * 1.2, yaw + Next(-15, 15), "dark", CrackLift);
        }

        public void Manhole(double x, double z)
        {
            Patch(x, z, .76, .76, 0, "manhole", PatchLift);
            Patch(x, z, .56, .56, 0, "dark", CrackLift);
        }

        public bool Inside(double x, double z, double margin) => Math.Abs(x) < Half - margin && Math.Abs(z) < Half - margin;

        // Not straddling a curb: the surface is the same height all round the point.
        public bool Level(double x, double z, double pad)
        {
            double y = Surface(x, z);
            return Surface(x + pad, z) == y && Surface(x - pad, z) == y && Surface(x, z + pad) == y && Surface(x, z - pad) == y;
        }

        // Rubbish like the buildings' back alleys: flattened cardboard, loose planks, cartons and the odd bag, a few pieces
        // to a cluster with gaps between clusters. `count` is pieces; `where` limits the area (null: anywhere on the tile).
        public void Litter(int count, Func<double, double, bool> where = null)
        {
            double cx = 0, cz = 0;
            for (int placed = 0, tries = 0; placed < count && tries < count * 30; tries++)
            {
                if (tries % 10 == 0 || placed % 3 == 0) { cx = Next(-Half + .6, Half - .6); cz = Next(-Half + .6, Half - .6); }
                double x = cx + Next(-.7, .7), z = cz + Next(-.7, .7);
                if (!Inside(x, z, .6) || (where != null && !where(x, z)) || !Level(x, z, .5) || Occupied(x, z, .2)) continue;
                placed++;
                var f = On(x, z, Next(0, 360));
                double roll = Random.NextDouble();
                if (roll < .55) Box(f, -Next(.2, .4), Next(.2, .4), 0, .02, -Next(.18, .35), Next(.18, .35), "cardboard");
                else if (roll < .75) Box(f, -.07, .07, 0, .03, -Next(.45, .75), Next(.45, .75), "wood");
                else if (roll < .9) Box(f, -.2, .2, 0, Next(.2, .35), -.16, .16, "cardboard");
                else Box(f, -.22, .22, 0, .32, -.2, .2, Pick("trashBag", "trashBagGreen"));
                Occupy(f, -.4, .4, -.4, .4);
            }
        }

        // ---------------------------------------------------------------- vehicles

        // A vehicle standing in `f` (origin on the ground under its centre, facing +z). Returns its top height.
        public double Car(string name, Frame f, CarType t, CarLook look)
        {
            double w = t.Width / 2, l = t.Length / 2, r = t.WheelRadius;
            double sink = look.Stripped ? t.Clearance : look.Burnt ? r * .3 : 0; // on the ground, or on its rims
            double gc = t.Clearance - sink, belt = gc + t.BodyHeight;
            double front = l - look.FrontCrush, rear = -l + look.RearCrush;
            string paint = look.Burnt ? "rust" : look.Paint;
            string glass = look.Burnt ? "dark" : look.CrackedGlass ? "glassCracked" : "glass";
            bool Open(string part) => Array.IndexOf(look.Open, part) >= 0;

            // Wheels: square tyres with a hub, bare rusted hubs on burnt cars.
            double wheelZ = t.WheelBase / 2;
            foreach (double side in new[] { -1.0, 1.0 })
                foreach (double z in look.Stripped ? new double[0] : new[] { -wheelZ, wheelZ })
                {
                    double y = r - sink, s = side;
                    void Wheel(double inner, double outer, double radius, string material) =>
                        Box(f, Math.Min(s * inner, s * outer), Math.Max(s * inner, s * outer), y - radius, y + radius, z - radius, z + radius, material);
                    if (look.Burnt) Wheel(w - .26, w - .04, r * .7, "rust");
                    else
                    {
                        Wheel(w - .27, w - .03, r, "tyre");
                        Wheel(w - .03, w - .015, r * .5, "trim");
                    }
                    if (t.Kind == "Truck" && z < 0) Wheel(w - .52, w - .28, look.Burnt ? r * .7 : r, look.Burnt ? "rust" : "tyre"); // dual rear wheels
                }

            double top;
            if (t.Kind == "Truck")
            {
                // Cab at the front, cargo box behind on a dark chassis.
                double cabRear = t.CabinRear - .25;
                Box(f, -w, w, gc, belt, cabRear, front, paint);
                top = belt + t.CabinHeight + .08;
                Cabin(f, -w + .08, w - .08, belt, top, cabRear + .1, Math.Min(t.CabinFront, front - .2) - .2, cabRear + .1, false, paint, glass);
                Box(f, -.55, .55, gc - .05, gc + .3, rear + .2, cabRear, "trim");
                double boxBottom = gc + .3, boxTop = 3.35 - sink;
                Box(f, -w, w, boxBottom, boxTop, rear, cabRear - .12, look.Burnt ? "burnt" : look.Cargo);
                if (Open("Rear")) RearDoors(f, w, rear, boxBottom + .05, boxTop - .05, look.Burnt ? "burnt" : look.Cargo);
                Collider(name, f, -w, w, -sink, belt + t.CabinHeight + .08, cabRear, front + .08);
                Collider(name + " cargo", f, -w, w, -sink, boxTop, rear - .08, cabRear);
                top = boxTop;
            }
            else if (t.Kind == "Van")
            {
                Box(f, -w, w, gc, belt, rear, front, paint);
                double cabFront = Math.Min(t.CabinFront, front - .15) - .45;
                top = belt + t.CabinHeight + .07;
                Cabin(f, -w + .03, w - .03, belt, top, rear, cabFront, t.CabinRear, false, paint, glass);
                if (Open("Rear")) RearDoors(f, w, rear, gc + .1, top - .05, paint);
                Collider(name, f, -w, w, -sink, belt, rear - .08, front + .08);
                Collider(name + " cabin", f, -w, w, belt, top, rear, cabFront);
            }
            else if (t.Kind == "Bus")
            {
                // One long box with a window band split by pillars, a roof unit, and two doors on the kerb side (+x).
                Box(f, -w, w, gc, belt, rear, front, paint);
                top = belt + t.CabinHeight;
                Cabin(f, -w + .02, w - .02, belt, top, rear, front, rear + .2, true, paint, glass);
                for (double z = rear + 1.6; z < front - 1.0; z += 1.5)
                    foreach (double side in new[] { -1.0, 1.0 })
                        Box(f, side < 0 ? -w - .01 : w - .02, side < 0 ? -w + .02 : w + .01, belt, top, z - .07, z + .07, paint);
                Box(f, -.75, .75, top, top + .22, rear + 1.2, rear + 3.0, look.Burnt ? "burnt" : "signPole");
                foreach (double z in new[] { front - 1.3, -.6 })
                    Box(f, w, w + .02, gc + .12, top - .15, z - .6, z + .6, Open("FR") ? "dark" : glass);
                Collider(name, f, -w, w, -sink, top, rear - .08, front + .08);
            }
            else
            {
                Box(f, -w, w, gc, belt, rear, front, paint);
                double cabinFront = Math.Min(t.CabinFront, front - .3), cabinRear = Math.Max(t.CabinRear, rear + .15);
                bool hatch = t.Kind == "Car" && t.CabinRear - (-l) < .3; // the cabin runs to the tail: a hatchback or wagon
                top = belt + t.CabinHeight + .05;
                double roofRear = cabinRear + (hatch || t.Kind == "Pickup" ? .05 : .2), roofFront = cabinFront - .15;
                Cabin(f, -w + .12, w - .12, belt, top, roofRear, roofFront, roofRear, true, look.Burnt ? "burnt" : paint, glass);
                if (t.Kind == "Pickup") // open bed behind the cab
                {
                    foreach (double side in new[] { -1.0, 1.0 })
                        Box(f, side < 0 ? -w : w - .08, side < 0 ? -w + .08 : w, belt, belt + .42, rear, cabinRear - .05, paint);
                    // The tailgate, hinged at its foot; dropped flat behind the bed when open.
                    Box(f.Local(0, belt, rear, 0, Open("Rear") ? -88 : 0), -w + .08, w - .08, 0, .42, 0, .08, paint);
                    foreach (double side in new[] { -1.0, 1.0 })
                        Collider(name + " bed side", f, side < 0 ? -w : w - .1, side < 0 ? -w + .1 : w, belt, belt + .42, rear, cabinRear);
                    if (!Open("Rear")) Collider(name + " tailgate", f, -w, w, belt, belt + .42, rear - .02, rear + .1);
                }
                if (Open("Trunk") && !hatch && t.Kind == "Car")
                    Box(f.Local(0, belt, cabinRear, 0, 65), -w + .08, w - .08, 0, .05, -(cabinRear - rear) + .05, 0, paint);
                if (Open("Hood"))
                    Box(f.Local(0, belt, cabinFront, 0, -55), -w + .08, w - .08, 0, .05, 0, front - cabinFront - .05, paint);
                Collider(name, f, -w, w, -sink, belt, rear - .08, front + .08);
                Collider(name + " cabin", f, -w + .1, w - .1, belt, top, roofRear, roofFront);
            }

            if (look.Police)
            {
                double z = t.Kind == "Van" ? Math.Min(t.CabinFront, front - .15) - .75 : 0;
                Box(f, -.55, 0, top, top + .12, z - .15, z + .15, "taillight");
                Box(f, 0, .55, top, top + .12, z - .15, z + .15, "paintBlue");
            }
            if (look.Stripe != null && !look.Burnt)
                foreach (double side in new[] { -1.0, 1.0 })
                    Box(f, side < 0 ? -w - .01 : w, side < 0 ? -w : w + .01, belt - .32, belt - .14, rear + .1, front - .1, look.Stripe);

            // Bumpers and lights.
            Box(f, -w + .04, w - .04, gc + .05, gc + .3, front - .02, front + .08, "trim");
            Box(f, -w + .04, w - .04, gc + .05, gc + .3, rear - .08, rear + .02, "trim");
            if (!look.Burnt)
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    double x0 = side < 0 ? -w + .12 : w - .42, x1 = x0 + .3;
                    Box(f, x0, x1, belt - .24, belt - .1, front - .01, front + .02, look.FrontCrush > .15 ? "glassCracked" : "headlight");
                    Box(f, x0, x1, belt - .26, belt - .12, rear - .02, rear + .01, "taillight");
                }

            // Doors flung open, hinged at their front edge.
            if (t.Kind != "Truck" && t.Kind != "Bus")
            {
                double doorTop = belt + t.CabinHeight * .85, doorFront = t.Kind == "Van" ? t.CabinFront - .05 : Math.Min(t.CabinFront, front - .3) - .1;
                double doorLength = t.Kind == "Van" ? .85 : t.Kind == "Pickup" ? 1.1 : 1.05;
                foreach (var (part, side, start) in new[] { ("FL", -1.0, doorFront), ("FR", 1.0, doorFront), ("RL", -1.0, doorFront - doorLength - .05), ("RR", 1.0, doorFront - doorLength - .05) })
                {
                    if (!Open(part) || (t.Kind != "Car" && part[0] == 'R')) continue;
                    var hinge = f.Local(side * w, 0, start, side * -Next(50, 70));
                    Box(hinge, side < 0 ? -.07 : 0, side < 0 ? 0 : .07, gc + .05, belt, -doorLength, 0, paint);
                    Box(hinge, side < 0 ? -.05 : .01, side < 0 ? -.01 : .05, belt, doorTop, -doorLength + .1, -.12, glass);
                }
            }

            return top;
        }

        // A painted cabin box with dark window panels just proud of it: side windows from `windowsFrom` forward, a
        // windscreen, and a rear window if `rearWindow`.
        void Cabin(Frame f, double x0, double x1, double y0, double y1, double z0, double z1, double windowsFrom, bool rearWindow, string paint, string glass)
        {
            Box(f, x0, x1, y0, y1, z0, z1, paint);
            double wy0 = y0 + .1, wy1 = y1 - .08;
            Box(f, x0 - .015, x0, wy0, wy1, windowsFrom + .1, z1 - .12, glass);
            Box(f, x1, x1 + .015, wy0, wy1, windowsFrom + .1, z1 - .12, glass);
            Box(f, x0 + .1, x1 - .1, wy0, wy1, z1, z1 + .015, glass);
            if (rearWindow) Box(f, x0 + .12, x1 - .12, wy0, wy1, z0 - .015, z0, glass);
        }

        // Two rear doors hinged at the back corners, swung about 110 degrees out and back.
        void RearDoors(Frame f, double w, double rear, double y0, double y1, string material)
        {
            foreach (double side in new[] { -1.0, 1.0 })
                Box(f.Local(side * w, 0, rear, -side * 200), -.035, .035, y0, y1, 0, w - .02, material);
        }

        // ---------------------------------------------------------------- street furniture

        // A street lamp with its arm reaching along +z (toward the road). Origin at the foot of the post.
        public void LampPost(Frame f)
        {
            Box(f, -.15, .15, 0, .6, -.15, .15, "lampMetal");
            Box(f, -.07, .07, .6, 6.4, -.07, .07, "lampMetal");
            Box(f, -.05, .05, 6.2, 6.3, -.05, 1.6, "lampMetal");
            Box(f, -.17, .17, 6.08, 6.26, 1.25, 1.95, "lampMetal");
            Box(f, -.13, .13, 6.05, 6.08, 1.3, 1.9, "lampLens");
            Collider("Lamp post", f, -.18, .18, 0, 6.4, -.18, .18);
        }

        public void Hydrant(Frame f)
        {
            Box(f, -.18, .18, 0, .08, -.18, .18, "hydrant");
            Box(f, -.13, .13, .08, .58, -.13, .13, "hydrant");
            Box(f, -.16, .16, .58, .66, -.16, .16, "hydrant");
            Box(f, -.09, .09, .66, .78, -.09, .09, "hydrant");
            Box(f, -.24, .24, .37, .47, -.05, .05, "hydrant");
            Box(f, -.07, .07, .35, .49, 0, .22, "trim");
            Collider("Hydrant", f, -.2, .2, 0, .8, -.2, .2);
        }

        // A sign facing +z: "Stop", "Yield", "Speed", "Curve", "DeadEnd", "NoEntry" or "Works". Flat box plates on a square pole.
        public void Sign(Frame f, string kind)
        {
            Box(f, -.035, .035, 0, 2.55, -.035, .035, "signPole");
            void Plate(double half, double y0, double y1, double z0, string material) => Box(f, -half, half, y0, y1, z0, z0 + .02, material);
            switch (kind)
            {
                case "Stop":
                    Plate(.38, 1.84, 2.56, .035, "signWhite");
                    Plate(.34, 1.88, 2.52, .055, "signRed");
                    Plate(.22, 2.17, 2.23, .075, "signWhite");
                    break;
                case "Yield":
                    Plate(.38, 1.86, 2.54, .035, "signRed");
                    Plate(.28, 1.96, 2.44, .055, "signWhite");
                    break;
                case "DeadEnd": // blue square, white bar ending in a red cross-piece
                    Plate(.32, 1.88, 2.52, .035, "signWhite");
                    Plate(.28, 1.92, 2.48, .055, "paintBlue");
                    Plate(.04, 2.0, 2.3, .075, "signWhite");
                    Plate(.16, 2.3, 2.36, .075, "signRed");
                    break;
                case "NoEntry": // red square, white bar
                    Plate(.32, 1.88, 2.52, .035, "signWhite");
                    Plate(.29, 1.91, 2.49, .055, "signRed");
                    Plate(.21, 2.15, 2.25, .075, "signWhite");
                    break;
                case "Works": // yellow square with a dark digger mark
                    Plate(.34, 1.86, 2.54, .035, "trim");
                    Plate(.31, 1.89, 2.51, .055, "signYellow");
                    Box(f, -.16, .1, 2.0, 2.12, .075, .085, "trim");
                    Box(f, -.04, .06, 2.12, 2.36, .075, .085, "trim");
                    break;
                case "Speed":
                    Plate(.3, 1.82, 2.58, .035, "trim");
                    Plate(.27, 1.85, 2.55, .055, "signWhite");
                    Box(f, -.14, -.03, 2.0, 2.3, .075, .085, "trim");
                    Box(f, .03, .14, 2.0, 2.3, .075, .085, "trim");
                    break;
                default: // Curve: a yellow square with a bent arrow
                    Plate(.34, 1.86, 2.54, .035, "trim");
                    Plate(.31, 1.89, 2.51, .055, "signYellow");
                    Plate(.03, 1.98, 2.3, .075, "trim");
                    Box(f, -.2, .03, 2.25, 2.31, .075, .095, "trim");
                    break;
            }
            Collider(kind + " sign", f, -.1, .1, 0, 2.6, -.1, .1);
        }

        // A dead traffic light facing +z.
        public void TrafficLight(Frame f)
        {
            Box(f, -.08, .08, 0, 3.4, -.08, .08, "signPole");
            Box(f, -.18, .18, 2.3, 3.32, .1, .42, "trim");
            string[] lenses = { "lightRed", "lightAmber", "lightGreen" };
            for (int i = 0; i < 3; i++)
            {
                double y = 3.12 - i * .32;
                Box(f, -.11, .11, y - .11, y + .11, .42, .44, lenses[i]);
                Box(f, -.14, .14, y + .11, y + .14, .42, .6, "trim");
            }
            Collider("Traffic light", f, -.2, .2, 0, 3.4, -.2, .45);
        }

        // A rubbish bag: a box, lying down when `fallen`.
        public void TrashBag(Frame f, double scale = 1, bool fallen = false)
        {
            string m = Pick("trashBag", "trashBag", "trashBagGreen");
            if (fallen) Box(f, -.24 * scale, .24 * scale, 0, .32 * scale, -.36 * scale, .36 * scale, m);
            else Box(f, -.24 * scale, .24 * scale, 0, .5 * scale, -.22 * scale, .22 * scale, m);
        }

        // A heap of bags around (x, z): some upright, some fallen over.
        public void BagPile(double x, double z, int count, double spread)
        {
            for (int i = 0; i < count; i++)
            {
                double px = x + Next(-spread, spread), pz = z + Next(-spread, spread);
                if (!Inside(px, pz, .4)) continue;
                TrashBag(On(px, pz, Next(0, 360)), Next(.85, 1.1), Random.NextDouble() < .3);
            }
            Occupy(Frame.At(x, 0, z), -spread - .3, spread + .3, -spread - .3, spread + .3);
        }

        // A wheelie bin; tipped onto its back with its lid open and its rubbish spilled out of the mouth (+z).
        public void WheelieBin(Frame f, bool tipped)
        {
            var body = tipped ? f.Local(0, .3, 0, 0, 90) : f;
            Box(body, -.28, .28, .05, .95, -.3, .3, "binGreen");
            Box(body, -.3, .3, .95, 1.0, -.34, .32, "trim");
            Box(body, -.3, -.24, 0, .2, -.35, -.15, "tyre");
            Box(body, .24, .3, 0, .2, -.35, -.15, "tyre");
            if (tipped) Collider("Wheelie bin", f, -.3, .3, 0, .6, -.15, 1.0);
            else Collider("Wheelie bin", f, -.3, .3, 0, 1.0, -.34, .34);
        }

        public void Crate(Frame f, double size)
        {
            Box(f, -size / 2, size / 2, 0, size * .8, -size / 2, size / 2, "wood");
            Box(f, -size / 2 - .01, size / 2 + .01, size * .35, size * .45, -size / 2 - .01, size / 2 + .01, "cardboard");
            Collider("Crate", f, -size / 2, size / 2, 0, size * .8, -size / 2, size / 2);
        }

        public void Cone(Frame f)
        {
            Box(f, -.2, .2, 0, .04, -.2, .2, "cone");
            Box(f, -.13, .13, .04, .28, -.13, .13, "cone");
            Box(f, -.1, .1, .28, .38, -.1, .1, "signWhite");
            Box(f, -.065, .065, .38, .62, -.065, .065, "cone");
        }

        // A suitcase burst open, clothes thrown about.
        public void Suitcase(double x, double z, double yaw)
        {
            var f = On(x, z, yaw);
            Box(f, -.26, .26, 0, .1, -.36, .36, "suitcase");
            Box(f, .3, .82, 0, .1, -.36, .36, "suitcase");
            Box(f, -.24, .24, .1, .14, -.34, .34, Pick("clothBlue", "clothRed"));
            for (int i = 0; i < 3; i++)
            {
                double px = x + Next(-1.1, 1.1), pz = z + Next(-1.1, 1.1);
                if (Inside(px, pz, .5) && Level(px, pz, .4)) Patch(px, pz, Next(.35, .5), Next(.4, .6), Next(0, 360), Pick("clothRed", "clothBlue"), LitterLift);
            }
        }

        public void LooseTyre(Frame f)
        {
            Box(f, -.34, .34, 0, .24, -.34, .34, "tyre");
            Box(f, -.16, .16, .24, .25, -.16, .16, "trim");
        }

        public void Debris(double x, double z, double radius, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double px = x + Next(-radius, radius), pz = z + Next(-radius, radius);
                if (!Inside(px, pz, .4) || !Level(px, pz, .3)) continue;
                double s = Next(.25, .5);
                Box(On(px, pz, Next(0, 360)), -s / 2, s / 2, 0, s * .4, -s * .4, s * .4, Pick("debris", "debris", "curb"));
            }
        }

        public void Bench(Frame f)
        {
            Box(f, -.9, .9, .42, .48, -.22, .22, "wood");
            Box(f, -.9, .9, .55, .9, -.27, -.22, "wood");
            foreach (double x in new[] { -.75, .75 }) Box(f, x - .04, x + .04, 0, .9, -.27, .2, "trim");
            Collider("Bench", f, -.9, .9, 0, .9, -.27, .22);
        }

        public void NewsBox(Frame f)
        {
            Box(f, -.24, .24, 0, 1.0, -.22, .22, "paintBlue");
            Box(f, -.17, .17, .55, .85, .22, .23, "glassCracked");
            Collider("Newspaper box", f, -.24, .24, 0, 1.0, -.22, .22);
        }

        // A 2-long concrete road barrier along x, with a band of red and white warning paint.
        public void Barrier(Frame f)
        {
            Box(f, -1, 1, 0, .25, -.3, .3, "barrier");
            Box(f, -1, 1, .25, .82, -.15, .15, "barrier");
            for (int i = 0; i < 4; i++)
                Box(f, -.9 + i * .45, -.7 + i * .45, .55, .68, -.165, .165, i % 2 == 0 ? "signRed" : "lineWhite");
            Collider("Barrier", f, -1, 1, 0, .82, -.3, .3);
        }

        // An oil drum; `fire` makes it a burnt-out fire barrel full of ash.
        public void Barrel(Frame f, bool fire)
        {
            Box(f, -.28, .28, 0, .88, -.28, .28, fire ? "rust" : Pick("paintBlue", "rust", "paintGreen"));
            foreach (double y in new[] { .28, .6 }) Box(f, -.295, .295, y, y + .04, -.295, .295, fire ? "burnt" : "trim");
            if (fire) Box(f, -.22, .22, .88, .89, -.22, .22, "dark");
            Collider("Barrel", f, -.3, .3, 0, .88, -.3, .3);
        }

        // A wooden pallet lying flat, or leaning against something when the frame is tilted.
        public void Pallet(Frame f)
        {
            for (int i = 0; i < 5; i++) Box(f, -.6 + i * .27, -.5 + i * .27, .1, .14, -.5, .5, "wood");
            foreach (double z in new[] { -.45, 0, .45 }) Box(f, -.6, .6, 0, .1, z - .05, z + .05, "wood");
        }

        public void Mattress(Frame f)
        {
            Box(f, -.45, .45, 0, .18, -.95, .95, "mattress");
            Box(f, -.46, .46, .06, .12, -.96, .96, "paintBeige");
        }

        // A street tree like the buildings' trees: a square trunk under a canopy of cubes, in a soil grate. Pass a frame
        // rolled over for a fallen tree: then the crown gets a collider too and there is no grate.
        public void Tree(Frame f, double size = 1, bool fallen = false)
        {
            double h = 2.2 * size, c = .95 * size;
            if (!fallen) Box(f, -.55, .55, 0, .012, -.55, .55, "soil");
            Box(f, -.13 * size, .13 * size, 0, h + .4 * size, -.13 * size, .13 * size, "bark");
            Box(f, -c, c, h, h + 1.5 * size, -c, c, "foliage");
            double s = .55 * size;
            Box(f.Local(Next(-.25, .25) * size, 0, Next(-.25, .25) * size, Next(10, 80)), -s, s, h + 1.1 * size, h + 2.0 * size, -s, s, "foliageDark");
            Collider("Tree", f, -.15 * size, .15 * size, 0, h, -.15 * size, .15 * size);
            if (fallen) Collider("Tree crown", f, -c, c, h, h + 1.5 * size, -c, c);
        }

        // A concrete planter with shrubs, along x.
        public void Planter(Frame f, double length = 1.4)
        {
            double l = length / 2;
            Box(f, -l, l, 0, .55, -.35, .35, "curb");
            Box(f, -l + .07, l - .07, .55, .57, -.28, .28, "soil");
            for (double x = -l + .32; x < l - .2; x += .45)
            {
                double s = Next(.15, .22);
                Box(f.Local(x, .55, Next(-.04, .04), Next(0, 90)), -s, s, 0, Next(.3, .55), -s, s, Pick("foliage", "foliageDark"));
            }
            Collider("Planter", f, -l, l, 0, .6, -.35, .35);
        }

        public void Bollard(Frame f)
        {
            Box(f, -.1, .1, 0, .9, -.1, .1, "trim");
            Box(f, -.11, .11, .7, .8, -.11, .11, "signYellow");
            Collider("Bollard", f, -.11, .11, 0, .9, -.11, .11);
        }

        // A bus shelter open toward +z: a dark glass back and one side panel in a metal frame, a roof, a bench, and the
        // stop sign on its own pole past the +x end. Origin at the middle of the front edge, on the sidewalk.
        public void BusStop(Frame f)
        {
            const double l = 1.7, back = -1.4, roof = 2.4;
            foreach (double x in new[] { -l, l })
            {
                Box(f, x - .05, x + .05, 0, roof, back - .05, back + .05, "lampMetal");
                Box(f, x - .05, x + .05, 0, roof, -.25, -.15, "lampMetal");
                Collider("Bus shelter post", f, x - .05, x + .05, 0, roof, -.25, -.15);
            }
            Box(f, -l, l, .15, roof - .1, back - .02, back + .02, "glass");
            Box(f, -l - .02, -l + .02, .15, roof - .1, back + .05, -.25, "glass");
            Box(f, -l - .15, l + .15, roof, roof + .12, back - .15, .1, "lampMetal");
            Box(f, -1.2, 1.2, .42, .48, back + .06, back + .46, "wood");
            foreach (double x in new[] { -1.05, 1.05 }) Box(f, x - .05, x + .05, 0, .42, back + .1, back + .42, "trim");
            Box(f, l + .45, l + .52, 0, 2.6, -.2, -.13, "signPole");
            Box(f, l + .3, l + .67, 2.05, 2.55, -.13, -.11, "paintBlue");
            Box(f, l + .38, l + .59, 2.2, 2.42, -.11, -.1, "signWhite");
            Collider("Bus shelter", f, -l - .05, l + .05, 0, roof, back - .06, back + .06);
            Collider("Bus shelter side", f, -l - .06, -l + .06, 0, roof, back + .06, -.25);
            Collider("Bus shelter bench", f, -1.2, 1.2, 0, .48, back + .06, back + .46);
            Collider("Bus stop sign", f, l + .38, l + .59, 0, 2.6, -.22, -.1);
        }

        // A 6 m shipping container along x with ribbed sides and its doors at +x. Origin under the middle of its floor.
        public void Container(Frame f, string paint)
        {
            const double l = 3.03, w = 1.22, h = 2.59;
            Box(f, -l, l, 0, h, -w, w, paint);
            for (double x = -l + .35; x < l - .2; x += .6)
                foreach (double s in new[] { -1.0, 1.0 })
                    Box(f, x - .06, x + .06, .08, h - .08, s < 0 ? -w - .03 : w, s < 0 ? -w : w + .03, paint);
            foreach (double z in new[] { -.6, 0, .6 }) Box(f, l, l + .04, .1, h - .1, z - .03, z + .03, "trim");
            Collider("Container", f, -l, l + .04, 0, h, -w - .03, w + .03);
        }

        // Tall concrete wall panels (T-walls) along x from x0 to x1, each on a foot: a 4.9 high wall nobody climbs.
        public void ConcreteWall(Frame f, double x0, double x1)
        {
            int n = (int)Math.Ceiling((x1 - x0) / 1.25 - 1e-6);
            double width = (x1 - x0) / n;
            for (int i = 0; i < n; i++)
            {
                double a = x0 + i * width;
                Box(f, a + .02, a + width - .02, 0, 4.9 + Next(-.05, .05), -.15, .15, "barrier");
                Box(f, a + .02, a + width - .02, 0, .45, -.6, .6, "barrier");
            }
            Collider("Wall", f, x0, x1, 0, 4.9, -.15, .15);
            Collider("Wall foot", f, x0, x1, 0, .45, -.6, .6);
        }

        // A wall of sandbags along x, `rows` bags high (0.25 each), staggered and a little uneven.
        public void Sandbags(Frame f, double x0, double x1, int rows)
        {
            for (int r = 0; r < rows; r++)
                for (double x = x0 + (r % 2) * .3; x < x1 - .1; x += .6)
                {
                    double e = Math.Min(x + .57, x1), m = (x + e) / 2;
                    Box(f.Local(m, r * .25, Next(-.03, .03), Next(-4, 4)), x - m, e - m, 0, .25, -.2, .2, "sandbag");
                }
            Collider("Sandbags", f, x0, x1, 0, rows * .25, -.24, .24);
        }

        // A shopping cart facing +z (handle at -z).
        public void ShoppingCart(Frame f)
        {
            Box(f, -.27, .27, .4, .43, -.42, .45, "lampMetal");
            Box(f, -.28, -.26, .43, .92, -.42, .45, "lampMetal");
            Box(f, .26, .28, .43, .92, -.42, .45, "lampMetal");
            Box(f, -.27, .27, .43, .92, .43, .45, "lampMetal");
            Box(f, -.27, .27, .43, .92, -.44, -.42, "lampMetal");
            Box(f, -.27, .27, .95, 1.0, -.6, -.55, "signRed");
            foreach (double x in new[] { -.22, .22 })
                foreach (double z in new[] { -.36, .38 })
                {
                    Box(f, x - .02, x + .02, .08, .4, z - .02, z + .02, "lampMetal");
                    Box(f, x - .03, x + .03, 0, .08, z - .05, z + .05, "tyre");
                }
            Collider("Shopping cart", f, -.28, .28, 0, 1.0, -.6, .45);
        }

        // A wheeled stretcher, along z.
        public void Stretcher(Frame f)
        {
            Box(f, -.3, .3, .55, .62, -1.0, 1.0, "lampMetal");
            Box(f, -.28, .28, .62, .72, -.95, .95, "mattress");
            foreach (double z in new[] { -.85, .85 }) Box(f, -.25, .25, .1, .55, z - .03, z + .03, "lampMetal");
            Collider("Stretcher", f, -.3, .3, 0, .72, -1.0, 1.0);
        }

        // A sofa facing +z, dumped in the street.
        public void Sofa(Frame f, string material)
        {
            Box(f, -1.0, 1.0, 0, .42, -.45, .45, material);
            Box(f, -1.0, 1.0, .42, .85, -.45, -.22, material);
            foreach (double x in new[] { -1.0, .82 }) Box(f, x, x + .18, .42, .62, -.45, .45, material);
            Collider("Sofa", f, -1.0, 1.0, 0, .85, -.45, .45);
        }

        public void PortableToilet(Frame f)
        {
            Box(f, -.58, .58, 0, 2.2, -.58, .58, "paintBlue");
            Box(f, -.62, .62, 2.2, 2.32, -.62, .62, "signWhite");
            Box(f, -.4, .4, .08, 2.0, .58, .6, "clothBlue");
            Collider("Portable toilet", f, -.62, .62, 0, 2.32, -.62, .62);
        }

        // A site generator or compressor: a painted box on a frame.
        public void Generator(Frame f)
        {
            Box(f, -.5, .5, 0, .1, -.32, .32, "trim");
            Box(f, -.45, .45, .1, .75, -.28, .28, "paintMustard");
            Box(f, -.5, -.44, .1, .85, -.32, .32, "trim");
            Box(f, .44, .5, .1, .85, -.32, .32, "trim");
            Collider("Generator", f, -.5, .5, 0, .85, -.32, .32);
        }

        // A tarp stretched flat over four poles, `width` along x and `depth` along z, high enough to walk under.
        public void TarpShelter(Frame f, double width, double depth, string material)
        {
            double x = width / 2 - .1, z = depth / 2 - .1;
            foreach (double sx in new[] { -x, x })
                foreach (double sz in new[] { -z, z })
                {
                    Box(f, sx - .04, sx + .04, 0, 2.05, sz - .04, sz + .04, "wood");
                    Collider("Tarp pole", f, sx - .05, sx + .05, 0, 2.05, sz - .05, sz + .05);
                }
            Box(f, -width / 2, width / 2, 2.05, 2.08, -depth / 2, depth / 2, material);
        }

        // A heap of dug-up earth: stacked turned boxes.
        public void DirtPile(Frame f, double size)
        {
            Box(f, -size, size, 0, size * .35, -size * .8, size * .8, "dirt");
            Box(f.Local(Next(-.1, .1), 0, Next(-.1, .1), Next(10, 40)), -size * .6, size * .6, size * .35, size * .65, -size * .5, size * .5, "dirt");
            Box(f.Local(0, 0, 0, Next(50, 80)), -size * .3, size * .3, size * .65, size * .85, -size * .25, size * .25, "dirt");
            Collider("Dirt pile", f, -size, size, 0, size * .35, -size * .8, size * .8);
        }
    }
}
