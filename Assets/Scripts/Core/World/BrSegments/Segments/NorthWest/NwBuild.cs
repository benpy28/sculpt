using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The North West segment's layout (see <see cref="NorthWestSegment"/> for its ground). Map coordinates: x east,
    /// z north. The spawn region stands at x 210..248 along the south border, its pads looking east down the
    /// approach to the home anchor (389.76, 915.84).
    ///
    /// Greyfield Farm (home, round the anchor): a courtyard farm open to the west (where the pads come in), closed on
    /// the east by a 4.5 m farm wall (it blocks every pad's view east, past the anchor), a great barn with a hay loft,
    /// the farmhouse, a stable row and a dairy: ten chests, most behind a wall or up in the loft.
    ///
    /// Radio Mast Hill (the north-west quarter): a walled transmitter compound on the summit (walls too tall to climb,
    /// two gates) with the transmitter hall and its strongroom, an eight-storey lattice mast with a spire, a generator
    /// shed and a fuel store; a bunker dug into the crag on the hill's north-west flank with a lookout; shepherd huts
    /// on the flanks.
    ///
    /// Farmland: Silo Row (a grain elevator whose fifth-floor windows step out onto a deck of six sealed silos, a
    /// drying shed, the weighbridge office, a grain store), the Water Tower by the spawn, Westcott Orchard behind the
    /// spawn (packing house, cider barn, farmhouse, pigsties, 48 fruit trees), Greyfield Green north of home, the
    /// Holloway hamlet along the sunken lane (inn, chapel and bell tower, smithy, cottages), the Burnt Farm on the
    /// swell, Hollins Farm in the north-east (red barn, silos, farmhouse, cow shed, implement shed, walled yard,
    /// orchard), Mast Farm at the hill's north foot, the Pond Mill. Hedgerows, field walls, field barns and hay ricks
    /// break up every stretch of open ground.
    /// </summary>
    internal static class NwBuild
    {
        /// <summary>The chests this build put in sealed rooms (for the tests).</summary>
        public static System.Collections.Generic.List<Vec3d> SealedChests(BrBuild b) => NwKit.SealedChests(b);

        private const double Col = GreyboxBlock.Column;

        public static void Build(BrBuild b)
        {
            b.Nature.Groves = 5;
            b.Nature.LoneTrees = 18;
            b.Nature.RockGroups = 22;
            b.Nature.Bushes = 30;
            Home(b);
            RadioMastHill(b);
            SiloRow(b);
            WaterTower(b);
            Westcott(b);
            Green(b);
            Holloway(b);
            Hollins(b);
            Fields(b);
        }

        // ================================================================ Greyfield Farm (home)

        private static void Home(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = BrGround.Base;
            // The farm wall: 4.5 m of stone along the east side, a gate in its north end (outside every pad's line).
            double wy = b.Foot(421.2, 885.6, 421.5, 956.6, 1.2, y);
            b.Strip(new Vec3d(421.2, wy, 885.6), 59.0, false, 3);
            b.Strip(new Vec3d(421.2, wy, 947.6), 9.0, false, 3);

            double by = NwKit.Foot(b, 398.1, 896.1, 3, 5, 2.5, y);
            NwPlan barn = NwKit.Barn(b, 398.1, by, 896.1, 3, 5, "Greyfield great barn", BrStyle.Timber, 0, chests: 4);
            double hy = NwKit.Foot(b, 360.3, 933.9, 3, 3, 2.5, y);
            NwPlan house = NwKit.FarmHouse(b, 360.3, hy, 933.9, "Greyfield farmhouse", BrStyle.Brick, 0, chests: 3);
            double sy = NwKit.Foot(b, 360.3, 885.0, 3, 1, 2.0, y);
            NwPlan stables = NwKit.Row(b, 360.3, sy, 885.0, 3, true, true, 0b100, 0b001, "Greyfield stables", BrStyle.Brick, 1);
            double dy = NwKit.Foot(b, 376.0, 905.0, 1, 1, 1.5, y);
            NwPlan dairy = NwKit.Row(b, 376.0, dy, 905.0, 1, true, false, 0b1, 0, "Greyfield dairy", BrStyle.Plaster, 0);
            // Hay bales in the yard (chest-high cover).
            b.Strip(new Vec3d(358, y, 912), 6.0, false, 1);
            b.Strip(new Vec3d(384, y, 926), 4.5, true, 1);
            b.Strip(new Vec3d(370, y, 897), 4.5, true, 1);
            b.Poi(BrSegments.All[b.Id].HomePoiName, a, barn.T.Bounds, house.T.Bounds, stables.T.Bounds, dairy.T.Bounds);
            b.Open(new Rect2(352, 893, 396, 932));
        }

        // ================================================================ Radio Mast Hill

        private static void RadioMastHill(BrBuild b)
        {
            // The summit compound: walls 4.5 m tall, a gate on the south and one on the east.
            const double X0 = 168, Z0 = 1096, X1 = 212, Z1 = 1140;
            double L = b.Foot(X0, Z0, X1, Z1, 2.0);
            NwKit.Walls(b, X0, Z0, X1, Z1, L, 3, (0, 28.0), (3, 6.0));
            NwPlan hall = NwKit.Hall(b, 171.5, L, 1099.5, "Transmitter hall", BrStyle.Concrete, 2, chests: 5);
            NwPlan mast = NwKit.Tower(b, 196.0, L, 1117.5, 8, "Radio mast", BrStyle.Steel, 0, lattice: true, chestEvery: 1, firstChest: 1, fx: true);
            // The spire: a slim hollow pillar of tiles on the mast's roof (12 m), over the room column's middle cell.
            double top = mast.T.Top, sx = 196.0 + Col + 2.4, sz = 1117.5 + 6.3 + Col + 2.4;
            b.Strip(new Vec3d(sx, top, sz), 1.5, true, 8);
            b.Strip(new Vec3d(sx, top, sz + 1.2), 1.5, true, 8);
            b.Strip(new Vec3d(sx, top, sz + 0.3), 0.9, false, 8);
            b.Strip(new Vec3d(sx + 1.2, top, sz + 0.3), 0.9, false, 8);
            NwPlan gen = NwKit.Hut(b, 171.5, L, 1129.5, 0, "Generator shed", BrStyle.Concrete, 2);
            NwPlan fuel = NwKit.Row(b, 181.0, L, 1129.5, 2, true, false, 0b10, 0b01, "Fuel store", BrStyle.Industrial, 3);
            b.Chest(new Vec3d(205, L, 1108), Math.PI);
            b.Strip(new Vec3d(198, L, 1111), 4.5, true, 1); // sandbags
            b.Item(Item.Grenade, 2, new Vec3d(193.5, L, 1100.5));
            b.Poi("Radio Mast Hill", new Vec3d(190, L, 1118), hall.T.Bounds, mast.T.Bounds, gen.T.Bounds, fuel.T.Bounds);
            b.Open(new Rect2(X0, Z0, X1, Z1));

            // The crag: a bunker dug in where the shelf meets the hill, a lookout at its far tip, a low wall on its west edge.
            double cy = NwKit.Foot(b, 126.0, 1150.0, 3, 2, 2.0, NorthWestSegment.CragTop);
            NwPlan bunker = NwKit.Bunker(b, 126.0, cy, 1150.0, "Crag bunker");
            b.Item(Item.Launcher, 6, new Vec3d(129.5, cy, 1152.5));
            double ly = NwKit.Foot(b, 118.5, 1167.5, 1, 1, 1.5, NorthWestSegment.CragTop);
            NwPlan look = NwKit.Hut(b, 118.5, ly, 1167.5, 3, "Crag lookout", BrStyle.Stone, 1);
            b.Poi("The Crag", new Vec3d(132, cy, 1160), bunker.T.Bounds, look.T.Bounds);
            b.Open(new Rect2(NorthWestSegment.CragX0, NorthWestSegment.CragZ0, NorthWestSegment.CragX1, NorthWestSegment.CragZ1));

            // Shepherd huts on the flanks, doors downhill.
            HutOn(b, 236.0, 1056.0, 3, "Shepherd hut", 0);
            HutOn(b, 124.0, 1078.0, 2, "Shepherd hut", 1);
            HutOn(b, 210.0, 1190.0, 1, "Shepherd hut", 2);
            HutOn(b, 90.0, 1205.0, 1, "Rim hut", 3);
        }

        private static NwPlan HutOn(BrBuild b, double x, double z, int side, string name, int variant)
        {
            double y = NwKit.Foot(b, x, z, 1, 1, 2.2);
            return NwKit.Hut(b, x, y, z, side, name, BrStyle.Stone, variant);
        }

        // ================================================================ Silo Row

        private static void SiloRow(BrBuild b)
        {
            // The elevator and its six silos stand on one footing; the silos touch (their roofs are one deck).
            double y = b.Foot(296.0, 990.0, 328.7, 1009.2, 2.5);
            NwPlan elev = NwKit.Tower(b, 296.0, y, 990.0, 7, "Grain elevator", BrStyle.Industrial, 0, lattice: false, chestEvery: 1, firstChest: 0,
                before: p =>
                {
                    p.WZ(0, 2, 1, WallKind.Wall);       // the silos stand against its east side
                    p.WX(0, 3, 1, WallKind.ShutDoor);   // so the shut door is on the north
                    for (int s = 1; s < 4; s++) for (int j = 0; j < 3; j++) p.WZ(s, 2, j, WallKind.Wall);
                    p.WZ(4, 2, 0, WallKind.Window);     // floor 4: step out onto the silo deck
                    p.WZ(4, 2, 1, WallKind.Window);
                });
            for (int k = 0; k < 6; k++)
            {
                double sx = 308.9 + (k % 3) * NwKit.Span(1), sz = 990.0 + (k / 3) * NwKit.Span(1);
                NwKit.Silo(b, sx, y, sz, 4, true, k);
            }
            double wy = NwKit.Foot(b, 264.0, 1005.0, 4, 3, 2.5);
            GreyboxBlock.Building shed = BrKit.Warehouse(b, 264.0, wy, 1005.0);
            NwKit.MarkSealed(b, 3);
            double oy = NwKit.Foot(b, 275.0, 975.0, 2, 1, 2.0);
            NwPlan office = NwKit.Row(b, 275.0, oy, 975.0, 2, true, false, 0b10, 0b01, "Weighbridge office", BrStyle.Plaster, 2);
            double gy = NwKit.Foot(b, 318.0, 1012.0, 3, 1, 2.0);
            NwPlan store = NwKit.Row(b, 318.0, gy, 1012.0, 3, true, false, 0b010, 0b001, "Grain store", BrStyle.Industrial, 2);
            b.Chest(new Vec3d(302.0, y, 989.3), Math.PI);
            b.Strip(new Vec3d(286.0, y, 991.0), 4.5, false, 1);
            b.Poi("Silo Row", new Vec3d(305, y, 1000), elev.T.Bounds, shed.Bounds, office.T.Bounds, store.T.Bounds);
        }

        // ================================================================ Water Tower (north of the spawn)

        private static void WaterTower(BrBuild b)
        {
            double y = b.Foot(226.0, 985.0, 249.6, 1004.2, 2.5);
            NwPlan tower = NwKit.Tower(b, 226.0, y, 985.0, 6, "Water tower", BrStyle.Concrete, 3, lattice: true, chestEvery: 2, firstChest: 1,
                before: p => p.Outer(5, WallKind.Wall));
            NwPlan pump = NwKit.Row(b, 243.0, y, 990.0, 2, false, false, 0b10, 0b01, "Pump house", BrStyle.Brick, 2);
            b.Poi("Water Tower", new Vec3d(235, y, 995), tower.T.Bounds, pump.T.Bounds);
        }

        // ================================================================ Westcott Orchard (behind the spawn)

        private static void Westcott(BrBuild b)
        {
            double py = NwKit.Foot(b, 92.0, 890.0, 4, 3, 2.5);
            GreyboxBlock.Building pack = BrKit.Warehouse(b, 92.0, py, 890.0);
            NwKit.MarkSealed(b, 3);
            double hy = NwKit.Foot(b, 130.0, 892.0, 2, 3, 2.0);
            b.Style = BrStyle.Brick; b.Variant = 2;
            GreyboxBlock.Building house = BrKit.House(b, 130.0, hy, 892.0);
            double cy = NwKit.Foot(b, 150.0, 890.0, 3, 4, 2.5);
            NwPlan barn = NwKit.Barn(b, 150.0, cy, 890.0, 3, 4, "Cider barn", BrStyle.Timber, 2, chests: 5);
            double sy = NwKit.Foot(b, 186.0, 890.0, 1, 3, 2.0);
            NwPlan sties = NwKit.Row(b, 186.0, sy, 890.0, 3, false, false, 0b100, 0b001, "Pigsties", BrStyle.Brick, 3);
            double s1 = NwKit.Foot(b, 176.0, 962.0, 1, 1, 2.2);
            BrKit.Shed(b, 176.0, s1, 962.0);
            NwKit.MarkSealed(b);
            double s2 = NwKit.Foot(b, 120.0, 1002.0, 1, 1, 2.2);
            BrKit.Shed(b, 120.0, s2, 1002.0);
            NwKit.MarkSealed(b);
            double ny = NwKit.Foot(b, 110.0, 928.0, 1, 1, 2.2);
            NwKit.Hut(b, 110.0, ny, 928.0, 0, "Hen house", BrStyle.Timber, 1);
            NwKit.Orchard(b, 100.0, 945.0, 8, 6, 9.0, 9.5, 11);
            b.Poi("Westcott Orchard", new Vec3d(135, py, 935), pack.Bounds, house.Bounds, barn.T.Bounds, sties.T.Bounds);
        }

        // ================================================================ Greyfield Green (north of home)

        private static void Green(BrBuild b)
        {
            double hy = NwKit.Foot(b, 352.0, 985.0, 2, 3, 2.0);
            b.Style = BrStyle.Plaster; b.Variant = 1;
            GreyboxBlock.Building house = BrKit.House(b, 352.0, hy, 985.0);
            double ry = NwKit.Foot(b, 370.0, 1020.0, 3, 1, 2.0);
            NwPlan row = NwKit.Row(b, 370.0, ry, 1020.0, 3, true, false, 0b010, 0b100, "Green cottages", BrStyle.Painted, 2);
            double fy = NwKit.Foot(b, 405.0, 985.0, 1, 2, 2.0);
            NwPlan fb = NwKit.FieldBarn(b, 405.0, fy, 985.0, false, false, "Green barn", 1);
            double ey = NwKit.Foot(b, 352.0, 1008.0, 1, 2, 2.0);
            NwKit.Row(b, 352.0, ey, 1008.0, 2, false, false, 0b01, 0b10, "Green cottages", BrStyle.Painted, 3);
            double cy2 = NwKit.Foot(b, 401.0, 1008.0, 2, 3, 2.0);
            b.Style = BrStyle.Brick; b.Variant = 3;
            GreyboxBlock.Building east = BrKit.House(b, 401.0, cy2, 1008.0);
            double ky = NwKit.Foot(b, 372.0, 966.0, 1, 1, 2.2);
            NwKit.Rick(b, 372.0, ky, 966.0, 1);
            b.Poi("Greyfield Green", new Vec3d(385, hy, 1000), house.Bounds, row.T.Bounds, fb.T.Bounds, east.Bounds);
            b.Open(new Rect2(372, 988, 398, 1012));
        }

        // ================================================================ The Holloway hamlet

        private static void Holloway(BrBuild b)
        {
            double iy = NwKit.Foot(b, 352.0, 1069.0, 3, 3, 2.0);
            NwPlan inn = NwKit.FarmHouse(b, 352.0, iy, 1069.0, "The Plough inn", BrStyle.Plaster, 3, chests: 4);
            double ry = NwKit.Foot(b, 320.0, 1070.0, 4, 1, 2.0);
            NwPlan row = NwKit.Row(b, 320.0, ry, 1070.0, 4, true, false, 0b0100, 0b0001, "Holloway cottages", BrStyle.Brick, 1);
            double cy = b.Foot(378.0, 1069.0, 378.0 + NwKit.Span(2), 1094.5 + NwKit.Span(3), NwKit.MinApron);
            NwPlan chapel = NwKit.Chapel(b, 378.0, cy, 1069.0, "Greyfield chapel");
            NwPlan bell = NwKit.Tower(b, 378.0, cy, 1094.5, 4, "Bell tower", BrStyle.Stone, 2, lattice: false, chestEvery: 2, firstChest: 2,
                before: p => { p.WX(0, 0, 1, WallKind.Wall); p.Outer(3, WallKind.Window); });
            double ly = NwKit.Foot(b, 296.0, 1073.0, 2, 1, 2.0);
            NwKit.Row(b, 296.0, ly, 1073.0, 2, true, false, 0b10, 0b01, "Lane cottages", BrStyle.Plaster, 0);
            double my = NwKit.Foot(b, 330.0, 1022.0, 2, 1, 2.0);
            NwPlan smithy = NwKit.Row(b, 330.0, my, 1022.0, 2, true, true, 0b01, 0b10, "Smithy", BrStyle.Stone, 0);
            b.Poi("The Holloway", new Vec3d(360, BrGround.Base, 1050), inn.T.Bounds, row.T.Bounds, chapel.T.Bounds, bell.T.Bounds, smithy.T.Bounds);
        }

        // ================================================================ Hollins Farm (north east)

        private static void Hollins(BrBuild b)
        {
            double y = b.Foot(304.0, 1176.0, 343.5, 1207.8, 2.5);
            NwPlan barn = NwKit.Barn(b, 318.0, y, 1176.0, 4, 5, "Hollins red barn", BrStyle.Painted, 1, chests: 5);
            NwKit.Silo(b, 304.0, y, 1186.0, 4, false, 7);
            NwKit.Silo(b, 304.0, y, 1186.0 + NwKit.Span(1), 4, true, 8);
            double hy = NwKit.Foot(b, 372.0, 1192.0, 3, 3, 2.0);
            NwPlan house = NwKit.FarmHouse(b, 372.0, hy, 1192.0, "Hollins farmhouse", BrStyle.Stone, 0, chests: 3);
            double cy = NwKit.Foot(b, 350.0, 1160.0, 4, 1, 2.0);
            NwPlan cows = NwKit.Row(b, 350.0, cy, 1160.0, 4, true, true, 0b1000, 0b0001, "Cow shed", BrStyle.Timber, 1);
            double wy = NwKit.Foot(b, 390.0, 1150.0, 4, 3, 2.5);
            GreyboxBlock.Building shed = BrKit.Warehouse(b, 390.0, wy, 1150.0);
            NwKit.MarkSealed(b, 3);
            double ky = b.Foot(285.0, 1140.0, 307.5, 1162.5, 1.5);
            BrKit.Compound(b, 285.0, ky, 1140.0);
            double ry = NwKit.Foot(b, 355.0, 1200.0, 1, 1, 2.2);
            NwKit.Rick(b, 355.0, ry, 1200.0, 2);
            double gy2 = NwKit.Foot(b, 346.0, 1212.0, 1, 1, 2.25, y);
            NwKit.Hut(b, 346.0, gy2, 1212.0, 3, "Granary", BrStyle.Timber, 3);
            NwKit.Orchard(b, 364.0, 1123.0, 5, 3, 9.0, 9.0, 23);
            b.Poi("Hollins Farm", new Vec3d(352, y, 1185), barn.T.Bounds, house.T.Bounds, cows.T.Bounds, shed.Bounds);
        }

        // ================================================================ Fields, hedges and the outliers

        private static void Fields(BrBuild b)
        {
            // Mast Farm at the hill's north foot.
            double my = NwKit.Foot(b, 252.0, 1192.0, 2, 3, 2.0);
            b.Style = BrStyle.Stone; b.Variant = 2;
            GreyboxBlock.Building mast = BrKit.House(b, 252.0, my, 1192.0);
            double fy = NwKit.Foot(b, 272.0, 1196.0, 2, 1, 2.0);
            NwKit.FieldBarn(b, 272.0, fy, 1196.0, true, false, "Mast Farm barn", 2);
            b.Poi("Mast Farm", new Vec3d(262, my, 1200), mast.Bounds);

            // The Pond Mill by the dew pond.
            double py = NwKit.Foot(b, 88.0, 1012.0, 3, 4, 2.5);
            NwPlan mill = NwKit.Barn(b, 88.0, py, 1012.0, 3, 4, "Pond mill", BrStyle.Stone, 1, chests: 5, fx: true);
            b.Poi("Pond Mill", new Vec3d(98, py, 1025), mill.T.Bounds);

            // The Burnt Farm on the swell.
            double ry = NwKit.Foot(b, 305.0, 1100.0, 3, 2, 2.0);
            NwPlan ruin = NwKit.Ruin(b, 305.0, ry, 1100.0, 7, "Burnt farm");
            b.Poi("Burnt Farm", new Vec3d(315, ry, 1106), ruin.T.Bounds);

            // Field barns and hay ricks: cover and a chest each.
            FieldBarnOn(b, 300.0, 957.0, true, false, 3);
            FieldBarnOn(b, 280.0, 1090.0, true, true, 4);
            FieldBarnOn(b, 125.0, 1020.0, false, true, 5);
            RickOn(b, 330.0, 965.0, 3);
            RickOn(b, 350.0, 1100.0, 4);
            RickOn(b, 160.0, 1022.0, 5);
            RickOn(b, 270.0, 1150.0, 6);
            RickOn(b, 405.0, 1100.0, 7);
            RickOn(b, 240.0, 965.0, 8);

            // The sheepfold at the hill's south foot: 3 m walls (a mantle), one gate, a shelter with a chest.
            double fy2 = b.Foot(192.0, 1012.0, 210.0, 1030.0, 1.5);
            NwKit.Walls(b, 192.0, 1012.0, 210.0, 1030.0, fy2, 2, (0, 7.5));
            NwKit.Hut(b, 194.0, fy2, 1021.5, 3, "Sheepfold shelter", BrStyle.Stone, 2);
            b.Poi("Sheepfold", new Vec3d(201, fy2, 1021), new Aabb(new Vec3d(192, fy2, 1012), new Vec3d(210, fy2 + 3, 1030)));

            // Dry stone walls along the hill's contours: chest-high cover for anyone climbing.
            foreach (var (r, deg, len) in HillWalls) HillWall(b, r, deg, len);

            // Hedgerows between the fields (gaps are gateways).
            NwKit.Hedge(b, 255, 955, 289, 955, 4.6, 1, (0.45, 0.6));
            NwKit.Hedge(b, 322, 955, 352, 955, 4.6, 2);
            NwKit.Hedge(b, 262, 963, 262, 994, 4.6, 3, (0.4, 0.55));
            NwKit.Hedge(b, 136, 1012, 186, 1012, 4.6, 4, (0.5, 0.62));
            NwKit.Hedge(b, 92, 945, 92, 1000, 4.6, 5, (0.45, 0.6));
            NwKit.Hedge(b, 340, 1094, 340, 1125, 4.6, 6, (0.4, 0.55));
            NwKit.Hedge(b, 288, 1122, 332, 1122, 4.6, 7, (0.4, 0.55));
            NwKit.Hedge(b, 278, 1170, 300, 1170, 4.6, 8);
            NwKit.Hedge(b, 387, 960, 414, 960, 4.6, 9, (0.45, 0.6));
            // Field walls: chest-high stone, each on its own little footing.
            foreach (var (x, z, len, alongX) in FieldWalls) FieldWall(b, x, z, len, alongX);
            // A few trees: the north foot and the field corners.
            (double x, double z)[] trees = { (125, 1217), (150, 1214), (226, 1214), (418, 1080), (270, 1035), (254, 1030) };
            for (int k = 0; k < trees.Length; k++) b.Tree(trees[k].x, trees[k].z, 900 + k, k % 3 == 0);
        }

        /// <summary>Dry stone walls on the hill: (distance from the summit, bearing in degrees from east, length).
        /// Each runs along the contour (along x on the north and south flanks, along z on the east and west).</summary>
        private static readonly (double r, double deg, double len)[] HillWalls =
        {
            (50, 270, 9), (66, 255, 7.5), (66, 285, 7.5), (52, 0, 9), (68, 10, 7.5), (52, 180, 7.5), (70, 190, 9),
            (50, 90, 9), (68, 80, 7.5), (60, 315, 6), (60, 45, 6), (62, 225, 6), (80, 300, 7.5), (82, 30, 7.5),
        };

        /// <summary>Field walls: (x, z, length, along x).</summary>
        private static readonly (double x, double z, double len, bool alongX)[] FieldWalls =
        {
            (150, 1040, 12, true), (230, 1040, 10.5, true), (330, 1134, 12, true), (288, 1134, 9, true), (412, 1060, 12, false),
            (300, 1030, 9, false), (172, 940, 9, true), (205, 965, 9, false), (330, 940, 9, true), (415, 1185, 12, false),
        };

        private static void FieldWall(BrBuild b, double x, double z, double len, bool alongX)
        {
            double x1 = alongX ? x + len : x + 0.3, z1 = alongX ? z + 0.3 : z + len;
            if (!b.IsClear(new Rect2(x, z, x1, z1), 2.0)) return;
            if (!b.TryFoot(x, z, x1, z1, 1.2, out double y)) return;
            b.Strip(new Vec3d(x, y, z), len, alongX, 1);
        }

        private static void HillWall(BrBuild b, double r, double deg, double len)
        {
            double a = deg * Math.PI / 180, cx = NorthWestSegment.HillX + Math.Cos(a) * r, cz = NorthWestSegment.HillZ + Math.Sin(a) * r;
            bool alongX = Math.Abs(Math.Sin(a)) >= Math.Abs(Math.Cos(a));
            double x0 = alongX ? cx - len / 2 : cx - 0.15, z0 = alongX ? cz - 0.15 : cz - len / 2;
            double x1 = alongX ? cx + len / 2 : cx + 0.15, z1 = alongX ? cz + 0.15 : cz + len / 2;
            var rect = new Rect2(x0, z0, x1, z1);
            if (!b.IsClear(rect, 2.0)) return;
            if (!b.TryFoot(x0, z0, x1, z1, 1.2, out double y)) return;
            b.Strip(new Vec3d(x0, y, z0), len, alongX, 1);
        }

        private static void FieldBarnOn(BrBuild b, double x, double z, bool alongX, bool open, int variant)
        {
            double y = NwKit.Foot(b, x, z, alongX ? 2 : 1, alongX ? 1 : 2, 2.0);
            NwKit.FieldBarn(b, x, y, z, alongX, open, "Field barn", variant);
        }

        private static void RickOn(BrBuild b, double x, double z, int variant)
        {
            double y = NwKit.Foot(b, x, z, 1, 1, 2.2);
            NwKit.Rick(b, x, y, z, variant);
        }
    }
}
