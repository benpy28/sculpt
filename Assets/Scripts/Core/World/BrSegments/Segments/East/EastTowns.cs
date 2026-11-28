using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The East segment's towns and outposts: Damside Square (the home POI round the anchor: the dam company's long
    /// office block across the pads' view, the visitor centre, the workshops), the Lake Hotel on its knoll over the
    /// reservoir, the lakeshore cabins, the roadhouse on the west road, the staff village of Damside in the south west
    /// (hostel tower, school, terraces, houses, a walled depot), Surge Tower on Surge Hill (ten storeys), the bus depot on
    /// the south road, the switchyard (walls too tall to climb, sealed transformer rooms), Pylon Hill and the workers'
    /// camp by the spawn region.
    /// </summary>
    public static class EastTowns
    {
        private const double Col = EastBuild.Col, Md = EastBuild.M;

        public static void Build(BrBuild b)
        {
            Home(b);
            LakeHotel(b);
            Lakeshore(b);
            Roadhouse(b);
            Damside(b);
            SurgeHill(b);
            SouthRoad(b);
            Switchyard(b);
            Camp(b);
            Outposts(b);
            b.Style = BrStyle.Brick;
            b.Variant = 0;
        }

        // ---------------------------------------------------------------- the home POI

        /// <summary>
        /// Damside Square: exactly ten chests within 45 m of the anchor. The company offices (two storeys, 57 m long, no
        /// two openings in line through it) stand across every pad's view west past the anchor.
        /// </summary>
        private static void Home(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = BrGround.Base;
            // The company offices: 2 x 9 modules, the long side facing the square.
            var hq = new GreyboxBlock.Building(993.0, a.Z - 28.5, 2, 9, 2);
            b.Foot(hq, 2.5, y);
            b.Stairwell(hq, 0, true, +1);
            for (int s = 0; s < 2; s++)
            {
                for (int j = 3; j < 9; j++) hq.AlongZ[s, 1, j] = j % 2 == 1 ? WallKind.Door : WallKind.Wall;
                hq.AlongZ[s, 1, 0] = WallKind.Door;
                hq.AlongZ[s, 1, 1] = WallKind.Wall;
                hq.AlongZ[s, 1, 2] = WallKind.Door;
                foreach (int line in new[] { 3, 6 }) hq.AlongX[s, line, 1] = WallKind.Wall;
                hq.AlongX[s, 3, 0] = WallKind.Wall;
            }
            hq.AlongZ[0, 2, 2] = WallKind.ShutDoor;     // the square side
            hq.AlongZ[0, 2, 4] = WallKind.WideDoor;     // the main entrance
            hq.AlongZ[0, 2, 7] = WallKind.ShutDoor;
            hq.AlongZ[0, 0, 6] = WallKind.ShutDoor;     // the back (out of line with the front doors)
            for (int j = 0; j < 9; j++)
            {
                hq.AlongZ[1, 2, j] = j % 2 == 1 ? WallKind.Window : WallKind.Wall;
                hq.AlongZ[1, 0, j] = j % 4 == 0 ? WallKind.Window : WallKind.Wall;
            }
            hq.AlongX[1, 9, 1] = WallKind.Window;
            hq.AlongX[1, 0, 1] = WallKind.Window;
            b.Style = BrStyle.Plaster;
            b.Variant = 1;
            b.Raise(hq, "Dam company offices");
            BrKit.Parapet(b, hq);
            b.Chest(hq, 1, 3, 0, Math.PI / 2);
            b.Chest(hq, 0, 7, 0, 0);
            b.Chest(hq, 1, 5, 1, -Math.PI / 2);
            b.Chest(hq, 1, 4, 2, Math.PI / 2);

            // The visitor centre: glass, one storey, north of the square (its windows look over the reservoir).
            var vc = new GreyboxBlock.Building(1009.5, 677.0, 3, 2, 1);
            b.Foot(vc, 2.5, y);
            vc.AlongX[0, 0, 1] = WallKind.WideDoor;
            vc.AlongZ[0, 3, 0] = WallKind.ShutDoor;
            vc.AlongX[0, 2, 1] = WallKind.Window;
            vc.AlongX[0, 2, 2] = WallKind.Window;
            vc.AlongZ[0, 1, 1] = WallKind.Wall;          // the archive at the back west, sealed
            vc.AlongX[0, 1, 0] = WallKind.Wall;
            b.Style = BrStyle.Glass;
            b.Variant = 1;
            b.Raise(vc, "Visitor centre");
            b.Chest(vc, 0, 1, 0, 0);
            b.Chest(vc, 2, 0, 0, Math.PI);

            // The workshops: three bays south of the square, the east one sealed.
            var ws = new GreyboxBlock.Building(1009.5, 612.0, 3, 1, 1);
            b.Foot(ws, 2.5, y);
            ws.AlongZ[0, 1, 0] = WallKind.Wall;
            ws.AlongZ[0, 2, 0] = WallKind.Wall;
            ws.AlongX[0, 1, 0] = WallKind.WideDoor;
            ws.AlongX[0, 1, 1] = WallKind.ShutDoor;
            ws.AlongX[0, 0, 0] = WallKind.Window;
            b.Style = BrStyle.Industrial;
            b.Variant = 2;
            b.Raise(ws, "Workshops");
            b.Chest(ws, 0, 0, 0, 0);
            b.Chest(ws, 2, 0, 0, 0);

            // The square: a fountain box and planters (low walls), two chests.
            b.Strip(new Vec3d(a.X - 9.0, y, a.Z - 2.25), 4.5, true, 1);
            b.Strip(new Vec3d(a.X - 9.0, y, a.Z + 2.25), 4.5, true, 1);
            b.Strip(new Vec3d(a.X + 4.5, y, a.Z - 10.5), 6, true, 1);
            b.Strip(new Vec3d(a.X + 4.5, y, a.Z + 10.2), 6, true, 1);
            b.Strip(new Vec3d(a.X - 1.5, y, a.Z + 16.5), 6, false, 1);
            b.Chest(new Vec3d(a.X - 6.75, y, a.Z), Math.PI / 2);
            b.Chest(new Vec3d(a.X + 7.5, y, a.Z + 13.5), Math.PI);
            b.Poi("Damside Square", a, hq.Bounds, vc.Bounds, ws.Bounds);
        }

        // ---------------------------------------------------------------- round the basin

        /// <summary>The Lake Hotel: four storeys on the knoll over the reservoir's west end, a terrace roof.</summary>
        private static void LakeHotel(BrBuild b)
        {
            var h = new GreyboxBlock.Building(928.0, 690.0, 3, 3, 4);
            b.Foot(h, 2.5);
            b.Stairwell(h, 0, true, +1);
            for (int s = 0; s < 4; s++)
            {
                for (int j = 0; j < 3; j++) h.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                h.AlongZ[s, 2, 1] = WallKind.Wall;       // rooms in the east lane
                h.AlongX[s, 1, 2] = WallKind.Wall;
                h.AlongX[s, 2, 2] = WallKind.Door;
                if (s > 0)
                {
                    for (int i = 1; i < 3; i++) h.AlongX[s, 3, i] = WallKind.Window;   // the lake side
                    h.AlongX[s, 0, s % 2 + 1] = WallKind.Window;
                    h.AlongZ[s, 3, s % 3] = WallKind.Window;
                }
            }
            h.AlongX[0, 0, 1] = WallKind.WideDoor;
            h.AlongX[0, 3, 2] = WallKind.ShutDoor;
            h.AlongZ[0, 3, 1] = WallKind.ShutDoor;
            b.Style = BrStyle.Plaster;
            b.Variant = 2;
            b.Raise(h, "Lake Hotel");
            BrKit.Parapet(b, h);
            b.Chest(h, 2, 0, 0, Math.PI);
            b.Chest(h, 2, 2, 1, Math.PI);
            b.Chest(h, 1, 0, 2, 0);
            b.Chest(h, 2, 1, 3, Math.PI);
            b.Chest(h, 2, 2, 4, Math.PI);
            b.Poi("Lake Hotel", new Vec3d(938, h.Y0, 700), h.Bounds);
        }

        /// <summary>Cabins along the south bank's ridge: pairs of rooms, one of each pair sealed.</summary>
        private static void Lakeshore(BrBuild b)
        {
            b.Style = BrStyle.Timber;
            b.Variant = 1;
            foreach (var (x, z, k) in new[] { (958.0, 716.0, 0), (985.0, 720.0, 1), (1034.0, 718.0, 0), (1060.0, 714.0, 1) })
            {
                double y = b.Foot(x, z, x + 2 * Md + Col, z + Md + Col, 2.5);
                BrKit.Terrace(b, x, y, z, 2, true, k);
            }
        }

        /// <summary>The roadhouse on the west road: a warehouse with sealed storerooms.</summary>
        private static void Roadhouse(BrBuild b)
        {
            b.Style = BrStyle.Painted;
            b.Variant = 1;
            var t = new GreyboxBlock.Building(900.0, 608.0, 4, 3, 1);
            double y = b.Foot(t, 2.5);
            BrKit.Warehouse(b, 900.0, y, 608.0);
            b.Item(Item.Grenade, 2, new Vec3d(905.0, y, 612.0));
        }

        // ---------------------------------------------------------------- Damside

        /// <summary>
        /// Damside, where the dam's staff live: the six-storey hostel, the school, three terraces, three houses, the corner
        /// shop, a walled depot and two sealed sheds, round a crossroads.
        /// </summary>
        private static void Damside(BrBuild b)
        {
            b.Style = BrStyle.Plaster;
            b.Variant = 3;
            double hy = b.Foot(905.0, 560.0, 905.0 + 2 * Md + Col, 560.0 + 3 * Md + Col, 2.5);
            GreyboxBlock.Building ht = BrKit.Tower(b, 905.0, hy, 560.0, 2, 3, 6, 2, "Staff hostel");

            // The school: two storeys, a stair along its north row, a classroom at the west end.
            b.Style = BrStyle.Brick;
            b.Variant = 4;
            var sc = new GreyboxBlock.Building(925.0, 585.0, 4, 2, 2);
            b.Foot(sc, 2.5);
            b.Stairwell(sc, 1, false, +1);
            for (int i = 0; i < 4; i++)
            {
                sc.AlongX[0, 0, i] = i == 1 ? WallKind.WideDoor : i == 3 ? WallKind.Window : WallKind.Wall;
                sc.AlongX[1, 0, i] = WallKind.Window;
            }
            sc.AlongZ[0, 0, 0] = WallKind.ShutDoor;
            sc.AlongZ[0, 1, 0] = WallKind.Wall;
            sc.AlongZ[0, 1, 1] = WallKind.Wall;
            sc.AlongZ[1, 1, 0] = WallKind.Door;
            sc.AlongZ[1, 4, 0] = WallKind.Window;
            b.Raise(sc, "School");
            BrKit.Parapet(b, sc);
            b.Chest(sc, 0, 1, 0, 0);
            b.Chest(sc, 3, 0, 0, 0);
            b.Chest(sc, 0, 0, 1, 0);

            // The corner shop.
            b.Style = BrStyle.Painted;
            b.Variant = 2;
            var shop = new GreyboxBlock.Building(956.0, 589.0, 2, 2, 1);
            b.Foot(shop, 2.5);
            shop.AlongX[0, 0, 0] = WallKind.ShutDoor;
            shop.AlongX[0, 0, 1] = WallKind.Window;
            shop.AlongZ[0, 1, 1] = WallKind.Wall;      // the stock room, sealed
            shop.AlongX[0, 1, 1] = WallKind.Wall;
            b.Raise(shop, "Corner shop");
            b.Chest(shop, 0, 0, 0, 0);
            b.Chest(shop, 1, 1, 0, Math.PI);

            // Terraces.
            b.Style = BrStyle.Brick;
            b.Variant = 5;
            foreach (var (x, z, n, along, sealedRoom) in new[] { (925.0, 560.0, 4, true, 2), (925.0, 543.0, 4, true, 1), (961.0, 535.0, 3, false, 0) })
            {
                double w = along ? n * Md + Col : Md + Col, d = along ? Md + Col : n * Md + Col;
                double ty = b.Foot(x, z, x + w, z + d, 2.5);
                BrKit.Terrace(b, x, ty, z, n, along, sealedRoom);
            }

            // Houses.
            b.Style = BrStyle.Timber;
            b.Variant = 2;
            foreach (var (x, z) in new[] { (889.0, 517.0), (908.0, 512.0), (927.0, 489.0) })
            {
                var t = new GreyboxBlock.Building(x, z, 2, 3);
                double ty = b.Foot(t, 2.5);
                BrKit.House(b, x, ty, z);
            }

            // The depot: a walled yard (walls too tall to climb, one gate).
            b.Style = BrStyle.Industrial;
            b.Variant = 3;
            double dy = b.Foot(890.0, 452.0, 912.5, 474.5, 2.5);
            BrKit.Compound(b, 890.0, dy, 452.0);
            b.Item(Item.C4, 2, new Vec3d(897.0, dy, 460.0));

            // Sealed sheds.
            foreach (var (x, z) in new[] { (930.0, 466.0), (948.0, 455.0) })
            {
                double sy = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, sy, z);
            }
            b.Poi("Damside", new Vec3d(930, hy, 540), ht.Bounds, sc.Bounds);
        }

        // ---------------------------------------------------------------- Surge Hill

        /// <summary>Surge Tower: ten storeys of concrete on Surge Hill's top, the landmark of the south (a chest every second
        /// floor and on the roof; the fast way up is a hole).</summary>
        private static void SurgeHill(BrBuild b)
        {
            b.Style = BrStyle.Concrete;
            b.Variant = 5;
            double x = 994.0, z = 526.0;
            double y = b.Foot(x, z, x + 2 * Md + Col, z + 3 * Md + Col, 2.5);
            GreyboxBlock.Building t = BrKit.Tower(b, x, y, z, 2, 3, 10, 2, "Surge Tower");
            b.Poi("Surge Tower", new Vec3d(x + 6, y, z + 9), t.Bounds);
        }

        // ---------------------------------------------------------------- the south road

        /// <summary>The bus depot beside the south road: a garage with wide doors and a sealed office.</summary>
        private static void SouthRoad(BrBuild b)
        {
            b.Style = BrStyle.Industrial;
            b.Variant = 4;
            var g = new GreyboxBlock.Building(1066.0, 558.0, 3, 2, 1);
            b.Foot(g, 2.5);
            g.AlongZ[0, 0, 0] = WallKind.WideDoor;
            g.AlongZ[0, 0, 1] = WallKind.WideDoor;
            g.AlongZ[0, 2, 0] = WallKind.Wall;
            g.AlongZ[0, 2, 1] = WallKind.Wall;          // the office (east column), sealed
            g.AlongX[0, 2, 1] = WallKind.Window;
            b.Raise(g, "Bus depot");
            b.Chest(g, 2, 1, 0, Math.PI);
            b.Chest(g, 1, 0, 0, 0);
            b.Open(new Rect2(1040, BrLayout.Buildable(5).Z0, 1060, 612));
        }

        // ---------------------------------------------------------------- the switchyard and Pylon Hill

        /// <summary>
        /// The switchyard: a 45 m square yard walled three tiles high (too tall to climb), one gate in the north wall;
        /// inside, four sealed transformer rooms and the switch house. Break a wall anywhere to get in. A relay hut and a
        /// chest on Pylon Hill above it.
        /// </summary>
        private static void Switchyard(BrBuild b)
        {
            b.Style = BrStyle.Industrial;
            b.Variant = 5;
            double x0 = 1086.0, z0 = 462.0, size = 45.0, gate = 3.0, th = Col;
            double y = b.Foot(x0, z0, x0 + size, z0 + size, 2.5);
            double half = (size - gate) / 2;
            b.Strip(new Vec3d(x0, y, z0 + size - th), half, true, 3);
            b.Strip(new Vec3d(x0 + half + gate, y, z0 + size - th), half, true, 3);
            b.Strip(new Vec3d(x0, y, z0), size, true, 3);
            b.Strip(new Vec3d(x0, y, z0 + th), size - 2 * th, false, 3);
            b.Strip(new Vec3d(x0 + size - th, y, z0 + th), size - 2 * th, false, 3);
            foreach (var (dx, dz) in new[] { (5.0, 5.0), (19.0, 5.0), (5.0, 19.0), (19.0, 19.0) })
                BrKit.Shed(b, x0 + dx, y, z0 + dz);
            var house = new GreyboxBlock.Building(x0 + 32.0, z0 + 5.0, 1, 2, 1) { Y0 = y };
            house.AlongZ[0, 0, 1] = WallKind.ShutDoor;
            house.AlongX[0, 1, 0] = WallKind.Wall;     // a sealed relay room at the south end
            b.Raise(house, "Switch house");
            b.Chest(house, 0, 1, 0, -Math.PI / 2);
            b.Chest(house, 0, 0, 0, 0);
            b.Chest(new Vec3d(x0 + 34.0, y, z0 + 36.0), Math.PI);
            b.Poi("Switchyard", new Vec3d(x0 + size / 2, y, z0 + size / 2));

            // Pylon Hill: a sealed relay hut on its top and a chest beside it.
            double hy = b.Foot(1163.0, 506.0, 1169.6, 512.6, 2.5);
            BrKit.Shed(b, 1163.0, hy, 506.0);
            b.SpotChest(1177.0, 517.0, Math.PI);
        }

        // ---------------------------------------------------------------- outposts between the towns

        /// <summary>
        /// Small things between the big ones, so open ground is never a long run: the spillway gate house on the south
        /// plateau, the fuel stop on the hump by the south road, the allotments between Damside and the square, the weir
        /// huts at the reservoir's head, the fishermen's sheds and lookouts on the north ridge, another terrace and house
        /// in Damside, another lakeshore cabin, low walls across the open fields.
        /// </summary>
        private static void Outposts(BrBuild b)
        {
            // The spillway gate house: two storeys on the plateau's level ground (no footing near the gorge).
            b.Style = BrStyle.Concrete;
            b.Variant = 6;
            var gh = new GreyboxBlock.Building(1180.0, 718.2, 4, 2, 2) { Y0 = EastPlan.Crest };
            b.Stairwell(gh, 1, false, -1);
            gh.AlongX[0, 0, 0] = WallKind.ShutDoor;
            gh.AlongZ[0, 4, 0] = WallKind.ShutDoor;
            gh.AlongX[0, 2, 3] = WallKind.Window;
            gh.AlongZ[0, 1, 0] = WallKind.Wall;          // a sealed winch room (west end, ground floor)
            gh.AlongZ[0, 1, 1] = WallKind.Wall;
            gh.AlongX[0, 0, 0] = WallKind.Wall;
            gh.AlongX[0, 0, 2] = WallKind.ShutDoor;
            for (int i = 0; i < 4; i++) gh.AlongX[1, 2, i] = i % 2 == 0 ? WallKind.Window : WallKind.Wall;
            gh.AlongZ[1, 0, 0] = WallKind.Window;
            b.Raise(gh, "Spillway gate house");
            BrKit.Parapet(b, gh);
            b.Chest(gh, 0, 0, 0, 0);
            b.Chest(gh, 3, 0, 0, Math.PI);
            b.Chest(gh, 1, 0, 1, 0);

            // The fuel stop on the hump beside the south road.
            b.Style = BrStyle.Painted;
            b.Variant = 3;
            var fs = new GreyboxBlock.Building(1094.0, 598.0, 2, 1, 1);
            double fy = b.Foot(fs, 2.5);
            fs.AlongX[0, 1, 0] = WallKind.ShutDoor;
            fs.AlongX[0, 1, 1] = WallKind.Window;
            fs.AlongZ[0, 1, 0] = WallKind.Wall;          // the back office, sealed
            b.Raise(fs, "Fuel stop");
            b.Chest(fs, 0, 0, 0, 0);
            b.Chest(fs, 1, 0, 0, Math.PI);
            b.LowWall(1096.0, 610.6, 9, true, 1);                    // the pump island

            // The allotments: two sealed sheds and low walls between Damside and the square.
            b.Style = BrStyle.Timber;
            b.Variant = 4;
            foreach (var (x, z) in new[] { (958.0, 612.0), (975.0, 628.0) })
            {
                double y = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, y, z);
            }
            b.LowWall(950.0, 640.0, 12, true, 1);
            b.LowWall(978.0, 598.0, 9, true, 1);

            // The weir at the reservoir's head: two sealed sluice huts and its broken wall across the bed.
            b.Style = BrStyle.Stone;
            b.Variant = 3;
            foreach (var (x, z) in new[] { (905.0, 768.0), (905.0, 800.0) })
            {
                double y = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, y, z);
            }
            b.LowWall(913.5, 777.0, 21, false, 1);

            // The fishermen's sheds on the north-west bank, lookouts on the north ridge.
            b.Style = BrStyle.Timber;
            b.Variant = 5;
            foreach (var (x, z) in new[] { (912.0, 826.0), (930.0, 836.0) })
            {
                double y = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, y, z);
            }
            foreach (var (x, z) in new[] { (987.0, 851.0), (1073.0, 849.0), (1180.0, 520.0 - 46.0) })
                b.SpotChest(x, z, Math.PI);

            // Damside: another terrace and house.
            b.Style = BrStyle.Brick;
            b.Variant = 6;
            double ty = b.Foot(885.0, 575.0, 885.0 + Md + Col, 575.0 + 2 * Md + Col, 2.5);
            BrKit.Terrace(b, 885.0, ty, 575.0, 2, false, 1);
            b.Style = BrStyle.Timber;
            b.Variant = 2;
            var hs = new GreyboxBlock.Building(947.0, 499.0, 2, 3);
            double hsy = b.Foot(hs, 2.5);
            BrKit.House(b, 947.0, hsy, 499.0);

            // Another lakeshore cabin.
            b.Style = BrStyle.Timber;
            b.Variant = 1;
            double cy = b.Foot(1010.0, 722.0, 1010.0 + 2 * Md + Col, 722.0 + Md + Col, 2.5);
            BrKit.Terrace(b, 1010.0, cy, 722.0, 2, true, 0);

            // Low walls across the open fields (crouch cover between the buildings).
            foreach (var (x, z, len, alongX) in new[]
            {
                (1080.0, 615.0, 9.0, false), (1120.0, 612.0, 10.5, true), (1150.0, 540.0, 9.0, true), (1040.0, 470.0, 9.0, false),
                (1068.0, 520.0, 10.5, true), (975.0, 470.0, 9.0, true), (965.0, 665.0, 9.0, false), (1068.0, 690.0, 12.0, true),
                (900.0, 700.0, 9.0, false), (1130.0, 690.0, 9.0, true), (1210.0, 600.0, 9.0, false), (1015.0, 590.0, 10.5, true),
            })
                b.LowWall(x, z, len, alongX, 1);
        }

        // ---------------------------------------------------------------- the workers' camp

        /// <summary>Cabins and stores south of the spawn region.</summary>
        private static void Camp(BrBuild b)
        {
            b.Style = BrStyle.Timber;
            b.Variant = 3;
            foreach (var (x, z, n, k) in new[] { (1158.0, 572.0, 4, 1), (1162.0, 594.0, 3, 2) })
            {
                double y = b.Foot(x, z, x + n * Md + Col, z + Md + Col, 2.5);
                BrKit.Terrace(b, x, y, z, n, true, k);
            }
            foreach (var (x, z) in new[] { (1198.0, 560.0), (1140.0, 590.0) })
            {
                double y = b.Foot(x, z, x + Md + Col, z + Md + Col, 2.5);
                BrKit.Shed(b, x, y, z);
            }
        }
    }
}
