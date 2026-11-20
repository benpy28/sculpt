using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The surface kernel: turns sub-cell occupancy into the one chamfered shape used for rendering, player
    /// collision and bullets ("what you see is what you hit").
    ///
    /// The world is cut into dual cells: one 0.3 m cube around every sub-cell lattice vertex, whose 8 corners are
    /// the centres of the 8 sub-cells meeting at that vertex. A dual cell's solid depends only on which of those 8
    /// are occupied (256 configurations) and is a single convex polytope:
    ///   solid = the convex hull of the occupied corners, grown by an L1 ball of radius h = 0.15 m (half a sub-cell),
    ///   clipped to the dual cell, written as an intersection of half-spaces whose normals are the 26 directions
    ///   (6 axes, 12 edge diagonals, 8 corner diagonals).
    /// Results: flat floors and walls sit exactly on sub-cell faces; every convex edge gets a 45 degree chamfer
    /// 0.15 m wide and every concave edge a matching fillet; a one-sub-cell step (0.3 m) becomes a 45 degree slope,
    /// so a staircase of them is a smooth ramp; diagonal contacts connect (no hairline gaps for bullets to leak).
    /// Fixed tie-breaks: everything is integer and table driven, so every machine builds the same shape.
    ///
    /// Units inside a dual cell: "h-units" (1 = 0.15 m), cell spans [0, 2] on each axis. Exact vertex maths uses
    /// twelfths of h ("P12"), which every vertex lands on (plane triples with normals in {-1, 0, 1} have det at most 4).
    /// Corner bit b = dx + 2 dy + 4 dz is the sub-cell at (vertex - 1 + (dx, dy, dz)).
    /// </summary>
    public static class TerrainShape
    {
        public const int ConfigCount = 256;
        public const double H = SubGrid.SubSize * 0.5;

        /// <summary>The 13 axis directions (unnormalised). Direction k of 26 is +Axes[k] for k &lt; 13, else -Axes[k - 13].</summary>
        public static readonly Int3[] Axes =
        {
            new Int3(1, 0, 0), new Int3(0, 1, 0), new Int3(0, 0, 1),
            new Int3(1, 1, 0), new Int3(1, -1, 0), new Int3(1, 0, 1), new Int3(1, 0, -1), new Int3(0, 1, 1), new Int3(0, 1, -1),
            new Int3(1, 1, 1), new Int3(1, 1, -1), new Int3(1, -1, 1), new Int3(1, -1, -1),
        };

        public static Int3 Dir26(int k) => k < 13 ? Axes[k] : -Axes[k - 13];

        /// <summary>One flat face of a dual cell's polytope.</summary>
        public sealed class Face
        {
            /// <summary>Direction index 0..25 (outward normal).</summary>
            public int Dir;
            /// <summary>Unit outward normal.</summary>
            public Vec3d Normal;
            /// <summary>Vertices in h-units, ordered so (v1 - v0) x (v2 - v0) points outward (right-handed maths).</summary>
            public Vec3d[] Verts;
            /// <summary>Same vertices in twelfths of h.</summary>
            public Int3[] P12;
            /// <summary>True if the face lies on the dual cell's own boundary (shared with the neighbour cell).</summary>
            public bool OnBoundary;
            /// <summary>Boundary side 0..5 (+X, -X, +Y, -Y, +Z, -Z), or -1.</summary>
            public int Side = -1;
        }

        /// <summary>Precomputed polytope for one corner configuration.</summary>
        public sealed class Poly
        {
            public int Config;
            public bool Empty;
            public bool Full;
            /// <summary>Tight interval of the polytope along each of the 13 axes (unnormalised dot product, h-units).</summary>
            public readonly double[] Min = new double[13];
            public readonly double[] Max = new double[13];
            /// <summary>Extra separating axes (edge x box-axis) not among the 13, flattened x,y,z; with their intervals.</summary>
            public double[] ExtraAxes = Array.Empty<double>();
            public double[] ExtraMin = Array.Empty<double>();
            public double[] ExtraMax = Array.Empty<double>();
            public Face[] Faces = Array.Empty<Face>();
            /// <summary>Signature id of the cross-section on each boundary side (0 = nothing there).</summary>
            public readonly int[] SideSig = new int[6];
            public Vec3d[] Vertices = Array.Empty<Vec3d>();
        }

        private static readonly Poly[] _polys = BuildAll();

        public static Poly Get(int config) => _polys[config];

        /// <summary>Corner position of bit b in h-units.</summary>
        public static Int3 Corner(int b) => new Int3((b & 1) * 2, ((b >> 1) & 1) * 2, ((b >> 2) & 1) * 2);

        /// <summary>Occupancy bits of the dual cell at lattice vertex (vx, vy, vz).</summary>
        public static int ConfigAt(ISubCells g, int vx, int vy, int vz)
        {
            int c = 0;
            if (g.Get(vx - 1, vy - 1, vz - 1) != 0) c |= 1;
            if (g.Get(vx, vy - 1, vz - 1) != 0) c |= 2;
            if (g.Get(vx - 1, vy, vz - 1) != 0) c |= 4;
            if (g.Get(vx, vy, vz - 1) != 0) c |= 8;
            if (g.Get(vx - 1, vy - 1, vz) != 0) c |= 16;
            if (g.Get(vx, vy - 1, vz) != 0) c |= 32;
            if (g.Get(vx - 1, vy, vz) != 0) c |= 64;
            if (g.Get(vx, vy, vz) != 0) c |= 128;
            return c;
        }

        /// <summary>World position of the minimum corner of the dual cell at a lattice vertex.</summary>
        public static Vec3d CellMin(int vx, int vy, int vz) =>
            new Vec3d(vx * SubGrid.SubSize - H, vy * SubGrid.SubSize - H, vz * SubGrid.SubSize - H);

        // =====================================================================================

        private static Poly[] BuildAll()
        {
            var sigs = new Dictionary<string, int>();
            var all = new Poly[ConfigCount];
            for (int c = 0; c < ConfigCount; c++) all[c] = Build(c, sigs);
            return all;
        }

        private static int Dot(Int3 a, Int3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static Int3 Cross(Int3 a, Int3 b) => new Int3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0) { int t = a % b; a = b; b = t; }
            return a;
        }

        /// <summary>Divides by the gcd and makes the first non-zero component positive.</summary>
        private static Int3 Canonical(Int3 v)
        {
            int g = Gcd(Gcd(v.X, v.Y), v.Z);
            if (g == 0) return v;
            v = new Int3(v.X / g, v.Y / g, v.Z / g);
            if (v.X < 0 || (v.X == 0 && (v.Y < 0 || (v.Y == 0 && v.Z < 0)))) v = -v;
            return v;
        }

        private static Poly Build(int config, Dictionary<string, int> sigs)
        {
            var p = new Poly { Config = config };
            if (config == 0)
            {
                p.Empty = true;
                return p;
            }
            p.Full = config == 255;

            // Plane offsets in h-units: n.p <= T.
            var T = new int[26];
            for (int k = 0; k < 26; k++)
            {
                Int3 n = Dir26(k);
                int best = int.MinValue;
                for (int b = 0; b < 8; b++)
                    if ((config & (1 << b)) != 0) best = Math.Max(best, Dot(n, Corner(b)));
                int cube = 2 * (Math.Max(0, n.X) + Math.Max(0, n.Y) + Math.Max(0, n.Z));
                T[k] = Math.Min(best + 1, cube);
            }

            // Vertices: every feasible intersection of three planes, in twelfths of h.
            var verts = new List<Int3>();
            var seen = new HashSet<Int3>();
            for (int i = 0; i < 26; i++)
            {
                Int3 ni = Dir26(i);
                for (int j = i + 1; j < 26; j++)
                {
                    Int3 nj = Dir26(j);
                    Int3 cjk0 = Cross(ni, nj);
                    if (cjk0 == Int3.Zero) continue;
                    for (int k = j + 1; k < 26; k++)
                    {
                        Int3 nk = Dir26(k);
                        Int3 jk = Cross(nj, nk);
                        int det = Dot(ni, jk);
                        if (det == 0) continue;
                        Int3 ki = Cross(nk, ni);
                        Int3 ij = cjk0;
                        // p = (Ti jk + Tj ki + Tk ij) / det
                        int nx = T[i] * jk.X + T[j] * ki.X + T[k] * ij.X;
                        int ny = T[i] * jk.Y + T[j] * ki.Y + T[k] * ij.Y;
                        int nz = T[i] * jk.Z + T[j] * ki.Z + T[k] * ij.Z;
                        if (det < 0) { det = -det; nx = -nx; ny = -ny; nz = -nz; }
                        bool ok = true;
                        for (int m = 0; m < 26 && ok; m++)
                        {
                            Int3 nm = Dir26(m);
                            if ((long)nm.X * nx + (long)nm.Y * ny + (long)nm.Z * nz > (long)T[m] * det) ok = false;
                        }
                        if (!ok) continue;
                        if (12 % det != 0) throw new InvalidOperationException("Unexpected vertex denominator " + det);
                        int s = 12 / det;
                        var v12 = new Int3(nx * s, ny * s, nz * s);
                        if (seen.Add(v12)) verts.Add(v12);
                    }
                }
            }
            verts.Sort(Int3.CompareYZX);

            if (verts.Count < 4)
            {
                // Degenerate (cannot happen with radius-h growth, but stay safe): treat as empty.
                p.Empty = true;
                return p;
            }

            p.Vertices = new Vec3d[verts.Count];
            for (int i = 0; i < verts.Count; i++) p.Vertices[i] = new Vec3d(verts[i].X / 12.0, verts[i].Y / 12.0, verts[i].Z / 12.0);

            // Tight supports along the 13 axes.
            var max12 = new int[26];
            for (int k = 0; k < 26; k++)
            {
                Int3 n = Dir26(k);
                int best = int.MinValue;
                foreach (var v in verts) best = Math.Max(best, Dot(n, v));
                max12[k] = best;
            }
            for (int k = 0; k < 13; k++)
            {
                p.Max[k] = max12[k] / 12.0;
                p.Min[k] = -max12[k + 13] / 12.0;
            }

            // Faces.
            var faces = new List<Face>();
            var edgeDirs = new HashSet<Int3>();
            for (int k = 0; k < 26; k++)
            {
                Int3 n = Dir26(k);
                var on = new List<Int3>();
                foreach (var v in verts) if (Dot(n, v) == max12[k]) on.Add(v);
                if (on.Count < 3) continue;
                List<Int3> poly = OrderPolygon(on, n);
                if (poly.Count < 3) continue;

                var f = new Face { Dir = k, Normal = Vec3d.FromInt3(n).Normalized, P12 = poly.ToArray() };
                f.Verts = new Vec3d[poly.Count];
                for (int i = 0; i < poly.Count; i++) f.Verts[i] = new Vec3d(poly[i].X / 12.0, poly[i].Y / 12.0, poly[i].Z / 12.0);
                int side = SideOf(n, max12[k]);
                f.OnBoundary = side >= 0;
                f.Side = side;
                faces.Add(f);
                for (int i = 0; i < poly.Count; i++)
                {
                    Int3 e = Canonical(poly[(i + 1) % poly.Count] - poly[i]);
                    if (e != Int3.Zero) edgeDirs.Add(e);
                }
            }
            p.Faces = faces.ToArray();

            // Extra separating axes for box tests: box axis x polytope edge, when not one of the 13.
            var axisSet = new HashSet<Int3>();
            foreach (var a in Axes) axisSet.Add(Canonical(a));
            var extra = new List<Int3>();
            var boxAxes = new[] { new Int3(1, 0, 0), new Int3(0, 1, 0), new Int3(0, 0, 1) };
            var edgeList = new List<Int3>(edgeDirs);
            edgeList.Sort(Int3.CompareYZX);
            foreach (var e in edgeList)
                foreach (var a in boxAxes)
                {
                    Int3 w = Canonical(Cross(a, e));
                    if (w == Int3.Zero || axisSet.Contains(w)) continue;
                    axisSet.Add(w);
                    extra.Add(w);
                }
            p.ExtraAxes = new double[extra.Count * 3];
            p.ExtraMin = new double[extra.Count];
            p.ExtraMax = new double[extra.Count];
            for (int i = 0; i < extra.Count; i++)
            {
                Int3 w = extra[i];
                int lo = int.MaxValue, hi = int.MinValue;
                foreach (var v in verts)
                {
                    int d = Dot(w, v);
                    lo = Math.Min(lo, d);
                    hi = Math.Max(hi, d);
                }
                p.ExtraAxes[i * 3] = w.X;
                p.ExtraAxes[i * 3 + 1] = w.Y;
                p.ExtraAxes[i * 3 + 2] = w.Z;
                p.ExtraMin[i] = lo / 12.0;
                p.ExtraMax[i] = hi / 12.0;
            }

            // Cross-section signatures on the six boundary planes.
            foreach (var f in p.Faces)
            {
                if (!f.OnBoundary) continue;
                var pts = new List<string>();
                foreach (var v in f.P12)
                {
                    int a, b;
                    switch (f.Side >> 1) { case 0: a = v.Y; b = v.Z; break; case 1: a = v.X; b = v.Z; break; default: a = v.X; b = v.Y; break; }
                    pts.Add(a.ToString("D3") + "," + b.ToString("D3"));
                }
                pts.Sort(string.CompareOrdinal);
                string key = (f.Side >> 1) + ":" + string.Join(";", pts);
                if (!sigs.TryGetValue(key, out int id)) { id = sigs.Count + 1; sigs[key] = id; }
                p.SideSig[f.Side] = id;
            }
            return p;
        }

        /// <summary>Boundary side of a face, or -1. +X side sits at x = 2 h, -X at x = 0, and so on.</summary>
        private static int SideOf(Int3 n, int max12)
        {
            if (n == new Int3(1, 0, 0) && max12 == 24) return 0;
            if (n == new Int3(-1, 0, 0) && max12 == 0) return 1;
            if (n == new Int3(0, 1, 0) && max12 == 24) return 2;
            if (n == new Int3(0, -1, 0) && max12 == 0) return 3;
            if (n == new Int3(0, 0, 1) && max12 == 24) return 4;
            if (n == new Int3(0, 0, -1) && max12 == 0) return 5;
            return -1;
        }

        /// <summary>Orders coplanar points counter-clockwise around the normal and drops collinear ones.</summary>
        private static List<Int3> OrderPolygon(List<Int3> pts, Int3 n)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (var v in pts) { cx += v.X; cy += v.Y; cz += v.Z; }
            cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;
            Vec3d nn = Vec3d.FromInt3(n).Normalized;
            Vec3d u = Math.Abs(nn.X) < 0.9 ? CrossD(nn, new Vec3d(1, 0, 0)).Normalized : CrossD(nn, new Vec3d(0, 1, 0)).Normalized;
            Vec3d w = CrossD(nn, u);
            var keyed = new List<KeyValuePair<double, Int3>>();
            foreach (var v in pts)
            {
                var d = new Vec3d(v.X - cx, v.Y - cy, v.Z - cz);
                keyed.Add(new KeyValuePair<double, Int3>(Math.Atan2(d.Dot(w), d.Dot(u)), v));
            }
            keyed.Sort((a, b) => a.Key.CompareTo(b.Key));
            var ordered = new List<Int3>();
            foreach (var kv in keyed) ordered.Add(kv.Value);

            // Drop points lying on the line between their neighbours (exact integer test).
            bool removed = true;
            while (removed && ordered.Count >= 3)
            {
                removed = false;
                for (int i = 0; i < ordered.Count; i++)
                {
                    Int3 a = ordered[(i + ordered.Count - 1) % ordered.Count], b = ordered[i], c = ordered[(i + 1) % ordered.Count];
                    if (Cross(b - a, c - b) == Int3.Zero)
                    {
                        ordered.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }
            }
            return ordered;
        }

        private static Vec3d CrossD(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
