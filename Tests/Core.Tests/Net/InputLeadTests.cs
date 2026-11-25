using System;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// How early a client's commands reach the server is pure delay for everything the server confirms (hits, kills,
    /// walls breaking) and for what other players see of you. The clock must settle on the target lead, not on
    /// whatever larger value it happened to start from: the same latency every session.
    /// </summary>
    public class InputLeadTests
    {
        /// <summary>
        /// On a clean link the worst lead the server reports settles at the target or one tick over (one tick is what
        /// a whole-tick report can resolve), from any ping and frame rate. Before 2026-11-25 a 2 tick dead band let
        /// it rest anywhere up to target + 2, which is 33 ms of extra delay at 60 Hz.
        /// </summary>
        [TestCase(0.0, 60.0, 1)]
        [TestCase(0.0, 144.0, 2)]
        [TestCase(0.0, 500.0, 3)]
        [TestCase(10.0, 240.0, 4)]
        [TestCase(25.0, 144.0, 5)]
        [TestCase(60.0, 97.0, 6)]
        [TestCase(100.0, 61.0, 7)]
        public void Lead_SettlesOnTheTarget(double oneWayMs, double fps, int seed)
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(oneWayMs), NetHarness.Lag(oneWayMs), 1000.0 / fps, seed);
            p.Script = Scripts.Wander;
            h.RunSeconds(14); // the first guess is deliberately early; let the clock walk back
            int target = h.Config.TargetLeadTicks;
            int min = int.MaxValue, max = int.MinValue, samples = 0;
            p.OnFrame = peer =>
            {
                int lead = peer.Client.LastServerLead;
                if (lead == Protocol.NoLead) return;
                samples++;
                if (lead < min) min = lead;
                if (lead > max) max = lead;
            };
            int missed = p.ServerSide.MissedInputs;
            h.RunSeconds(6);
            Assert.That(samples, Is.GreaterThan(100));
            Assert.That(min, Is.GreaterThanOrEqualTo(target), "never later than the target");
            Assert.That(max, Is.LessThanOrEqualTo(target + 1), $"lead rested between {min} and {max} ticks");
            Assert.That(p.ServerSide.MissedInputs - missed, Is.Zero, "and no tick ran without the real command");
        }

        /// <summary>The server's hold time (what the latency overlay shows) is then about the target lead, every session.</summary>
        [Test]
        public void Hold_IsTheSameEverySession()
        {
            double lo = double.MaxValue, hi = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                var h = NetHarness.Flat();
                h.Server.Clock = () => h.NowMs;
                // Sessions differ in frame rate and in when they joined relative to the server's tick.
                h.RunMs(seed * 3.7);
                var p = h.AddClient(NetHarness.Lag(15), NetHarness.Lag(15), 1000.0 / (90 + seed * 37), seed);
                p.Script = Scripts.Wander;
                h.RunSeconds(14);
                double sum = 0;
                int n = 0, last = p.Client.LastSnapshotTick;
                p.OnFrame = peer =>
                {
                    int t = peer.Client.LastSnapshotTick;
                    if (t == last) return;
                    last = t;
                    double hold = peer.Client.ServerHoldMs(t);
                    if (hold < 0) return;
                    sum += hold;
                    n++;
                };
                h.RunSeconds(4);
                Assert.That(n, Is.GreaterThan(100));
                double avg = sum / n;
                lo = Math.Min(lo, avg);
                hi = Math.Max(hi, avg);
            }
            double tick = NetHarness.TickMs;
            Assert.That(hi - lo, Is.LessThan(1.25 * tick), $"average hold ranged from {lo:0.0} to {hi:0.0} ms across sessions");
            Assert.That(hi, Is.LessThan((new NetConfig().TargetLeadTicks + 1.5) * tick), $"highest average hold {hi:0.0} ms");
        }

        /// <summary>A rough link still gets the slack it needs: the clock follows the worst lead, it does not chase the average.</summary>
        [Test]
        public void JitterAndLoss_KeepTheirSlack()
        {
            var h = NetHarness.M2Map(out _);
            var p = h.AddClient(NetHarness.Lag(40, 12, 3), NetHarness.Lag(40, 12, 3), 1000.0 / 144.0, seed: 9);
            p.Script = Scripts.Wander;
            h.RunSeconds(12);
            int missed = p.ServerSide.MissedInputs, snaps = p.Client.ClockSnaps;
            int ticks = h.Server.Tick;
            h.RunSeconds(20);
            ticks = h.Server.Tick - ticks;
            Assert.That(p.ServerSide.MissedInputs - missed, Is.LessThan(ticks * 0.02), "under 2% of ticks without the real command");
            Assert.That(p.Client.ClockSnaps - snaps, Is.LessThanOrEqualTo(2), "and the clock is not jumping about");
        }
    }
}
