using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>What the crosshair picks in sculpt mode on a map of tiles.</summary>
    public struct TileTarget
    {
        /// <summary>Tile id, or -1 (nothing in range under the crosshair).</summary>
        public int Tile;
        public Vec3d Point;
        /// <summary>Picked by magnetism (a near miss), not exactly under the crosshair.</summary>
        public bool Magnetised;
        public bool Valid => Tile >= 0;
        public static TileTarget None => new TileTarget { Tile = -1 };
    }

    /// <summary>
    /// Crosshair selection of wall tiles for healing: the nearest tile the crosshair ray meets (broken tiles count: you
    /// aim at the hole), unless something solid that is not a tile is in front of it. If that tile has nothing to do
    /// for the chosen kind, slight magnetism looks for a tile that has in two small rings around the crosshair. Range
    /// is measured like the server (eye to the tile's nearest point).
    /// </summary>
    public static class TileAim
    {
        private static readonly double[] Rings = { 1.5, 3.0 };
        [ThreadStatic] private static List<int> _near;

        public static TileTarget Find(ITerrain terrain, TileWalls walls, Vec3d eye, Vec3d dir, double range, HealKind kind)
        {
            if (walls == null) return TileTarget.None;
            TileTarget t = Ray(terrain, walls, eye, dir, range);
            if (t.Valid && TileHealing.StateCheck(walls[t.Tile], kind, walls.Config) == HealResult.Ok) return t;
            Vec3d up = Math.Abs(dir.Y) > 0.95 ? new Vec3d(1, 0, 0) : new Vec3d(0, 1, 0);
            Vec3d right = Cross(up, dir).Normalized;
            Vec3d up2 = Cross(dir, right).Normalized;
            foreach (double deg in Rings)
            {
                double a = deg * Math.PI / 180.0;
                TileTarget pick = TileTarget.None;
                double bestDot = double.NegativeInfinity;
                for (int k = 0; k < 8; k++)
                {
                    double th = k * Math.PI / 4.0;
                    Vec3d d = (dir * Math.Cos(a) + (right * Math.Cos(th) + up2 * Math.Sin(th)) * Math.Sin(a)).Normalized;
                    TileTarget c = Ray(terrain, walls, eye, d, range);
                    if (!c.Valid || TileHealing.StateCheck(walls[c.Tile], kind, walls.Config) != HealResult.Ok) continue;
                    double dot = (c.Point - eye).Normalized.Dot(dir);
                    if (dot > bestDot) { bestDot = dot; pick = c; }
                }
                if (pick.Valid) { pick.Magnetised = true; return pick; }
            }
            return t; // the tile under the crosshair even if there is nothing to do (the HUD says why)
        }

        /// <summary>The nearest tile along the ray within range (any state), or None if solid non-tile terrain is nearer.</summary>
        public static TileTarget Ray(ITerrain terrain, TileWalls walls, Vec3d eye, Vec3d dir, double range)
        {
            double max = range + walls.Config.TileSize * 1.5; // the nearest point can be in range while the hit is a little further
            int tile = RaycastAnyTile(walls, eye, dir, max, out double dist);
            if (tile < 0) return TileTarget.None;
            if (terrain != null)
            {
                TerrainHit h = terrain.Raycast(eye, dir, dist + 1e-6);
                int ht = walls.TileOfHit(h);
                if (h.Hit && h.Distance < dist - 1e-6 && (ht < 0 || walls.IsFollower(ht))) return TileTarget.None; // frame, floor, rock in front
            }
            if ((walls.BoundsOf(tile).ClosestPoint(eye) - eye).Length > range) return TileTarget.None;
            return new TileTarget { Tile = tile, Point = eye + dir * dist };
        }

        /// <summary>Nearest tile box (broken ones too) the ray enters within <paramref name="max"/> metres, or -1.</summary>
        public static int RaycastAnyTile(TileWalls walls, Vec3d o, Vec3d dir, double max, out double dist)
        {
            dist = max;
            Vec3d end = o + dir * max;
            var span = new Aabb(
                new Vec3d(Math.Min(o.X, end.X), Math.Min(o.Y, end.Y), Math.Min(o.Z, end.Z)),
                new Vec3d(Math.Max(o.X, end.X), Math.Max(o.Y, end.Y), Math.Max(o.Z, end.Z)));
            var near = _near ??= new List<int>();
            walls.TilesNear(span.Expand(0.01), near);
            int best = -1;
            foreach (int t in near)
            {
                if (walls.IsFollower(t)) continue; // frame followers are never a heal target (and block like frame, see Ray)
                Brush shape = walls.ShapeOf(t);
                double d;
                if (shape.IsRamp) { if (!BrushWorld.Clip(shape, o, dir, dist, out d, out _)) continue; } // the slope, not its box
                else if (!Slab(shape.Bounds, o, dir, dist, out d)) continue;
                if (best >= 0 && d >= dist) continue;
                best = t;
                dist = d;
            }
            return best;
        }

        /// <summary>Ray entry distance into a box (0 if it starts inside), if within max.</summary>
        public static bool Slab(Aabb b, Vec3d o, Vec3d d, double max, out double t)
        {
            double t0 = 0, t1 = max;
            t = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double oo = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                double dd = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                double lo = axis == 0 ? b.Min.X : axis == 1 ? b.Min.Y : b.Min.Z;
                double hi = axis == 0 ? b.Max.X : axis == 1 ? b.Max.Y : b.Max.Z;
                if (Math.Abs(dd) < 1e-12)
                {
                    if (oo < lo || oo > hi) return false;
                    continue;
                }
                double a = (lo - oo) / dd, c = (hi - oo) / dd;
                if (a > c) { double s = a; a = c; c = s; }
                if (a > t0) t0 = a;
                if (c < t1) t1 = c;
                if (t0 > t1) return false;
            }
            t = t0;
            return true;
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
