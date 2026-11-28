using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Builds the North segment, the Train Yard (<see cref="NorthSegment"/>). Every building rewards breaching: the
    /// station's ticket office and strongroom are sealed, the goods shed's long sides are blank and its storerooms
    /// sealed, sealed boxcars hold loot, the engine shed's hall is open only at its track doors, the signal tower's
    /// fast way up is a hole. Coordinates are map metres (x east, z north).
    /// </summary>
    internal static class NorthBuild
    {
        private const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, Base = BrGround.Base;
        private const double Yard = Base;

        /// <summary>Track centre lines in the yard, south to north, and the island platforms between them.</summary>
        public static readonly double[] Tracks = { 955, 961, 973, 979, 991, 997 };
        public const double P2Z0 = 965.5, P2Z1 = 968.5, P1Z0 = 983.5, P1Z1 = 986.5;
        /// <summary>The level crossing: the road from the bridge under the embankment to the station's platform.</summary>
        public const double CrossX0 = 640, CrossX1 = 666;
        /// <summary>The footbridge from the embankment top over the yard to the main platform.</summary>
        public const double FootX0 = 712, FootX1 = 715, FootZ0 = 933, FootZ1 = 1001, FootStairZ1 = 1013;
        /// <summary>The footbridge over the cutting, at the ridge top.</summary>
        public const double CutBridgeX0 = 798.5, CutBridgeX1 = 801.5, CutBridgeZ0 = 962, CutBridgeZ1 = 990;
        /// <summary>The engine shed over the west tracks.</summary>
        public const double ShedX0 = 496, ShedZ0 = 950.5;

        private static bool NearHome(BrBuild b, double x, double z) =>
            Math.Sqrt((x - b.Anchor.X) * (x - b.Anchor.X) + (z - b.Anchor.Z) * (z - b.Anchor.Z)) <= BrLayout.HomeRadius + 1.5;

        public static void Build(BrBuild b)
        {
            BuildStation(b);
            GreyboxBlock.Building shed = BuildEngineShed(b);
            BuildYard(b, shed);
            BuildEmbankment(b);
            BuildRidge(b);
            BuildGoodsYard(b);
            BuildSignalTower(b);
            BuildTerminal(b);
            BuildFill(b);
        }

        // ---------------------------------------------------------------- Junction Station (the home POI)

        /// <summary>
        /// A two-storey station, 10 x 3 modules, across the anchor (its long south face blocks every pad's view past
        /// the anchor). Ground floor: stairs in both end lanes, a double-height concourse open to the plaza (two wide
        /// doors), a departures screen behind them, a sealed ticket office (break in from the concourse or the
        /// corridor), a parcels office. Upstairs: a gallery round the concourse void, offices, a sealed strongroom.
        /// Flat roof with a parapet, reached by both stairwells. A platform canopy on the yard side. Ten chests: eight in
        /// and on the station, one on each island platform.
        /// </summary>
        private static void BuildStation(BrBuild b)
        {
            Vec3d a = b.Anchor;
            var t = new GreyboxBlock.Building(a.X - (10 * M + Col) / 2, a.Z - 3 * M / 2 - Col / 2, 10, 3, 2);
            b.Foot(t, 2, Base);
            b.Stairwell(t, 0, true, +1);
            b.Stairwell(t, 9, true, -1);
            for (int s = 0; s < 2; s++)
            {
                t.AlongZ[s, 1, 0] = WallKind.Door; t.AlongZ[s, 1, 1] = WallKind.Wall; t.AlongZ[s, 1, 2] = WallKind.Door;
                t.AlongZ[s, 9, 0] = WallKind.Door; t.AlongZ[s, 9, 1] = WallKind.Wall; t.AlongZ[s, 9, 2] = WallKind.Door;
            }
            // ---- Ground floor.
            t.AlongZ[0, 1, 2] = WallKind.Wall;       // no way from the west stairs into the ticket office
            t.AlongX[0, 0, 0] = WallKind.ShutDoor;   // platform -> west stairs
            t.AlongX[0, 1, 1] = WallKind.Wall;       // ticket office (cells 1-2, rows 1-2) | corridor
            t.AlongX[0, 1, 2] = WallKind.Wall;
            t.AlongZ[0, 3, 1] = WallKind.Wall;       // ticket office | concourse
            t.AlongZ[0, 3, 2] = WallKind.Wall;
            t.AlongX[0, 3, 3] = WallKind.Window;     // the front onto the plaza: two wide doors
            t.AlongX[0, 3, 4] = WallKind.WideDoor;
            t.AlongX[0, 3, 5] = WallKind.WideDoor;
            t.AlongX[0, 3, 6] = WallKind.Window;
            t.AlongX[0, 0, 3] = WallKind.ShutDoor;   // the back onto the platform: doors off to the sides
            t.AlongX[0, 0, 6] = WallKind.ShutDoor;
            t.AlongX[0, 1, 4] = WallKind.Wall;       // departures screen in the concourse
            t.AlongX[0, 1, 5] = WallKind.Wall;
            t.AlongZ[0, 7, 0] = WallKind.Wall;       // parcels office (cells 7-8) | concourse, one door
            t.AlongZ[0, 7, 1] = WallKind.Door;
            t.AlongZ[0, 7, 2] = WallKind.Wall;
            t.AlongX[0, 3, 8] = WallKind.ShutDoor;   // parcels office street door
            t.AlongX[0, 3, 9] = WallKind.ShutDoor;   // street -> east stairs
            t.Hole[0, 4, 1] = true;                  // the concourse void
            t.Hole[0, 5, 1] = true;
            // ---- Upstairs.
            t.AlongZ[1, 3, 0] = WallKind.Wall;       // west offices | gallery
            t.AlongZ[1, 3, 1] = WallKind.Door;
            t.AlongZ[1, 3, 2] = WallKind.Wall;
            t.AlongX[1, 1, 7] = WallKind.Wall;       // strongroom (cell 7, rows 1-2): sealed
            t.AlongZ[1, 7, 0] = WallKind.Door;       // gallery -> east corridor
            t.AlongZ[1, 7, 1] = WallKind.Wall;
            t.AlongZ[1, 7, 2] = WallKind.Wall;
            t.AlongZ[1, 8, 1] = WallKind.Wall;
            t.AlongZ[1, 8, 2] = WallKind.Wall;
            for (int i = 3; i <= 6; i++) { t.AlongX[1, 0, i] = WallKind.Window; t.AlongX[1, 3, i] = WallKind.Window; }
            t.AlongX[1, 0, 1] = WallKind.Window;
            t.AlongX[1, 3, 2] = WallKind.Window;
            t.AlongX[1, 0, 8] = WallKind.Window;
            b.Style = BrStyle.Stone; b.Variant = 1;
            b.Raise(t, "Junction Station");
            BrKit.Parapet(b, t);
            b.Chest(t, 1, 1, 0, Math.PI / 2);   // ticket office
            b.Chest(t, 2, 2, 0, -Math.PI / 2);
            b.Chest(t, 3, 2, 0, Math.PI);       // concourse
            b.Chest(t, 8, 1, 0, -Math.PI / 2);  // parcels office
            b.Chest(t, 2, 1, 1, Math.PI / 2);   // upstairs office
            b.Chest(t, 7, 1, 1, Math.PI);       // strongroom
            b.Chest(t, 7, 2, 1, 0);
            b.Chest(t, 5, 0, 2, 0);             // roof

            // The platform canopy: six modules of open bays (lintel and roof only) between the station and track 1.
            var c = new GreyboxBlock.Building(a.X - 3 * M - Col / 2, 1003.6, 6, 1, 1);
            b.Foot(c, 1, Base);
            for (int i = 0; i < 6; i++) { c.AlongX[0, 0, i] = WallKind.WideDoor; c.AlongX[0, 1, i] = WallKind.WideDoor; }
            c.AlongZ[0, 0, 0] = WallKind.WideDoor;
            c.AlongZ[0, 6, 0] = WallKind.WideDoor;
            b.Style = BrStyle.Steel; b.Variant = 1;
            b.Raise(c, "Platform canopy");
            b.Poi(BrSegments.All[b.Id].HomePoiName, a, t.Bounds, c.Bounds);
        }

        // ---------------------------------------------------------------- the engine shed

        /// <summary>
        /// The engine shed over the west end of tracks 1-3: 8 x 5 modules, a hall two storeys tall with wide track doors
        /// at both ends, an inspection platform and parked wagons inside; along its north side offices on two floors
        /// with the stairs and a balcony over the hall; a flat roof. Its long sides are blank: breach them.
        /// </summary>
        private static GreyboxBlock.Building BuildEngineShed(BrBuild b)
        {
            var t = new GreyboxBlock.Building(ShedX0, ShedZ0, 8, 5, 2);
            double y = b.Foot(t, 1.5, Yard);
            b.Stairwell(t, 4, false, +1);
            for (int i = 0; i < 8; i++) for (int j = 0; j < 4; j++) t.Hole[0, i, j] = true; // the hall: two storeys tall
            foreach (int j in new[] { 0, 1, 3 }) { t.AlongZ[0, 0, j] = WallKind.WideDoor; t.AlongZ[0, 8, j] = WallKind.WideDoor; }
            t.AlongZ[0, 0, 4] = WallKind.ShutDoor;   // west: into the office row
            for (int j = 0; j < 4; j++) { t.AlongZ[1, 0, j] = WallKind.Window; t.AlongZ[1, 8, j] = WallKind.Window; }
            for (int i = 0; i < 8; i++)
            {
                t.AlongX[0, 4, i] = i == 0 || i == 5 ? WallKind.Door : WallKind.Wall;          // hall | office row
                t.AlongX[1, 4, i] = i == 5 || i == 6 ? WallKind.None : WallKind.Wall;          // upstairs: a balcony over the hall
                if (i % 2 == 1) t.AlongX[1, 0, i] = WallKind.Window;                          // high windows on the south side
                if (i % 2 == 0 && i > 3) t.AlongX[1, 5, i] = WallKind.Window;
            }
            t.AlongZ[0, 4, 4] = WallKind.Wall;   // ground: stair hall (cells 0-3) | office (cells 4-7)
            t.AlongZ[1, 5, 4] = WallKind.Wall;   // upstairs: landing (cells 3-4) | office (5-7)
            t.AlongZ[1, 4, 4] = WallKind.None;
            b.Style = BrStyle.Brick; b.Variant = 2;
            b.Raise(t, "Engine shed");
            BrKit.Parapet(b, t);
            // Inside: an inspection platform, wagons on the three tracks (one of each kind of the train kit's pattern).
            NorthKit.Platform(b, t.Cx(0) + 4, P2Z0, t.Cx(8) - 4, P2Z1, y);
            b.Chest(new Vec3d(t.Cx(4), y + 1.5, (P2Z0 + P2Z1) / 2), 0);
            NorthKit.Train(b, t.Cx(0) + 3, t.Cx(8) - 3, Tracks[0], y, 11, 2, (x, z) => false, 1);
            NorthKit.Train(b, t.Cx(1), t.Cx(1) + NorthKit.WagonLen, Tracks[1], y, 12, 1, (x, z) => false, 1);
            NorthKit.Train(b, t.Cx(3) + 3, t.Cx(8) - 3, Tracks[2], y, 13, 0, (x, z) => false);
            b.Chest(t, 6, 4, 0, Math.PI);   // office
            b.Chest(t, 6, 4, 1, Math.PI);   // upstairs office
            b.Chest(t, 7, 4, 2, Math.PI);   // roof
            b.Item(Item.Grenade, 2, new Vec3d(t.Cx(0) + 2, y, t.Cz(4) + 3));
            b.Poi("Engine Shed", new Vec3d(t.Cx(4), y, t.Cz(2)), t.Bounds);
            return t;
        }

        // ---------------------------------------------------------------- the yard

        /// <summary>
        /// Trains on all six tracks (gaps at the level crossing and under the footbridge's piers), the two island
        /// platforms, buffer stops at the west end, the footbridge from the embankment to the main platform.
        /// </summary>
        private static void BuildYard(BrBuild b, GreyboxBlock.Building engineShed)
        {
            double shedX1 = engineShed.Cx(8) + Col;
            Func<double, double, bool> noChest = (x, z) => NearHome(b, x, z);
            // West of the crossing (east of the shed for the tracks it covers), east of the crossing, and into the cutting.
            uint seed = 100;
            for (int k = 0; k < Tracks.Length; k++)
            {
                double z = Tracks[k];
                bool shed = z < engineShed.Cz(5);
                double w0 = shed ? shedX1 + 4 : 478;
                NorthKit.Train(b, w0, CrossX0 - 2, z, Yard, seed++, 2, noChest);
                double eEnd = z > NorthSegment.CutZ0 && z < NorthSegment.CutZ1 ? 742 : 752;
                NorthKit.Train(b, CrossX1 + 2, eEnd, z, Yard, seed++, 2, noChest);
                if (!shed) b.Strip(new Vec3d(474, Yard, z - 1.5), 3, false, 1); // buffer stop
            }
            // A train standing in the cutting.
            NorthKit.Train(b, 760, 830, Tracks[2], Yard, 120, 2, noChest);

            // Island platforms (split at the crossing; the west one of P2 starts past the shed).
            NorthKit.Platform(b, shedX1 + 6, P2Z0, CrossX0, P2Z1, Yard);
            NorthKit.Platform(b, CrossX1, P2Z0, 740, P2Z1, Yard);
            NorthKit.Platform(b, 520, P1Z0, CrossX0, P1Z1, Yard);
            NorthKit.Platform(b, CrossX1, P1Z0, 745, P1Z1, Yard);
            // Relay cabinets in the alleys between the tracks, two per alley on opposite halves and staggered, so no
            // alley is a clear sight line from one end of the yard to the other but you can always run down it.
            (double lo, double hi)[] alleys = { (956.5, 959.5), (962.5, 965.5), (968.5, 971.5), (974.5, 977.5), (980.5, 983.5), (986.5, 989.5), (992.5, 995.5), (998.5, 1003.4) };
            for (int k = 0; k < alleys.Length; k++)
            {
                double mid = (alleys[k].lo + alleys[k].hi) / 2;
                bool even = k % 2 == 0;
                // Each half of the alley gets one cabinet west of the crossing and one east of it.
                foreach (double x in even ? new[] { 586.0, 734.0 } : new[] { 611.0, 691.0 })
                    NorthKit.Box(b, x, alleys[k].lo, 1.5, mid - alleys[k].lo, true, Yard, 2, true);
                foreach (double x in even ? new[] { 611.0, 691.0 } : new[] { 586.0, 734.0 })
                    NorthKit.Box(b, x, mid, 1.5, alleys[k].hi - mid, true, Yard, 2, true);
            }
            // The two home chests on the platforms, either side of the crossing.
            b.Chest(new Vec3d(CrossX0 - 4, Yard + 1.5, (P1Z0 + P1Z1) / 2), Math.PI / 2);
            b.Chest(new Vec3d(CrossX1 + 4, Yard + 1.5, (P1Z0 + P1Z1) / 2), -Math.PI / 2);

            // The footbridge: a deck 6 m up from the embankment top over every track, a parapet each side, piers in
            // three alleys, stairs down to the main platform at the north end.
            double top = Base + NorthSegment.EmbH;
            NorthKit.Deck(b, FootX0, FootZ0, FootX1, FootZ1, top);
            b.Strip(new Vec3d(FootX0, top, FootZ0), FootZ1 - FootZ0, false, 1);
            b.Strip(new Vec3d(FootX1 - Col, top, FootZ0), FootZ1 - FootZ0, false, 1);
            foreach (double pz in new[] { 963.85, 975.85, 987.85 }) NorthKit.Pier(b, FootX0 + 0.75, pz, Yard - 0.3, 4, true);
            b.Climb(new Aabb(new Vec3d(FootX0, Yard, FootZ1), new Vec3d(FootX1, top, FootStairZ1)), Dir.South);
            b.Chest(new Vec3d((FootX0 + FootX1) / 2, top, 958), 0);

            b.Open(new Rect2(NorthSegment.YardX0, NorthSegment.YardZ0, NorthSegment.YardX1, NorthSegment.YardZ1));
            b.Poi("Train Yard", new Vec3d(600, Yard, 976));
        }

        // ---------------------------------------------------------------- the embankment

        /// <summary>
        /// The main line on the embankment: a bridge deck over the road (a parapet each side), a train parked on each
        /// side of it, two sealed platelayers' huts on the top.
        /// </summary>
        private static void BuildEmbankment(BrBuild b)
        {
            double top = Base + NorthSegment.EmbH, z = NorthSegment.EmbZ;
            double x0 = NorthSegment.RoadX - NorthSegment.RoadHalf - 1, x1 = NorthSegment.RoadX + NorthSegment.RoadHalf + 1;
            NorthKit.Deck(b, x0, z - 4.5, x1, z + 4.5, top);
            b.Strip(new Vec3d(x0, top, z - 4.5), x1 - x0, true, 1);
            b.Strip(new Vec3d(x0, top, z + 4.5 - Col), x1 - x0, true, 1);
            Func<double, double, bool> none = (x, zz) => false;
            NorthKit.Train(b, 540, x0 - 3, z, top, 200, 2, none);
            NorthKit.Train(b, x1 + 3, 755, z, top, 201, 2, none);
            foreach (double hx in new[] { 505.0, 518.0 })
            {
                double y = b.Foot(hx, z - 3.3, hx + M + Col, z + 3.3, 0.5, top);
                BrKit.Shed(b, hx, y, z - 3.3);
            }
            b.Open(new Rect2(NorthSegment.EmbX0, z - 18, NorthSegment.EmbX1, z + 18));
        }

        // ---------------------------------------------------------------- Signal Ridge and the cutting

        private static void BuildRidge(BrBuild b)
        {
            double top = Base + NorthSegment.RidgeH;
            // The footbridge over the cut.
            NorthKit.Deck(b, CutBridgeX0, CutBridgeZ0, CutBridgeX1, CutBridgeZ1, top);
            b.Strip(new Vec3d(CutBridgeX0, top, CutBridgeZ0), CutBridgeZ1 - CutBridgeZ0, false, 1);
            b.Strip(new Vec3d(CutBridgeX1 - Col, top, CutBridgeZ0), CutBridgeZ1 - CutBridgeZ0, false, 1);
            // The signal box on the ridge north of the cut: a lever room downstairs, the glazed box upstairs.
            var t = new GreyboxBlock.Building(NorthSegment.RidgeX - M - Col / 2, 1010, 2, 3, 2); // 25 m back from the cut: its footing's bank must not fill the cut
            b.Foot(t, 2);
            b.Stairwell(t, 1, true, +1);
            for (int s = 0; s < 2; s++) { t.AlongZ[s, 1, 0] = WallKind.Door; t.AlongZ[s, 1, 1] = WallKind.Wall; t.AlongZ[s, 1, 2] = WallKind.Door; }
            t.AlongX[0, 0, 1] = WallKind.ShutDoor;   // south, into the stairs
            t.AlongZ[0, 0, 1] = WallKind.Window;     // the lever room's west window
            foreach (int j in new[] { 0, 1, 2 }) { t.AlongZ[1, 0, j] = WallKind.Window; t.AlongZ[1, 2, j] = WallKind.Window; }
            t.AlongX[1, 0, 0] = WallKind.Window; t.AlongX[1, 0, 1] = WallKind.Window; t.AlongX[1, 3, 0] = WallKind.Window;
            b.Style = BrStyle.Timber; b.Variant = 1;
            b.Raise(t, "Signal box");
            BrKit.Parapet(b, t);
            b.Chest(t, 0, 1, 0, Math.PI / 2);
            b.Chest(t, 0, 1, 1, Math.PI / 2);
            // A chest on the ridge south of the cut.
            b.SpotChest(NorthSegment.RidgeX, 942, Math.PI);
            b.Open(new Rect2(NorthSegment.CutX0, NorthSegment.CutZ0 - 2, NorthSegment.CutX1, NorthSegment.CutZ1 + 2));
            b.Poi("The Cutting", new Vec3d(NorthSegment.RidgeX, Base, (NorthSegment.CutZ0 + NorthSegment.CutZ1) / 2), t.Bounds);
        }

        // ---------------------------------------------------------------- the goods yard (north west)

        private static void BuildGoodsYard(BrBuild b)
        {
            // The goods shed: 12 x 3 modules, one tall storey, doors only at its ends: the long south side faces the
            // loading dock and is blank, so the short way in is a hole. Five sealed storerooms along the north side.
            var g = new GreyboxBlock.Building(470, 1080, 12, 3, 1);
            double y = b.Foot(g, 2);
            for (int i = 0; i < 12; i++) g.AlongX[0, 2, i] = i == 0 ? WallKind.Door : WallKind.Wall;
            for (int i = 2; i < 12; i += 2) g.AlongZ[0, i, 2] = WallKind.Wall;
            g.AlongZ[0, 0, 0] = WallKind.WideDoor;
            g.AlongZ[0, 12, 1] = WallKind.ShutDoor;
            b.Style = BrStyle.Industrial; b.Variant = 1;
            b.Raise(g, "Goods shed");
            BrKit.Parapet(b, g);
            for (int i = 2; i < 12; i += 2) b.Chest(g, i, 2, 0, Math.PI);
            b.Chest(g, 1, 2, 0, Math.PI);
            b.Chest(g, 4, 0, 0, 0);
            b.Chest(g, 9, 1, 0, Math.PI);
            // The loading dock along the blank side, and the siding in front of it.
            double dy = b.Foot(474, 1073.5, 542, 1079.7, 0.5, y);
            NorthKit.Platform(b, 474, 1073.5, 542, 1079.4, dy);
            double sy = b.Foot(474, 1065.5, 546, 1070.5, 0.5, y);
            NorthKit.Train(b, 476, 546, 1068, sy, 300, 2, (x, z) => false);
            b.Item(Item.C4, 2, new Vec3d(g.Cx(0) + 3, y, g.Cz(0) + 3));
            b.Poi("Goods Yard", new Vec3d(508, y, 1088), g.Bounds);

            // Freight sheds: one tall storey, a wide door at the south end, a shut door at the north, a sealed office.
            (double x, double z)[] sheds = { (553, 1112), (573.5, 1112), (594, 1112), (553, 1141), (573.5, 1141) };
            b.Style = BrStyle.Painted;
            int v = 0;
            foreach (var p in sheds)
            {
                var f = new GreyboxBlock.Building(p.x, p.z, 2, 3, 1);
                b.Foot(f, 2);
                f.AlongX[0, 0, 0] = WallKind.WideDoor;
                f.AlongX[0, 3, 0] = WallKind.ShutDoor;
                f.AlongX[0, 2, 1] = WallKind.Wall;   // the office (east lane, back cell): sealed
                f.AlongZ[0, 1, 2] = WallKind.Wall;
                b.Variant = v++;
                b.Raise(f, "Freight shed");
                b.Chest(f, 1, 2, 0, Math.PI);
                b.Chest(f, 0, 1, 0, 0);
            }

            // The water tower on its hill: five storeys, the stairs up its west lane, the tank deck on the roof.
            double wx = NorthSegment.WaterX - M - Col / 2, wz = NorthSegment.WaterZ - 1.5 * M - Col / 2;
            double wy = b.Foot(wx, wz, wx + 2 * M + Col, wz + 3 * M + Col, 3);
            b.Style = BrStyle.Brick; b.Variant = 3;
            var water = BrKit.Tower(b, wx, wy, wz, 2, 3, 5, 2, "Water tower");
            b.Poi("Water Tower", new Vec3d(NorthSegment.WaterX, wy, NorthSegment.WaterZ), water.Bounds);

            // Railway cottages: rows of one-room houses with a sealed room each.
            (double x, double z, int sealedRoom)[] rows = { (520, 1208, 1), (556, 1208, 2), (556, 1176, 0) };
            b.Style = BrStyle.Brick;
            v = 0;
            foreach (var r in rows)
            {
                double ry = b.Foot(r.x, r.z, r.x + 4 * M + Col, r.z + M + Col, 2);
                b.Variant = 4 + v++;
                BrKit.Terrace(b, r.x, ry, r.z, 4, true, r.sealedRoom);
            }
        }

        // ---------------------------------------------------------------- the signal tower (north east)

        private static void BuildSignalTower(BrBuild b)
        {
            double x = 716, z = 1090, side = 3 * M + Col;
            double y = b.Foot(x, z, x + side, z + side, 3);
            b.Style = BrStyle.Concrete; b.Variant = 1;
            var tower = BrKit.Tower(b, x, y, z, 3, 3, 10, 2, "Signal tower");
            b.Poi("Signal Tower", new Vec3d(x + side / 2, y, z + side / 2), tower.Bounds);
            b.Item(Item.Launcher, 6, new Vec3d(x + side - 4, tower.Top, z + 4));

            // Station Row, between the approach and the terminal: the railway hotel (six storeys, its fast way up a
            // hole), the railway offices, the lamp stores (sealed).
            double hx = 694, hz = 1182;
            double hy = b.Foot(hx, hz, hx + side, hz + side, 3);
            b.Style = BrStyle.Plaster; b.Variant = 2;
            var hotel = BrKit.TowerPlan(b, hx, hy, hz, 3, 3, 6);
            b.Raise(hotel, "Railway hotel");
            BrKit.Parapet(b, hotel);
            for (int s = 1; s < 6; s += 2) b.Chest(hotel, 2, s % 4 == 1 ? 0 : 2, s, Math.PI);
            b.Chest(hotel, 2, 2, 6, Math.PI);
            b.Chest(hotel, 1, 1, 0, 0);
            b.Poi("Railway Hotel", new Vec3d(hx + side / 2, hy, hz + side / 2), hotel.Bounds);

            double ox = 721, oz = 1140;
            double oy = b.Foot(ox, oz, ox + 2 * M + Col, oz + 3 * M + Col, 2);
            b.Style = BrStyle.Brick; b.Variant = 7;
            BrKit.House(b, ox, oy, oz);
            foreach (double sx in new[] { 695.0, 708.6 })
            {
                double sy = b.Foot(sx, 1146, sx + M + Col, 1146 + M + Col, 2);
                b.Style = BrStyle.Industrial; b.Variant = 3;
                BrKit.Shed(b, sx, sy, 1146);
            }
        }

        // ---------------------------------------------------------------- the container terminal (north east)

        /// <summary>
        /// Rows of shipping containers (9 m x 3 m x 3 m tile boxes) one or two high in a grid either side of a central
        /// road, with a cross aisle and a gantry crane over the road. Some are sealed with a chest, some open-ended.
        /// </summary>
        private static void BuildTerminal(BrBuild b)
        {
            double y = Base;
            double[] cols = { 746, 757, 768, 779, 801, 812 }; // the road runs between 788 and 801; nothing in the seam band
            int chests = 0;
            for (int k = 0; k < 18; k++)
            {
                if (k == 9) continue; // the cross aisle
                double z0 = 1076 + 8 * k;
                for (int c = 0; c < cols.Length; c++)
                {
                    uint h = BrKit.Hash(c, k, 4007);
                    if (h % 100 < 38) continue; // an empty slot
                    bool two = (h >> 8) % 100 < 40;
                    bool open = (h >> 16) % 5 == 0;
                    NorthKit.Box(b, cols[c], z0, 9, 3, true, y, 2, true, open ? NorthKit.Side.OpenEnd : NorthKit.Side.Solid);
                    if (!open && (h >> 20) % 2 == 0) { b.Chest(new Vec3d(cols[c] + 4.5, y, z0 + 1.5), Math.PI / 2); chests++; }
                    if (two)
                    {
                        double y2 = y + 3 + Col;
                        NorthKit.Box(b, cols[c], z0, 9, 3, true, y2, 2, true);
                        if ((h >> 24) % 3 == 0) { b.Chest(new Vec3d(cols[c] + 4.5, y2, z0 + 1.5), -Math.PI / 2); chests++; }
                    }
                }
            }
            // The gantry crane over the road at the cross aisle: four legs and a beam 15 m up.
            double cz = 1076 + 8 * 9 + 0.5, beam = y + 15;
            foreach (double lx in new[] { 788.0, 799.5 })
                foreach (double lz in new[] { cz, cz + 2.7 })
                    b.Strip(new Vec3d(lx, y, lz), 1.5, true, 10);
            NorthKit.Deck(b, 788.0, cz, 801.0, cz + 3, beam + Col);
            b.Open(new Rect2(NorthSegment.TermX0, NorthSegment.TermZ0, NorthSegment.TermX1, NorthSegment.TermZ1));
            b.Poi("Container Terminal", new Vec3d(794, y, 1146));
        }

        // ---------------------------------------------------------------- villages and nature

        private static void BuildFill(BrBuild b)
        {
            // The coal yard on the station's east flank: walls three tiles tall (too tall to climb), one gate, a hut.
            double cy = b.Foot(700, 1030, 722.5, 1052.5, 2.5, Base);
            b.Style = BrStyle.Industrial; b.Variant = 4;
            BrKit.Compound(b, 700, cy, 1030);
            b.Poi("Coal Yard", new Vec3d(711, cy, 1041));

            // South of the embankment, the permanent way depot: sleeper stacks (one tile, crouch cover) and rail stacks
            // (two tiles, full cover you can climb), and three sealed tool huts, so crossing to the bank isn't a run in the open.
            (double x, double z, double len, bool alongX, int rows)[] stacks =
            {
                (488, 896, 6, true, 2), (520, 902, 4.5, false, 1), (548, 894, 7.5, true, 1), (585, 900, 6, true, 2),
                (612, 893, 4.5, false, 2), (676, 898, 6, true, 1), (700, 893, 4.5, false, 2), (728, 900, 7.5, true, 2),
                (770, 895, 6, true, 1), (800, 901, 4.5, false, 2),
            };
            b.Style = BrStyle.Timber; b.Variant = 2;
            foreach (var st in stacks) b.LowWall(st.x, st.z, st.len, st.alongX, st.rows);
            foreach (double hx in new[] { 560.0, 745.0, 818.0 })
            {
                double hy = b.Foot(hx, 891, hx + M + Col, 891 + M + Col, 2.5);
                BrKit.Shed(b, hx, hy, 891);
            }

            b.Style = BrStyle.Plaster; b.Variant = 0;
            // The railway village south of the embankment and round the edges.
            BrKit.Villages(b, new Rect2(b.Area.X0, b.Area.Z0, b.Area.X1, 916), 1, stopAtChests: BrLayout.MaxChests - 6);
            BrKit.Villages(b, new Rect2(b.Area.X0, 1030, 610, b.Area.Z1), 2, stopAtChests: BrLayout.MaxChests - 6);
            b.Nature.Groves = 3; b.Nature.LoneTrees = 10; b.Nature.RockGroups = 8; b.Nature.Bushes = 24;
            b.Open(new Rect2(b.Anchor.X - 40, b.Anchor.Z - 40, b.Anchor.X + 40, b.Anchor.Z + 40));
        }

    }
}
