using NUnit.Framework;
namespace Sculpt.Core.Tests
{
    public class BrNavProbe
    {
        [Test, Explicit]
        public void Time()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BotNav n = BrMap.BuildNav();
            TestContext.WriteLine($"nav 0.75: {n.Nodes.Count} nodes {n.EdgeCount} edges in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            BotNav n2 = BrMap.BuildNav(1.0);
            TestContext.WriteLine($"nav 1.0: {n2.Nodes.Count} nodes {n2.EdgeCount} edges in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            var path = new System.Collections.Generic.List<int>();
            int a = n.Nearest(new Vec3d(270, 0, 220)), b = n.Nearest(new Vec3d(153, 0, 153));
            bool ok = n.FindPath(a, b, path);
            TestContext.WriteLine($"path {ok} {path.Count} in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            var sg = new Sculpt.Core.Audio.SoundGrid(BrMap.Terrain(BrMap.BuildGround(), BrMap.BuildBrushes(out _, out _)));
            TestContext.WriteLine($"sound grid in {sw.ElapsedMilliseconds} ms");
        }
    }
}
