using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Sculpt rules. Default layout: sculptor (id 1, team 1) stands in cell (5,0,5),
    /// target block sits on the ground at (7,0,5), pushed east into (8,0,5).
    /// </summary>
    public class SculptTests
    {
        private const double B = VoxelGrid.BlockSize;
        private TestWorld _w;
        private PlayerBody _p1;
        private static readonly Int3 Target = new Int3(7, 0, 5);

        [SetUp]
        public void SetUp()
        {
            _w = new TestWorld();
            _w.Grid.Set(Target, Block.Solid);
            _p1 = _w.AddPlayer(1, 1, 5, 0, 5);
        }

        private SculptAction PushEast(Int3 cell, bool reinforce = false) =>
            SculptAction.Move(cell, Dir.East, TestWorld.FaceCenter(cell, Dir.West), reinforce);

        /// <summary>Places a player hovering above a cell so it can look straight down at its column.</summary>
        private PlayerBody Hover(int id, int team, int x, int z, double feetY = 3.0)
        {
            var p = new PlayerBody(id, team, new Vec3d((x + 0.5) * B, feetY, (z + 0.5) * B));
            _w.Sculpt.AddPlayer(p);
            return p;
        }

        private static Vec3d TopOf(Int3 cell) => TestWorld.FaceCenter(cell, Dir.Up);

        /// <summary>The centre of a cell lies on the slope of any ramp in it.</summary>
        private static Vec3d SlopeCenter(Int3 cell) => VoxelGrid.CellBounds(cell).Center;

        // ---------- Basic move ----------

        [Test]
        public void PushMovesBlockOneCellAfterExactWindup()
        {
            SculptCheck c = _w.Sculpt.TryBegin(_p1, PushEast(Target));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.Cost, Is.EqualTo(500));
            Assert.That(c.WindupTicks, Is.EqualTo(12));
            Assert.That(c.Destination, Is.EqualTo(new Int3(8, 0, 5)));
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(9500));

            _w.Step(11);
            Assert.That(_w.At(7, 0, 5).IsEmpty, Is.False, "still winding up at tick 11");
            _w.Step(1);
            Assert.That(_w.At(7, 0, 5).IsEmpty, Is.True);
            Assert.That(_w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.Sculpt.Events.Any(e => e.Type == SculptEventType.Completed && e.Tick == 12), Is.True);
        }

        [Test]
        public void PullMovesBlockTowardSculptor()
        {
            var a = SculptAction.Move(Target, Dir.West, TestWorld.FaceCenter(Target, Dir.West));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(6, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(7, 0, 5).IsEmpty, Is.True);
        }

        [Test]
        public void BlockedDestinationIsInvalidAndCostsNothing()
        {
            _w.Grid.Set(8, 0, 5, Block.Solid);
            SculptCheck c = _w.Sculpt.TryBegin(_p1, PushEast(Target));
            Assert.That(c.Result, Is.EqualTo(SculptResult.DestinationBlocked));
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(_w.Sculpt.PendingOf(1), Is.Null);
        }

        [Test]
        public void PushIntoMapEdgeIsBlocked()
        {
            var edge = new Int3(31, 0, 5);
            _w.Grid.Set(edge, Block.Solid);
            var p = Hover(2, 1, 30, 5, 1.0);
            var a = SculptAction.Move(edge, Dir.East, TestWorld.FaceCenter(edge, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(p, a).Result, Is.EqualTo(SculptResult.DestinationBlocked));
        }

        [Test]
        public void VerticalMoveIsBadDirection()
        {
            var a = SculptAction.Move(Target, Dir.Up, TestWorld.FaceCenter(Target, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.BadDirection));
        }

        [Test]
        public void AnchorIsNotSculptable()
        {
            _w.Grid.Set(Target, Block.Anchor);
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(Target)).Result, Is.EqualTo(SculptResult.NotSculptable));
        }

        [Test]
        public void BlockAboveSculptRangeIsNotSculptable()
        {
            var high = new Int3(7, 4, 5);
            _w.Grid.Set(high, Block.Solid);
            var p = Hover(2, 1, 6, 5, 4.4);
            Assert.That(_w.Sculpt.Evaluate(p, PushEast(high)).Result, Is.EqualTo(SculptResult.NotSculptable));
        }

        [Test]
        public void EmptyTargetIsNoTarget()
        {
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(new Int3(7, 1, 5))).Result, Is.EqualTo(SculptResult.NoTarget));
        }

        // ---------- Range and line of sight ----------

        [Test]
        public void OutOfRangeIsInvalid()
        {
            var far = new Int3(12, 0, 5);
            _w.Grid.Set(far, Block.Solid);
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(far)).Result, Is.EqualTo(SculptResult.OutOfRange));
        }

        [Test]
        public void JustInsideRangeIsValid()
        {
            // Eye x = 6.6. Block at x cell 10 starts at x = 12.0: horizontal gap 5.4 m, vertical gap to block top 0.42 m.
            _w.Grid.Set(Target, Block.Empty);
            var c = new Int3(10, 0, 5);
            _w.Grid.Set(c, Block.Solid);
            var a = SculptAction.Move(c, Dir.East, TestWorld.FaceCenter(c, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.Valid));
        }

        [Test]
        public void BlockInTheWayIsNoLineOfSight()
        {
            _w.Grid.Set(6, 1, 5, Block.Solid);
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(Target)).Result, Is.EqualTo(SculptResult.NoLineOfSight));
        }

        [Test]
        public void AimPointNotOnTargetIsRejected()
        {
            var a = PushEast(Target);
            a.AimPoint = TestWorld.FaceCenter(new Int3(7, 2, 5), Dir.West);
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.NoLineOfSight));
        }

        [Test]
        public void HiddenFaceIsNoLineOfSight()
        {
            // Aim at the far (east) face: the ray hits the west face of the same block first, which is fine,
            // but aiming at a face of a block behind the target is not.
            _w.Grid.Set(8, 0, 5, Block.Solid);
            var behind = new Int3(8, 0, 5);
            var a = SculptAction.Move(behind, Dir.North, TestWorld.FaceCenter(behind, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.NoLineOfSight));
        }

        // ---------- Bar ----------

        [Test]
        public void CostsMatchSpec()
        {
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(Target)).Cost, Is.EqualTo(500));
            Assert.That(_w.Sculpt.Evaluate(_p1, PushEast(Target, true)).Cost, Is.EqualTo(2000));
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Raise(Target, TopOf(Target))).Cost, Is.EqualTo(1000));
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Sink(Target, TopOf(Target))).Cost, Is.EqualTo(700));
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Shape(Target, Dir.West, TopOf(Target))).Cost, Is.EqualTo(500));
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Raise(Target, TopOf(Target), true)).Cost, Is.EqualTo(2500));
        }

        [Test]
        public void NotEnoughBarIsInvalid()
        {
            _w.Sculpt.BarOf(1).SetValue(499);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).Result, Is.EqualTo(SculptResult.NotEnoughBar));
            _w.Sculpt.BarOf(1).SetValue(500);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(0));
        }

        [Test]
        public void RegenWaitsThenRefillsInExactlyTwentySeconds()
        {
            var cfg = new SculptConfig();
            var bar = new SculptBar();
            bar.Spend(SculptConfig.BarMax, 0);
            for (int t = 1; t <= 90; t++) bar.Tick(t, cfg);
            Assert.That(bar.Value, Is.EqualTo(0), "no regen during the 1.5 s delay");
            bar.Tick(91, cfg);
            Assert.That(bar.Value, Is.EqualTo(8));
            for (int t = 92; t <= 1289; t++) bar.Tick(t, cfg);
            Assert.That(bar.Value, Is.EqualTo(9991));
            bar.Tick(1290, cfg);
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax));
            bar.Tick(1291, cfg);
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax));
        }

        [Test]
        public void SpendingResetsRegenDelay()
        {
            var cfg = new SculptConfig();
            var bar = new SculptBar();
            bar.Spend(5000, 0);
            for (int t = 1; t <= 100; t++) bar.Tick(t, cfg);
            int afterFirst = bar.Value;
            Assert.That(afterFirst, Is.GreaterThan(5000));
            bar.Spend(500, 100);
            int v = bar.Value;
            for (int t = 101; t <= 190; t++) bar.Tick(t, cfg);
            Assert.That(bar.Value, Is.EqualTo(v));
        }

        // ---------- Windup, locks, busy ----------

        [Test]
        public void SecondActionDuringWindupIsBusy()
        {
            _w.Grid.Set(7, 0, 6, Block.Solid);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(new Int3(7, 0, 6))).Result, Is.EqualTo(SculptResult.Busy));
        }

        [Test]
        public void LockedTileCannotBeTargetedByOthers()
        {
            var p2 = _w.AddPlayer(2, 2, 5, 0, 4);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            Assert.That(_w.Sculpt.Evaluate(p2, PushEast(Target)).Result, Is.EqualTo(SculptResult.Locked));
        }

        [Test]
        public void LockedDestinationCannotReceiveOtherBlocks()
        {
            // p1 pushes (7,0,5) east into (8,0,5). p2 tries to push (8,0,6) south into the same cell.
            _w.Grid.Set(8, 0, 6, Block.Solid);
            var p2 = _w.AddPlayer(2, 2, 8, 0, 8);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            var other = new Int3(8, 0, 6);
            var a = SculptAction.Move(other, Dir.South, TestWorld.FaceCenter(other, Dir.North));
            Assert.That(_w.Sculpt.Evaluate(p2, a).Result, Is.EqualTo(SculptResult.Locked));
        }

        [Test]
        public void LocksReleaseAfterResolution()
        {
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            Assert.That(_w.Sculpt.IsLocked(Target, out _), Is.True);
            _w.Step(12);
            Assert.That(_w.Sculpt.IsLocked(Target, out _), Is.False);
            Assert.That(_w.Sculpt.IsLocked(new Int3(8, 0, 5), out _), Is.False);
            Assert.That(_w.Sculpt.PendingOf(1), Is.Null);
        }

        [Test]
        public void DisturbedWindupCancelsAndRefunds()
        {
            // A loose block above the destination falls into it 4 ticks after the windup starts.
            _w.Grid.Set(8, 1, 5, Block.Solid);
            _w.Sculpt.NotifyCellChanged(new Int3(8, 1, 5));
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).Kind, Is.EqualTo(BlockKind.Solid), "target did not move");
            Assert.That(_w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid), "fallen block is there");
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax), "refunded");
            Assert.That(_w.Sculpt.Events.Any(e => e.Type == SculptEventType.Cancelled && e.Result == SculptResult.Disturbed), Is.True);
        }

        [Test]
        public void LeavingRangeDuringWindupStillCompletes()
        {
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _p1.Position = TestWorld.FeetIn(25, 0, 25);
            _w.Step(12);
            Assert.That(_w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
        }

        [Test]
        public void RemovingPlayerReleasesLocks()
        {
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Sculpt.RemovePlayer(1);
            Assert.That(_w.Sculpt.IsLocked(Target, out _), Is.False);
            _w.Step(20);
            Assert.That(_w.At(7, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
        }

        // ---------- Reinforcement ----------

        [Test]
        public void ReinforcedActionUsesCasterWindupAndReinforcesResult()
        {
            SculptCheck c = _w.Sculpt.TryBegin(_p1, PushEast(Target, true));
            Assert.That(c.WindupTicks, Is.EqualTo(21));
            _w.Step(21);
            Block b = _w.At(8, 0, 5);
            Assert.That(b.ReinforceTeam, Is.EqualTo(1));
            Assert.That(b.ReinforceUntilTick, Is.EqualTo(21 + 1200));
            Assert.That(b.IsReinforced(21 + 1199), Is.True);
            Assert.That(b.IsReinforced(21 + 1200), Is.False, "decays after 20 s");
        }

        private void ReinforceTarget(byte team, int untilTick)
        {
            Block b = _w.At(7, 0, 5);
            b.ReinforceTeam = team;
            b.ReinforceUntilTick = untilTick;
            _w.Grid.Set(Target, b);
        }

        [Test]
        public void EnemyReinforcedBlockCostsDoubleAndSlowWindup()
        {
            ReinforceTarget(2, 1200);
            SculptCheck c = _w.Sculpt.Evaluate(_p1, PushEast(Target));
            Assert.That(c.EnemyReinforced, Is.True);
            Assert.That(c.Cost, Is.EqualTo(1000));
            Assert.That(c.WindupTicks, Is.EqualTo(24));

            SculptCheck r = _w.Sculpt.Evaluate(_p1, PushEast(Target, true));
            Assert.That(r.Cost, Is.EqualTo(2 * 500 + 1500), "reinforce surcharge is not doubled");
            Assert.That(r.WindupTicks, Is.EqualTo(24));
        }

        [Test]
        public void TeammateReinforcedBlockIsNormal()
        {
            ReinforceTarget(1, 1200);
            SculptCheck c = _w.Sculpt.Evaluate(_p1, PushEast(Target));
            Assert.That(c.EnemyReinforced, Is.False);
            Assert.That(c.Cost, Is.EqualTo(500));
            Assert.That(c.WindupTicks, Is.EqualTo(12));
        }

        [Test]
        public void ExpiredReinforcementIsNormal()
        {
            ReinforceTarget(2, 10);
            _w.Step(10);
            SculptCheck c = _w.Sculpt.Evaluate(_p1, PushEast(Target));
            Assert.That(c.EnemyReinforced, Is.False);
            Assert.That(c.Cost, Is.EqualTo(500));
        }

        [Test]
        public void ReinforcementMovesWithBlockForTeammate()
        {
            ReinforceTarget(1, 1200);
            _w.Sculpt.TryBegin(_p1, PushEast(Target));
            _w.Step(12);
            Assert.That(_w.At(8, 0, 5).ReinforceTeam, Is.EqualTo(1));
            Assert.That(_w.At(8, 0, 5).ReinforceUntilTick, Is.EqualTo(1200), "timer keeps running, not restarted");
        }

        [Test]
        public void EnemyChangeRemovesReinforcement()
        {
            ReinforceTarget(2, 5000);
            _w.Sculpt.TryBegin(_p1, PushEast(Target));
            _w.Step(24);
            Block b = _w.At(8, 0, 5);
            Assert.That(b.IsReinforced(_w.Sculpt.Tick), Is.False);
        }

        [Test]
        public void SculptedBlockIsTintedBySculptorTeam()
        {
            _w.Sculpt.TryBegin(_p1, PushEast(Target));
            _w.Step(12);
            Assert.That(_w.At(8, 0, 5).TintTeam, Is.EqualTo(1));
            Assert.That(_w.At(8, 0, 5).TintTick, Is.EqualTo(12));
        }

        // ---------- Shove and carry ----------

        [Test]
        public void PlayerInDestinationIsShovedJustClear()
        {
            var p2 = _w.AddPlayer(2, 2, 8, 0, 5); // feet x = 10.2, box 9.85..10.55, destination cell x 9.6..10.8
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Step(12);
            Assert.That(p2.Position.X, Is.EqualTo(10.8 + 0.35).Within(1e-9));
            Assert.That(p2.Position.Y, Is.EqualTo(0));
            Assert.That(VoxelGeometry.BoxOverlapsSolid(_w.Grid, p2.Bounds), Is.False);
            Assert.That(_w.Sculpt.Events.Any(e => e.Type == SculptEventType.PlayerShoved && e.PlayerId == 2), Is.True);
        }

        [Test]
        public void ShoveIntoWallIsInvalid()
        {
            _w.AddPlayer(2, 2, 8, 0, 5);
            _w.Grid.Set(9, 0, 5, Block.Solid);
            SculptCheck c = _w.Sculpt.TryBegin(_p1, PushEast(Target));
            Assert.That(c.Result, Is.EqualTo(SculptResult.PlayerBlocked));
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax));
        }

        [Test]
        public void ShoveBecomingBlockedDuringWindupCancels()
        {
            var p2 = _w.AddPlayer(2, 2, 8, 0, 5);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Grid.Set(9, 0, 5, Block.Solid); // wall appears behind p2
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(p2.Position.X, Is.EqualTo(8.5 * B).Within(1e-9));
            Assert.That(_w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax));
        }

        [Test]
        public void PullShovesSculptorBackMinimally()
        {
            _p1.Position = new Vec3d(7.1, 0, 5.5 * B); // box 6.75..7.45 overlaps destination cell 6 (7.2..8.4)
            var a = SculptAction.Move(Target, Dir.West, TestWorld.FaceCenter(Target, Dir.West));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_p1.Position.X, Is.EqualTo(7.2 - 0.35).Within(1e-9));
        }

        [Test]
        public void PlayerStandingOnBlockIsNotDraggedByPush()
        {
            var p2 = new PlayerBody(2, 2, new Vec3d(7.5 * B, B, 5.5 * B));
            _w.Sculpt.AddPlayer(p2);
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Step(12);
            Assert.That(p2.Position, Is.EqualTo(new Vec3d(7.5 * B, B, 5.5 * B)));
        }

        [Test]
        public void RaiseCarriesPlayerOnTop()
        {
            var p2 = new PlayerBody(2, 2, new Vec3d(7.5 * B, B, 5.5 * B));
            _w.Sculpt.AddPlayer(p2);
            SculptCheck c = _w.Sculpt.TryBegin(_p1, SculptAction.Raise(Target, TestWorld.FaceCenter(Target, Dir.West)));
            Assert.That(c.IsValid, Is.True, c.Result.ToString());
            _w.Step(12);
            Assert.That(_w.At(7, 1, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(p2.Position.Y, Is.EqualTo(2 * B).Within(1e-9));
        }

        [Test]
        public void RaiseWithNoHeadroomForRiderIsInvalid()
        {
            var p2 = new PlayerBody(2, 2, new Vec3d(7.5 * B, B, 5.5 * B));
            _w.Sculpt.AddPlayer(p2);
            _w.Grid.Set(7, 3, 5, Block.Anchor); // ceiling at 3.6 m; lifted player would span 2.4..4.2
            var a = SculptAction.Raise(Target, TestWorld.FaceCenter(Target, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.PlayerBlocked));
        }

        [Test]
        public void SinkDropsNobodyIntoGeometry()
        {
            var p2 = new PlayerBody(2, 2, new Vec3d(7.5 * B, B, 5.5 * B));
            _w.Sculpt.AddPlayer(p2);
            Assert.That(_w.Sculpt.TryBegin(_p1, SculptAction.Sink(Target, TestWorld.FaceCenter(Target, Dir.West))).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).IsEmpty, Is.True);
            Assert.That(p2.Position.Y, Is.EqualTo(B), "Core does not move them; movement gravity drops them");
        }

        [Test]
        public void BeganEventWarnsPlayersTouchingTheBlock()
        {
            var onTop = new PlayerBody(2, 2, new Vec3d(7.5 * B, B, 5.5 * B));
            var far = new PlayerBody(3, 2, TestWorld.FeetIn(20, 0, 20));
            _w.Sculpt.AddPlayer(onTop);
            _w.Sculpt.AddPlayer(far);
            _w.Sculpt.TryBegin(_p1, PushEast(Target));
            SculptEvent e = _w.Sculpt.Events.Single(x => x.Type == SculptEventType.Began);
            Assert.That(e.WarnedPlayers, Is.EqualTo(new[] { 2 }));
        }

        // ---------- Raise / sink / height range ----------

        [Test]
        public void RaiseAddsOnTopOfTheTargetedColumn()
        {
            _w.Grid.Set(7, 1, 5, Block.Solid);
            // Aim at the lower block's side: the new block still goes on top of the column.
            SculptCheck c = _w.Sculpt.TryBegin(_p1, SculptAction.Raise(Target, TestWorld.FaceCenter(Target, Dir.West)));
            Assert.That(c.Destination, Is.EqualTo(new Int3(7, 2, 5)));
            _w.Step(12);
            Assert.That(_w.At(7, 2, 5).Kind, Is.EqualTo(BlockKind.Solid));
        }

        [Test]
        public void RaiseOnGroundAddsLevelOne()
        {
            var ground = new Int3(6, -1, 5);
            var p = Hover(2, 1, 6, 5);
            SculptCheck c = _w.Sculpt.TryBegin(p, SculptAction.Raise(ground, TopOf(ground)));
            Assert.That(c.IsValid, Is.True, c.Result.ToString());
            _w.Step(12);
            Assert.That(_w.At(6, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
        }

        [Test]
        public void RaiseAboveMaxHeightIsInvalid()
        {
            _w.Grid.Fill(new Int3(7, 1, 5), new Int3(7, 3, 5), Block.Solid); // top at +4
            var p = Hover(2, 1, 7, 5, 7.0);
            var top = new Int3(7, 3, 5);
            Assert.That(_w.Sculpt.Evaluate(p, SculptAction.Raise(top, TopOf(top))).Result, Is.EqualTo(SculptResult.OutOfHeightRange));
        }

        [Test]
        public void SinkRemovesTopOfColumn()
        {
            _w.Grid.Set(7, 1, 5, Block.Solid);
            SculptCheck c = _w.Sculpt.TryBegin(_p1, SculptAction.Sink(Target, TestWorld.FaceCenter(Target, Dir.West)));
            Assert.That(c.Source, Is.EqualTo(new Int3(7, 1, 5)));
            _w.Step(12);
            Assert.That(_w.At(7, 1, 5).IsEmpty, Is.True);
            Assert.That(_w.At(7, 0, 5).IsEmpty, Is.False);
        }

        [Test]
        public void SinkDownToMinusTwoThenBedrockStops()
        {
            var p = Hover(2, 1, 6, 5, 1.0);
            _w.Sculpt.BarOf(2).SetValue(SculptConfig.BarMax);
            var col = new Int3(6, -1, 5);
            Assert.That(_w.Sculpt.TryBegin(p, SculptAction.Sink(col, TopOf(col))).IsValid, Is.True);
            _w.Step(12);
            var col2 = new Int3(6, -2, 5);
            Assert.That(_w.Sculpt.TryBegin(p, SculptAction.Sink(col2, TopOf(col2))).IsValid, Is.True);
            _w.Step(12);
            var bedrock = new Int3(6, -3, 5);
            Assert.That(_w.At(6, -3, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.Sculpt.Evaluate(p, SculptAction.Sink(bedrock, TopOf(bedrock))).Result, Is.EqualTo(SculptResult.OutOfHeightRange));
            // Raising from -2 is allowed again.
            Assert.That(_w.Sculpt.Evaluate(p, SculptAction.Raise(bedrock, TopOf(bedrock))).Result, Is.EqualTo(SculptResult.Valid));
        }

        [Test]
        public void AnchorTopCannotBeRaisedOrSunk()
        {
            _w.Grid.Set(Target, Block.Anchor);
            var aim = TestWorld.FaceCenter(Target, Dir.West);
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Raise(Target, aim)).Result, Is.EqualTo(SculptResult.NotSculptable));
            Assert.That(_w.Sculpt.Evaluate(_p1, SculptAction.Sink(Target, aim)).Result, Is.EqualTo(SculptResult.NotSculptable));
        }

        [Test]
        public void ReinforcedSinkReinforcesNewTop()
        {
            _w.Grid.Set(7, 1, 5, Block.Solid);
            _w.Sculpt.TryBegin(_p1, SculptAction.Sink(Target, TestWorld.FaceCenter(Target, Dir.West), true));
            _w.Step(21);
            Assert.That(_w.At(7, 0, 5).ReinforceTeam, Is.EqualTo(1));
        }

        [Test]
        public void ReinforcedSinkWithNothingUnderneathIsRejectedForFree()
        {
            // Column (6,*,5) sunk to -2: the block under the top is bedrock, so there is nothing to reinforce.
            _w.Grid.Set(6, -1, 5, Block.Empty);
            var p = Hover(2, 1, 6, 5, 1.0);
            var top = new Int3(6, -2, 5);
            SculptCheck c = _w.Sculpt.TryBegin(p, SculptAction.Sink(top, TopOf(top), true));
            Assert.That(c.Result, Is.EqualTo(SculptResult.NothingToReinforce));
            Assert.That(_w.Sculpt.BarOf(2).Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(_w.Sculpt.Evaluate(p, SculptAction.Sink(top, TopOf(top))).Result, Is.EqualTo(SculptResult.Valid), "plain sink is fine");
        }

        // ---------- Shape / ramps ----------

        [Test]
        public void ShapeMakesRampThenTogglesBackToFlat()
        {
            var a = SculptAction.Shape(Target, Dir.West, TopOf(Target));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).IsRamp && _w.At(7, 0, 5).RampLow == Dir.West, Is.True);

            // A different direction re-faces it.
            var a2 = SculptAction.Shape(Target, Dir.North, SlopeCenter(Target));
            Assert.That(_w.Sculpt.TryBegin(_p1, a2).IsValid, Is.True, "aim on the slope is fine");
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).RampLow, Is.EqualTo(Dir.North));

            // Same direction again goes back to flat.
            var a3 = SculptAction.Shape(Target, Dir.North, SlopeCenter(Target));
            Assert.That(_w.Sculpt.TryBegin(_p1, a3).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).IsRamp, Is.False);
        }

        [Test]
        public void ShapeNeedsColumnTop()
        {
            _w.Grid.Set(7, 1, 5, Block.Solid);
            var a = SculptAction.Shape(Target, Dir.West, TestWorld.FaceCenter(Target, Dir.West));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.NotColumnTop));
        }

        [Test]
        public void ShapeVerticalIsBadDirection()
        {
            var a = SculptAction.Shape(Target, Dir.Up, TopOf(Target));
            Assert.That(_w.Sculpt.Evaluate(_p1, a).Result, Is.EqualTo(SculptResult.BadDirection));
        }

        [Test]
        public void FlatteningRampLiftsPlayerStandingOnSlope()
        {
            _w.Grid.Set(Target, Block.Ramp(Dir.West)); // high at east
            // Player on the slope near the east side: surface under the box's east edge is high.
            double feet = VoxelGeometry.SurfaceHeight(_w.At(7, 0, 5), Target, 7 * B + 0.95, 0) + 0.001;
            var p2 = new PlayerBody(2, 2, new Vec3d(7 * B + 0.6, feet, 5.5 * B));
            _w.Sculpt.AddPlayer(p2);
            Assert.That(VoxelGeometry.BoxOverlapsSolid(_w.Grid, p2.Bounds), Is.False, "setup: standing on the slope");
            var a = SculptAction.Shape(Target, Dir.West, SlopeCenter(Target));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).IsRamp, Is.False);
            Assert.That(p2.Position.Y, Is.EqualTo(B).Within(1e-9));
        }

        [Test]
        public void RaiseOnRampKeepsRampOnTop()
        {
            _w.Grid.Set(Target, Block.Ramp(Dir.West));
            var a = SculptAction.Raise(Target, SlopeCenter(Target));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 0, 5).IsRamp, Is.False);
            Assert.That(_w.At(7, 1, 5).IsRamp && _w.At(7, 1, 5).RampLow == Dir.West, Is.True);
        }

        [Test]
        public void SinkRampLeavesFlatTop()
        {
            _w.Grid.Set(7, 1, 5, Block.Ramp(Dir.West));
            Assert.That(_w.Sculpt.TryBegin(_p1, SculptAction.Sink(Target, TestWorld.FaceCenter(Target, Dir.West))).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(7, 1, 5).IsEmpty, Is.True);
            Assert.That(_w.At(7, 0, 5).IsRamp, Is.False);
        }

        [Test]
        public void PushingBlockOntoRampFlattensIt()
        {
            _w.Grid.Set(8, 0, 5, Block.Ramp(Dir.East));
            var upper = new Int3(7, 1, 5);
            _w.Grid.Set(upper, Block.Solid);
            var a = SculptAction.Move(upper, Dir.East, TestWorld.FaceCenter(upper, Dir.West));
            Assert.That(_w.Sculpt.TryBegin(_p1, a).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(8, 1, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(8, 0, 5).IsRamp, Is.False);
        }

        [Test]
        public void MovedRampKeepsItsShape()
        {
            _w.Grid.Set(Target, Block.Ramp(Dir.South));
            Assert.That(_w.Sculpt.TryBegin(_p1, PushEast(Target)).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(8, 0, 5).IsRamp && _w.At(8, 0, 5).RampLow == Dir.South, Is.True);
        }

        // ---------- Gravity ----------

        [Test]
        public void BlockPushedOffLedgeFallsOneCellEveryFourTicks()
        {
            _w.Grid.Set(7, 1, 5, Block.Solid);
            var upper = new Int3(7, 1, 5);
            Assert.That(_w.Sculpt.TryBegin(_p1, SculptAction.Move(upper, Dir.East, TestWorld.FaceCenter(upper, Dir.West))).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(8, 1, 5).Kind, Is.EqualTo(BlockKind.Solid), "arrives in the air");
            _w.Step(3);
            Assert.That(_w.At(8, 1, 5).Kind, Is.EqualTo(BlockKind.Solid), "still there at +3 ticks");
            _w.Step(1);
            Assert.That(_w.At(8, 1, 5).IsEmpty, Is.True);
            Assert.That(_w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            _w.Step(20);
            Assert.That(_w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid), "rests on the ground");
            Assert.That(_w.Sculpt.HasFallingBlocks, Is.False);
        }

        [Test]
        public void StackFallsTogether()
        {
            var col = new Int3(10, 0, 5);
            _w.Grid.Fill(new Int3(10, 0, 5), new Int3(10, 2, 5), Block.Solid);
            var p = _w.AddPlayer(2, 1, 8, 0, 5);
            Assert.That(_w.Sculpt.TryBegin(p, SculptAction.Move(col, Dir.East, TestWorld.FaceCenter(col, Dir.West))).IsValid, Is.True);
            _w.Step(12);
            Assert.That(_w.At(10, 1, 5).Kind == BlockKind.Solid && _w.At(10, 2, 5).Kind == BlockKind.Solid && _w.At(10, 0, 5).IsEmpty, Is.True);
            _w.Step(4);
            Assert.That(_w.At(10, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(10, 1, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(10, 2, 5).IsEmpty, Is.True);
        }

        [Test]
        public void FallIsMultiCellAndDeterministic()
        {
            _w.Grid.Set(12, 3, 12, Block.Solid);
            _w.Sculpt.NotifyCellChanged(new Int3(12, 3, 12));
            _w.Step(1);  // timer starts
            _w.Step(4);
            Assert.That(_w.At(12, 2, 12).Kind, Is.EqualTo(BlockKind.Solid));
            _w.Step(4);
            Assert.That(_w.At(12, 1, 12).Kind, Is.EqualTo(BlockKind.Solid));
            _w.Step(4);
            Assert.That(_w.At(12, 0, 12).Kind, Is.EqualTo(BlockKind.Solid));
            _w.Step(4);
            Assert.That(_w.At(12, 0, 12).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(12, -1, 12).Kind, Is.EqualTo(BlockKind.Solid));
        }

        [Test]
        public void AnchorsNeverFallAndHoldWhatIsOnThem()
        {
            _w.Grid.Set(12, 2, 12, Block.Anchor);
            _w.Grid.Set(12, 3, 12, Block.Solid);
            _w.Sculpt.NotifyCellChanged(new Int3(12, 2, 12));
            _w.Step(30);
            Assert.That(_w.At(12, 2, 12).Kind, Is.EqualTo(BlockKind.Anchor));
            Assert.That(_w.At(12, 3, 12).Kind, Is.EqualTo(BlockKind.Solid));
        }

        [Test]
        public void FallingBlockNeverCrushesAPlayer()
        {
            var p2 = _w.AddPlayer(2, 2, 12, 0, 12); // occupies 0..1.8 m
            _w.Grid.Set(12, 2, 12, Block.Solid);
            _w.Sculpt.NotifyCellChanged(new Int3(12, 2, 12));
            _w.Step(40);
            Assert.That(_w.At(12, 2, 12).Kind, Is.EqualTo(BlockKind.Solid), "waits above the player");
            Assert.That(VoxelGeometry.BoxOverlapsSolid(_w.Grid, p2.Bounds), Is.False);
            p2.Position = TestWorld.FeetIn(14, 0, 14);
            _w.Step(1);
            Assert.That(_w.At(12, 1, 12).Kind, Is.EqualTo(BlockKind.Solid), "drops as soon as the cell is clear");
        }

        [Test]
        public void BlockLandingOnRampFlattensIt()
        {
            _w.Grid.Set(12, 0, 12, Block.Ramp(Dir.East));
            _w.Grid.Set(12, 2, 12, Block.Solid);
            _w.Sculpt.NotifyCellChanged(new Int3(12, 2, 12));
            _w.Step(10);
            Assert.That(_w.At(12, 1, 12).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(_w.At(12, 0, 12).IsRamp, Is.False);
        }

        [Test]
        public void GravityChangesAreLoggedForSync()
        {
            _w.Grid.Set(12, 1, 12, Block.Solid);
            _w.Grid.ClearChanges();
            _w.Sculpt.NotifyCellChanged(new Int3(12, 1, 12));
            _w.Step(5);
            var changes = new System.Collections.Generic.List<Int3>();
            _w.Grid.DrainChanges(changes);
            Assert.That(changes, Does.Contain(new Int3(12, 0, 12)));
            Assert.That(changes, Does.Contain(new Int3(12, 1, 12)));
        }
    }
}
