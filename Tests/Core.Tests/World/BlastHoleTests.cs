using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Walls by area (Ben 2026-11-25: "remove the grid", cracks by area, "a shotgun pump should one shot any wall and AR
    /// should 2 tap", heals by area). Damage lands on an 8 x 8 cell grid per tile, never by tile; holes are never a
    /// pixel hole or a sliver, and their edges are cut on the diagonal. Hand-worked cases plus a seeded random fight that
    /// checks the fairness rules after every hit and heal.
    /// </summary>
    public class BlastHoleTests
    {
        private const int N = Brush.CellsPerSide;
        private const double Cell = 1.5 / N;

        /// <summary>A wall of w x h tiles running east from (0, 0, 10), thin in Z; tile (i, j) is ids[j * w + i].</summary>
        private static BrushWorld Wall(int w, int h, out int[] ids, BreachConfig c = null)
        {
            var world = new BrushWorld(c);
            ids = world.Tiles.AddWall(new Vec3d(0, 0, 10), Dir.East, w, h);
            world.Tiles.Changes.Clear();
            return world;
        }

        /// <summary>The wall face as one grid of cells: true = open (blown out or broken tile).</summary>
        private static bool[,] Grid(TileWalls tw, int w, int h, int[] ids)
        {
            var g = new bool[w * N, h * N];
            for (int tj = 0; tj < h; tj++)
                for (int ti = 0; ti < w; ti++)
                {
                    ulong open = tw.OpenCells(ids[tj * w + ti]);
                    for (int c = 0; c < N * N; c++)
                        g[ti * N + c % N, tj * N + c / N] = ((open >> c) & 1) != 0;
                }
            return g;
        }

        private static int OpenCount(bool[,] g)
        {
            int n = 0;
            foreach (bool b in g) if (b) n++;
            return n;
        }

        /// <summary>
        /// The fairness rules on a wall face (off the face counts as open air, like a doorway): every open cell is in a
        /// 2 x 2 open block, every standing cell is in a 2 x 2 standing block, and every opening has at least
        /// <paramref name="minCells"/> cells unless it reaches the edge of the wall.
        /// </summary>
        private static void AssertFair(bool[,] g, int minCells, string when)
        {
            int W = g.GetLength(0), H = g.GetLength(1);
            bool Open(int x, int y) => x < 0 || y < 0 || x >= W || y >= H || g[x, y];
            bool InBlock(int x, int y, bool open)
            {
                for (int dx = -1; dx <= 0; dx++)
                    for (int dy = -1; dy <= 0; dy++)
                    {
                        bool all = true;
                        for (int a = 0; a < 2 && all; a++)
                            for (int b = 0; b < 2 && all; b++)
                                if (Open(x + dx + a, y + dy + b) != open) all = false;
                        if (all) return true;
                    }
                return false;
            }
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    bool o = g[x, y];
                    if (!InBlock(x, y, o))
                        Assert.Fail($"{when}: {(o ? "open" : "standing")} cell ({x}, {y}) is part of a strip thinner than 2 cells\n{Dump(g)}");
                }
            var seen = new bool[W, H];
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    if (!g[x, y] || seen[x, y]) continue;
                    int size = 0;
                    bool edge = false;
                    var q = new Queue<(int, int)>();
                    q.Enqueue((x, y));
                    seen[x, y] = true;
                    while (q.Count > 0)
                    {
                        var (cx, cy) = q.Dequeue();
                        size++;
                        foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                        {
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H) { edge = true; continue; }
                            if (!g[nx, ny] || seen[nx, ny]) continue;
                            seen[nx, ny] = true;
                            q.Enqueue((nx, ny));
                        }
                    }
                    Assert.That(size >= minCells || edge, Is.True, $"{when}: an opening of only {size} cells (min {minCells})\n{Dump(g)}");
                }
        }

        private static string Dump(bool[,] g)
        {
            var sb = new System.Text.StringBuilder();
            for (int y = g.GetLength(1) - 1; y >= 0; y--)
            {
                for (int x = 0; x < g.GetLength(0); x++) sb.Append(g[x, y] ? '.' : '#').Append(x % 8 == 7 ? "|" : "");
                sb.Append('\n');
                if (y % 8 == 0) sb.Append(new string('-', g.GetLength(0) * 9 / 8)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>What collides at every cell centre is exactly what the cells say.</summary>
        private static void AssertCollisionMatches(BrushWorld world, TileWalls tw, int[] ids)
        {
            foreach (int t in ids)
            {
                ulong open = tw.OpenCells(t);
                for (int c = 0; c < N * N; c++)
                {
                    Vec3d p = tw.CellBox(t, c).Center;
                    bool solid = world.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.01, 0.01, 0.01), p + new Vec3d(0.01, 0.01, 0.01)));
                    Assert.That(solid, Is.EqualTo(((open >> c) & 1) == 0), $"tile {t} cell {c}");
                }
            }
        }

        /// <summary>
        /// What collides everywhere on the face is the drawn shape (quarter cells, edges cut on the diagonal) to within
        /// 2.5 cm, sampled on a fine lattice.
        /// </summary>
        private static void AssertCollisionIsTheDrawnShape(BrushWorld world, TileWalls tw, int[] ids)
        {
            const int Q = TileWalls.Quads;
            double q = 1.5 / Q;
            foreach (int t in ids)
            {
                Aabb b = tw.BoundsOf(t);
                for (int sx = 0; sx < Q * 4; sx++)
                    for (int sy = 0; sy < Q * 4; sy++)
                    {
                        double x = (sx + 0.37) / 4.0, y = (sy + 0.61) / 4.0; // in quarter cells, never on a seam
                        int qi = (int)x, qj = (int)y;
                        QuadFill f = tw.QuadOf(t, qi, qj);
                        bool drawn = TileWalls.SolidAt(f, qi, qj, x, y);
                        var p = new Vec3d(b.Min.X + x * q, b.Min.Y + y * q, 10.15);
                        bool hits = world.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.0005, 0.0005, 0.0005), p + new Vec3d(0.0005, 0.0005, 0.0005)));
                        if (drawn == hits) continue;
                        Assert.That(f == QuadFill.Inner || f == QuadFill.Outer, Is.True, $"tile {t} quad ({qi}, {qj}) {f}: only a diagonal edge may differ");
                        double u = (qi & 1) == 1 ? x - qi : qi + 1 - x, v = (qj & 1) == 1 ? y - qj : qj + 1 - y;
                        Assert.That(Math.Abs(u + v - 1) * q, Is.LessThanOrEqualTo(0.025), $"tile {t} quad ({qi}, {qj}): {Math.Abs(u + v - 1) * q:F3} m off the drawn edge");
                    }
            }
        }

        private static Vec3d Face(double x, double y) => new Vec3d(x, y, 10);

        private static int Tile(int[] ids, int w, double x, double y) => ids[(int)(y / 1.5) * w + (int)(x / 1.5)];

        private static BulletHit Pellet(TileWalls tw, int[] ids, int w, double x, double y) =>
            new BulletHit { Kind = HitKind.Terrain, Brush = tw[Tile(ids, w, x, y)].Brush + 1, Point = Face(x, y) };

        private static List<BulletHit> Ar(TileWalls tw, int[] ids, int w, double x, double y) => new List<BulletHit> { Pellet(tw, ids, w, x, y) };

        /// <summary>A point-blank pump shot: nine pellets in a tight ring round (x, y).</summary>
        private static List<BulletHit> Pump(TileWalls tw, int[] ids, int w, double x, double y, double spread = 0.06)
        {
            var list = new List<BulletHit> { Pellet(tw, ids, w, x, y) };
            int h = ids.Length / w;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                double px = x + Math.Cos(a) * spread, py = y + Math.Sin(a) * spread;
                if (px <= 0 || py <= 0 || px >= w * 1.5 || py >= h * 1.5) continue; // missed the wall
                list.Add(Pellet(tw, ids, w, px, py));
            }
            return list;
        }

        // ------------------------------------------------------------------ hand-worked cases

        [Test]
        public void Ar_FirstShotCracksAround_SecondOnTheSameSpotOpensAHole()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            int mid = ids[4];
            tw.ApplyShot(Ar(tw, ids, 3, 2.25, 2.25), Weapon.Ar, 1);
            Assert.That(OpenCount(Grid(tw, 3, 3, ids)), Is.EqualTo(0), "one AR bullet only cracks");
            int cell = tw.CellAt(mid, Face(2.25, 2.25));
            Assert.That(tw.CellHealth(mid, cell), Is.EqualTo(tw.Config.IntactHealth - tw.Config.ArTileDamage), "the cells round the hit took the damage");
            Assert.That(tw.CellHealth(mid, 0), Is.EqualTo(tw.Config.IntactHealth), "a corner 1 m away took none");
            tw.ApplyShot(Ar(tw, ids, 3, 2.25, 2.25), Weapon.Ar, 1);
            bool[,] g = Grid(tw, 3, 3, ids);
            double area = OpenCount(g) * Cell * Cell;
            Assert.That(area, Is.GreaterThanOrEqualTo(0.63), "a hole of at least half a body");
            Assert.That(area, Is.LessThan(1.2), "an AR hole stays modest");
            AssertFair(g, tw.MinHoleCells(mid), "after two AR bullets");
            AssertCollisionMatches(world, tw, ids);
            AssertCollisionIsTheDrawnShape(world, tw, ids);
        }

        [Test]
        public void Ar_TwoTaps_OnATileSeam_StillOpenAHole_AcrossBothTiles()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            // Right on the border between tiles (1, 1) and (2, 1): the tile grid must not matter.
            tw.ApplyShot(Ar(tw, ids, 3, 3.0, 2.2), Weapon.Ar, 1);
            tw.ApplyShot(Ar(tw, ids, 3, 3.02, 2.25), Weapon.Ar, 1);
            Assert.That(tw.OpenCells(ids[4]), Is.Not.EqualTo(0ul), "the left tile opened");
            Assert.That(tw.OpenCells(ids[5]), Is.Not.EqualTo(0ul), "and the right one: one hole across the seam");
            AssertFair(Grid(tw, 3, 3, ids), tw.MinHoleCells(ids[4]), "seam hole");
        }

        [Test]
        public void Ar_TwoShotsFarApart_OnlyCrack()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Ar(tw, ids, 3, 1.0, 1.0), Weapon.Ar, 1);
            tw.ApplyShot(Ar(tw, ids, 3, 3.2, 3.2), Weapon.Ar, 1);
            Assert.That(OpenCount(Grid(tw, 3, 3, ids)), Is.EqualTo(0), "damage is where you hit: two different spots are two cracks");
        }

        [Test]
        public void Punch_FirstOneShowsDamage_SecondOpens()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            int mid = ids[4];
            tw.Damage(mid, tw.Config.PunchTileDamage, Face(2.25, 2.25), TileCause.Punch, 1, tw.Config.PunchHitRadius);
            Assert.That(tw.Changes.Count, Is.GreaterThan(0), "the first punch is a change the clients see (cracks)");
            Assert.That(tw.CellHealth(mid, tw.CellAt(mid, Face(2.25, 2.25))), Is.LessThan(tw.Config.IntactHealth));
            Assert.That(tw.OpenCells(mid), Is.EqualTo(0ul));
            tw.Damage(mid, tw.Config.PunchTileDamage, Face(2.25, 2.25), TileCause.Punch, 1, tw.Config.PunchHitRadius);
            Assert.That(tw.OpenCells(mid), Is.Not.EqualTo(0ul), "two punches open it");
        }

        [Test]
        public void PointBlankPump_OneShotsAPlainOrPatchedWall_NotAReinforcedOne()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Pump(tw, ids, 3, 2.25, 2.25), Weapon.Pump, 1);
            bool[,] g = Grid(tw, 3, 3, ids);
            Assert.That(OpenCount(g) * Cell * Cell, Is.GreaterThan(0.9), "one shot, one big hole");
            AssertFair(g, tw.MinHoleCells(ids[4]), "after the pump");
            AssertCollisionIsTheDrawnShape(world, tw, ids);

            var w2 = Wall(3, 3, out int[] ids2);
            foreach (int t in ids2) w2.Tiles.Set(t, TileState.Patched, w2.Tiles.Config.PatchedHealth, TileCause.Heal);
            w2.Tiles.ApplyShot(Pump(w2.Tiles, ids2, 3, 2.25, 2.25), Weapon.Pump, 1);
            Assert.That(OpenCount(Grid(w2.Tiles, 3, 3, ids2)), Is.GreaterThan(0), "a patched wall goes in one shot too");

            var w3 = Wall(3, 3, out int[] ids3);
            foreach (int t in ids3) w3.Tiles.Set(t, TileState.Reinforced, w3.Tiles.Config.ReinforcedHealth, TileCause.Reinforce);
            w3.Tiles.ApplyShot(Pump(w3.Tiles, ids3, 3, 2.25, 2.25), Weapon.Pump, 1);
            Assert.That(OpenCount(Grid(w3.Tiles, 3, 3, ids3)), Is.EqualTo(0), "Ben: no one-shot on reinforced");
            w3.Tiles.ApplyShot(Pump(w3.Tiles, ids3, 3, 2.25, 2.25), Weapon.Pump, 1);
            Assert.That(OpenCount(Grid(w3.Tiles, 3, 3, ids3)), Is.GreaterThan(0), "two pump shots do");
        }

        [Test]
        public void ReinforcedWall_TakesFourAr_PatchedOne()
        {
            var w = Wall(3, 3, out int[] ids);
            foreach (int t in ids) w.Tiles.Set(t, TileState.Reinforced, w.Tiles.Config.ReinforcedHealth, TileCause.Reinforce);
            for (int i = 0; i < 3; i++) w.Tiles.ApplyShot(Ar(w.Tiles, ids, 3, 2.25, 2.25), Weapon.Ar, 1);
            Assert.That(OpenCount(Grid(w.Tiles, 3, 3, ids)), Is.EqualTo(0));
            w.Tiles.ApplyShot(Ar(w.Tiles, ids, 3, 2.25, 2.25), Weapon.Ar, 1);
            Assert.That(OpenCount(Grid(w.Tiles, 3, 3, ids)), Is.GreaterThan(0));

            var p = Wall(3, 3, out int[] pids);
            foreach (int t in pids) p.Tiles.Set(t, TileState.Patched, p.Tiles.Config.PatchedHealth, TileCause.Heal);
            p.Tiles.ApplyShot(Ar(p.Tiles, pids, 3, 2.25, 2.25), Weapon.Ar, 1);
            Assert.That(OpenCount(Grid(p.Tiles, 3, 3, pids)), Is.GreaterThan(0), "patched wall is weaker: one AR bullet");
        }

        [Test]
        public void HoleEdges_AreCutOnTheDiagonal_NotStairSteps()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Pump(tw, ids, 3, 2.25, 2.25), Weapon.Pump, 1);
            int diagonal = 0;
            foreach (int t in ids)
                for (int qi = 0; qi < TileWalls.Quads; qi++)
                    for (int qj = 0; qj < TileWalls.Quads; qj++)
                    {
                        QuadFill f = tw.QuadOf(t, qi, qj);
                        if (f == QuadFill.Inner || f == QuadFill.Outer) diagonal++;
                    }
            Assert.That(diagonal, Is.GreaterThan(8), "a round hole has diagonal edges all round it");
        }

        [Test]
        public void GrenadeOnAWall_RoundHoleAboutFourAndAHalfMetresAcross()
        {
            var world = Wall(5, 5, out int[] ids);
            TileWalls tw = world.Tiles;
            var blast = new Vec3d(3.75, 3.75, 9.8); // 0.2 m in front of the middle tile's face
            var into = new List<int>();
            GrenadeRules.TilesInBlast(tw, blast, new GrenadeConfig(), into);
            tw.Blast(blast, into, tw.Config.GrenadeTileDamage, tw.Config.GrenadeBlastRadius, TileCause.Grenade, 1);
            bool[,] g = Grid(tw, 5, 5, ids);
            int row = 0, col = 0;
            for (int x = 0; x < 5 * N; x++) if (g[x, 20]) row++;
            for (int y = 0; y < 5 * N; y++) if (g[20, y]) col++;
            Assert.That(row * Cell, Is.InRange(3.6, 5.2));
            Assert.That(col * Cell, Is.InRange(3.6, 5.2));
            Assert.That(g[3, 3], Is.False, "far corners stand: the hole is round");
            AssertFair(g, tw.MinHoleCells(ids[0]), "after the grenade");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void GrenadeOnReinforcedWall_LeavesItStanding()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            foreach (int t in ids) tw.Set(t, TileState.Reinforced, 200, TileCause.Reinforce);
            var blast = new Vec3d(2.25, 2.25, 9.8);
            var into = new List<int>();
            GrenadeRules.TilesInBlast(tw, blast, new GrenadeConfig(), into);
            tw.Blast(blast, into, tw.Config.GrenadeTileDamage, tw.Config.GrenadeBlastRadius, TileCause.Grenade, 1);
            Assert.That(OpenCount(Grid(tw, 3, 3, ids)), Is.EqualTo(0), "a reinforced wall survives a grenade (badly cracked)");
            Assert.That(tw.CellHealth(ids[4], TileWalls.MiddleCell), Is.EqualTo(200 - tw.Config.GrenadeTileDamage));
        }

        [Test]
        public void BulletsPassThroughTheHole_NotThroughTheWallBeside_It()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Pump(tw, ids, 3, 2.25, 2.25), Weapon.Pump, 1);
            TerrainHit through = world.Raycast(new Vec3d(2.25, 2.25, 8), new Vec3d(0, 0, 1), 5);
            Assert.That(through.Hit, Is.False, "straight through the middle of the hole");
            TerrainHit beside = world.Raycast(new Vec3d(0.3, 0.3, 8), new Vec3d(0, 0, 1), 5);
            Assert.That(beside.Hit, Is.True);
            Assert.That(beside.Point.Z, Is.EqualTo(10).Within(1e-9));
        }

        // ------------------------------------------------------------------ healing by area

        private static void Heal(TileWalls tw, int tile, int cell, HealKind kind = HealKind.Heal)
        {
            var plan = new Dictionary<int, ulong>();
            tw.HealPlan(tile, cell, kind, plan);
            tw.ApplyHeal(plan, kind, 1);
        }

        [Test]
        public void Heal_ASmallHole_FillsAllOfIt()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Pump(tw, ids, 3, 2.25, 2.25, 0.25), Weapon.Pump, 1);
            Assert.That(OpenCount(Grid(tw, 3, 3, ids)), Is.GreaterThan(0));
            // Aim at the hole's lowest open cell, off centre: the whole hole still closes.
            int t = -1, cell = -1;
            foreach (int u in ids)
            {
                ulong o = tw.OpenCells(u);
                for (int c = 0; c < 64 && t < 0; c++) if (((o >> c) & 1) != 0) { t = u; cell = c; }
            }
            Heal(tw, t, cell);
            Assert.That(OpenCount(Grid(tw, 3, 3, ids)), Is.EqualTo(0), "a hole smaller than a person heals whole");
            Assert.That(tw[t].Patch, Is.Not.EqualTo(0ul), "and comes back as patch");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void Heal_ABrokenTile_InAWall_FillsTheWholeTile()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.Set(ids[4], TileState.Broken, 0, TileCause.Bullet);
            Heal(tw, ids[4], 0);
            Assert.That(tw[ids[4]].State, Is.EqualTo(TileState.Patched));
            Assert.That(tw[ids[4]].Health, Is.EqualTo(tw.Config.PatchedHealth));
        }

        [Test]
        public void Heal_ABigHole_ClosesAChunkAroundTheAim_AndStaysFair()
        {
            var world = Wall(5, 5, out int[] ids);
            TileWalls tw = world.Tiles;
            var blast = new Vec3d(3.75, 3.75, 9.8);
            var into = new List<int>();
            GrenadeRules.TilesInBlast(tw, blast, new GrenadeConfig(), into);
            tw.Blast(blast, into, tw.Config.GrenadeTileDamage, tw.Config.GrenadeBlastRadius, TileCause.Grenade, 1);
            int before = OpenCount(Grid(tw, 5, 5, ids));
            int mid = ids[12];
            Heal(tw, mid, tw.CellAt(mid, Face(3.75, 3.75)));
            bool[,] g = Grid(tw, 5, 5, ids);
            int closed = before - OpenCount(g);
            double r = tw.Config.HealRadius;
            Assert.That(closed * Cell * Cell, Is.InRange(Math.PI * r * r * 0.7, Math.PI * r * r * 1.6), "about a 1.9 m disc of it");
            Assert.That(OpenCount(g), Is.GreaterThan(0), "a grenade hole takes more than one heal");
            AssertFair(g, tw.MinHoleCells(mid), "after a chunk heal");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void Heal_RepairsBadCracks_NeverWeakensReinforced()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.Damage(ids[4], 70, Face(2.25, 2.25), TileCause.Bullet, 1, 0.5); // cracked to 30, nothing open
            int cell = tw.CellAt(ids[4], Face(2.25, 2.25));
            Assert.That(tw.CellHealth(ids[4], cell), Is.EqualTo(30));
            Heal(tw, ids[4], cell);
            Assert.That(tw.CellHealth(ids[4], cell), Is.EqualTo(tw.Config.PatchedHealth));
            var plan = new Dictionary<int, ulong>();
            tw.Set(ids[0], TileState.Reinforced, 120, TileCause.Reinforce);
            tw.HealPlan(ids[0], 9, HealKind.Heal, plan);
            Assert.That(plan.ContainsKey(ids[0]), Is.False, "a heal never touches reinforced cells");
        }

        [Test]
        public void HealSpace_NamesTheCellsThatComeBack()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.ApplyShot(Pump(tw, ids, 3, 2.25, 2.25), Weapon.Pump, 1);
            var plan = new Dictionary<int, ulong>();
            tw.HealPlan(ids[4], tw.CellAt(ids[4], Face(2.25, 2.25)), HealKind.Heal, plan);
            var cells = new List<Aabb>();
            tw.HealSpace(plan, cells, new List<int>());
            Assert.That(cells.Count, Is.EqualTo(OpenCount(Grid(tw, 3, 3, ids))), "every open cell of the small hole");
        }

        // ------------------------------------------------------------------ the rest

        [Test]
        public void SameHits_SameHoles_OnEveryMachine()
        {
            ulong[] Run()
            {
                var world = Wall(4, 3, out int[] ids);
                TileWalls tw = world.Tiles;
                var rng = new Random(7);
                for (int i = 0; i < 60; i++)
                {
                    int t = ids[rng.Next(ids.Length)];
                    Aabb b = tw.BoundsOf(t);
                    var at = Face(b.Min.X + rng.NextDouble() * 1.5, b.Min.Y + rng.NextDouble() * 1.5);
                    tw.Damage(t, 13 + rng.Next(60), at, TileCause.Bullet, 1, 0.3 + rng.NextDouble() * 0.5);
                }
                var masks = new ulong[ids.Length * 2];
                for (int i = 0; i < ids.Length; i++)
                {
                    masks[i] = tw.OpenCells(ids[i]);
                    ulong h = 0;
                    for (int c = 0; c < 64; c++) h = h * 31 + (ulong)tw.CellHealth(ids[i], c);
                    masks[ids.Length + i] = h;
                }
                return masks;
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }

        [Test]
        public void TilesMessage_CarriesTheCells_AndACopyCollidesTheSame()
        {
            var server = Wall(3, 3, out int[] ids);
            var client = Wall(3, 3, out _);
            client.Tiles.CopyOnly = true;
            server.Tiles.ApplyShot(Ar(server.Tiles, ids, 3, 2.0, 2.6), Weapon.Ar, 3);
            server.Tiles.ApplyShot(Ar(server.Tiles, ids, 3, 2.0, 2.6), Weapon.Ar, 3);
            server.Tiles.Set(ids[0], TileState.Reinforced, 200, TileCause.Reinforce, 3);
            server.Tiles.ApplyShot(Ar(server.Tiles, ids, 3, 0.7, 0.7), Weapon.Ar, 3);
            var w = new ByteWriter();
            TileProtocol.Write(w, new TilesMsg { Tick = 5, Changes = new List<TileChange>(server.Tiles.Changes) });
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            var changes = new List<TileChange>();
            TilesMsg m = TileProtocol.Read(ref r, changes, new List<HealEvent>());
            Assert.That(m.Changes.Count, Is.EqualTo(server.Tiles.Changes.Count));
            foreach (TileChange c in m.Changes)
                client.Tiles.WriteCells(c.Tile, c.To == TileState.Broken ? ulong.MaxValue : c.Open, c.Patch, c.Steel, c.Hp, c.Cause, c.By);
            for (int i = 0; i < ids.Length; i++)
            {
                WallTile a = server.Tiles[ids[i]], b = client.Tiles[ids[i]];
                Assert.That(b.Open, Is.EqualTo(a.Open));
                Assert.That(b.State, Is.EqualTo(a.State));
                Assert.That(b.Health, Is.EqualTo(a.Health));
                Assert.That(b.Steel, Is.EqualTo(a.Steel));
                Assert.That(client.Tiles.CellHealths(ids[i]), Is.EqualTo(server.Tiles.CellHealths(ids[i])));
            }
            AssertCollisionMatches(client, client.Tiles, ids);
            AssertCollisionIsTheDrawnShape(client, client.Tiles, ids);
        }

        [Test]
        public void BlastHolesOff_TheOldWholeTileRule()
        {
            var world = Wall(3, 3, out int[] ids, new BreachConfig { BlastHoles = false });
            world.Tiles.Damage(ids[4], 100, Face(2.9, 2.25), TileCause.Bullet);
            Assert.That(world.Tiles[ids[4]].State, Is.EqualTo(TileState.Broken));
            foreach (int t in ids) if (t != ids[4]) Assert.That(world.Tiles.OpenCells(t), Is.EqualTo(0ul));
        }

        // ------------------------------------------------------------------ seeded fight

        [Test]
        public void RandomFight_NeverAPixelHoleOrASliver([Range(1, 8)] int seed)
        {
            const int W = 6, H = 3;
            var world = Wall(W, H, out int[] ids);
            TileWalls tw = world.Tiles;
            var rng = new Random(seed);
            int min = tw.MinHoleCells(ids[0]);
            for (int step = 0; step < 120; step++)
            {
                int t = ids[rng.Next(ids.Length)];
                Aabb b = tw.BoundsOf(t);
                double roll = rng.NextDouble();
                var at = Face(b.Min.X + rng.NextDouble() * 1.5, b.Min.Y + rng.NextDouble() * 1.5);
                string what;
                if (roll < 0.15)
                {
                    HealKind kind = rng.NextDouble() < 0.3 ? HealKind.Reinforce : HealKind.Heal;
                    Heal(tw, t, tw.CellAt(t, at), kind);
                    what = $"{kind} {t} at ({at.X:F2}, {at.Y:F2})";
                }
                else if (roll < 0.2)
                {
                    tw.Set(t, TileState.Reinforced, 200, TileCause.Reinforce);
                    what = "reinforce whole " + t;
                }
                else
                {
                    if (roll < 0.55) tw.ApplyShot(Ar(tw, ids, W, at.X, at.Y), Weapon.Ar, 1);
                    else if (roll < 0.8) tw.ApplyShot(Pump(tw, ids, W, at.X, at.Y, rng.NextDouble() * 0.4), Weapon.Pump, 1);
                    else if (roll < 0.9) tw.Damage(t, 50, at, TileCause.Punch, 1, tw.Config.PunchHitRadius);
                    else
                    {
                        var blast = new Vec3d(at.X, at.Y, 9.8);
                        var into = new List<int>();
                        GrenadeRules.TilesInBlast(tw, blast, new GrenadeConfig(), into);
                        tw.Blast(blast, into, tw.Config.GrenadeTileDamage, tw.Config.GrenadeBlastRadius, TileCause.Grenade, 1);
                    }
                    what = $"hit ({roll:F2}) at ({at.X:F2}, {at.Y:F2})";
                }
                AssertFair(Grid(tw, W, H, ids), min, $"seed {seed} step {step} ({what})");
                AssertCollisionMatches(world, tw, ids);
                if (step % 20 == 19) AssertCollisionIsTheDrawnShape(world, tw, ids);
            }
        }
    }
}
