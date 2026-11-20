using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Bugs found by the hardening suite, written as the behaviour the spec asks for. While a bug is open its test
    /// is marked Ignore (the reason says why) to keep the suite green. When a fix lands, delete the Ignore line.
    /// ISSUE-1..4 were terrain v1 sculpt bugs (fixed in M2, then superseded by terrain v2 pieces).
    /// </summary>
    [Category("KnownIssue")]
    public class KnownIssueTests
    {
        /// <summary>
        /// ISSUE-5 (join feel, fixed in M3.5): on joining at about 90 to 120 ms one way (180 to 240 ms ping) the first
        /// lead guess was 4 to 7 ticks short, under the snap threshold, so the clock slewed for 2.5 to 4 s with every
        /// command arriving late. Now the client snaps whenever the server reports late inputs.
        /// Expectation: within 1.5 s of joining, inputs arrive in time.
        /// </summary>
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

        /// <summary>
        /// ISSUE-6 (stale shoves on throw): a throw never plans shoves, but its resolution applies whatever the last
        /// shove plan left in the shared scratch list. Any earlier Evaluate (another player's ghost, a rejected confirm,
        /// a different windup) that planned to lift or shove someone gets replayed when the throw lands: that player
        /// teleports to the old planned spot (can be in the air, pulled back, or a fresh position that is now inside rock).
        /// Found by the moving-player piece fuzz (seeds 33, 314220, 523678: players "shoved" down 0.09 to 0.43 m by throws).
        /// Expectation: a throw moves nobody except via its rider kick.
        /// </summary>
        [Test]
        [Ignore("ISSUE-6: throw resolution replays a stale shove plan and teleports bystanders")]
        public void ThrowNeverReplaysSomeoneElsesShovePlan()
        {
            var w = new Pieces.SculptWorld();
            w.Tiles.Set(7, 0, 6, Tile.Solid);                 // the cover tile that gets thrown
            var thrower = w.AddPlayer(1, 1, 5, 0, 6);
            var bystander = w.AddPlayer(2, 2, 10, 0, 12);      // far from the throw
            var ghoster = w.AddPlayer(3, 2, 8, 0, 12);        // only previews a raise under the bystander
            Vec3d start = bystander.Position;

            SculptCheck t = w.Sculpt.TryBegin(thrower, w.Throw(thrower, new Vec3d(8.4, 0.6, 7.8), new Vec3d(0, 0.3, -1)));
            Assert.That(t.Result, Is.EqualTo(SculptResult.Valid), "setup: the throw starts");

            // The ghost: a raise of the ground the bystander stands on would lift them a level. Evaluate only, never confirmed.
            SculptCheck ghost = w.Sculpt.Evaluate(ghoster, w.Raise(ghoster, Pieces.SculptWorld.FeetIn(10, 0, 12)));
            Assert.That(ghost.Result, Is.EqualTo(SculptResult.Valid), "setup: the previewed raise would carry the bystander");

            w.Step(t.WindupTicks + 1);
            Assert.That(w.Sculpt.Thrown.Count + (w.Sculpt.Events.Exists(e => e.Type == SculptEventType.ThrowImpact) ? 1 : 0), Is.GreaterThan(0), "setup: the piece was thrown");
            Assert.That(w.Sculpt.Events.Exists(e => e.Type == SculptEventType.PlayerShoved && e.PlayerId == bystander.Id), Is.False, "no shove event for a bystander");
            Assert.That(bystander.Position, Is.EqualTo(start), "the bystander did not move");
        }
    }
}
