using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Server ticks went to 8-37 ms while players blew big holes in walls on the battle royale map (Ben, 2026-11-28).
    /// The cost was mostly bullets: every shot's ray gathered and clipped every brush in a 9 m tube along its whole
    /// 250 m (thousands in a village), and through a hole it also walked 250 m of the props grid cell by cell. Then a
    /// sight grid redo per changed tile, and a replay segment packed on the tick every 10 s. These pin the cheaper
    /// paths to the old answers.
    /// </summary>
    public class BigHoleCostTests
    {
        /// <summary>Walls with holes blown in them, loose boxes and ramps, spread over 200 m.</summary>
        private static BrushWorld HoledTown(int seed)
        {
            var rng = new Random(seed);
            var w = new BrushWorld();
            var walls = new List<int[]>();
            for (int k = 0; k < 14; k++)
            {
                var corner = new Vec3d(rng.Next(0, 130) * 1.5, rng.Next(0, 3) * 1.5, rng.Next(0, 130) * 1.5);
                walls.Add(w.Tiles.AddWall(corner, rng.Next(2) == 0 ? Dir.East : Dir.North, 4 + rng.Next(8), 2 + rng.Next(3)));
            }
            for (int k = 0; k < 400; k++)
            {
                var min = new Vec3d(rng.NextDouble() * 200, rng.NextDouble() * 6, rng.NextDouble() * 200);
                var size = new Vec3d(0.3 + rng.NextDouble() * 6, 0.3 + rng.NextDouble() * 4, 0.3 + rng.NextDouble() * 6);
                if (k % 5 == 0) w.AddRamp(min, min + size, new[] { Dir.East, Dir.West, Dir.North, Dir.South }[rng.Next(4)], BrushKind.Ground);
                else w.AddBox(min, min + size, BrushKind.Ground);
            }
            TileWalls tw = w.Tiles;
            tw.Prepare();
            var near = new List<int>();
            foreach (int[] ids in walls)
                for (int k = 0; k < 4; k++)
                {
                    Aabb b = tw.BoundsOf(ids[rng.Next(ids.Length)]);
                    Vec3d at = b.Center;
                    near.Clear();
                    tw.TilesNear(new Aabb(at - new Vec3d(2.5, 2.5, 2.5), at + new Vec3d(2.5, 2.5, 2.5)), near);
                    near.Sort();
                    tw.Blast(at, near, 150, 0.6 + rng.NextDouble() * 1.4, TileCause.Grenade, 1);
                }
            tw.Changes.Clear();
            return w;
        }

        private static TerrainHit Brute(BrushWorld w, Vec3d o, Vec3d dir, double max)
        {
            dir = dir.Normalized;
            var r = new TerrainHit();
            double best = max;
            for (int i = 0; i < w.Count; i++)
            {
                Brush b = w[i];
                if (!b.Solid || !BrushWorld.Clip(b, o, dir, best, out double t, out Vec3d n)) continue;
                if (r.Hit && t >= best) continue;
                best = t;
                r.Hit = true;
                r.Distance = t;
                r.Brush = i + 1;
            }
            return r;
        }

        [Test]
        public void LongRays_HitExactlyWhatClippingEveryBrushSays([Values(1, 2, 3)] int seed)
        {
            BrushWorld w = HoledTown(seed);
            int holed = 0;
            for (int i = 0; i < w.Count; i++) if (w[i].Solids != null) holed++;
            Assert.That(holed, Is.GreaterThan(10), "the walls really have holes (exact cell shapes)");
            var rng = new Random(seed * 77);
            int hits = 0, far = 0;
            for (int k = 0; k < 4000; k++)
            {
                var o = new Vec3d(-20 + rng.NextDouble() * 240, rng.NextDouble() * 8, -20 + rng.NextDouble() * 240);
                // Mostly flat (bullets), some steep, some straight along an axis (the walk's ties between cell edges).
                Vec3d d = k % 7 == 0 ? new Vec3d(k % 2 == 0 ? 1 : -1, 0, 0)
                    : k % 11 == 0 ? new Vec3d(0, 0, 1)
                    : new Vec3d(rng.NextDouble() * 2 - 1, (rng.NextDouble() * 2 - 1) * (k % 3 == 0 ? 1 : 0.1), rng.NextDouble() * 2 - 1);
                if (d.LengthSquared < 1e-6) continue;
                double max = k % 5 == 0 ? 10 + rng.NextDouble() * 20 : 250;
                TerrainHit got = w.Raycast(o, d, max);
                TerrainHit want = Brute(w, o, d, max);
                Assert.That(got.Hit, Is.EqualTo(want.Hit), $"ray {k} from {o} along {d}");
                if (!want.Hit) continue;
                hits++;
                if (want.Distance > 30) far++;
                Assert.That(got.Distance, Is.EqualTo(want.Distance), $"ray {k}: the nearest hit");
                Assert.That(BrushWorld.Clip(w[got.Brush - 1], o, d.Normalized, 1e9, out double t, out _) && t == got.Distance,
                    $"ray {k}: the brush named is hit at that distance");
            }
            TestContext.WriteLine($"{hits} hits, {far} past 30 m");
            Assert.That(hits, Is.GreaterThan(800));
            Assert.That(far, Is.GreaterThan(100), "plenty of hits far along the ray, past holes and gaps");
        }

        [Test]
        public void TheBattleRoyalePropsGrid_InPieces_HitsWhatTheFullWalkHits()
        {
            SubGrid g = BrMap.BuildGround();
            HeightField f = BrMap.Field;
            var rng = new Random(5);
            int hits = 0;
            for (int k = 0; k < 6000; k++)
            {
                double x = 100 + rng.NextDouble() * (BrMap.Size - 200), z = 100 + rng.NextDouble() * (BrMap.Size - 200);
                var o = new Vec3d(x, f.HeightAt(x, z) + 0.5 + rng.NextDouble() * 4, z);
                var d = new Vec3d(rng.NextDouble() * 2 - 1, (rng.NextDouble() * 2 - 1) * 0.15, rng.NextDouble() * 2 - 1);
                if (d.LengthSquared < 1e-6) continue;
                double max = 20 + rng.NextDouble() * 230;
                TerrainHit got = g.Raycast(o, d, max);
                TerrainHit want = TerrainGeometry.Raycast(g, o, d, max);
                Assert.That(got.Hit, Is.EqualTo(want.Hit), $"ray {k} from {o} along {d}");
                if (!want.Hit) continue;
                hits++;
                Assert.That(got.Distance, Is.EqualTo(want.Distance).Within(1e-9), $"ray {k}");
                Assert.That(got.Sub, Is.EqualTo(want.Sub), $"ray {k}: the same sub-cell");
                Assert.That(got.Id, Is.EqualTo(want.Id), $"ray {k}");
            }
            Assert.That(hits, Is.GreaterThan(300), "enough rays reach a tree, a bush or a rock");
        }

        [Test]
        public void TheBattleRoyaleMap_RaysIntoBuildingsFirst_GiveTheSameHit()
        {
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out _, out _);
            CompositeTerrain fast = BrMap.Terrain(g, w);
            Assert.That(fast.RayIntoBFirst, "the battle royale terrain casts into the buildings first");
            var plain = new CompositeTerrain(fast.A, fast.B);
            HeightField f = BrMap.Field;
            var rng = new Random(8);
            int buildings = 0, ground = 0;
            for (int k = 0; k < 6000; k++)
            {
                double x = 150 + rng.NextDouble() * (BrMap.Size - 300), z = 150 + rng.NextDouble() * (BrMap.Size - 300);
                var o = new Vec3d(x, f.HeightAt(x, z) + 0.3 + rng.NextDouble() * 12, z);
                var d = new Vec3d(rng.NextDouble() * 2 - 1, (rng.NextDouble() * 2 - 1) * 0.3, rng.NextDouble() * 2 - 1);
                if (d.LengthSquared < 1e-6) continue;
                TerrainHit a = fast.Raycast(o, d, 250), b = plain.Raycast(o, d, 250);
                Assert.That(a.Hit, Is.EqualTo(b.Hit), $"ray {k}");
                if (!a.Hit) continue;
                Assert.That(a.Distance, Is.EqualTo(b.Distance), $"ray {k}");
                Assert.That(a.Brush, Is.EqualTo(b.Brush), $"ray {k}");
                Assert.That(a.Ground, Is.EqualTo(b.Ground), $"ray {k}");
                if (a.Brush > 0) buildings++; else ground++;
            }
            Assert.That(buildings, Is.GreaterThan(500));
            Assert.That(ground, Is.GreaterThan(500));
        }

        [Test]
        public void AShotOnTheBattleRoyaleMap_StaysCheap()
        {
            // Pellets from the street through a building and on across the map. Was about 45 us a ray (.NET) before the
            // walk stopped at the first hit; the least of five batches so a busy PC doesn't fail it.
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out _, out _);
            CompositeTerrain terrain = BrMap.Terrain(g, w);
            HeightField f = BrMap.Field;
            var rays = new List<(Vec3d, Vec3d)>();
            var rng = new Random(3);
            while (rays.Count < 500)
            {
                double x = 150 + rng.NextDouble() * (BrMap.Size - 300), z = 150 + rng.NextDouble() * (BrMap.Size - 300);
                var o = new Vec3d(x, f.HeightAt(x, z) + 1.6, z);
                if (terrain.BoxOverlapsSolid(new Aabb(o - new Vec3d(0.3, 1.5, 0.3), o + new Vec3d(0.3, 0.2, 0.3)))) continue;
                rays.Add((o, new Vec3d(rng.NextDouble() * 2 - 1, (rng.NextDouble() * 2 - 1) * 0.05, rng.NextDouble() * 2 - 1)));
            }
            foreach ((Vec3d o, Vec3d d) in rays) terrain.Raycast(o, d, 250); // warm up
            double best = double.MaxValue;
            for (int batch = 0; batch < 5; batch++)
            {
                var sw = Stopwatch.StartNew();
                foreach ((Vec3d o, Vec3d d) in rays) terrain.Raycast(o, d, 250);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1000 / rays.Count);
            }
            TestContext.WriteLine($"{best:0.0} us a ray");
            Assert.That(best, Is.LessThan(30), "a 250 m bullet ray on the battle royale map (was about 45 us)");
        }

        [Test]
        public void TheServersSightGrid_AfterBigHoles_IsTheGridBuiltFresh()
        {
            // The server redoes its sight grid once per patch of touching changes now, not once per change.
            BrushWorld w = HoledTown(4); // already holed when the server starts
            var s = new NetServer(w, new MovementConfig(), new NetConfig(), (c, d, r) => { });
            s.PrepareSight();
            TileWalls tw = s.Walls;
            var rng = new Random(9);
            var near = new List<int>();
            for (int round = 0; round < 30; round++)
            {
                // A few blasts in one tick, some touching each other, some far apart.
                for (int k = 0; k < 1 + rng.Next(4); k++)
                {
                    int t = rng.Next(tw.Count);
                    if (!tw.HasCells(t)) continue;
                    Vec3d at = tw.BoundsOf(t).Center + new Vec3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                    near.Clear();
                    tw.TilesNear(new Aabb(at - new Vec3d(2.5, 2.5, 2.5), at + new Vec3d(2.5, 2.5, 2.5)), near);
                    near.Sort();
                    tw.Blast(at, near, 150, 0.6 + rng.NextDouble() * 1.4, TileCause.Grenade, 1);
                }
                if (round % 5 == 4)
                    for (int k = 0; k < 6; k++) // and some come back (cells closing redo the grid too)
                    {
                        int t = rng.Next(tw.Count);
                        if (tw.HasCells(t)) tw.Set(t, TileState.Intact, 100, TileCause.Built);
                    }
                s.Step();
            }
            var fresh = new Sculpt.Core.Audio.SoundGrid(w);
            fresh.Build();
            Sculpt.Core.Audio.SoundGrid live = s.SightGrid;
            Assert.That(live, Is.Not.Null);
            int checkedFaces = 0;
            for (int x = -4; x < 420; x++)
                for (int y = -6; y < 20; y++)
                    for (int z = -4; z < 420; z += 1)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            bool a = live.Open(x, y, z, axis), b = fresh.Open(x, y, z, axis);
                            if (a != b) Assert.Fail($"cell ({x}, {y}, {z}) axis {axis}: live {a}, fresh {b}");
                            checkedFaces++;
                        }
            Assert.That(checkedFaces, Is.GreaterThan(1000000));
        }

        [Test]
        public void ALateJoinersTiles_AreEveryTileThatEverChanged()
        {
            // The join message (and every replay key frame) now walks only the tiles play touched.
            BrushWorld w = HoledTown(6);
            var sent = new List<byte[]>();
            var s = new NetServer(w, new MovementConfig(), new NetConfig(), (c, d, r) => { if (c == 7) sent.Add(d); });
            TileWalls tw = s.Walls;
            var rng = new Random(2);
            var near = new List<int>();
            for (int k = 0; k < 40; k++)
            {
                int t = rng.Next(tw.Count);
                if (!tw.HasCells(t)) { tw.Damage(t, 30, TileCause.Bullet, 1); continue; }
                Vec3d at = tw.BoundsOf(t).Center;
                near.Clear();
                tw.TilesNear(new Aabb(at - new Vec3d(2, 2, 2), at + new Vec3d(2, 2, 2)), near);
                near.Sort();
                tw.Blast(at, near, 150, 1.0, TileCause.Grenade, 1);
            }
            s.Step();
            var want = new List<int>();
            for (int i = 0; i < tw.Count; i++) if (tw[i].Changed) want.Add(i);
            Assert.That(want.Count, Is.GreaterThan(40));
            s.AddPlayer(7, 1, new Vec3d(-10, 0, -10), 0);
            List<int> got = null;
            foreach (byte[] d in sent)
            {
                if (Protocol.PeekType(d, 0, d.Length) != MsgType.Tiles) continue;
                var r = new ByteReader(d, 0, d.Length);
                TilesMsg m = TileProtocol.Read(ref r, new List<TileChange>(), new List<HealEvent>());
                if (!m.Join) continue;
                got = new List<int>();
                foreach (TileChange c in m.Changes) got.Add(c.Tile);
            }
            Assert.That(got, Is.EqualTo(want), "every changed tile, in tile order");
        }
    }
}
