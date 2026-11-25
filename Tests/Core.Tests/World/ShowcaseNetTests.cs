using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>The F6 showcase played the way the game plays it: through a real client and server.</summary>
    public class ShowcaseNetTests
    {
        internal static (ShowcaseRun run, NetHarness.Peer p, NetHarness h, double maxCorr) PlayNet(double pingMs, double frameMs = 1000.0 / 144.0)
        {
            var brushes0 = BreachMap.BuildBrushes(out BreachMapInfo info);
            NetHarness h = new NetHarness(() => BreachMap.Terrain(BreachMap.BuildGround(), BreachMap.BuildBrushes(out _)),
                info.Course.Start + new Vec3d(3, 0, 0), c => { c.AllowDevWarp = true; c.DevWarpPoint = info.Course.Start; });
            var p = h.AddClient(NetHarness.Lag(pingMs / 2), NetHarness.Lag(pingMs / 2), frameMs);
            h.RunSeconds(1.0);
            ShowcaseRun run = null;
            bool warpSent = false;
            int corrBefore = 0;
            double maxCorr = 0;
            p.Script = t =>
            {
                if (!warpSent) { warpSent = true; p.Client.WarpNext = true; return default; }
                if (run == null) return default;
                return run.Finished ? new MoveInput { Yaw = Math.PI / 2 } : run.Next(p.Client.Predicted);
            };
            p.OnFrame = peer =>
            {
                foreach (var e in peer.Events)
                    if (e.Type == MoveEventType.Respawned && run == null) { run = new ShowcaseRun(info.Course, h.Movement); corrBefore = peer.Client.Corrections; }
                peer.Events.Clear();
                if (run != null && peer.Client.Corrections > corrBefore) maxCorr = Math.Max(maxCorr, peer.Client.LastCorrectionMeters);
            };
            for (int k = 0; k < 40 * 4 && (run == null || !run.Finished); k++) h.RunMs(250);
            return (run, p, h, maxCorr);
        }

        [TestCase(0)]
        [TestCase(60)]
        [TestCase(120)]
        public void ShowcaseFinishesOverTheNetWithoutCorrections(double ping)
        {
            var (run, p, h, maxCorr) = PlayNet(ping);
            Assert.That(run, Is.Not.Null, "warp happened");
            TestContext.Out.WriteLine($"{run.Current} {run.FailReason} ticks {run.Ticks} corrections {p.Client.Corrections} max {maxCorr:0.000} m");
            Assert.That(run.Current, Is.EqualTo(ShowcaseRun.Phase.Done), run.FailReason);
            Assert.That(maxCorr, Is.LessThan(0.01), "no visible corrections during the run");
        }
    }
}
