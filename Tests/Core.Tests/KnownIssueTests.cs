using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Tests.Fuzz;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Bugs found by the hardening suite, written as the behaviour the spec asks for. They fail on the
    /// current Core, so they are marked Ignore (the reason says why) to keep the suite green.
    /// When a fix lands in Core, delete the Ignore line and the test must pass.
    /// </summary>
    [Category("KnownIssue")]
    public class KnownIssueTests
    {
        private const double B = VoxelGrid.BlockSize;

        private static PlayerBody Crouched(int id, int team, double x, double z)
        {
            var p = new PlayerBody(id, team, new Vec3d(x, 5, z)) { Height = 1.1, EyeHeight = 0.95 };
            return p;
        }

        /// <summary>
        /// ISSUE-1 (trap): a block falling onto a ramp flattens the ramp, but gravity only checks the
        /// landing cell for players, not the ramp's air pocket that the flattening fills. A crouched
        /// player on the low lip of the ramp ends up inside the now-full cube.
        /// </summary>
        [Test]
        public void BlockLandingOnRampNeverTrapsPlayerOnTheRampLip()
        {
            var w = new TestWorld();
            var ramp = new Int3(5, 0, 5);
            w.Grid.Set(ramp, Block.Ramp(Dir.East)); // low edge at x = 7.2

            // Crouched player with only 5 cm of their box over the ramp's low lip.
            var victim = Crouched(2, 2, 7.2 - 0.05 + 0.35, 6.6);
            Assert.That(Inspect.DropToGround(w.Grid, victim), Is.True);
            w.Sculpt.AddPlayer(victim);
            Assert.That(Inspect.IsInsideTerrain(w.Grid, victim), Is.False, "setup: victim starts clear");
            Assert.That(victim.Bounds.Max.Y, Is.LessThan(B), "setup: victim's head is below the landing cell");

            w.Grid.Set(5, 2, 5, Block.Solid); // unsupported block above the ramp
            w.Sculpt.NotifyCellChanged(new Int3(5, 2, 5));
            w.Step(40);

            Assert.That(Inspect.IsInsideTerrain(w.Grid, victim), Is.False, "nobody is ever trapped inside geometry");
        }

        /// <summary>
        /// ISSUE-2 (shove distance): pushing a ramp horizontally shoves a player standing on its slope by
        /// up to ~1.9 m (more than one cell), because the shove clears the whole destination cell from the
        /// player's trailing edge. Rule 7 says at most one cell. Found by the crowded-arena fuzz (seed 47516).
        /// The right fix is a design call for Ben: cap at one cell, or lift the player onto the moved slope.
        /// </summary>
        [Test]
        public void PushingARampNeverShovesMoreThanOneCell()
        {
            var w = new TestWorld();
            var target = new Int3(7, 0, 5); // spans x 8.4 .. 9.6
            w.Grid.Set(target, Block.Ramp(Dir.East));
            var sculptor = w.AddPlayer(1, 1, 5, 0, 5);

            var victim = new PlayerBody(2, 2, new Vec3d(9.0 + 0.35, 5, 6.6)); // trailing edge half way down the slope
            Assert.That(Inspect.DropToGround(w.Grid, victim), Is.True);
            w.Sculpt.AddPlayer(victim);
            Vec3d start = victim.Position;

            var a = SculptAction.Move(target, Dir.East, TestWorld.FaceCenter(target, Dir.West));
            Assert.That(w.Sculpt.TryBegin(sculptor, a).IsValid, Is.True);
            w.Step(12);

            Assert.That(Inspect.IsInsideTerrain(w.Grid, victim), Is.False);
            Assert.That((victim.Position - start).HorizontalLength, Is.LessThanOrEqualTo(B + 1e-6), "shove is at most one cell");
        }

        /// <summary>
        /// ISSUE-3 (support rule): a stack that contains an anchor anywhere never falls, so a block pushed into
        /// the cell directly under a floating anchor (an arch, bridge or landmark) hangs there. Agreed rule 1:
        /// a block is held up only by the block directly below it.
        /// </summary>
        [Test]
        public void BlockPushedUnderFloatingAnchorStillFalls()
        {
            var w = new TestWorld();
            w.Grid.Set(7, 0, 5, Block.Solid);
            var moving = new Int3(7, 1, 5);
            w.Grid.Set(moving, Block.Solid);
            w.Grid.Set(8, 2, 5, Block.Anchor); // floating anchor, e.g. part of a landmark arch
            var sculptor = w.AddPlayer(1, 1, 5, 0, 5);

            var a = SculptAction.Move(moving, Dir.East, TestWorld.FaceCenter(moving, Dir.West));
            Assert.That(w.Sculpt.TryBegin(sculptor, a).IsValid, Is.True);
            w.Step(12 + 40);

            Assert.That(w.At(8, 1, 5).IsEmpty, Is.True, "the pushed block must not hang under the anchor");
            Assert.That(w.At(8, 0, 5).Kind, Is.EqualTo(BlockKind.Solid), "it falls to the ground");
            Assert.That(w.At(8, 2, 5).Kind, Is.EqualTo(BlockKind.Anchor), "the anchor stays put");
        }

        /// <summary>
        /// ISSUE-4 (false red ghost): raising a ramp column while a crouched player stands on the ramp's low
        /// lip is rejected as PlayerBlocked. The lift only considers the flattened old top, then finds the
        /// new ramp in the way, instead of lifting the player onto the new slope like a standing player.
        /// </summary>
        [Test]
        public void RaiseOnRampCarriesCrouchedPlayerOnTheLip()
        {
            var w = new TestWorld();
            var target = new Int3(7, 0, 5);
            w.Grid.Set(target, Block.Ramp(Dir.East)); // low edge at x = 9.6
            var sculptor = w.AddPlayer(1, 1, 5, 0, 5);

            var victim = Crouched(2, 2, 9.6 - 0.05 + 0.35, 6.6);
            Assert.That(Inspect.DropToGround(w.Grid, victim), Is.True);
            w.Sculpt.AddPlayer(victim);
            Vec3d start = victim.Position;

            var a = SculptAction.Raise(target, TestWorld.FaceCenter(target, Dir.West));
            Assert.That(w.Sculpt.Evaluate(sculptor, a).Result, Is.EqualTo(SculptResult.Valid));
            w.Sculpt.TryBegin(sculptor, a);
            w.Step(12);

            Assert.That(Inspect.IsInsideTerrain(w.Grid, victim), Is.False);
            Assert.That(victim.Position.Y - start.Y, Is.EqualTo(B).Within(1e-5), "carried up exactly one level");
        }

        /// <summary>
        /// ISSUE-5 (join feel): on joining at about 90 to 120 ms one way (180 to 240 ms ping) the first lead guess
        /// is 4 to 7 ticks short. That is under the snap threshold (8), so the clock only speeds up by 3%, which takes
        /// 2.5 to 4 s (measured: 100 ms one way settles at ~3 s, 118 ms at ~4 s). During that time every command reaches the server late: the server repeats the last input
        /// on every tick and the client is corrected on every snapshot. Higher pings snap and settle within a second; lower pings are close enough to start.
        /// Expectation: within 1.5 s of joining, inputs arrive in time.
        /// </summary>
        [Ignore("ISSUE-5: clock takes up to 4 s to catch up after a mid-latency join; every input is late until then")]
        [TestCase(90.0)]
        [TestCase(100.0)]
        [TestCase(110.0)]
        [TestCase(120.0)]
        public void JoiningAtMidLatency_InputsArriveInTimeWithinOneAndAHalfSeconds(double oneWayMs)
        {
            var h = Net.NetHarness.Flat();
            var p = h.AddClient(Net.NetHarness.Lag(oneWayMs), Net.NetHarness.Lag(oneWayMs));
            p.Script = Net.Scripts.StraightNorth;
            h.RunSeconds(1.5);
            int missed = p.ServerSide.MissedInputs;
            h.RunSeconds(1.0);
            Assert.That(p.ServerSide.MissedInputs - missed, Is.Zero, "inputs still late 1.5 s after joining");
        }
    }
}
