using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sculpt.Core;
using Sculpt.Core.Diagnostics;
using Sculpt.Core.Net;

// Server cost while walls get blown into big holes, on the real battle royale map: a few players stand in front of a
// building's wall, sweep their aim across it and fire the pump (one-shots plain wall), throw grenades into it, then
// walk through what is left. Times every server tick by section.
//   dotnet run -c Release --project Tests/BrPerf -- holes [players, default 6] [seconds, default 30] [ms threshold, default 3]
internal static class HoleRun
{
    private static readonly int N = (int)Prof.Section.Count;

    private sealed class Wrecker : IBotBrain
    {
        public Vec3d Wall;     // a point on the wall's face
        public Vec3d Out;      // the wall's outward normal (toward where we stand)
        public int Seed;
        public bool CountsAsPlayer => true;

        public InputCmd Next(NetServer s, ServerPlayer me)
        {
            int t = s.Tick + Seed * 37;
            double phase = t / (double)SimTime.TicksPerSecond;
            bool walkIn = phase % 20 > 14; // every 20 s: 6 s walking into the holes
            Vec3d along = new Vec3d(Out.Z, 0, -Out.X);
            // Aim at a point that wanders across the wall (6 m either side, 0.3-4 m up).
            Vec3d aim = Wall + along * (6 * Math.Sin(phase * 0.7 + Seed)) + new Vec3d(0, 2 + 1.8 * Math.Sin(phase * 1.3 + Seed * 2), 0);
            Vec3d eye = me.State.Position + new Vec3d(0, 1.62, 0);
            Vec3d d = aim - eye;
            double yaw = Math.Atan2(d.X, d.Z);
            double pitch = -Math.Atan2(d.Y, Math.Sqrt(d.X * d.X + d.Z * d.Z)) * 180 / Math.PI;
            bool grenadeTime = Seed % 3 == 0 && (t / SimTime.TicksPerSecond) % 4 == 0;
            byte slot = (byte)(grenadeTime ? 5 : 1);
            var c = new InputCmd { Tick = s.Tick, ViewTick = s.Tick - 3, Pitch = (float)pitch };
            c.Move.Yaw = yaw;
            if (walkIn) { c.Move.MoveZ = 1; c.Move.MoveX = Math.Sin(phase * 2 + Seed) * 0.5; }
            else c.Move.MoveX = Math.Sin(phase * 0.9 + Seed) > 0 ? 0.6 : -0.6;
            c.Hand.WeaponKey = slot;
            c.Move.FireHeld = !NoShoot && (!walkIn || t % 30 < 15);
            c.Move.FirePressed = c.Move.FireHeld && t % 6 == 0;
            c.Move.WantsToShoot = c.Move.FireHeld;
            if (t % (SimTime.TicksPerSecond * 7) == 0) c.Hand.Reload = true;
            return c;
        }
    }

    public static int Run(int players, double secs, double threshold)
    {
        var start = new long[N];
        var tick = new double[N];
        NetServer s = null;
        var slow = new List<(int Tick, double[] Ms, int Changes)>();
        var all = new List<double>();
        var sums = new double[N];
        int changesThisTick = 0;
        Prof.BeginHook = i => start[i] = Stopwatch.GetTimestamp();
        Prof.EndHook = i =>
        {
            tick[i] += (Stopwatch.GetTimestamp() - start[i]) * 1000.0 / Stopwatch.Frequency;
            if (i != (int)Prof.Section.ServerTick) return;
            all.Add(tick[i]);
            for (int k = 0; k < N; k++) sums[k] += tick[k];
            if (tick[i] >= threshold)
            {
                slow.Add((s.Tick, (double[])tick.Clone(), changesThisTick));
                var zl = new System.Text.StringBuilder(GC.CollectionCount(0) != _gcSeen ? "   GC " : "      ");
            }
            _gcSeen = GC.CollectionCount(0);
            Array.Clear(tick, 0, N);
        };

        var sw = Stopwatch.StartNew();
        SubGrid g = BrMap.BuildGround();
        BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
        s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
        TileWalls walls = TileWalls.Find(s.Grid);
        walls.Prepare();
        s.PrepareSight();
        s.StartStream();
        Console.WriteLine($"map and server built in {sw.ElapsedMilliseconds} ms");

        // A long ground-floor wall near the middle of the map with open ground in front of it.
        Vec3d face = default, outward = default, stand = default;
        double best = double.MaxValue;
        for (int i = 0; i < walls.Count; i++)
        {
            WallTile t = walls[i];
            if (t.Kind != TileKind.Wall || t.Facing == Dir.Up || t.Facing == Dir.Down || !walls.HasCells(i)) continue;
            Aabb b = walls.BoundsOf(i);
            Vec3d c = (b.Min + b.Max) * 0.5;
            double r = Math.Abs(c.X - br.Size * 0.5 - 150) + Math.Abs(c.Z - br.Size * 0.5 - 150);
            if (r >= best) continue;
            foreach (int sign in new[] { 1, -1 })
            {
                Vec3d n = DirVec(t.Facing) * sign;
                Vec3d p = new Vec3d(c.X, b.Min.Y + 1.0, c.Z) + n * 6;
                // Standing room, ground under it, a clear line to the tile and open ground 6 m either side.
                if (s.Grid.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.5, 0, 0.5), p + new Vec3d(0.5, 1.9, 0.5)))) continue;
                TerrainHit down = s.Grid.Raycast(p, new Vec3d(0, -1, 0), 3);
                if (!down.Hit || down.Distance > 1.3) continue;
                Vec3d eye = p + new Vec3d(0, 1.0, 0);
                TerrainHit h = s.Grid.Raycast(eye, (c - eye).Normalized, 10);
                if (!h.Hit || walls.TileOfHit(h) != i) continue;
                Vec3d along = new Vec3d(n.Z, 0, -n.X);
                bool open = true;
                foreach (double k in new[] { -6.0, -3.0, 3.0, 6.0 })
                {
                    Vec3d q = p + along * k;
                    if (s.Grid.BoxOverlapsSolid(new Aabb(q - new Vec3d(0.4, -0.3, 0.4), q + new Vec3d(0.4, 1.9, 0.4)))) open = false;
                    Vec3d qe = q + new Vec3d(0, 1.0, 0);
                    TerrainHit qh = s.Grid.Raycast(qe, -n, 7);
                    if (!qh.Hit || walls.TileOfHit(qh) < 0) open = false;
                }
                if (!open) continue;
                best = r; face = new Vec3d(c.X, b.Min.Y, c.Z) + n * 0.15; outward = n; stand = p;
            }
        }
        if (best == double.MaxValue) { Console.WriteLine("no wall found"); return 1; }
        Console.WriteLine($"wall at {face}, facing {outward}");
        Vec3d alongW = new Vec3d(outward.Z, 0, -outward.X);
        for (int k = 0; k < players; k++)
        {
            Vec3d p = stand + alongW * ((k - (players - 1) / 2.0) * 1.6);
            s.AddBot(new Wrecker { Wall = face, Out = outward, Seed = k }, 1, p, Math.Atan2(-outward.X, -outward.Z));
        }

        void RayBench(string label)
        {
            var comp = (CompositeTerrain)s.Grid;
            var inner = (CompositeTerrain)comp.A;
            var terr = new (string, ITerrain)[] { ("field", inner.A), ("props", inner.B), ("brushes", comp.B), ("all", comp) };
            Vec3d eye = stand + new Vec3d(0, 1.6, 0);
            var line = new System.Text.StringBuilder(label + " rays (us each):");
            foreach (var (name, t) in terr)
            {
                int hits = 0;
                var rsw = Stopwatch.StartNew();
                for (int k = 0; k < 2000; k++)
                {
                    Vec3d aim = face + alongW * (6 * Math.Sin(k * 0.37)) + new Vec3d(0, 2 + 1.8 * Math.Sin(k * 0.71), 0);
                    if (t.Raycast(eye, (aim - eye).Normalized, 250).Hit) hits++;
                }
                line.Append($" {name} {rsw.Elapsed.TotalMilliseconds * 1000 / 2000:0.0} ({hits} hit)");
            }
            Console.WriteLine(line);
        }
        RayBench("before");
        int ticks = (int)(secs * SimTime.TicksPerSecond);
        var wall = Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++)
        {
            s.Step();
            changesThisTick = 0;
        }
        RayBench("after");
        int broken = 0, holed = 0;
        for (int i = 0; i < walls.Count; i++)
        {
            if (!walls[i].Changed) continue;
            if (walls[i].State == TileState.Broken) broken++; else if (walls[i].Open != 0) holed++;
        }
        all.Sort();
        double P(double q) => all.Count == 0 ? 0 : all[Math.Min(all.Count - 1, (int)(all.Count * q))];
        Console.WriteLine($"{players} players, {secs} s in {wall.Elapsed.TotalSeconds:0.0} s: {broken} tiles broken, {holed} holed");
        Console.WriteLine($"server tick avg {sums[0] / Math.Max(1, all.Count):0.000} ms, p99 {P(0.99):0.00}, p99.9 {P(0.999):0.00}, worst {P(1):0.00}; {slow.Count} ticks over {threshold} ms");
        Console.Write("avg ms/tick by section:");
        for (int i = 1; i < (int)Prof.Section.ClientReceive; i++) Console.Write($" {(Prof.Section)i} {sums[i] / Math.Max(1, all.Count):0.000}");
        Console.WriteLine();
        Console.Write("per call (ms):");
        Console.WriteLine();
        Console.Write("   tick   total");
        for (int i = 1; i < (int)Prof.Section.ClientReceive; i++) Console.Write($" {(Prof.Section)i,9}");
        Console.WriteLine();
        foreach ((int t, double[] ms, int _) in slow.GetRange(0, Math.Min(60, slow.Count)))
        {
            Console.Write($"{t,7} {ms[0],7:0.00}");
            for (int i = 1; i < (int)Prof.Section.ClientReceive; i++) Console.Write($" {ms[i],9:0.00}");
            Console.WriteLine();
        }
        return 0;
    }

    public static bool NoShoot = Environment.GetEnvironmentVariable("HOLES_NOSHOOT") == "1";
    private static int _gcSeen;
    private static readonly double[] TotMs = new double[16];
    private static readonly long[] TotN = new long[16];
    /// <summary>The first 20 s only warm up (.NET compiles each code path on first use; the game compiles everything at load).</summary>
    private static readonly int WarmTicks = 1000 + 20 * SimTime.TicksPerSecond;
    private static readonly Dictionary<int, string> ZLines = new Dictionary<int, string>();
    private static Vec3d DirVec(Dir d) => d switch
    {
        Dir.East => new Vec3d(1, 0, 0), Dir.West => new Vec3d(-1, 0, 0), Dir.North => new Vec3d(0, 0, 1), Dir.South => new Vec3d(0, 0, -1),
        Dir.Up => new Vec3d(0, 1, 0), _ => new Vec3d(0, -1, 0),
    };
}
