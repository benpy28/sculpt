using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Pieces for the South West segment (the Factory): tall halls with mezzanine galleries and railings, parapets with
    /// gaps where a bridge lands, floor bridges between buildings, a water tower with a tank room on top, open rail
    /// wagons. Owned by the South West segment's thread (not shared kit): copy, don't call, from another segment.
    /// </summary>
    internal static class FactoryKit
    {
        public const double M = GreyboxBlock.Module, C = GreyboxBlock.Column, SH = GreyboxBlock.StoreyHeight, Tile = 1.5;

        /// <summary>The outer size of a building <paramref name="modules"/> long.</summary>
        public static double W(int modules) => modules * M + C;

        private static bool Solidish(WallKind k) => k != WallKind.None && k != WallKind.Open;

        /// <summary>Holes every cell of slab <paramref name="s"/>; then <see cref="Floor"/> puts back the parts that stay.</summary>
        public static void HoleAll(GreyboxBlock.Building t, int s)
        {
            for (int i = 0; i < t.Mx; i++)
                for (int j = 0; j < t.Mz; j++) t.Hole[s, i, j] = true;
        }

        /// <summary>Floors cells i0..i1, j0..j1 (inclusive) of slab <paramref name="s"/>.</summary>
        public static void Floor(GreyboxBlock.Building t, int s, int i0, int i1, int j0, int j1)
        {
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++) t.Hole[s, i, j] = false;
        }

        /// <summary>True if a column stands at grid point (i, j): some storey has a wall touching it (as Raise decides).</summary>
        public static bool HasColumn(GreyboxBlock.Building b, int i, int j)
        {
            for (int s = 0; s < b.Storeys; s++)
            {
                if (i < b.Mx && Solidish(b.AlongX[s, j, i])) return true;
                if (i > 0 && Solidish(b.AlongX[s, j, i - 1])) return true;
                if (j < b.Mz && Solidish(b.AlongZ[s, i, j])) return true;
                if (j > 0 && Solidish(b.AlongZ[s, i, j - 1])) return true;
            }
            return false;
        }

        /// <summary>
        /// Chest-high railings (one breakable tile row) along every edge of slab <paramref name="s"/> where a floored cell
        /// meets a hole, except round the stairwell (cells 0..2 of <paramref name="stairLane"/> along z): a mezzanine
        /// gallery you can fight from. Where two railings meet (or one ends) a 0.3 m post fills the column point; it is a
        /// follower of the railing tiles beside it, so it goes when they all have (never a slit). Call after Raise.
        /// </summary>
        public static void Railings(BrBuild b, GreyboxBlock.Building t, int s, int stairLane)
        {
            bool Stair(int i, int j) => i == stairLane && j >= 0 && j <= 2;
            bool Fl(int i, int j) => i >= 0 && j >= 0 && i < t.Mx && j < t.Mz && !t.Hole[s, i, j];
            double y = t.Y0 + (s + 1) * SH;
            var nodes = new Dictionary<(int, int), List<int>>();
            void Node(int i, int j, int id)
            {
                if (!nodes.TryGetValue((i, j), out List<int> l)) nodes[(i, j)] = l = new List<int>();
                l.Add(id);
            }
            for (int j = 1; j < t.Mz; j++)
                for (int i = 0; i < t.Mx; i++)
                {
                    if (Fl(i, j - 1) == Fl(i, j) || Stair(i, j - 1) || Stair(i, j)) continue;
                    if (s + 1 < t.Storeys && Solidish(t.AlongX[s + 1, j, i])) continue;
                    int[] ids = b.Strip(new Vec3d(t.Cx(i) + C, y, t.Cz(j)), M - C, true, 1);
                    Node(i, j, ids[0]);
                    Node(i + 1, j, ids[ids.Length - 1]);
                }
            for (int i = 1; i < t.Mx; i++)
                for (int j = 0; j < t.Mz; j++)
                {
                    if (Fl(i - 1, j) == Fl(i, j) || Stair(i - 1, j) || Stair(i, j)) continue;
                    if (s + 1 < t.Storeys && Solidish(t.AlongZ[s + 1, i, j])) continue;
                    int[] ids = b.Strip(new Vec3d(t.Cx(i), y, t.Cz(j) + C), M - C, false, 1);
                    Node(i, j, ids[0]);
                    Node(i, j + 1, ids[ids.Length - 1]);
                }
            foreach (var kv in nodes)
            {
                (int i, int j) = kv.Key;
                if (HasColumn(t, i, j)) continue;
                var lo = new Vec3d(t.Cx(i), y, t.Cz(j));
                int id = b.World.Tiles.AddFollower(new Aabb(lo, lo + new Vec3d(C, Tile, C)), kv.Value);
                b.Info.Tiles.Add(id);
                b.Info.Frame.Add(id);
            }
        }

        /// <summary>A one-row parapet round a flat roof (as <see cref="BrKit.Parapet"/>), left out where it crosses any of
        /// <paramref name="gaps"/> (where a bridge lands on the roof).</summary>
        public static void Parapet(BrBuild b, GreyboxBlock.Building t, params Rect2[] gaps)
        {
            double y = t.Top, x0 = t.X0, z0 = t.Z0, x1 = t.Cx(t.Mx) + C, z1 = t.Cz(t.Mz) + C;
            void Side(double cx, double cz, double len, bool alongX)
            {
                Rect2 foot = alongX ? new Rect2(cx, cz, cx + len, cz + C) : new Rect2(cx, cz, cx + C, cz + len);
                var cuts = new List<(double lo, double hi)>();
                foreach (Rect2 g in gaps)
                    if (g.Overlaps(foot)) cuts.Add(alongX ? (g.X0, g.X1) : (g.Z0, g.Z1));
                cuts.Sort((p, q) => p.lo.CompareTo(q.lo));
                double a = alongX ? cx : cz, end = a + len;
                foreach (var c in cuts)
                {
                    if (c.lo > a + 0.01) Piece(a, Math.Min(c.lo, end));
                    a = Math.Max(a, c.hi);
                }
                if (end > a + 0.01) Piece(a, end);
                void Piece(double p0, double p1) =>
                    b.Strip(alongX ? new Vec3d(p0, y, cz) : new Vec3d(cx, y, p0), p1 - p0, alongX, 1);
            }
            Side(x0, z0, x1 - x0, true);
            Side(x0, z1 - C, x1 - x0, true);
            Side(x0, z0 + C, z1 - z0 - 2 * C, false);
            Side(x1 - C, z0 + C, z1 - z0 - 2 * C, false);
        }

        /// <summary>
        /// Breakable floor tiles over a rectangle with their top at <paramref name="top"/> (a bridge, a gantry, the
        /// floor of an overhanging room), split into equal pieces no bigger than a tile. With <paramref name="rails"/>
        /// a one-row railing runs along both long sides (inside the rectangle).
        /// </summary>
        public static void Deck(BrBuild b, double x0, double z0, double x1, double z1, double top, bool rails = false)
        {
            TileWalls tw = b.World.Tiles;
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / Tile - 1e-9)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / Tile - 1e-9));
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    var lo = new Vec3d(x0 + (x1 - x0) * i / nx, top - C, z0 + (z1 - z0) * j / nz);
                    var hi = new Vec3d(x0 + (x1 - x0) * (i + 1) / nx, top, z0 + (z1 - z0) * (j + 1) / nz);
                    int id = tw.AddTile(new Aabb(lo, hi), Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
            if (!rails) return;
            bool alongZ = z1 - z0 >= x1 - x0;
            if (alongZ)
            {
                b.Strip(new Vec3d(x0, top, z0), z1 - z0, false, 1);
                b.Strip(new Vec3d(x1 - C, top, z0), z1 - z0, false, 1);
            }
            else
            {
                b.Strip(new Vec3d(x0, top, z0), x1 - x0, true, 1);
                b.Strip(new Vec3d(x0, top, z1 - C), x1 - x0, true, 1);
            }
        }

        /// <summary>
        /// A water tower: a 2 x 3 stair core <paramref name="storeys"/> high with a 4 x 5 tank room overhanging it on
        /// every side by one module (its floor round the core is a breakable deck over a long drop), windows all round:
        /// the stairs arrive inside the tank. Returns the tank. (x, z) is the tank's south-west corner.
        /// </summary>
        public static GreyboxBlock.Building WaterTower(BrBuild b, double x, double y, double z, int storeys, out GreyboxBlock.Building core)
        {
            core = BrKit.TowerPlan(b, x + M, y, z + M, 2, 3, storeys);
            for (int s = 1; s < storeys; s++)
            {
                core.AlongX[s, 0, 1] = s % 3 == 0 ? WallKind.Window : WallKind.Wall;
                for (int j = 0; j < 3; j++) core.AlongZ[s, 2, j] = s % 3 == 1 && j == 1 ? WallKind.Window : WallKind.Wall;
            }
            b.Raise(core, "Water tower");
            // The tank room: tile walls and a roof deck on the core's roof and a deck round it (not a building: it
            // doesn't stand on the ground).
            double tx1 = x + W(4), tz1 = z + W(5), cx0 = core.X0, cz0 = core.Z0, cx1 = cx0 + W(2), cz1 = cz0 + W(3);
            Deck(b, x, z, cx0, tz1, core.Top);
            Deck(b, cx1, z, tx1, tz1, core.Top);
            Deck(b, cx0, z, cx1, cz0, core.Top);
            Deck(b, cx0, cz1, cx1, tz1, core.Top);
            Box(b, x, z, tx1, tz1, core.Top, 3, windows: true);
            var tank = new GreyboxBlock.Building(x, z, 4, 5, 1) { Y0 = core.Top };
            return tank;
        }

        /// <summary>
        /// A closed box of tile walls <paramref name="rows"/> tall on (x0, z0)..(x1, z1) at level <paramref name="y"/>,
        /// with a roof deck: a room standing on something other than the ground (a stacked container, a tank). With
        /// <paramref name="windows"/>, every other 6.3 m panel of the middle row has a 3 m window.
        /// </summary>
        public static void Box(BrBuild b, double x0, double z0, double x1, double z1, double y, int rows, bool windows = false)
        {
            void Side(double cx, double cz, double len, bool alongX)
            {
                for (int r = 0; r < rows; r++)
                {
                    double yy = y + r * Tile;
                    if (!windows || r != 1) { b.Strip(alongX ? new Vec3d(cx, yy, cz) : new Vec3d(cx, yy, cz), len, alongX, 1); continue; }
                    int panels = Math.Max(1, (int)Math.Round(len / M));
                    double pl = len / panels, a = 0;
                    for (int k = 0; k < panels; k++)
                    {
                        if (k % 2 == 1 || pl < 5) continue;
                        double g0 = k * pl + (pl - 3) / 2;
                        Piece(a, g0);
                        a = g0 + 3;
                    }
                    Piece(a, len);
                    void Piece(double p0, double p1)
                    {
                        if (p1 - p0 < 0.01) return;
                        b.Strip(alongX ? new Vec3d(cx + p0, yy, cz) : new Vec3d(cx, yy, cz + p0), p1 - p0, alongX, 1);
                    }
                }
            }
            Side(x0, z0, x1 - x0, true);
            Side(x0, z1 - C, x1 - x0, true);
            Side(x0, z0 + C, z1 - z0 - 2 * C, false);
            Side(x1 - C, z0 + C, z1 - z0 - 2 * C, false);
            Deck(b, x0, z0, x1, z1, y + rows * Tile + C);
        }

        /// <summary>An open rail wagon: two sides and two ends of tile wall, <paramref name="rows"/> tall, 9 m x 3 m, along x.</summary>
        public static void Wagon(BrBuild b, double x, double y, double z, int rows = 2)
        {
            const double len = 9, wide = 3;
            b.Strip(new Vec3d(x, y, z), len, true, rows);
            b.Strip(new Vec3d(x, y, z + wide - C), len, true, rows);
            b.Strip(new Vec3d(x, y, z + C), wide - 2 * C, false, rows);
            b.Strip(new Vec3d(x + len - C, y, z + C), wide - 2 * C, false, rows);
        }

        /// <summary>A two-storey house, 2 x 3 modules (as <see cref="BrKit.House"/>, with a name).</summary>
        public static GreyboxBlock.Building House(BrBuild b, double x, double y, double z, string name)
        {
            var t = new GreyboxBlock.Building(x, z, 2, 3) { Y0 = y };
            b.Stairwell(t, 1, true, +1);
            t.AlongZ[0, 0, 1] = WallKind.ShutDoor;
            t.AlongZ[0, 0, 2] = WallKind.Window;
            t.AlongX[0, 3, 0] = WallKind.Window;
            t.AlongZ[0, 1, 0] = WallKind.Wall;
            t.AlongZ[0, 1, 2] = WallKind.Wall;
            t.AlongZ[1, 1, 0] = WallKind.Wall;
            t.AlongZ[1, 1, 1] = WallKind.Wall;
            t.AlongZ[1, 1, 2] = WallKind.Door;
            t.AlongZ[1, 0, 0] = WallKind.Window;
            t.AlongZ[1, 0, 2] = WallKind.Window;
            t.AlongX[1, 0, 0] = WallKind.Window;
            b.Raise(t, name);
            b.Chest(t, 0, 2, 0, Math.PI / 2);
            b.Chest(t, 0, 0, 1, Math.PI / 2);
            return t;
        }
    }
}
