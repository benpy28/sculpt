using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>Scratch probe (dev only): prints natural and final ground around points. SCULPT_PROBE="u,v;u,v".</summary>
    public class Probe
    {
        [Test]
        public void ProbeGround()
        {
            string spec = Environment.GetEnvironmentVariable("SCULPT_PROBE");
            if (string.IsNullOrEmpty(spec)) Assert.Ignore("set SCULPT_PROBE");
            var sb = new StringBuilder();
            double c = BrMap.Size / 2;
            HeightField f = BrMap.Field;
            foreach (string pt in spec.Split(';'))
            {
                string[] a = pt.Split(',');
                double u = double.Parse(a[0]), v = double.Parse(a[1]);
                sb.AppendLine($"== {u},{v}");
                for (double dv = -3; dv <= 3; dv += 1.5)
                {
                    for (double du = -3; du <= 3; du += 1.5)
                        sb.Append($"{BrGround.At(u + du, v + dv),7:0.00}/{f.HeightAt(c + u + du, c + v + dv),6:0.00} ");
                    sb.AppendLine();
                }
            }
            File.WriteAllText(Environment.GetEnvironmentVariable("SCULPT_PROBE_OUT") ?? "probe.txt", sb.ToString());
        }

        [Test]
        public void ProbeNavBuild()
        {
            string o = Environment.GetEnvironmentVariable("SCULPT_PROBE_NAV");
            if (string.IsNullOrEmpty(o)) Assert.Ignore("set SCULPT_PROBE_NAV");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BotNav nav = BrMap.BuildNav(1.0);
            File.WriteAllText(o, $"{nav.Nodes.Count} spots in {sw.ElapsedMilliseconds} ms: {BotNav.LastBuildTimes}");
        }
    }
}
