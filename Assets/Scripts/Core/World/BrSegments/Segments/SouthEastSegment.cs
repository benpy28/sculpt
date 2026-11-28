using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The South East segment of the battle royale map (id 2, see <see cref="BrLayout"/>): the Quarry.
    ///
    /// The middle of the segment is a deep terraced pit (<see cref="Quarry"/>): 180 x 150 m at the rim, four 6 m rock faces
    /// (70 degrees, nobody climbs them) with 13 m benches between, down to a floor 24 m under the rim. Haul ramps spiral
    /// down the benches (two or three ways onto every level), a straight line of steel stairs runs up the north side from
    /// the floor to the rim (breakable: break a flight and that shortcut is gone), a conveyor climbs from the floor into
    /// the crusher tower, and a 185 m conveyor gallery crosses the whole pit high up from the crusher tower to the loading
    /// hopper on the south rim, on hollow pylons standing on the benches. Low safety berms run along the bench edges
    /// (cover), sealed sheds on the benches and a site hut and pump house on the floor hold the pit's loot.
    ///
    /// Round the rim: the crusher works (a ten-storey crusher tower whose fast way up is a hole, the screening plant, the
    /// pay office and the lamp house), the blasting magazine (sealed bunkers behind walls too tall to climb, with C4 and
    /// grenades), the loading hopper, workshops and fitters' row on the east rim, a silo tower and the rail loading shed in
    /// the north east, the miners' row and institute on the west rim. Spoil heaps (28 degree tips with flat tops and a
    /// chest on each) are the hills. The home POI round the anchor is the quarry offices: a three-storey site office on
    /// the west side of the plaza (it stops every pad's view past the anchor), the weighbridge hall, the canteen and a
    /// sealed strongroom.
    /// </summary>
    public sealed class SouthEastSegment : BrSegment
    {
        public override int Id => 2;

        public override string HomePoiName => "Quarry Offices";

        public override double Ground(double x, double z) => Quarry.Ground(x, z);

        private const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, Base = BrGround.Base;

        public override void Build(BrBuild b)
        {
            var avoid = new List<Rect2>(); // things in the pit the berms keep clear of
            Home(b);
            Pit(b, avoid);
            CrusherWorks(b);
            SouthRim(b);
            EastRim(b);
            NorthEast(b);
            WestRim(b);
            QuarryVillage(b);
            Heaps(b);
            Cover(b);
            Berms(b, avoid);
            Infill(b);
            Nature(b);
        }

        // ================================================================ the quarry village (north band, rail cottages, chapel)

        private static void QuarryVillage(BrBuild b)
        {
            // Two rows of workers' cottages and a boarding house between the crusher works and the offices.
            b.Style = BrStyle.Brick; b.Variant = 5;
            double y1 = b.Foot(975.0, 344.0, 975.0 + 4 * M + Col, 344.0 + M + Col, 2);
            BrKit.Terrace(b, 975.0, y1, 344.0, 4, true, 1);
            b.Style = BrStyle.Painted; b.Variant = 4;
            double y2 = b.Foot(1005.0, 344.0, 1005.0 + 4 * M + Col, 344.0 + M + Col, 2);
            BrKit.Terrace(b, 1005.0, y2, 344.0, 4, true, 3);

            b.Style = BrStyle.Plaster; b.Variant = 4;
            var bh = new GreyboxBlock.Building(1036.0, 332.0, 3, 3, 2);
            b.Foot(bh, 2);
            b.Stairwell(bh, 0, true, +1);
            for (int s = 0; s < 2; s++)
            {
                for (int j = 0; j < 3; j++) bh.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                bh.AlongZ[s, 2, 0] = WallKind.Wall; // two rooms on each floor in the east lanes
                bh.AlongZ[s, 2, 1] = WallKind.Door;
                bh.AlongZ[s, 2, 2] = WallKind.Wall;
                bh.AlongX[s, 3, 2] = WallKind.Window;
                bh.AlongZ[s, 3, 1] = WallKind.Window;
            }
            bh.AlongX[0, 0, 1] = WallKind.ShutDoor;
            bh.AlongX[1, 0, 2] = WallKind.Window;
            bh.AlongX[1, 3, 1] = WallKind.Window;
            bh.AlongX[0, 1, 2] = WallKind.Wall; // ground floor: a sealed pantry in the south east corner
            bh.AlongZ[0, 2, 0] = WallKind.Wall;
            b.Raise(bh, "Boarding house");
            BrKit.Parapet(b, bh);
            b.Chest(bh, 2, 0, 0, Math.PI);
            b.Chest(bh, 1, 2, 0, Math.PI);
            b.Chest(bh, 2, 2, 1, Math.PI);
            b.Chest(bh, 1, 0, 1, 0);
            b.Chest(bh, 2, 1, 2, Math.PI);

            // Rail cottages in the north east corner and the rail office.
            b.Style = BrStyle.Timber; b.Variant = 5;
            double ry = b.Foot(1198.0, 327.0, 1198.0 + M + Col, 327.0 + 4 * M + Col, 2);
            BrKit.Terrace(b, 1198.0, ry, 327.0, 4, false, 2);
            b.Style = BrStyle.Brick; b.Variant = 6;
            var ro = new GreyboxBlock.Building(1120.0, 333.0, 2, 3);
            b.Foot(ro, 2);
            HouseWalls(b, ro);
            b.Raise(ro, "Rail office");
            BrKit.Parapet(b, ro);
            b.Chest(ro, 0, 2, 0, Math.PI / 2);
            b.Chest(ro, 0, 0, 1, Math.PI / 2);

            // West rim: a third row by the pit and the chapel (a sealed vestry).
            b.Style = BrStyle.Stone; b.Variant = 3;
            double wy = b.Foot(940.0, 196.0, 940.0 + M + Col, 196.0 + 3 * M + Col, 2);
            BrKit.Terrace(b, 940.0, wy, 196.0, 3, false, 0);
            b.Style = BrStyle.Stone; b.Variant = 4;
            var ch = new GreyboxBlock.Building(935.0, 224.0, 2, 2, 1);
            b.Foot(ch, 2);
            ch.AlongX[0, 0, 0] = WallKind.ShutDoor;
            ch.AlongZ[0, 0, 1] = WallKind.Window;
            ch.AlongX[0, 2, 0] = WallKind.Window;
            ch.AlongZ[0, 1, 1] = WallKind.Wall; // the vestry: cell (1,1)
            ch.AlongX[0, 1, 1] = WallKind.Wall;
            b.Raise(ch, "Chapel");
            b.Chest(ch, 1, 1, 0, Math.PI);
            b.Chest(ch, 0, 1, 0, Math.PI);

            // Spares store on the south rim (a warehouse: blank long sides, sealed storerooms).
            b.Style = BrStyle.Steel; b.Variant = 5;
            double sy = b.Foot(1112.0, 86.0, 1112.0 + 4 * M + Col, 86.0 + 3 * M + Col, 2);
            BrKit.Warehouse(b, 1112.0, sy, 86.0);

            b.Poi("Quarry Village", new Vec3d(1010, Base, 340), bh.Bounds);
        }

        // ================================================================ infill: workers' plots in what is left

        private static void Infill(BrBuild b)
        {
            // Never in the pit: only the strips round it, far enough from the lip that no footing's bank reaches a face.
            Rect2 a = b.Area;
            const int cap = 168;
            BrKit.Villages(b, new Rect2(a.X0, a.Z0, 945, a.Z1), 11, 20, cap);
            BrKit.Villages(b, new Rect2(1152, a.Z0, a.X1, a.Z1), 12, 20, cap);
            BrKit.Villages(b, new Rect2(945, a.Z0, 1152, 118), 13, 20, cap);
            BrKit.Villages(b, new Rect2(945, 300, 1152, a.Z1), 14, 20, cap);
        }

        // ================================================================ home: the quarry offices

        private static void Home(BrBuild b)
        {
            Vec3d a = b.Anchor; // (915.84, 389.76)

            // Site office: three storeys along the plaza's west side. Its long east face stops every pad's view past the anchor.
            b.Style = BrStyle.Plaster; b.Variant = 1;
            var o = new GreyboxBlock.Building(884.9, 368.0, 3, 5, 3);
            b.Foot(o, 2, Base);
            b.Stairwell(o, 0, true, +1);
            for (int s = 0; s < 3; s++)
            {
                // The stair lane's side wall: open where the flights start and arrive.
                for (int j = 0; j < 3; j++) o.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                // East face (onto the plaza): windows; the main door on the ground floor.
                for (int j = 0; j < 5; j++) o.AlongZ[s, 3, j] = j % 2 == 0 ? WallKind.Window : WallKind.Wall;
                o.AlongZ[s, 0, 3] = WallKind.Window; // west, toward the street
                o.AlongX[s, 5, 1] = WallKind.Window;
                if (s > 0)
                {
                    o.AlongZ[s, 1, 3] = WallKind.Door;
                    o.AlongX[s, 3, 1] = WallKind.Door; // open-plan offices, one partition
                    o.AlongX[s, 3, 2] = WallKind.Wall;
                    o.AlongX[s, 0, 2] = WallKind.Window;
                }
            }
            o.AlongZ[0, 3, 2] = WallKind.ShutDoor;   // main door
            o.AlongX[0, 0, 2] = WallKind.ShutDoor;   // side door, south
            // Ground floor: a sealed records room in the stair lane's north end (cells (0,3) and (0,4)).
            o.AlongX[0, 3, 0] = WallKind.Wall;
            o.AlongZ[0, 1, 3] = WallKind.Wall;
            o.AlongZ[0, 1, 4] = WallKind.Wall;
            o.AlongZ[0, 0, 3] = WallKind.Wall;
            o.AlongX[0, 3, 1] = WallKind.Door;
            b.Raise(o, "Site office");
            BrKit.Parapet(b, o);
            b.Chest(o, 0, 4, 0, Math.PI);      // records room (sealed)
            b.Chest(o, 2, 4, 1, Math.PI);
            b.Chest(o, 2, 0, 2, 0);
            b.Chest(o, 1, 3, 3, Math.PI);      // the roof

            // Weighbridge hall on the north side: a sealed west bay, a wide door onto the plaza.
            b.Style = BrStyle.Steel; b.Variant = 2;
            var wb = new GreyboxBlock.Building(906.0, 408.5, 3, 1, 1);
            b.Foot(wb, 2, Base);
            wb.AlongX[0, 0, 1] = WallKind.WideDoor;
            wb.AlongX[0, 0, 2] = WallKind.Window;
            wb.AlongZ[0, 3, 0] = WallKind.ShutDoor;
            wb.AlongZ[0, 1, 0] = WallKind.Wall;  // the sealed bay
            b.Raise(wb, "Weighbridge hall");
            b.Chest(wb, 0, 0, 0, 0);
            b.Chest(wb, 2, 0, 0, Math.PI);

            // Canteen on the south side, with a sealed store.
            b.Style = BrStyle.Timber; b.Variant = 1;
            var cn = new GreyboxBlock.Building(907.0, 358.5, 2, 2, 1);
            b.Foot(cn, 2, Base);
            cn.AlongX[0, 2, 1] = WallKind.ShutDoor;
            cn.AlongX[0, 2, 0] = WallKind.Window;
            cn.AlongZ[0, 2, 1] = WallKind.Window;
            cn.AlongX[0, 1, 0] = WallKind.Wall;
            cn.AlongZ[0, 1, 0] = WallKind.Wall;
            b.Raise(cn, "Canteen");
            b.Chest(cn, 0, 0, 0, 0);
            b.Chest(cn, 1, 1, 0, Math.PI);

            // Strongroom: sealed.
            b.Style = BrStyle.Concrete; b.Variant = 3;
            var sr = new GreyboxBlock.Building(889.0, 403.0, 1, 1, 1);
            b.Foot(sr, 2, Base);
            b.Raise(sr, "Strongroom");
            b.Chest(sr, 0, 0, 0, 0);

            // The plaza: a chest by the weighbridge platform's barriers, low walls for cover.
            b.Chest(new Vec3d(a.X + 4, Base, a.Z + 6), -Math.PI / 2);
            b.LowWall(a.X + 8, a.Z + 9, 4.5, true, 1);
            b.LowWall(a.X + 8, a.Z - 10, 4.5, true, 1);
            b.LowWall(a.X - 3, a.Z - 2, 4.5, false, 1);
            b.Poi("Quarry Offices", a, o.Bounds, wb.Bounds, cn.Bounds, sr.Bounds);
        }

        // ================================================================ the pit

        private static void Pit(BrBuild b, List<Rect2> avoid)
        {
            double cx = Quarry.Cx, cz = Quarry.Cz;

            // Steel stairs up the north side, floor to rim: four flights zig-zagging 4 m apart (each lands on its bench
            // beside the next one's foot, so a sprint up one flight stops on the bench).
            for (int L = 0; L < 4; L++)
            {
                double sx0 = L % 2 == 0 ? 1023.5 : 1027.5;
                double zHi = cz + Quarry.Az + Quarry.Lip(L), zLo = zHi - 2 * Quarry.Rise;
                var box = new Aabb(new Vec3d(sx0, Quarry.Level(L), zLo), new Vec3d(sx0 + 3.0, Quarry.Level(L + 1), zHi));
                b.Climb(box, Dir.North, true);
                avoid.Add(new Rect2(sx0, zLo, sx0 + 3.0, zHi));
            }

            // The conveyor: from the pit floor up into the crusher tower's second floor (19 degrees, 96 m).
            double convTop = Base + 2 * GreyboxBlock.StoreyHeight;
            var conv = new Aabb(new Vec3d(ConvX0, Quarry.Floor, 208.0), new Vec3d(ConvX0 + 3.0, convTop, TowerZ0));
            b.Climb(conv, Dir.North, false);
            avoid.Add(new Rect2(ConvX0, 208.0, ConvX0 + 3.0, 260.0)); // where it is low over the benches

            // The gallery's pylons: hollow 3 x 3 m towers of tiles on the benches and the floor.
            double galleryUnder = GalleryY - Col;
            double Mid(int L) => (Quarry.Lip(L - 1) + Quarry.FaceFoot(L)) / 2;
            foreach ((double z, int L) in new[] { (cz - Quarry.Az - Mid(3), 3), (cz - Quarry.Az - Mid(1), 1), (cz, 0), (cz + Quarry.Az + Mid(1), 1), (cz + Quarry.Az + Mid(3), 3) })
            {
                double g = Quarry.Level(L);
                int rows = (int)Math.Floor((galleryUnder - g - 0.3) / 1.5);
                double x0 = GalleryX0;
                b.Strip(new Vec3d(x0, g, z - 1.5), 3.0, true, rows);
                b.Strip(new Vec3d(x0, g, z + 1.2), 3.0, true, rows);
                b.Strip(new Vec3d(x0, g, z - 1.2), 2.4, false, rows);
                b.Strip(new Vec3d(x0 + 2.7, g, z - 1.2), 2.4, false, rows);
                avoid.Add(new Rect2(x0, z - 1.5, x0 + 3.0, z + 1.5));
            }

            // On the floor: the site hut, the pump house (sealed), low walls, two chests in the open.
            b.Style = BrStyle.Timber; b.Variant = 2;
            var hut = new GreyboxBlock.Building(1029.0, 212.0, 2, 2, 1) { Y0 = Quarry.Floor };
            hut.AlongX[0, 0, 0] = WallKind.ShutDoor;
            hut.AlongX[0, 2, 1] = WallKind.ShutDoor;
            hut.AlongZ[0, 0, 1] = WallKind.Window;
            hut.AlongZ[0, 2, 0] = WallKind.Window;
            hut.AlongX[0, 1, 1] = WallKind.Wall; // a back office, through the hut
            hut.AlongZ[0, 1, 1] = WallKind.Door;
            b.Raise(hut, "Site hut");
            b.Chest(hut, 1, 1, 0, Math.PI);
            b.Chest(hut, 0, 0, 0, 0);
            b.Item(Item.Grenade, 2, new Vec3d(hut.Cx(1) + Col + 2, Quarry.Floor, hut.Cz(0) + Col + 1.5));
            avoid.Add(Rect2.Of(hut.Bounds));

            b.Style = BrStyle.Concrete; b.Variant = 1;
            var pump = new GreyboxBlock.Building(1068.0, 190.0, 1, 1, 1) { Y0 = Quarry.Floor };
            b.Raise(pump, "Pump house");
            b.Chest(pump, 0, 0, 0, 0);
            avoid.Add(Rect2.Of(pump.Bounds));

            // Low walls on the floor (blocks left by the dumpers): cover in the bottom of the bowl.
            (double x, double z, double len, bool alongX, int rows)[] floorWalls =
            {
                (1062, 228, 6, true, 1), (1078, 214, 6, false, 1), (1020, 196, 6, true, 1), (1070, 230, 4.5, true, 2),
                (1060, 196, 4.5, false, 1), (1036, 202, 4.5, true, 1),
            };
            foreach (var w in floorWalls)
            {
                b.Strip(new Vec3d(w.x, Quarry.Floor, w.z), w.len, w.alongX, w.rows);
                avoid.Add(new Rect2(w.x, w.z, w.alongX ? w.x + w.len : w.x + Col, w.alongX ? w.z + Col : w.z + w.len));
            }
            b.Chest(new Vec3d(1081, Quarry.Floor, 205), -Math.PI / 2);

            // Dumper shelter: open along its south side, two bays.
            b.Style = BrStyle.Steel; b.Variant = 6;
            var shel = new GreyboxBlock.Building(1060.0, 216.0, 2, 1, 1) { Y0 = Quarry.Floor };
            shel.AlongX[0, 0, 0] = WallKind.WideDoor;
            shel.AlongX[0, 0, 1] = WallKind.WideDoor;
            b.Raise(shel, "Dumper shelter");
            b.Chest(shel, 0, 0, 0, Math.PI);
            b.Chest(shel, 1, 0, 0, Math.PI);
            avoid.Add(Rect2.Of(shel.Bounds));

            // Chests on the rim road, one a side.
            b.Chest(new Vec3d(1090, Base, 290), Math.PI);
            b.Chest(new Vec3d(1070, Base, 130), 0);
            b.Chest(new Vec3d(1142, Base, 230), -Math.PI / 2);
            b.Chest(new Vec3d(957, Base, 190), Math.PI / 2);

            // Sealed sheds on the benches.
            b.Style = BrStyle.Industrial; b.Variant = 1;
            (double x, double z, int L, string name)[] sheds =
            {
                (1020.0, 170.0, 1, "Powder shed"), (984.0, 224.0, 2, "Tool shed"), (1125.6, 195.0, 3, "Fuel shed"), (1070.0, 257.3, 2, "Drill shed"),
                (1019.5, 157.0, 2, "Winch shed"), (1035.0, 274.0, 3, "Lamp shed"),
            };
            foreach (var s in sheds)
            {
                var t = new GreyboxBlock.Building(s.x, s.z, 1, 1, 1) { Y0 = Quarry.Level(s.L) };
                b.Raise(t, s.name);
                b.Chest(t, 0, 0, 0, 0);
                avoid.Add(Rect2.Of(t.Bounds));
            }
            // Chests behind the berms.
            b.Chest(new Vec3d(1012, Quarry.Level(3), 276), Math.PI);
            b.Chest(new Vec3d(1085, Quarry.Level(3), 143), 0);
            b.Chest(new Vec3d(1102, Quarry.Level(1), 215), -Math.PI / 2);
            b.Chest(new Vec3d(1000, Quarry.Level(2), 158), 0);
            b.Chest(new Vec3d(1118, Quarry.Level(2), 230), Math.PI);
            b.Chest(new Vec3d(1000, Quarry.Level(1), 245), 0);
            b.Chest(new Vec3d(1040, Quarry.Floor, 190), 0);

            b.Poi("The Pit", new Vec3d(cx, Quarry.Floor, cz), hut.Bounds, pump.Bounds);
        }

        // The conveyor and gallery positions (they line up with doors in the crusher tower and the hopper).
        private const double TowerX0 = 1038.0, TowerZ0 = 304.0, HopperZ0 = 100.0;
        /// <summary>Conveyor: the middle two tiles of the tower's south wall, segment 1.</summary>
        private const double ConvX0 = TowerX0 + M + Col + 1.5;
        /// <summary>Gallery: the middle two tiles of segment 2.</summary>
        private const double GalleryX0 = TowerX0 + 2 * M + Col + 1.5;
        /// <summary>The gallery's walking level: the third floor of the tower and the hopper.</summary>
        private const double GalleryY = Base + 3 * GreyboxBlock.StoreyHeight;

        // ================================================================ north rim: the crusher works

        private static void CrusherWorks(BrBuild b)
        {
            // Crusher tower: ten storeys. The conveyor comes in on the second floor, the gallery leaves from the third.
            b.Foot(TowerX0, TowerZ0, TowerX0 + 3 * M + Col, TowerZ0 + 3 * M + Col, 3, Base);
            var t = BrKit.TowerPlan(b, TowerX0, Base, TowerZ0, 3, 3, 10);
            t.AlongX[2, 0, 1] = WallKind.Door;
            t.AlongX[3, 0, 2] = WallKind.Door;
            b.Style = BrStyle.Industrial; b.Variant = 2;
            b.Raise(t, "Crusher tower");
            BrKit.Parapet(b, t);
            for (int s = 0; s < 10; s++) b.Chest(t, 2, s % 2 == 0 ? 2 : 1, s, Math.PI);
            b.Chest(t, 2, 2, 10, Math.PI);
            b.Item(Item.Grenade, 2, new Vec3d(t.Cx(1) + Col + 3, Base, t.Cz(2) + Col + 3));

            // The gallery: 185 m of walkway across the pit at the third floor, parapets both sides, a platform over the middle.
            double z0 = HopperZ0 + 3 * M + Col, z1 = TowerZ0;
            Deck(b, GalleryX0, z0, GalleryX0 + 3.0, z1, GalleryY);
            Deck(b, GalleryX0 - 6.3, 204.0, GalleryX0, 214.0, GalleryY);
            Deck(b, GalleryX0 + 3.0, 204.0, GalleryX0 + 9.3, 214.0, GalleryY);
            // Parapets: along the walkway, broken by the platform.
            foreach (double px in new[] { GalleryX0, GalleryX0 + 3.0 - Col })
            {
                b.Strip(new Vec3d(px, GalleryY, z0 + 0.3), 204.0 - z0 - 0.3, false, 1);
                b.Strip(new Vec3d(px, GalleryY, 214.0), z1 - 214.0 - 0.3, false, 1);
            }
            // Screens (two rows) for a stretch on each half: somewhere to stop crossing.
            b.Strip(new Vec3d(GalleryX0, GalleryY + 1.5, 150.0), 9.0, false, 1);
            b.Strip(new Vec3d(GalleryX0 + 3.0 - Col, GalleryY + 1.5, 150.0), 9.0, false, 1);
            b.Strip(new Vec3d(GalleryX0, GalleryY + 1.5, 258.0), 9.0, false, 1);
            b.Strip(new Vec3d(GalleryX0 + 3.0 - Col, GalleryY + 1.5, 258.0), 9.0, false, 1);
            // The platform's edge: low walls round it with gaps.
            double pw0 = GalleryX0 - 6.3, pw1 = GalleryX0 + 9.3;
            b.Strip(new Vec3d(pw0, GalleryY, 204.0), 4.5, true, 1);
            b.Strip(new Vec3d(pw1 - 4.5, GalleryY, 204.0), 4.5, true, 1);
            b.Strip(new Vec3d(pw0, GalleryY, 214.0 - Col), 4.5, true, 1);
            b.Strip(new Vec3d(pw1 - 4.5, GalleryY, 214.0 - Col), 4.5, true, 1);
            b.Strip(new Vec3d(pw0, GalleryY, 205.5), 7.0, false, 1);
            b.Strip(new Vec3d(pw1 - Col, GalleryY, 205.5), 7.0, false, 1);
            b.Chest(new Vec3d(pw0 + 2.5, GalleryY, 209.0), Math.PI / 2);
            b.Chest(new Vec3d(GalleryX0 + 1.5, GalleryY, 154.5), 0);       // behind the screens
            b.Chest(new Vec3d(GalleryX0 + 1.5, GalleryY, 262.5), Math.PI);

            // Screening plant beside the tower: long blank side onto the pit, a sealed motor room.
            b.Style = BrStyle.Steel; b.Variant = 1;
            var p = new GreyboxBlock.Building(1061.0, TowerZ0, 5, 3, 2);
            b.Foot(p, 3, Base);
            b.Stairwell(p, 0, false, +1);
            p.AlongZ[0, 5, 1] = WallKind.WideDoor;
            p.AlongZ[0, 0, 1] = WallKind.ShutDoor;
            p.AlongX[0, 3, 2] = WallKind.ShutDoor;
            p.AlongX[1, 0, 1] = WallKind.Window;
            p.AlongX[1, 0, 3] = WallKind.Window;
            p.AlongX[1, 3, 0] = WallKind.Window;
            p.AlongX[1, 3, 4] = WallKind.Window;
            p.AlongZ[1, 5, 1] = WallKind.Window;
            p.AlongX[0, 2, 0] = WallKind.Wall;  // motor room: cells (0,2) and (1,2)
            p.AlongX[0, 2, 1] = WallKind.Wall;
            p.AlongZ[0, 2, 2] = WallKind.Wall;
            p.AlongX[0, 1, 4] = WallKind.Wall;  // a screen bay
            p.AlongZ[0, 4, 1] = WallKind.Door;
            b.Raise(p, "Screening plant");
            BrKit.Parapet(b, p);
            b.Chest(p, 0, 2, 0, Math.PI);
            b.Chest(p, 4, 0, 0, 0);
            b.Chest(p, 2, 2, 1, Math.PI);
            b.Chest(p, 4, 2, 2, Math.PI);

            // Pay office (sealed shed) and the lamp house, west of the tower.
            b.Style = BrStyle.Concrete; b.Variant = 2;
            var pay = new GreyboxBlock.Building(1016.0, 306.0, 1, 1, 1);
            b.Foot(pay, 2);
            b.Raise(pay, "Pay office");
            b.Chest(pay, 0, 0, 0, 0);

            b.Style = BrStyle.Brick; b.Variant = 2;
            var lamp = new GreyboxBlock.Building(986.0, 300.0, 2, 3);
            b.Foot(lamp, 2);
            HouseWalls(b, lamp);
            b.Raise(lamp, "Lamp house");
            BrKit.Parapet(b, lamp);
            b.Chest(lamp, 0, 2, 0, Math.PI / 2);
            b.Chest(lamp, 0, 0, 1, Math.PI / 2);

            // Changing rooms: a row with a sealed end room.
            b.Style = BrStyle.Painted; b.Variant = 3;
            double cy = b.Foot(1003.0, 326.0, 1003.0 + 3 * M + Col, 326.0 + M + Col, 2);
            BrKit.Terrace(b, 1003.0, cy, 326.0, 3, true, 2);

            b.Poi("Crusher Works", new Vec3d(1050, Base, 312), t.Bounds, p.Bounds);
        }

        /// <summary>A walkway deck of floor tiles (about 1.5 m square) at <paramref name="top"/>.</summary>
        private static void Deck(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            double s = b.World.Tiles.Config.TileSize;
            int nx = (int)Math.Ceiling((x1 - x0) / s - 1e-9), nz = (int)Math.Ceiling((z1 - z0) / s - 1e-9);
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    var box = new Aabb(new Vec3d(x0 + (x1 - x0) * i / nx, top - Col, z0 + (z1 - z0) * j / nz),
                                       new Vec3d(x0 + (x1 - x0) * (i + 1) / nx, top, z0 + (z1 - z0) * (j + 1) / nz));
                    int id = b.World.Tiles.AddTile(box, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
            b.Info.Bridges.Add(new Aabb(new Vec3d(x0, top - Col, z0), new Vec3d(x1, top, z1)));
        }

        /// <summary>The kit house's walls (2 x 3 modules, stairs in the east lane) on a building not yet raised.</summary>
        private static void HouseWalls(BrBuild b, GreyboxBlock.Building t)
        {
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
        }

        // ================================================================ south rim: hopper, magazine, blaster's house

        private static void SouthRim(BrBuild b)
        {
            // Loading hopper: four storeys, the gallery arrives on the third.
            b.Foot(TowerX0, HopperZ0, TowerX0 + 3 * M + Col, HopperZ0 + 3 * M + Col, 3, Base);
            var h = BrKit.TowerPlan(b, TowerX0, Base, HopperZ0, 3, 3, 4);
            h.AlongX[3, 3, 2] = WallKind.Door;
            b.Style = BrStyle.Industrial; b.Variant = 3;
            b.Raise(h, "Loading hopper");
            BrKit.Parapet(b, h);
            for (int s = 0; s < 4; s++) b.Chest(h, 2, s % 2 == 0 ? 0 : 1, s, Math.PI);
            b.Chest(h, 2, 2, 4, Math.PI);

            // Blasting magazine: three sealed bunkers inside 4.5 m blast walls (too tall to climb), one gate onto the pit.
            const double x0 = 976.0, z0 = 88.5, x1 = 1018.0, z1 = 112.5, gate = 3.0;
            double y = b.Foot(x0, z0, x1, z1, 2);
            double half = (x1 - x0 - gate) / 2;
            b.Strip(new Vec3d(x0, y, z0), x1 - x0, true, 3);
            b.Strip(new Vec3d(x0, y, z1 - Col), half, true, 3);
            b.Strip(new Vec3d(x0 + half + gate, y, z1 - Col), half, true, 3);
            b.Strip(new Vec3d(x0, y, z0 + Col), z1 - z0 - 2 * Col, false, 3);
            b.Strip(new Vec3d(x1 - Col, y, z0 + Col), z1 - z0 - 2 * Col, false, 3);
            b.Style = BrStyle.Concrete; b.Variant = 4;
            Item[] loot = { Item.C4, Item.Grenade, Item.Launcher };
            int[] counts = { 2, 2, 1 };
            for (int k = 0; k < 3; k++)
            {
                var bk = new GreyboxBlock.Building(980.0 + k * 11.5, 94.0, 1, 1, 1) { Y0 = y };
                b.Raise(bk, "Magazine bunker");
                b.Chest(bk, 0, 0, 0, 0);
                b.Item(loot[k], counts[k], new Vec3d(bk.Cx(0) + Col + 1.4, y, bk.Cz(0) + Col + 4.6));
            }
            b.Chest(new Vec3d(1012.0, y, 107.0), Math.PI);

            // The blaster's house and the weigh station.
            b.Style = BrStyle.Brick; b.Variant = 3;
            var bh = new GreyboxBlock.Building(1068.0, 98.0, 2, 3);
            b.Foot(bh, 2);
            HouseWalls(b, bh);
            b.Raise(bh, "Blaster's house");
            BrKit.Parapet(b, bh);
            b.Chest(bh, 0, 2, 0, Math.PI / 2);
            b.Chest(bh, 0, 0, 1, Math.PI / 2);

            b.Style = BrStyle.Timber; b.Variant = 3;
            double wy = b.Foot(1090.0, 101.0, 1090.0 + 3 * M + Col, 101.0 + M + Col, 2);
            BrKit.Terrace(b, 1090.0, wy, 101.0, 3, true, 0);

            b.Poi("Blasting Magazine", new Vec3d(997, y, 100));
        }

        // ================================================================ east rim: workshops

        private static void EastRim(BrBuild b)
        {
            // Vehicle workshop: garage doors onto the pit, a sealed parts store at the back.
            b.Style = BrStyle.Painted; b.Variant = 1;
            var w = new GreyboxBlock.Building(1157.0, 168.0, 3, 6, 1);
            b.Foot(w, 3);
            w.AlongZ[0, 0, 1] = WallKind.WideDoor;
            w.AlongZ[0, 0, 4] = WallKind.WideDoor;
            w.AlongZ[0, 3, 0] = WallKind.ShutDoor;
            w.AlongZ[0, 3, 2] = WallKind.Window;
            w.AlongX[0, 6, 0] = WallKind.Window;
            for (int i = 0; i < 3; i++) w.AlongX[0, 3, i] = i == 1 ? WallKind.Door : WallKind.Wall; // the two bays
            w.AlongZ[0, 2, 4] = WallKind.Wall; // parts store: cells (2,4), (2,5)
            w.AlongZ[0, 2, 5] = WallKind.Wall;
            w.AlongX[0, 4, 2] = WallKind.Wall;
            b.Raise(w, "Workshop");
            b.Chest(w, 2, 5, 0, Math.PI);
            b.Chest(w, 1, 1, 0, 0);
            b.Chest(w, 0, 4, 0, Math.PI / 2);

            // Fitters' row, a compressor house (sealed) and the foreman's house.
            b.Style = BrStyle.Brick; b.Variant = 4;
            double fy = b.Foot(1158.0, 216.0, 1158.0 + M + Col, 216.0 + 3 * M + Col, 2);
            BrKit.Terrace(b, 1158.0, fy, 216.0, 3, false, 1);

            b.Style = BrStyle.Concrete; b.Variant = 5;
            var cp = new GreyboxBlock.Building(1160.0, 244.0, 1, 1, 1);
            b.Foot(cp, 2);
            b.Raise(cp, "Compressor house");
            b.Chest(cp, 0, 0, 0, 0);

            b.Style = BrStyle.Plaster; b.Variant = 2;
            var fh = new GreyboxBlock.Building(1160.0, 262.0, 2, 3);
            b.Foot(fh, 2);
            HouseWalls(b, fh);
            b.Raise(fh, "Foreman's house");
            BrKit.Parapet(b, fh);
            b.Chest(fh, 0, 2, 0, Math.PI / 2);
            b.Chest(fh, 0, 0, 1, Math.PI / 2);

            b.Poi("Workshops", new Vec3d(1167, Base, 210), w.Bounds);
        }

        // ================================================================ north east: silo and rail loading

        private static void NorthEast(BrBuild b)
        {
            // Silo tower: seven storeys of concrete, two modules across.
            double sy = b.Foot(1110.0, 306.0, 1110.0 + 2 * M + Col, 306.0 + 3 * M + Col, 3);
            b.Style = BrStyle.Concrete; b.Variant = 6;
            var s = BrKit.TowerPlan(b, 1110.0, sy, 306.0, 2, 3, 7);
            b.Raise(s, "Silo tower");
            BrKit.Parapet(b, s);
            for (int k = 0; k < 7; k += 2) b.Chest(s, 1, k % 4 == 0 ? 0 : 2, k, Math.PI);
            b.Chest(s, 1, 2, 7, Math.PI);

            // Rail loading shed (warehouse: blank long sides, sealed storerooms).
            b.Style = BrStyle.Industrial; b.Variant = 4;
            double ly = b.Foot(1131.5, 300.0, 1131.5 + 4 * M + Col, 300.0 + 3 * M + Col, 3);
            BrKit.Warehouse(b, 1131.5, ly, 300.0);

            // Fuel yard: walls too tall to climb, one gate.
            double cy = b.Foot(1160.0, 330.0, 1182.5, 352.5, 1.5);
            b.Style = BrStyle.Steel; b.Variant = 3;
            BrKit.Compound(b, 1160.0, cy, 330.0);
            b.Poi("Silo", new Vec3d(1130, Base, 315), s.Bounds);
        }

        // ================================================================ west rim: miners' row

        private static void WestRim(BrBuild b)
        {
            b.Style = BrStyle.Stone; b.Variant = 1;
            double ay = b.Foot(925.0, 158.0, 925.0 + M + Col, 158.0 + 5 * M + Col, 2);
            BrKit.Terrace(b, 925.0, ay, 158.0, 5, false, 2);
            b.Style = BrStyle.Stone; b.Variant = 2;
            double by = b.Foot(905.0, 158.0, 905.0 + M + Col, 158.0 + 5 * M + Col, 2);
            BrKit.Terrace(b, 905.0, by, 158.0, 5, false, 0);

            // Miners' institute: two storeys, a sealed library.
            b.Style = BrStyle.Brick; b.Variant = 1;
            var it = new GreyboxBlock.Building(904.0, 198.0, 3, 4, 2);
            b.Foot(it, 2);
            b.Stairwell(it, 0, true, +1);
            for (int s = 0; s < 2; s++)
            {
                for (int j = 0; j < 3; j++) it.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                it.AlongZ[s, 3, 0] = WallKind.Window;
                it.AlongX[s, 4, 1] = WallKind.Window;
                it.AlongZ[s, 0, 3] = WallKind.Window;
            }
            it.AlongX[0, 0, 1] = WallKind.ShutDoor;
            it.AlongZ[0, 3, 1] = WallKind.ShutDoor;
            it.AlongZ[0, 2, 2] = WallKind.Wall; // library: cells (2,2), (2,3)
            it.AlongZ[0, 2, 3] = WallKind.Wall;
            it.AlongX[0, 2, 2] = WallKind.Wall;
            it.AlongX[1, 2, 1] = WallKind.Door;
            it.AlongX[1, 2, 2] = WallKind.Wall;
            it.AlongZ[1, 3, 3] = WallKind.Window;
            b.Raise(it, "Miners' institute");
            BrKit.Parapet(b, it);
            b.Chest(it, 2, 3, 0, Math.PI);
            b.Chest(it, 1, 0, 0, 0);
            b.Chest(it, 2, 0, 1, 0);
            b.Chest(it, 1, 3, 2, Math.PI);

            // Two houses north of the west road.
            b.Style = BrStyle.Plaster; b.Variant = 3;
            var h1 = new GreyboxBlock.Building(890.0, 268.0, 2, 3);
            b.Foot(h1, 2);
            HouseWalls(b, h1);
            b.Raise(h1, "House");
            BrKit.Parapet(b, h1);
            b.Chest(h1, 0, 2, 0, Math.PI / 2);
            b.Chest(h1, 0, 0, 1, Math.PI / 2);

            b.Style = BrStyle.Timber; b.Variant = 4;
            var h2 = new GreyboxBlock.Building(930.0, 268.0, 2, 3);
            b.Foot(h2, 2);
            HouseWalls(b, h2);
            b.Raise(h2, "House");
            BrKit.Parapet(b, h2);
            b.Chest(h2, 0, 2, 0, Math.PI / 2);
            b.Chest(h2, 0, 0, 1, Math.PI / 2);

            b.Poi("Miners' Row", new Vec3d(918, Base, 200), it.Bounds);
            b.Open(new Rect2(882.4, 248, 962, 264)); // the west road
        }

        // ================================================================ spoil heaps

        private static void Heaps(BrBuild b)
        {
            foreach (var hp in Quarry.HeapTops) b.SpotChest(hp.x, hp.z, hp.yaw);
        }

        // ================================================================ cover between buildings

        private static void Cover(BrBuild b)
        {
            // Rock-fall barriers and block walls round the rim road and in the yards.
            (double x, double z, double len, bool alongX, int rows)[] walls =
            {
                (970, 300, 6, true, 1), (1100, 290, 6, true, 1), (1146, 255, 6, false, 1), (1148, 150, 4.5, false, 2),
                (960, 140, 6, true, 1), (1030, 126, 6, true, 1), (945, 235, 4.5, false, 1), (1125, 128, 4.5, true, 2),
                (1196, 335, 6, false, 1), (1105, 338, 6, true, 1), (960, 330, 4.5, false, 2), (1020, 345, 6, true, 1),
            };
            foreach (var w in walls)
            {
                var r = new Rect2(w.x, w.z, w.alongX ? w.x + w.len : w.x + Col, w.alongX ? w.z + Col : w.z + w.len);
                if (!b.IsClear(r, 1.5) || b.InHome(r, 2)) continue;
                double near = double.MaxValue, far = double.MinValue;
                foreach ((double x, double z) in new[] { (r.X0, r.Z0), (r.X1, r.Z0), (r.X0, r.Z1), (r.X1, r.Z1) })
                {
                    double d = Quarry.Sdf(x, z);
                    near = Math.Min(near, d); far = Math.Max(far, d);
                }
                // On the rim road (exactly level) a wall needs no footing; near it a footing's bank would fill the
                // top face, so a wall there is skipped; further out it stands on its own little footing.
                if (near > Quarry.Lip(3) + 0.5 && far < Quarry.Lip(3) + Quarry.RimFlat - 0.5) b.Strip(new Vec3d(r.X0, Base, r.Z0), w.len, w.alongX, w.rows);
                else if (near > Quarry.Lip(3) + Quarry.FootClear) b.LowWall(w.x, w.z, w.len, w.alongX, w.rows);
            }

            // Chests along the rim road (no footings near the lip: the road is level).
            b.Chest(new Vec3d(1000, Base, 288), Math.PI);
            b.Chest(new Vec3d(1120, Base, 140), 0);
            b.Chest(new Vec3d(975, Base, 145), Math.PI / 2);
            b.Chest(new Vec3d(1140, Base, 250), -Math.PI / 2);
            b.Chest(new Vec3d(1090, Quarry.Level(2), 262), Math.PI);
            b.Chest(new Vec3d(1006, Quarry.Level(1), 176), 0);
        }

        // ================================================================ berms along the bench edges

        private static void Berms(BrBuild b, List<Rect2> avoid)
        {
            const double piece = 7.5, gap = 4.5;
            for (int side = 0; side < 4; side++)
                for (int L = 1; L <= 3; L++)
                {
                    double half = side % 2 == 0 ? Quarry.Ax : Quarry.Az;
                    double d = Quarry.Lip(L - 1) + 1.0, y = Quarry.Level(L);
                    for (double t = -half + 1.5; t + piece <= half - 1.5; t += piece + gap)
                    {
                        if (Quarry.RampNear(side, L, t, t + piece)) continue;
                        Rect2 r = Quarry.BermRect(side, d, t, piece);
                        bool hit = false;
                        foreach (Rect2 q in avoid) if (q.Grow(2.0).Overlaps(r)) { hit = true; break; }
                        if (hit) continue;
                        b.Strip(new Vec3d(r.X0, y, r.Z0), piece, side % 2 == 0, 1);
                    }
                }
        }

        // ================================================================ props

        private static void Nature(BrBuild b)
        {
            b.Nature.Groves = 3;
            b.Nature.LoneTrees = 12;
            b.Nature.RockGroups = 34;
            b.Nature.Bushes = 30;
            // Boulders on the pit floor (the scatter adds more on the benches where they fit).
            (double x, double z)[] rocks = { (1070, 205) };
            for (int k = 0; k < rocks.Length; k++) b.Rocks(rocks[k].x, rocks[k].z, 9100 + k);
        }

        // ================================================================ the ground

        /// <summary>The quarry's ground: the pit, its haul ramps, the level rim road round it, the spoil heaps.</summary>
        internal static class Quarry
        {
            // Why these odd numbers: the height field samples the ground every 1.5 m (vertices at x, z = 0.3 + 1.5 k). A
            // rock face only reads as a rock face (over 55 degrees) to the field if its whole rise falls inside one cell;
            // where a ramp runs along a face the part of the face above the ramp can be 2-4 m tall, and split across two
            // cells it would be a 53 degree slope, neither hill nor rock. So every straight face is 85 degrees (0.52 m
            // across), and every face, ramp side and ramp end on the straight sides sits in the middle of a cell column:
            // the pit's middle is a cell middle, and the faces are 15 m (ten cells) apart.
            /// <summary>Rock faces at 85 degrees.</summary>
            public static readonly double K = Math.Tan(85 * Math.PI / 180);
            /// <summary>Each rock face rises this much; the floor is this low; faces are this far apart.</summary>
            public const double Rise = 6, Floor = -22.5, Pitch = 15;
            public static double Run => Rise / K;
            /// <summary>The pit's middle (the middle of a height-field cell), and the half size of its floor's core (the
            /// faces follow a rounded rectangle round it), chosen so that every face's middle is a cell's middle.</summary>
            public const double Cx = 1049.55, Cz = 209.55;
            public static readonly double Ax = 42 - 6 - Run / 2, Az = 28.5 - 6 - Run / 2;
            /// <summary>A bench's width (lip of one face to the foot of the next).</summary>
            public static double Bench => Pitch - Run;
            /// <summary>Face i (0: floor up to the lowest bench .. 3: the top bench up to the rim): where its foot and lip are (distance out from the core).</summary>
            public static double FaceFoot(int i) => 6 + i * Pitch;
            public static double Lip(int i) => FaceFoot(i) + Run;
            /// <summary>Level 0 is the floor, 1..3 the benches from the bottom, 4 the rim (base level).</summary>
            public static double Level(int k) => Floor + k * Rise;
            /// <summary>The rim road: exactly level at base for <see cref="RimFlat"/> out from the lip, then easing to the shared ground over <see cref="RimRoad"/>.</summary>
            public const double RimFlat = 12, RimRoad = 25;
            /// <summary>A footing's apron stays this far out from the lip, so its 28 degree bank never reaches the top face
            /// (a 6 m face needs 11.3 m of bank).</summary>
            public const double FootClear = 13;

            /// <summary>A haul ramp: on side 0 north, 1 east, 2 south, 3 west; from level L at T0 up to level L + 1 at T1, W wide against the face.</summary>
            public struct Ramp { public int Side, L; public double T0, T1, W; }

            // Widths and high ends keep the ramps' side and end faces in the middle of a cell (see above): a width of
            // 1.5 k - Run, a high end at 1.5 k - Run / 2 when the ramp climbs toward +t, 1.5 k + Run / 2 toward -t.
            private static double Wide(int cells) => 1.5 * cells - Run;
            private static double EndUp(double t) => t - Run / 2;
            private static double EndDown(double t) => t + Run / 2;

            public static readonly Ramp[] Ramps =
            {
                new Ramp { Side = 0, L = 3, T0 = 34.5, T1 = EndDown(9), W = Wide(6) },   // rim to the top bench, north (by the crusher works)
                new Ramp { Side = 2, L = 3, T0 = -4.5, T1 = EndDown(-33), W = Wide(6) }, // and south (by the magazine)
                new Ramp { Side = 3, L = 3, T0 = -10.5, T1 = EndUp(19.5), W = Wide(6) }, // and west (the west road)
                new Ramp { Side = 1, L = 2, T0 = -15, T1 = EndUp(15), W = Wide(5) },     // top bench to the middle one, east
                new Ramp { Side = 3, L = 2, T0 = 12, T1 = EndDown(-18), W = Wide(5) },   // and west
                new Ramp { Side = 2, L = 1, T0 = 0, T1 = EndUp(30), W = Wide(5) },       // middle to the lowest, south
                new Ramp { Side = 3, L = 0, T0 = 15, T1 = EndDown(-15), W = Wide(5) },   // lowest bench to the floor, west
                new Ramp { Side = 1, L = 0, T0 = -15, T1 = EndUp(15), W = Wide(5) },     // and east
            };

            /// <summary>Spoil heaps: centre, radius, height (28.8 degree sides, a flat top), and the ridge in the east.</summary>
            private static readonly (double x, double z, double r, double h)[] HeapList =
            {
                (942, 322, 32, 15), (1180, 118, 38, 18), (925, 118, 30, 13),
            };
            private const double RidgeX = 1205, RidgeZ0 = 175, RidgeZ1 = 298, RidgeHalf = 26, RidgeH = 12, Tip = 0.55;

            /// <summary>A chest on top of each heap and on the ridge.</summary>
            public static readonly (double x, double z, double yaw)[] HeapTops =
            {
                (942, 322, Math.PI), (1180, 118, 0), (925, 118, Math.PI / 2), (1205, 190, -Math.PI / 2), (1205, 240, -Math.PI / 2), (1205, 285, -Math.PI / 2),
            };

            public static double Ground(double x, double z)
            {
                double d = Sdf(x, z);
                double s = BrGround.Shared(x, z);
                double outside = BrGround.Base + (s - BrGround.Base) * GroundShapes.Smoothstep((d - Lip(3) - RimFlat) / RimRoad) + Heap(x, z);
                if (d > Lip(3) + 30) return outside;
                double pit = Profile(d);
                foreach (Ramp r in Ramps) pit = Math.Max(pit, RampAt(r, x, z));
                return Math.Min(outside, pit);
            }

            /// <summary>Distance out from the floor's core (a rounded rectangle; negative inside the core).</summary>
            public static double Sdf(double x, double z)
            {
                double qx = Math.Abs(x - Cx) - Ax, qz = Math.Abs(z - Cz) - Az;
                double ox = Math.Max(qx, 0), oz = Math.Max(qz, 0);
                return Math.Sqrt(ox * ox + oz * oz) + Math.Min(Math.Max(qx, qz), 0);
            }

            /// <summary>The benches and faces by distance out (the top face keeps rising: the rim road cuts it off).</summary>
            public static double Profile(double d)
            {
                double h = Floor;
                for (int i = 0; i < 3; i++) h += Math.Max(0, Math.Min(Rise, (d - FaceFoot(i)) * K));
                return h + Math.Max(0, (d - FaceFoot(3)) * K);
            }

            /// <summary>Plane distance out from the core on a side, and the position along it.</summary>
            public static void SideFrame(int side, double x, double z, out double dp, out double t)
            {
                switch (side)
                {
                    case 0: dp = z - Cz - Az; t = x - Cx; break;
                    case 1: dp = x - Cx - Ax; t = z - Cz; break;
                    case 2: dp = Cz - z - Az; t = x - Cx; break;
                    default: dp = Cx - x - Ax; t = z - Cz; break;
                }
            }

            public static double RampAt(in Ramp r, double x, double z)
            {
                SideFrame(r.Side, x, z, out double dp, out double t);
                double dIn = FaceFoot(r.L) - r.W, dOut = Lip(r.L) + 2;
                double span = r.T1 - r.T0, u = (t - r.T0) / span;
                double lo = Level(r.L), hi = Level(r.L + 1);
                double h = lo + (hi - lo) * Math.Max(0, Math.Min(1, u));
                if (u > 1) h -= K * (u - 1) * Math.Abs(span);
                if (dp < dIn) h -= K * (dIn - dp);
                if (dp > dOut) h -= K * (dp - dOut);
                return h;
            }

            /// <summary>True if a berm on bench <paramref name="bench"/> of a side between t0 and t1 would sit on a ramp's top.</summary>
            public static bool RampNear(int side, int bench, double t0, double t1)
            {
                foreach (Ramp r in Ramps)
                {
                    if (r.Side != side || r.L + 1 != bench) continue;
                    double a = Math.Min(r.T0, r.T1) - 4, c = Math.Max(r.T0, r.T1) + 4;
                    if (t1 > a && t0 < c) return true;
                }
                return false;
            }

            /// <summary>The footprint of a berm piece d out from the core on a side, from t along it.</summary>
            public static Rect2 BermRect(int side, double d, double t, double len)
            {
                const double th = 0.3;
                switch (side)
                {
                    case 0: return new Rect2(Cx + t, Cz + Az + d, Cx + t + len, Cz + Az + d + th);
                    case 1: return new Rect2(Cx + Ax + d, Cz + t, Cx + Ax + d + th, Cz + t + len);
                    case 2: return new Rect2(Cx + t, Cz - Az - d - th, Cx + t + len, Cz - Az - d);
                    default: return new Rect2(Cx - Ax - d - th, Cz + t, Cx - Ax - d, Cz + t + len);
                }
            }

            public static double Heap(double x, double z)
            {
                double h = 0;
                foreach (var hp in HeapList)
                {
                    double r = Math.Sqrt((x - hp.x) * (x - hp.x) + (z - hp.z) * (z - hp.z));
                    h = Math.Max(h, Math.Max(0, Math.Min(hp.h, (hp.r - r) * Tip)));
                }
                double rz = Math.Max(RidgeZ0, Math.Min(RidgeZ1, z));
                double rr = Math.Sqrt((x - RidgeX) * (x - RidgeX) + (z - rz) * (z - rz));
                return Math.Max(h, Math.Max(0, Math.Min(RidgeH, (RidgeHalf - rr) * Tip)));
            }
        }
    }
}
