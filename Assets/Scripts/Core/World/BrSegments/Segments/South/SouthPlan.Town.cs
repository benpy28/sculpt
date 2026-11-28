using System;

namespace Sculpt.Core
{
    internal static partial class SouthPlan
    {
        // ================================================================ the blocks framing the square's south side

        /// <summary>
        /// A terrace block along X: a row facing south onto one street (depth <paramref name="depthS"/>), a row facing
        /// north onto another, walled back gardens between them (a chest-high wall down the middle, plot walls, tall end
        /// walls with one gate). Both rows stand on one level. Returns the level.
        /// </summary>
        private static double TerraceBlock(SouthKit k, double x0, double zSouth, int depthS, string southRoles, double xNorth, double zNorth, string northRoles,
                                           int storeysS, int storeysN, uint seed, double want, BrStyle styleS, BrStyle styleN, string nameS, string nameN, int gardenChestPlot = -1)
        {
            int nS = SouthKit.RowModules(southRoles), nN = SouthKit.RowModules(northRoles);
            double zGarden0 = zSouth + W(depthS), zGarden1 = zNorth, garden = zGarden1 - zGarden0;
            double gx0 = Math.Max(x0, xNorth), gx1 = Math.Min(x0 + W(nS), xNorth + W(nN));
            double y = k.Level(new Rect2(x0, zSouth, x0 + W(nS), zNorth), 2, want, nameS);
            k.Level(new Rect2(xNorth, zNorth - 1, xNorth + W(nN), zNorth + W(3)), 2, y, nameN);
            k.Row(x0, zSouth, y, storeysS, SouthKit.Face.South, southRoles, seed, depthS, nameS, styleS, (int)(seed % 4));
            k.Row(xNorth, zNorth, y, storeysN, SouthKit.Face.North, northRoles, seed + 7, 3, nameN, styleN, (int)((seed + 1) % 4));
            // Gardens: a wall down the middle, plot walls every two modules, tall walls at both ends (one with a gate).
            double mid = zGarden0 + garden / 2 - 0.15;
            k.Wall(gx0 + 0.3, mid, y, gx1 - gx0 - 0.6, true, 1);
            int plots = 0;
            for (double x = gx0 + 2 * M; x < gx1 - M; x += 2 * M)
            {
                // Plot walls meet the houses' back walls and the middle wall (no slits); chest high, so you vault them.
                k.Wall(x, zGarden0, y, mid - zGarden0, false, 1);
                k.Wall(x, mid + 0.3, y, zGarden1 - mid - 0.3, false, 1);
                plots++;
            }
            k.Wall(gx0, zGarden0, y, garden, false, 3, 1.5);   // tall end walls: one gate in the west one
            k.Wall(gx1 - 0.3, zGarden0, y, garden, false, 3);
            if (gardenChestPlot >= 0)
            {
                k.Chest(new Vec3d(gx0 + (gardenChestPlot * 2 + 1) * M, y, zGarden0 + garden * 0.25), 0, nameS + " garden");
                k.Chest(new Vec3d(gx0 + (gardenChestPlot * 2 + 3) * M, y, zGarden0 + garden * 0.25), 0, nameS + " garden");
            }
            return y;
        }

        private static void SquareBlocks(SouthKit k)
        {
            // South-west of the square: Cheap Street (three storeys on the high street) and the Tanners' Lane workshops.
            TerraceBlock(k, 588.6, 196.0, 2, "P* H X* H2 P*", 588.6, 228.8, "P* S2 H2* X* P*", 1, 2, 101, Core,
                         BrStyle.Brick, BrStyle.Plaster, "Tanners' Lane", "Cheap Street", gardenChestPlot: 1);
            // South-east: Bridge Row and the Mill Lane workshops.
            TerraceBlock(k, 678.9, 196.0, 2, "H2* P X* H P*", 672.6, 228.8, "P* H2* S2 X* P*", 1, 2, 202, Core,
                         BrStyle.Plaster, BrStyle.Timber, "Mill Lane", "Bridge Row", gardenChestPlot: 2);
        }

        private static void Districts(SouthKit k)
        {
            WestStreet(k);
            UpperTown(k);
            NorthQuarter(k);
            EastStreet(k);
            Brewery(k);
            MillHill(k);
            GallowsKnoll(k);
            Approach(k);
        }

        /// <summary>A row on its own footing (natural level unless <paramref name="want"/>); returns the building.</summary>
        private static GreyboxBlock.Building Row(SouthKit k, double x0, double z0, int storeys, SouthKit.Face face, string roles, uint seed,
                                                 BrStyle style, string name, double want = double.NaN, int depth = 3, double apron = 2)
        {
            int n = SouthKit.RowModules(roles);
            bool ns = face == SouthKit.Face.South || face == SouthKit.Face.North;
            Rect2 r = ns ? SouthKit.RectOf(x0, z0, n, depth) : SouthKit.RectOf(x0, z0, depth, n);
            double y = k.Level(r, apron, want, name);
            return k.Row(x0, z0, y, storeys, face, roles, seed, depth, name, style, (int)(seed % 5));
        }

        /// <summary>
        /// A yard walled too tall to climb (three tiles), one gate, a locked shed with a chest and a chest in the yard:
        /// cheap cover between buildings and loot you breach or walk round to.
        /// </summary>
        private static void Yard(SouthKit k, Rect2 r, string name, double gateS = -1, double gateN = -1, double gateW = -1, double gateE = -1, int seed = 0, bool tree = true)
        {
            double y = k.Level(r, 1.5, double.NaN, name);
            k.Walled(r, y, 3, gateS, gateN, gateW, gateE);
            double sx = r.X1 - SouthKit.W(1) - 1.2, sz = r.Z1 - SouthKit.W(1) - 1.2;
            BrKit.Shed(k.B, sx, y, sz, chest: false);
            k.Chest(new Vec3d(sx + 3.3, y, sz + 3.3), Math.PI, name + " shed");
            k.Chest(new Vec3d(r.X0 + 3.5, y, r.Z0 + 3.5), 0, name);
            if (tree) k.B.Tree((r.X0 + sx) / 2 + 1, (r.Z0 + r.Z1) / 2 + 2, 990 + seed, big: seed % 2 == 0);
        }

        // ================================================================ West Street and the Corn Market

        public const double StreetS = 248, StreetN = 264; // the high street's two building lines

        private static void WestStreet(SouthKit k)
        {
            BrBuild b = k.B;
            // North side, from the square westward: Weavers' Row (two storeys) up to the Corn Market.
            Row(k, 546.0, StreetN, 2, SouthKit.Face.South, "H* P* S2 X* T*", 301, BrStyle.Brick, "Weavers' Row", Core);
            // South side: Pudding Lane between the Corn Market and Cheap Street.
            Yard(k, new Rect2(546, 220, 574, 246), "Pudding Yard", gateN: 12, seed: 8);

            // The Corn Market: a plaza on the high street with the corn exchange standing in the middle of the road
            // (the street splits round it, so no sight line runs the length of the high street).
            b.Open(new Rect2(492, 232, 541, 290));
            double ex = 503.0, ez = 256 - W(3) / 2;
            var ce = new GreyboxBlock.Building(ex, ez, 4, 3, 2);
            ce.Y0 = k.Level(SouthKit.RectOf(ex, ez, 4, 3), 2, double.NaN, "corn exchange");
            for (int i = 0; i < 4; i++) { ce.AlongX[0, 0, i] = WallKind.WideDoor; ce.AlongX[0, 3, i] = i == 0 ? WallKind.Wall : WallKind.WideDoor; }
            for (int j = 0; j < 3; j++) { ce.AlongZ[0, 0, j] = j == 2 ? WallKind.Wall : WallKind.WideDoor; ce.AlongZ[0, 4, j] = WallKind.WideDoor; }
            k.Stairs(ce, 2, false, -1, 1);                    // north aisle, rising west, up to the roof
            ce.AlongX[0, 2, 0] = WallKind.Wall;              // the grain store in the north-west corner: sealed
            ce.AlongZ[0, 1, 2] = WallKind.Wall;
            for (int i = 0; i < 4; i++) { ce.AlongX[1, 0, i] = WallKind.Window; ce.AlongX[1, 3, i] = i % 2 == 0 ? WallKind.Window : WallKind.Wall; }
            for (int j = 0; j < 3; j++) { ce.AlongZ[1, 0, j] = WallKind.Window; ce.AlongZ[1, 4, j] = WallKind.Window; }
            ce.AlongX[1, 1, 3] = WallKind.Wall; ce.AlongZ[1, 3, 0] = WallKind.Wall; // the clerk's office upstairs (south-east): sealed
            k.Raise(ce, "Corn exchange", BrStyle.Stone, 3);
            BrKit.Parapet(b, ce);
            k.Chest(ce, 0, 2, 0, Math.PI / 2, "corn exchange grain store");
            k.Chest(ce, 2, 0, 0, 0, "corn exchange floor");
            k.Chest(ce, 3, 0, 1, Math.PI, "corn exchange clerk's office");
            b.Poi("Corn Market", new Vec3d(ex + W(4) / 2, ce.Y0, 256), ce.Bounds);
            // Market stalls round it.
            k.Wall(495, 238, ce.Y0, 4.5, true, 1);
            k.Wall(532, 238, ce.Y0, 4.5, true, 1);
            k.Wall(495, 280, ce.Y0, 4.5, true, 1);
            k.Wall(532, 280, ce.Y0, 4.5, true, 1);
            k.Chest(new Vec3d(497.2, ce.Y0, 240), 0, "corn market stall");
            k.Chest(new Vec3d(534.2, ce.Y0, 278.8), Math.PI, "corn market stall");

            // Tollgate: the rows by the west road out of town.
            Row(k, 452.0, StreetN, 2, SouthKit.Face.South, "P* S2* H* X* P*", 303, BrStyle.Timber, "Tollgate Row");
            Yard(k, new Rect2(452, 220, 488, 246), "Toll Yard", gateN: 4, seed: 1);
        }

        // ================================================================ the Upper Town: the church on the hill

        private static void UpperTown(SouthKit k)
        {
            BrBuild b = k.B;
            // The churchyard: one footing on the hilltop for the church, its tower and the yard round them.
            var yard = new Rect2(454, 312, 526, 362);
            double y = k.Level(yard, 3, double.NaN, "churchyard");
            double nx = 478.0, nz = 330.8, tx = nx - W(3), tz = nz + W(2) / 2 - W(3) / 2;
            // The nave: one tall room (no floor between its two storeys), clerestory windows, a door each side, pews.
            var nave = new GreyboxBlock.Building(nx, nz, 5, 2, 2) { Y0 = y };
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 2; j++) nave.Hole[0, i, j] = true;
            nave.AlongX[0, 0, 1] = WallKind.ShutDoor;
            nave.AlongX[0, 2, 3] = WallKind.Door;
            for (int i = 0; i < 5; i++) { nave.AlongX[1, 0, i] = WallKind.Window; nave.AlongX[1, 2, i] = WallKind.Window; }
            nave.AlongZ[1, 5, 0] = WallKind.Window; nave.AlongZ[1, 5, 1] = WallKind.Window;
            k.Raise(nave, "Church", BrStyle.Stone, 4);
            for (int i = 1; i <= 3; i++)
            {
                k.Wall(nave.Cx(i) + 1.0, nz + 2.2, y, 4.3, true, 1);
                k.Wall(nave.Cx(i) + 1.0, nz + W(2) - 2.5, y, 4.3, true, 1);
            }
            k.Chest(new Vec3d(nave.Cx(4) + 3.3, y, nz + W(2) / 2), -Math.PI / 2, "church altar");
            // The bell tower at the west end: six storeys, stairs up its north lane, an open belfry, a roof.
            var tw = new GreyboxBlock.Building(tx, tz, 3, 3, 4) { Y0 = y };
            k.Stairs(tw, 2, false, -1, 0);
            for (int s = 0; s < 4; s++)
            {
                tw.AlongX[s, 2, 0] = WallKind.Door; tw.AlongX[s, 2, 1] = WallKind.Wall; tw.AlongX[s, 2, 2] = WallKind.Door;
                if (s == 0) continue;
                for (int q = 0; q < 3; q++)
                {
                    WallKind open = s == 3 ? WallKind.WideDoor : WallKind.Window;
                    if (s == 3 || (q + s) % 2 == 0) { tw.AlongZ[s, 0, q] = open; tw.AlongX[s, 0, q] = open; }
                }
            }
            tw.AlongZ[0, 0, 1] = WallKind.WideDoor;
            tw.AlongX[0, 0, 1] = WallKind.ShutDoor;
            k.Raise(tw, "Church tower", BrStyle.Stone, 4);
            BrKit.Parapet(b, tw);
            k.Chest(tw, 1, 0, 2, 0, "church tower");
            k.Chest(tw, 1, 1, 4, 0, "church tower roof");
            b.Poi("St Brannoc's", new Vec3d(nx, y, nz), nave.Bounds, tw.Bounds);
            // The churchyard wall (chest high) with lych gates, and yews.
            k.Walled(yard, y, 1, gateS: 34, gateN: 20, gateE: 22);
            k.Chest(new Vec3d(516, y, 356), Math.PI, "churchyard");
            foreach (var (gx, gz, s) in new[] { (462.0, 318.0, 1), (462.0, 356.0, 3), (490.0, 322.0, 5) })
                b.Tree(gx, gz, 900 + s);

            // Church Street: a terrace climbing the hill east of the churchyard, fronts on the lane to the west.
            Row(k, 537.0, 300.0, 2, SouthKit.Face.West, "H* S2* P* X* T", 311, BrStyle.Plaster, "Church Street");
            // The rectory garden behind the square's shops: tall walls, one gate, a chest, fruit trees.
            var rg = new Rect2(566, 290, 595, 320);
            double gy = k.Level(rg, 1, double.NaN, "rectory garden");
            k.Walled(rg, gy, 3, gateW: 12);
            BrKit.Shed(b, 586, gy, 305.5);
            k.Chest(new Vec3d(570, gy, 296), 0, "rectory garden");
            b.Tree(574, 312, 950); b.Tree(582, 296, 951);
            // Castle Street: two rows on the hill's north shoulder, facing each other across a narrow lane.
            Row(k, 462.0, 368.0, 2, SouthKit.Face.North, "P* H2* S2* X* P*", 312, BrStyle.Timber, "Castle Street");
            // Castle Yard north of it: a walled yard too tall to climb, one gate on the lane, a stable and a chest.
            var cyR = new Rect2(462, 394, 505, 420);
            double cyy = k.Level(cyR, 1.5, double.NaN, "Castle Yard");
            k.Walled(cyR, cyy, 3, gateS: 20);
            BrKit.Shed(b, 494, cyy, 408);
            k.Chest(new Vec3d(470, cyy, 412), Math.PI / 2, "Castle Yard");
            b.Tree(480, 406, 940, big: true);
            Yard(k, new Rect2(518, 386, 552, 418), "Mount Yard", gateW: 10, seed: 2);
        }

        // ================================================================ the north quarter: the inn, the guildhall

        private static void NorthQuarter(SouthKit k)
        {
            BrBuild b = k.B;
            b.Open(new Rect2(600, 329, 712, 346)); // Hall Yard
            // The Wheatsheaf: a courtyard inn west of the north road.
            double iy = k.Level(SouthKit.RectOf(600, 352, 5, 5), 2, double.NaN, "Wheatsheaf");
            k.Courtyard(600, 352, iy, 5, 5, 2, "The Wheatsheaf", BrStyle.Timber, 2, 401);
            b.Poi("The Wheatsheaf", new Vec3d(616, iy, 368));
            // The guildhall east of the road: three storeys, a vault in the middle of the ground floor with no door.
            double gx = 666, gz = 352;
            var g = new GreyboxBlock.Building(gx, gz, 4, 4, 2);
            g.Y0 = k.Level(SouthKit.RectOf(gx, gz, 4, 4), 2, double.NaN, "guildhall");
            k.Stairs(g, 3, true, +1, 0);
            g.AlongZ[0, 0, 1] = WallKind.ShutDoor;          // onto the north road
            g.AlongX[0, 0, 2] = WallKind.Door;              // from Hall Yard
            g.AlongX[0, 1, 1] = WallKind.Wall; g.AlongX[0, 3, 1] = WallKind.Wall; // the vault: cells (1..2, 1..2)
            g.AlongX[0, 1, 2] = WallKind.Wall; g.AlongX[0, 3, 2] = WallKind.Wall;
            g.AlongZ[0, 1, 1] = WallKind.Wall; g.AlongZ[0, 1, 2] = WallKind.Wall;
            g.AlongZ[0, 3, 1] = WallKind.Wall; g.AlongZ[0, 3, 2] = WallKind.Wall;
            for (int s = 0; s < 2; s++)
            {
                // The stair lane's side wall: open where the flights start and arrive (the vault's wall on the ground floor).
                g.AlongZ[s, 3, 0] = WallKind.Door; g.AlongZ[s, 3, 3] = WallKind.Door;
                if (s > 0) { g.AlongZ[s, 3, 1] = WallKind.Wall; g.AlongZ[s, 3, 2] = WallKind.Door; }
            }
            for (int s = 1; s < 2; s++)
                for (int q = 0; q < 4; q++)
                {
                    if ((q + s) % 2 == 0) g.AlongZ[s, 0, q] = WallKind.Window;
                    if ((q + s) % 2 == 1) g.AlongX[s, 0, q] = WallKind.Window;
                    if (q % 2 == 0) g.AlongX[s, 4, q] = WallKind.Window;
                }
            k.Raise(g, "Guildhall", BrStyle.Stone, 5);
            BrKit.Parapet(b, g);
            k.Chest(g, 1, 1, 0, 0, "guildhall vault");
            k.Chest(g, 2, 2, 0, Math.PI, "guildhall vault");
            k.Chest(g, 0, 3, 1, Math.PI / 2, "guildhall upstairs");
            b.Poi("Guildhall", new Vec3d(gx + W(4) / 2, g.Y0, gz + W(4) / 2), g.Bounds);
            // North rows either side of the north road, and Sheep Street west of the inn.
            Row(k, 589.0, 392.0, 2, SouthKit.Face.North, "H* X* S2* P* H*", 402, BrStyle.Brick, "North Road");
            Yard(k, new Rect2(668, 388, 704, 416), "Hay Yard", gateW: 8, seed: 3);
            Row(k, 562.0, 330.0, 2, SouthKit.Face.East, "H* S2* X* P*", 404, BrStyle.Painted, "Sheep Street");
            // Bell Street's north side: a short row behind the clock tower.
            Row(k, 702.0, 335.0, 2, SouthKit.Face.West, "P* H* S2", 405, BrStyle.Brick, "Bell Street");
        }

        // ================================================================ East Street and the Horse Fair

        private static void EastStreet(SouthKit k)
        {
            BrBuild b = k.B;
            Row(k, 718.0, StreetN, 2, SouthKit.Face.South, "P* S2* X* H*", 501, BrStyle.Plaster, "East Street", Core);
            Row(k, 733.0, StreetS - W(3), 2, SouthKit.Face.North, "H* P* S2 X*", 502, BrStyle.Brick, "East Street", Core);
            // The Horse Fair: a wide place with the weigh house standing in the road.
            b.Open(new Rect2(768, 232, 816, 292));
            double wx = 782.4, wz = 256 - W(3) / 2;
            var wh = new GreyboxBlock.Building(wx, wz, 3, 3, 2);
            wh.Y0 = k.Level(SouthKit.RectOf(wx, wz, 3, 3), 2, double.NaN, "weigh house");
            k.Stairs(wh, 0, true, +1);
            for (int s = 0; s < 2; s++) { wh.AlongZ[s, 1, 0] = WallKind.Door; wh.AlongZ[s, 1, 1] = WallKind.Wall; wh.AlongZ[s, 1, 2] = WallKind.Door; }
            wh.AlongZ[0, 3, 1] = WallKind.WideDoor;
            wh.AlongX[0, 0, 0] = WallKind.ShutDoor;
            wh.AlongX[0, 2, 2] = WallKind.Wall; wh.AlongZ[0, 2, 2] = WallKind.Wall; // the scale room: sealed
            for (int q = 0; q < 3; q++) { wh.AlongX[1, 0, q] = WallKind.Window; wh.AlongX[1, 3, q] = WallKind.Window; wh.AlongZ[1, 3, q] = WallKind.Window; }
            k.Raise(wh, "Weigh house", BrStyle.Painted, 1);
            BrKit.Parapet(b, wh);
            k.Chest(wh, 2, 2, 0, Math.PI, "weigh house scale room");
            k.Chest(wh, 2, 0, 1, Math.PI, "weigh house upstairs");
            b.Poi("Horse Fair", new Vec3d(792, wh.Y0, 256), wh.Bounds);
            k.Wall(772, 238, wh.Y0, 6, true, 1); k.Wall(806, 280, wh.Y0, 6, true, 1);
            k.Chest(new Vec3d(775, wh.Y0, 240), 0, "horse fair");
            k.Chest(new Vec3d(809, wh.Y0, 278.8), Math.PI, "horse fair");
            // Rows by the east road out of town.
            Row(k, 820.0, StreetN, 2, SouthKit.Face.South, "H* S2* X*", 503, BrStyle.Timber, "Ferry Row");
            Yard(k, new Rect2(820, 214, 852, 246), "Ferry Yard", gateN: 14, seed: 4);
        }

        // ================================================================ the brewery and Orchard Rise

        private static void Brewery(SouthKit k)
        {
            BrBuild b = k.B;
            double wy = k.Level(new Rect2(722, 296, 762, 330), 2, double.NaN, "brewery");
            BrKit.Warehouse(b, 724, wy, 300);
            b.Poi("Brewery", new Vec3d(737, wy, 310));
            // The tan yard: a compound with walls too tall to climb.
            double cy = k.Level(new Rect2(770, 296, 792.5, 318.5), 2, double.NaN, "tan yard");
            BrKit.Compound(b, 770, cy, 296);
            // The walled orchard on Orchard Rise: tall walls, two gates, trees, a sealed apple store.
            var oz = new Rect2(760, 338, 822, 392);
            double oy = k.Level(oz, 2, double.NaN, "orchard");
            k.Walled(oz, oy, 3, gateS: 10, gateW: 30);
            BrKit.Shed(b, 806, oy, 376);
            k.Chest(new Vec3d(764, oy, 365), Math.PI / 2, "orchard");
            k.Chest(new Vec3d(816, oy, 350), -Math.PI / 2, "orchard");
            int s = 0;
            for (double x = 772; x <= 808; x += 12)
                for (double z = 348; z <= 384; z += 12)
                {
                    if (x > 790 && z > 366) continue; // the apple store
                    b.Tree(x, z, 700 + s++);
                }
            b.Poi("Orchard Rise", new Vec3d(791, oy, 365));
            Yard(k, new Rect2(830, 298, 856, 330), "Maltings Yard", gateW: 12, seed: 5);
            Yard(k, new Rect2(718, 392, 750, 418), "Kiln Yard", gateS: 6, seed: 6);
        }

        // ================================================================ Mill Hill

        private static void MillHill(SouthKit k)
        {
            BrBuild b = k.B;
            // The windmill on the hilltop: five storeys, stairs to a roof with a parapet: the south-east's lookout.
            double mx = 786.0, mz = 146.0;
            var mill = new GreyboxBlock.Building(mx, mz, 3, 3, 4);
            mill.Y0 = k.Level(SouthKit.RectOf(mx, mz, 3, 3), 3, double.NaN, "windmill");
            k.Stairs(mill, 0, true, +1);
            for (int s = 0; s < 4; s++)
            {
                mill.AlongZ[s, 1, 0] = WallKind.Door; mill.AlongZ[s, 1, 1] = WallKind.Wall; mill.AlongZ[s, 1, 2] = WallKind.Door;
                if (s == 2) for (int q = 0; q < 3; q++) mill.AlongZ[s, 2, q] = WallKind.Wall; // the sack store: sealed
                if (s == 0) continue;
                for (int q = 0; q < 3; q++) if ((q + s) % 2 == 0) { mill.AlongZ[s, 3, q] = WallKind.Window; mill.AlongX[s, 3, q] = WallKind.Window; mill.AlongX[s, 0, q] = WallKind.Window; }
            }
            mill.AlongX[0, 0, 0] = WallKind.ShutDoor;
            mill.AlongZ[0, 3, 1] = WallKind.Door;
            k.Raise(mill, "Windmill", BrStyle.Timber, 0);
            BrKit.Parapet(b, mill);
            k.Chest(mill, 2, 1, 2, Math.PI, "windmill");
            k.Chest(mill, 1, 1, 4, 0, "windmill roof");
            k.Chest(mill, 2, 2, 0, Math.PI, "windmill floor");
            b.Poi("Mill Hill", new Vec3d(mx + W(3) / 2, mill.Y0, mz + W(3) / 2), mill.Bounds);
            // The granary on the hill's west flank and a miller's cottage.
            Yard(k, new Rect2(744, 180, 772, 206), "Rick Yard", gateW: 8, seed: 7, tree: false);
            double cy = k.Level(SouthKit.RectOf(820, 120, 2, 3), 2, double.NaN, "miller's cottage");
            BrKit.House(b, 820, cy, 120);
        }

        // ================================================================ Gallows Knoll: the allotments

        private static void GallowsKnoll(SouthKit k)
        {
            BrBuild b = k.B;
            // The folly on top: a small three-storey tower, stairs to the roof.
            double fx = 506, fz = 140;
            var f = new GreyboxBlock.Building(fx, fz, 3, 2, 3);
            f.Y0 = k.Level(SouthKit.RectOf(fx, fz, 3, 2), 3, double.NaN, "folly");
            k.Stairs(f, 0, false, +1, 0);
            f.AlongX[0, 2, 1] = WallKind.ShutDoor;
            for (int s = 1; s < 3; s++) { f.AlongX[s, 2, 0] = WallKind.Window; f.AlongZ[s, 3, 1] = WallKind.Window; f.AlongX[s, 2, 2] = WallKind.Window; }
            k.Raise(f, "Folly", BrStyle.Ruin, 0);
            BrKit.Parapet(b, f);
            k.Chest(f, 2, 1, 1, Math.PI, "folly");
            k.Chest(f, 1, 1, 3, 0, "folly roof");
            b.Poi("Gallows Knoll", new Vec3d(fx + 9.6, f.Y0, fz + 6.4), f.Bounds);
            // Allotments on a terrace cut into the knoll's east foot: nine plots walled chest high, sheds, bushes.
            var al = new Rect2(548, 96, 593, 142);
            double ay = k.Level(al, 1.5, double.NaN, "allotments");
            for (int pi = 0; pi < 3; pi++)
                for (int pj = 0; pj < 3; pj++)
                {
                    double x0 = al.X0 + pi * 15 + 1.5, z0 = al.Z0 + pj * 15.3 + 1.2;
                    var r = new Rect2(x0, z0, x0 + 12, z0 + 13);
                    k.Walled(r, ay, 1, gateS: 4.5);
                    uint h = BrKit.Hash(pi, pj, 41);
                    if ((h & 3) == 0) BrKit.Shed(b, x0 + 2.85, ay, z0 + 3.35);
                    else if ((h & 3) != 3) k.Chest(new Vec3d(x0 + 6, ay, z0 + 8), 0, "allotment");
                    else b.Bush(x0 + 6, z0 + 8, (int)(h >> 4));
                }
            // Cottages on the west road.
            double cy = k.Level(SouthKit.RectOf(456, 196, 2, 3), 2, double.NaN, "cottage");
            BrKit.House(b, 456, cy, 196);
            double ty = k.Level(new Rect2(470, 100, 504, 114), 1.5, double.NaN, "tool sheds");
            BrKit.Terrace(b, 470.0, ty, 101.0, 2, true, 1);
        }

        // ================================================================ beside the approach

        private static void Approach(SouthKit k)
        {
            BrBuild b = k.B;
            // The tanners' and dyers' yards either side of the road in from the spawn: walled yards with a shed each.
            foreach (var (x0, seed) in new[] { (566.0, 1), (2 * 652.8 - 566.0 - 40.0, 2) })
            {
                var r = new Rect2(x0, 148, x0 + 40, 180);
                double y = k.Level(r, 2, double.NaN, "yard");
                k.Walled(r, y, 3, gateN: seed == 1 ? 6 : 31, gateS: 18);
                BrKit.Shed(b, seed == 1 ? x0 + 4 : x0 + 29.6, y, 156);
                k.Wall(x0 + 14, 165, y, 12, true, 1);
                k.Chest(new Vec3d(x0 + 20, y, 172), 0, seed == 1 ? "tanners' yard" : "dyers' yard");
            }
            // An avenue of limes along the road in.
            for (int i = 0; i < 3; i++)
            {
                b.Tree(611.6, 156 + i * 12, 960 + i);
                b.Tree(690.0, 156 + i * 12, 960 + i);
            }
        }
    }
}
