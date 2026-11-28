using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The South West segment of the battle royale map (id 0): the Factory. The spawn region stands west of the home
    /// anchor (north-east corner of the square); everything else is one old works and the town that grew round it.
    ///
    /// - Works Gate (home POI, round the anchor): a six-storey Company House across the end of the approach (its strongroom
    ///   has no door), the gatehouse, a sealed wages office, the weighbridge hut, barriers on the plaza.
    /// - The Works (one level platform in the middle): the rolling mill (three storeys tall, a west mezzanine, a north
    ///   gallery at the first floor and a south gallery at the second, railed; a sealed tool crib), the foundry (two
    ///   storeys tall round a double-height casting floor, a railed mezzanine ring, a sealed pattern store) joined to the
    ///   mill's south gallery by a bridge from its roof, the machine shop, a four-storey drawing office, the boiler house
    ///   and its twelve-storey stack (a door from the boiler roof at the second floor), the water tower (a tank room 35 m
    ///   up, its floor overhanging the core), the stockyard behind a 4.5 m wall with one gate (container stacks, a stair
    ///   onto them, sealed stores), the bonded store (a long blank south side, sealed bays) and two warehouses.
    /// - Mill Row: four terraces of six, back-yard walls, the pub and the institute (sealed vestry).
    /// - South of Works Road: the five-storey research block, Pond Row terraces, the co-op store, the dry settling pond
    ///   with its pump house (sealed valve room), the coke ovens.
    /// - The railhead along the south: goods shed, signal box, loco shed, the explosives magazine (a walled compound),
    ///   a siding of open wagons.
    /// - Slag tips: the Big Tip (south-west), the West Bank ridge and the North Tip, chests on their tops; tip huts,
    ///   the engine shed, allotments and pigeon lofts behind the spawn region.
    /// </summary>
    public sealed class SouthWestSegment : BrSegment
    {
        public override int Id => 0;

        public override string HomePoiName => "Works Gate";

        private const double M = FactoryKit.M, C = FactoryKit.C, SH = FactoryKit.SH;
        private static double W(int n) => FactoryKit.W(n);

        // ---- Ground features (map coordinates).
        public const double BigTipX = 118, BigTipZ = 120, BigTipR = 40, BigTipH = 14;
        public const double NorthTipX = 118, NorthTipZ = 385, NorthTipR = 32, NorthTipH = 10;
        public static readonly Rect2 Pond = new Rect2(352, 105, 378, 131);
        public const double PondDepth = 4.2, PondBank = 18;

        /// <summary>The works platform: one level footing every works building stands on.</summary>
        public static readonly Rect2 Works = new Rect2(140, 150, 335, 335);

        public override double Ground(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            h += GroundShapes.Hill(x, z, BigTipX, BigTipZ, BigTipR, BigTipH);
            h += GroundShapes.Ridge(x, z, 100, 175, 102, 285, 20, 7);
            h += GroundShapes.Hill(x, z, NorthTipX, NorthTipZ, NorthTipR, NorthTipH);
            h -= GroundShapes.Mesa(x, z, Pond.X0, Pond.Z0, Pond.X1, Pond.Z1, PondDepth, PondBank);
            return h;
        }

        /// <summary>What a build records for the tests (per build: two copies of the map are built at once on different threads).</summary>
        public sealed class Notes
        {
            /// <summary>Chests behind walls with no way in.</summary>
            public readonly System.Collections.Generic.List<Vec3d> SealedChests = new System.Collections.Generic.List<Vec3d>();
            /// <summary>High spots that must be reachable on foot from the plaza (roofs, galleries, the tank, the containers).</summary>
            public readonly System.Collections.Generic.List<(string Name, Vec3d At)> HighSpots = new System.Collections.Generic.List<(string, Vec3d)>();
        }

        /// <summary>The template build's records (every build is the same).</summary>
        public static System.Collections.Generic.List<Vec3d> SealedChests => BrMap.Template.Segments[0].Notes<Notes>().SealedChests;
        public static System.Collections.Generic.List<(string Name, Vec3d At)> HighSpots => BrMap.Template.Segments[0].Notes<Notes>().HighSpots;

        private static Vec3d Cell(GreyboxBlock.Building t, int i, int j, int storey) =>
            new Vec3d(t.Cx(i) + C + 3, t.Y0 + storey * SH, t.Cz(j) + C + 3);

        public override void Build(BrBuild b)
        {
            b.Nature.Groves = 3;
            b.Nature.LoneTrees = 12;
            b.Nature.RockGroups = 14;
            b.Nature.Bushes = 24;
            WorksGate(b);
            TheWorks(b);
            MillRow(b);
            SouthOfTheRoad(b);
            Railhead(b);
            Tips(b);
            Approach(b);
            b.Style = BrStyle.Brick;
            b.Variant = 0;
        }

        // ================================================================ Works Gate (home POI)

        private static void WorksGate(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = BrGround.Base;

            // The Company House: six storeys across the end of the approach. Strongroom on the ground floor (no door).
            b.Foot(397, 366, 397 + W(3), 366 + W(4), 2.2, y);
            var office = BrKit.TowerPlan(b, 397, y, 366, 3, 4, 6);
            office.AlongX[0, 0, 2] = WallKind.Wall;     // the strongroom's outer wall
            office.AlongX[0, 0, 1] = WallKind.WideDoor; // the south entrance
            office.AlongZ[0, 0, 3] = WallKind.WideDoor; // the plaza entrance
            office.AlongX[0, 1, 2] = WallKind.Wall;     // strongroom
            office.AlongZ[0, 2, 0] = WallKind.Wall;
            for (int s = 1; s < 6; s++) office.AlongZ[s, 0, 3] = WallKind.Window; // looking over the plaza
            b.Style = BrStyle.Stone; b.Variant = 0;
            b.Raise(office, "Company House");
            BrKit.Parapet(b, office);
            b.Chest(office, 2, 0, 0, 0);
            b.Notes<Notes>().SealedChests.Add(Cell(office, 2, 0, 0));
            b.Notes<Notes>().HighSpots.Add(("Company House roof", Cell(office, 1, 2, 6)));
            b.Chest(office, 2, 3, 3, Math.PI);
            b.Chest(office, 2, 2, 6, Math.PI);

            // The gatehouse (two storeys) on the north side of the plaza.
            b.Foot(360, 400.8, 360 + W(2), 400.8 + W(3), 2.2, y);
            b.Style = BrStyle.Brick; b.Variant = 1;
            FactoryKit.House(b, 360, y, 400.8, "Gatehouse");

            // The wages office: sealed, two chests. Break in.
            b.Foot(375, 350, 375 + W(2), 350 + W(1), 2.2, y);
            var wages = new GreyboxBlock.Building(375, 350, 2, 1, 1) { Y0 = y };
            b.Style = BrStyle.Brick; b.Variant = 2;
            b.Raise(wages, "Wages office");
            b.Chest(wages, 0, 0, 0, 0);
            b.Chest(wages, 1, 0, 0, 0);
            b.Notes<Notes>().SealedChests.Add(Cell(wages, 0, 0, 0));

            // The weighbridge hut and the timekeeper's hut.
            b.Foot(362, 366, 362 + W(1), 366 + W(1), 2.2, y);
            var weigh = new GreyboxBlock.Building(362, 366, 1, 1, 1) { Y0 = y };
            weigh.AlongX[0, 0, 0] = WallKind.ShutDoor;
            b.Style = BrStyle.Painted; b.Variant = 0;
            b.Raise(weigh, "Weighbridge hut");
            b.Chest(weigh, 0, 0, 0, 0);
            b.Foot(402, 396.5, 402 + W(1), 396.5 + W(1), 2.2, y);
            var keeper = new GreyboxBlock.Building(402, 396.5, 1, 1, 1) { Y0 = y };
            keeper.AlongZ[0, 0, 0] = WallKind.Door;
            b.Raise(keeper, "Timekeeper's hut");

            // The plaza: barriers and two chests in the open.
            b.Strip(new Vec3d(374, y, 386), 6, true, 2);
            b.Strip(new Vec3d(393, y, 376), 4.5, false, 2);
            b.Strip(new Vec3d(380, y, 401), 6, true, 1);
            b.Chest(new Vec3d(384, y, 397), Math.PI);
            b.Chest(new Vec3d(378, y, 382), 0);
            b.Open(new Rect2(a.X - 30, a.Z - 30, a.X + 30, a.Z + 30));
            b.Poi("Works Gate", a);
        }

        // ================================================================ The Works

        private static void TheWorks(BrBuild b)
        {
            Rect2 w = Works;
            double y = b.Foot(w.X0, w.Z0, w.X1, w.Z1, 3, BrGround.Base);
            var halls = new System.Collections.Generic.List<Aabb>();

            // ---- The rolling mill: 8 x 4 modules, three storeys tall.
            var mill = new GreyboxBlock.Building(148, 302, 8, 4, 3) { Y0 = y };
            FactoryKit.HoleAll(mill, 0);
            FactoryKit.HoleAll(mill, 1);
            FactoryKit.Floor(mill, 0, 0, 1, 0, 2); // the west mezzanine (round the stairs)
            FactoryKit.Floor(mill, 0, 0, 7, 3, 3); // the north gallery, first floor
            FactoryKit.Floor(mill, 1, 0, 1, 0, 2);
            FactoryKit.Floor(mill, 1, 0, 7, 0, 0); // the south gallery, second floor
            b.Stairwell(mill, 0, true, +1);
            for (int j = 0; j < 3; j++) { mill.AlongZ[0, 1, j] = WallKind.Wall; mill.AlongZ[0, 2, j] = WallKind.Wall; } // the tool crib
            mill.AlongX[0, 3, 1] = WallKind.Wall;
            mill.AlongZ[0, 8, 1] = WallKind.WideDoor;
            mill.AlongZ[0, 8, 2] = WallKind.WideDoor;
            mill.AlongX[0, 0, 5] = WallKind.WideDoor;
            mill.AlongX[0, 0, 3] = WallKind.Door;
            mill.AlongX[0, 4, 4] = WallKind.ShutDoor;
            for (int i = 0; i < 8; i++)
            {
                if (i % 2 == 1) mill.AlongX[1, 4, i] = WallKind.Window;
                if (i % 2 == 1) mill.AlongX[2, 0, i] = WallKind.Window;
                if (i % 2 == 0 && i > 1) mill.AlongX[2, 4, i] = WallKind.Window;
            }
            mill.AlongX[2, 0, 2] = WallKind.Door; // onto the bridge from the foundry roof
            mill.AlongZ[1, 0, 3] = WallKind.Window;
            mill.AlongZ[2, 8, 1] = WallKind.Window;
            mill.AlongZ[2, 8, 2] = WallKind.Window;
            b.Style = BrStyle.Industrial; b.Variant = 0;
            b.Raise(mill, "Rolling mill");
            FactoryKit.Railings(b, mill, 0, 0);
            FactoryKit.Railings(b, mill, 1, 0);
            FactoryKit.Parapet(b, mill);
            b.Chest(mill, 1, 0, 0, Math.PI / 2);  // tool crib
            b.Chest(mill, 1, 2, 0, Math.PI / 2);
            b.Chest(mill, 6, 2, 0, 0);            // the mill floor
            b.Chest(mill, 3, 1, 0, Math.PI / 2);
            b.Chest(mill, 5, 3, 1, Math.PI);      // north gallery
            b.Chest(mill, 1, 1, 1, Math.PI / 2);  // west mezzanine
            b.Chest(mill, 4, 0, 2, 0);            // south gallery
            b.Chest(mill, 1, 0, 2, Math.PI / 2);
            b.Chest(mill, 6, 2, 3, Math.PI);      // roof
            b.Notes<Notes>().SealedChests.Add(Cell(mill, 1, 1, 0));
            b.Notes<Notes>().HighSpots.Add(("mill north gallery", Cell(mill, 6, 3, 1)));
            b.Notes<Notes>().HighSpots.Add(("mill south gallery", Cell(mill, 6, 0, 2)));
            b.Notes<Notes>().HighSpots.Add(("mill roof", Cell(mill, 4, 2, 3)));
            halls.Add(mill.Bounds);

            // ---- The foundry: 6 x 5 modules, two storeys tall round a double-height casting floor.
            var fd = new GreyboxBlock.Building(148, 255, 6, 5, 2) { Y0 = y };
            for (int i = 2; i <= 4; i++) for (int j = 1; j <= 3; j++) fd.Hole[0, i, j] = true;
            b.Stairwell(fd, 0, true, +1);
            for (int j = 1; j <= 3; j++) fd.AlongZ[0, 5, j] = WallKind.Wall; // the pattern store, under the east mezzanine
            fd.AlongX[0, 1, 5] = WallKind.Wall;
            fd.AlongX[0, 4, 5] = WallKind.Wall;
            fd.AlongZ[0, 0, 4] = WallKind.WideDoor;
            fd.AlongX[0, 0, 2] = WallKind.WideDoor;
            fd.AlongX[0, 0, 4] = WallKind.Door;
            fd.AlongX[0, 5, 3] = WallKind.Door;
            fd.AlongZ[0, 6, 0] = WallKind.Door;
            for (int i = 0; i < 6; i++)
            {
                if (i % 2 == 1) fd.AlongX[1, 0, i] = WallKind.Window;
                if (i % 2 == 0) fd.AlongX[1, 5, i] = WallKind.Window;
            }
            for (int j = 0; j < 5; j += 2) fd.AlongZ[1, 6, j] = WallKind.Window;
            fd.AlongZ[1, 0, 3] = WallKind.Window;
            b.Style = BrStyle.Industrial; b.Variant = 1;
            b.Raise(fd, "Foundry");
            FactoryKit.Railings(b, fd, 0, 0);
            // The bridge from the foundry roof to the mill's south gallery (its door is segment 2 of the mill's south wall).
            double bx0 = mill.Cx(2) + C + 1.5, bx1 = bx0 + 3.0, fz1 = fd.Cz(fd.Mz) + C;
            FactoryKit.Parapet(b, fd, new Rect2(bx0, fz1 - 1, bx1, fz1 + 1));
            FactoryKit.Deck(b, bx0, fz1, bx1, mill.Z0, fd.Top, rails: true);
            b.Chest(fd, 3, 2, 0, 0);             // casting floor
            b.Chest(fd, 1, 3, 0, Math.PI / 2);
            b.Chest(fd, 5, 1, 0, Math.PI);       // pattern store
            b.Chest(fd, 5, 3, 0, Math.PI);
            b.Chest(fd, 3, 0, 1, 0);             // mezzanine
            b.Chest(fd, 3, 4, 1, Math.PI);
            b.Chest(fd, 5, 4, 1, Math.PI);
            b.Chest(fd, 2, 2, 2, 0);             // roof
            b.Notes<Notes>().SealedChests.Add(Cell(fd, 5, 2, 0));
            b.Notes<Notes>().HighSpots.Add(("foundry mezzanine", Cell(fd, 2, 4, 1)));
            b.Notes<Notes>().HighSpots.Add(("foundry roof", Cell(fd, 3, 3, 2)));
            halls.Add(fd.Bounds);

            // ---- The machine shop: 6 x 3, two storeys tall, a north mezzanine and stairs in its east lane.
            var ms = new GreyboxBlock.Building(212, 306, 6, 3, 2) { Y0 = y };
            FactoryKit.HoleAll(ms, 0);
            FactoryKit.Floor(ms, 0, 4, 5, 0, 2);
            FactoryKit.Floor(ms, 0, 0, 5, 2, 2);
            FactoryKit.Floor(ms, 0, 0, 1, 0, 0); // the store's ceiling
            b.Stairwell(ms, 5, true, -1);
            ms.AlongZ[0, 2, 0] = WallKind.Wall; // the sealed store
            ms.AlongX[0, 1, 0] = WallKind.Wall;
            ms.AlongX[0, 1, 1] = WallKind.Wall;
            ms.AlongZ[0, 0, 1] = WallKind.WideDoor;
            ms.AlongX[0, 0, 3] = WallKind.Door;
            ms.AlongX[0, 0, 2] = WallKind.WideDoor;
            ms.AlongX[0, 3, 1] = WallKind.ShutDoor;
            for (int i = 0; i < 4; i++) { if (i % 2 == 1) ms.AlongX[1, 0, i] = WallKind.Window; if (i % 2 == 0) ms.AlongX[1, 3, i] = WallKind.Window; }
            b.Style = BrStyle.Steel; b.Variant = 0;
            b.Raise(ms, "Machine shop");
            FactoryKit.Railings(b, ms, 0, 5);
            FactoryKit.Parapet(b, ms);
            b.Chest(ms, 0, 0, 0, 0);
            b.Chest(ms, 1, 0, 0, 0);
            b.Chest(ms, 2, 1, 0, 0);
            b.Chest(ms, 1, 2, 1, Math.PI);
            b.Chest(ms, 2, 1, 2, 0);
            b.Notes<Notes>().SealedChests.Add(Cell(ms, 0, 0, 0));
            b.Notes<Notes>().HighSpots.Add(("machine shop mezzanine", Cell(ms, 2, 2, 1)));
            halls.Add(ms.Bounds);

            // ---- The drawing office: four storeys.
            var dw = BrKit.TowerPlan(b, 268, y, 304, 3, 3, 4);
            b.Style = BrStyle.Brick; b.Variant = 3;
            b.Raise(dw, "Drawing office");
            BrKit.Parapet(b, dw);
            b.Chest(dw, 2, 0, 0, Math.PI);
            b.Chest(dw, 2, 2, 1, Math.PI);
            b.Chest(dw, 2, 0, 3, Math.PI);
            b.Chest(dw, 2, 2, 4, Math.PI);
            b.Notes<Notes>().HighSpots.Add(("drawing office roof", Cell(dw, 1, 1, 4)));

            // ---- The boiler house (double height) and its stack, twelve storeys, a door off the boiler roof.
            var bh = new GreyboxBlock.Building(197, 258, 3, 3, 2) { Y0 = y };
            FactoryKit.HoleAll(bh, 0);
            bh.AlongZ[0, 0, 1] = WallKind.Door;
            bh.AlongX[0, 0, 1] = WallKind.ShutDoor;
            bh.AlongX[0, 3, 1] = WallKind.Door;
            bh.AlongX[1, 0, 0] = WallKind.Window;
            bh.AlongX[1, 0, 2] = WallKind.Window;
            bh.AlongX[1, 3, 1] = WallKind.Window;
            b.Style = BrStyle.Brick; b.Variant = 4;
            b.Raise(bh, "Boiler house");
            b.Chest(bh, 1, 1, 0, 0);
            b.Chest(bh, 0, 2, 0, Math.PI / 2);
            double stackX = bh.Cx(3) + C + 1.5;
            // Ten storeys: only the Old City's spire has twelve (Ben: the landmark is the tallest).
            var st = BrKit.TowerPlan(b, stackX, y, 258, 2, 3, 10);
            for (int s = 1; s < 10; s++)
            {
                st.AlongX[s, 0, 1] = s % 3 == 0 ? WallKind.Window : WallKind.Wall;
                for (int j = 0; j < 3; j++) st.AlongZ[s, 2, j] = s % 4 == 2 && j == 1 ? WallKind.Window : WallKind.Wall;
            }
            st.AlongZ[2, 0, 2] = WallKind.Door; // from the boiler roof
            b.Style = BrStyle.Brick; b.Variant = 5;
            b.Raise(st, "Boiler stack");
            BrKit.Parapet(b, st);
            double dz0 = st.Cz(2) + C + 1.5, dz1 = dz0 + 3.0;
            // A fire escape up the boiler house's north side: a second way onto its roof (and to the stack's door).
            double fe0 = bh.X0 + 3.5, fe1 = fe0 + 3.0, nz = bh.Cz(3) + C;
            b.Climb(new Aabb(new Vec3d(fe0, y, nz), new Vec3d(fe1, bh.Top, nz + 2 * GreyboxBlock.StairRun)), Dir.South);
            FactoryKit.Parapet(b, bh, new Rect2(bh.Cx(3) - 1, dz0, bh.Cx(3) + 1, dz1), new Rect2(fe0, nz - 1, fe1, nz + 1));
            FactoryKit.Deck(b, bh.Cx(3) + C, dz0, stackX, dz1, bh.Top);
            b.Chest(st, 1, 2, 5, Math.PI);
            b.Chest(st, 1, 0, 7, 0);
            b.Chest(st, 1, 0, 9, 0); // the top floor
            b.Chest(st, 1, 2, st.Storeys, Math.PI); // on the roof
            b.Notes<Notes>().HighSpots.Add(("boiler stack roof", Cell(st, 1, 1, st.Storeys)));
            b.Notes<Notes>().HighSpots.Add(("boiler house roof", Cell(bh, 1, 1, 2)));

            // ---- The water tower in the works yard: the tank room 35 m up.
            var tank = FactoryKit.WaterTower(b, 285, y, 235, 7, out GreyboxBlock.Building core);
            b.Chest(core, 1, 2, 2, Math.PI);
            b.Chest(core, 1, 0, 4, 0);
            b.Chest(new Vec3d(tank.X0 + 3.2, tank.Y0, tank.Z0 + 16), Math.PI / 2);
            b.Chest(new Vec3d(tank.Cx(4) - 2.9, tank.Y0, tank.Z0 + 16), -Math.PI / 2);
            b.Chest(new Vec3d(tank.X0 + 16, tank.Y0, tank.Cz(5) - 2.9), Math.PI);
            b.Notes<Notes>().HighSpots.Add(("water tank (overhang)", new Vec3d(tank.X0 + 3.2, tank.Y0, tank.Z0 + 10)));

            // ---- The stockyard: 4.5 m walls (too tall to climb), one gate on the north side.
            Stockyard(b, y);

            // ---- The bonded store: 8 x 3, double height, an office mezzanine at the east end; three sealed bays along
            // its south side, which has no door or window at all.
            var bs = new GreyboxBlock.Building(232, 190, 8, 3, 2) { Y0 = y };
            FactoryKit.HoleAll(bs, 0);
            FactoryKit.Floor(bs, 0, 6, 7, 0, 2);
            FactoryKit.Floor(bs, 0, 0, 5, 0, 0); // a gallery over the bays (their ceiling)
            b.Stairwell(bs, 7, true, +1);
            for (int i = 0; i < 6; i++) bs.AlongX[0, 1, i] = WallKind.Wall;
            bs.AlongZ[0, 2, 0] = WallKind.Wall;
            bs.AlongZ[0, 4, 0] = WallKind.Wall;
            bs.AlongZ[0, 6, 0] = WallKind.Wall;
            bs.AlongZ[0, 0, 1] = WallKind.WideDoor;
            bs.AlongX[0, 3, 1] = WallKind.WideDoor;
            bs.AlongX[0, 3, 4] = WallKind.WideDoor;
            for (int i = 0; i < 8; i += 2) bs.AlongX[1, 3, i] = WallKind.Window;
            b.Style = BrStyle.Brick; b.Variant = 6;
            b.Raise(bs, "Bonded store");
            FactoryKit.Railings(b, bs, 0, 7);
            FactoryKit.Parapet(b, bs);
            b.Chest(bs, 0, 0, 0, Math.PI / 2);
            b.Chest(bs, 2, 0, 0, Math.PI / 2);
            b.Chest(bs, 4, 0, 0, Math.PI / 2);
            b.Chest(bs, 3, 2, 0, Math.PI);
            b.Chest(bs, 5, 1, 0, Math.PI / 2);
            b.Chest(bs, 6, 1, 1, Math.PI / 2);
            b.Chest(bs, 1, 1, 2, 0);
            b.Notes<Notes>().SealedChests.Add(Cell(bs, 2, 0, 0));
            b.Notes<Notes>().HighSpots.Add(("bonded store gallery", Cell(bs, 3, 0, 1)));

            b.Style = BrStyle.Industrial; b.Variant = 2;
            BrKit.Warehouse(b, 292, y, 190);
            b.Variant = 3;
            BrKit.Warehouse(b, 292, y, 158);

            // ---- The works yard: stacked pallets, a coal bunker, coal and scrap heaps.
            b.Strip(new Vec3d(244, y, 281), 9, true, 2);
            b.Strip(new Vec3d(256, y, 263), 6, false, 1);
            b.Strip(new Vec3d(318, y, 286), 7.5, true, 2);
            b.Strip(new Vec3d(262, y, 236), 9, false, 2);
            b.Strip(new Vec3d(318, y, 222), 6, true, 1);
            b.Strip(new Vec3d(248, y, 226), 6, true, 2);
            b.Strip(new Vec3d(325, y, 300), 7.5, false, 2);
            b.Strip(new Vec3d(232, y, 290), 6, false, 1);
            b.Style = BrStyle.Concrete; b.Variant = 1;
            BrKit.Shed(b, 320, y, 310);
            b.Chest(new Vec3d(252, y, 284), Math.PI);
            b.Chest(new Vec3d(320, y, 290), Math.PI);
            b.Chest(new Vec3d(266, y, 240), Math.PI / 2);
            b.Chest(new Vec3d(328, y, 304), -Math.PI / 2);
            b.Chest(new Vec3d(252, y, 229), Math.PI);
            b.Rocks(272, 280, 11);
            b.Rocks(250, 248, 12);
            b.Rocks(325, 250, 13);
            b.Poi("The Works", new Vec3d(240, y, 250), halls.ToArray());
        }

        private static void Stockyard(BrBuild b, double y)
        {
            const double x0 = 148, z0 = 160, x1 = 218, z1 = 215, gate0 = 181, gate1 = 184;
            b.Strip(new Vec3d(x0, y, z0), x1 - x0, true, 3);
            b.Strip(new Vec3d(x0, y, z1 - C), gate0 - x0, true, 3);
            b.Strip(new Vec3d(gate1, y, z1 - C), x1 - gate1, true, 3);
            b.Strip(new Vec3d(x0, y, z0 + C), z1 - z0 - 2 * C, false, 3);
            b.Strip(new Vec3d(x1 - C, y, z0 + C), z1 - z0 - 2 * C, false, 3);
            // Container stacks against the south wall: two touching, one stacked on the first, a stair up onto them.
            b.Style = BrStyle.Painted;
            double cz = z0 + 3;
            b.Variant = 1; BrKit.Shed(b, 158, y, cz);
            b.Variant = 2; BrKit.Shed(b, 158 + W(1), y, cz);
            FactoryKit.Box(b, 158, cz, 158 + W(1), cz + W(1), y + SH, 3); // the stacked one: sealed
            b.Chest(new Vec3d(158 + W(1) / 2, y + SH, cz + W(1) / 2), 0);
            b.Notes<Notes>().SealedChests.Add(new Vec3d(158 + W(1) / 2, y + SH, cz + W(1) / 2 + 1.5));
            b.Notes<Notes>().HighSpots.Add(("container roof", new Vec3d(158 + W(1) * 1.5, y + SH, cz + W(1) / 2)));
            double sx = 158 + 2 * W(1);
            b.Climb(new Aabb(new Vec3d(sx, y, cz + 1.8), new Vec3d(sx + GreyboxBlock.StairRun, y + SH, cz + 4.8)), Dir.West);
            b.Variant = 4; BrKit.Shed(b, 190, y, cz);
            b.Variant = 5; BrKit.Shed(b, 190, y, cz + W(1));
            // The stores: a row of three, the middle one sealed.
            b.Style = BrStyle.Timber; b.Variant = 0;
            BrKit.Terrace(b, 186, y, 196, 3, true, 1);
            b.Strip(new Vec3d(160, y, 190), 9, true, 2);
            b.Strip(new Vec3d(172, y, 176), 6, false, 1);
            b.Chest(new Vec3d(178, y, 185), 0);
            b.Open(new Rect2(x0, z0, x1, z1));
            b.Poi("Stockyard", new Vec3d((x0 + x1) / 2, y, (z0 + z1) / 2));
        }

        // ================================================================ Mill Row

        private static void MillRow(BrBuild b)
        {
            double y = b.Foot(350, 276, 392, 331, 2.2);
            b.Style = BrStyle.Brick;
            double[] rows = { 278, 293, 308, 323 };
            for (int k = 0; k < rows.Length; k++)
            {
                b.Variant = 1 + k;
                var row = BrKit.Terrace(b, 352, y, rows[k], 6, true, (k * 2 + 1) % 6);
                int open = ((k * 2 + 1) % 6 + 3) % 6;
                b.Chest(new Vec3d(row.Cx(open) + C + 3, y, row.Cz(0) + C + 3), Math.PI);
                if (k < rows.Length - 1)
                {
                    // Back-yard walls between the rows, an alley gap in the middle.
                    b.Strip(new Vec3d(352, y, rows[k] + W(1) + 3.5), 16, true, 1);
                    b.Strip(new Vec3d(372, y, rows[k] + W(1) + 3.5), 18, true, 1);
                }
            }
            b.Open(new Rect2(350, 276, 392, 331));
            double py = b.Foot(398, 280, 398 + W(2), 280 + W(3), 2.2);
            b.Style = BrStyle.Plaster; b.Variant = 0;
            FactoryKit.House(b, 398, py, 280, "The Furnace Arms");

            double iy = b.Foot(398, 306, 398 + W(3), 306 + W(3), 2.2);
            var inst = new GreyboxBlock.Building(398, 306, 3, 3, 2) { Y0 = iy };
            b.Stairwell(inst, 2, true, +1);
            inst.AlongX[0, 2, 0] = WallKind.Wall; // the vestry, sealed
            inst.AlongZ[0, 1, 2] = WallKind.Wall;
            inst.AlongX[0, 0, 1] = WallKind.ShutDoor;
            inst.AlongZ[0, 0, 0] = WallKind.Door;
            inst.AlongZ[0, 2, 0] = WallKind.Wall;
            inst.AlongZ[0, 2, 2] = WallKind.Wall;
            inst.AlongX[1, 0, 0] = WallKind.Window;
            inst.AlongX[1, 0, 1] = WallKind.Window;
            inst.AlongZ[1, 0, 1] = WallKind.Window;
            inst.AlongX[1, 3, 0] = WallKind.Window;
            b.Style = BrStyle.Stone; b.Variant = 1;
            b.Raise(inst, "Workers' institute");
            b.Chest(inst, 0, 2, 0, 0);
            b.Chest(inst, 1, 1, 1, 0);
            b.Tree(395, 340, 21);
            b.Poi("Mill Row", new Vec3d(375, y, 305));
        }

        // ================================================================ South of Works Road

        private static void SouthOfTheRoad(BrBuild b)
        {
            // The research block: five storeys by the road.
            double ry = b.Foot(398, 205, 398 + W(3), 205 + W(3), 2.2);
            var rs = BrKit.TowerPlan(b, 398, ry, 205, 3, 3, 5);
            rs.AlongX[0, 0, 2] = WallKind.Wall;
            rs.AlongX[0, 0, 1] = WallKind.WideDoor;
            for (int s = 1; s < 5; s++) rs.AlongX[s, 3, 1] = WallKind.Window;
            b.Style = BrStyle.Glass; b.Variant = 0;
            b.Raise(rs, "Research block");
            BrKit.Parapet(b, rs);
            b.Chest(rs, 2, 0, 0, 0);
            b.Chest(rs, 2, 2, 1, Math.PI);
            b.Chest(rs, 2, 2, 2, Math.PI);
            b.Chest(rs, 2, 0, 4, 0);
            b.Chest(rs, 2, 2, 5, Math.PI);
            b.Notes<Notes>().HighSpots.Add(("research block roof", Cell(rs, 1, 1, 5)));

            // Pond Row: three terraces of five along z.
            double py = b.Foot(350, 196, 393, 232, 2.2);
            b.Style = BrStyle.Brick;
            double[] xs = { 352, 368, 384 };
            for (int k = 0; k < xs.Length; k++)
            {
                b.Variant = 5 + k;
                var row = BrKit.Terrace(b, xs[k], py, 198, 5, false, (k * 3 + 1) % 5);
                int open = ((k * 3 + 1) % 5 + 2) % 5;
                b.Chest(new Vec3d(row.Cx(0) + C + 3, py, row.Cz(open) + C + 3), -Math.PI / 2);
            }
            b.Open(new Rect2(350, 196, 393, 232));

            // The co-op store.
            double cy = b.Foot(345, 160, 345 + W(4), 160 + W(3), 3);
            b.Style = BrStyle.Plaster; b.Variant = 1;
            BrKit.Warehouse(b, 345, cy, 160);

            // The settling pond (dry) and its pump house on the bottom: the valve room is sealed.
            double ph = b.Foot(358.7, 111.7, 358.7 + W(2), 111.7 + W(2), 2.2);
            var pump = new GreyboxBlock.Building(358.7, 111.7, 2, 2, 1) { Y0 = ph };
            pump.AlongX[0, 2, 0] = WallKind.ShutDoor;
            pump.AlongZ[0, 1, 1] = WallKind.Wall;
            pump.AlongX[0, 1, 1] = WallKind.Wall;
            pump.AlongZ[0, 0, 0] = WallKind.Window;
            b.Style = BrStyle.Concrete; b.Variant = 2;
            b.Raise(pump, "Pump house");
            b.Chest(pump, 1, 1, 0, Math.PI);
            b.Notes<Notes>().SealedChests.Add(Cell(pump, 1, 1, 0));
            b.Chest(pump, 0, 0, 0, 0);
            b.Poi("Settling pond", new Vec3d(Pond.CX, ph, Pond.CZ));

            // The coke ovens: a long row of sealed-off bays.
            double ky = b.Foot(405, 112, 405 + W(1), 112 + W(5), 2.2);
            b.Style = BrStyle.Industrial; b.Variant = 4;
            BrKit.Terrace(b, 405, ky, 112, 5, false, 2);

            // Two sealed sheds.
            b.Style = BrStyle.Timber; b.Variant = 1;
            foreach (var (x, z) in new[] { (392.0, 168.0), (406.0, 184.0) })
            {
                double sy = b.Foot(x, z, x + W(1), z + W(1), 2.2);
                BrKit.Shed(b, x, sy, z);
            }
            b.LowWall(340, 246, 9, true, 1);
            b.LowWall(372, 266, 7.5, true, 1);
            b.Bush(386, 160, 31);
            b.Tree(392, 245, 32);
        }

        // ================================================================ The railhead

        private static void Railhead(BrBuild b)
        {
            b.Style = BrStyle.Timber; b.Variant = 2;
            double gy = b.Foot(165, 95, 165 + W(4), 95 + W(3), 3);
            BrKit.Warehouse(b, 165, gy, 95);

            double sy = b.Foot(215, 95, 215 + W(2), 95 + W(3), 2.2);
            var sig = BrKit.TowerPlan(b, 215, sy, 95, 2, 3, 3);
            b.Style = BrStyle.Painted; b.Variant = 2;
            b.Raise(sig, "Signal box");
            BrKit.Parapet(b, sig);
            b.Chest(sig, 1, 2, 1, Math.PI);
            b.Chest(sig, 1, 2, 3, Math.PI);
            b.Notes<Notes>().HighSpots.Add(("signal box roof", Cell(sig, 1, 0, 3)));

            double ly = b.Foot(245, 95, 245 + W(4), 95 + W(3), 3);
            b.Style = BrStyle.Industrial; b.Variant = 5;
            BrKit.Warehouse(b, 245, ly, 95);

            double my = b.Foot(290, 97, 312.5, 119.5, 2.2);
            b.Style = BrStyle.Concrete; b.Variant = 3;
            BrKit.Compound(b, 290, my, 97);

            // The siding: open wagons, a few with loot in them.
            double[] wx = { 180, 193, 206, 226, 239, 262, 275, 300, 313 };
            for (int k = 0; k < wx.Length; k++)
            {
                double y = b.Foot(wx[k], 128, wx[k] + 9, 131, 1.2);
                FactoryKit.Wagon(b, wx[k], y, 128);
                if (k % 2 == 1) b.Chest(new Vec3d(wx[k] + 4.5, y, 129.5), Math.PI / 2);
            }
            b.Style = BrStyle.Painted; b.Variant = 3;
            foreach (double x in new[] { 252.0, 326.0 })
            {
                double hy = b.Foot(x, 136, x + W(1), 136 + W(1), 2.2);
                var hut = new GreyboxBlock.Building(x, 136, 1, 1, 1) { Y0 = hy };
                hut.AlongX[0, 0, 0] = WallKind.Door;
                b.Raise(hut, "Weigh house");
                b.Chest(hut, 0, 0, 0, 0);
            }
            b.Poi("Railhead", new Vec3d(240, gy, 120));
        }

        // ================================================================ Slag tips, the west and the north west

        private static void Tips(BrBuild b)
        {
            b.SpotChest(BigTipX, BigTipZ, 0);
            b.SpotChest(100, 205, Math.PI / 2);
            b.SpotChest(101, 262, Math.PI / 2);
            b.SpotChest(NorthTipX, NorthTipZ, Math.PI);
            b.Poi("Big Tip", new Vec3d(BigTipX, BigTipH, BigTipZ));

            // Tip huts below the west bank.
            b.Style = BrStyle.Timber; b.Variant = 3;
            foreach (double z in new[] { 182.0, 240.0 })
            {
                double hy = b.Foot(126, z, 126 + W(1), z + W(1), 2.5);
                var hut = new GreyboxBlock.Building(126, z, 1, 1, 1) { Y0 = hy };
                hut.AlongZ[0, 1, 0] = WallKind.ShutDoor;
                b.Raise(hut, "Tip hut");
                b.Chest(hut, 0, 0, 0, Math.PI / 2);
            }

            // A house and a terrace between the west bank and the north tip.
            double hhy = b.Foot(100, 310, 100 + W(2), 310 + W(3), 2.2);
            b.Style = BrStyle.Plaster; b.Variant = 2;
            FactoryKit.House(b, 100, hhy, 310, "Tipman's house");
            double ty = b.Foot(122, 305, 122 + W(1), 305 + W(3), 2.2);
            b.Style = BrStyle.Brick; b.Variant = 8;
            BrKit.Terrace(b, 122, ty, 305, 3, false, 0);

            // The engine shed, the pigeon lofts and the allotments behind the spawn region.
            double ey = b.Foot(160, 395, 160 + W(4), 395 + W(3), 3);
            b.Style = BrStyle.Brick; b.Variant = 9;
            BrKit.Warehouse(b, 160, ey, 395);
            double cy = b.Foot(160, 342, 182.5, 364.5, 2.2);
            b.Style = BrStyle.Stone; b.Variant = 2;
            BrKit.Compound(b, 160, cy, 342);
            b.Style = BrStyle.Timber; b.Variant = 4;
            foreach (var (x, z) in new[] { (190.0, 345.0), (190.0, 372.0), (166.0, 374.0) })
            {
                double sy = b.Foot(x, z, x + W(1), z + W(1), 2.2);
                BrKit.Shed(b, x, sy, z);
            }
            b.LowWall(184, 384, 7.5, true, 1);
            b.Poi("Engine shed", new Vec3d(172, ey, 404));
        }

        // ================================================================ Between the works and the approach

        private static void Approach(BrBuild b)
        {
            double fy = b.Foot(300, 342, 300 + W(2), 342 + W(2), 2.2);
            var fire = new GreyboxBlock.Building(300, 342, 2, 2, 1) { Y0 = fy };
            fire.AlongX[0, 0, 0] = WallKind.WideDoor;
            fire.AlongX[0, 0, 1] = WallKind.WideDoor;
            b.Style = BrStyle.Painted; b.Variant = 4;
            b.Raise(fire, "Fire station");
            b.Chest(fire, 1, 1, 0, Math.PI);
            double ly = b.Foot(270, 343, 270 + W(1), 343 + W(1), 2.2);
            b.Style = BrStyle.Brick; b.Variant = 10;
            BrKit.Shed(b, 270, ly, 343);
            // Soft cover between the pad lanes: bushes and rocks (they never change the approach's ground).
            b.Bush(290, 389.8, 51);
            b.Bush(270, 390, 53);
        }
    }
}
