using System;

namespace Sculpt.Core
{
    /// <summary>
    /// What the East segment builds (see <see cref="EastPlan"/>): the dam complex (<see cref="EastDam"/>), the reservoir
    /// (<see cref="EastReservoir"/>), the towns (<see cref="EastTowns"/>), then the props.
    /// </summary>
    public static partial class EastBuild
    {
        public static void Build(BrBuild b)
        {
            EastDam.Build(b);
            EastReservoir.Build(b);
            EastTowns.Build(b);
            // The plains between: more scatter than the default (cover on the open ground), dead trees and rocks on the bed.
            b.Nature.Groves = 12;
            b.Nature.LoneTrees = 45;
            b.Nature.RockGroups = 40;
            b.Nature.Bushes = 90;
            int seed = 501;
            foreach (var (x, z) in new[] { (952.0, 780.0), (1030.0, 785.0), (940.0, 812.0), (1040.0, 783.0) }) b.Tree(x, z, seed++);
            foreach (var (x, z) in new[] { (968.0, 772.0), (1072.0, 790.0), (1074.0, 772.0), (928.0, 776.0) }) b.Rocks(x, z, seed++);
        }

        // ---------------------------------------------------------------- helpers shared by the East files

        public const double Col = GreyboxBlock.Column, M = GreyboxBlock.Module;

        /// <summary>Solid concrete that never breaks (BrushKind.Wall): fills the one-cell gap between a building's wall and a
        /// rock face, so nobody falls into a crack.</summary>
        public static void Fill(BrBuild b, double x0, double y0, double z0, double x1, double y1, double z1) =>
            b.World.AddBox(new Vec3d(x0, y0, z0), new Vec3d(x1, y1, z1), BrushKind.Wall);

        /// <summary>A walkway of floor tiles (about 1.5 m pieces) with its top at <paramref name="top"/> over [x0, x1] x [z0, z1].</summary>
        public static void Walkway(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            double s = b.World.Tiles.Config.TileSize, sx = x1 - x0, sz = z1 - z0;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int a = 0; a < nx; a++)
                for (int c = 0; c < nz; c++)
                {
                    var t = new Aabb(new Vec3d(x0 + sx * a / nx, top - Col, z0 + sz * c / nz), new Vec3d(x0 + sx * (a + 1) / nx, top, z0 + sz * (c + 1) / nz));
                    int id = b.World.Tiles.AddTile(t, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
            b.Info.Bridges.Add(new Aabb(new Vec3d(x0, top - Col, z0), new Vec3d(x1, top, z1)));
        }

        /// <summary>
        /// A one-row parapet round a building's roof, leaving gaps: <paramref name="gaps"/> lists (side, from, to) with side
        /// 0 south, 1 north, 2 west, 3 east and from/to along that side (map x for south and north, map z for west and east).
        /// </summary>
        public static void Parapet(BrBuild b, GreyboxBlock.Building t, params (int side, double from, double to)[] gaps)
        {
            double y = t.Top, x0 = t.X0, z0 = t.Z0, x1 = t.Cx(t.Mx) + Col, z1 = t.Cz(t.Mz) + Col;
            Run(b, 0, x0, x1, new Vec3d(x0, y, z0), true, gaps);
            Run(b, 1, x0, x1, new Vec3d(x0, y, z1 - Col), true, gaps);
            Run(b, 2, z0 + Col, z1 - Col, new Vec3d(x0, y, z0 + Col), false, gaps);
            Run(b, 3, z0 + Col, z1 - Col, new Vec3d(x1 - Col, y, z0 + Col), false, gaps);
        }

        /// <summary>Strips along one side from a to b (along its axis), skipping the gaps on that side.</summary>
        private static void Run(BrBuild b, int side, double a, double e, Vec3d corner, bool alongX, (int side, double from, double to)[] gaps)
        {
            var cuts = new System.Collections.Generic.List<(double, double)>();
            foreach (var g in gaps) if (g.side == side) cuts.Add((g.from, g.to));
            cuts.Sort();
            double at = a;
            foreach (var (f, t) in cuts)
            {
                if (f > at + 0.05) Piece(b, corner, at, f, alongX);
                at = Math.Max(at, t);
            }
            if (e > at + 0.05) Piece(b, corner, at, e, alongX);
        }

        private static void Piece(BrBuild b, Vec3d corner, double from, double to, bool alongX)
        {
            Vec3d c = alongX ? new Vec3d(from, corner.Y, corner.Z) : new Vec3d(corner.X, corner.Y, from);
            b.Strip(c, to - from, alongX, 1);
        }
    }
}
