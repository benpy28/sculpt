using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Regression tests from the 2026-11-26 wall/door/throwable bug hunt.</summary>
    public class WallBugHuntTests
    {
        private static NetHarness DuelMatch(out BreachMapInfo info, out NetHarness.Peer a)
        {
            DuelMap.BuildBrushes(out info);
            BreachMapInfo i = info;
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), i.TeamSpawns[0][0]);
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            h.Server.MatchConfig.DurationTicks = 120 * SimTime.Per60;
            h.Server.MatchConfig.ResultsTicks = 30 * SimTime.Per60;
            a = h.AddClient(spawn: i.TeamSpawns[0][0]);
            h.AddClient(spawn: i.TeamSpawns[1][0], team: 2, seed: 2);
            return h;
        }

        [Test]
        public void ANewMatch_PutsBackCellDamageAndMaterialsThatDoNotMoveTheTileSummary()
        {
            var h = DuelMatch(out BreachMapInfo info, out var a);
            h.RunSeconds(1.0);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            TileWalls w = h.Server.Walls;
            int cracked = -1, mixed = -1;
            foreach (int t in info.Block.Tiles)
            {
                if (!w.HasCells(t) || w[t].State != TileState.Intact) continue;
                if (cracked < 0) cracked = t;
                else { mixed = t; break; }
            }
            // One cell at the edge of an SMG hit: 25 off one cell (the tile still sums to 100, Intact).
            byte[] hp = w.CellHealths(cracked);
            hp[0] = 75;
            w.WriteCells(cracked, 0, 0, 0, hp, TileCause.Bullet, 0);
            // One reinforced and two patched cells (200 + 50 + 50 = 3 x 100: the tile still sums to 100, Intact).
            byte[] hp2 = w.CellHealths(mixed);
            hp2[0] = 200; hp2[1] = 50; hp2[2] = 50;
            w.WriteCells(mixed, 0, 0b110, 0b001, hp2, TileCause.Heal, 0);
            Assert.That(w[cracked].State, Is.EqualTo(TileState.Intact));
            Assert.That(w[cracked].Health, Is.EqualTo(100));
            Assert.That(w[mixed].State, Is.EqualTo(TileState.Intact));
            Assert.That(w[mixed].Health, Is.EqualTo(100));

            h.RunSeconds(2.0 + 0.6); // playing ends, results, new countdown
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown).Or.EqualTo(MatchPhase.Playing));
            Assert.That(w.CellHealth(cracked, 0), Is.EqualTo(100), "the cracked cell is whole again");
            Assert.That(w[mixed].Patch, Is.EqualTo(0ul), "no cement left");
            Assert.That(w[mixed].Steel, Is.EqualTo(0ul), "no steel left");
            Assert.That(w.CellHealth(mixed, 0), Is.EqualTo(100));
            h.RunSeconds(0.3);
            Assert.That(a.Client.Walls.CellHealth(cracked, 0), Is.EqualTo(100), "on the client too");
            Assert.That(a.Client.Walls[mixed].Steel, Is.EqualTo(0ul), "on the client too");
        }
    }
}
