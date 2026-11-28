using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sculpt.Core;
using Sculpt.Core.Diagnostics;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;

// Re-runs a recorded battle royale (a .sclr from the game's Replays folder) on .NET and times every server tick by
// section, so a slow tick someone saw in play can be found again exactly:
//   dotnet run -c Release --project Tests/BrPerf -- replay <file.sclr> [ms threshold, default 6] [--stream]
// --stream also records a fresh match stream and replay file while re-running (as the game does), so its cost shows.
internal static class ReplayRun
{
    private static readonly int N = (int)Prof.Section.Count;

    public static int Run(string path, double threshold, bool stream)
    {
        ReplayFile file = ReplayFile.Read(File.ReadAllBytes(path));
        Console.WriteLine($"{Path.GetFileName(path)}: {file.Header.Map} {file.Header.Mode}, ticks {file.FirstTick}..{file.LastTick} ({(file.LastTick - file.FirstTick) / (double)SimTime.TicksPerSecond:0} s)");
        BrMode mode = file.Header.Mode != null && file.Header.Mode.Contains("Squads") ? BrMode.Squads
            : file.Header.Mode != null && file.Header.Mode.Contains("Duos") ? BrMode.Duos : BrMode.Solos;

        var start = new long[N];
        var tick = new double[N];
        Prof.BeginHook = i => start[i] = Stopwatch.GetTimestamp();
        NetServer server = null;
        var slow = new List<(int Tick, double[] Ms)>();
        var all = new List<double>();
        Prof.EndHook = i =>
        {
            tick[i] += (Stopwatch.GetTimestamp() - start[i]) * 1000.0 / Stopwatch.Frequency;
            if (i != (int)Prof.Section.ServerTick) return;
            double total = tick[i];
            all.Add(total);
            if (total >= threshold)
            {
                slow.Add((server.Tick, (double[])tick.Clone()));
                var zl = new System.Text.StringBuilder();
                Console.WriteLine($"  tick {server.Tick}: {total:0.00} ms{zl}");
            }
            Array.Clear(tick, 0, N);
        };
        ReplayWriter writer = null;
        var sw = Stopwatch.StartNew();
        ReplayCheck check = ReplayVerifier.Run(file, () =>
        {
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            var cfg = new BrConfig { Mode = mode, Seed = 0 };
            server = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            TileWalls.Find(server.Grid)?.Prepare();
            server.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(server, info, br);
            server.PrepareSight();
            Console.WriteLine($"map and server built in {sw.ElapsedMilliseconds} ms");
            if (stream)
            {
                MatchStream ms = server.StartStream();
                writer = new ReplayWriter(new MemoryStream(), new ReplayHeader { Map = "BattleRoyale", Mode = mode.ToString(), Build = "brperf", StartedUtcTicks = 1 }, ms);
            }
            return server;
        });
        Console.WriteLine(check.ToString());
        all.Sort();
        double P(double q) => all.Count == 0 ? 0 : all[Math.Min(all.Count - 1, (int)(all.Count * q))];
        double sum = 0; foreach (double d in all) sum += d;
        Console.WriteLine($"{all.Count} ticks: avg {sum / Math.Max(1, all.Count):0.000} ms, p99 {P(0.99):0.00}, p99.9 {P(0.999):0.00}, worst {P(1):0.00}; {slow.Count} ticks over {threshold} ms");
        Console.Write("  tick    secs   total");
        for (int i = 1; i < N; i++) if (i < (int)Prof.Section.ClientReceive) Console.Write($" {(Prof.Section)i,9}");
        Console.WriteLine();
        foreach ((int t, double[] ms) in slow)
        {
            Console.Write($"{t,7} {(t - file.FirstTick) / (double)SimTime.TicksPerSecond,7:0.00} {ms[0],7:0.00}");
            for (int i = 1; i < (int)Prof.Section.ClientReceive; i++) Console.Write($" {ms[i],9:0.00}");
            Console.WriteLine();
        }
        return 0;
    }
}
