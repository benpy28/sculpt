using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Pieces
{
    public class PieceFinderTests
    {
        private const double B = SubGrid.TileSize;

        [Test]
        public void FloorPieceIsTheAimedTileGrownIrregularlyAtMostOneTileOut()
        {
            var w = new SculptWorld();
            var cells = new List<Int3>();
            var sizes = new List<int>();
            bool sawIrregular = false;
            for (int tx = 4; tx < 24; tx++)
                for (int tz = 4; tz < 24; tz += 3)
                {
                    Int3 seed = new Int3(tx * 4 + 1, -1, tz * 4 + 2);
                    PieceFinder.FindNatural(w.Grid, seed, w.Config, cells);
                    sizes.Add(cells.Count);
                    // The whole aimed tile's band, and nothing beyond one tile out or outside the band.
                    for (int x = tx * 4; x < tx * 4 + 4; x++)
                        for (int z = tz * 4; z < tz * 4 + 4; z++)
                            for (int y = -4; y < 0; y++)
                                Assert.That(cells.Contains(new Int3(x, y, z)), Is.True);
                    foreach (var c in cells)
                    {
                        Assert.That(c.Y, Is.InRange(-4, -1));
                        Assert.That(c.X, Is.InRange(tx * 4 - 4, tx * 4 + 7));
                        Assert.That(c.Z, Is.InRange(tz * 4 - 4, tz * 4 + 7));
                    }
                    int xs = cells.Max(c => c.X) - cells.Min(c => c.X) + 1, zs = cells.Max(c => c.Z) - cells.Min(c => c.Z) + 1;
                    if (cells.Count != xs * zs * 4) sawIrregular = true;
                }
            double avg = sizes.Average();
            Assert.That(avg, Is.InRange(150, 420), "typical piece about 2x2 tiles (256 sub-cells)");
            Assert.That(sizes.Max(), Is.LessThanOrEqualTo(12 * 12 * 4));
            Assert.That(sawIrregular, Is.True, "outlines are not all rectangles");
        }

        [Test]
        public void SameAimSamePiece()
        {
            var w = new SculptWorld();
            var a = new List<Int3>();
            var b = new List<Int3>();
            PieceFinder.FindNatural(w.Grid, new Int3(41, -2, 37), w.Config, a);
            PieceFinder.FindNatural(w.Grid, new Int3(41, -2, 37), w.Config, b);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void PieceFollowsTheTerrainItStartsFrom()
        {
            var w = new SculptWorld();
            w.Tiles.Fill(new Int3(11, 0, 8), new Int3(11, 0, 14), Tile.Solid); // a 1-level wall east of the aimed tile
            var cells = new List<Int3>();
            PieceFinder.FindNatural(w.Grid, new Int3(10 * 4 + 2, -1, 10 * 4 + 2), w.Config, cells);
            Assert.That(cells.Any(c => c.X >= 44), Is.False, "floor columns under the wall are a different height, so they stay");
            // And aiming at the wall grabs wall only.
            PieceFinder.FindNatural(w.Grid, new Int3(11 * 4, 1, 10 * 4 + 2), w.Config, cells);
            Assert.That(cells.All(c => c.X >= 44 && c.X < 48 && c.Y >= 0 && c.Y <= 3), Is.True);
        }

        [Test]
        public void AnchorsAndBedrockAreNeverPartOfAPiece()
        {
            var w = new SculptWorld();
            w.Tiles.Set(10, -1, 11, Tile.Anchor);
            var cells = new List<Int3>();
            PieceFinder.FindNatural(w.Grid, new Int3(41, -1, 41), w.Config, cells);
            foreach (var c in cells) Assert.That(w.Grid.Get(c), Is.EqualTo(SubId.Natural));
            PieceFinder.FindNatural(w.Grid, new Int3(41, -1, 45), w.Config, cells);
            Assert.That(cells, Is.Empty, "anchor tile itself is not natural");
        }
    }

    public class PieceSculptTests
    {
        private const double B = SubGrid.TileSize;

        private static (SculptWorld w, PlayerBody p) WallWorld()
        {
            var w = new SculptWorld();
            w.Tiles.Fill(new Int3(7, 0, 5), new Int3(7, 0, 6), Tile.Solid); // 1-level wall, x 8.4 .. 9.6, z 6 .. 8.4
            var p = w.AddPlayer(1, 1, 5, 0, 5.5);
            return (w, p);
        }

        [Test]
        public void ScalingFormulas()
        {
            var c = new SculptConfig();
            Assert.That(c.ScaledCost(500, 256), Is.EqualTo(500));
            Assert.That(c.ScaledCost(500, 64), Is.EqualTo(250), "clamped at half");
            Assert.That(c.ScaledCost(500, 1000), Is.EqualTo(1000), "clamped at double");
            Assert.That(c.ScaledCost(1000, 384), Is.EqualTo(1500));
            Assert.That(c.ScaledWindup(100), Is.EqualTo(9), "0.15 s");
            Assert.That(c.ScaledWindup(257), Is.EqualTo(10));
            Assert.That(c.ScaledWindup(320), Is.EqualTo(11));
            Assert.That(c.ScaledWindup(576), Is.EqualTo(15), "capped");
            Assert.That(c.DeltaFor(SculptActionType.Move, Dir.East), Is.EqualTo(new Int3(5, 0, 0)), "push travels 1.5 m");
            Assert.That(c.DeltaFor(SculptActionType.Raise, Dir.East), Is.EqualTo(new Int3(0, 4, 0)), "raise is one level");
            // Throw numbers from the balance sim: 1 tile, 2x2, 3x3.
            Assert.That(c.ThrowWindup(64), Is.EqualTo(27));
            Assert.That(c.ThrowWindup(256), Is.EqualTo(36));
            Assert.That(c.ThrowWindup(576), Is.EqualTo(51));
            Assert.That(c.ThrowCost(64), Is.EqualTo(1525));
            Assert.That(c.ThrowCost(256), Is.EqualTo(2500));
            Assert.That(c.ThrowCost(576), Is.EqualTo(4125));
            Assert.That(c.ThrowSpeed(256), Is.EqualTo(17.0).Within(1e-9));
            Assert.That(c.ThrowDamage(64, 1), Is.EqualTo(40).Within(1e-9));
            Assert.That(c.ThrowDamage(256, 1), Is.EqualTo(70).Within(1e-9));
            Assert.That(c.ThrowDamage(576, 1.2), Is.EqualTo(120).Within(1e-9), "capped");
            Assert.That(c.DisarmTicks(30), Is.EqualTo(0));
            Assert.That(c.DisarmTicks(60), Is.EqualTo(15));
            Assert.That(c.DisarmTicks(90), Is.EqualTo(30));
            Assert.That(c.DisarmTicks(119), Is.EqualTo(30));
        }

        [Test]
        public void ParkedSwitchOffRefusesEveryBlockMoveForFree()
        {
            var (w, p) = WallWorld();
            w.Config.BlockMovesEnabled = false;
            int bar = w.Sculpt.BarOf(1).Value;
            var point = new Vec3d(8.4, 0.6, 6.6);
            foreach (SculptAction a in new[] { w.Move(p, point, Dir.East), w.Raise(p, point), w.Sink(p, point), w.Throw(p, point, new Vec3d(1, 0, 0)) })
            {
                Assert.That(w.Sculpt.Evaluate(p, a).Result, Is.EqualTo(SculptResult.NotSculptable), a.Type.ToString());
                Assert.That(w.Sculpt.TryBegin(p, a).Result, Is.EqualTo(SculptResult.NotSculptable), a.Type.ToString());
            }
            w.Step(30);
            Assert.That(w.Sculpt.BarOf(1).Value, Is.EqualTo(bar), "nothing charged");
            w.Config.BlockMovesEnabled = true;
            Assert.That(w.Sculpt.TryBegin(p, w.Move(p, point, Dir.East)).Result, Is.EqualTo(SculptResult.Valid), "switching it back on restores it");
        }

        [Test]
        public void PushMovesThePieceOneTileAsOneRigidUnit()
        {
            var (w, p) = WallWorld();
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.Size, Is.InRange(64, 128));
            Assert.That(c.WindupTicks, Is.EqualTo(9));
            Assert.That(c.Cost, Is.EqualTo(w.Config.ScaledCost(500, c.Size)));
            Assert.That(w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax - c.Cost), "charged on confirm");
            ushort id = w.Grid.Get(a.Target);
            Assert.That(SubId.IsPiece(id), Is.True, "carved on confirm");

            var before = w.Sculpt.Pieces.Get(id).Cells.ToArray();
            w.Step(8);
            Assert.That(w.Grid.Get(before[0]), Is.EqualTo(id), "not moved during windup");
            w.Step(1);
            foreach (var x in before)
            {
                Assert.That(w.Grid.Get(x + new Int3(5, 0, 0)), Is.EqualTo(id));
                if (!before.Contains(x - new Int3(5, 0, 0))) Assert.That(w.Grid.Get(x), Is.EqualTo(SubId.Empty));
            }
            Assert.That(w.Sculpt.Events.Any(e => e.Type == SculptEventType.Completed), Is.True);
        }

        [Test]
        public void PushIntoRockIsInvalidAndFree()
        {
            var (w, p) = WallWorld();
            w.Tiles.Fill(new Int3(8, 0, 4), new Int3(8, 1, 7), Tile.Solid); // taller rock right behind the wall (not part of the piece)
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.DestinationBlocked));
            Assert.That(w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(w.Grid.Get(a.Target), Is.EqualTo(SubId.Natural), "nothing carved");
        }

        [Test]
        public void VerticalMoveIsBadDirection()
        {
            var (w, p) = WallWorld();
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.Up);
            Assert.That(w.Sculpt.Evaluate(p, a).Result, Is.EqualTo(SculptResult.BadDirection));
        }

        [Test]
        public void RaiseGrowsTheColumnAndCarriesTheRider()
        {
            var w = new SculptWorld();
            var sculptor = w.AddPlayer(1, 1, 5, 0, 5);
            var rider = w.AddPlayer(2, 2, 8, 0, 5);
            var a = w.Raise(sculptor, new Vec3d(8.5 * B, 0, 5.5 * B));
            SculptCheck c = w.Sculpt.TryBegin(sculptor, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(w.Sculpt.Events.Single(e => e.Type == SculptEventType.Began).WarnedPlayers, Is.EqualTo(new[] { 2 }));
            w.Step(c.WindupTicks);
            Assert.That(w.TopAt(8.5 * B, 5.5 * B), Is.EqualTo(B).Within(1e-9), "surface one level up");
            Assert.That(w.Grid.Get(new Int3(34, -1, 22)), Is.Not.EqualTo(SubId.Empty), "the level it left is filled");
            Assert.That(rider.Position.Y, Is.EqualTo(B).Within(0.02), "carried up");
            Assert.That(w.Inside(rider), Is.False);
        }

        [Test]
        public void SinkDropsTheColumnOneLevelAndStopsAtTheRangeFloor()
        {
            var w = new SculptWorld();
            var p = w.AddPlayer(1, 1, 5, 0, 5);
            Vec3d spot = new Vec3d(8.5 * B, 0, 5.5 * B);
            var a = w.Sink(p, spot);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks);
            Assert.That(w.TopAt(spot.X, spot.Z), Is.EqualTo(-B).Within(1e-9));
            // Second sink reaches level -2, a third would leave the range.
            p.Position = SculptWorld.FeetIn(6.5, 0, 5);
            c = w.Sculpt.TryBegin(p, w.Sink(p, spot.WithY(-B)));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks);
            Assert.That(w.TopAt(spot.X, spot.Z), Is.EqualTo(-2 * B).Within(1e-9));
            p.Position = SculptWorld.FeetIn(7, 0, 5.5);
            Assert.That(w.Sculpt.Evaluate(p, w.Sink(p, spot.WithY(-2 * B))).Result, Is.EqualTo(SculptResult.OutOfHeightRange));
        }

        [Test]
        public void AnchorsAndBedrockCannotBeSculpted()
        {
            var w = new SculptWorld();
            w.Tiles.Set(8, -1, 5, Tile.Anchor);
            var p = w.AddPlayer(1, 1, 6, 0, 5);
            Assert.That(w.Sculpt.Evaluate(p, w.Raise(p, new Vec3d(8.5 * B, 0, 5.5 * B))).Result, Is.EqualTo(SculptResult.NotSculptable));
        }

        [Test]
        public void RangeAndLineOfSight()
        {
            var (w, p) = WallWorld();
            p.Position = SculptWorld.FeetIn(1, 0, 5.5);
            Assert.That(w.Sculpt.Evaluate(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East)).Result, Is.EqualTo(SculptResult.OutOfRange));
            // Aim through rock: target behind the wall, aim point on the wall.
            p.Position = SculptWorld.FeetIn(5, 0, 5.5);
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            a.Target = new Int3(40, 0, 25);
            Assert.That(w.Sculpt.Evaluate(p, a).Result, Is.Not.EqualTo(SculptResult.Valid));
        }

        [Test]
        public void ClickDuringYourOwnWindupIsBufferedAndFiresWhenItEnds()
        {
            var (w, p) = WallWorld();
            Assert.That(w.Sculpt.TryBegin(p, w.Raise(p, new Vec3d(4.5 * B, 0, 7.5 * B))).Result, Is.EqualTo(SculptResult.Valid));
            w.Step(7);
            var second = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            Assert.That(w.Sculpt.TryBegin(p, second).Result, Is.EqualTo(SculptResult.Busy));
            Assert.That(w.Sculpt.HasBufferedConfirm(1), Is.True);
            w.Step(2); // the raise lands at tick 9; the buffered push starts on that same tick
            Assert.That(w.Sculpt.PendingOf(1), Is.Not.Null);
            Assert.That(w.Sculpt.PendingOf(1).Action.Type, Is.EqualTo(SculptActionType.Move));
        }

        [Test]
        public void AClickEarlyInYourOwnWindupIsQueuedNotRefused()
        {
            var (w, p) = WallWorld();
            Assert.That(w.Sculpt.TryBegin(p, w.Raise(p, new Vec3d(4.5 * B, 0, 7.5 * B))).Result, Is.EqualTo(SculptResult.Valid));
            w.Step(1); // far more than the 3-tick click buffer before the windup ends
            Assert.That(w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East)).Result, Is.EqualTo(SculptResult.Busy));
            w.Step(7);
            Assert.That(w.Sculpt.HasBufferedConfirm(1), Is.True, "still queued");
            w.Step(1);
            Assert.That(w.Sculpt.PendingOf(1)?.Action.Type, Is.EqualTo(SculptActionType.Move), "fires the tick the raise lands");
        }

        [Test]
        public void TheNewestQueuedClickReplacesTheOlderOne()
        {
            var (w, p) = WallWorld();
            Assert.That(w.Sculpt.TryBegin(p, w.Raise(p, new Vec3d(4.5 * B, 0, 7.5 * B))).Result, Is.EqualTo(SculptResult.Valid));
            w.Sculpt.TryBegin(p, w.Sink(p, new Vec3d(3.5 * B, 0, 3.5 * B)));
            w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East));
            w.Step(9);
            Assert.That(w.Sculpt.PendingOf(1)?.Action.Type, Is.EqualTo(SculptActionType.Move));
        }

        [Test]
        public void ScrollingTwiceRaisesTheSamePieceTwice()
        {
            var w = new SculptWorld();
            var p = w.AddPlayer(1, 1, 5, 0, 5);
            Vec3d spot = new Vec3d(8.5 * B, 0, 5.5 * B);
            SculptCheck first = w.Sculpt.TryBegin(p, w.Raise(p, spot));
            Assert.That(first.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(2);
            Assert.That(w.Sculpt.TryBegin(p, w.Raise(p, spot)).Result, Is.EqualTo(SculptResult.Busy), "second notch queues on the moving piece");
            w.Step(first.WindupTicks - 2);
            Assert.That(w.TopAt(spot.X, spot.Z), Is.EqualTo(B).Within(1e-9));
            Assert.That(w.Sculpt.PendingOf(1)?.Action.Type, Is.EqualTo(SculptActionType.Raise), "the queued raise follows the piece up");
            w.Step(w.Sculpt.PendingOf(1).CompleteTick - w.Sculpt.Tick);
            Assert.That(w.TopAt(spot.X, spot.Z), Is.EqualTo(2 * B).Within(1e-9));
        }

        [Test]
        public void RaiseAimedAtTheSideOfAWallRaisesItsTop()
        {
            var w = new SculptWorld();
            w.Tiles.Fill(new Int3(8, 0, 5), new Int3(8, 1, 6), Tile.Solid); // 2-level wall
            var p = w.AddPlayer(1, 1, 6, 0, 5.5);
            // Aim at the lower level of the west face: rock sits above it, so the top level is raised instead.
            var a = w.Raise(p, new Vec3d(8 * B, 0.5, 6.6));
            Assert.That(a.Target.Y, Is.InRange(0, 3), "aimed at the lower level");
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.Cells.All(x => x.Y >= 4 && x.Y <= 7), Is.True, "the top level is the piece");
            w.Step(c.WindupTicks);
            Assert.That(w.TopAt(8.5 * B, 5.5 * B), Is.EqualTo(3 * B).Within(1e-9), "wall grows to 3 levels");
        }

        [Test]
        public void LockedWhileAnotherPlayerWindsUpAndTheClickIsBuffered()
        {
            var (w, p) = WallWorld();
            var q = w.AddPlayer(2, 2, 5, 0, 7);
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            Assert.That(w.Sculpt.TryBegin(p, a).Result, Is.EqualTo(SculptResult.Valid));
            w.Step(7);
            var b = w.Move(q, new Vec3d(8.4, 0.6, 7.2), Dir.East);
            SculptCheck locked = w.Sculpt.TryBegin(q, b);
            Assert.That(locked.Result, Is.EqualTo(SculptResult.Locked));
            Assert.That(w.Sculpt.BarOf(2).Value, Is.EqualTo(SculptConfig.BarMax), "locked rejection is free");
            Assert.That(w.Sculpt.HasBufferedConfirm(2), Is.True);
            w.Step(5);
            Assert.That(w.Sculpt.HasBufferedConfirm(2), Is.False, "dropped when the target is gone");
        }

        [Test]
        public void BufferedClickExpiresAfterThreeTicks()
        {
            var (w, p) = WallWorld();
            var q = w.AddPlayer(2, 2, 5, 0, 7);
            Assert.That(w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East)).Result, Is.EqualTo(SculptResult.Valid));
            w.Step(2);
            w.Sculpt.TryBegin(q, w.Move(q, new Vec3d(8.4, 0.6, 7.2), Dir.East));
            w.Step(4);
            Assert.That(w.Sculpt.HasBufferedConfirm(2), Is.False);
            w.Step(10);
            Assert.That(w.Sculpt.PendingOf(2), Is.Null, "too early: the click was dropped");
        }

        [Test]
        public void OneWindupAtATime()
        {
            var (w, p) = WallWorld();
            Assert.That(w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East)).Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(w.Sculpt.TryBegin(p, w.Raise(p, new Vec3d(4.5 * B, 0, 7.5 * B))).Result, Is.EqualTo(SculptResult.Busy));
        }

        [Test]
        public void PlayerInThePathIsShovedAtMostOneTileAndNeverTrapped()
        {
            var (w, p) = WallWorld();
            var victim = w.AddPlayer(2, 2, 8, 0, 5.5);
            victim.Position = new Vec3d(9.6 + 0.36, 0, 6.6);
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Vec3d start = victim.Position;
            w.Step(c.WindupTicks);
            Assert.That(w.Inside(victim), Is.False);
            Assert.That(victim.Position.X - start.X, Is.InRange(0.01, w.Config.MaxShove));
            Assert.That(Math.Abs(victim.Position.Y - start.Y), Is.LessThan(0.31));
        }

        [Test]
        public void ShoveIntoAWallIsInvalid()
        {
            var (w, p) = WallWorld();
            // Rock 0.3 m past where the pushed piece ends (x 11.1): too little room for a player.
            w.Grid.Fill(new Int3(38, 0, 16), new Int3(41, 11, 31), SubId.Natural);
            var victim = w.AddPlayer(2, 2, 8, 0, 5.5);
            victim.Position = new Vec3d(11.4 - 0.36, 0, 6.6);
            Assert.That(w.Sculpt.Evaluate(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East)).Result, Is.EqualTo(SculptResult.PlayerBlocked));
        }

        [Test]
        public void RiderOnASidewaysMoveGetsOnlyAFrictionKick()
        {
            var (w, p) = WallWorld();
            var rider = w.AddPlayer(2, 2, 7, 1, 5.5);
            rider.Position = new Vec3d(9.0, B, 6.9);
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Vec3d start = rider.Position;
            w.Step(c.WindupTicks);
            Assert.That(rider.Position, Is.EqualTo(start), "not carried the full tile");
            Assert.That(rider.Kick.X, Is.EqualTo(w.Config.RiderDragSpeed).Within(1e-9));
            Assert.That(w.Sculpt.Events.Any(e => e.Type == SculptEventType.PlayerDragged && e.PlayerId == 2), Is.True);
        }

        [Test]
        public void UnsupportedPieceFallsRigidlyAndLands()
        {
            var w = new SculptWorld();
            // A 2-level stack: pushing its lower level out leaves the upper level hanging.
            w.Tiles.Fill(new Int3(10, 0, 10), new Int3(10, 1, 10), Tile.Solid);
            var p = w.AddPlayer(1, 1, 8, 0, 10);
            var a = w.Move(p, new Vec3d(12.0, 0.6, 12.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks);
            w.Step(30);
            Assert.That(w.Sculpt.Events.Count(e => e.Type == SculptEventType.PieceFell), Is.EqualTo(4), "one level, one sub-cell per tick");
            Assert.That(w.TopAt(12.6, 12.6), Is.EqualTo(B).Within(1e-9), "the upper level came down to the floor");
            Assert.That(w.Sculpt.HasFallingPieces, Is.False);
        }

        [Test]
        public void ThirdOfTheUndersideIsEnoughSupport()
        {
            var w = new SculptWorld();
            var cells = new List<Int3>();
            // A 1-level slab 6 sub-cells long hanging over an edge: 2 of 6 underside columns on rock stays up.
            w.Grid.Fill(new Int3(40, 0, 40), new Int3(41, 0, 41), SubId.Natural);  // a post 2x2 sub-cells
            w.Grid.Fill(new Int3(40, 1, 40), new Int3(45, 1, 41), SubId.Natural);  // slab on top of it, 6 x 2
            for (int x = 40; x <= 45; x++) for (int z = 40; z <= 41; z++) cells.Add(new Int3(x, 1, z));
            Assert.That(w.Sculpt.IsSupported(cells), Is.True, "4 of 12 underside sub-cells = one third");
            cells.Clear();
            for (int x = 40; x <= 46; x++) for (int z = 40; z <= 41; z++) cells.Add(new Int3(x, 1, z));
            Assert.That(w.Sculpt.IsSupported(cells), Is.False, "4 of 14 is less");
        }

        [Test]
        public void FallingPieceWaitsAbovePlayerInsteadOfCrushing()
        {
            var w = new SculptWorld();
            w.Tiles.Fill(new Int3(10, 0, 10), new Int3(10, 2, 10), Tile.Solid); // 3 levels
            var p = w.AddPlayer(1, 1, 8, 0, 10);
            var a = w.Move(p, new Vec3d(12.0, 0.6, 12.6), Dir.East);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            var victim = w.AddPlayer(2, 2, 10, 0, 10);
            victim.Height = 1.1; // crouched in the gap that opens
            w.Step(c.WindupTicks);
            Assert.That(w.Inside(victim), Is.False, "the pushed-out level left room");
            w.Step(40);
            Assert.That(w.Inside(victim), Is.False, "never crushed");
            Assert.That(w.Sculpt.HasFallingPieces, Is.True, "still waiting above");
            w.Sculpt.RemovePlayer(2);
            w.Step(40);
            Assert.That(w.Sculpt.HasFallingPieces, Is.False);
        }

        [Test]
        public void ReinforcementIsPerPieceAndEnemiesPayDoubleAndWaitLonger()
        {
            var (w, p) = WallWorld();
            var enemy = w.AddPlayer(2, 2, 10, 0, 5.5);
            var a = w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East, reinforce: true);
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.WindupTicks, Is.EqualTo(21));
            Assert.That(c.Cost, Is.EqualTo(w.Config.ScaledCost(500, c.Size) + 1500));
            w.Step(c.WindupTicks);
            PieceInfo info = w.Sculpt.Pieces.Get(c.PieceId);
            Assert.That(info.IsReinforced(w.Sculpt.Tick), Is.True);

            // The enemy pushes it back west: double base cost, 24-tick windup, reinforcement removed. Gun locks.
            enemy.Position = new Vec3d(13.2, 0, 6.6);
            var b = w.Move(enemy, new Vec3d(10.8, 0.6, 6.6), Dir.West);
            SculptCheck e = w.Sculpt.TryBegin(enemy, b);
            Assert.That(e.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(e.EnemyReinforced, Is.True);
            Assert.That(e.Cost, Is.EqualTo(w.Config.ScaledCost(500, e.Size) * 2));
            Assert.That(e.WindupTicks, Is.EqualTo(24));
            Assert.That(e.LocksGun, Is.True, "attacking an enemy piece locks the gun");
            w.Step(e.WindupTicks);
            Assert.That(info.IsReinforced(w.Sculpt.Tick), Is.False);
            Assert.That(info.Team, Is.EqualTo(2));
        }

        [Test]
        public void OwnPiecesDoNotLockTheGun()
        {
            var (w, p) = WallWorld();
            SculptCheck c = w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East));
            Assert.That(c.LocksGun, Is.False, "natural rock");
            w.Step(c.WindupTicks + 100);
            p.Position = new Vec3d(8.0, 0, 6.6);
            SculptCheck d = w.Sculpt.Evaluate(p, w.Raise(p, new Vec3d(10.2, 1.2, 6.9)));
            Assert.That(d.PieceId, Is.EqualTo(c.PieceId));
            Assert.That(d.LocksGun, Is.False, "own piece");
        }

        [Test]
        public void OpsReplayOnAFreshGridReproducesTheWorld()
        {
            var (w, p) = WallWorld();
            SubGrid start = w.Grid.Clone();
            w.Tiles.Fill(new Int3(12, 0, 12), new Int3(12, 1, 12), Tile.Solid);
            SubGrid start2 = w.Grid.Clone();
            w.Sculpt.Pieces.Ops.Clear();
            SculptCheck c = w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East));
            w.Step(c.WindupTicks + 5);
            p.Position = SculptWorld.FeetIn(10, 0, 12);
            c = w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(14.4, 0.6, 15.0), Dir.North));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks + 40);

            var replay = new PieceTable(start2);
            foreach (var op in w.Sculpt.Pieces.Ops) replay.Apply(op);
            Assert.That(start2.SameCells(w.Grid), Is.True);
            Assert.That(start.SameCells(w.Grid), Is.False);
        }

        [Test]
        public void SameCommandsSameWorld()
        {
            ulong Run()
            {
                var (w, p) = WallWorld();
                var q = w.AddPlayer(2, 2, 9, 0, 9);
                w.Sculpt.TryBegin(p, w.Move(p, new Vec3d(8.4, 0.6, 6.6), Dir.East));
                w.Sculpt.TryBegin(q, w.Sink(q, new Vec3d(11.5 * B, 0, 9.5 * B)));
                w.Step(60);
                return w.Grid.Hash();
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }
    }

    public class ThrowTests
    {
        private const double B = SubGrid.TileSize;

        private static (SculptWorld w, PlayerBody p) CoverWorld()
        {
            var w = new SculptWorld();
            w.Tiles.Set(7, 0, 6, Tile.Solid); // a single 1-level cover tile, x 8.4..9.6, z 7.2..8.4
            var p = w.AddPlayer(1, 1, 5, 0, 6);
            return (w, p);
        }

        [Test]
        public void ThrowTearsThePieceOutAndBreaksOnTerrain()
        {
            var (w, p) = CoverWorld();
            w.Tiles.Fill(new Int3(14, 0, 2), new Int3(14, 3, 12), Tile.Solid); // far wall east
            var a = w.Throw(p, new Vec3d(8.4, 0.6, 7.8), new Vec3d(1, 0, 0));
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.LocksGun, Is.True);
            Assert.That(c.WindupTicks, Is.EqualTo(w.Config.ThrowWindup(c.Size)));
            Assert.That(c.Cost, Is.EqualTo(w.Config.ThrowCost(c.Size)));
            w.Step(c.WindupTicks);
            Assert.That(w.Sculpt.Thrown.Count, Is.EqualTo(1));
            Assert.That(w.Grid.Get(a.Target), Is.EqualTo(SubId.Empty), "gap left at launch");
            w.Step(120);
            Assert.That(w.Sculpt.Thrown.Count, Is.Zero);
            var impact = w.Sculpt.Events.Single(e => e.Type == SculptEventType.ThrowImpact);
            Assert.That(impact.Point.X, Is.InRange(14 * B - 2.0, 14 * B), "broke on the far wall");
        }

        [Test]
        public void DirectHitDamagesAndDisarmsTheEnemyButNeverATeammate()
        {
            var (w, p) = CoverWorld();
            var mate = w.AddPlayer(2, 1, 10, 0, 6);
            var enemy = w.AddPlayer(3, 2, 12, 0, 6);
            mate.Position = new Vec3d(11.4, 0, 7.8);
            enemy.Position = new Vec3d(14.0, 0, 7.8);
            var a = w.Throw(p, new Vec3d(8.4, 0.6, 7.8), new Vec3d(1, 0, 0));
            SculptCheck c = w.Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            var began = w.Sculpt.Events.Single(e => e.Type == SculptEventType.Began);
            Assert.That(began.WarnedPlayers, Does.Contain(3), "enemy in the lane is warned");
            Assert.That(began.WarnedPlayers, Does.Not.Contain(2), "lane warning is for enemies");
            w.Step(c.WindupTicks + 60);
            var hits = w.Sculpt.Events.Where(e => e.Type == SculptEventType.ThrowHit).ToList();
            Assert.That(hits.Any(h => h.PlayerId == 2), Is.False, "no friendly fire");
            var hit = hits.Single(h => h.PlayerId == 3);
            Assert.That(hit.Value, Is.InRange(20, 120));
            Assert.That(hit.Disarm, Is.EqualTo(w.Config.DisarmTicks(hit.Value)));
        }

        [Test]
        public void ReinforcedPiecesCannotBeThrown()
        {
            var (w, p) = CoverWorld();
            SculptCheck c = w.Sculpt.TryBegin(p, w.Raise(p, new Vec3d(9.0, 1.2, 7.8), reinforce: true));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks + 1);
            var t = w.Sculpt.Evaluate(p, w.Throw(p, new Vec3d(8.4, 1.8, 7.8), new Vec3d(1, 0, 0)));
            Assert.That(t.Result, Is.EqualTo(SculptResult.CannotThrowReinforced));
        }

        [Test]
        public void RiderIsKickedOffAndNotHit()
        {
            var (w, p) = CoverWorld();
            var rider = w.AddPlayer(2, 2, 7, 1, 6);
            rider.Position = new Vec3d(9.0, B, 7.8);
            SculptCheck c = w.Sculpt.TryBegin(p, w.Throw(p, new Vec3d(8.4, 0.6, 7.8), new Vec3d(1, 0, 0)));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            w.Step(c.WindupTicks);
            Assert.That(rider.Kick.X, Is.GreaterThan(1.0), "pushed along the throw");
            w.Step(5);
            Assert.That(w.Sculpt.Events.Any(e => e.Type == SculptEventType.ThrowHit && e.PlayerId == 2), Is.False, "never hits its own rider at launch");
        }

        [Test]
        public void OneThrowAtATime()
        {
            var (w, p) = CoverWorld();
            w.Tiles.Set(5, 0, 8, Tile.Solid);
            SculptCheck c = w.Sculpt.TryBegin(p, w.Throw(p, new Vec3d(8.4, 0.6, 7.8), new Vec3d(1, 0.2, 0)));
            w.Step(c.WindupTicks);
            Assert.That(w.Sculpt.Thrown.Count, Is.EqualTo(1));
            Assert.That(w.Sculpt.Evaluate(p, w.Throw(p, new Vec3d(6.6, 0.6, 9.6), new Vec3d(0, 0, 1))).Result, Is.EqualTo(SculptResult.Busy));
        }

        [Test]
        public void FlightIsDeterministic()
        {
            Vec3d Run()
            {
                var (w, p) = CoverWorld();
                SculptCheck c = w.Sculpt.TryBegin(p, w.Throw(p, new Vec3d(8.4, 0.6, 7.8), new Vec3d(0.9, 0.3, 0.2)));
                w.Step(c.WindupTicks + 200);
                return w.Sculpt.Events.Single(e => e.Type == SculptEventType.ThrowImpact).Point;
            }
            Assert.That(Run(), Is.EqualTo(Run()));
        }
    }
}
