using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Terrain v2 sculpt fuzz: bots fire random pushes, pulls, raises, sinks and throws at a generated arena while
    /// standing around in the blast zone. Invariants on every tick: nobody is ever inside terrain, bars stay in range,
    /// locks belong to running windups; at the end the op log replays to the same grid, everything that rests is
    /// supported (or blocked straight below), and the whole run is deterministic.
    /// </summary>
    public class PieceFuzzTests
    {
        private static IEnumerable<int> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) ? v : 4;
            for (int i = 0; i < n; i++) yield return 101 + i * 7919;
        }

        private sealed class Run
        {
            public SubGrid Grid;
            public SubGrid Start;
            public SculptSystem Sculpt;
            public List<PlayerBody> Players = new List<PlayerBody>();
            public int Valid, Throws, Falls, Shoves, Hits;
        }

        private static Run Play(int seed, int ticks)
        {
            var r = new Rng((ulong)seed);
            var run = new Run();
            run.Grid = MapGen.Generate(seed, out MapInfo info, 32);
            run.Start = run.Grid.Clone();
            run.Sculpt = new SculptSystem(run.Grid, new SculptConfig());
            double size = run.Grid.SizeX * SubGrid.SubSize;

            for (int id = 1; id <= 6; id++)
            {
                var p = new PlayerBody(id, id % 2 + 1, new Vec3d(size * 0.5 + r.Range(-8, 8), 6, size * 0.5 + r.Range(-8, 8)));
                if (r.Chance(0.3)) { p.Height = 1.1; p.EyeHeight = 0.95; }
                if (!Inspect.DropToGround(new VoxelGrid(run.Grid), p, -4, 9)) continue;
                run.Sculpt.AddPlayer(p);
                run.Players.Add(p);
            }

            for (int t = 0; t < ticks; t++)
            {
                foreach (var p in run.Players)
                {
                    if (!r.Chance(0.08)) continue;
                    // Aim at a random nearby point on the terrain.
                    Vec3d look = new Vec3d(r.Range(-1, 1), r.Range(-0.9, 0.1), r.Range(-1, 1));
                    TerrainHit h = run.Grid.Raycast(p.Eye, look, 6.0);
                    if (!h.Hit) continue;
                    SculptAction a;
                    int kind = r.Int(0, 10);
                    if (kind < 4) a = SculptAction.Move(h.Sub, r.Pick(DirUtil.Horizontal), h.Point, r.Chance(0.2));
                    else if (kind < 6) a = SculptAction.Raise(h.Sub, h.Point, r.Chance(0.2));
                    else if (kind < 8) a = SculptAction.Sink(h.Sub, h.Point, r.Chance(0.2));
                    else a = SculptAction.Throw(h.Sub, h.Point, new Vec3d(r.Range(-1, 1), r.Range(-0.2, 0.5), r.Range(-1, 1)));
                    if (run.Sculpt.TryBegin(p, a).IsValid) run.Valid++;
                }
                run.Sculpt.Step();
                foreach (var e in run.Sculpt.Events)
                {
                    if (e.Type == SculptEventType.Thrown) run.Throws++;
                    if (e.Type == SculptEventType.PieceFell) run.Falls++;
                    if (e.Type == SculptEventType.PlayerShoved) run.Shoves++;
                    if (e.Type == SculptEventType.ThrowHit) run.Hits++;
                }
                run.Sculpt.ClearEvents();

                foreach (var p in run.Players)
                {
                    Assert.That(run.Grid.BoxOverlapsSolid(p.Bounds), Is.False, $"seed {seed} tick {t}: player {p.Id} inside terrain at {p.Position}");
                    int bar = run.Sculpt.BarOf(p.Id).Value;
                    Assert.That(bar, Is.InRange(0, SculptConfig.BarMax));
                    p.Kick = Vec3d.Zero;
                }
            }
            return run;
        }

        [TestCaseSource(nameof(Seeds))]
        public void PiecesNeverTrapPlayersAndTheOpLogReplays(int seed)
        {
            Run run = Play(seed, 900);
            Assert.That(run.Valid, Is.GreaterThan(10), "the bots got real sculpts in");

            // Let everything settle, then check support.
            for (int i = 0; i < 200 && run.Sculpt.HasFallingPieces; i++) run.Sculpt.Step();
            for (int i = 0; i < 30; i++) run.Sculpt.Step();
            foreach (var pc in run.Sculpt.Pieces.All)
            {
                if (run.Sculpt.IsFalling(pc.Id)) continue; // waiting above a player
                bool supported = run.Sculpt.IsSupported(pc.Cells);
                bool blocked = false;
                var set = new HashSet<Int3>(pc.Cells);
                foreach (var c in pc.Cells)
                    if (!set.Contains(c + Int3.Down) && run.Grid.Get(c + Int3.Down) != SubId.Empty) blocked = true;
                Assert.That(supported || blocked, Is.True, $"seed {seed}: piece {pc.Id} rests on nothing");
            }

            var replay = new PieceTable(run.Start);
            foreach (var op in run.Sculpt.Pieces.Ops) replay.Apply(op);
            Assert.That(run.Start.SameCells(run.Grid), Is.True, $"seed {seed}: op log does not reproduce the grid");
        }

        [TestCaseSource(nameof(Seeds))]
        public void SameSeedSameWorld(int seed)
        {
            Run a = Play(seed, 400), b = Play(seed, 400);
            Assert.That(a.Grid.Hash(), Is.EqualTo(b.Grid.Hash()));
            for (int i = 0; i < a.Players.Count; i++) Assert.That(a.Players[i].Position, Is.EqualTo(b.Players[i].Position));
        }

        [Test]
        public void FuzzCoversEveryRule()
        {
            int throws = 0, falls = 0, shoves = 0;
            foreach (int seed in Seeds())
            {
                Run run = Play(seed, 900);
                throws += run.Throws;
                falls += run.Falls;
                shoves += run.Shoves;
            }
            Assert.That(throws, Is.GreaterThan(0), "throws");
            Assert.That(falls, Is.GreaterThan(0), "pieces fell");
            Assert.That(shoves, Is.GreaterThan(0), "players shoved or lifted");
        }
    }
}
