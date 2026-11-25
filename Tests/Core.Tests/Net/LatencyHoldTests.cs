using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Diagnostics;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>The server tells each client how long its command waited (latency overlay). Nothing in the simulation reads it.</summary>
    public class LatencyHoldTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1234)]
        [TestCase(65534)]
        public void Hold_RoundTripsOnTheWire(int tenths)
        {
            var w = new ByteWriter();
            Protocol.Write(w, new SnapshotMsg { ServerTick = 5, HasSelf = true, Self = MotorState.At(new Vec3d(1, 2, 3)), SelfHoldTenthMs = tenths, Others = new List<PlayerSnap>() });
            var r = new ByteReader(w.Buffer, 0, w.Length);
            SnapshotMsg back = Protocol.ReadSnapshot(ref r, new List<PlayerSnap>());
            Assert.That(back.SelfHoldTenthMs, Is.EqualTo(tenths));
            Assert.That(r.Remaining, Is.Zero);
        }

        [TestCase(-1)]
        [TestCase(65535)]
        [TestCase(900000)]
        public void Hold_OutOfRange_IsSentAsUnknown(int tenths)
        {
            var w = new ByteWriter();
            Protocol.Write(w, new SnapshotMsg { ServerTick = 5, HasSelf = true, Self = MotorState.At(new Vec3d(1, 2, 3)), SelfHoldTenthMs = tenths, Others = new List<PlayerSnap>() });
            var r = new ByteReader(w.Buffer, 0, w.Length);
            Assert.That(Protocol.ReadSnapshot(ref r, new List<PlayerSnap>()).SelfHoldTenthMs, Is.EqualTo(Protocol.NoHold));
        }

        [Test]
        public void WithoutAClock_NothingIsReported()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20));
            h.RunSeconds(2);
            Assert.That(p.Client.LastSnapshotTick, Is.GreaterThan(0));
            Assert.That(p.Client.ServerHoldMs(p.Client.LastSnapshotTick), Is.LessThan(0));
        }

        /// <summary>
        /// With a clock, every snapshot says how long the command for its tick waited. The client aims to be
        /// TargetLeadTicks early, so the wait is that lead give or take the clock's dead band: more than nothing, and
        /// well under the lead the server would refuse.
        /// </summary>
        [TestCase(0.0)]
        [TestCase(20.0)]
        [TestCase(60.0)]
        public void WithAClock_HoldIsAboutTheInputLead(double oneWayMs)
        {
            var h = NetHarness.Flat();
            h.Server.Clock = () => h.NowMs;
            var p = h.AddClient(NetHarness.Lag(oneWayMs), NetHarness.Lag(oneWayMs));
            p.Script = Scripts.Wander;
            h.RunSeconds(4); // let the clock settle
            int known = 0, seen = 0;
            double min = double.MaxValue, max = 0, sum = 0;
            int last = p.Client.LastSnapshotTick;
            p.OnFrame = peer =>
            {
                int t = peer.Client.LastSnapshotTick;
                if (t == last) return;
                last = t;
                seen++;
                double hold = peer.Client.ServerHoldMs(t);
                if (hold < 0) return;
                known++;
                sum += hold;
                if (hold < min) min = hold;
                if (hold > max) max = hold;
            };
            h.RunSeconds(3);
            Assert.That(seen, Is.GreaterThan(100));
            Assert.That(known, Is.GreaterThan(seen * 0.95), "nearly every tick had the real command and so a hold time");
            double tick = NetHarness.TickMs;
            Assert.That(min, Is.GreaterThanOrEqualTo(0));
            Assert.That(sum / known, Is.InRange(0.5 * tick, (h.Config.TargetLeadTicks + 4) * tick), $"average hold {sum / known:0.0} ms");
            Assert.That(max, Is.LessThan(h.Config.MaxCommandLeadTicks * tick));
        }

        /// <summary>Worked by hand: a command that arrives 25 ms before the server sends its tick's snapshot is held 25.0 ms.</summary>
        [Test]
        public void Hold_IsArrivalToSnapshot_OnTheServersClock()
        {
            double now = 1000;
            var sent = new List<byte[]>();
            var cfg = new NetConfig();
            var grid = new VoxelGrid(2, 2, 2, -16);
            grid.FillBaseGround();
            var server = new NetServer(grid, new MovementConfig(), cfg, (c, d, r) => { if (!r) sent.Add(d); }, startTick: 100) { Clock = () => now };
            server.AddPlayer(1, 1, TileMap.FeetIn(8, 0, 8), 0);

            var w = new ByteWriter();
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = 0, Cmds = new List<InputCmd> { InputCmd.Quantize(new InputCmd { Tick = 102 }) } });
            server.Receive(1, w.ToArray(), 0, w.Length);   // arrives at 1000 for tick 102

            now = 1010;
            server.Step();                                  // tick 101: no command for it, nothing to report
            Assert.That(HoldIn(sent[sent.Count - 1]), Is.EqualTo(Protocol.NoHold));
            now = 1025;
            server.Step();                                  // tick 102: held from 1000 to 1025
            Assert.That(HoldIn(sent[sent.Count - 1]), Is.EqualTo(250));
            now = 1040;
            server.Step();                                  // tick 103: the last command repeated, no arrival to measure from
            Assert.That(HoldIn(sent[sent.Count - 1]), Is.EqualTo(Protocol.NoHold));
        }

        /// <summary>A redundant copy of a command arriving later must not move its arrival time.</summary>
        [Test]
        public void RedundantCopies_KeepTheFirstArrival()
        {
            double now = 0;
            var sent = new List<byte[]>();
            var grid = new VoxelGrid(2, 2, 2, -16);
            grid.FillBaseGround();
            var server = new NetServer(grid, new MovementConfig(), new NetConfig(), (c, d, r) => { if (!r) sent.Add(d); }, startTick: 100) { Clock = () => now };
            server.AddPlayer(1, 1, TileMap.FeetIn(8, 0, 8), 0);
            var w = new ByteWriter();
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = 0, Cmds = new List<InputCmd> { InputCmd.Quantize(new InputCmd { Tick = 101 }) } });
            server.Receive(1, w.ToArray(), 0, w.Length);
            now = 9;
            server.Receive(1, w.ToArray(), 0, w.Length);
            now = 12;
            server.Step();
            Assert.That(HoldIn(sent[sent.Count - 1]), Is.EqualTo(120));
        }

        private static int HoldIn(byte[] snapshot)
        {
            var r = new ByteReader(snapshot, 0, snapshot.Length);
            return Protocol.ReadSnapshot(ref r, new List<PlayerSnap>()).SelfHoldTenthMs;
        }

        /// <summary>End to end through the probe: a click's parts add up and the server share is the reported hold.</summary>
        [Test]
        public void Probe_OverTheHarness_AddsUp()
        {
            var h = NetHarness.Flat();
            h.Server.Clock = () => h.NowMs;
            var p = h.AddClient(NetHarness.Lag(30), NetHarness.Lag(30));
            h.RunSeconds(3);
            var probe = new LatencyProbe();
            int lastSnap = p.Client.LastSnapshotTick, lastTick = p.Client.Tick, frame = 0;
            p.OnFrame = peer =>
            {
                // Frame order in the game: the last frame went out, snapshots are read, input is read, ticks run, packets leave.
                probe.FrameOut(h.NowMs);
                int snap = peer.Client.LastSnapshotTick;
                if (snap != lastSnap)
                {
                    lastSnap = snap;
                    if (probe.WaitingFor(out int want) && snap >= want) probe.Answered(want <= snap ? want : snap, peer.Client.ServerHoldMs(want), h.NowMs);
                }
                if (peer.Client.Tick != lastTick)
                {
                    lastTick = peer.Client.Tick;
                    probe.Ticked(lastTick, h.NowMs);
                    probe.Sent(h.NowMs);
                }
                if (++frame % 40 == 0) probe.Press(ClickKind.Fire, h.NowMs - 1, h.NowMs);
            };
            h.RunSeconds(4);
            Assert.That(probe.Completed, Is.GreaterThan(8));
            Assert.That(probe.TimedOut, Is.Zero);
            ClickSample s = probe.Last;
            Assert.That(s.ServerKnown, Is.True);
            Assert.That(s.RoundTripMs, Is.GreaterThanOrEqualTo(60), "two 30 ms legs at least");
            Assert.That(s.RoundTripMs, Is.LessThan(60 + 8 * NetHarness.TickMs));
            Assert.That(s.NetUpMs, Is.EqualTo(30).Within(NetHarness.TickMs), "each leg is 30 ms; frame and tick stepping blur it by under a tick");
            Assert.That(s.InputWaitMs + s.LocalMs + s.NetUpMs + s.ServerMs + s.DownAndDisplayMs, Is.EqualTo(s.TotalMs).Within(1e-6));
        }
    }
}
