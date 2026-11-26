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

        /// <summary>Flat ground, a wall column (x 6..7.5, 3 tiles), a double door (x 7.5..10.5, 3 m), a wall column (x 10.5..12), a lintel row.</summary>
        private static CompositeTerrain DoorArena(out TileWalls walls, out int left, out int right)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            double s = w.Tiles.Config.TileSize, th = w.Tiles.Config.TileThickness;
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(10.5, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(7.5, 3, 12), Dir.East, 2, 1);
            left = w.Tiles.AddTile(new Aabb(new Vec3d(7.5, 0, 12), new Vec3d(9, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            right = w.Tiles.AddTile(new Aabb(new Vec3d(9, 0, 12), new Vec3d(10.5, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            w.Tiles.PairDoor(left, right);
            w.Tiles.Changes.Clear();
            walls = w.Tiles;
            var t = new CompositeTerrain(g, w);
            w.Tiles.Outside = g;
            return t;
        }

        /// <summary>Open cells of the wall column beside the door (tiles 0..2), as one opening count (they are stacked).</summary>
        private static int OpenInColumn(TileWalls w)
        {
            int n = 0;
            for (int t = 0; t < 3; t++) n += TileWalls.PopCount(w.OpenCells(t));
            return n;
        }

        [Test]
        public void APatchedOrReinforcedDoor_StaysPatchedOrReinforced_AfterOpeningAndShutting()
        {
            DoorArena(out TileWalls w, out int left, out int right);
            var leaves = new List<int>();
            w.Set(left, TileState.Patched, w.Config.PatchedHealth, TileCause.Heal, 1);
            w.Set(right, TileState.Reinforced, 80, TileCause.Reinforce, 1); // a reinforced leaf that took some damage
            DoorRules.Set(w, left, true, TileCause.Door, 1, leaves);
            DoorRules.Set(w, left, false, TileCause.Door, 1, leaves);
            Assert.That(w[left].State, Is.EqualTo(TileState.Patched), "a healed door still looks healed");
            Assert.That(w[left].Health, Is.EqualTo(w.Config.PatchedHealth));
            Assert.That(w[right].State, Is.EqualTo(TileState.Reinforced), "a reinforced door is still reinforced");
            Assert.That(w[right].Health, Is.EqualTo(80));
        }

        [Test]
        public void AHoleBesideAnOpenDoor_IsStillAFairHole_OnceTheDoorShuts()
        {
            DoorArena(out TileWalls w, out int left, out _);
            var leaves = new List<int>();
            DoorRules.Set(w, left, true, TileCause.Door, 1, leaves);
            // Two AR bullets on the wall right beside the open doorway: the hole joins the doorway.
            var at = new Vec3d(7.45, 1.0, 12);
            int tile = 0;
            w.Damage(tile, w.Config.ArTileDamage, at, TileCause.Bullet, 1, w.Config.ArHitRadius);
            w.Damage(tile, w.Config.ArTileDamage, at, TileCause.Bullet, 1, w.Config.ArHitRadius);
            int open = OpenInColumn(w);
            Assert.That(open, Is.GreaterThan(0), "the two taps opened something");
            Assert.That(DoorRules.Set(w, left, false, TileCause.Door, 1, leaves), Is.EqualTo(2));
            open = OpenInColumn(w);
            int min = w.MinHoleCells(tile);
            Assert.That(open == 0 || open >= min, Is.True,
                $"with the door shut the hole beside it is its own opening: {open} cells, the minimum is {min}");
        }
    }
}
