using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Blast holes (Ben 2026-11-25): holes are shaped by the hit on an 8 x 8 cell grid per tile, never a pixel hole or a
    /// sliver. Hand-worked cases plus a seeded random fight that checks the fairness rules after every hit.
    /// </summary>
    public class BlastHoleTests
    {
        private const int N = Brush.CellsPerSide;

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
                    Assert.That(size >= minCells || edge, Is.True, $"{when}: an opening of only {size} cells (min {minCells})");
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

        /// <summary>What collides is exactly what the cells say.</summary>
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

        private static Vec3d Face(double x, double y) => new Vec3d(x, y, 10);

        // ------------------------------------------------------------------ hand-worked cases

        [Test]
        public void FarBulletBreak_OpensARoundHoleAroundTheHit_RestOfTheTileStands()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            int mid = ids[4]; // tile (1, 1): x 1.5..3, y 1.5..3
            Vec3d hit = Face(2.25, 2.25);
            for (int i = 0; i < 7; i++) tw.Damage(mid, 13, hit, TileCause.Bullet);
            Assert.That(tw[mid].Holed, Is.False, "7 AR bullets crack it but do not break it");
            Assert.That(tw.Damage(mid, 13, hit, TileCause.Bullet), Is.True, "the 8th breaks it");
            WallTile t = tw[mid];
            Assert.That(t.State, Is.EqualTo(TileState.Damaged), "a far bullet takes a round chunk; the rest stays, cracked");
            Assert.That(t.Holed, Is.True);
            bool[,] g = Grid(tw, 3, 3, ids);
            int open = OpenCount(g);
            double cellArea = (1.5 / N) * (1.5 / N);
            Assert.That(open * cellArea, Is.GreaterThanOrEqualTo(0.63), "at least half a body");
            Assert.That(open * cellArea, Is.LessThan(1.8), "a far bullet's hole stays modest");
            // Round-ish around the hit: no open cell further than the radius plus the roughness, and the cells right
            // at the hit are open.
            double r = tw.Config.BlastRadiusFor(13) + tw.Config.BlastJitter + 0.3;
            for (int x = 0; x < 3 * N; x++)
                for (int y = 0; y < 3 * N; y++)
                {
                    if (!g[x, y]) continue;
                    double cx = (x + 0.5) * 1.5 / N, cy = (y + 0.5) * 1.5 / N;
                    Assert.That(Math.Sqrt((cx - 2.25) * (cx - 2.25) + (cy - 2.25) * (cy - 2.25)), Is.LessThan(r));
                }
            Assert.That(g[11, 11] && g[12, 12], Is.True, "the cells at the hit are gone");
            Assert.That(t.Health, Is.InRange(1, 49), "what is left needs a few more hits");
            AssertFair(g, tw.MinHoleCells(mid), "after one break");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void PointBlankPump_BlowsABigUnevenChunk_AcrossTileBorders()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            int mid = ids[4];
            // Two point-blank pump shots, all 9 pellets on the middle tile near its lower-left corner.
            var pellets = new List<BulletHit>();
            for (int k = 0; k < 9; k++)
                pellets.Add(new BulletHit { Kind = HitKind.Terrain, Brush = tw[mid].Brush + 1, Point = Face(1.8 + 0.02 * k, 1.8 + 0.01 * k) });
            tw.ApplyShot(pellets, Weapon.Pump, 1);
            Assert.That(tw[mid].Holed, Is.False);
            tw.ApplyShot(pellets, Weapon.Pump, 1);
            bool[,] g = Grid(tw, 3, 3, ids);
            double area = OpenCount(g) * (1.5 / N) * (1.5 / N);
            Assert.That(area, Is.GreaterThan(3.0), "a big chunk, not a 1.5 m square (2.25 m^2)");
            int neighboursHit = 0;
            foreach (int t in ids) if (t != mid && tw.OpenCells(t) != 0) neighboursHit++;
            Assert.That(neighboursHit, Is.GreaterThanOrEqualTo(2), "the hole cuts into the tiles around it");
            AssertFair(g, tw.MinHoleCells(mid), "after the pump");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void ReinforcedNeighbours_KeepEveryCell()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            foreach (int t in ids) if (t != ids[4]) tw.Set(t, TileState.Reinforced, 200, TileCause.Reinforce);
            tw.Damage(ids[4], 1000, Face(2.25, 2.25), TileCause.Grenade, 1, 2.2);
            foreach (int t in ids)
                if (t != ids[4]) Assert.That(tw.OpenCells(t), Is.EqualTo(0ul), $"reinforced tile {t}");
            Assert.That(tw[ids[4]].State, Is.EqualTo(TileState.Broken), "the big blast takes the middle tile whole");
        }

        [Test]
        public void GrenadeOnAWall_RoundHoleAboutFourAndAHalfMetresAcross()
        {
            var world = Wall(5, 5, out int[] ids);
            TileWalls tw = world.Tiles;
            var blast = new Vec3d(3.75, 3.75, 9.8); // 0.2 m in front of the middle tile's face
            var into = new List<int>();
            GrenadeRules.TilesInBlast(tw, blast, new GrenadeConfig(), into);
            foreach (int t in into) tw.Damage(t, tw.Config.GrenadeTileDamage, blast, TileCause.Grenade, 1, tw.Config.GrenadeBlastRadius);
            bool[,] g = Grid(tw, 5, 5, ids);
            // Across the middle row and column it is about 4.4 m wide, but the corners of the 3 x 3 tile square stand.
            int row = 0, col = 0;
            for (int x = 0; x < 5 * N; x++) if (g[x, 20]) row++;
            for (int y = 0; y < 5 * N; y++) if (g[20, y]) col++;
            Assert.That(row * 1.5 / N, Is.InRange(3.6, 5.2));
            Assert.That(col * 1.5 / N, Is.InRange(3.6, 5.2));
            Assert.That(g[8, 8], Is.False, "the corner of the old 3 x 3 square still stands: the hole is round");
            AssertFair(g, tw.MinHoleCells(ids[0]), "after the grenade");
            AssertCollisionMatches(world, tw, ids);
        }

        [Test]
        public void BulletsAndPlayersPassThroughTheHole_NotThroughTheWallBeside_It()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            tw.Damage(ids[4], 100, Face(2.25, 2.25), TileCause.Bullet);
            TerrainHit through = world.Raycast(new Vec3d(2.25, 2.25, 8), new Vec3d(0, 0, 1), 5);
            Assert.That(through.Hit, Is.False, "straight through the middle of the hole");
            TerrainHit beside = world.Raycast(new Vec3d(0.3, 0.3, 8), new Vec3d(0, 0, 1), 5);
            Assert.That(beside.Hit, Is.True);
            Assert.That(beside.Point.Z, Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void Heal_FillsTheTile_AndTheSmallBitsItLeavesNextDoor()
        {
            var world = Wall(3, 3, out int[] ids);
            TileWalls tw = world.Tiles;
            // A break near the middle tile's right edge takes a little of the tile to its right.
            tw.Damage(ids[4], 100, Face(2.9, 2.25), TileCause.Bullet);
            Assert.That(tw.OpenCells(ids[5]), Is.Not.EqualTo(0ul), "the hole crosses into the right-hand tile");
            var filled = new List<Aabb>();
            tw.CellsFilledByRestoring(ids[4], filled);
            tw.Set(ids[4], TileState.Patched, 50, TileCause.Heal);
            Assert.That(tw[ids[4]].Open, Is.EqualTo(0ul), "a heal brings every cell back");
            bool[,] g = Grid(tw, 3, 3, ids);
            AssertFair(g, tw.MinHoleCells(ids[4]), "after the heal");
            AssertCollisionMatches(world, tw, ids);
            if (tw.OpenCells(ids[5]) == 0) Assert.That(filled, Is.Not.Empty, "the dry run named the cells that filled");
        }

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
                    tw.Damage(t, 13 + rng.Next(60), at, TileCause.Bullet);
                }
                var masks = new ulong[ids.Length];
                for (int i = 0; i < ids.Length; i++) masks[i] = tw.OpenCells(ids[i]);
                return masks;
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }

        [Test]
        public void TilesMessage_CarriesTheCells_AndACopyCollidesTheSame()
        {
            var server = Wall(3, 3, out int[] ids);
            var client = Wall(3, 3, out _);
            server.Tiles.Damage(ids[4], 100, Face(2.0, 2.6), TileCause.Bullet, 3);
            var w = new ByteWriter();
            TileProtocol.Write(w, new TilesMsg { Tick = 5, Changes = new List<TileChange>(server.Tiles.Changes) });
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            var changes = new List<TileChange>();
            TilesMsg m = TileProtocol.Read(ref r, changes, new List<HealEvent>());
            Assert.That(m.Changes.Count, Is.EqualTo(server.Tiles.Changes.Count));
            foreach (TileChange c in m.Changes) client.Tiles.Set(c.Tile, c.To, c.Health, c.Cause, c.By, c.Open);
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.That(client.Tiles[ids[i]].Open, Is.EqualTo(server.Tiles[ids[i]].Open));
                Assert.That(client.Tiles[ids[i]].State, Is.EqualTo(server.Tiles[ids[i]].State));
            }
            AssertCollisionMatches(client, client.Tiles, ids);
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
                string what;
                if (roll < 0.12)
                {
                    bool reinforce = rng.NextDouble() < 0.3;
                    if (reinforce) tw.Set(t, TileState.Reinforced, 200, TileCause.Reinforce);
                    else tw.Set(t, TileState.Patched, 50, TileCause.Heal);
                    what = (reinforce ? "reinforce " : "heal ") + t;
                }
                else
                {
                    var at = Face(b.Min.X + rng.NextDouble() * 1.5, b.Min.Y + rng.NextDouble() * 1.5);
                    int dmg = roll < 0.5 ? 13 : roll < 0.8 ? 54 : roll < 0.9 ? 50 : 150;
                    double radius = dmg == 150 ? tw.Config.GrenadeBlastRadius : -1;
                    tw.Damage(t, dmg, at, TileCause.Bullet, 1, radius);
                    what = $"hit {t} for {dmg} at ({at.X:F2}, {at.Y:F2})";
                }
                AssertFair(Grid(tw, W, H, ids), min, $"seed {seed} step {step} ({what})");
                AssertCollisionMatches(world, tw, ids);
            }
        }
    }
}
