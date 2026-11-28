using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Smooth ground (map v3 step 3, Ben 2026-11-28: real hills and elevation changes, never terraces): heights on a
    /// square grid (1.5 m on the battle royale map), solid below the surface. Each grid cell is four triangles that meet
    /// at its centre, whose height is the mean of the four corners. That split looks the same from all eight mirror and
    /// turn directions (a fixed diagonal would not), so a map stamped eight ways stays exactly fair.
    ///
    /// The same surface is drawn, walked on and shot (what you see is what you hit). Slopes with a normal steeper than
    /// <see cref="WalkableNormalY"/> are rock faces: you can't stand on them or step up them, and you slide down them
    /// (see <see cref="ISteepGround"/>). Off the grid there is no ground at all.
    /// </summary>
    public sealed class HeightField : ITerrain, ISteepGround
    {
        public const double Eps = Aabb.Epsilon;

        public readonly double OriginX, OriginZ, Cell;
        /// <summary>Cells along X and Z (vertices are one more each way).</summary>
        public readonly int NX, NZ;
        private readonly float[] _h;

        /// <summary>
        /// Up component of the steepest walkable slope's normal: 0.7 is about 45.6 degrees, so the 45 degree chamfers and
        /// every ramp and stair already in the game stay walkable. Map design keeps hills well under it and rock faces
        /// well over it (tested), so nothing sits on the edge.
        /// </summary>
        public double WalkableNormalY = 0.7;

        public double MinHeight { get; private set; }
        public double MaxHeight { get; private set; }

        public HeightField(double originX, double originZ, double cell, int nx, int nz, double fill = 0)
        {
            if (nx < 1 || nz < 1 || !(cell > 0)) throw new ArgumentException("A height field needs at least one cell.");
            OriginX = originX;
            OriginZ = originZ;
            Cell = cell;
            NX = nx;
            NZ = nz;
            _h = new float[(nx + 1) * (nz + 1)];
            for (int i = 0; i < _h.Length; i++) _h[i] = (float)fill;
            MinHeight = MaxHeight = (float)fill;
        }

        public double SizeX => NX * Cell;
        public double SizeZ => NZ * Cell;

        /// <summary>Height of vertex (i, j), 0..NX and 0..NZ.</summary>
        public double this[int i, int j] => _h[i + (NX + 1) * j];

        public double VertexX(int i) => OriginX + i * Cell;
        public double VertexZ(int j) => OriginZ + j * Cell;

        /// <summary>Sets one vertex. Call <see cref="Finish"/> once the heights are in.</summary>
        public void Set(int i, int j, double h) => _h[i + (NX + 1) * j] = (float)h;

        /// <summary>Works out the height range after the heights are set (queries use it to skip quickly).</summary>
        public void Finish()
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float v in _h)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            MinHeight = lo;
            MaxHeight = hi;
        }

        /// <summary>Fills every vertex from a function of its world position.</summary>
        public static HeightField FromFunction(double originX, double originZ, double cell, int nx, int nz, Func<double, double, double> height)
        {
            var f = new HeightField(originX, originZ, cell, nx, nz);
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++)
                    f.Set(i, j, height(f.VertexX(i), f.VertexZ(j)));
            f.Finish();
            return f;
        }

        /// <summary>
        /// A square field centred on (cx, cz), <paramref name="halfCells"/> cells from the centre to each side, whose
        /// heights come from one design eighth: <paramref name="eighth"/>(u, v) with u = the larger and v = the smaller of
        /// the distances from the centre along X and Z (0 &lt;= v &lt;= u). Worked out from whole cell counts, so every
        /// vertex is exactly equal to its seven twins (four turns, four mirrors).
        /// </summary>
        public static HeightField Symmetric(double cx, double cz, double cell, int halfCells, Func<double, double, double> eighth)
        {
            int n = 2 * halfCells;
            var f = new HeightField(cx - halfCells * cell, cz - halfCells * cell, cell, n, n);
            var memo = new float[(halfCells + 1) * (halfCells + 1)];
            for (int a = 0; a <= halfCells; a++)
                for (int b = 0; b <= a; b++)
                    memo[a * (halfCells + 1) + b] = (float)eighth(a * cell, b * cell);
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    int di = Math.Abs(i - halfCells), dj = Math.Abs(j - halfCells);
                    int a = Math.Max(di, dj), b = Math.Min(di, dj);
                    f._h[i + (n + 1) * j] = memo[a * (halfCells + 1) + b];
                }
            f.Finish();
            return f;
        }

        // ------------------------------------------------------------------ the surface

        /// <summary>The four triangles of a cell, as (u, v) corners in the cell's own 0..1 square: south, east, north, west.</summary>
        private static readonly double[] TriU = { 0, 1, 0.5, 1, 1, 0.5, 1, 0, 0.5, 0, 0, 0.5 };
        private static readonly double[] TriV = { 0, 0, 0.5, 0, 1, 0.5, 1, 1, 0.5, 1, 0, 0.5 };

        /// <summary>A triangle's plane: h = A + B u + C v in the cell's own coordinates.</summary>
        private struct Plane
        {
            public double A, B, C;
            public double At(double u, double v) => A + B * u + C * v;
        }

        private double Corner(int i, int j, int k) =>
            k == 0 ? _h[i + (NX + 1) * j] : k == 1 ? _h[i + 1 + (NX + 1) * j] : k == 2 ? _h[i + 1 + (NX + 1) * (j + 1)] : _h[i + (NX + 1) * (j + 1)];

        /// <summary>Heights at a triangle's three corners (the two cell corners, then the centre).</summary>
        private void TriHeights(int i, int j, int t, out double h0, out double h1, out double hc)
        {
            double h00 = _h[i + (NX + 1) * j], h10 = _h[i + 1 + (NX + 1) * j];
            double h11 = _h[i + 1 + (NX + 1) * (j + 1)], h01 = _h[i + (NX + 1) * (j + 1)];
            hc = (h00 + h10 + h11 + h01) * 0.25;
            switch (t)
            {
                case 0: h0 = h00; h1 = h10; break;
                case 1: h0 = h10; h1 = h11; break;
                case 2: h0 = h11; h1 = h01; break;
                default: h0 = h01; h1 = h00; break;
            }
        }

        private Plane PlaneOf(int i, int j, int t)
        {
            TriHeights(i, j, t, out double h0, out double h1, out double hc);
            double u0 = TriU[3 * t], v0 = TriV[3 * t];
            double du1 = TriU[3 * t + 1] - u0, dv1 = TriV[3 * t + 1] - v0, dh1 = h1 - h0;
            double du2 = 0.5 - u0, dv2 = 0.5 - v0, dh2 = hc - h0;
            double det = du1 * dv2 - du2 * dv1;
            double b = (dh1 * dv2 - dh2 * dv1) / det, c = (du1 * dh2 - du2 * dh1) / det;
            return new Plane { A = h0 - b * u0 - c * v0, B = b, C = c };
        }

        /// <summary>Unit normal of a triangle (points up).</summary>
        private Vec3d NormalOf(in Plane p) => new Vec3d(-p.B / Cell, 1, -p.C / Cell).Normalized;

        /// <summary>Which of a cell's four triangles holds local point (u, v).</summary>
        private static int TriangleAt(double u, double v)
        {
            double du = u - 0.5, dv = v - 0.5;
            if (Math.Abs(dv) >= Math.Abs(du)) return dv < 0 ? 0 : 2;
            return du > 0 ? 1 : 3;
        }

        private bool CellOf(double x, double z, out int i, out int j, out double u, out double v)
        {
            double fx = (x - OriginX) / Cell, fz = (z - OriginZ) / Cell;
            i = (int)Math.Floor(fx);
            j = (int)Math.Floor(fz);
            if (fx < 0 || fz < 0 || fx > NX || fz > NZ) { u = v = 0; return false; }
            if (i >= NX) i = NX - 1;
            if (j >= NZ) j = NZ - 1;
            u = fx - i;
            v = fz - j;
            return true;
        }

        /// <summary>True if (x, z) lies on the grid.</summary>
        public bool Covers(double x, double z) => CellOf(x, z, out _, out _, out _, out _);

        /// <summary>The surface height at (x, z); negative infinity off the grid.</summary>
        public double HeightAt(double x, double z)
        {
            if (!CellOf(x, z, out int i, out int j, out double u, out double v)) return double.NegativeInfinity;
            return PlaneOf(i, j, TriangleAt(u, v)).At(u, v);
        }

        /// <summary>The surface normal at (x, z) (straight up off the grid).</summary>
        public Vec3d NormalAt(double x, double z)
        {
            if (!CellOf(x, z, out int i, out int j, out double u, out double v)) return new Vec3d(0, 1, 0);
            return NormalOf(PlaneOf(i, j, TriangleAt(u, v)));
        }

        /// <summary>Steepest triangle in cell (i, j): the smallest up component of its normals.</summary>
        public double CellMinNormalY(int i, int j)
        {
            double m = 1;
            for (int t = 0; t < 4; t++) m = Math.Min(m, NormalOf(PlaneOf(i, j, t)).Y);
            return m;
        }

        /// <summary>Normal of triangle t (0 south, 1 east, 2 north, 3 west) of cell (i, j).</summary>
        public Vec3d TriangleNormal(int i, int j, int t) => NormalOf(PlaneOf(i, j, t));

        private double CellMax(int i, int j) => Math.Max(Math.Max(Corner(i, j, 0), Corner(i, j, 1)), Math.Max(Corner(i, j, 2), Corner(i, j, 3)));
        private double CellMin(int i, int j) => Math.Min(Math.Min(Corner(i, j, 0), Corner(i, j, 1)), Math.Min(Corner(i, j, 2), Corner(i, j, 3)));

        /// <summary>
        /// The highest point of the surface over the rectangle [x0, x1] x [z0, z1] (either side may be zero wide), or
        /// negative infinity if it lies off the grid. Exact: the surface is flat on each triangle, so the highest point
        /// of a triangle's part is one of the corners of that part.
        /// </summary>
        public double MaxOver(double x0, double z0, double x1, double z1)
        {
            double best = double.NegativeInfinity;
            ForTriangles(x0, z0, x1, z1, double.NegativeInfinity, (int i, int j, int t, double m, ref double acc) => { if (m > acc) acc = m; }, ref best);
            return best;
        }

        /// <summary>The lowest point of the surface over the grid vertices touching the rectangle (a quick bound: never above the true lowest).</summary>
        public double MinBoundOver(double x0, double z0, double x1, double z1)
        {
            int i0 = Math.Max(0, (int)Math.Floor((x0 - OriginX) / Cell)), i1 = Math.Min(NX, (int)Math.Ceiling((x1 - OriginX) / Cell));
            int j0 = Math.Max(0, (int)Math.Floor((z0 - OriginZ) / Cell)), j1 = Math.Min(NZ, (int)Math.Ceiling((z1 - OriginZ) / Cell));
            double m = double.PositiveInfinity;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++) m = Math.Min(m, _h[i + (NX + 1) * j]);
            return m;
        }

        /// <summary>Highest grid vertex touching the rectangle (a quick bound: never below the true highest point).</summary>
        public double MaxBoundOver(double x0, double z0, double x1, double z1)
        {
            int i0 = Math.Max(0, (int)Math.Floor((x0 - OriginX) / Cell)), i1 = Math.Min(NX, (int)Math.Ceiling((x1 - OriginX) / Cell));
            int j0 = Math.Max(0, (int)Math.Floor((z0 - OriginZ) / Cell)), j1 = Math.Min(NZ, (int)Math.Ceiling((z1 - OriginZ) / Cell));
            double m = double.NegativeInfinity;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++) m = Math.Max(m, _h[i + (NX + 1) * j]);
            return m;
        }

        private delegate void TriVisit<T>(int i, int j, int t, double maxHere, ref T acc);

        /// <summary>
        /// Visits every triangle the rectangle touches with the highest surface point over their shared part. Cells whose
        /// highest corner is at or below <paramref name="skipBelow"/> are skipped (nothing there can reach it).
        /// </summary>
        private void ForTriangles<T>(double x0, double z0, double x1, double z1, double skipBelow, TriVisit<T> visit, ref T acc)
        {
            double fx0 = (x0 - OriginX) / Cell, fx1 = (x1 - OriginX) / Cell, fz0 = (z0 - OriginZ) / Cell, fz1 = (z1 - OriginZ) / Cell;
            if (fx1 < 0 || fz1 < 0 || fx0 > NX || fz0 > NZ) return;
            fx0 = Math.Max(0, fx0); fz0 = Math.Max(0, fz0); fx1 = Math.Min(NX, fx1); fz1 = Math.Min(NZ, fz1);
            int i0 = Math.Min(NX - 1, (int)Math.Floor(fx0)), i1 = Math.Min(NX - 1, (int)Math.Floor(fx1));
            int j0 = Math.Min(NZ - 1, (int)Math.Floor(fz0)), j1 = Math.Min(NZ - 1, (int)Math.Floor(fz1));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (CellMax(i, j) <= skipBelow) continue;
                    double u0 = Math.Max(0, fx0 - i), u1 = Math.Min(1, fx1 - i), v0 = Math.Max(0, fz0 - j), v1 = Math.Min(1, fz1 - j);
                    if (u0 > u1 || v0 > v1) continue;
                    for (int t = 0; t < 4; t++)
                    {
                        Plane p = PlaneOf(i, j, t);
                        double m = MaxOnTriangle(p, t, u0, v0, u1, v1);
                        if (m > double.NegativeInfinity) visit(i, j, t, m, ref acc);
                    }
                }
        }

        private const double In = 1e-12;

        /// <summary>Highest value of the plane over (triangle t) intersected with the rectangle, or -inf if they don't meet.</summary>
        private static double MaxOnTriangle(in Plane p, int t, double u0, double v0, double u1, double v1)
        {
            double best = double.NegativeInfinity;
            double au = TriU[3 * t], av = TriV[3 * t], bu = TriU[3 * t + 1], bv = TriV[3 * t + 1], cu = 0.5, cv = 0.5;
            // Triangle corners inside the rectangle.
            Try(p, au, av, u0, v0, u1, v1, ref best);
            Try(p, bu, bv, u0, v0, u1, v1, ref best);
            Try(p, cu, cv, u0, v0, u1, v1, ref best);
            // Rectangle corners inside the triangle.
            if (InTriangle(u0, v0, au, av, bu, bv)) Take(p, u0, v0, ref best);
            if (InTriangle(u1, v0, au, av, bu, bv)) Take(p, u1, v0, ref best);
            if (InTriangle(u0, v1, au, av, bu, bv)) Take(p, u0, v1, ref best);
            if (InTriangle(u1, v1, au, av, bu, bv)) Take(p, u1, v1, ref best);
            // Triangle edges crossing the rectangle's sides.
            Edge(p, au, av, bu, bv, u0, v0, u1, v1, ref best);
            Edge(p, bu, bv, cu, cv, u0, v0, u1, v1, ref best);
            Edge(p, cu, cv, au, av, u0, v0, u1, v1, ref best);
            return best;
        }

        private static void Take(in Plane p, double u, double v, ref double best)
        {
            double h = p.At(u, v);
            if (h > best) best = h;
        }

        private static void Try(in Plane p, double u, double v, double u0, double v0, double u1, double v1, ref double best)
        {
            if (u >= u0 - In && u <= u1 + In && v >= v0 - In && v <= v1 + In) Take(p, u, v, ref best);
        }

        /// <summary>Is (u, v) in the triangle with corners a, b and the cell centre (boundary counts)?</summary>
        private static bool InTriangle(double u, double v, double au, double av, double bu, double bv)
        {
            double d1 = Cross(au, av, bu, bv, u, v), d2 = Cross(bu, bv, 0.5, 0.5, u, v), d3 = Cross(0.5, 0.5, au, av, u, v);
            bool neg = d1 < -In || d2 < -In || d3 < -In, pos = d1 > In || d2 > In || d3 > In;
            return !(neg && pos);
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
            (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        private static void Edge(in Plane p, double au, double av, double bu, double bv, double u0, double v0, double u1, double v1, ref double best)
        {
            double du = bu - au, dv = bv - av;
            if (Math.Abs(du) > In)
            {
                EdgeAtU(p, au, av, du, dv, u0, v0, v1, ref best);
                EdgeAtU(p, au, av, du, dv, u1, v0, v1, ref best);
            }
            if (Math.Abs(dv) > In)
            {
                EdgeAtV(p, au, av, du, dv, v0, u0, u1, ref best);
                EdgeAtV(p, au, av, du, dv, v1, u0, u1, ref best);
            }
        }

        private static void EdgeAtU(in Plane p, double au, double av, double du, double dv, double uu, double v0, double v1, ref double best)
        {
            double s = (uu - au) / du;
            if (s < -In || s > 1 + In) return;
            double vv = av + dv * s;
            if (vv >= v0 - In && vv <= v1 + In) Take(p, uu, vv, ref best);
        }

        private static void EdgeAtV(in Plane p, double au, double av, double du, double dv, double vv, double u0, double u1, ref double best)
        {
            double s = (vv - av) / dv;
            if (s < -In || s > 1 + In) return;
            double uu = au + du * s;
            if (uu >= u0 - In && uu <= u1 + In) Take(p, uu, vv, ref best);
        }

        // ------------------------------------------------------------------ ITerrain

        public bool BoxOverlapsSolid(Aabb box)
        {
            if (box.Min.Y >= MaxHeight - Eps) return false;
            if (box.Max.Y < MinHeight - Eps && Covers(box.Center.X, box.Center.Z)) return true; // deep under the ground
            double top = MaxOver(box.Min.X, box.Min.Z, box.Max.X, box.Max.Z);
            return box.Min.Y < top - Eps;
        }

        private struct Contact
        {
            public double Bottom;
            public double WalkableY;
            public Vec3d Walkable;
            public double SupportH;
            public Vec3d Support;
            public bool Any;
            public HeightField F;
        }

        private void Contacts(Aabb box, double probe, ref Contact c)
        {
            c.Bottom = box.Min.Y - probe;
            c.WalkableY = -1;
            c.SupportH = double.NegativeInfinity;
            c.F = this;
            if (c.Bottom >= MaxHeight - Eps) return;
            ForTriangles(box.Min.X, box.Min.Z, box.Max.X, box.Max.Z, c.Bottom + Eps, (int i, int j, int t, double m, ref Contact acc) =>
            {
                if (m <= acc.Bottom + Eps) return; // doesn't reach the probed box
                Vec3d n = acc.F.NormalOf(acc.F.PlaneOf(i, j, t));
                acc.Any = true;
                if (n.Y >= acc.F.WalkableNormalY && n.Y > acc.WalkableY + 1e-12) { acc.WalkableY = n.Y; acc.Walkable = n; }
                if (m > acc.SupportH + 1e-9 || (Math.Abs(m - acc.SupportH) <= 1e-9 && n.Y < acc.Support.Y)) { acc.SupportH = m; acc.Support = n; }
            }, ref c);
        }

        /// <summary>
        /// Ground under the box within the probe, and the flattest walkable slope it touches. A box held up only by rock
        /// faces (slopes steeper than <see cref="WalkableNormalY"/>) has no ground: you slide off it.
        /// </summary>
        public bool GroundContact(Aabb box, double probe, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            var c = new Contact();
            Contacts(box, probe, ref c);
            if (c.WalkableY < 0) return false;
            normal = c.Walkable;
            return true;
        }

        /// <summary>True when the box rests (within the probe) on rock faces only; <paramref name="normal"/> is the face it rests on.</summary>
        public bool SteepUnder(Aabb box, double probe, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            var c = new Contact();
            Contacts(box, probe, ref c);
            if (!c.Any || c.WalkableY >= 0) return false;
            normal = c.Support;
            return true;
        }

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance)
        {
            var result = new TerrainHit();
            Vec3d d = direction.Normalized;
            if (d.LengthSquared == 0 || maxDistance <= 0) return result;
            // Starting under the ground: a hit right at the origin (like a brush the ray starts inside).
            double h0 = HeightAt(origin.X, origin.Z);
            if (origin.Y < h0 - Eps)
            {
                result.Hit = true;
                result.Point = origin;
                result.Distance = 0;
                result.Normal = -d;
                result.Ground = true;
                return result;
            }
            // Clip to the grid and to the height range.
            double tMin = 0, tMax = maxDistance;
            if (!Slab(origin.X, d.X, OriginX, OriginX + SizeX, ref tMin, ref tMax)) return result;
            if (!Slab(origin.Z, d.Z, OriginZ, OriginZ + SizeZ, ref tMin, ref tMax)) return result;
            if (d.Y >= 0 && origin.Y >= MaxHeight) return result;
            if (d.Y < 0) tMax = Math.Min(tMax, (MinHeight - Eps - origin.Y) / d.Y);
            else if (d.Y > 0) { double tOut = (MaxHeight + Eps - origin.Y) / d.Y; if (tOut < tMin) return result; tMax = Math.Min(tMax, tOut); }
            if (tMin > tMax) return result;

            // Walk the cells the ray crosses (2D grid walk).
            double ox = (origin.X - OriginX) / Cell, oz = (origin.Z - OriginZ) / Cell, dx = d.X / Cell, dz = d.Z / Cell;
            double sx0 = ox + dx * tMin, sz0 = oz + dz * tMin;
            int i = Math.Max(0, Math.Min(NX - 1, (int)Math.Floor(sx0))), j = Math.Max(0, Math.Min(NZ - 1, (int)Math.Floor(sz0)));
            int stepI = dx > 0 ? 1 : dx < 0 ? -1 : 0, stepJ = dz > 0 ? 1 : dz < 0 ? -1 : 0;
            double tNextI = stepI > 0 ? (i + 1 - ox) / dx : stepI < 0 ? (i - ox) / dx : double.PositiveInfinity;
            double tNextJ = stepJ > 0 ? (j + 1 - oz) / dz : stepJ < 0 ? (j - oz) / dz : double.PositiveInfinity;
            double tDeltaI = stepI != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity, tDeltaJ = stepJ != 0 ? Math.Abs(1 / dz) : double.PositiveInfinity;
            double ta = tMin;
            int guard = NX + NZ + 4;
            while (guard-- > 0)
            {
                double tb = Math.Min(tMax, Math.Min(tNextI, tNextJ));
                if (tb >= ta - 1e-12 && RayCell(origin, d, i, j, ta, tb, ref result)) return result;
                if (tb >= tMax) break;
                if (tNextI <= tNextJ) { i += stepI; tNextI += tDeltaI; }
                else { j += stepJ; tNextJ += tDeltaJ; }
                if (i < 0 || j < 0 || i >= NX || j >= NZ) break;
                ta = tb;
            }
            return result;
        }

        private static bool Slab(double o, double d, double lo, double hi, ref double tMin, ref double tMax)
        {
            if (Math.Abs(d) < 1e-15) return o >= lo && o <= hi;
            double t0 = (lo - o) / d, t1 = (hi - o) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            tMin = Math.Max(tMin, t0);
            tMax = Math.Min(tMax, t1);
            return tMin <= tMax;
        }

        /// <summary>The ray's piece [ta, tb] inside cell (i, j): first point at or under the surface.</summary>
        private bool RayCell(Vec3d o, Vec3d d, int i, int j, double ta, double tb, ref TerrainHit hit)
        {
            double ya = o.Y + d.Y * ta, yb = o.Y + d.Y * tb;
            if (Math.Min(ya, yb) > CellMax(i, j) + Eps) return false;
            double x0 = OriginX + i * Cell, z0 = OriginZ + j * Cell;
            // Local coordinates along the piece: u(t) = pu + qu t, v(t) = pv + qv t.
            double pu = (o.X - x0) / Cell, qu = d.X / Cell, pv = (o.Z - z0) / Cell, qv = d.Z / Cell;
            // Split where the piece crosses the cell's diagonals (u = v and u + v = 1).
            double s1 = double.NaN, s2 = double.NaN;
            double den1 = qu - qv;
            if (Math.Abs(den1) > 1e-15) s1 = (pv - pu) / den1;
            double den2 = qu + qv;
            if (Math.Abs(den2) > 1e-15) s2 = (1 - pu - pv) / den2;
            double a = ta, m1 = s1 > ta && s1 < tb ? s1 : double.NaN, m2 = s2 > ta && s2 < tb ? s2 : double.NaN;
            if (!double.IsNaN(m1) && !double.IsNaN(m2) && m2 < m1) (m1, m2) = (m2, m1);
            if (double.IsNaN(m1)) { m1 = m2; m2 = double.NaN; }
            for (int k = 0; k < 3; k++)
            {
                double b = k == 0 ? (double.IsNaN(m1) ? tb : m1) : k == 1 ? (double.IsNaN(m2) ? tb : m2) : tb;
                if (b < a) continue;
                double mid = (a + b) * 0.5;
                int t = TriangleAt(Clamp01(pu + qu * mid), Clamp01(pv + qv * mid));
                Plane p = PlaneOf(i, j, t);
                double fa = o.Y + d.Y * a - p.At(pu + qu * a, pv + qv * a);
                double fb = o.Y + d.Y * b - p.At(pu + qu * b, pv + qv * b);
                if (fa <= Eps || fb <= Eps)
                {
                    double tt = fa <= Eps ? a : a + (b - a) * fa / (fa - fb);
                    hit.Hit = true;
                    hit.Distance = tt;
                    hit.Point = o + d * tt;
                    hit.Normal = NormalOf(p);
                    hit.Ground = true;
                    return true;
                }
                if (b >= tb) break;
                a = b;
            }
            return false;
        }

        private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    }
}
