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
    }
}
