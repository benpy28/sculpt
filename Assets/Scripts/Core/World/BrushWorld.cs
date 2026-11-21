using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>What a brush is, for the look and for the rules (which ones can ever break).</summary>
    public enum BrushKind : byte
    {
        /// <summary>Outdoor ground slabs and the course floor.</summary>
        Ground = 0,
        /// <summary>Building floors and ceilings. Unbreakable.</summary>
        Floor = 1,
        /// <summary>Columns and beams. Unbreakable.</summary>
        Frame = 2,
        /// <summary>Stairs: collide as a smooth ramp, drawn as steps. Unbreakable.</summary>
        Stairs = 3,
        /// <summary>Plain ramps. Unbreakable.</summary>
        Ramp = 4,
        /// <summary>Plain solid walls that never break (course walls, map edges).</summary>
        Wall = 5,
        /// <summary>Painted markings (finish pads). Never solid.</summary>
        Marker = 6,
    }

    /// <summary>
    /// One axis-aligned solid: a box, or a wedge (ramp) whose top rises from Min.Y at one end to Max.Y at the other,
    /// along <see cref="Rise"/>. Wedges are solid under the slope only. Everything sits on the 90 degree grid.
    /// </summary>
    public struct Brush
    {
        public Aabb Bounds;
        public BrushKind Kind;
        /// <summary>True for a wedge. Rise is the horizontal direction the slope climbs toward (never Up or Down).</summary>
        public bool IsRamp;
        public Dir Rise;
        /// <summary>False once a brush is gone (breakable tiles later). Gone brushes never collide or stop bullets.</summary>
        public bool Solid;

        public static Brush Box(Aabb b, BrushKind kind) => new Brush { Bounds = b, Kind = kind, Solid = true };

        public static Brush Wedge(Aabb b, Dir rise, BrushKind kind)
        {
            if (rise == Dir.Up || rise == Dir.Down) throw new ArgumentException("A ramp rises along a horizontal axis.", nameof(rise));
            return new Brush { Bounds = b, Kind = kind, IsRamp = true, Rise = rise, Solid = true };
        }

        /// <summary>0 at the low end, 1 at the high end, for a horizontal coordinate along the rise axis.</summary>
        public double RiseFraction(double x, double z)
        {
            Aabb b = Bounds;
            switch (Rise)
            {
                case Dir.East: return (x - b.Min.X) / (b.Max.X - b.Min.X);
                case Dir.West: return (b.Max.X - x) / (b.Max.X - b.Min.X);
                case Dir.North: return (z - b.Min.Z) / (b.Max.Z - b.Min.Z);
                default: return (b.Max.Z - z) / (b.Max.Z - b.Min.Z);
            }
        }

        /// <summary>Unit normal of the slope (points up and toward the low end).</summary>
        public Vec3d SlopeNormal
        {
            get
            {
                Aabb b = Bounds;
                double rise = b.Max.Y - b.Min.Y;
                double run = Rise == Dir.East || Rise == Dir.West ? b.Max.X - b.Min.X : b.Max.Z - b.Min.Z;
                Vec3d up = Rise.ToVec();
                return new Vec3d(-up.X * rise, run, -up.Z * rise).Normalized;
            }
        }
    }

    /// <summary>
    /// The building world: exact boxes and wedges in a uniform spatial hash. Answers the same questions as the sub-cell
    /// terrain (<see cref="ITerrain"/>), so movement, bullets and aiming need no changes. No chamfers: a 1.5 m hole is a
    /// 1.5 m hole. Touching is never overlapping (same epsilon as <see cref="Aabb"/>).
    /// </summary>
    public sealed class BrushWorld : ITerrain
    {
        public const double CellSize = 3.0;
        private const double Eps = Aabb.Epsilon;

        private readonly List<Brush> _brushes = new List<Brush>();
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        [ThreadStatic] private static List<int> _scratch;
        [ThreadStatic] private static HashSet<int> _seen;

        public int Count => _brushes.Count;
        public Brush this[int i] => _brushes[i];
        public IReadOnlyList<Brush> Brushes => _brushes;

        /// <summary>Adds a brush and returns its index (stable for the life of the world).</summary>
        public int Add(Brush b)
        {
            if (!(b.Bounds.Max.X > b.Bounds.Min.X && b.Bounds.Max.Y > b.Bounds.Min.Y && b.Bounds.Max.Z > b.Bounds.Min.Z))
                throw new ArgumentException("A brush needs a positive size on every axis.");
            int id = _brushes.Count;
            _brushes.Add(b);
            CellRange(b.Bounds, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x, y, z);
                        if (!_cells.TryGetValue(k, out var list)) _cells[k] = list = new List<int>(4);
                        list.Add(id);
                    }
            return id;
        }

        public int AddBox(Vec3d min, Vec3d max, BrushKind kind) => Add(Brush.Box(new Aabb(min, max), kind));
        public int AddRamp(Vec3d min, Vec3d max, Dir rise, BrushKind kind) => Add(Brush.Wedge(new Aabb(min, max), rise, kind));

        /// <summary>Switches a brush's collision on or off (for breakable tiles).</summary>
        public void SetSolid(int id, bool solid)
        {
            Brush b = _brushes[id];
            b.Solid = solid;
            _brushes[id] = b;
        }

        // ------------------------------------------------------------------ queries

        public bool BoxOverlapsSolid(Aabb box)
        {
            List<int> near = Near(box);
            for (int i = 0; i < near.Count; i++)
            {
                Brush b = _brushes[near[i]];
                if (b.Solid && Overlaps(b, box)) return true;
            }
            return false;
        }

        public bool GroundContact(Aabb box, double probe, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            Aabb probed = box.Translate(new Vec3d(0, -probe, 0));
            List<int> near = Near(probed);
            bool any = false;
            double bestY = -1;
            for (int i = 0; i < near.Count; i++)
            {
                Brush b = _brushes[near[i]];
                if (!b.Solid || !Overlaps(b, probed)) continue;
                any = true;
                Vec3d n = ContactNormal(b, box);
                if (n.Y > 0.5 && n.Y > bestY + 1e-9)
                {
                    bestY = n.Y;
                    normal = n;
                }
            }
            return any;
        }

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance)
        {
            var result = new TerrainHit();
            Vec3d dir = direction.Normalized;
            if (dir.LengthSquared == 0 || maxDistance <= 0) return result;
            Vec3d end = origin + dir * maxDistance;
            var span = new Aabb(
                new Vec3d(Math.Min(origin.X, end.X), Math.Min(origin.Y, end.Y), Math.Min(origin.Z, end.Z)),
                new Vec3d(Math.Max(origin.X, end.X), Math.Max(origin.Y, end.Y), Math.Max(origin.Z, end.Z)));
            // Long rays over a big world: walk the hash cells along the ray instead of the whole span.
            List<int> near = span.Max.X - span.Min.X + span.Max.Y - span.Min.Y + span.Max.Z - span.Min.Z > 8 * CellSize
                ? NearRay(origin, dir, maxDistance)
                : Near(span.Expand(0.01));
            double best = maxDistance;
            for (int i = 0; i < near.Count; i++)
            {
                int id = near[i];
                Brush b = _brushes[id];
                if (!b.Solid) continue;
                if (!Clip(b, origin, dir, best, out double t, out Vec3d n)) continue;
                if (result.Hit && t >= best) continue;
                best = t;
                result.Hit = true;
                result.Distance = t;
                result.Point = origin + dir * t;
                result.Normal = n;
                result.Brush = id + 1;
            }
            return result;
        }

        // ------------------------------------------------------------------ exact shape tests

        /// <summary>Do the interiors of the brush and the box overlap?</summary>
        public static bool Overlaps(in Brush b, Aabb box)
        {
            if (!b.Bounds.Overlaps(box)) return false;
            if (!b.IsRamp) return true;
            // Solid = under the slope. The box reaches lowest at its Min.Y and the slope is highest at the box's
            // extent toward the high end, clamped into the wedge.
            return box.Min.Y < SurfaceHeightUnder(b, box) - Eps;
        }

        /// <summary>Highest slope height over the part of the wedge's footprint the box covers.</summary>
        private static double SurfaceHeightUnder(in Brush b, Aabb box)
        {
            Aabb w = b.Bounds;
            double f;
            switch (b.Rise)
            {
                case Dir.East: f = (Math.Min(box.Max.X, w.Max.X) - w.Min.X) / (w.Max.X - w.Min.X); break;
                case Dir.West: f = (w.Max.X - Math.Max(box.Min.X, w.Min.X)) / (w.Max.X - w.Min.X); break;
                case Dir.North: f = (Math.Min(box.Max.Z, w.Max.Z) - w.Min.Z) / (w.Max.Z - w.Min.Z); break;
                default: f = (w.Max.Z - Math.Max(box.Min.Z, w.Min.Z)) / (w.Max.Z - w.Min.Z); break;
            }
            f = Math.Max(0, Math.Min(1, f));
            return w.Min.Y + (w.Max.Y - w.Min.Y) * f;
        }

        /// <summary>The normal the box rests on: straight up on a box top, the slope normal on a wedge.</summary>
        private static Vec3d ContactNormal(in Brush b, Aabb box)
        {
            if (!b.IsRamp) return box.Min.Y >= b.Bounds.Max.Y - 0.05 ? new Vec3d(0, 1, 0) : new Vec3d(0, 0, 0);
            // Past the high end the box sits on the crest line: that still reads as the slope (walkable).
            return b.SlopeNormal;
        }

        /// <summary>Ray against one convex brush (the bounds' six planes plus the slope plane for a wedge).</summary>
        private static bool Clip(in Brush b, Vec3d o, Vec3d d, double maxT, out double tHit, out Vec3d nHit)
        {
            double tEnter = 0, tExit = maxT;
            nHit = default;
            tHit = 0;
            bool entered = false;
            Aabb w = b.Bounds;
            for (int axis = 0; axis < 3; axis++)
            {
                double oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                double da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                double lo = axis == 0 ? w.Min.X : axis == 1 ? w.Min.Y : w.Min.Z;
                double hi = axis == 0 ? w.Max.X : axis == 1 ? w.Max.Y : w.Max.Z;
                if (Math.Abs(da) < 1e-12)
                {
                    if (oa <= lo || oa >= hi) return false;
                    continue;
                }
                double t0 = (lo - oa) / da, t1 = (hi - oa) / da;
                Vec3d n0 = Axis(axis, -1), n1 = Axis(axis, 1);
                if (t0 > t1) { (t0, t1) = (t1, t0); (n0, n1) = (n1, n0); }
                if (t0 > tEnter) { tEnter = t0; nHit = n0; entered = true; }
                if (t1 < tExit) tExit = t1;
                if (tEnter > tExit) return false;
            }
            if (b.IsRamp)
            {
                // Slope plane: n . p <= n . p0, with p0 on the low edge.
                Vec3d n = b.SlopeNormal;
                Vec3d p0 = LowEdgePoint(b);
                double dist = n.Dot(o - p0);
                double dn = n.Dot(d);
                if (Math.Abs(dn) < 1e-12)
                {
                    if (dist >= 0) return false;
                }
                else
                {
                    double t = -dist / dn;
                    if (dn < 0) { if (t > tEnter) { tEnter = t; nHit = n; entered = true; } }
                    else if (t < tExit) tExit = t;
                    if (tEnter > tExit) return false;
                }
            }
            if (!entered)
            {
                // The ray starts inside the brush: report a hit right at the origin.
                tHit = 0;
                nHit = -d;
                return true;
            }
            tHit = tEnter;
            return tEnter <= maxT;
        }

        private static Vec3d LowEdgePoint(in Brush b)
        {
            Aabb w = b.Bounds;
            switch (b.Rise)
            {
                case Dir.East: return new Vec3d(w.Min.X, w.Min.Y, w.Min.Z);
                case Dir.West: return new Vec3d(w.Max.X, w.Min.Y, w.Min.Z);
                case Dir.North: return new Vec3d(w.Min.X, w.Min.Y, w.Min.Z);
                default: return new Vec3d(w.Min.X, w.Min.Y, w.Max.Z);
            }
        }

        private static Vec3d Axis(int axis, double s) =>
            axis == 0 ? new Vec3d(s, 0, 0) : axis == 1 ? new Vec3d(0, s, 0) : new Vec3d(0, 0, s);

        // ------------------------------------------------------------------ spatial hash

        private List<int> Near(Aabb box)
        {
            var list = _scratch ??= new List<int>();
            var seen = _seen ??= new HashSet<int>();
            list.Clear();
            seen.Clear();
            CellRange(box, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        if (_cells.TryGetValue(Key(x, y, z), out var ids))
                            for (int i = 0; i < ids.Count; i++)
                                if (seen.Add(ids[i])) list.Add(ids[i]);
            return list;
        }

        private List<int> NearRay(Vec3d o, Vec3d d, double maxT)
        {
            var list = _scratch ??= new List<int>();
            var seen = _seen ??= new HashSet<int>();
            list.Clear();
            seen.Clear();
            // Sample the ray every half cell and gather a one-cell neighbourhood: conservative and cheap.
            int steps = (int)Math.Ceiling(maxT / (CellSize * 0.5)) + 1;
            for (int s = 0; s <= steps; s++)
            {
                Vec3d p = o + d * Math.Min(maxT, s * CellSize * 0.5);
                int cx = Cell(p.X), cy = Cell(p.Y), cz = Cell(p.Z);
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int y = cy - 1; y <= cy + 1; y++)
                        for (int z = cz - 1; z <= cz + 1; z++)
                            if (_cells.TryGetValue(Key(x, y, z), out var ids))
                                for (int i = 0; i < ids.Count; i++)
                                    if (seen.Add(ids[i])) list.Add(ids[i]);
            }
            return list;
        }

        private static int Cell(double v) => (int)Math.Floor(v / CellSize);

        private static void CellRange(Aabb b, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
        {
            x0 = Cell(b.Min.X - Eps); x1 = Cell(b.Max.X + Eps);
            y0 = Cell(b.Min.Y - Eps); y1 = Cell(b.Max.Y + Eps);
            z0 = Cell(b.Min.Z - Eps); z1 = Cell(b.Max.Z + Eps);
        }

        private static long Key(int x, int y, int z) =>
            ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }
}
