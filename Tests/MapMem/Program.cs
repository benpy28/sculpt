using System;
using System.Diagnostics;
using Sculpt.Core;

// How much memory one copy of the battle royale map costs (ground and brushes), and how long it takes to build.
//   dotnet run -c Release --project Tests/MapMem
internal static class Program
{
    private static long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

    private static int Main()
    {
        long m0 = Mem();
        var sw = Stopwatch.StartNew();
        SubGrid g = BrMap.BuildGround();
        long m1 = Mem();
        Console.WriteLine($"ground: {(m1 - m0) / 1e6:0.0} MB, {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        BrushWorld w = BrMap.BuildBrushes(out _, out _);
        long ms = sw.ElapsedMilliseconds;
        long m2 = Mem();
        Console.WriteLine($"brushes: {(m2 - m1) / 1e6:0.0} MB, {ms} ms ({w.Count} brushes, {w.Tiles.Count} tiles)");
        var t = new CompositeTerrain(g, w);
        TileWalls.Find(t)?.Prepare();
        long m3 = Mem();
        Console.WriteLine($"prepare: {(m3 - m2) / 1e6:0.0} MB");
        w.Freeze();
        long m3b = Mem();
        Console.WriteLine($"frozen hash: {(m3b - m3) / 1e6:0.0} MB");
        sw.Restart();
        BrushWorld w2 = w.ShareCopy();
        SubGrid g2 = g.ShareCopy();
        long ms2 = sw.ElapsedMilliseconds;
        long m4 = Mem();
        Console.WriteLine($"shared copy (brushes + ground): {(m4 - m3b) / 1e6:0.0} MB, {ms2} ms");
        var rng = new Random(3);
        for (int k = 0; k < 2000; k++)
        {
            int tile = rng.Next(w2.Tiles.Count);
            Aabb bb = w2.Tiles.BoundsOf(tile);
            w2.Tiles.Damage(tile, 50, (bb.Min + bb.Max) * 0.5, TileCause.Bullet, 1, 0.45);
        }
        long m5 = Mem();
        Console.WriteLine($"after 2000 hits all over the map: +{(m5 - m4) / 1e6:0.0} MB");
        GC.KeepAlive(g2);
        GC.KeepAlive(g); GC.KeepAlive(w); GC.KeepAlive(w2); GC.KeepAlive(t);
        return 0;
    }
}
