using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The plan of the South segment's market town (<see cref="SouthSegment"/>): its ground and every building, district
    /// by district. Map coordinates (x east, z north); the anchor is (652.8, 280.8), the spawn region stands south of it.
    /// </summary>
    internal static partial class SouthPlan
    {
        private const double M = SouthKit.M, Col = SouthKit.Col, SH = SouthKit.SH;
        private static double W(int n) => SouthKit.W(n);

        /// <summary>Level of the town's core (the square and the streets round it).</summary>
        public const double Core = BrGround.Base;

        // ---- Hills (centre x, z, radius, height).
        public static readonly (double X, double Z, double R, double H)[] Hills =
        {
            (503, 340, 80, 7.5),   // the Upper Town hill: the church on top
            (796, 156, 66, 11),    // Mill Hill: the windmill
            (515, 148, 56, 9),     // Gallows Knoll: the allotments' hill
            (792, 362, 48, 5),     // Orchard Rise behind the brewery
        };

        public static double Ground(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            foreach (var k in Hills) h += GroundShapes.Hill(x, z, k.X, k.Z, k.R, k.H);
            return h;
        }

        public static void Build(BrBuild b)
        {
            var k = new SouthKit(b);
            SouthKit.Last = k;
            Square(k);
            SquareBlocks(k);
            // The open ground round the town (the hills, the outskirts): more groves, trees, rocks and bushes than the
            // default, so crossing between districts is never a run over bare grass.
            b.Nature.Groves = 14; b.Nature.LoneTrees = 60; b.Nature.RockGroups = 40; b.Nature.Bushes = 110;
            Districts(k);
            if (k.HomeChests != BrLayout.HomeChests)
                throw new InvalidOperationException($"South: {k.HomeChests} home chests, want {BrLayout.HomeChests}");
            b.Style = BrStyle.Brick;
            b.Variant = 0;
        }

        // ================================================================ the Market Square (home POI)

        /// <summary>The square's open ground: between the shops on the west, the town hall on the east, the corner blocks
        /// on the south and the market hall on the north.</summary>
        public static readonly Rect2 SquareRect = new Rect2(619.6, 248, 695.0, 309);

        // Buildings round the square (south-west corners).
        public const double HallX = 652.8 - 6 * M - Col / 2, HallZ = 309.0;          // 12 x 3 modules, two storeys
        public const double TownHallX = 695.0, TownHallZ = 264.0;                    // 3 x 3, two storeys
        public const double TowerX = 695.0, TowerZ = TownHallZ + 3 * M + Col;        // 3 x 3, ten storeys
        public const int TowerStoreys = 10;
        public const double ShopsX = 600.4, ShopsZ = 264.0;                          // 3 deep x 6 houses, three storeys
        public const double CrossX = 652.8 - M - Col / 2, CrossZ = 280.8 - M - Col / 2; // the butter cross, 2 x 2

        private static void Square(SouthKit k)
        {
            BrBuild b = k.B;
            Vec3d a = b.Anchor;
            b.Poi("Market Square", a);
            b.Open(SquareRect);

            // ---- The butter cross: an open arcade round two chests, in the middle of the square.
            var cross = new GreyboxBlock.Building(CrossX, CrossZ, 2, 2, 1) { Y0 = Core };
            for (int i = 0; i < 2; i++) { cross.AlongX[0, 0, i] = WallKind.WideDoor; cross.AlongX[0, 2, i] = WallKind.WideDoor; }
            for (int j = 0; j < 2; j++) { cross.AlongZ[0, 0, j] = WallKind.WideDoor; cross.AlongZ[0, 2, j] = WallKind.WideDoor; }
            k.Level(SouthKit.RectOf(CrossX, CrossZ, 2, 2), 1, Core, "butter cross");
            k.Raise(cross, "Butter cross", BrStyle.Stone, 1);
            k.Chest(new Vec3d(a.X - 2.2, Core, a.Z), -Math.PI / 2, "butter cross west", home: true);
            k.Chest(new Vec3d(a.X + 2.2, Core, a.Z), Math.PI / 2, "butter cross east", home: true);

            // ---- Stalls: low walls (chest high) in L shapes round the square, cover across the open ground.
            void Stall(double x, double z, bool openEast, bool openNorth)
            {
                // An L: a 4.5 m run along x and a 3 m run along z, at the square's level (it is level out to 35 m).
                double zx = openNorth ? z : z + 3.0 - 0.3;
                k.Wall(x, zx, Core, 4.5, true, 1);
                double xz = openEast ? x : x + 4.5 - 0.3;
                k.Wall(xz, openNorth ? z + 0.3 : z, Core, 2.7, false, 1);
            }
            Stall(a.X - 22, a.Z + 9, true, true);    // north-west
            Stall(a.X + 17.5, a.Z + 9, false, true); // north-east
            Stall(a.X - 22, a.Z - 13, true, false);  // south-west
            Stall(a.X + 17.5, a.Z - 13, false, false); // south-east
            Stall(a.X - 6, a.Z + 17, true, true);
            Stall(a.X + 1.5, a.Z - 22, false, false);
            k.Chest(new Vec3d(a.X - 20, Core, a.Z + 11), 0, "stall north-west", home: true);
            k.Chest(new Vec3d(a.X + 20, Core, a.Z + 11), 0, "stall north-east", home: true);
            k.Chest(new Vec3d(a.X - 20, Core, a.Z - 11), Math.PI, "stall south-west", home: true);
            k.Chest(new Vec3d(a.X + 20, Core, a.Z - 11), Math.PI, "stall south-east", home: true);

            // ---- The market hall: a long covered hall on the square's north side, two storeys tall inside (the nave
            // has no floor between), galleries along both sides, wide doors at the ends only: from the square you breach
            // through its side. Two strongrooms in its corners with no way in.
            var hall = new GreyboxBlock.Building(HallX, HallZ, 12, 3, 2) { Y0 = Core };
            k.Level(SouthKit.RectOf(HallX, HallZ, 12, 3), 2, Core, "market hall");
            hall.AlongZ[0, 0, 1] = WallKind.WideDoor;
            hall.AlongZ[0, 12, 1] = WallKind.WideDoor;
            hall.AlongX[0, 1, 0] = WallKind.Wall;   // the south-west strongroom
            hall.AlongZ[0, 1, 0] = WallKind.Wall;
            hall.AlongX[0, 2, 11] = WallKind.Wall;  // the north-east strongroom
            hall.AlongZ[0, 11, 2] = WallKind.Wall;
            for (int i = 2; i <= 9; i++) hall.Hole[0, i, 1] = true; // the nave: open to the roof
            k.Stairs(hall, 2, false, +1, 1);  // north aisle, west end, up to the gallery and the roof
            k.Stairs(hall, 0, false, -1, 8);  // south aisle, east end
            for (int i = 0; i < 12; i++)
            {
                if (i % 2 == 1) hall.AlongX[1, 0, i] = WallKind.Window; // over the square
                if (i % 3 == 0) hall.AlongX[1, 3, i] = WallKind.Window;
            }
            for (int j = 0; j < 3; j++) { hall.AlongZ[1, 0, j] = WallKind.Window; hall.AlongZ[1, 12, j] = WallKind.Window; }
            k.Raise(hall, "Market hall", BrStyle.Timber, 1);
            BrKit.Parapet(b, hall);
            // Balustrades along the galleries' edges over the nave.
            for (int i = 2; i <= 9; i++)
            {
                k.Wall(hall.Cx(i) + Col, hall.Cz(1), Core + SH, M - Col, true, 1);
                k.Wall(hall.Cx(i) + Col, hall.Cz(2), Core + SH, M - Col, true, 1);
            }
            // Stalls down the nave.
            for (int i = 3; i <= 8; i += 2)
            {
                k.Wall(hall.Cx(i) + 1.2, hall.Cz(1) + 1.5, Core, 3.0, true, 1);
                k.Wall(hall.Cx(i) + 1.2, hall.Cz(2) - 1.8, Core, 3.0, true, 1);
            }
            k.Chest(hall, 4, 1, 0, 0, "hall nave west", home: true);
            k.Chest(hall, 7, 1, 0, Math.PI, "hall nave east", home: true);
            k.Chest(hall, 6, 0, 1, 0, "hall south gallery", home: true);
            k.Chest(hall, 0, 0, 0, Math.PI / 2, "hall strongroom south-west");
            k.Chest(hall, 11, 2, 0, -Math.PI / 2, "hall strongroom north-east");

            // ---- The town hall and its clock tower on the square's east side.
            var th = new GreyboxBlock.Building(TownHallX, TownHallZ, 3, 3, 2) { Y0 = Core };
            var tw = new GreyboxBlock.Building(TowerX, TowerZ, 3, 3, TowerStoreys) { Y0 = Core };
            k.Level(new Rect2(TownHallX, TownHallZ, TownHallX + W(3), TowerZ + W(3)), 2, Core, "town hall");
            th.AlongZ[0, 0, 1] = WallKind.WideDoor;   // onto the square
            th.AlongX[0, 0, 1] = WallKind.ShutDoor;   // onto East Street
            k.Stairs(th, 2, true, +1);
            for (int s = 0; s < 2; s++)
            {
                th.AlongZ[s, 2, 0] = WallKind.Door; th.AlongZ[s, 2, 1] = WallKind.Wall; th.AlongZ[s, 2, 2] = WallKind.Door;
            }
            th.AlongZ[1, 0, 0] = WallKind.Window; th.AlongZ[1, 0, 1] = WallKind.Window; th.AlongZ[1, 0, 2] = WallKind.Window; // the council chamber over the square
            th.AlongX[1, 0, 0] = WallKind.Window;
            th.AlongZ[0, 3, 1] = WallKind.Window;
            k.Raise(th, "Town hall", BrStyle.Stone, 0);
            BrKit.Parapet(b, th);
            k.Chest(th, 0, 1, 0, Math.PI / 2, "town hall");
            k.Chest(th, 1, 0, 1, Math.PI, "town hall council chamber");

            // The tower: a switchback stair up its east lane, two vault floors sealed off from the stair, a clock room
            // with windows all round, an open belfry, a roof with a parapet: the town's landmark and its best perch.
            k.Stairs(tw, 2, true, +1);
            for (int s = 0; s < TowerStoreys; s++)
            {
                // The stair lane's side wall: open where the flights arrive and start; on the vault floors a wall
                // down the middle leaves a corridor round the stairwell and a sealed room on the square side.
                tw.AlongZ[s, 2, 0] = WallKind.Door;
                tw.AlongZ[s, 2, 1] = WallKind.Wall;
                tw.AlongZ[s, 2, 2] = WallKind.Door;
                if (s == 4 || s == 7) for (int q = 0; q < 3; q++) tw.AlongZ[s, 1, q] = WallKind.Wall;
                if (s == 0) continue;
                bool clock = s == TowerStoreys - 2, belfry = s == TowerStoreys - 1;
                for (int q = 0; q < 3; q++)
                {
                    WallKind open = belfry ? WallKind.WideDoor : WallKind.Window;
                    if (belfry || clock || (q + s) % 2 == 0) { tw.AlongZ[s, 0, q] = open; tw.AlongX[s, 3, q] = open; }
                    if (s >= 2 && (belfry || clock || (q + s) % 2 == 1)) tw.AlongX[s, 0, q] = open;
                    if (belfry || clock) tw.AlongZ[s, 3, q] = WallKind.Window;
                }
            }
            tw.AlongZ[0, 0, 1] = WallKind.WideDoor;  // onto the square
            tw.AlongX[0, 3, 0] = WallKind.ShutDoor;  // onto Bell Street
            k.Raise(tw, "Clock tower", BrStyle.Stone, 2);
            BrKit.Parapet(b, tw);
            k.Chest(tw, 1, 2, 2, Math.PI, "clock tower floor 3");
            k.Chest(tw, 0, 0, 4, Math.PI, "clock tower vault");
            k.Chest(tw, 0, 2, 7, Math.PI, "clock tower clockwork vault");
            k.Chest(tw, 1, 1, TowerStoreys, Math.PI, "clock tower roof");
            b.Poi("Clock Tower", new Vec3d(TowerX + W(3) / 2, Core, TowerZ + W(3) / 2), tw.Bounds);

            // ---- The shops on the square's west side: three storeys, fronts on the square.
            k.Level(SouthKit.RectOf(ShopsX, ShopsZ, 3, 6), 2, Core, "square shops");
            var shops = k.Row(ShopsX, ShopsZ, Core, 3, SouthKit.Face.East, "P* S2 H* P X*", 11, name: "Square shops", style: BrStyle.Painted, variant: 3);
            k.Chest(shops, 2, 3, 0, -Math.PI / 2, "square shops front", home: true);
            b.Poi("Market Hall", new Vec3d(652.8, Core, HallZ + W(3) / 2), hall.Bounds);
        }
    }
}
