using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>One spawn region of the battle royale map: a largish building with little loot (Ben 2026-11-26).</summary>
    public sealed class BrRegion
    {
        public int Index;
        public string Name = "";
        public Aabb Building;
        /// <summary>The four solo pads (feet), spread through the building, each in its own room. Duos use 0 and 3.</summary>
        public readonly Vec3d[] Pads = new Vec3d[4];
        public readonly double[] PadYaw = new double[4];
        /// <summary>Each pad's small loot spot (a chest), the same walk from every pad.</summary>
        public readonly Vec3d[] LootSpots = new Vec3d[4];
        /// <summary>Squads: four spots together in the middle of the building (the entrance hall).</summary>
        public readonly Vec3d[] SquadSpots = new Vec3d[4];
        public double SquadYaw;
        /// <summary>The big POI this region shares with its neighbour across the corner (index into <see cref="BrMapInfo.Pois"/>).</summary>
        public int Poi;
        /// <summary>The other region that shares it.</summary>
        public int Partner;
    }

    public sealed class BrPoi
    {
        public string Name = "";
        /// <summary>The point every pad of both regions is the same distance from (feet height 0).</summary>
        public Vec3d Centre;
        public readonly List<Aabb> Buildings = new List<Aabb>();
    }

    /// <summary>A chest on the battle royale map at the start of a match.</summary>
    public struct BrChestSpot
    {
        public Vec3d Position;
        /// <summary>Which way the chest faces (its loot spills that way).</summary>
        public double Yaw;
        /// <summary>Region index for pad loot spots, -1 elsewhere.</summary>
        public int Region;
    }

    public sealed class BrMapInfo
    {
        public double Size;
        public Vec3d Centre;
        public readonly List<BrRegion> Regions = new List<BrRegion>();
        public readonly List<BrPoi> Pois = new List<BrPoi>();
        /// <summary>The town in the middle (eight buildings round a plaza).</summary>
        public readonly List<Aabb> Town = new List<Aabb>();
        public readonly List<BrChestSpot> Chests = new List<BrChestSpot>();
        /// <summary>Distance from every pad to its region's shared POI centre (all equal, tested).</summary>
        public double PadToPoi;
        /// <summary>Walk from every pad to its loot spot (all equal: a straight line out of its doorway).</summary>
        public double PadToLoot;
        /// <summary>The safe play area's half size (the rim berms start here).</summary>
        public double PlayHalf;
    }

    /// <summary>
    /// The battle royale map (Ben 2026-11-26), a 614 m square: twice as wide each way as the first playable, so four
    /// times the area. Eight spawn regions round the edge, two per side; each is a two-storey building (4 x 9 modules)
    /// with four pad rooms along its front, a stairwell hall in the middle and very little loot: one chest outside each
    /// pad room's doorway. Every pad has the same straight walk out of its doorway to its chest, and the same distance to
    /// its big POI. The four big POIs sit inward of the corners, each shared by the two regions either side of that
    /// corner: two halls mirrored across the diagonal, chests inside. In the middle a town of sixteen buildings round a
    /// plaza. Between them, in every eighth: an outpost (a two-storey 3 x 3 building with loot on both floors) on the
    /// way from the middle to the edge, a barn near the edge, two hills with a chest on top, and low walls for cover in
    /// the fields. A rim berm with a cliff round the edge so nobody walks off.
    ///
    /// Fairness by construction: everything is designed once in one eighth of the map (east edge, north half) and
    /// stamped eight times by the eight symmetries of the square (four turns, four mirrors), so every region, pad, chest
    /// and POI has an exact twin in every other eighth. Same breakable tiles, frame rule, doors and stairs as the 1v1
    /// map (it uses the greybox builder).
    /// </summary>
    public static class BrMap
    {
        public const int ChunksAcross = 128;  // 128 x 4.8 m = 614.4 m square (Ben 2026-11-26: twice as wide each way)

        // Layout in the design eighth, in metres from the map centre: u toward the east edge, v toward the north (0 <= v <= u).
        public const double RegionFront = 240, RegionMidV = 120;
        public const double PoiAt = 120.2;          // the shared POI centre sits on the diagonal (u = v)
        public const double RimStart = 271.2, RimTop = 4.5, CliffStart = 298.2, CliffTop = 9.0;

        public static double Size => ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize;

        public static SubGrid BuildGround()
        {
            var g = new SubGrid(ChunksAcross, 1, ChunksAcross, -16);
            g.FillBaseGround();
            return g;
        }

        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) => new CompositeTerrain(ground, brushes);

        /// <summary>The eight symmetries of the square as (u, v) -> (a u + b v, c u + d v). Regions 2k and 2k+1 share POI k.</summary>
        public static readonly int[][] Sym =
        {
            new[] { 1, 0, 0, 1 },    // 0 identity: east edge, north half
            new[] { 0, 1, 1, 0 },    // 1 diagonal mirror: north edge, east half (shares the NE POI with 0)
            new[] { 0, -1, 1, 0 },   // 2 quarter turn: north edge, west half
            new[] { -1, 0, 0, 1 },   // 3 mirror: west edge, north half (shares the NW POI with 2)
            new[] { -1, 0, 0, -1 },  // 4 half turn: west edge, south half
            new[] { 0, -1, -1, 0 },  // 5 anti-diagonal mirror: south edge, west half (SW POI)
            new[] { 0, 1, -1, 0 },   // 6 three-quarter turn: south edge, east half
            new[] { 1, 0, 0, -1 },   // 7 mirror: east edge, south half (SE POI)
        };

        private static readonly string[] RegionNames = { "East Works", "North Yard", "North Mill", "West Works", "West Yard", "South Mill", "South Works", "East Yard" };
        private static readonly string[] PoiNames = { "Northeast Halls", "Northwest Halls", "Southwest Halls", "Southeast Halls" };

        public static BrushWorld BuildBrushes(out BreachMapInfo info, out BrMapInfo br, BreachConfig breach = null)
        {
            double size = Size, c = size / 2;
            var w = new BrushWorld(breach);
            info = new BreachMapInfo { SizeX = size, SizeZ = size, Block = new GreyboxInfo(), SpawnYaw = new double[] { 0, 0 } };
            br = new BrMapInfo { Size = size, Centre = new Vec3d(c, 0, c), PlayHalf = RimStart };

            // ---- The design eighth, built once into its own world in map coordinates. ----
            var src = new BrushWorld(breach);
            var part = new GreyboxInfo();
            var eighth = new Eighth();
            BuildEighth(src, part, c, eighth);

            // ---- Stamp it eight times. ----
            var seen = new HashSet<string>();
            for (int k = 0; k < 8; k++)
            {
                int[] m = Sym[k];
                Stamp(src, part, w, info.Block, m, c, seen);
                var r = new BrRegion { Index = k, Name = RegionNames[k], Poi = k / 2, Partner = k ^ 1, Building = Tr(eighth.Region, m, c) };
                for (int i = 0; i < 4; i++)
                {
                    r.Pads[i] = Tr(eighth.Pads[i], m, c);
                    r.PadYaw[i] = TrYaw(eighth.PadYaw, m);
                    r.LootSpots[i] = Tr(eighth.Loot[i], m, c);
                    r.SquadSpots[i] = Tr(eighth.Squad[i], m, c);
                }
                r.SquadYaw = TrYaw(eighth.PadYaw, m);
                br.Regions.Add(r);
                foreach (BrChestSpot ch in eighth.Chests)
                    br.Chests.Add(new BrChestSpot { Position = Tr(ch.Position, m, c), Yaw = TrYaw(ch.Yaw, m), Region = ch.Region >= 0 ? k : -1 });
                if (k % 2 == 0)
                {
                    var poi = new BrPoi { Name = PoiNames[k / 2], Centre = Tr(eighth.PoiCentre, m, c) };
                    poi.Buildings.Add(Tr(eighth.Poi, m, c));
                    poi.Buildings.Add(Tr(eighth.Poi, Mul(m, Sym[1]), c)); // its twin across the diagonal
                    br.Pois.Add(poi);
                }
                br.Town.Add(Tr(eighth.Town, m, c));
                br.Town.Add(Tr(eighth.Town2, m, c));
                foreach (GroundItem g in eighth.Items)
                {
                    GroundItem t = g;
                    t.Position = Tr(g.Position, m, c);
                    info.ItemSpawns.Add(t);
                }
            }
            br.PadToPoi = (Flat(br.Regions[0].Pads[0]) - Flat(br.Pois[0].Centre)).Length;
            br.PadToLoot = (br.Regions[0].Pads[0] - br.Regions[0].LootSpots[0]).HorizontalLength;
            foreach (BrRegion r in br.Regions)
                for (int i = 0; i < 4; i++) info.TeamSpawns[0].Add(r.Pads[i]);
            info.TeamSpawns[1].AddRange(info.TeamSpawns[0]);
            w.Tiles.Changes.Clear(); // the build itself is not news
            return w;
        }

        private static Vec3d Flat(Vec3d p) => new Vec3d(p.X, 0, p.Z);

        /// <summary>Bot walking grid for the whole map, doors open (bots open them or punch them down).</summary>
        public static BotNav BuildNav(double cell = 0.75)
        {
            BrushWorld w = BuildBrushes(out BreachMapInfo info, out _);
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            return BotNav.Build(Terrain(BuildGround(), w), 0, 0, info.SizeX, info.SizeZ, cell, groundTop: 0); // flat base ground at y 0
        }

        // ================================================================ The design eighth

        private sealed class Eighth
        {
            public Aabb Region, Poi, Town, Town2;
            public readonly Vec3d[] Pads = new Vec3d[4], Loot = new Vec3d[4], Squad = new Vec3d[4];
            public double PadYaw;
            public Vec3d PoiCentre;
            public readonly List<BrChestSpot> Chests = new List<BrChestSpot>();
            public readonly List<GroundItem> Items = new List<GroundItem>();
        }

        private static void BuildEighth(BrushWorld w, GreyboxInfo info, double c, Eighth e)
        {
            const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, F1 = GreyboxBlock.StoreyHeight;
            e.PoiCentre = new Vec3d(c + PoiAt, 0, c + PoiAt);

            // ---- Spawn region: 4 x 9 modules, front (x-line 0) faces the POI. Pad rooms 2 x 2 at z cells {0,1} {2,3}
            //      {5,6} {7,8} with an open doorway at segment 0, 2, 6 and 8; the middle lane (z cell 4) is the entrance
            //      hall and the stairwell. Each front room has a back room behind it; no room sees another. ----
            double span9 = 9 * M + Col;
            var rb = new GreyboxBlock.Building(c + RegionFront, c + RegionMidV - span9 / 2, 4, 9);
            GreyboxBlock.Stairwell(w, info, rb, 4, false, -1);
            int[] padSeg = { 0, 2, 6, 8 };
            for (int s = 0; s < 2; s++)
            {
                // Walls between the rooms (z-lines 2, 4, 5, 7) all the way back, and between front and back rooms.
                foreach (int line in new[] { 2, 4, 5, 7 })
                    for (int i = 0; i < 4; i++) rb.AlongX[s, line, i] = WallKind.Wall;
            }
            for (int j = 0; j < 9; j++)
            {
                if (j == 4) continue;
                rb.AlongZ[0, 2, j] = j == 1 || j == 3 || j == 5 || j == 7 ? WallKind.Door : WallKind.Wall; // front room <-> back room
                rb.AlongZ[0, 0, j] = Array.IndexOf(padSeg, j) >= 0 ? WallKind.Door : WallKind.Window;     // the front
            }
            rb.AlongZ[0, 0, 4] = WallKind.WideDoor;  // the main entrance into the hall
            rb.AlongZ[0, 4, 4] = WallKind.ShutDoor;  // back door out of the stair lane
            // First floor: two long wings either side of the stair lane, windows all round, doors from the landing.
            for (int j = 0; j < 9; j++)
            {
                rb.AlongZ[1, 0, j] = j == 4 ? WallKind.Wall : WallKind.Window;
                rb.AlongZ[1, 4, j] = j % 3 == 1 ? WallKind.Window : WallKind.Wall;
                if (j != 4) rb.AlongZ[1, 2, j] = WallKind.None;
            }
            foreach (int line in new[] { 2, 7 }) for (int i = 0; i < 4; i++) rb.AlongX[1, line, i] = WallKind.None;
            rb.AlongX[1, 4, 1] = WallKind.Door;
            rb.AlongX[1, 4, 3] = WallKind.Door;  // round the stair hole to where the upper flight starts
            rb.AlongX[1, 5, 1] = WallKind.Door;
            rb.AlongX[1, 0, 1] = WallKind.Window;
            rb.AlongX[1, 9, 1] = WallKind.Window;
            GreyboxBlock.Raise(w, info, rb, "Region");
            e.Region = rb.Bounds;

            // Pads: at the doorway's line, deep enough in the room that every pad is the same distance from the POI.
            // The outer two sit 4 m in; the inner two further back by exactly what evens the distance.
            double front = rb.X0;
            double[] padZ = new double[4];
            for (int k = 0; k < 4; k++) padZ[k] = rb.Cz(padSeg[k]) + Col + 3.0;
            double outerDx = front + 4.0 - e.PoiCentre.X;
            double r2 = outerDx * outerDx + Sq(padZ[0] - e.PoiCentre.Z);
            double deepest = 0;
            for (int k = 0; k < 4; k++)
            {
                double dz = padZ[k] - e.PoiCentre.Z;
                double x = e.PoiCentre.X + Math.Sqrt(Math.Max(0, r2 - dz * dz));
                e.Pads[k] = new Vec3d(x, 0, padZ[k]);
                deepest = Math.Max(deepest, x);
            }
            // Loot spot: straight out of the doorway, the same walk for every pad (the deepest one's chest 6 m outside).
            double walk = deepest - (front - 6.0);
            for (int k = 0; k < 4; k++)
            {
                e.Loot[k] = e.Pads[k] - new Vec3d(walk, 0, 0);
                e.Chests.Add(new BrChestSpot { Position = e.Loot[k], Yaw = Math.PI / 2, Region = 0 });
            }
            e.PadYaw = -Math.PI / 2; // facing the doorway (west in the design eighth)
            // Squads: together in the entrance hall (z cell 4, x cell 0).
            double hx = rb.Cx(0) + Col + 3.0, hz = rb.Cz(4) + Col + 3.0;
            e.Squad[0] = new Vec3d(hx - 1.2, 0, hz - 1.2);
            e.Squad[1] = new Vec3d(hx - 1.2, 0, hz + 1.2);
            e.Squad[2] = new Vec3d(hx + 1.2, 0, hz - 1.2);
            e.Squad[3] = new Vec3d(hx + 1.2, 0, hz + 1.2);

            // ---- Big POI (half of it: its twin across the diagonal comes from the mirror). A 3 x 3 hall, stairwell in
            //      the west column, a first-floor room, chests on both floors. ----
            var p = new GreyboxBlock.Building(c + PoiAt + 1.9, c + PoiAt - 21.1, 3, 3);
            GreyboxBlock.Stairwell(w, info, p, 0, true, +1);
            p.AlongX[0, 0, 1] = WallKind.WideDoor;  // south, toward the edge-middle hill
            p.AlongX[0, 0, 2] = WallKind.Window;
            p.AlongZ[0, 3, 1] = WallKind.ShutDoor;  // east, facing the spawn region
            p.AlongZ[0, 3, 0] = WallKind.Window;
            p.AlongZ[0, 3, 2] = WallKind.Window;
            p.AlongX[0, 3, 2] = WallKind.Door;      // north, into the corner yard (off the line through the south door,
                                                    // which would let pads on opposite edges see each other)
            p.AlongZ[0, 0, 1] = WallKind.Window;    // west, toward the middle
            p.AlongZ[1, 1, 0] = WallKind.Wall;      // first floor: the stairwell's side wall, door where the stairs arrive
            p.AlongZ[1, 1, 1] = WallKind.Wall;
            p.AlongZ[1, 1, 2] = WallKind.Door;
            p.AlongX[1, 1, 2] = WallKind.Door;      // a room in the south-east corner
            p.AlongZ[1, 2, 0] = WallKind.Door;
            foreach (int i in new[] { 1, 2 }) p.AlongX[1, 0, i] = WallKind.Window;
            p.AlongX[1, 3, 2] = WallKind.Window;
            p.AlongZ[1, 3, 1] = WallKind.Window;
            p.AlongZ[1, 3, 2] = WallKind.Window;
            GreyboxBlock.Raise(w, info, p, "Halls");
            e.Poi = p.Bounds;
            Chest(e, p, 1, 1, 0, 0);
            Chest(e, p, 2, 2, 0, Math.PI);
            Chest(e, p, 2, 0, F1, -Math.PI / 2);
            Chest(e, p, 1, 2, F1, Math.PI);
            e.Items.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(p.Cx(2) + Col + 3, 0, p.Cz(1) + Col + 1) });

            // ---- The town in the middle: one of eight 2 x 3 buildings round the plaza; the stairwell in the outer lane,
            //      the room column facing the plaza. ----
            var t = new GreyboxBlock.Building(c + 30, c + 2, 2, 3);
            GreyboxBlock.Stairwell(w, info, t, 1, true, +1);
            t.AlongZ[0, 0, 1] = WallKind.WideDoor;  // onto the plaza
            t.AlongZ[0, 0, 0] = WallKind.Window;
            t.AlongZ[0, 0, 2] = WallKind.Window;
            t.AlongZ[0, 2, 0] = WallKind.ShutDoor;  // out the back
            t.AlongX[0, 0, 0] = WallKind.Door;      // the alley ends
            t.AlongX[0, 3, 0] = WallKind.Window;
            t.AlongZ[0, 1, 0] = WallKind.Wall;      // room column | stair lane, open in the middle
            t.AlongZ[0, 1, 2] = WallKind.Wall;
            t.AlongZ[1, 1, 0] = WallKind.Wall;
            t.AlongZ[1, 1, 1] = WallKind.Wall;
            t.AlongZ[1, 1, 2] = WallKind.Door;      // where the stairs arrive
            for (int j = 0; j < 3; j++) t.AlongZ[1, 0, j] = WallKind.Window;
            t.AlongX[1, 0, 0] = WallKind.Window;
            GreyboxBlock.Raise(w, info, t, "Town");
            e.Town = t.Bounds;
            Chest(e, t, 0, 0, 0, Math.PI / 2);
            Chest(e, t, 0, 1, F1, Math.PI / 2);

            // A second, single-storey town block behind it (2 x 2, one open room): the town's outer ring.
            var t2 = new GreyboxBlock.Building(c + 50, c + 22, 2, 2, 1);
            t2.AlongZ[0, 0, 0] = WallKind.Door;      // toward the plaza
            t2.AlongZ[0, 0, 1] = WallKind.Window;
            t2.AlongX[0, 0, 1] = WallKind.Window;
            t2.AlongX[0, 2, 0] = WallKind.Door;      // the far side
            t2.AlongZ[0, 2, 1] = WallKind.ShutDoor;
            GreyboxBlock.Raise(w, info, t2, "Town");
            e.Town2 = t2.Bounds;
            Chest(e, t2, 1, 1, 0, -Math.PI / 2);

            // ---- Outpost: a two-storey 3 x 3 building on the way from the middle to the edge (inside the circle for
            //      longer than the regions, so a mid-map fight). Stairwell in the west column, rooms upstairs. ----
            var o = new GreyboxBlock.Building(c + 150, c + 30, 3, 3);
            GreyboxBlock.Stairwell(w, info, o, 0, true, +1);
            o.AlongZ[0, 0, 1] = WallKind.WideDoor;  // west, toward the middle
            o.AlongZ[0, 0, 0] = WallKind.Window;
            o.AlongX[0, 0, 2] = WallKind.Door;      // south
            o.AlongZ[0, 3, 1] = WallKind.Door;      // east, toward the edge
            o.AlongZ[0, 3, 2] = WallKind.Window;
            o.AlongX[0, 3, 1] = WallKind.ShutDoor;  // north
            o.AlongZ[0, 2, 0] = WallKind.Wall;      // a ground-floor room in the south-east corner
            o.AlongX[0, 1, 2] = WallKind.Door;
            o.AlongZ[1, 1, 0] = WallKind.Wall;      // first floor: the stairwell's side wall, door where the stairs arrive
            o.AlongZ[1, 1, 1] = WallKind.Wall;
            o.AlongZ[1, 1, 2] = WallKind.Door;
            o.AlongX[1, 2, 1] = WallKind.Door;
            o.AlongX[1, 2, 2] = WallKind.Wall;
            foreach (int i in new[] { 1, 2 }) o.AlongX[1, 0, i] = WallKind.Window;
            o.AlongZ[1, 3, 0] = WallKind.Window;
            o.AlongZ[1, 3, 2] = WallKind.Window;
            GreyboxBlock.Raise(w, info, o, "Outpost");
            Chest(e, o, 1, 1, 0, 0);
            Chest(e, o, 2, 0, 0, Math.PI / 2);
            Chest(e, o, 2, 2, F1, Math.PI);
            e.Items.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(o.Cx(1) + Col + 1.5, 0, o.Cz(2) + Col + 3) });

            // ---- Barn near the edge, between the outpost and the spawn region: one big room, two chests. ----
            var b = new GreyboxBlock.Building(c + 196, c + 44, 2, 2, 1);
            b.AlongX[0, 0, 0] = WallKind.WideDoor;  // south
            b.AlongZ[0, 0, 1] = WallKind.Door;      // west
            b.AlongZ[0, 2, 0] = WallKind.Window;    // east
            b.AlongX[0, 2, 1] = WallKind.ShutDoor;  // north
            GreyboxBlock.Raise(w, info, b, "Barn");
            Chest(e, b, 0, 1, 0, Math.PI / 2);
            Chest(e, b, 1, 0, 0, -Math.PI / 2);

            // ---- Hills (exact wedges, unbreakable ground green). ----
            // Between the two regions on one edge: a 3 m berm across the edge's middle (it is its own mirror image).
            info.Hills.Add(w.AddRamp(new Vec3d(c + 188, 0, c - 13.5), new Vec3d(c + 236, 3, c - 4.5), Dir.North, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(c + 188, 0, c - 4.5), new Vec3d(c + 236, 3, c + 4.5), BrushKind.Ground));
            info.Hills.Add(w.AddRamp(new Vec3d(c + 188, 0, c + 4.5), new Vec3d(c + 236, 3, c + 13.5), Dir.South, BrushKind.Ground));
            // Between the town and the POIs: a hill up 3 m over 12 m, 6 m flat top, down over 12 m; a chest on top.
            info.Hills.Add(w.AddRamp(new Vec3d(c + 76, 0, c + 40), new Vec3d(c + 88, 3, c + 58), Dir.East, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(c + 88, 0, c + 40), new Vec3d(c + 94, 3, c + 58), BrushKind.Ground));
            info.Hills.Add(w.AddRamp(new Vec3d(c + 94, 0, c + 40), new Vec3d(c + 106, 3, c + 58), Dir.West, BrushKind.Ground));
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(c + 91, 3, c + 49), Yaw = Math.PI, Region = -1 });
            // In the corner field between the POI and the spawn region: a taller hill, up 4.5 m over 15 m, 6 m top.
            info.Hills.Add(w.AddRamp(new Vec3d(c + 160, 0, c + 96), new Vec3d(c + 175, 4.5, c + 118), Dir.East, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(c + 175, 0, c + 96), new Vec3d(c + 181, 4.5, c + 118), BrushKind.Ground));
            info.Hills.Add(w.AddRamp(new Vec3d(c + 181, 0, c + 96), new Vec3d(c + 196, 4.5, c + 118), Dir.West, BrushKind.Ground));
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(c + 178, 4.5, c + 107), Yaw = -Math.PI / 2, Region = -1 });
            // The rim: a berm up 4.5 m toward the edge, then a 9 m cliff (too tall to climb). Its corner piece is its
            // own diagonal mirror.
            info.Hills.Add(w.AddRamp(new Vec3d(c + RimStart, 0, c), new Vec3d(c + CliffStart, RimTop, c + RimStart), Dir.East, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(c + CliffStart, 0, c), new Vec3d(c * 2, CliffTop, c + RimStart), BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(c + RimStart, 0, c + RimStart), new Vec3d(c * 2, CliffTop, c * 2), BrushKind.Ground));

            // ---- Low cover (one tile, vaultable) in the fields. ----
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 184, 0, c + 60), 6, false, 1));
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 172, 0, c + 132), 4.5, true, 1));
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 130, 0, c + 60), 6, false, 1));
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 110, 0, c + 20), 4.5, true, 1));
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 70, 0, c + 6), 4.5, false, 1));
            // Two two-tile walls in the corner field: cover, and they cut the two sight lines out of the pad doorways
            // that nothing else blocks: straight across the map to the far edge's pads (along the pad row), and across
            // the corner to the neighbouring region's pads (tested: no pad sees another).
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 184, 0, c + 141), 6, false, 2));
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(c + 186, 0, c + 164), 6, true, 2));
        }

        /// <summary>A chest in the middle of a building cell (i, j) at floor height y.</summary>
        private static void Chest(Eighth e, GreyboxBlock.Building b, int i, int j, double y, double yaw)
        {
            const double Col = GreyboxBlock.Column;
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(b.Cx(i) + Col + 3, y, b.Cz(j) + Col + 3), Yaw = yaw, Region = -1 });
        }

        private static double Sq(double v) => v * v;

        // ================================================================ Stamping

        /// <summary>m2 after m1 (apply m1 first).</summary>
        private static int[] Mul(int[] m2, int[] m1) => new[]
        {
            m2[0] * m1[0] + m2[1] * m1[2], m2[0] * m1[1] + m2[1] * m1[3],
            m2[2] * m1[0] + m2[3] * m1[2], m2[2] * m1[1] + m2[3] * m1[3],
        };

        public static Vec3d Tr(Vec3d p, int[] m, double c)
        {
            double u = p.X - c, v = p.Z - c;
            return new Vec3d(c + m[0] * u + m[1] * v, p.Y, c + m[2] * u + m[3] * v);
        }

        public static Aabb Tr(Aabb b, int[] m, double c)
        {
            Vec3d a = Tr(b.Min, m, c), d = Tr(b.Max, m, c);
            return new Aabb(new Vec3d(Math.Min(a.X, d.X), b.Min.Y, Math.Min(a.Z, d.Z)), new Vec3d(Math.Max(a.X, d.X), b.Max.Y, Math.Max(a.Z, d.Z)));
        }

        /// <summary>A yaw (0 = +z, x = sin yaw) under the symmetry.</summary>
        public static double TrYaw(double yaw, int[] m)
        {
            double x = Math.Sin(yaw), z = Math.Cos(yaw);
            return Math.Atan2(m[0] * x + m[1] * z, m[2] * x + m[3] * z);
        }

        public static Dir TrDir(Dir d, int[] m)
        {
            int x = d == Dir.East ? 1 : d == Dir.West ? -1 : 0, z = d == Dir.North ? 1 : d == Dir.South ? -1 : 0;
            if (x == 0 && z == 0) return d;
            int nx = m[0] * x + m[1] * z, nz = m[2] * x + m[3] * z;
            return nx > 0 ? Dir.East : nx < 0 ? Dir.West : nz > 0 ? Dir.North : Dir.South;
        }

        /// <summary>A wall's facing axis (North = thin in Z, East = thin in X) under the symmetry.</summary>
        private static Dir TrFacing(Dir f, int[] m)
        {
            if (f != Dir.North && f != Dir.East) return f;
            bool swaps = m[0] == 0;
            return swaps ? (f == Dir.North ? Dir.East : Dir.North) : f;
        }

        /// <summary>
        /// Copies every tile (with its kind, facing, follower links and door pairing) and every static brush of
        /// <paramref name="src"/> into <paramref name="dst"/> under the symmetry. Static brushes that land exactly on one
        /// already there (a piece that is its own mirror image) are added once.
        /// </summary>
        private static void Stamp(BrushWorld src, GreyboxInfo part, BrushWorld dst, GreyboxInfo into, int[] m, double c, HashSet<string> seen)
        {
            TileWalls st = src.Tiles, dt = dst.Tiles;
            var tileMap = new int[st.Count];
            for (int i = 0; i < st.Count; i++)
            {
                WallTile t = st[i];
                Brush shape = st.ShapeOf(i);
                Aabb box = Tr(shape.Bounds, m, c);
                int id;
                if (t.Links != null)
                {
                    var links = new int[t.Links.Length];
                    for (int k = 0; k < links.Length; k++) links[k] = tileMap[t.Links[k]];
                    id = dt.AddFollower(box, links);
                }
                else if (shape.IsRamp) id = dt.AddWedgeTile(box, TrDir(shape.Rise, m), t.Kind);
                else id = dt.AddTile(box, TrFacing(t.Facing, m), t.Kind);
                tileMap[i] = id;
                int partner = st.DoorPartner(i);
                if (partner >= 0 && partner < i) dt.PairDoor(tileMap[partner], id);
            }
            var brushMap = new Dictionary<int, int>();
            for (int b = 0; b < src.Count; b++)
            {
                if (st.TileOfBrush(b) >= 0) continue;
                Brush br = src[b];
                Aabb box = Tr(br.Bounds, m, c);
                Dir rise = br.IsRamp ? TrDir(br.Rise, m) : br.Rise;
                string key = $"{(int)br.Kind}:{br.IsRamp}:{(int)rise}:{Math.Round(box.Min.X, 3)}:{Math.Round(box.Min.Y, 3)}:{Math.Round(box.Min.Z, 3)}:{Math.Round(box.Max.X, 3)}:{Math.Round(box.Max.Y, 3)}:{Math.Round(box.Max.Z, 3)}";
                if (!seen.Add(key)) continue;
                brushMap[b] = dst.Add(br.IsRamp ? Brush.Wedge(box, rise, br.Kind) : Brush.Box(box, br.Kind));
            }
            foreach (Aabb b in part.Buildings) into.Buildings.Add(Tr(b, m, c));
            into.Names.AddRange(part.Names);
            foreach (Brush cl in part.Climbs) into.Climbs.Add(cl.IsRamp ? Brush.Wedge(Tr(cl.Bounds, m, c), TrDir(cl.Rise, m), cl.Kind) : Brush.Box(Tr(cl.Bounds, m, c), cl.Kind));
            foreach (int[] ids in part.ClimbTiles)
            {
                var n = new int[ids.Length];
                for (int k = 0; k < ids.Length; k++) n[k] = tileMap[ids[k]];
                into.ClimbTiles.Add(n);
            }
            foreach (int id in part.Floors) into.Floors.Add(tileMap[id]);
            foreach (int id in part.Frame) into.Frame.Add(tileMap[id]);
            foreach (int id in part.Doors) into.Doors.Add(tileMap[id]);
            foreach (int id in part.Tiles) into.Tiles.Add(tileMap[id]);
            foreach (Aabb d in part.Doorways) into.Doorways.Add(Tr(d, m, c));
            foreach (Aabb d in part.Bridges) into.Bridges.Add(Tr(d, m, c));
            foreach (int h in part.Hills) if (brushMap.TryGetValue(h, out int nb)) into.Hills.Add(nb);
        }
    }
}
