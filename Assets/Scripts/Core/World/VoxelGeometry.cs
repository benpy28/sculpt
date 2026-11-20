using System;

namespace Sculpt.Core
{
    /// <summary>Result of a ray cast against the voxel grid.</summary>
    public struct VoxelHit
    {
        public bool Hit;
        public Int3 Cell;
        public Vec3d Point;
        public double Distance;
        /// <summary>Surface normal (unit length). For a ramp slope this is diagonal.</summary>
        public Vec3d Normal;
        /// <summary>Axis face that was hit. Ramp slopes report Up: they count as the block's top.</summary>
        public Dir Face;
        public bool HitRampSlope;
    }

    /// <summary>Exact geometry of blocks, including ramp wedges: surface heights, box overlap and ray casts.</summary>
    public static class VoxelGeometry
    {
        private const double Bs = VoxelGrid.BlockSize;

        // ---- Ramp maths ----
        // A ramp is a wedge filling its cell: height is BlockSize at the high side and 0 at the RampLow side.
        // With s = fraction of the way toward the low side (0..1), the surface is at local height (1 - s) * BlockSize.

        /// <summary>Metres from the high edge toward the low edge, for world x/z inside (or clamped to) a cell.</summary>
        private static double ProgressToLow(Dir low, Aabb cell, double x, double z)
        {
            switch (low)
            {
                case Dir.East: return Clamp(x, cell.Min.X, cell.Max.X) - cell.Min.X;
                case Dir.West: return cell.Max.X - Clamp(x, cell.Min.X, cell.Max.X);
                case Dir.North: return Clamp(z, cell.Min.Z, cell.Max.Z) - cell.Min.Z;
                case Dir.South: return cell.Max.Z - Clamp(z, cell.Min.Z, cell.Max.Z);
                default: throw new ArgumentException("Ramp low side must be horizontal.");
            }
        }

        /// <summary>World height of the block's top surface at world x/z (clamped into the cell). Empty returns cell bottom.</summary>
        public static double SurfaceHeight(Block b, Int3 cell, double x, double z)
        {
            Aabb box = VoxelGrid.CellBounds(cell);
            if (b.IsEmpty) return box.Min.Y;
            if (!b.IsRamp) return box.Max.Y;
            return box.Max.Y - ProgressToLow(b.RampLow, box, x, z);
        }

        /// <summary>Does a box overlap the solid volume of this block (strictly, touching is not overlap)?</summary>
        public static bool BoxOverlapsBlock(Block b, Int3 cell, Aabb box)
        {
            if (b.IsEmpty) return false;
            Aabb c = VoxelGrid.CellBounds(cell);
            if (!box.Overlaps(c)) return false;
            if (!b.IsRamp) return true;

            // Lowest point of the box inside the cell, and the box edge furthest toward the high side.
            double yLow = Math.Max(box.Min.Y, c.Min.Y) - c.Min.Y;
            double sMin;
            switch (b.RampLow)
            {
                case Dir.East: sMin = Math.Max(box.Min.X, c.Min.X) - c.Min.X; break;
                case Dir.West: sMin = c.Max.X - Math.Min(box.Max.X, c.Max.X); break;
                case Dir.North: sMin = Math.Max(box.Min.Z, c.Min.Z) - c.Min.Z; break;
                case Dir.South: sMin = c.Max.Z - Math.Min(box.Max.Z, c.Max.Z); break;
                default: throw new ArgumentException("Ramp low side must be horizontal.");
            }
            return yLow + sMin < Bs - Aabb.Epsilon;
        }

        /// <summary>Optional cell substitutions so callers can test "what if" grid states without writing the grid.</summary>
        public struct Override
        {
            public Int3 Cell;
            public Block Block;

            public Override(Int3 cell, Block block)
            {
                Cell = cell;
                Block = block;
            }
        }

        public static Block GetWithOverrides(VoxelGrid g, Int3 c, Override[] overrides)
        {
            if (overrides != null)
                for (int i = 0; i < overrides.Length; i++)
                    if (overrides[i].Cell == c) return overrides[i].Block;
            return g.Get(c);
        }

        /// <summary>Does a box overlap any solid block in the grid (out-of-bounds sides and bottom count as solid)?</summary>
        public static bool BoxOverlapsSolid(VoxelGrid g, Aabb box, Override[] overrides = null)
        {
            Int3 lo = VoxelGrid.CellAt(box.Min + new Vec3d(Aabb.Epsilon, Aabb.Epsilon, Aabb.Epsilon));
            Int3 hi = VoxelGrid.CellAt(box.Max - new Vec3d(Aabb.Epsilon, Aabb.Epsilon, Aabb.Epsilon));
            for (int y = lo.Y; y <= hi.Y; y++)
                for (int z = lo.Z; z <= hi.Z; z++)
                    for (int x = lo.X; x <= hi.X; x++)
                    {
                        var c = new Int3(x, y, z);
                        if (BoxOverlapsBlock(GetWithOverrides(g, c, overrides), c, box)) return true;
                    }
            return false;
        }

        // ---- Ray casting ----

        /// <summary>Casts a ray through the grid. <paramref name="direction"/> need not be normalised.</summary>
        public static VoxelHit Raycast(VoxelGrid g, Vec3d origin, Vec3d direction, double maxDistance)
        {
            var result = new VoxelHit();
            Vec3d dir = direction.Normalized;
            if (dir.LengthSquared == 0 || maxDistance <= 0) return result;

            Int3 cell = VoxelGrid.CellAt(origin);
            int stepX = Math.Sign(dir.X), stepY = Math.Sign(dir.Y), stepZ = Math.Sign(dir.Z);
            double tMaxX = NextBoundary(origin.X, dir.X, cell.X);
            double tMaxY = NextBoundary(origin.Y, dir.Y, cell.Y);
            double tMaxZ = NextBoundary(origin.Z, dir.Z, cell.Z);
            double tDeltaX = stepX != 0 ? Bs / Math.Abs(dir.X) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? Bs / Math.Abs(dir.Y) : double.PositiveInfinity;
            double tDeltaZ = stepZ != 0 ? Bs / Math.Abs(dir.Z) : double.PositiveInfinity;

            int maxSteps = (int)(maxDistance / Bs) * 3 + 6;
            for (int i = 0; i < maxSteps; i++)
            {
                Block b = g.Get(cell);
                if (!b.IsEmpty && ClipBlock(b, cell, origin, dir, maxDistance, out double t, out Vec3d n))
                {
                    result.Hit = true;
                    result.Cell = cell;
                    result.Distance = t;
                    result.Point = origin + dir * t;
                    result.Normal = n;
                    result.HitRampSlope = b.IsRamp && n.Y > 0 && (n.X != 0 || n.Z != 0);
                    result.Face = result.HitRampSlope ? Dir.Up : FaceFromNormal(n);
                    return result;
                }

                double tNext = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                if (tNext > maxDistance) break;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { cell = new Int3(cell.X + stepX, cell.Y, cell.Z); tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ) { cell = new Int3(cell.X, cell.Y + stepY, cell.Z); tMaxY += tDeltaY; }
                else { cell = new Int3(cell.X, cell.Y, cell.Z + stepZ); tMaxZ += tDeltaZ; }
            }
            return result;
        }

        private static double NextBoundary(double o, double d, int cell)
        {
            if (d > 0) return ((cell + 1) * Bs - o) / d;
            if (d < 0) return (cell * Bs - o) / d;
            return double.PositiveInfinity;
        }

        /// <summary>Clips a ray against a block's convex volume (cube, plus the slope plane for ramps).</summary>
        private static bool ClipBlock(Block b, Int3 cell, Vec3d o, Vec3d d, double maxT, out double tEnter, out Vec3d normal)
        {
            Aabb c = VoxelGrid.CellBounds(cell);
            tEnter = 0;
            double tExit = maxT;
            normal = Vec3d.Zero;

            if (!ClipPlane(new Vec3d(1, 0, 0), c.Max.X, o, d, ref tEnter, ref tExit, ref normal)) return false;
            if (!ClipPlane(new Vec3d(-1, 0, 0), -c.Min.X, o, d, ref tEnter, ref tExit, ref normal)) return false;
            if (!ClipPlane(new Vec3d(0, 1, 0), c.Max.Y, o, d, ref tEnter, ref tExit, ref normal)) return false;
            if (!ClipPlane(new Vec3d(0, -1, 0), -c.Min.Y, o, d, ref tEnter, ref tExit, ref normal)) return false;
            if (!ClipPlane(new Vec3d(0, 0, 1), c.Max.Z, o, d, ref tEnter, ref tExit, ref normal)) return false;
            if (!ClipPlane(new Vec3d(0, 0, -1), -c.Min.Z, o, d, ref tEnter, ref tExit, ref normal)) return false;

            if (b.IsRamp)
            {
                // Inside the wedge: (y - minY) + progressToLow <= Bs  ->  n . p <= k with n = (lowDir.x, 1, lowDir.z).
                Int3 ld = b.RampLow.ToInt3();
                Vec3d refCorner = (ld.X + ld.Z) > 0 ? c.Min : c.Max;
                var n = new Vec3d(ld.X, 1, ld.Z);
                double k = Bs + c.Min.Y + (ld.X * refCorner.X + ld.Z * refCorner.Z);
                Vec3d before = normal;
                if (!ClipPlane(n, k, o, d, ref tEnter, ref tExit, ref normal)) return false;
                if (!normal.Equals(before)) normal = normal.Normalized;
            }
            return tEnter <= tExit;
        }

        private static bool ClipPlane(Vec3d n, double k, Vec3d o, Vec3d d, ref double tEnter, ref double tExit, ref Vec3d normal)
        {
            double denom = n.Dot(d);
            double dist = k - n.Dot(o); // >= 0 means origin is inside this half-space
            if (Math.Abs(denom) < 1e-12) return dist >= 0;
            double t = dist / denom;
            if (denom < 0)
            {
                if (t > tEnter) { tEnter = t; normal = n; }
            }
            else if (t < tExit) tExit = t;
            return tEnter <= tExit;
        }

        private static Dir FaceFromNormal(Vec3d n)
        {
            if (n.X > 0.5) return Dir.East;
            if (n.X < -0.5) return Dir.West;
            if (n.Z > 0.5) return Dir.North;
            if (n.Z < -0.5) return Dir.South;
            if (n.Y < -0.5) return Dir.Down;
            return Dir.Up; // also the "started inside" case
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
