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
    /// The battle royale map, v3 (Ben 2026-11-28: double the area, hills and elevation, "fun to explore"): a 1,306 m
    /// square on smooth height-map ground (<see cref="BrGround"/>: a raised plateau in the middle, rolling fields and
    /// hills, a dry river bed down each edge midline with a terraced quarry on it, a rim nobody climbs). Every building
    /// stands on a level footing cut into the ground (<see cref="Footings"/>), so walls stay on the 1.5 m grid and walking
    /// up to a door never needs a jump.
    ///
    /// Eight spawn regions round the edge, two per side, each beside a corner; each is a two-storey building (4 x 9
    /// modules) with four pad rooms along its front, a stairwell hall in the middle and very little loot: one chest
    /// outside each pad room's doorway. Every pad has the same straight walk out of its doorway to its chest, the same
    /// distance to its big POI and the same ground between (the region approach is level with its POI and dips the same
    /// way on every pad's line). The four big POIs sit on the diagonals, each shared by the two regions either side of
    /// that corner: two halls mirrored across the diagonal, chests inside. On the plateau a town round a plaza. Between
    /// them, in every eighth: a farmhouse, an outpost, a lodge, a barn and a cottage, hills (two with a chest on top), low
    /// walls, the plains (<see cref="BrNature"/>), and villages built to reward breaching everywhere else.
    ///
    /// Fairness by construction: everything is designed once in one eighth of the map (east edge, north half) and
    /// stamped eight times by the eight symmetries of the square (four turns, four mirrors), so every region, pad, chest,
    /// footing and POI has an exact twin in every other eighth, and the ground is the same function in every eighth.
    /// Same breakable tiles, frame rule, doors and stairs as the 1v1 map (it uses the greybox builder).
    /// </summary>
    public static class BrMap
    {
        // 272 x 4.8 m = 1,305.6 m square (map v3, Ben 2026-11-28: double the area of the 921.6 m map; each player has
        // about 231 m x 231 m of map, a 23 s sprint across).
        public const int ChunksAcross = 272;

        // Layout in the design eighth, in metres from the map centre: u toward the east edge, v toward the north (0 <= v <= u).
        public const double RegionFront = 509.1, RegionMidV = BrGround.PoiV;
        public const double PoiAt = BrGround.PoiU;   // the shared POI centre sits on the diagonal (u = v)
        /// <summary>The play area's half size: the rim's bank starts here.</summary>
        public const double RimStart = BrGround.RimStart;
        /// <summary>Where the rim's rock face starts (nobody gets past it).</summary>
        public const double CliffStart = BrGround.RimStart + BrGround.RimBank;

        /// <summary>The ground grid: 1.5 m cells, 432 each way from the middle (1,296 m: the last 4.8 m beyond the cliff
        /// top at each edge has no ground, and nobody can reach it).</summary>
        public const double Cell = 1.5;
        public const int HalfCells = 432;
        /// <summary>The ground grid's sub-cell layers (props only: the height map is the ground): from 19.2 m down (under
        /// the quarry floor) to 43.2 m up, over the tallest canopy on the fort hill.</summary>
        public const int GroundLayers = 13, GroundMinY = -64;
        /// <summary>The walking grid looks for floors from here down.</summary>
        public const double NavTop = 40;

        public static double Size => ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize;

        /// <summary>
        /// The sub-cell grid that holds the plains (trees, boulders, bushes; <see cref="BrNature"/>), sitting on the
        /// height-map ground (<see cref="Field"/>): everything else in it is air. Plain air chunks cost nothing.
        /// </summary>
        public static SubGrid BuildGround()
        {
            var g = new SubGrid(ChunksAcross, GroundLayers, ChunksAcross, GroundMinY);
            BrNature.Stamp(g);
            return g;
        }

        private static readonly object FieldGate = new object();
        private static HeightField _field;
        private static Footings _footings;

        /// <summary>The ground (natural ground with every building's footing cut in): built once per run, never changes,
        /// shared by the server, every client and the bots.</summary>
        public static HeightField Field
        {
            get
            {
                lock (FieldGate)
                {
                    if (_field == null) BuildField();
                    return _field;
                }
            }
        }

        /// <summary>The footings cut into <see cref="Field"/> (the design eighth's; each stands for its seven twins).</summary>
        public static Footings FootingsUsed
        {
            get
            {
                lock (FieldGate)
                {
                    if (_field == null) BuildField();
                    return _footings;
                }
            }
        }

        private static void BuildField()
        {
            double c = Size / 2;
            Footings f = NewFootings();
            BuildEighth(new BrushWorld(), new GreyboxInfo(), c, new Eighth(), f);
            f.Freeze(BrGround.QuarryFloor - 1, BrGround.CliffTop + 20, c - 10, c - 10, c + HalfCells * Cell + 10, c + HalfCells * Cell + 10);
            _field = HeightField.Symmetric(c, c, Cell, HalfCells, (u, v) => f.Apply(BrGround.Eighth(u, v), c + u, c + v));
            _footings = f;
        }

        private static Footings NewFootings() => new Footings(Size / 2, Size / 2, Sym);

        /// <summary>Natural ground (before footings) at a map position.</summary>
        public static double Natural(double x, double z) => BrGround.At(x - Size / 2, z - Size / 2);

        /// <summary>The design eighth on its own (for placing the plains round it): its world, buildings, the spots that
        /// must stay clear (chests, items, pads, loot spots) and the spawn region's building.</summary>
        internal static BrushWorld BuildDesignEighth(out GreyboxInfo part, out List<Vec3d> keepClear, out Aabb region)
        {
            var src = new BrushWorld();
            part = new GreyboxInfo();
            var e = new Eighth();
            BuildEighth(src, part, Size / 2, e, NewFootings());
            keepClear = new List<Vec3d>();
            foreach (BrChestSpot ch in e.Chests) keepClear.Add(ch.Position);
            foreach (GroundItem g in e.Items) keepClear.Add(g.Position);
            keepClear.AddRange(e.Pads);
            keepClear.AddRange(e.Loot);
            region = e.Region;
            return src;
        }

        /// <summary>What movement and bullets use: the height-map ground, the props grid and the buildings.</summary>
        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) =>
            new CompositeTerrain(new CompositeTerrain(Field, ground), brushes) { RayIntoBFirst = true }; // most shots hit a building first: the props grid is then walked only that far

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
            BuildEighth(src, part, c, eighth, NewFootings());

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
            return BuildNav(w, info, cell);
        }

        /// <summary>The walking grid from a copy of the map the caller no longer needs (its doors are opened): the game
        /// passes the copy it drew the map from, so the village map isn't built a fourth time.</summary>
        public static BotNav BuildNav(BrushWorld w, BreachMapInfo info, double cell, SubGrid ground = null)
        {
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            w.Tiles.Changes.Clear();
            // Columns stop at the smooth ground; the top is over every roof and the rim's top. A column with nothing on the
            // ground (no building, wall or prop) starts just over the ground.
            SubGrid g = ground ?? BuildGround();
            return BotNav.Build(Terrain(g, w), 0, 0, info.SizeX, info.SizeZ, cell, topY: NavTop, floorY: BrGround.QuarryFloor - 2, columnTop: NavColumnTop(w, g));
        }

        /// <summary>
        /// Where the walking grid starts looking down a column: just over the ground where nothing stands on it (no
        /// building, wall or prop anywhere up the column), else from the top. The same spots either way (tested).
        /// </summary>
        public static Func<double, double, double> NavColumnTop(BrushWorld w, SubGrid g)
        {
            HeightField f = Field;
            return (x, z) =>
            {
                double h = f.HeightAt(x, z);
                if (double.IsInfinity(h)) return NavTop;
                var column = new Aabb(new Vec3d(x - 0.05, h - 0.5, z - 0.05), new Vec3d(x + 0.05, NavTop, z + 0.05));
                if (w.BoxOverlapsSolid(column) || g.BoxOverlapsSolid(column)) return NavTop;
                return h + 1.0;
            };
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

        // ---------------------------------------------------------------- footings

        /// <summary>The mean natural ground over a rectangle (3 m samples, edges included).</summary>
        private static double MeanNatural(double x0, double z0, double x1, double z1)
        {
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / 3)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / 3));
            double sum = 0;
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++) sum += Natural(x0 + (x1 - x0) * i / nx, z0 + (z1 - z0) * j / nz);
            return sum / ((nx + 1) * (nz + 1));
        }

        /// <summary>
        /// Cuts a level footing for a landmark (a building, a low wall, a hilltop chest) at the level its ground asks for
        /// (the mean, on the 0.3 m lattice) and returns that level. Landmarks are laid out so their footings never fight.
        /// </summary>
        private static double Foot(Footings f, double x0, double z0, double x1, double z1, double apron)
        {
            double l = f.PickLevel(x0, z0, x1, z1, apron, MeanNatural(x0, z0, x1, z1));
            if (double.IsNaN(l)) throw new InvalidOperationException($"No level footing fits at {x0:0.0}, {z0:0.0}.");
            f.Add(x0, z0, x1, z1, l, apron);
            return l;
        }

        /// <summary>Puts a building on its own footing (its outer walls plus an apron) and returns its level.</summary>
        private static double Foot(Footings f, GreyboxBlock.Building b, double apron = 3)
        {
            b.Y0 = Foot(f, b.X0, b.Z0, b.Cx(b.Mx) + GreyboxBlock.Column, b.Cz(b.Mz) + GreyboxBlock.Column, apron);
            return b.Y0;
        }

        /// <summary>A low wall standing on its own little footing.</summary>
        private static void LowWall(BrushWorld w, GreyboxInfo info, Footings f, double x, double z, double length, bool alongX, int rows)
        {
            double th = w.Tiles.Config.TileThickness;
            double y = Foot(f, x, z, alongX ? x + length : x + th, alongX ? z + th : z + length, 1.2);
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x, y, z), length, alongX, rows));
        }

        /// <summary>A chest on a small level spot (a hilltop).</summary>
        private static void SpotChest(Eighth e, Footings f, double x, double z, double yaw)
        {
            double y = Foot(f, x - 1.5, z - 1.5, x + 1.5, z + 1.5, 1.0);
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(x, y, z), Yaw = yaw, Region = -1 });
        }

        private static void BuildEighth(BrushWorld w, GreyboxInfo info, double c, Eighth e, Footings foot)
        {
            const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, F1 = GreyboxBlock.StoreyHeight;
            e.PoiCentre = new Vec3d(c + PoiAt, 0, c + PoiAt);

            // ---- Spawn region: 4 x 9 modules, front (x-line 0) faces the POI. Pad rooms 2 x 2 at z cells {0,1} {2,3}
            //      {5,6} {7,8} with an open doorway at segment 0, 2, 6 and 8; the middle lane (z cell 4) is the entrance
            //      hall and the stairwell. Each front room has a back room behind it; no room sees another. ----
            double span9 = 9 * M + Col;
            var rb = new GreyboxBlock.Building(c + RegionFront, c + RegionMidV - span9 / 2, 4, 9);
            // Its footing takes in the pad walks out to the chests in front (all four on the same level ground).
            rb.Y0 = Foot(foot, rb.X0 - 12, rb.Z0, rb.Cx(4) + Col, rb.Cz(9) + Col, 3);
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
            double front = rb.X0, ry = rb.Y0;
            double[] padZ = new double[4];
            for (int k = 0; k < 4; k++) padZ[k] = rb.Cz(padSeg[k]) + Col + 3.0;
            double outerDx = front + 4.0 - e.PoiCentre.X;
            double r2 = outerDx * outerDx + Sq(padZ[0] - e.PoiCentre.Z);
            double deepest = 0;
            for (int k = 0; k < 4; k++)
            {
                double dz = padZ[k] - e.PoiCentre.Z;
                double x = e.PoiCentre.X + Math.Sqrt(Math.Max(0, r2 - dz * dz));
                e.Pads[k] = new Vec3d(x, ry, padZ[k]);
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
            e.Squad[0] = new Vec3d(hx - 1.2, ry, hz - 1.2);
            e.Squad[1] = new Vec3d(hx - 1.2, ry, hz + 1.2);
            e.Squad[2] = new Vec3d(hx + 1.2, ry, hz - 1.2);
            e.Squad[3] = new Vec3d(hx + 1.2, ry, hz + 1.2);

            // ---- Big POI (half of it: its twin across the diagonal comes from the mirror). A 3 x 3 hall, stairwell in
            //      the west column, a first-floor room, chests on both floors. ----
            var p = new GreyboxBlock.Building(c + PoiAt + 1.9, c + PoiAt - 21.1, 3, 3);
            Foot(foot, p);
            e.PoiCentre = new Vec3d(c + PoiAt, p.Y0, c + PoiAt);
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
            e.Items.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(p.Cx(2) + Col + 3, p.Y0, p.Cz(1) + Col + 1) });

            // ---- The town on the plateau: one of eight 2 x 3 buildings round the plaza; the stairwell in the outer lane,
            //      the room column facing the plaza. ----
            var t = new GreyboxBlock.Building(c + 42, c + 3, 2, 3);
            Foot(foot, t);
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
            var t2 = new GreyboxBlock.Building(c + 69, c + 29, 2, 2, 1);
            Foot(foot, t2);
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
            var o = new GreyboxBlock.Building(c + 318, c + 64, 3, 3);
            Foot(foot, o);
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
            e.Items.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(o.Cx(1) + Col + 1.5, o.Y0, o.Cz(2) + Col + 3) });

            // ---- Barn near the edge, between the outpost and the spawn region: one big room, two chests. ----
            var b = new GreyboxBlock.Building(c + 424, c + 93, 2, 2, 1);
            Foot(foot, b);
            b.AlongX[0, 0, 0] = WallKind.WideDoor;  // south
            b.AlongZ[0, 0, 1] = WallKind.Door;      // west
            b.AlongZ[0, 2, 0] = WallKind.Window;    // east
            b.AlongX[0, 2, 1] = WallKind.ShutDoor;  // north
            GreyboxBlock.Raise(w, info, b, "Barn");
            Chest(e, b, 0, 1, 0, Math.PI / 2);
            Chest(e, b, 1, 0, 0, -Math.PI / 2);

            // ---- Farmhouse: a two-storey 3 x 3 building in the ring between the plateau and the big POIs, on the fort
            //      hill's lower slope. Stairwell in the east column, a room each floor. ----
            var fh = new GreyboxBlock.Building(c + 184, c + 104, 3, 3);
            Foot(foot, fh);
            GreyboxBlock.Stairwell(w, info, fh, 2, true, +1); // the east column, rising north
            fh.AlongZ[0, 0, 1] = WallKind.WideDoor;  // west, toward the town
            fh.AlongZ[0, 0, 0] = WallKind.Window;
            fh.AlongX[0, 0, 1] = WallKind.Door;      // south
            fh.AlongZ[0, 3, 0] = WallKind.ShutDoor;  // east, by the foot of the stairs
            fh.AlongZ[0, 3, 1] = WallKind.Window;
            fh.AlongX[0, 2, 0] = WallKind.Wall;      // a ground-floor room in the north-west corner
            fh.AlongZ[0, 1, 2] = WallKind.Door;
            fh.AlongZ[1, 2, 0] = WallKind.Wall;      // first floor: the stairwell's side wall, door where the stairs arrive
            fh.AlongZ[1, 2, 1] = WallKind.Wall;
            fh.AlongZ[1, 2, 2] = WallKind.Door;
            fh.AlongX[1, 1, 0] = WallKind.Wall;      // a room in the south-west corner
            fh.AlongX[1, 1, 1] = WallKind.Door;
            foreach (int j in new[] { 0, 1 }) fh.AlongZ[1, 0, j] = WallKind.Window;
            foreach (int i in new[] { 0, 1 }) fh.AlongX[1, 0, i] = WallKind.Window;
            fh.AlongX[1, 3, 0] = WallKind.Window;
            fh.AlongZ[1, 3, 2] = WallKind.Window;
            GreyboxBlock.Raise(w, info, fh, "Farmhouse");
            Chest(e, fh, 0, 2, 0, Math.PI / 2);
            Chest(e, fh, 0, 0, F1, Math.PI / 2);
            e.Items.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(fh.Cx(1) + Col + 3, fh.Y0, fh.Cz(1) + Col + 3) });

            // ---- Lodge: a long single-storey 2 x 3 building beside the river bed, on the way from the plateau out. ----
            var l = new GreyboxBlock.Building(c + 212, c + 40, 2, 3, 1);
            Foot(foot, l);
            l.AlongZ[0, 0, 1] = WallKind.Door;      // west, toward the town
            l.AlongZ[0, 0, 0] = WallKind.Window;
            l.AlongZ[0, 2, 1] = WallKind.ShutDoor;  // east
            l.AlongZ[0, 2, 2] = WallKind.Window;
            l.AlongX[0, 3, 0] = WallKind.Window;    // north
            l.AlongX[0, 1, 0] = WallKind.Wall;      // two rooms: a doorway between them
            l.AlongX[0, 1, 1] = WallKind.Door;
            GreyboxBlock.Raise(w, info, l, "Lodge");
            Chest(e, l, 0, 0, 0, Math.PI / 2);
            Chest(e, l, 1, 2, 0, -Math.PI / 2);

            // ---- Cottage: a small single-storey 2 x 2 house in the field between the barn and the spawn region. ----
            var ct = new GreyboxBlock.Building(c + 419, c + 167, 2, 2, 1);
            Foot(foot, ct);
            ct.AlongX[0, 0, 1] = WallKind.Door;     // south
            ct.AlongZ[0, 0, 0] = WallKind.Window;   // west
            ct.AlongZ[0, 2, 1] = WallKind.ShutDoor; // east
            ct.AlongX[0, 2, 0] = WallKind.Window;   // north
            GreyboxBlock.Raise(w, info, ct, "Cottage");
            Chest(e, ct, 0, 1, 0, Math.PI);

            // ---- Chests on two hilltops (the hills themselves are ground, BrGround). ----
            SpotChest(e, foot, c + 168, c + 62, Math.PI);
            SpotChest(e, foot, c + 430, c + 225, -Math.PI / 2);

            // ---- Low cover (one tile, vaultable) in the fields, each on its own little footing. ----
            LowWall(w, info, foot, c + 390, c + 127, 6, false, 1);
            LowWall(w, info, foot, c + 365, c + 212, 4.5, true, 1);
            LowWall(w, info, foot, c + 276, c + 42, 6, false, 1);
            LowWall(w, info, foot, c + 249, c + 187, 4.5, true, 1);
            LowWall(w, info, foot, c + 150, c + 46, 4.5, true, 1);
            LowWall(w, info, foot, c + 99, c + 8, 4.5, false, 1);
            LowWall(w, info, foot, c + 467, c + 141, 6, true, 1);
            // A two-tile wall in the corner field: cover, and it cuts the sight line out of the pad doorways across the
            // corner to the neighbouring region's pads (tested: no pad sees another).
            LowWall(w, info, foot, c + 462, c + 329, 8.4, false, 2);

            // ---- Villages (Ben 2026-11-28: "way more buildings designed to reward breaking holes in them", the open
            //      fields between buildings weren't safe to be in): the rest of the eighth is a grid of village blocks
            //      with narrow streets between them, so cover is never far, and every building is made so a hole pays:
            //      sealed loot rooms with no door, long sides with no door (a hole is the short way through), party
            //      walls between row houses (holes make a covered route along the row), compound walls too tall to
            //      climb with one gate. Each block and lot stands on one footing. ----
            BuildVillages(w, info, e, c, foot);
        }

        // ================================================================ Villages

        /// <summary>Village blocks: squares this wide, this far apart (the rest is street); then small lots in the gaps.</summary>
        public const double BlockSize = 30, BlockPitch = 40, LotSize = 14, LotPitch = 19;
        /// <summary>A village block or lot needs ground that varies less than this under it (it stands on one footing).</summary>
        public const double VillageRise = 4.2;

        /// <summary>
        /// Fills the eighth with village blocks on a grid, then small lots (a short row of houses, a shed, low walls) in
        /// the gaps left between the blocks, the landmarks and the eighth's edges. A block or lot goes wherever its
        /// square (and a street's width round it) is clear of everything already built (the landmarks, walls, other
        /// blocks, the spawn region and its pad walks, chests), stays inside the eighth so its twins never touch it, is
        /// inside the rim, and has ground gentle enough for one level footing that doesn't fight its neighbours' (not on
        /// the river bed, the quarry or the plateau's edge). What it holds comes from a fixed hash of its place on the grid.
        /// </summary>
        private static void BuildVillages(BrushWorld w, GreyboxInfo info, Eighth e, double c, Footings foot)
        {
            var keep = new List<Vec3d>();
            foreach (BrChestSpot ch in e.Chests) keep.Add(ch.Position);
            foreach (GroundItem g in e.Items) keep.Add(g.Position);
            double maxSlope = Math.Tan(24 * Math.PI / 180);
            bool Fits(double x, double z, double size, double street, double apron, out double level)
            {
                level = 0;
                double u0 = x - c, v0 = z - c;
                if (v0 < 2 || v0 + size > u0 - 2) return false;     // inside the eighth, with room: its twins never touch it
                if (u0 + size > RimStart - 6) return false;         // inside the rim
                var clear = new Aabb(new Vec3d(x - street, -40, z - street), new Vec3d(x + size + street, 80, z + size + street));
                if (w.BoxOverlapsSolid(clear)) return false;
                foreach (Aabb bd in info.Buildings)                  // nor in a building's yard (doors open onto it)
                    if (bd.Max.X > clear.Min.X && bd.Min.X < clear.Max.X && bd.Max.Z > clear.Min.Z && bd.Min.Z < clear.Max.Z) return false;
                Aabb r = e.Region;                                  // the spawn region, its pad walks and chests
                if (x + size > r.Min.X - 26 && x < r.Max.X + 8 && z + size > r.Min.Z - 8 && z < r.Max.Z + 8) return false;
                foreach (Vec3d k in keep) if (k.X > clear.Min.X && k.X < clear.Max.X && k.Z > clear.Min.Z && k.Z < clear.Max.Z) return false;
                // Not on the river bed or its banks, the quarry, or the plateau's edge (its rock faces and ramps).
                if (u0 + size > BrGround.RiverStart - 30 && u0 < BrGround.RiverEnd + 15 && v0 < 22) return false;
                if (GroundShapes.ToRect(BrGround.QuarryU, 0, u0, v0, u0 + size, v0 + size) < 62) return false;
                double rIn = GroundShapes.ToRect(0, 0, u0, v0, u0 + size, v0 + size), rOut = Math.Sqrt(Sq(u0 + size) + Sq(v0 + size));
                if (rIn < BrGround.PlateauR + 40 && rOut > BrGround.PlateauR - 6) return false;
                // Gentle enough ground for one footing.
                double x0 = x - apron, z0 = z - apron, span = size + 2 * apron;
                int n = (int)Math.Ceiling(span / 3);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity, sum = 0;
                var row = new double[n + 1];
                for (int j = 0; j <= n; j++)
                {
                    double prev = 0;
                    for (int i = 0; i <= n; i++)
                    {
                        double h = Natural(x0 + span * i / n, z0 + span * j / n);
                        lo = Math.Min(lo, h); hi = Math.Max(hi, h); sum += h;
                        if (i > 0 && Math.Abs(h - prev) > maxSlope * span / n) return false;
                        if (j > 0 && Math.Abs(h - row[i]) > maxSlope * span / n) return false;
                        prev = h; row[i] = h;
                    }
                    if (hi - lo > VillageRise) return false;
                }
                level = foot.PickLevel(x, z, x + size, z + size, apron, sum / Sq(n + 1));
                if (double.IsNaN(level) || level - lo > VillageRise * 0.75 || hi - level > VillageRise * 0.75) return false;
                foot.Add(x, z, x + size, z + size, level, apron);
                return true;
            }
            uint Hash(int i, int j, uint salt)
            {
                uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ salt * 83492791u;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return h;
            }
            for (int i = 0; 12 + i * BlockPitch < RimStart; i++)
                for (int j = 0; 3 + j * BlockPitch < RimStart; j++)
                {
                    double x = c + 12 + i * BlockPitch, z = c + 3 + j * BlockPitch;
                    if (!Fits(x, z, BlockSize, 4, 3, out double y)) continue;
                    uint h = Hash(i, j, 1);
                    Block(w, info, e, x, y, z, (int)(h % 6), (int)(h >> 8));
                }
            for (int i = 0; 4 + i * LotPitch < RimStart; i++)
                for (int j = 0; 2.5 + j * LotPitch < RimStart; j++)
                {
                    double x = c + 4 + i * LotPitch, z = c + 2.5 + j * LotPitch;
                    if (!Fits(x, z, LotSize, 3, 2.5, out double y)) continue;
                    uint h = Hash(i, j, 2);
                    Lot(w, info, e, x, y, z, (int)(h % 5), (int)(h >> 8));
                }
        }

        /// <summary>One small lot in a 14 m square at (x, z), on level ground at y.</summary>
        private static void Lot(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z, int kind, int h)
        {
            switch (kind)
            {
                case 0: Terrace(w, info, e, x + 0.5, y, z + 3.7, 2, true, h % 2); break;   // two rooms along x
                case 1: Terrace(w, info, e, x + 3.7, y, z + 0.5, 2, false, h % 2); break;  // two rooms along z
                case 2:                                                                      // a shed behind a low wall
                    Shed(w, info, e, x + 3.7, y, z + 6.5);
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 2, y, z + 1.5), 9, true, 1));
                    break;
                case 3:                                                                      // an L of two-tile walls
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 2, y, z + 2), 9, true, 2));
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 2, y, z + 2.3), 9, false, 2));
                    break;
                default: Shed(w, info, e, x + 3.7, y, z + 3.7); break;                       // a sealed shed
            }
        }

        /// <summary>One village block in a 30 m square at (x, z), on level ground at y.</summary>
        private static void Block(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z, int kind, int h)
        {
            switch (kind)
            {
                case 0: // two rows of houses back to back across a yard, a low wall in the yard
                    Terrace(w, info, e, x + 2, y, z + 1, 4, true, h % 4);
                    Terrace(w, info, e, x + 2, y, z + 22.4, 4, true, (h / 4 + 2) % 4);
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 9, y, z + 14.6), 6, true, 1));
                    break;
                case 1: // a warehouse
                    Warehouse(w, info, e, x + 2, y, z + 5);
                    break;
                case 2: // a house, a short row along z, a shed
                    House(w, info, e, x, y, z + 5);
                    Terrace(w, info, e, x + 18, y, z + 15, 2, false, h % 2);
                    Shed(w, info, e, x + 18, y, z + 2);
                    break;
                case 3: // a walled compound
                    Compound(w, info, e, x + 3, y, z + 3);
                    break;
                case 4: // a house and a row of three along z
                    House(w, info, e, x + 17, y, z + 5);
                    Terrace(w, info, e, x + 2, y, z + 3, 3, false, h % 3);
                    break;
                default: // a yard: a shed and low walls to fight from
                    Shed(w, info, e, x + 11.7, y, z + 11.7);
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 2, y, z + 4), 7.5, true, 1));
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 20, y, z + 26), 7.5, true, 1));
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 4, y, z + 16), 6, false, 2));
                    info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + 25, y, z + 8), 6, false, 2));
                    break;
            }
        }

        /// <summary>
        /// A row of one-room houses, one storey, one module deep: each room has its own shut door on the front (or on
        /// the east side for a row along z) except room <paramref name="sealedRoom"/>, which has no way in at all and two
        /// chests: break in through the next room's party wall or from outside. The long back has no doors.
        /// </summary>
        private static void Terrace(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z, int rooms, bool alongX, int sealedRoom)
        {
            var b = alongX ? new GreyboxBlock.Building(x, z, rooms, 1, 1) : new GreyboxBlock.Building(x, z, 1, rooms, 1);
            b.Y0 = y;
            for (int k = 0; k < rooms; k++)
            {
                if (k > 0)
                {
                    if (alongX) b.AlongZ[0, k, 0] = WallKind.Wall; else b.AlongX[0, k, 0] = WallKind.Wall; // party walls
                }
                if (k == sealedRoom) continue;
                if (alongX) b.AlongX[0, 0, k] = WallKind.ShutDoor; else b.AlongZ[0, 1, k] = WallKind.ShutDoor;
                if (alongX && k % 2 == 0) b.AlongX[0, 1, k] = WallKind.Window; // a few back windows
            }
            GreyboxBlock.Raise(w, info, b, "Row houses");
            int si = alongX ? sealedRoom : 0, sj = alongX ? 0 : sealedRoom;
            const double Col = GreyboxBlock.Column;
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(b.Cx(si) + Col + 1.6, y, b.Cz(sj) + Col + 3), Yaw = Math.PI / 2, Region = -1 });
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(b.Cx(si) + Col + 4.4, y, b.Cz(sj) + Col + 3), Yaw = -Math.PI / 2, Region = -1 });
        }

        /// <summary>
        /// A one-storey warehouse, 4 x 3 modules: a wide door and a shut door at its ends only (the long sides have only
        /// two windows: a hole is the short way through). The back row is four storerooms; only the first opens onto
        /// the hall, the other three are sealed with a chest each.
        /// </summary>
        private static void Warehouse(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z)
        {
            var b = new GreyboxBlock.Building(x, z, 4, 3, 1) { Y0 = y };
            b.AlongZ[0, 0, 0] = WallKind.WideDoor;  // west end
            b.AlongZ[0, 4, 1] = WallKind.ShutDoor;  // east end
            for (int i = 0; i < 4; i++) b.AlongX[0, 2, i] = i == 0 ? WallKind.Door : WallKind.Wall; // the storerooms' front
            for (int i = 1; i < 4; i++) b.AlongZ[0, i, 2] = WallKind.Wall;                           // between storerooms
            b.AlongX[0, 0, 1] = WallKind.Window;    // two windows on the south side
            b.AlongX[0, 0, 2] = WallKind.Window;
            GreyboxBlock.Raise(w, info, b, "Warehouse");
            Chest(e, b, 2, 0, 0, 0);                // in the hall
            for (int i = 1; i < 4; i++) Chest(e, b, i, 2, 0, Math.PI);
        }

        /// <summary>A two-storey house, 2 x 3 modules: stairs in the east lane, a room on each floor, a chest on each.</summary>
        private static void House(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z)
        {
            var t = new GreyboxBlock.Building(x, z, 2, 3) { Y0 = y };
            GreyboxBlock.Stairwell(w, info, t, 1, true, +1);
            t.AlongZ[0, 0, 1] = WallKind.ShutDoor;  // west door
            t.AlongZ[0, 0, 2] = WallKind.Window;
            t.AlongX[0, 3, 0] = WallKind.Window;    // north
            t.AlongZ[0, 1, 0] = WallKind.Wall;      // room column | stair lane, open in the middle
            t.AlongZ[0, 1, 2] = WallKind.Wall;
            t.AlongZ[1, 1, 0] = WallKind.Wall;
            t.AlongZ[1, 1, 1] = WallKind.Wall;
            t.AlongZ[1, 1, 2] = WallKind.Door;      // where the stairs arrive
            t.AlongZ[1, 0, 0] = WallKind.Window;
            t.AlongZ[1, 0, 2] = WallKind.Window;
            t.AlongX[1, 0, 0] = WallKind.Window;
            GreyboxBlock.Raise(w, info, t, "House");
            Chest(e, t, 0, 2, 0, Math.PI / 2);
            Chest(e, t, 0, 0, GreyboxBlock.StoreyHeight, Math.PI / 2);
        }

        /// <summary>A sealed one-module shed: no door, no window, a chest inside. Pure breach loot.</summary>
        private static void Shed(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z)
        {
            var b = new GreyboxBlock.Building(x, z, 1, 1, 1) { Y0 = y };
            GreyboxBlock.Raise(w, info, b, "Shed");
            Chest(e, b, 0, 0, 0, 0);
        }

        /// <summary>
        /// A walled yard, 22.5 m square: walls three tiles (4.5 m) tall, too tall to climb, one 3 m gate on the south
        /// side; a one-room hut inside with a chest, another chest in the yard. Break the wall to get in anywhere else.
        /// </summary>
        private static void Compound(BrushWorld w, GreyboxInfo info, Eighth e, double x, double y, double z)
        {
            const double size = 22.5, gate = 3.0, th = 0.3;
            double half = (size - gate) / 2;
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x, y, z), half, true, 3));                          // south, west of the gate
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + half + gate, y, z), half, true, 3));             // south, east of the gate
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x, y, z + size - th), size, true, 3));               // north
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x, y, z + th), size - 2 * th, false, 3));            // west
            info.Tiles.AddRange(GreyboxBlock.StripWall(w.Tiles, new Vec3d(x + size - th, y, z + th), size - 2 * th, false, 3)); // east
            var hut = new GreyboxBlock.Building(x + 12, z + 12, 1, 1, 1) { Y0 = y };
            hut.AlongX[0, 0, 0] = WallKind.ShutDoor;
            GreyboxBlock.Raise(w, info, hut, "Compound");
            Chest(e, hut, 0, 0, 0, 0);
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(x + 5, y, z + 6), Yaw = 0, Region = -1 });
        }

        /// <summary>A chest in the middle of a building cell (i, j) at floor height y over the building's ground floor.</summary>
        private static void Chest(Eighth e, GreyboxBlock.Building b, int i, int j, double y, double yaw)
        {
            const double Col = GreyboxBlock.Column;
            e.Chests.Add(new BrChestSpot { Position = new Vec3d(b.Cx(i) + Col + 3, b.Y0 + y, b.Cz(j) + Col + 3), Yaw = yaw, Region = -1 });
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
