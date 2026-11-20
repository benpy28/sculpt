using System;
using NUnit.Framework;
using Sculpt.Core.Net;
namespace Sculpt.Core.Tests.Net
{
    public class NetDebugTests
    {
        [Test, Explicit]
        public void Trace150()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(150), NetHarness.Lag(150));
            for (int i = 0; i < 60; i++)
            {
                h.RunMs(100);
                TestContext.Out.WriteLine($"t={h.NowMs:0} srv={h.Server.Tick} cli={p.Client.Tick} lead={p.Client.LastServerLead} dil={p.Client.Dilation:0.000} snaps={p.Client.ClockSnaps} missed={p.ServerSide.MissedInputs} late={p.ServerSide.LateInputs}");
            }
        }
    }
}
