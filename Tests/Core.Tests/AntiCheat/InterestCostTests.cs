using System;
using System.Diagnostics;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.AntiCheat
{
    public class InterestCostTests
    {
        /// <summary>Runs in a wobbly circle, jumping now and then (a stand-in player for size tests).</summary>
        internal sealed class Runner : IBotBrain
        {
            private readonly int _k;
            public bool Still;
            public Runner(int k, bool still = false) { _k = k; Still = still; }
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me)
            {
                int t = server.Tick;
                var c = new InputCmd { Tick = t, ViewTick = t, Pitch = (float)(10 * Math.Sin(t * 0.01 + _k)) };
                if (!Still)
                {
                    c.Move.MoveZ = 1;
                    c.Move.SprintHeld = true;
                    c.Move.JumpPressed = (t + _k * 37) % 240 == 0;
                    c.Move.JumpHeld = c.Move.JumpPressed;
                }
                c.Move.Yaw = Math.IEEERemainder(t * 0.012 + _k, 2 * Math.PI);
                return InputCmd.Quantize(c);
            }
        }

        internal static NetServer Duel32(bool interest, bool still, out BreachMapInfo info)
        {
            var nc = new NetConfig();
            nc.AntiCheat.Interest.Enabled = interest;
            var s = new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out info)), new MovementConfig(), nc, (c, d, r) => { });
            for (int i = 0; i < 32; i++)
            {
                var spots = info.TeamSpawns[i % 2];
                s.AddBot(new Runner(i, still), 1 + i % 2, spots[i / 2 % spots.Count] + new Vec3d((i / 2) * 0.9, 0, 0), 0);
            }
            return s;
        }

        [Test]
        public void ThirtyTwoPlayers_ServerCostPerTick()
        {
            foreach (bool still in new[] { false, true })
            {
                double off = Cost(false, still, out _), on = Cost(true, still, out NetServer s);
                TestContext.WriteLine($"32 players {(still ? "standing still" : "running")}: {off:0.000} ms/tick without interest, {on:0.000} with ({on - off:0.000} ms), {s.InterestTests / (double)(s.Tick - 1000):0.0} pair tests and {s.InterestRays / (double)(s.Tick - 1000):0} rays per tick");
            }
        }

        private static double Cost(bool interest, bool still, out NetServer s)
        {
            s = Duel32(interest, still, out _);
            for (int i = 0; i < 120; i++) s.Step();
            var sw = Stopwatch.StartNew();
            int n = 5 * SimTime.TicksPerSecond;
            for (int i = 0; i < n; i++) s.Step();
            return sw.Elapsed.TotalMilliseconds / n;
        }
    }
}
