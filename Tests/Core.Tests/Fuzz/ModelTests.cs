using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Randomised checks of the small Core building blocks against simple reference models written
    /// independently of the Core code: bar economy, chunked storage, ray casts and box overlap.
    /// </summary>
    [Category("Fuzz")]
    public class ModelTests
    {
        private const double B = VoxelGrid.BlockSize;

        private static IEnumerable<ulong> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 8;
            for (ulong s = 1; s <= (ulong)n; s++) yield return s * 104729;
        }

        // ---------------- Sculpt bar ----------------

        /// <summary>
        /// Closed form: after the last spend leaves v0, with k regen ticks since (ticks more than RegenDelay
        /// after the spend) and R refunded, the bar is min(max, v0 + floor(k * max / RegenFull) + R).
        /// </summary>
        [TestCaseSource(nameof(Seeds))]
        public void SculptBarMatchesClosedForm(ulong seed)
        {
            var r = new Rng(seed);
            var cfg = new SculptConfig();
            var bar = new SculptBar();
            int max = SculptConfig.BarMax;

            int v0 = max, k = 0, refunds = 0, lastSpend = int.MinValue / 2;
            int Expected() => (int)Math.Min(max, v0 + (long)k * max / cfg.RegenFullTicks + refunds);

            for (int tick = 0; tick < 20000; tick++)
            {
                double roll = r.Double();
                if (roll < 0.03)
                {
                    int cost = r.Pick(new[] { 500, 700, 1000, 2000, 1500 + 500, 1500 + 1000, 3500 });
                    if (bar.CanAfford(cost))
                    {
                        bar.Spend(cost, tick);
                        v0 = Expected() - cost;
                        k = 0;
                        refunds = 0;
                        lastSpend = tick;
                    }
                    else
                    {
                        Assert.That(cost, Is.GreaterThan(Expected()), $"seed {seed} tick {tick}: CanAfford wrong");
                        Assert.Throws<InvalidOperationException>(() => bar.Spend(cost, tick));
                    }
                }
                else if (roll < 0.04)
                {
                    int amount = r.Pick(new[] { 500, 700, 1000, 2000 });
                    bar.Refund(amount);
                    refunds += amount;
                }

                // Same tick ordering as SculptSystem.Step: commands first, then the bar ticks.
                int next = tick + 1;
                bar.Tick(next, cfg);
                if (next - lastSpend > cfg.RegenDelayTicks) k++;
                Assert.That(bar.Value, Is.EqualTo(Expected()), $"seed {seed} tick {next}");
                Assert.That(bar.Value, Is.InRange(0, max));
            }
        }

        [Test]
        public void SculptBarRejectsNegativeSpend()
        {
            var bar = new SculptBar();
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.Spend(-1, 0));
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax));
        }

        [Test]
        public void RegenFirstGainIsExactlyAfterTheDelay()
        {
            var cfg = new SculptConfig();
            var bar = new SculptBar();
            bar.Spend(5000, 100);
            for (int t = 101; t <= 100 + cfg.RegenDelayTicks; t++)
            {
                bar.Tick(t, cfg);
                Assert.That(bar.Value, Is.EqualTo(5000), "no regen during the 1.5 s delay (tick " + t + ")");
            }
            bar.Tick(100 + cfg.RegenDelayTicks + 1, cfg);
            Assert.That(bar.Value, Is.GreaterThan(5000), "regen starts on the first tick after the delay");
        }

        // ---------------- Chunked storage ----------------

        [TestCaseSource(nameof(Seeds))]
        public void GridMatchesDictionaryModel(ulong seed)
        {
            var r = new Rng(seed);
            int cx = r.Int(1, 4), cy = r.Int(1, 3), cz = r.Int(1, 4);
            int minY = -16 * r.Int(0, 3);
            var g = new VoxelGrid(cx, cy, cz, minY);
            var model = new Dictionary<Int3, Block>();
            var changes = new List<Int3>();
            var dirty = new List<int>();
            Dir[] horiz = DirUtil.Horizontal;

            for (int i = 0; i < 4000; i++)
            {
                // Bias toward chunk borders, where indexing bugs live.
                int Coord(int size) => r.Chance(0.4) ? (r.Int(0, size / 16 + 1) * 16 + r.Int(-1, 1) + size) % size : r.Int(0, size);
                var c = new Int3(Coord(g.SizeX), Coord(g.SizeY) + minY, Coord(g.SizeZ));
                Block b;
                switch (r.Int(0, 4))
                {
                    case 0: b = Block.Empty; break;
                    case 1: b = Block.Anchor; break;
                    case 2: b = Block.Ramp(r.Pick(horiz)); break;
                    default: b = new Block { Kind = BlockKind.Solid, ReinforceTeam = (byte)r.Int(0, 3), ReinforceUntilTick = r.Int(-5, 100000), TintTeam = (byte)r.Int(0, 3), TintTick = r.Int(0, 100000) }; break;
                }
                g.Set(c, b);
                model[c] = b;

                changes.Clear();
                g.DrainChanges(changes);
                Assert.That(changes, Is.EqualTo(new[] { c }), "change log records exactly the write");

                dirty.Clear();
                g.DrainDirtyChunks(dirty);
                Assert.That(dirty, Does.Contain(g.ChunkIndex(g.ChunkCoordOf(c))), "own chunk dirty");
                foreach (Dir d in new[] { Dir.East, Dir.West, Dir.Up, Dir.Down, Dir.North, Dir.South })
                {
                    Int3 n = c + d.ToInt3();
                    if (g.InBounds(n)) Assert.That(dirty, Does.Contain(g.ChunkIndex(g.ChunkCoordOf(n))), "neighbour's chunk dirty");
                }
                Assert.That(dirty, Is.Ordered.And.Unique);
                Assert.That(dirty.Count, Is.LessThanOrEqualTo(4), "a cell touches at most 4 chunks by face");
            }

            for (int y = minY; y < g.MaxYExclusive; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                    {
                        var c = new Int3(x, y, z);
                        Block expect = model.TryGetValue(c, out var m) ? m : Block.Empty;
                        Assert.That(Inspect.Pack(g.Get(c)), Is.EqualTo(Inspect.Pack(expect)), $"cell {c}");
                    }

            for (int i = 0; i < g.ChunkCount; i++)
            {
                Int3 cc = g.ChunkCoordFromIndex(i);
                Assert.That(g.ChunkIndex(cc), Is.EqualTo(i));
                Int3 origin = g.ChunkOrigin(cc);
                Assert.That(g.ChunkCoordOf(origin), Is.EqualTo(cc));
                Assert.That(g.ChunkCoordOf(origin + new Int3(15, 15, 15)), Is.EqualTo(cc));
            }
        }

        [TestCaseSource(nameof(Seeds))]
        public void CellAtAndCellBoundsAgreeEverywhere(ulong seed)
        {
            var r = new Rng(seed);
            for (int i = 0; i < 20000; i++)
            {
                var p = new Vec3d(r.Range(-50, 50), r.Range(-50, 50), r.Range(-50, 50));
                Int3 c = VoxelGrid.CellAt(p);
                Assert.That(VoxelGrid.CellBounds(c).Contains(p, 1e-9), Is.True, $"{p} not inside its own cell {c}");
            }
        }

        // ---------------- Geometry ----------------

        private static VoxelGrid RandomBlocks(Rng r)
        {
            var g = new VoxelGrid(1, 1, 1, -16);
            Dir[] horiz = DirUtil.Horizontal;
            for (int y = -16; y < 0; y++)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++)
                    {
                        double roll = r.Double();
                        if (roll < 0.12) g.Set(x, y, z, Block.Solid);
                        else if (roll < 0.2) g.Set(x, y, z, Block.Ramp(r.Pick(horiz)));
                    }
            return g;
        }

        /// <summary>Raycast agrees with a brute-force march that samples the exact block shapes every millimetre.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void RaycastAgreesWithBruteForceMarch(ulong seed)
        {
            var r = new Rng(seed);
            VoxelGrid g = RandomBlocks(r);
            const double step = 0.001, maxDist = 8.0;
            int checkedHits = 0;

            for (int i = 0; i < 300; i++)
            {
                var o = new Vec3d(r.Range(1.0, 18.0), r.Range(-18.0, -1.0), r.Range(1.0, 18.0));
                if (Inspect.PointInBlock(g.Get(VoxelGrid.CellAt(o)), VoxelGrid.CellAt(o), o)) continue; // start in air
                var d = new Vec3d(r.Range(-1, 1), r.Range(-1, 1), r.Range(-1, 1)).Normalized;
                if (d.LengthSquared == 0) continue;

                VoxelHit hit = VoxelGeometry.Raycast(g, o, d, maxDist);

                double tRef = -1;
                for (double t = 0; t <= maxDist; t += step)
                {
                    Vec3d p = o + d * t;
                    Int3 c = VoxelGrid.CellAt(p);
                    if (!g.InBounds(c)) break; // outside the grid the ray reads anchors; keep to the interior
                    if (Inspect.PointInBlock(g.Get(c), c, p)) { tRef = t; break; }
                }
                if (tRef < 0)
                {
                    // March left the grid or found nothing: a hit must then be at least as far as the march got.
                    continue;
                }

                checkedHits++;
                Assert.That(hit.Hit, Is.True, $"seed {seed} ray {i}: march hit at {tRef:0.000} m, Raycast missed (o {o}, d {d})");
                Assert.That(hit.Distance, Is.EqualTo(tRef).Within(step + 1e-6), $"seed {seed} ray {i}: distance");
                Assert.That(Inspect.PointInBlock(g.Get(hit.Cell), hit.Cell, hit.Point + d * 1e-4) || Inspect.PointInBlock(g.Get(hit.Cell), hit.Cell, hit.Point),
                    Is.True, $"seed {seed} ray {i}: hit point is on the reported cell's block");
                Assert.That(hit.Normal.Dot(d), Is.LessThanOrEqualTo(1e-9), "normal faces the ray");
                Assert.That(hit.Normal.Length, Is.EqualTo(1.0).Within(1e-9), "unit normal");
            }
            Assert.That(checkedHits, Is.GreaterThan(50), "the random rays actually hit things");
        }

        /// <summary>A ray straight down lands exactly on SurfaceHeight, for flat blocks and every ramp facing.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void DownwardRayLandsOnSurfaceHeight(ulong seed)
        {
            var r = new Rng(seed);
            var g = new VoxelGrid(1, 1, 1, -16);
            var cell = new Int3(5, -5, 5);
            foreach (Block b in new[] { Block.Solid, Block.Ramp(Dir.East), Block.Ramp(Dir.West), Block.Ramp(Dir.North), Block.Ramp(Dir.South) })
            {
                g.Set(cell, b);
                Aabb box = VoxelGrid.CellBounds(cell);
                for (int i = 0; i < 500; i++)
                {
                    double x = r.Range(box.Min.X + 1e-4, box.Max.X - 1e-4), z = r.Range(box.Min.Z + 1e-4, box.Max.Z - 1e-4);
                    VoxelHit hit = VoxelGeometry.Raycast(g, new Vec3d(x, box.Max.Y + 2, z), new Vec3d(0, -1, 0), 10);
                    Assert.That(hit.Hit, Is.True);
                    Assert.That(hit.Cell, Is.EqualTo(cell));
                    Assert.That(hit.Point.Y, Is.EqualTo(VoxelGeometry.SurfaceHeight(b, cell, x, z)).Within(1e-9), $"{b} at ({x}, {z})");
                    Assert.That(hit.Face, Is.EqualTo(Dir.Up), "a ramp slope counts as the top face");
                }
            }
        }

        /// <summary>BoxOverlapsBlock agrees with an independent clip-then-corner reference (cases within 1 cm of touching are skipped).</summary>
        [TestCaseSource(nameof(Seeds))]
        public void BoxOverlapAgreesWithSampling(ulong seed)
        {
            var r = new Rng(seed);
            var cell = new Int3(3, 2, 3);
            Aabb c = VoxelGrid.CellBounds(cell);
            Dir[] horiz = DirUtil.Horizontal;
            int compared = 0;

            for (int i = 0; i < 600; i++)
            {
                Block b = r.Chance(0.25) ? Block.Solid : Block.Ramp(r.Pick(horiz));
                var min = new Vec3d(r.Range(c.Min.X - 1, c.Max.X), r.Range(c.Min.Y - 1, c.Max.Y), r.Range(c.Min.Z - 1, c.Max.Z));
                var box = new Aabb(min, min + new Vec3d(r.Range(0.1, 1.0), r.Range(0.1, 1.9), r.Range(0.1, 1.0)));

                // Reference: clip the box to the cell, then the block is hit if the clipped region has volume
                // and its lowest corner toward the high side lies under the slope. Signed margin, negative = clear.
                var lo = new Vec3d(Math.Max(box.Min.X, c.Min.X), Math.Max(box.Min.Y, c.Min.Y), Math.Max(box.Min.Z, c.Min.Z));
                var hi = new Vec3d(Math.Min(box.Max.X, c.Max.X), Math.Min(box.Max.Y, c.Max.Y), Math.Min(box.Max.Z, c.Max.Z));
                double best = Math.Min(hi.X - lo.X, Math.Min(hi.Y - lo.Y, hi.Z - lo.Z));
                if (b.IsRamp)
                {
                    double wedge = double.PositiveInfinity;
                    foreach (double x in new[] { lo.X, hi.X })
                        foreach (double y in new[] { lo.Y, hi.Y })
                            foreach (double z in new[] { lo.Z, hi.Z })
                                wedge = Math.Min(wedge, (y - c.Min.Y) + Progress(b.RampLow, c, x, z));
                    best = Math.Min(best, B - wedge);
                }
                if (Math.Abs(best) < 0.01) continue;
                compared++;
                Assert.That(VoxelGeometry.BoxOverlapsBlock(b, cell, box), Is.EqualTo(best > 0), $"seed {seed} case {i}: {b} vs {box}");
            }
            Assert.That(compared, Is.GreaterThan(300));
        }

        private static double Progress(Dir low, Aabb c, double x, double z)
        {
            switch (low)
            {
                case Dir.East: return x - c.Min.X;
                case Dir.West: return c.Max.X - x;
                case Dir.North: return z - c.Min.Z;
                default: return c.Max.Z - z;
            }
        }
    }
}
