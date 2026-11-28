using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sculpt.Core;
using Sculpt.Core.Diagnostics;
using Sculpt.Core.Net;

// Battle royale server cost on .NET: 32 bots play one match on the real map with the real rules; reports time and
// garbage per server-tick section (Core.Diagnostics.Prof). Not a test: a measuring tool for perf work.
//   dotnet run -c Release --project Tests/BrPerf -- [game seconds, default 120] [solos|duos|squads] [seed]
internal static class Program
{
    private static readonly int N = (int)Prof.Section.Count;
    private static readonly long[] Start = new long[N], StartAlloc = new long[N];
    private static readonly double[] TickMs = new double[N], TotalMs = new double[N], WorstMs = new double[N];
    private static readonly long[] TickAlloc = new long[N], TotalAlloc = new long[N];
    private static readonly List<double>[] PerTick = new List<double>[N];

    private static int Main(string[] args)
    {
        double secs = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 120;
        BrMode mode = args.Length > 1 ? (BrMode)Enum.Parse(typeof(BrMode), args[1], true) : BrMode.Solos;
        int seed = args.Length > 2 ? int.Parse(args[2]) : 5;
        for (int i = 0; i < N; i++) PerTick[i] = new List<double>();
        Prof.BeginHook = i => { Start[i] = Stopwatch.GetTimestamp(); StartAlloc[i] = GC.GetAllocatedBytesForCurrentThread(); };
        Prof.EndHook = i =>
        {
            TickMs[i] += (Stopwatch.GetTimestamp() - Start[i]) * 1000.0 / Stopwatch.Frequency;
            TickAlloc[i] += GC.GetAllocatedBytesForCurrentThread() - StartAlloc[i];
        };

        var load = Stopwatch.StartNew();
        SubGrid g = BrMap.BuildGround();
        BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
        Console.WriteLine($"map {br.Size:0} m: {w.Tiles.Count} tiles, {w.Count} brushes, {br.Chests.Count} chests; built in {load.ElapsedMilliseconds} ms");
        load.Restart();
        BotNav nav = BrMap.BuildNav(1.0);
        Console.WriteLine($"nav {nav.Nodes.Count} spots in {load.ElapsedMilliseconds} ms ({BotNav.LastBuildTimes})");
        var cfg = new BrConfig { Mode = mode, Seed = seed, FillWithoutHumans = true };
        cfg.MakeBot = i => new DuelBot(nav, DuelBotConfig.For((BotLevel)(i % 3)), cfg.Seed + 31 * i);
        // One watching client (a human standing at its pad): its Receive is timed as the ClientReceive section.
        var clientTerrain = BrMap.Terrain(BrMap.BuildGround(), BrMap.BuildBrushes(out _, out _));
        TileWalls.Find(clientTerrain)?.Prepare();
        NetClient client = null;
        var queue = new List<byte[]>();
        var s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { if (c == 1) queue.Add(d); });
        TileWalls.Find(s.Grid)?.Prepare();
        s.EnableBattleRoyale(cfg, br);
        BrSetup.PlaceItems(s, info, br);
        s.StartStream(); // a replay is recorded in the game too
        client = new NetClient(clientTerrain, new MovementConfig(), new NetConfig(), (d, r) => { });
        s.AddPlayer(1, 1, info.TeamSpawns[0][0], 0);
        double clientWorst = 0, clientTotal = 0; int clientWorstTick = 0;

        int ticks = 0, playTicks = 0, playStartTick = -1;
        int maxTicks = (int)(secs * SimTime.TicksPerSecond);
        long gc0 = GC.CollectionCount(0);
        var wall = Stopwatch.StartNew();
        while (ticks < maxTicks)
        {
            Array.Clear(TickMs, 0, N);
            Array.Clear(TickAlloc, 0, N);
            s.Step();
            ticks++;
            var cw = Stopwatch.StartNew();
            foreach (byte[] d in queue) client.Receive(d, 0, d.Length);
            queue.Clear();
            client.Walls?.Changes.Clear();
            double cms = cw.Elapsed.TotalMilliseconds;
            clientTotal += cms;
            if (cms > clientWorst) { clientWorst = cms; clientWorstTick = ticks; }
            {
                int wi = (ticks - 1) / (SimTime.TicksPerSecond / 2);
                while (Timeline.Count <= wi) Timeline.Add(new double[6]);
                double[] tl = Timeline[wi];
                tl[0] = (int)s.Match.Phase;
                tl[1] += TickMs[(int)Prof.Section.ServerTick];
                tl[2] = Math.Max(tl[2], TickMs[(int)Prof.Section.ServerTick]);
                tl[3] += TickMs[(int)Prof.Section.Interest];
                tl[4] += TickMs[(int)Prof.Section.Simulate];
                tl[5] += TickMs[(int)Prof.Section.Bots];
            }
            foreach (CombatEvent ce in s.CombatEvents)
                if (ce.Type == CombatEventType.Kill)
                {
                    ServerPlayer v = null, a = null;
                    foreach (ServerPlayer p in s.Players) { if (p.Id == ce.Victim) v = p; if (p.Id == ce.Attacker) a = p; }
                    {
                        ServerPlayer v2 = null, a2 = null;
                        foreach (ServerPlayer p in s.Players) { if (p.Id == ce.Victim) v2 = p; if (p.Id == ce.Attacker) a2 = p; }
                        if (a2 != null && v2 != null && a2 != v2) KillDist.Add((a2.State.Position - v2.State.Position).Length);
                    }
                    Deaths.Add($"{(ticks - playStartTick) / (double)SimTime.TicksPerSecond,6:0.0}s {s.Match.Phase,-9} {ce.Source,-8} victim {ce.Victim} attacker {ce.Attacker}" +
                        (a != null && v != null ? $" dist {(a.State.Position - v.State.Position).Length:0.0} m" : "") +
                        (v != null && (ticks - playStartTick) < 30 * SimTime.TicksPerSecond ? $" at {v.State.Position} (pad {v.Spawn}{(a != null ? $", attacker's pad {a.Spawn}" : "")})" : ""));
                }
            if (s.Match.Phase == MatchPhase.Playing && playStartTick < 0) playStartTick = ticks;
            if (playStartTick >= 0 && s.Match.Phase == MatchPhase.Playing)
                foreach (ServerPlayer p in s.Players)
                {
                    if (!s.InBrMatch(p.Id)) continue;
                    bool was = WasDead.Contains(p.Id);
                    if (!was && p.State.Dead)
                    {
                        WasDead.Add(p.Id);
                        bool killed = false;
                        foreach (CombatEvent ce in s.CombatEvents) if (ce.Type == CombatEventType.Kill && ce.Victim == p.Id && ce.Attacker != 0) killed = true;
                        if (!killed)
                        {
                            StormState st = s.Match.Storm;
                            double dx = p.State.Position.X - st.CentreXAt(s.Tick), dz = p.State.Position.Z - st.CentreZAt(s.Tick);
                            LastPos.TryGetValue(p.Id, out Vec3d old);
                            OtherDeaths.Add($"{(ticks - playStartTick) / (double)SimTime.TicksPerSecond,6:0.0}s player {p.Id} {(Sculpt.Core.Net.NetServer.IsBotConnection(p.ConnectionId) ? "bot" : "human")} at {p.State.Position} {Math.Sqrt(dx * dx + dz * dz) - st.RadiusAt(s.Tick):0} m outside the circle (phase {st.Phase + 1}); moved {(p.State.Position - old).HorizontalLength:0} m in the last 10 s");
                        }
                    }
                    if ((ticks - playStartTick) % (10 * SimTime.TicksPerSecond) == 0) LastPos[p.Id] = p.State.Position;
                }
            if (playStartTick >= 0 && s.Match.Phase == MatchPhase.Playing && (ticks - playStartTick) % (30 * SimTime.TicksPerSecond) == 0)
            {
                int inMatch = 0, live = 0;
                foreach (ServerPlayer p in s.Players) if (s.InBrMatch(p.Id)) { inMatch++; if (!p.State.Dead) live++; }
                StormState st = s.Match.Storm;
                Alive.Add($"{(ticks - playStartTick) / SimTime.TicksPerSecond,5}s alive {live,2}/{inMatch}  circle phase {st.Phase + 1} radius {st.RadiusAt(s.Tick),5:0}");
            }
            if (s.Match.Phase != MatchPhase.Playing) { if (playTicks > 0 && s.Match.Phase == MatchPhase.Ended) break; continue; }
            playTicks++;
            for (int i = 0; i < N; i++)
            {
                TotalMs[i] += TickMs[i];
                TotalAlloc[i] += TickAlloc[i];
                WorstMs[i] = Math.Max(WorstMs[i], TickMs[i]);
                PerTick[i].Add(TickMs[i]);
            }
        }
        int alive = 0;
        foreach (ServerPlayer p in s.Players) if (!p.State.Dead) alive++;
        Console.WriteLine($"{mode} seed {seed}: {playTicks / (double)SimTime.TicksPerSecond:0.0} s of play ({ticks} ticks) in {wall.Elapsed.TotalSeconds:0.0} s; {alive} alive; gen0 collections {GC.CollectionCount(0) - gc0}");
        Console.WriteLine($"client (joined {client.Joined}, {client.RemoteCount} remotes) receive: avg {clientTotal / Math.Max(1, ticks):0.000} ms/tick, worst {clientWorst:0.00} ms at tick {clientWorstTick}");
        Console.WriteLine("section       avg ms/tick   p99     p99.9   worst   KB/tick");
        for (int i = 0; i < N; i++)
        {
            if (i == (int)Prof.Section.ClientReceive) continue;
            List<double> l = PerTick[i];
            l.Sort();
            double P(double q) => l.Count == 0 ? 0 : l[Math.Min(l.Count - 1, (int)(l.Count * q))];
            Console.WriteLine($"{(Prof.Section)i,-13} {TotalMs[i] / Math.Max(1, playTicks),9:0.000} {P(0.99),8:0.00} {P(0.999),8:0.00} {WorstMs[i],8:0.00} {TotalAlloc[i] / 1024.0 / Math.Max(1, playTicks),8:0.00}");
        }
        Console.WriteLine($"deaths ({Deaths.Count}), seconds after play starts:");
        foreach (string d in Deaths.GetRange(0, Math.Min(40, Deaths.Count))) Console.WriteLine("  " + d);
        Console.WriteLine($"deaths with no killer ({OtherDeaths.Count}):");
        foreach (string d in OtherDeaths) Console.WriteLine("  " + d);
        KillDist.Sort();
        double Q(double q) => KillDist.Count == 0 ? 0 : KillDist[Math.Min(KillDist.Count - 1, (int)(KillDist.Count * q))];
        Console.WriteLine($"kills by a player: {KillDist.Count}, distance median {Q(0.5):0} m, 25% {Q(0.25):0} m, 75% {Q(0.75):0} m, 90% {Q(0.9):0} m");
        int covers = 0, rotations = 0, thirds = 0;
        foreach (var kv in s.Bots) if (kv.Value is DuelBot b) { covers += b.CoverCount; rotations += b.RotateCount; thirds += b.ThirdPartyCount; }
        Console.WriteLine($"bot tactics: cover {covers}, rotations {rotations}, third-party {thirds}");
        Console.WriteLine("alive every 30 s:");
        foreach (string a in Alive) Console.WriteLine("  " + a);
        Console.WriteLine("timeline (half seconds: phase, server ms avg/worst per tick, interest/simulate/bots avg):");
        int per = SimTime.TicksPerSecond / 2;
        for (int i = 0; i < Math.Min(Timeline.Count, 60); i++)
        {
            double[] t = Timeline[i];
            Console.WriteLine($"  {i * 0.5,5:0.0}s {(MatchPhase)(int)t[0],-10} {t[1] / per,6:0.00} / {t[2],6:0.00}   {t[3] / per,5:0.00} {t[4] / per,5:0.00} {t[5] / per,5:0.00}");
        }
        return 0;
    }

    private static readonly List<double[]> Timeline = new List<double[]>();
    private static readonly List<string> Deaths = new List<string>();
    private static readonly List<double> KillDist = new List<double>();
    private static readonly List<string> Alive = new List<string>();
    private static readonly List<string> OtherDeaths = new List<string>();
    private static readonly HashSet<int> WasDead = new HashSet<int>();
    private static readonly Dictionary<int, Vec3d> LastPos = new Dictionary<int, Vec3d>();
}
