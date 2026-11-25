using System;
using NUnit.Framework;
using Sculpt.Core.Diagnostics;

namespace Sculpt.Core.Tests.Diagnostics
{
    public class LatencyProbeTests
    {
        /// <summary>
        /// Worked by hand. Click at 100, read at 103, ticked at 108 (tick 500), sent at 109, predicted frame out at 115.
        /// The server's state for tick 500 arrives at 159 saying it held the command 20 ms; that frame is out at 163.
        /// </summary>
        [Test]
        public void OneClick_BreaksDownAsWorkedByHand()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 100, 103);
            Assert.That(p.Pending, Is.EqualTo(1));
            p.Ticked(500, 108);
            p.Sent(109);
            p.FrameOut(115);

            Assert.That(p.HasLocal, Is.True);
            Assert.That(p.HasLast, Is.False, "not answered yet");
            ClickSample l = p.LastLocal;
            Assert.That(l.Kind, Is.EqualTo(ClickKind.Fire));
            Assert.That(l.Tick, Is.EqualTo(500));
            Assert.That(l.InputWaitMs, Is.EqualTo(3));
            Assert.That(l.TickWaitMs, Is.EqualTo(5));
            Assert.That(l.LocalMs, Is.EqualTo(6), "click read to packet sent");
            Assert.That(l.ClickToFrameMs, Is.EqualTo(15));

            Assert.That(p.WaitingFor(out int oldest), Is.True);
            Assert.That(oldest, Is.EqualTo(500));
            p.Answered(500, 20, 159);
            Assert.That(p.WaitingFor(out _), Is.False);
            Assert.That(p.HasLast, Is.False, "the answer is not on a frame yet");
            p.FrameOut(163);

            Assert.That(p.HasLast, Is.True);
            Assert.That(p.Completed, Is.EqualTo(1));
            Assert.That(p.Pending, Is.Zero);
            ClickSample s = p.Last;
            Assert.That(s.RoundTripMs, Is.EqualTo(50));
            Assert.That(s.ServerKnown, Is.True);
            Assert.That(s.ServerMs, Is.EqualTo(20));
            Assert.That(s.NetworkMs, Is.EqualTo(30));
            Assert.That(s.NetUpMs, Is.EqualTo(15));
            Assert.That(s.DownAndDisplayMs, Is.EqualTo(19), "15 down plus 4 to get it on a frame");
            Assert.That(s.TotalMs, Is.EqualTo(63));
            Assert.That(s.InputWaitMs + s.LocalMs + s.NetUpMs + s.ServerMs + s.DownAndDisplayMs, Is.EqualTo(s.TotalMs).Within(1e-9),
                "the five parts shown in the overlay add up to the total");

            Assert.That(p.AvgLocalMs, Is.EqualTo(6));
            Assert.That(p.AvgClickToFrameMs, Is.EqualTo(15));
            Assert.That(p.AvgNetUpMs, Is.EqualTo(15));
            Assert.That(p.AvgServerMs, Is.EqualTo(20));
            Assert.That(p.AvgDownAndDisplayMs, Is.EqualTo(19));
            Assert.That(p.AvgTotalMs, Is.EqualTo(63));
            Assert.That(p.AvgRoundTripMs, Is.EqualTo(50));
        }

        [Test]
        public void UnknownHardwareTime_CountsFromTheRead()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Jump, double.NaN, 50);
            p.Ticked(1, 52);
            p.Sent(52);
            p.FrameOut(56);
            Assert.That(p.LastLocal.InputWaitMs, Is.Zero);
            Assert.That(p.LastLocal.ClickToFrameMs, Is.EqualTo(6));

            p.Press(ClickKind.Jump, 999, 60); // a "hardware" time after the read is impossible: ignored
            p.Ticked(2, 61);
            p.Sent(61);
            p.FrameOut(64);
            Assert.That(p.LastLocal.InputWaitMs, Is.Zero);
        }

        [Test]
        public void AnswerForAnEarlierTick_DoesNotCount()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 10, 10);
            p.Ticked(200, 11);
            p.Sent(12);
            p.FrameOut(15);
            p.Answered(199, 5, 40);
            p.FrameOut(41);
            Assert.That(p.HasLast, Is.False);
            Assert.That(p.Pending, Is.EqualTo(1));
        }

        /// <summary>The snapshot for the click's own tick was lost; a later one answers it but the server's share is unknown.</summary>
        [Test]
        public void AnswerForALaterTick_CompletesWithoutServerTime()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 10, 10);
            p.Ticked(200, 11);
            p.Sent(12);
            p.FrameOut(15);
            p.Answered(201, 7, 72);
            p.FrameOut(75);
            Assert.That(p.HasLast, Is.True);
            Assert.That(p.Last.ServerKnown, Is.False);
            Assert.That(p.Last.RoundTripMs, Is.EqualTo(60));
            Assert.That(p.Last.NetworkMs, Is.EqualTo(60), "with no server share reported, all of it is shown as network");
            Assert.That(p.AvgServerMs, Is.LessThan(0), "no click reported a server time");
        }

        [Test]
        public void AnswerBeforeItWasSent_IsNotCredited()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 10, 10);
            p.Ticked(200, 11);
            p.Answered(200, 3, 11.5); // cannot be an answer to a packet that has not left
            p.Sent(12);
            p.FrameOut(15);
            p.FrameOut(20);
            Assert.That(p.HasLast, Is.False);
            p.Answered(200, 3, 30);
            p.FrameOut(31);
            Assert.That(p.HasLast, Is.True);
        }

        /// <summary>
        /// The game learns of answers one frame late and stamps them with the time that frame's network read happened.
        /// If that is before the packet left, the server simulated the tick from a guess: no round trip, no server share.
        /// </summary>
        [Test]
        public void StateFromBeforeThePacketLeft_GivesNoRoundTrip()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 10, 10);
            p.Ticked(200, 11);
            p.Sent(12);
            p.FrameOut(15);
            p.Answered(200, 9, 10.5); // reported after the send, but it arrived before it
            p.FrameOut(20);
            Assert.That(p.HasLast, Is.True);
            Assert.That(p.Last.RoundTripMs, Is.Zero);
            Assert.That(p.Last.ServerKnown, Is.False);
            Assert.That(p.Last.NetUpMs, Is.Zero);
            Assert.That(p.Last.TotalMs, Is.EqualTo(10));
        }

        [Test]
        public void SecondPressBeforeATick_RidesWithTheFirst()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 10, 11);
            p.Press(ClickKind.Jump, 14, 15); // next frame, still no tick
            p.Ticked(7, 18);
            p.Sent(18);
            p.FrameOut(20);
            Assert.That(p.LastLocal.Kind, Is.EqualTo(ClickKind.Fire));
            Assert.That(p.LastLocal.HardwareMs, Is.EqualTo(10));
            Assert.That(p.Pending, Is.EqualTo(1));
        }

        [Test]
        public void TickWithNoPress_DoesNothing()
        {
            var p = new LatencyProbe();
            p.Ticked(1, 5);
            p.Sent(5);
            p.FrameOut(6);
            p.Answered(1, 2, 30);
            p.FrameOut(31);
            Assert.That(p.HasLocal, Is.False);
            Assert.That(p.HasLast, Is.False);
            Assert.That(p.Pending, Is.Zero);
        }

        [Test]
        public void CancelWaiting_ForgetsThePress()
        {
            var p = new LatencyProbe();
            p.Press(ClickKind.Fire, 1, 2);
            p.CancelWaiting();
            p.Ticked(3, 4);
            Assert.That(p.Pending, Is.Zero);
        }

        [Test]
        public void UnansweredPress_TimesOut()
        {
            var p = new LatencyProbe { TimeoutMs = 1000 };
            p.Press(ClickKind.Fire, 0, 0);
            p.Ticked(1, 1);
            p.Sent(1);
            p.FrameOut(5);
            p.FrameOut(900);
            Assert.That(p.Pending, Is.EqualTo(1));
            p.FrameOut(1001);
            Assert.That(p.Pending, Is.Zero);
            Assert.That(p.TimedOut, Is.EqualTo(1));
            Assert.That(p.HasLast, Is.False);
            Assert.That(p.HasLocal, Is.True, "the local half was still measured");

            p.Press(ClickKind.Fire, 2000, 2000); // never ticked (e.g. the menu opened)
            p.FrameOut(3500);
            Assert.That(p.Pending, Is.Zero);
        }

        [Test]
        public void ManyUnansweredPresses_NeverOverflow()
        {
            var p = new LatencyProbe { TimeoutMs = 1e9 };
            for (int i = 0; i < 100; i++)
            {
                p.Press(ClickKind.Fire, i * 10, i * 10);
                p.Ticked(i, i * 10 + 1);
                p.Sent(i * 10 + 1);
                p.FrameOut(i * 10 + 2);
            }
            Assert.That(p.Pending, Is.EqualTo(16));
            Assert.That(p.TimedOut, Is.EqualTo(84));
            p.Answered(99, 1, 2000);
            p.FrameOut(2001);
            Assert.That(p.Pending, Is.Zero);
            Assert.That(p.Completed, Is.EqualTo(16));
        }

        /// <summary>Worked by hand: 40 clicks whose total is 10 + i ms; the average covers the newest 32 (i = 8..39): 33.5.</summary>
        [Test]
        public void Averages_CoverTheNewest32()
        {
            var p = new LatencyProbe();
            for (int i = 0; i < 40; i++)
            {
                double t = i * 1000;
                p.Press(ClickKind.Fire, t, t);
                p.Ticked(i, t + 1);
                p.Sent(t + 2);
                p.FrameOut(t + 4);
                p.Answered(i, 2, t + 8 + i);
                p.FrameOut(t + 10 + i);
            }
            Assert.That(p.Completed, Is.EqualTo(40));
            Assert.That(p.AvgTotalMs, Is.EqualTo(33.5).Within(1e-9));
            Assert.That(p.AvgClickToFrameMs, Is.EqualTo(4));
            Assert.That(p.AvgServerMs, Is.EqualTo(2));
            Assert.That(p.Last.TotalMs, Is.EqualTo(49));
        }

        /// <summary>Seeded: whatever the timings, the parts shown add up to the total and none is negative.</summary>
        [Test]
        public void Parts_AlwaysAddUp()
        {
            var rng = new Random(77);
            var p = new LatencyProbe();
            double now = 0;
            for (int i = 0; i < 500; i++)
            {
                now += 50 + rng.NextDouble() * 400;
                double hw = now, read = hw + rng.NextDouble() * 8;
                double tick = read + rng.NextDouble() * 17, sent = tick + rng.NextDouble() * 2, drawn = sent + rng.NextDouble() * 9;
                double server = rng.NextDouble() * 40;
                double ack = sent + server + rng.NextDouble() * 150;
                bool lateFrame = ack < drawn;
                p.Press(ClickKind.Fire, hw, read);
                p.Ticked(i, tick);
                p.Sent(sent);
                if (lateFrame) p.Answered(i, server, ack);
                p.FrameOut(drawn);
                if (!lateFrame) p.Answered(i, server, ack);
                double shown = Math.Max(ack, drawn) + rng.NextDouble() * 9;
                if (!lateFrame) p.FrameOut(shown);
                ClickSample s = p.Last;
                Assert.That(p.Completed, Is.EqualTo(i + 1));
                Assert.That(s.InputWaitMs + s.LocalMs + s.NetUpMs + s.ServerMs + s.DownAndDisplayMs, Is.EqualTo(s.TotalMs).Within(1e-6));
                Assert.That(s.InputWaitMs, Is.GreaterThanOrEqualTo(0));
                Assert.That(s.TickWaitMs, Is.GreaterThanOrEqualTo(0));
                Assert.That(s.LocalMs, Is.GreaterThanOrEqualTo(s.TickWaitMs));
                Assert.That(s.NetUpMs, Is.GreaterThanOrEqualTo(0));
                Assert.That(s.DownAndDisplayMs, Is.GreaterThanOrEqualTo(0));
                now = Math.Max(now, s.AckDrawnMs);
            }
        }
    }
}
