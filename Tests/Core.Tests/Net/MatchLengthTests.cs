using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// The 1v1 HUD showed a hard-coded "5:00" and "Most kills in 5 minutes": the client was never told the match length.
    /// The Match message now carries how long Playing lasts (<see cref="MatchState.PlayTicks"/>).
    /// </summary>
    public class MatchLengthTests
    {
        [Test]
        public void TheMatchLength_RoundTrips()
        {
            foreach (BrMode mode in new[] { BrMode.None, BrMode.Squads })
            {
                var m = new MatchState { Phase = MatchPhase.Countdown, PhaseStartTick = 100, PhaseEndTick = 460, PlayTicks = 3 * 60 * SimTime.TicksPerSecond, Mode = mode, TeamSize = mode == BrMode.None ? 1 : 4 };
                m.Scores.Add(new MatchScore { PlayerId = 1, Team = 1, Kills = 2 });
                var w = new ByteWriter(64);
                MatchProtocol.Write(w, m, 120);
                byte[] data = w.ToArray();
                var r = new ByteReader(data, 0, data.Length);
                var b = new MatchState();
                MatchProtocol.Read(ref r, b);
                Assert.That(b.PlayTicks, Is.EqualTo(3 * 60 * SimTime.TicksPerSecond));
                Assert.That(b.PhaseEndTick, Is.EqualTo(460));
                Assert.That(b.Scores[0].Kills, Is.EqualTo(2));
                var c = new MatchState();
                c.CopyFrom(b);
                Assert.That(c.PlayTicks, Is.EqualTo(b.PlayTicks));
            }
        }

        [Test]
        public void Junk_OnlyEverThrowsNetFormatException_AndANegativeLengthIsRefused()
        {
            var m = new MatchState { Phase = MatchPhase.Playing, PlayTicks = 5 * 60 * SimTime.TicksPerSecond };
            var w = new ByteWriter(64);
            MatchProtocol.Write(w, m, 9);
            byte[] data = w.ToArray();
            // A negative length (tick 1..4: type, sent tick, phase, start, end, then the length).
            byte[] neg = (byte[])data.Clone();
            int at = 1 + 4 + 1 + 4 + 4;
            neg[at] = 0xFF; neg[at + 1] = 0xFF; neg[at + 2] = 0xFF; neg[at + 3] = 0xFF;
            Assert.Throws<NetFormatException>(() => { var rr = new ByteReader(neg, 0, neg.Length); MatchProtocol.Read(ref rr, new MatchState()); });
            var rng = new System.Random(5);
            for (int i = 0; i < 4000; i++)
            {
                byte[] bad = (byte[])data.Clone();
                int len = rng.Next(4) == 0 ? rng.Next(bad.Length) : bad.Length;
                bad[rng.Next(bad.Length)] ^= (byte)(1 + rng.Next(255));
                try
                {
                    var rr = new ByteReader(bad, 0, len);
                    var s = new MatchState();
                    MatchProtocol.Read(ref rr, s);
                    Assert.That(s.PlayTicks, Is.GreaterThanOrEqualTo(0));
                }
                catch (NetFormatException) { }
            }
        }

        [Test]
        public void TheServer_TellsClientsItsConfiguredLength()
        {
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            });
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            h.Server.MatchConfig.DurationTicks = 7 * 60 * SimTime.TicksPerSecond;
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            h.RunSeconds(0.3);
            Assert.That(a.Client.Match.Phase, Is.EqualTo(MatchPhase.Waiting));
            Assert.That(a.Client.Match.PlayTicks, Is.EqualTo(7 * 60 * SimTime.TicksPerSecond), "known before the countdown starts");
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            h.RunSeconds(0.2);
            Assert.That(b.Client.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            Assert.That(b.Client.Match.PlayTicks, Is.EqualTo(7 * 60 * SimTime.TicksPerSecond));
        }
    }
}
