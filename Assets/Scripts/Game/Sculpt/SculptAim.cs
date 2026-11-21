using System;
using Sculpt.Core;

namespace Sculpt.Game.Sculpting
{
    /// <summary>What the crosshair is on in sculpt mode, and which way each press would move it.</summary>
    public struct SculptTarget
    {
        public bool Valid;
        public Int3 Sub;
        public Vec3d Point;
        public Vec3d Normal;
        public bool TopFace;
        /// <summary>LMB direction (push). RMB is the opposite (pull).</summary>
        public Dir PushDir;
        /// <summary>Top face only: the next-nearest edge (fallback when PushDir is blocked).</summary>
        public Dir SideDir;
        public ushort Id;
        /// <summary>True if magnetism picked this instead of what is exactly under the crosshair.</summary>
        public bool Magnetised;
    }

    /// <summary>
    /// Sculpt targeting: the sculptable sub-cell under the crosshair with slight magnetism (the sculptable closest to the
    /// crosshair inside a small cone, a wider cone when looking down at your feet), and direction picking on world axes
    /// only: a side face pushes into the face, a top face is split along its diagonals and the triangle picks the axis.
    /// Players are never selectable (terrain rays only). Range and line of sight match the server's rules.
    /// </summary>
    public static class SculptAim
    {
        private static readonly double[] Cone = { 1.5, 3.0 };
        private static readonly double[] FeetCone = { 1.5, 3.0, 5.0, 7.0 };

        public static Vec3d LookDir(double yawRad, double pitchDeg)
        {
            double p = pitchDeg * Math.PI / 180.0;
            return new Vec3d(Math.Sin(yawRad) * Math.Cos(p), -Math.Sin(p), Math.Cos(yawRad) * Math.Cos(p));
        }

        public static SculptTarget Find(ITerrain terrain, ISubCells cells, Vec3d eye, Vec3d dir, SculptConfig cfg, double pitchDeg)
        {
            SculptTarget best = Try(terrain, cells, eye, dir, cfg);
            if (best.Valid) return best;

            // Magnetism: rings of rays around the crosshair, nearest ring first.
            double[] rings = pitchDeg > 40 ? FeetCone : Cone;
            Vec3d up = Math.Abs(dir.Y) > 0.95 ? new Vec3d(1, 0, 0) : new Vec3d(0, 1, 0);
            Vec3d right = Cross(up, dir).Normalized;
            Vec3d up2 = Cross(dir, right).Normalized;
            foreach (double deg in rings)
            {
                double a = deg * Math.PI / 180.0;
                double bestDot = double.NegativeInfinity;
                SculptTarget pick = default;
                for (int k = 0; k < 8; k++)
                {
                    double t = k * Math.PI / 4.0;
                    Vec3d d = (dir * Math.Cos(a) + (right * Math.Cos(t) + up2 * Math.Sin(t)) * Math.Sin(a)).Normalized;
                    SculptTarget c = Try(terrain, cells, eye, d, cfg);
                    if (!c.Valid) continue;
                    // Closest to the crosshair: the aim point's angle from the view direction.
                    double dot = (c.Point - eye).Normalized.Dot(dir);
                    if (dot > bestDot) { bestDot = dot; pick = c; }
                }
                if (pick.Valid)
                {
                    pick.Magnetised = true;
                    return pick;
                }
            }
            return best;
        }

        private static SculptTarget Try(ITerrain terrain, ISubCells cells, Vec3d eye, Vec3d dir, SculptConfig cfg)
        {
            var t = new SculptTarget();
            TerrainHit h = terrain.Raycast(eye, dir, cfg.RangeMeters + 1.5);
            if (!h.Hit || h.Brush != 0) return t; // buildings are not sculpt pieces (their tiles get their own aim)
            ushort id = cells.Get(h.Sub.X, h.Sub.Y, h.Sub.Z);
            t.Sub = h.Sub;
            t.Point = h.Point;
            t.Normal = h.Normal;
            t.Id = id;
            if (!SubId.IsSculptable(id) || h.Sub.Y < cfg.MinSubY || h.Sub.Y > cfg.MaxSubY) return t;
            Aabb box = SubGrid.SubBounds(h.Sub);
            if ((box.ClosestPoint(eye) - eye).Length > cfg.RangeMeters) return t;

            Vec3d n = h.Normal;
            if (n.Y > 0.7)
            {
                t.TopFace = true;
                Int3 tile = SubGrid.TileOf(h.Sub);
                double u = (h.Point.X - tile.X * SubGrid.TileSize) / SubGrid.TileSize - 0.5;
                double v = (h.Point.Z - tile.Z * SubGrid.TileSize) / SubGrid.TileSize - 0.5;
                t.PushDir = Math.Abs(u) >= Math.Abs(v) ? (u >= 0 ? Dir.East : Dir.West) : (v >= 0 ? Dir.North : Dir.South);
                t.SideDir = Math.Abs(u) >= Math.Abs(v) ? (v >= 0 ? Dir.North : Dir.South) : (u >= 0 ? Dir.East : Dir.West);
            }
            else if (n.Y < -0.7)
            {
                t.PushDir = DominantHorizontal(dir); // under an overhang: away from you
            }
            else
            {
                t.PushDir = DominantHorizontal(new Vec3d(-n.X, 0, -n.Z)); // into the face
            }
            t.Valid = true;
            return t;
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static Dir DominantHorizontal(Vec3d v)
        {
            if (Math.Abs(v.X) >= Math.Abs(v.Z)) return v.X >= 0 ? Dir.East : Dir.West;
            return v.Z >= 0 ? Dir.North : Dir.South;
        }
    }
}
