using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Exact queries against the chamfered surface: box overlap (separating axes against each dual cell's convex
    /// polytope), ground contact and ray casts. Works on any <see cref="ISubCells"/>, so "what if" overlays get the
    /// same answers the real grid would.
    /// </summary>
    public static class TerrainGeometry
    {
        private const double S = SubGrid.SubSize;
        private const double H = TerrainShape.H;
        private static readonly double[] AxisLen = BuildAxisLen();

        private static double[] BuildAxisLen()
        {
            var l = new double[13];
            for (int k = 0; k < 13; k++) l[k] = Vec3d.FromInt3(TerrainShape.Axes[k]).Length;
            return l;
        }

        [ThreadStatic] private static byte[] _occ;

        /// <summary>Range of lattice vertices whose dual cells overlap [lo, hi] on one axis by more than the epsilon.</summary>
        private static void VertexRange(double lo, double hi, out int v0, out int v1)
        {
            v0 = (int)Math.Floor((lo + Aabb.Epsilon + H) / S);
            v1 = (int)Math.Floor((hi - Aabb.Epsilon + H) / S);
        }

        /// <summary>Reads occupancy of sub-cells [x0-1, x1] x [y0-1, y1] x [z0-1, z1] into a scratch array.</summary>
        private static byte[] ReadOcc(ISubCells g, int x0, int x1, int y0, int y1, int z0, int z1, out int sx, out int sxz)
        {
            sx = x1 - x0 + 2;
            int sz = z1 - z0 + 2;
            int sy = y1 - y0 + 2;
            sxz = sx * sz;
            int n = sxz * sy;
            byte[] occ = _occ;
            if (occ == null || occ.Length < n) _occ = occ = new byte[Math.Max(n, 1024)];
            int i = 0;
            for (int y = y0 - 1; y <= y1; y++)
                for (int z = z0 - 1; z <= z1; z++)
                    for (int x = x0 - 1; x <= x1; x++)
                        occ[i++] = g.Get(x, y, z) != 0 ? (byte)1 : (byte)0;
            return occ;
        }

        private static int ConfigFrom(byte[] occ, int i, int sx, int sxz)
        {
            // i = index of sub-cell (V - 1) in the scratch array.
            return occ[i] | occ[i + 1] << 1 | occ[i + sxz] << 2 | occ[i + sxz + 1] << 3 |
                   occ[i + sx] << 4 | occ[i + sx + 1] << 5 | occ[i + sxz + sx] << 6 | occ[i + sxz + sx + 1] << 7;
        }

        // ------------------------------------------------------------------ Box overlap

        public static bool BoxOverlapsSolid(ISubCells g, Aabb box)
        {
            VertexRange(box.Min.X, box.Max.X, out int x0, out int x1);
            VertexRange(box.Min.Y, box.Max.Y, out int y0, out int y1);
            VertexRange(box.Min.Z, box.Max.Z, out int z0, out int z1);
            if (x1 < x0 || y1 < y0 || z1 < z0) return false;
            byte[] occ = ReadOcc(g, x0, x1, y0, y1, z0, z1, out int sx, out int sxz);

            // Corners, not centre and half extents: the test is then monotone (a box inside another never collides when
            // the bigger one does not), because every projection is a sum of terms that each only grow with the box (ISSUE-7).
            for (int vy = y0; vy <= y1; vy++)
                for (int vz = z0; vz <= z1; vz++)
                {
                    int row = (vy - y0) * sxz + (vz - z0) * sx;
                    for (int vx = x0; vx <= x1; vx++)
                    {
                        int cfg = ConfigFrom(occ, row + (vx - x0), sx, sxz);
                        if (cfg == 0) continue;
                        if (cfg == 255) return true;
                        Vec3d m = TerrainShape.CellMin(vx, vy, vz);
                        if (HitsBox(TerrainShape.Get(cfg), (box.Min.X - m.X) / H, (box.Min.Y - m.Y) / H, (box.Min.Z - m.Z) / H,
                                (box.Max.X - m.X) / H, (box.Max.Y - m.Y) / H, (box.Max.Z - m.Z) / H)) return true;
                    }
                }
            return false;
        }

        /// <summary>Box by its corners (h-units relative to the cell) against a polytope, by separating axes.</summary>
        public static bool HitsBox(TerrainShape.Poly p, double lx, double ly, double lz, double hx, double hy, double hz)
        {
            const double eps = Aabb.Epsilon / H;
            var axes = TerrainShape.Axes;
            for (int k = 0; k < 13; k++)
            {
                Int3 a = axes[k];
                double lo = Lo(a.X, lx, hx) + Lo(a.Y, ly, hy) + Lo(a.Z, lz, hz);
                double hi = Hi(a.X, lx, hx) + Hi(a.Y, ly, hy) + Hi(a.Z, lz, hz);
                double el = eps * AxisLen[k];
                if (lo >= p.Max[k] - el || hi <= p.Min[k] + el) return false;
            }
            double[] xa = p.ExtraAxes;
            for (int i = 0, n = p.ExtraMin.Length; i < n; i++)
            {
                double ax = xa[i * 3], ay = xa[i * 3 + 1], az = xa[i * 3 + 2];
                double lo = Lo(ax, lx, hx) + Lo(ay, ly, hy) + Lo(az, lz, hz);
                double hi = Hi(ax, lx, hx) + Hi(ay, ly, hy) + Hi(az, lz, hz);
                double el = eps * Math.Sqrt(ax * ax + ay * ay + az * az);
                if (lo >= p.ExtraMax[i] - el || hi <= p.ExtraMin[i] + el) return false;
            }
            return true;
        }

        private static double Lo(double a, double l, double h) => a >= 0 ? a * l : a * h;
        private static double Hi(double a, double l, double h) => a >= 0 ? a * h : a * l;

        /// <summary>Box (centre, half extents, h-units relative to the cell) against a polytope, by separating axes.</summary>
        public static bool Hits(TerrainShape.Poly p, double cx, double cy, double cz, double ex, double ey, double ez)
        {
            const double eps = Aabb.Epsilon / H;
            var axes = TerrainShape.Axes;
            for (int k = 0; k < 13; k++)
            {
                Int3 a = axes[k];
                double c = a.X * cx + a.Y * cy + a.Z * cz;
                double r = Math.Abs(a.X) * ex + Math.Abs(a.Y) * ey + Math.Abs(a.Z) * ez;
                double el = eps * AxisLen[k];
                if (c - r >= p.Max[k] - el || c + r <= p.Min[k] + el) return false;
            }
            double[] xa = p.ExtraAxes;
            for (int i = 0, n = p.ExtraMin.Length; i < n; i++)
            {
                double ax = xa[i * 3], ay = xa[i * 3 + 1], az = xa[i * 3 + 2];
                double c = ax * cx + ay * cy + az * cz;
                double r = Math.Abs(ax) * ex + Math.Abs(ay) * ey + Math.Abs(az) * ez;
                double el = eps * Math.Sqrt(ax * ax + ay * ay + az * az);
                if (c - r >= p.ExtraMax[i] - el || c + r <= p.ExtraMin[i] + el) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ Ground contact

        public static bool GroundContact(ISubCells g, Aabb box, double probe, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            Aabb probed = box.Translate(new Vec3d(0, -probe, 0));
            VertexRange(probed.Min.X, probed.Max.X, out int x0, out int x1);
            VertexRange(probed.Min.Y, probed.Max.Y, out int y0, out int y1);
            VertexRange(probed.Min.Z, probed.Max.Z, out int z0, out int z1);
            if (x1 < x0 || y1 < y0 || z1 < z0) return false;
            byte[] occ = ReadOcc(g, x0, x1, y0, y1, z0, z1, out int sx, out int sxz);

            double ex = (box.Max.X - box.Min.X) * 0.5 / H, ey = (box.Max.Y - box.Min.Y) * 0.5 / H, ez = (box.Max.Z - box.Min.Z) * 0.5 / H;
            Vec3d c = box.Center;
            bool any = false;
            double bestY = -1;
            for (int vy = y0; vy <= y1; vy++)
                for (int vz = z0; vz <= z1; vz++)
                {
                    int row = (vy - y0) * sxz + (vz - z0) * sx;
                    for (int vx = x0; vx <= x1; vx++)
                    {
                        int cfg = ConfigFrom(occ, row + (vx - x0), sx, sxz);
                        if (cfg == 0) continue;
                        TerrainShape.Poly p = TerrainShape.Get(cfg);
                        Vec3d m = TerrainShape.CellMin(vx, vy, vz);
                        double lx = (c.X - m.X) / H, ly = (c.Y - m.Y) / H, lz = (c.Z - m.Z) / H;
                        if (!Hits(p, lx, ly - probe / H, lz, ex, ey, ez)) continue;
                        any = true;
                        Vec3d n = ContactNormal(p, lx, ly, lz, ex, ey, ez);
                        // A flat top on the cell's upper boundary with solid continuing above is inside the rock,
                        // not a surface: the cell above reports the real contact.
                        if (n.Y > 0.999999 && p.Max[1] >= 2 && TerrainShape.ConfigAt(g, vx, vy + 1, vz) != 0) continue;
                        if (n.Y > 0.5 && n.Y > bestY + 1e-9)
                        {
                            bestY = n.Y;
                            normal = n;
                        }
                    }
                }
            return any;
        }

        /// <summary>Direction of largest separation from the polytope to the (resting) box: the contact normal.</summary>
        private static Vec3d ContactNormal(TerrainShape.Poly p, double cx, double cy, double cz, double ex, double ey, double ez)
        {
            double best = double.NegativeInfinity;
            Vec3d bestN = new Vec3d(0, 1, 0);
            var axes = TerrainShape.Axes;
            for (int k = 0; k < 13; k++)
            {
                Int3 a = axes[k];
                double c = a.X * cx + a.Y * cy + a.Z * cz;
                double r = Math.Abs(a.X) * ex + Math.Abs(a.Y) * ey + Math.Abs(a.Z) * ez;
                double inv = 1.0 / AxisLen[k];
                double up = (c - r - p.Max[k]) * inv;   // box beyond the polytope along +a
                double down = (p.Min[k] - (c + r)) * inv; // box beyond along -a
                if (up > best + 1e-9) { best = up; bestN = Vec3d.FromInt3(a) * inv; }
                if (down > best + 1e-9) { best = down; bestN = Vec3d.FromInt3(-a) * inv; }
            }
            double[] xa = p.ExtraAxes;
            for (int i = 0, n = p.ExtraMin.Length; i < n; i++)
            {
                var a = new Vec3d(xa[i * 3], xa[i * 3 + 1], xa[i * 3 + 2]);
                double c = a.X * cx + a.Y * cy + a.Z * cz;
                double r = Math.Abs(a.X) * ex + Math.Abs(a.Y) * ey + Math.Abs(a.Z) * ez;
                double inv = 1.0 / a.Length;
                double up = (c - r - p.ExtraMax[i]) * inv;
                double down = (p.ExtraMin[i] - (c + r)) * inv;
                if (up > best + 1e-9) { best = up; bestN = a * inv; }
                if (down > best + 1e-9) { best = down; bestN = -a * inv; }
            }
            return bestN;
        }

        // ------------------------------------------------------------------ Ray cast

        public static TerrainHit Raycast(ISubCells g, Vec3d origin, Vec3d direction, double maxDistance)
        {
            var result = new TerrainHit();
            Vec3d dir = direction.Normalized;
            if (dir.LengthSquared == 0 || maxDistance <= 0) return result;

            // DDA over dual cells: vertex V covers [V S - H, V S + H].
            Vec3d o = origin + new Vec3d(H, H, H);
            int vx = (int)Math.Floor(o.X / S), vy = (int)Math.Floor(o.Y / S), vz = (int)Math.Floor(o.Z / S);
            int stepX = Math.Sign(dir.X), stepY = Math.Sign(dir.Y), stepZ = Math.Sign(dir.Z);
            double tMaxX = NextBoundary(o.X, dir.X, vx), tMaxY = NextBoundary(o.Y, dir.Y, vy), tMaxZ = NextBoundary(o.Z, dir.Z, vz);
            double tDeltaX = stepX != 0 ? S / Math.Abs(dir.X) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? S / Math.Abs(dir.Y) : double.PositiveInfinity;
            double tDeltaZ = stepZ != 0 ? S / Math.Abs(dir.Z) : double.PositiveInfinity;

            int maxSteps = (int)(maxDistance / S) * 3 + 6;
            for (int i = 0; i < maxSteps; i++)
            {
                int cfg = TerrainShape.ConfigAt(g, vx, vy, vz);
                if (cfg != 0 && Clip(TerrainShape.Get(cfg), TerrainShape.CellMin(vx, vy, vz), origin, dir, maxDistance, out double t, out Vec3d n))
                {
                    result.Hit = true;
                    result.Distance = t;
                    result.Point = origin + dir * t;
                    result.Normal = n;
                    result.Vertex = new Int3(vx, vy, vz);
                    result.Sub = NearestOccupied(cfg, vx, vy, vz, result.Point);
                    result.Id = g.Get(result.Sub.X, result.Sub.Y, result.Sub.Z);
                    return result;
                }
                double tNext = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                if (tNext > maxDistance) break;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { vx += stepX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ) { vy += stepY; tMaxY += tDeltaY; }
                else { vz += stepZ; tMaxZ += tDeltaZ; }
            }
            return result;
        }

        private static double NextBoundary(double o, double d, int cell)
        {
            if (d > 0) return ((cell + 1) * S - o) / d;
            if (d < 0) return (cell * S - o) / d;
            return double.PositiveInfinity;
        }

        /// <summary>Clips a ray (world units) against a dual cell's polytope. t in metres along the unit direction.</summary>
        private static bool Clip(TerrainShape.Poly p, Vec3d cellMin, Vec3d origin, Vec3d dir, double maxT, out double tEnter, out Vec3d normal)
        {
            Vec3d lo = (origin - cellMin) / H;
            Vec3d ld = dir / H;
            tEnter = 0;
            double tExit = maxT;
            int enterK = -1;
            var axes = TerrainShape.Axes;
            for (int k = 0; k < 26; k++)
            {
                Int3 a = k < 13 ? axes[k] : -axes[k - 13];
                double T = k < 13 ? p.Max[k] : -p.Min[k - 13];
                double denom = a.X * ld.X + a.Y * ld.Y + a.Z * ld.Z;
                double dist = T - (a.X * lo.X + a.Y * lo.Y + a.Z * lo.Z);
                if (Math.Abs(denom) < 1e-12)
                {
                    if (dist < 0) { normal = default; return false; }
                    continue;
                }
                double t = dist / denom;
                if (denom < 0)
                {
                    if (t > tEnter) { tEnter = t; enterK = k; }
                }
                else if (t < tExit) tExit = t;
                if (tEnter > tExit) { normal = default; return false; }
            }
            normal = enterK >= 0 ? Vec3d.FromInt3(TerrainShape.Dir26(enterK)).Normalized : -dir; // started inside
            return true;
        }

        /// <summary>The occupied corner sub-cell of a dual cell nearest a point (lowest bit wins ties).</summary>
        public static Int3 NearestOccupied(int cfg, int vx, int vy, int vz, Vec3d point)
        {
            double best = double.PositiveInfinity;
            Int3 bestSub = new Int3(vx - 1, vy - 1, vz - 1);
            for (int b = 0; b < 8; b++)
            {
                if ((cfg & (1 << b)) == 0) continue;
                var sub = new Int3(vx - 1 + (b & 1), vy - 1 + ((b >> 1) & 1), vz - 1 + ((b >> 2) & 1));
                double d = (SubGrid.SubBounds(sub).ClosestPoint(point) - point).LengthSquared;
                if (d < best - 1e-12)
                {
                    best = d;
                    bestSub = sub;
                }
            }
            return bestSub;
        }
    }
}
