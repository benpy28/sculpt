using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sculpt.Core
{
    /// <summary>One spawn region of the battle royale map: a largish building with little loot (Ben 2026-11-26).</summary>
    public sealed class BrRegion
    {
        public int Index;
        public string Name = "";
        /// <summary>The segment it stands in (<see cref="BrLayout"/>).</summary>
        public int Segment;
        public Aabb Building;
        /// <summary>The four solo pads (feet), spread through the building, each in its own room. Duos use 0 and 3.</summary>
        public readonly Vec3d[] Pads = new Vec3d[4];
        public readonly double[] PadYaw = new double[4];
        /// <summary>Each pad's small loot spot (a chest), the same walk from every pad.</summary>
        public readonly Vec3d[] LootSpots = new Vec3d[4];
        /// <summary>Squads: four spots together in the middle of the building (the entrance hall).</summary>
        public readonly Vec3d[] SquadSpots = new Vec3d[4];
        public double SquadYaw;
        /// <summary>The home anchor every pad is the same distance from (<see cref="BrLayout.Anchor"/>).</summary>
        public Vec3d Anchor;
        /// <summary>Its segment's home POI (index into <see cref="BrMapInfo.Pois"/>, -1 if the segment named none).</summary>
        public int Poi = -1;
    }

    public sealed class BrPoi
    {
        public string Name = "";
        /// <summary>The POI's middle (feet height).</summary>
        public Vec3d Centre;
        public int Segment = -1;
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
        public readonly List<BrChestSpot> Chests = new List<BrChestSpot>();
        /// <summary>Every building with its segment and look (for the graphics pass and the minimap).</summary>
        public readonly List<BrBuilding> Buildings = new List<BrBuilding>();
        /// <summary>Distance from every pad to its region's home anchor (all equal, tested).</summary>
        public double PadToPoi;
        /// <summary>Walk from every pad to its loot spot (all equal: a straight line out of its doorway).</summary>
        public double PadToLoot;
        /// <summary>The safe play area's half size (the rim's bank starts here).</summary>
        public double PlayHalf;
    }

    /// <summary>
    /// The battle royale map, v3 with segments (Ben 2026-11-28: map v3 was "super boring"; split into nine parts, each
    /// designed with maximum effort on its own). A 1,306 m square on smooth height-map ground, cut into a 3 x 3 grid of
    /// segments (<see cref="BrLayout"/>: the contract they build to). The framework here puts the map together:
    ///
    /// 1. the spawn regions (<see cref="BrSpawnKit"/>): one per outer segment, the same building stamped eight ways;
    /// 2. each segment's content (<see cref="BrSegment.Build"/>, in id order, each in its own file under BrSegments/);
    /// 3. the ground (<see cref="BrGround"/>: each segment's own ground, eased together at the seams, the home plazas and
    ///    approaches, the rim), with every footing cut in (<see cref="Footings"/>);
    /// 4. the plains (<see cref="BrNature"/>: props each segment asked for plus a scatter over its open ground).
    ///
    /// Every building stands on a level footing, so walls stay on the 1.5 m grid and walking up to a door never needs a
    /// jump. Same breakable tiles, frame rule, doors and stairs as the 1v1 map (it uses the greybox builder).
    /// </summary>
    public static class BrMap
    {
        // 272 x 4.8 m = 1,305.6 m square.
        public const int ChunksAcross = 272;

        /// <summary>The play area's half size: the rim's bank starts here.</summary>
        public const double RimStart = BrGround.RimStart;
        /// <summary>Where the rim's rock face starts (nobody gets past it).</summary>
        public const double CliffStart = BrGround.RimStart + BrGround.RimBank;

        /// <summary>The ground grid: 1.5 m cells, 432 each way from the middle (1,296 m: the last 4.8 m beyond the cliff
        /// top at each edge has no ground, and nobody can reach it).</summary>
        public const double Cell = 1.5;
        public const int HalfCells = 432;
        /// <summary>The ground grid's sub-cell layers (props only: the height map is the ground): from 28.8 m down to 52.8 m
        /// up, so a prop fits anywhere between <see cref="BrGround.Lowest"/> and <see cref="BrGround.Highest"/>.</summary>
        public const int GroundLayers = 17, GroundMinY = -96;
        /// <summary>The walking grid looks for floors from here down (over every roof: keep building tops under 85 m).</summary>
        public const double NavTop = 90;

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

        // ================================================================ The template build

        /// <summary>One build of the whole map with what each segment recorded (for the ground, the plains and tests).</summary>
        public sealed class TemplateBuild
        {
            public BrushWorld World;
            public BreachMapInfo Info;
            public BrMapInfo Br;
            public Footings Footings;
            public BrBuild[] Segments;
        }

        private static readonly object FieldGate = new object();
        private static TemplateBuild _template;
        private static HeightField _field;

        /// <summary>The map built once per run (never handed to play: the server and clients each build their own).</summary>
        public static TemplateBuild Template
        {
            get
            {
                lock (FieldGate)
                {
                    if (_template == null) _template = Build(null);
                    return _template;
                }
            }
        }

        /// <summary>The ground (natural ground with every building's footing cut in): built once per run, never changes,
        /// shared by the server, every client and the bots.</summary>
        public static HeightField Field
        {
            get
            {
                lock (FieldGate)
                {
                    if (_field == null) _field = BuildField(_template ??= Build(null));
                    return _field;
                }
            }
        }

        /// <summary>The footings cut into <see cref="Field"/>.</summary>
        public static Footings FootingsUsed => Template.Footings;

        private static HeightField BuildField(TemplateBuild t)
        {
            double c = Size / 2, o = c - HalfCells * Cell;
            int n = 2 * HalfCells;
            // The natural ground first (on every core), then the footings cut into it.
            var natural = new float[(n + 1) * (n + 1)];
            Parallel.For(0, n + 1, j =>
            {
                for (int i = 0; i <= n; i++) natural[i + (n + 1) * j] = (float)BrGround.Natural(o + i * Cell, o + j * Cell);
            });
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (float h in natural) { if (h < lo) lo = h; if (h > hi) hi = h; }
            Footings f = t.Footings;
            f.Freeze(lo - 1, hi + 1, o - 10, o - 10, o + n * Cell + 10, o + n * Cell + 10);
            var field = new HeightField(o, o, Cell, n, n);
            Parallel.For(0, n + 1, j =>
            {
                for (int i = 0; i <= n; i++) field.Set(i, j, f.Apply(natural[i + (n + 1) * j], o + i * Cell, o + j * Cell));
            });
            field.Finish();
            return field;
        }

        /// <summary>Natural ground (before footings) at a map position.</summary>
        public static double Natural(double x, double z) => BrGround.Natural(x, z);

        /// <summary>What movement and bullets use: the height-map ground, the props grid and the buildings.</summary>
        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) => new CompositeTerrain(new CompositeTerrain(Field, ground), brushes);

        /// <summary>The eight symmetries of the square as (u, v) -> (a u + b v, c u + d v).</summary>
        public static readonly int[][] Sym =
        {
            new[] { 1, 0, 0, 1 },    // 0 identity
            new[] { 0, 1, 1, 0 },    // 1 diagonal mirror
            new[] { 0, -1, 1, 0 },   // 2 quarter turn
            new[] { -1, 0, 0, 1 },   // 3 mirror across x
            new[] { -1, 0, 0, -1 },  // 4 half turn
            new[] { 0, -1, -1, 0 },  // 5 anti-diagonal mirror
            new[] { 0, 1, -1, 0 },   // 6 three-quarter turn
            new[] { 1, 0, 0, -1 },   // 7 mirror across z
        };

        /// <summary>The whole map, built fresh (each server and client builds its own copy).</summary>
        public static BrushWorld BuildBrushes(out BreachMapInfo info, out BrMapInfo br, BreachConfig breach = null)
        {
            TemplateBuild t = Build(breach);
            info = t.Info;
            br = t.Br;
            return t.World;
        }

        /// <summary>Builds the map: the spawn regions, then every segment in id order.</summary>
        public static TemplateBuild Build(BreachConfig breach)
        {
            double size = Size, c = size / 2;
            var w = new BrushWorld(breach);
            var info = new BreachMapInfo { SizeX = size, SizeZ = size, Block = new GreyboxInfo(), SpawnYaw = new double[] { 0, 0 } };
            var br = new BrMapInfo { Size = size, Centre = new Vec3d(c, 0, c), PlayHalf = RimStart };
            var foot = new Footings(c, c, null);
            var t = new TemplateBuild { World = w, Info = info, Br = br, Footings = foot, Segments = new BrBuild[BrLayout.Count] };
            for (int id = 0; id < BrLayout.Count; id++) t.Segments[id] = new BrBuild(id, w, info.Block, foot);

            // ---- 1. The spawn regions: two templates, stamped into the eight outer segments. ----
            var templates = new BrSpawnKit.Template[2];
            var srcs = new BrushWorld[2];
            var parts = new GreyboxInfo[2];
            for (int k = 0; k < 2; k++)
            {
                srcs[k] = new BrushWorld(breach);
                parts[k] = new GreyboxInfo();
                templates[k] = BrSpawnKit.Build(srcs[k], parts[k], k == 1);
            }
            var seen = new HashSet<string>();
            int index = 0;
            foreach (int id in BrLayout.RegionSegments)
            {
                int k = BrLayout.IsCorner(id) ? 1 : 0;
                int[] m = BrLayout.KitSymmetry(id);
                BrSpawnKit.Template tp = templates[k];
                Stamp(srcs[k], parts[k], w, info.Block, m, c, seen);
                var r = new BrRegion { Index = index++, Name = BrLayout.Names[id], Segment = id, Building = Tr(tp.Building, m, c), Anchor = BrLayout.Anchor(id) };
                for (int i = 0; i < 4; i++)
                {
                    r.Pads[i] = Tr(tp.Pads[i], m, c);
                    r.PadYaw[i] = TrYaw(tp.PadYaw, m);
                    r.LootSpots[i] = Tr(tp.Loot[i], m, c);
                    r.SquadSpots[i] = Tr(tp.Squad[i], m, c);
                    br.Chests.Add(new BrChestSpot { Position = r.LootSpots[i], Yaw = TrYaw(Math.PI / 2, m), Region = r.Index });
                }
                r.SquadYaw = TrYaw(tp.PadYaw, m);
                br.Regions.Add(r);
                br.Buildings.Add(new BrBuilding { Bounds = r.Building, Segment = id, Name = "Spawn region", Style = BrStyle.Concrete });
                // Its level ground, and what its segment keeps clear: the plot (with the footing's apron), the pad lanes.
                Rect2 plot = TrRect(tp.Plot, m, c);
                foot.Add(plot.X0, plot.Z0, plot.X1, plot.Z1, BrGround.Base, 3);
                BrBuild b = t.Segments[id];
                b.Reserved.Add(plot.Grow(3));
                for (int i = 0; i < 4; i++)
                {
                    BrSpawnKit.Lane(tp, i, out Vec3d from, out Vec3d to);
                    b.Lanes.Add((Tr(from, m, c), Tr(to, m, c), BrLayout.PadLaneWidth / 2));
                    b.KeepClearSpots.Add(r.Pads[i]);
                    b.KeepClearSpots.Add(r.LootSpots[i]);
                }
            }
            foreach (BrLayout.Port p in BrLayout.Ports)
            {
                t.Segments[p.A].Reserved.Add(p.LaneIn(p.A));
                t.Segments[p.B].Reserved.Add(p.LaneIn(p.B));
            }

            // ---- 2. The segments. ----
            for (int id = 0; id < BrLayout.Count; id++)
            {
                BrBuild b = t.Segments[id];
                b.TileFirst = w.Tiles.Count;
                b.BrushFirst = w.Count;
                b.FootingFirst = foot.Count;
                b.BuildingFirst = info.Block.Buildings.Count;
                BrSegments.All[id].Build(b);
                b.TileEnd = w.Tiles.Count;
                b.BrushEnd = w.Count;
                b.FootingEnd = foot.Count;
                b.BuildingEnd = info.Block.Buildings.Count;
                br.Chests.AddRange(b.Chests);
                foreach (GroundItem g in b.Items) info.ItemSpawns.Add(g);
                br.Pois.AddRange(b.Pois);
                br.Buildings.AddRange(b.Buildings);
                foreach (BrChestSpot ch in b.Chests) b.KeepClearSpots.Add(ch.Position);
                foreach (GroundItem g in b.Items) b.KeepClearSpots.Add(g.Position);
            }
            foreach (BrRegion r in br.Regions)
            {
                string want = BrSegments.All[r.Segment].HomePoiName;
                for (int k = 0; k < br.Pois.Count; k++)
                    if (br.Pois[k].Segment == r.Segment && br.Pois[k].Name == want) { r.Poi = k; break; }
            }

            br.PadToPoi = (Flat(br.Regions[0].Pads[0]) - Flat(br.Regions[0].Anchor)).Length;
            br.PadToLoot = (br.Regions[0].Pads[0] - br.Regions[0].LootSpots[0]).HorizontalLength;
            foreach (BrRegion r in br.Regions)
                for (int i = 0; i < 4; i++) info.TeamSpawns[0].Add(r.Pads[i]);
            info.TeamSpawns[1].AddRange(info.TeamSpawns[0]);
            w.Tiles.Changes.Clear(); // the build itself is not news
            return t;
        }

        private static Vec3d Flat(Vec3d p) => new Vec3d(p.X, 0, p.Z);

        /// <summary>Bot walking grid for the whole map, doors open (bots open them or punch them down).</summary>
        public static BotNav BuildNav(double cell = 0.75)
        {
            BrushWorld w = BuildBrushes(out BreachMapInfo info, out _);
            return BuildNav(w, info, cell);
        }

        /// <summary>The walking grid from a copy of the map the caller no longer needs (its doors are opened): the game
        /// passes the copy it drew the map from, so the map isn't built once more.</summary>
        public static BotNav BuildNav(BrushWorld w, BreachMapInfo info, double cell, SubGrid ground = null)
        {
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            w.Tiles.Changes.Clear();
            SubGrid g = ground ?? BuildGround();
            return BotNav.Build(Terrain(g, w), 0, 0, info.SizeX, info.SizeZ, cell, topY: NavTop, floorY: BrGround.Lowest - 2, columnTop: NavColumnTop(w, g));
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

        // ================================================================ Stamping

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

        public static Rect2 TrRect(Rect2 r, int[] m, double c)
        {
            Aabb b = Tr(new Aabb(new Vec3d(r.X0, 0, r.Z0), new Vec3d(r.X1, 0, r.Z1)), m, c);
            return Rect2.Of(b);
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
        internal static void Stamp(BrushWorld src, GreyboxInfo part, BrushWorld dst, GreyboxInfo into, int[] m, double c, HashSet<string> seen)
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
