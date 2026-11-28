using System;
using static Sculpt.Core.EastPlan;

namespace Sculpt.Core
{
    /// <summary>
    /// The dry reservoir: a broad bed of silt with the old river's channel winding down it (2 m deep: crouch in it and
    /// nobody on the bed sees you), and Oldmere, the village the water drowned, back in the open: roofless houses of
    /// broken brick walls (cover at every height), the church and its tall stone bell tower (the reservoir's landmark,
    /// looking over the dam's crest), sealed cellars, the old bridge over the channel. A pump house and a boat house on
    /// the north bank where the north road comes down.
    /// </summary>
    public static class EastReservoir
    {
        private const double Col = EastBuild.Col, Md = EastBuild.M;

        public static void Build(BrBuild b)
        {
            Church(b);
            Ruins(b);
            Bridge(b, 1000.0);
            NorthBank(b);
            b.Poi("Oldmere", new Vec3d(1005, -3, 790));
        }

        /// <summary>
        /// The church: a roofless-looking one-storey nave broken open at the west end and its bell tower, six storeys of
        /// stone with the stair up its west lane, windows only near the top, a bell room roof with a parapet. The fast way
        /// up is a hole: break a floor beside the stair.
        /// </summary>
        private static void Church(BrBuild b)
        {
            double x0 = 1013.1, z0 = 795.0, tx = x0 + 2 * Md + Col, x1 = tx + 2 * Md + Col, z1 = z0 + 3 * Md + Col;
            double y = b.Foot(x0, z0, x1, z1, 2.5);
            var nave = new GreyboxBlock.Building(x0, z0, 2, 3, 1) { Y0 = y };
            nave.AlongZ[0, 0, 1] = WallKind.WideDoor;     // the west front, broken open
            nave.AlongX[0, 0, 0] = WallKind.Window;
            nave.AlongX[0, 3, 1] = WallKind.Door;          // a gap in the north wall
            nave.AlongX[0, 0, 1] = WallKind.Door;
            nave.AlongZ[0, 2, 0] = WallKind.Door;          // into the tower (its stair's foot)
            b.Style = BrStyle.Ruin;
            b.Variant = 1;
            b.Raise(nave, "Oldmere church");
            b.Chest(nave, 0, 2, 0, Math.PI);
            b.Chest(nave, 1, 0, 1, Math.PI);               // on the nave's roof

            GreyboxBlock.Building t = BrKit.TowerPlan(b, tx, y, z0, 2, 3, 6);
            t.AlongZ[0, 0, 0] = WallKind.Door;             // from the nave
            t.AlongZ[0, 0, 2] = WallKind.Wall;
            t.AlongX[0, 0, 1] = WallKind.ShutDoor;         // south door
            t.AlongX[0, 3, 1] = WallKind.Wall;
            for (int s = 1; s < 6; s++)
            {
                t.AlongZ[s, 0, 1] = s >= 4 ? WallKind.Window : WallKind.Wall;
                t.AlongX[s, 3, 1] = s >= 4 ? WallKind.Window : WallKind.Wall;
                for (int i = 1; i < 2; i++) t.AlongX[s, 0, i] = s >= 4 ? WallKind.Window : WallKind.Wall;
                for (int j = 0; j < 3; j++) t.AlongZ[s, 2, j] = s >= 4 && j != 1 ? WallKind.Window : WallKind.Wall;
            }
            b.Style = BrStyle.Stone;
            b.Variant = 2;
            b.Raise(t, "Oldmere bell tower");
            BrKit.Parapet(b, t);
            for (int s = 1; s < 6; s += 2) b.Chest(t, 1, s % 4 == 1 ? 0 : 2, s, Math.PI);
            b.Chest(t, 1, 2, 6, Math.PI);
            b.Item(Item.Launcher, 1, new Vec3d(t.Cx(1) + 2, t.Top, t.Cz(0) + 3));
        }

        /// <summary>The drowned houses: roofless brick shells (walls broken to random heights, one or two tiles), and sealed cellars.</summary>
        private static void Ruins(BrBuild b)
        {
            b.Style = BrStyle.Ruin;
            b.Variant = 0;
            // (x, z, width, depth, seed): along both sides of the channel.
            (double x, double z, double w, double d, int seed)[] houses =
            {
                (962, 806, 10.5, 9, 1), (979, 812, 9, 7.5, 2), (948, 760, 12, 9, 3), (968, 752, 9, 9, 4),
                (990, 746, 10.5, 7.5, 5), (1012, 745, 12, 10.5, 6), (1036, 744, 9, 9, 7), (1046, 792, 7.5, 9, 8),
                (994, 800, 9, 7.5, 9), (930, 795, 9, 10.5, 10),
            };
            foreach (var h in houses)
            {
                double y = RuinHouse(b, h.x, h.z, h.w, h.d, h.seed);
                if (h.seed == 5) b.Item(Item.Grenade, 2, new Vec3d(h.x + h.w / 2, y, h.z + h.d * 0.25));
                if (h.seed == 10) b.Item(Item.Grenade, 2, new Vec3d(h.x + h.w / 2, y, h.z + h.d * 0.25));
            }
            // Cellars: sealed one-module boxes (a chest each).
            foreach (var (x, z) in new[] { (983.0, 765.0), (1024.0, 760.0), (955.0, 820.0), (1049.5, 758.0) })
            {
                double y = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, y, z);
            }
        }

        /// <summary>A roofless shell on its own footing: four walls of 1.5 m columns broken to 0, 1 or 2 tiles (a 3 m gap
        /// always on the side toward the channel), one or two chests inside. Returns its level.</summary>
        private static double RuinHouse(BrBuild b, double x0, double z0, double w, double d, int seed)
        {
            double y = b.Foot(x0, z0, x0 + w, z0 + d, 1.2);
            int nx = (int)Math.Round(w / 1.5);
            uint h = BrKit.Hash(seed, 17, 5u);
            int door = (int)(h % (uint)(nx - 2)) + 1;
            bool doorNorth = z0 < EastGround.ChannelMid(x0 + w / 2);
            int Rows(int side, int k)
            {
                uint r = BrKit.Hash(seed * 31 + side, k, 9u) % 10;
                return r < 2 ? 0 : r < 5 ? 1 : 2;
            }
            for (int k = 0; k < nx; k++)
            {
                int rs = Rows(0, k), rn = Rows(1, k);
                if (!doorNorth && (k == door || k == door + 1)) rs = 0;
                if (doorNorth && (k == door || k == door + 1)) rn = 0;
                if (rs > 0) b.Strip(new Vec3d(x0 + 1.5 * k, y, z0), 1.5, true, rs);
                if (rn > 0) b.Strip(new Vec3d(x0 + 1.5 * k, y, z0 + d - Col), 1.5, true, rn);
            }
            double inner = d - 2 * Col;
            int mz = Math.Max(1, (int)Math.Round(inner / 1.5));
            for (int k = 0; k < mz; k++)
            {
                double a = z0 + Col + inner * k / mz, len = inner / mz;
                int rw = Rows(2, k), re = Rows(3, k);
                if (rw > 0) b.Strip(new Vec3d(x0, y, a), len, false, rw);
                if (re > 0) b.Strip(new Vec3d(x0 + w - Col, y, a), len, false, re);
            }
            b.Chest(new Vec3d(x0 + w * 0.3, y, z0 + d * 0.5), Math.PI / 2);
            if (seed % 2 == 0) b.Chest(new Vec3d(x0 + w * 0.7, y, z0 + d * 0.5), -Math.PI / 2);
            return y;
        }

        /// <summary>The old bridge over the channel at x: a 3 m deck of floor tiles at the banks' height with low walls
        /// along it (crouch under it in the channel and you are hidden).</summary>
        private static void Bridge(BrBuild b, double x)
        {
            double c = EastGround.ChannelMid(x), z0 = c - 9, z1 = c + 9;
            double top = Math.Max(b.Natural(x, z0), b.Natural(x, z1));
            top = Math.Ceiling(top / 0.3) * 0.3;
            EastBuild.Walkway(b, x - 1.5, z0, x + 1.5, z1, top);
            b.Strip(new Vec3d(x - 1.5 - Col, top, z0 + 1.5), z1 - z0 - 3, false, 1);
            b.Strip(new Vec3d(x + 1.5, top, z0 + 1.5), z1 - z0 - 3, false, 1);
            b.Open(new Rect2(x - 6, z0 - 3, x + 6, z1 + 3));
        }

        /// <summary>The north bank: the pump house (two storeys, by the north road) and the boat house's shell.</summary>
        private static void NorthBank(BrBuild b)
        {
            b.Style = BrStyle.Brick;
            b.Variant = 3;
            var p = new GreyboxBlock.Building(1003.0, 830.0, 4, 2, 2);
            b.Foot(p, 2.5);
            b.Stairwell(p, 1, false, -1);
            p.AlongX[0, 0, 1] = WallKind.ShutDoor;
            p.AlongZ[0, 4, 0] = WallKind.ShutDoor;
            p.AlongX[0, 0, 3] = WallKind.Window;
            p.AlongX[1, 0, 0] = WallKind.Window;
            p.AlongX[1, 0, 2] = WallKind.Window;
            p.AlongX[1, 2, 2] = WallKind.Window;
            p.AlongZ[0, 1, 0] = WallKind.Wall;        // a sealed pump room
            p.AlongX[0, 1, 0] = WallKind.Wall;
            b.Raise(p, "Pump house");
            BrKit.Parapet(b, p);
            b.Chest(p, 0, 0, 0, 0);
            b.Chest(p, 2, 0, 1, Math.PI / 2);
            b.Chest(p, 3, 0, 0, Math.PI);

            b.Style = BrStyle.Timber;
            double y = b.Foot(960.0, 834.0, 975.0, 846.0, 1.5);
            b.Strip(new Vec3d(960.0, y, 834.0), 15, true, 2);
            b.Strip(new Vec3d(960.0, y, 834.3), 6, false, 2);
            b.Strip(new Vec3d(974.7, y, 838.5), 7.5, false, 1);
            b.Chest(new Vec3d(967.0, y, 840.0), Math.PI);
            b.Open(new Rect2(1040, 800, 1062, 858.4));
        }
    }
}
