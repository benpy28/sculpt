using System;

namespace Sculpt.Core
{
    /// <summary>A rectangle on the map's ground plane (metres, map coordinates).</summary>
    public struct Rect2
    {
        public double X0, Z0, X1, Z1;
        public Rect2(double x0, double z0, double x1, double z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
        public double CX => (X0 + X1) / 2;
        public double CZ => (Z0 + Z1) / 2;
        public bool Contains(double x, double z, double margin = 0) => x >= X0 + margin && x <= X1 - margin && z >= Z0 + margin && z <= Z1 - margin;
        public bool Overlaps(Rect2 o) => o.X1 > X0 && o.X0 < X1 && o.Z1 > Z0 && o.Z0 < Z1;
        public Rect2 Grow(double d) => new Rect2(X0 - d, Z0 - d, X1 + d, Z1 + d);
        public static Rect2 Of(Aabb b) => new Rect2(b.Min.X, b.Min.Z, b.Max.X, b.Max.Z);
        public override string ToString() => $"[{X0:0.0}, {Z0:0.0} .. {X1:0.0}, {Z1:0.0}]";
    }

    /// <summary>
    /// The battle royale map cut into nine segments, a 3 x 3 grid of 435.2 m squares (Ben 2026-11-28: map v3 was "super
    /// boring"; each segment is designed by its own thread). This file is the contract every segment builds to: where the
    /// segments are, the seams between them, the roads that cross the seams, the spawn region each outer segment holds,
    /// and the budgets. Segment ids are gx + 3 gz (gx west to east, gz south to north), so 4 is the centre.
    ///
    /// Seams: within <see cref="SeamBand"/> of a border with another segment the ground eases (C1, a smoothstep) to the
    /// shared ground <see cref="BrGround.Shared"/>, so two segments' ground always meets without a step or a crease.
    /// Nothing is built within <see cref="BorderKeepOut"/> of an inner border (buildings, walls, footings and their
    /// aprons, chests, items, props), so the 24 m strip along every inner border is an open street from one end of the
    /// map to the other and no footing's bank ever reaches into a neighbour. Each inner border has one road port at the
    /// middle of its playable part: a <see cref="PortWidth"/> lane running <see cref="PortDepth"/> into both segments
    /// that stays clear, where a segment's own roads meet the border street.
    ///
    /// Fairness: each of the eight outer segments holds one spawn region (<see cref="BrSpawnKit"/>, built by the
    /// framework, the same building everywhere) facing its home anchor; every anchor is <see cref="AnchorRing"/> from the
    /// map's centre, every pad <see cref="BrSpawnKit.PadToAnchor"/> from its anchor, the ground between is the same for
    /// every pad, the four pad lanes stay clear, and every segment puts exactly <see cref="HomeChests"/> chests within
    /// <see cref="HomeRadius"/> of its anchor (its home POI). Beyond that each segment is its own design (tested budgets).
    /// </summary>
    public static class BrLayout
    {
        public const int Across = 3, Count = 9, Centre = 4;
        /// <summary>One segment's side: 1,305.6 m / 3.</summary>
        public static double SegSize => BrMap.Size / Across;
        public static double MapCentre => BrMap.Size / 2;

        public static readonly string[] Names = { "South West", "South", "South East", "West", "Centre", "East", "North West", "North", "North East" };
        /// <summary>File and class stem of each segment (Assets/Scripts/Core/World/BrSegments/Segments/&lt;Stem&gt;Segment.cs).</summary>
        public static readonly string[] Stems = { "SouthWest", "South", "SouthEast", "West", "Centre", "East", "NorthWest", "North", "NorthEast" };

        // ---- Seams and borders.
        /// <summary>The ground eases to the shared ground over this distance from an inner border.</summary>
        public const double SeamBand = 40;
        /// <summary>Nothing is built this close to an inner border (so the border strip is a 24 m street).</summary>
        public const double BorderKeepOut = 12;
        /// <summary>Keep everything built this far inside the rim's start (the play area's edge).</summary>
        public const double RimKeepOut = 6;
        /// <summary>A road port's lane width and how far it runs into each segment, clear.</summary>
        public const double PortWidth = 12, PortDepth = 40;

        // ---- Spawn regions and home POIs.
        /// <summary>Every home anchor's distance from the map's centre.</summary>
        public const double AnchorRing = 372;
        /// <summary>The home POI: chests within this of the anchor count toward <see cref="HomeChests"/>; the ground in it is level at <see cref="BrGround.Base"/>.</summary>
        public const double HomeRadius = 45;
        public const int HomeChests = 10;
        /// <summary>A pad lane (pad's loot chest to the home plaza) is this wide and stays clear.</summary>
        public const double PadLaneWidth = 6;

        // ---- Budgets per segment (tested).
        public const int MaxTiles = 55000, MaxTilesCentre = 80000;
        public const int MinChests = 140, MaxChests = 230, MaxChestsCentre = 320;
        public const int MaxItems = 40;
        public const int MaxProps = 900;

        /// <summary>The order the outer segments' regions are listed in (compass order, east first, counter-clockwise).</summary>
        public static readonly int[] RegionSegments = { 5, 8, 7, 6, 3, 0, 1, 2 };

        public static int Gx(int id) => id % Across;
        public static int Gz(int id) => id / Across;

        /// <summary>The segment's square (map coordinates).</summary>
        public static Rect2 Bounds(int id)
        {
            double s = SegSize;
            return new Rect2(Gx(id) * s, Gz(id) * s, (Gx(id) + 1) * s, (Gz(id) + 1) * s);
        }

        /// <summary>The play area: inside the rim's start.</summary>
        public static Rect2 Play => new Rect2(MapCentre - BrMap.RimStart, MapCentre - BrMap.RimStart, MapCentre + BrMap.RimStart, MapCentre + BrMap.RimStart);

        /// <summary>Where a segment may build: its square less the border keep-out on its inner sides and the rim keep-out outside.</summary>
        public static Rect2 Buildable(int id)
        {
            Rect2 b = Bounds(id), p = Play;
            int gx = Gx(id), gz = Gz(id);
            return new Rect2(
                gx == 0 ? p.X0 + RimKeepOut : b.X0 + BorderKeepOut,
                gz == 0 ? p.Z0 + RimKeepOut : b.Z0 + BorderKeepOut,
                gx == Across - 1 ? p.X1 - RimKeepOut : b.X1 - BorderKeepOut,
                gz == Across - 1 ? p.Z1 - RimKeepOut : b.Z1 - BorderKeepOut);
        }

        /// <summary>The segment that owns a map point (points off the map go to the nearest segment).</summary>
        public static int Owner(double x, double z)
        {
            double s = SegSize;
            int gx = Math.Max(0, Math.Min(Across - 1, (int)Math.Floor(x / s)));
            int gz = Math.Max(0, Math.Min(Across - 1, (int)Math.Floor(z / s)));
            return gx + Across * gz;
        }

        /// <summary>Distance from a point to the nearest inner border of segment <paramref name="id"/> (infinite if it has none near).</summary>
        public static double ToInnerBorder(int id, double x, double z)
        {
            Rect2 b = Bounds(id);
            int gx = Gx(id), gz = Gz(id);
            double d = double.PositiveInfinity;
            if (gx > 0) d = Math.Min(d, Math.Abs(x - b.X0));
            if (gx < Across - 1) d = Math.Min(d, Math.Abs(b.X1 - x));
            if (gz > 0) d = Math.Min(d, Math.Abs(z - b.Z0));
            if (gz < Across - 1) d = Math.Min(d, Math.Abs(b.Z1 - z));
            return d;
        }

        // ---------------------------------------------------------------- road ports

        /// <summary>A road port: where a lane crosses the inner border between segments A and B.</summary>
        public struct Port
        {
            public int A, B;
            /// <summary>The crossing point on the border.</summary>
            public double X, Z;
            /// <summary>True if the border runs along Z (the lane runs along X).</summary>
            public bool BorderAlongZ;
            /// <summary>The clear lane inside segment <paramref name="id"/> (A or B).</summary>
            public Rect2 LaneIn(int id)
            {
                double h = PortWidth / 2;
                bool aSide = id == A;
                if (BorderAlongZ) return aSide ? new Rect2(X - PortDepth, Z - h, X, Z + h) : new Rect2(X, Z - h, X + PortDepth, Z + h);
                return aSide ? new Rect2(X - h, Z - PortDepth, X + h, Z) : new Rect2(X - h, Z, X + h, Z + PortDepth);
            }
        }

        private static Port[] _ports;

        /// <summary>The twelve road ports (one per inner border), each at the middle of the border's playable part.</summary>
        public static Port[] Ports
        {
            get
            {
                if (_ports != null) return _ports;
                var list = new System.Collections.Generic.List<Port>();
                Rect2 p = Play;
                for (int gz = 0; gz < Across; gz++)
                    for (int gx = 0; gx < Across; gx++)
                    {
                        int id = gx + Across * gz;
                        Rect2 b = Bounds(id);
                        double z0 = Math.Max(b.Z0, p.Z0), z1 = Math.Min(b.Z1, p.Z1), x0 = Math.Max(b.X0, p.X0), x1 = Math.Min(b.X1, p.X1);
                        if (gx < Across - 1) list.Add(new Port { A = id, B = id + 1, X = b.X1, Z = (z0 + z1) / 2, BorderAlongZ = true });
                        if (gz < Across - 1) list.Add(new Port { A = id, B = id + Across, X = (x0 + x1) / 2, Z = b.Z1, BorderAlongZ = false });
                    }
                _ports = list.ToArray();
                return _ports;
            }
        }

        // ---------------------------------------------------------------- home anchors

        /// <summary>True for the four corner segments.</summary>
        public static bool IsCorner(int id) => id != Centre && Gx(id) != 1 && Gz(id) != 1;

        /// <summary>
        /// The symmetry (as in <see cref="BrMap.Sym"/>: (u, v) -> (a u + b v, c u + d v) about the map's centre) that takes
        /// the spawn kit's template to this segment's region: the edge template sits in the east segment, the corner
        /// template in the north-east one. Null for the centre.
        /// </summary>
        public static int[] KitSymmetry(int id)
        {
            switch (id)
            {
                case 5: return new[] { 1, 0, 0, 1 };      // east: the edge template
                case 7: return new[] { 0, -1, 1, 0 };     // north: a quarter turn
                case 3: return new[] { -1, 0, 0, -1 };    // west: a half turn
                case 1: return new[] { 0, 1, -1, 0 };     // south: three quarter turns
                case 8: return new[] { 1, 0, 0, 1 };      // north east: the corner template
                case 6: return new[] { -1, 0, 0, 1 };     // north west: mirrored across x
                case 0: return new[] { -1, 0, 0, -1 };    // south west: a half turn
                case 2: return new[] { 1, 0, 0, -1 };     // south east: mirrored across z
                default: return null;
            }
        }

        /// <summary>The template anchor (relative to the map's centre) the segment's kit is stamped from.</summary>
        public static void TemplateAnchor(bool corner, out double u, out double v)
        {
            if (corner) { u = v = AnchorRing / Math.Sqrt(2); }
            else { u = AnchorRing; v = 0; }
        }

        /// <summary>The segment's home anchor in map coordinates (its home POI's centre, feet height <see cref="BrGround.Base"/>).</summary>
        public static Vec3d Anchor(int id)
        {
            int[] m = KitSymmetry(id);
            if (m == null) return new Vec3d(MapCentre, BrGround.Base, MapCentre);
            TemplateAnchor(IsCorner(id), out double u, out double v);
            return new Vec3d(MapCentre + m[0] * u + m[1] * v, BrGround.Base, MapCentre + m[2] * u + m[3] * v);
        }

        /// <summary>The direction (unit, ground plane) from a segment's anchor toward its spawn region.</summary>
        public static void TowardKit(int id, out double dx, out double dz)
        {
            int[] m = KitSymmetry(id);
            dx = m == null ? 0 : m[0];
            dz = m == null ? 0 : m[2];
        }
    }
}
