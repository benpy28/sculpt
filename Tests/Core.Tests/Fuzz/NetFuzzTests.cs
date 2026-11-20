using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Seeded netcode soak. Random arenas, several clients each with their own random link (latency, jitter,
    /// loss, reordering, duplicated packets), frame rate and frame hitches, driven by movement bots.
    /// Checks the server and every prediction stay legal every tick, that the server is a pure function of the
    /// commands it consumed, and that every client converges exactly onto the server once input stops.
    /// </summary>
    [Category("Fuzz")]
    public class NetFuzzTests
    {
        private const double B = VoxelGrid.BlockSize;
        private const double TickMs = 1000.0 / 60.0;

        private static IEnumerable<ulong> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 8;
            for (ulong s = 1; s <= (ulong)n; s++) yield return s * 3571;
        }

        private sealed class Peer
        {
            public int Conn;
            public NetClient Client;
            public SimulatedLink Up, Down;
            public double FrameMs, NextFrameMs, DupChance;
            public InputBot Bot;
            public bool Idle;
            public ServerPlayer Server;
            public readonly List<MoveEvent> Events = new List<MoveEvent>();
        }

        private sealed class Harness
        {
            public readonly ulong Seed;
            public readonly Rng R;
            public readonly MovementConfig Movement = new MovementConfig();
            public readonly NetConfig Config = new NetConfig();
            public readonly NetServer Server;
            public readonly List<Peer> Peers = new List<Peer>();
            public double NowMs;
            private double _nextServerTickMs = TickMs;
            private readonly Dictionary<int, Peer> _byConn = new Dictionary<int, Peer>();
            private readonly ulong _terrainSeed;

            // Server-side record of every command consumed and every state reached, per player id.
            public readonly Dictionary<int, List<InputCmd>> Consumed = new Dictionary<int, List<InputCmd>>();
            public readonly Dictionary<int, MotorState> Initial = new Dictionary<int, MotorState>();
            public readonly Dictionary<int, List<MotorState>> ServerStates = new Dictionary<int, List<MotorState>>();
            private readonly Dictionary<int, MotorChecker> _checkers = new Dictionary<int, MotorChecker>();
            private readonly List<MoveEvent> _noEvents = new List<MoveEvent>();
            public int Hitches;
            public double HitchChance = 0.002;

            public Harness(ulong seed)
            {
                Seed = seed;
                R = new Rng(seed);
                _terrainSeed = R.Next();
                Server = new NetServer(MakeGrid(), Movement, Config, (conn, data, reliable) =>
                {
                    if (_byConn.TryGetValue(conn, out var p)) Send(p, p.Down, data, reliable);
                });
            }

            public VoxelGrid MakeGrid()
            {
                var g = new VoxelGrid(2, 2, 2, -16);
                g.FillBaseGround();
                TerrainGen.Build(g, new Rng(_terrainSeed), 2, 29, rampChance: 0.3);
                g.ClearChanges();
                return g;
            }

            private void Send(Peer p, SimulatedLink link, byte[] data, bool reliable)
            {
                link.Send(data, reliable, NowMs);
                if (!reliable && R.Chance(p.DupChance)) link.Send((byte[])data.Clone(), false, NowMs); // a duplicated datagram
            }

            public Peer AddClient(LinkConditions up, LinkConditions down, double fps, double dup)
            {
                var p = new Peer
                {
                    Conn = Peers.Count + 1,
                    Up = new SimulatedLink(up, (int)(R.Next() & 0x7fffffff)),
                    Down = new SimulatedLink(down, (int)(R.Next() & 0x7fffffff)),
                    FrameMs = 1000.0 / fps,
                    NextFrameMs = NowMs,
                    DupChance = dup,
                    Bot = new InputBot(new Rng(R.Next())),
                };
                p.Client = new NetClient(MakeGrid(), Movement, Config, (data, reliable) => Send(p, p.Up, data, reliable));
                _byConn[p.Conn] = p;
                Peers.Add(p);

                // Spawn somewhere legal on this arena.
                VoxelGrid g = Server.Grid;
                Vec3d spawn = default;
                for (int i = 0; i < 500; i++)
                {
                    var body = new PlayerBody(0, 1, new Vec3d(R.Range(4 * B, 28 * B), 0, R.Range(4 * B, 28 * B)));
                    if (Inspect.DropToGround(g, body) && !MotorChecker.Inside(g, Movement, MotorState.At(body.Position))) { spawn = body.Position; break; }
                }
                p.Server = Server.AddPlayer(p.Conn, 1 + p.Conn % 2, spawn, 0);
                Consumed[p.Server.Id] = new List<InputCmd>();
                ServerStates[p.Server.Id] = new List<MotorState>();
                Initial[p.Server.Id] = p.Server.State;
                _checkers[p.Server.Id] = new MotorChecker(Movement);
                return p;
            }

            public void Run(double ms)
            {
                double end = NowMs + ms;
                const double step = 0.25;
                while (NowMs < end)
                {
                    NowMs += step;
                    foreach (Peer p in Peers)
                    {
                        Peer peer = p;
                        p.Up.Deliver(NowMs, d => Server.Receive(peer.Conn, d, 0, d.Length));
                    }
                    while (NowMs >= _nextServerTickMs)
                    {
                        var before = new Dictionary<int, MotorState>();
                        foreach (ServerPlayer sp in Server.Players) before[sp.Id] = sp.State;
                        Server.Step();
                        foreach (ServerPlayer sp in Server.Players)
                        {
                            Consumed[sp.Id].Add(sp.LastCmd);
                            ServerStates[sp.Id].Add(sp.State);
                            _checkers[sp.Id].Check(Server.Grid, before[sp.Id], sp.State, _noEvents, $"seed {Seed} server tick {Server.Tick} player {sp.Id}", worldStatic: true);
                        }
                        _nextServerTickMs += TickMs;
                    }
                    foreach (Peer p in Peers)
                    {
                        Peer peer = p;
                        p.Down.Deliver(NowMs, d => peer.Client.Receive(d, 0, d.Length));
                        if (NowMs < p.NextFrameMs) continue;

                        // Frame time wobbles, and now and then the client hitches (GC, alt-tab, loading).
                        double frame = p.FrameMs * R.Range(0.8, 1.2);
                        if (HitchChance > 0 && R.Chance(HitchChance)) { frame += R.Range(50, 300); Hitches++; }
                        p.NextFrameMs += frame;
                        int n = p.Client.BeginFrame(frame / 1000.0);
                        for (int i = 0; i < n; i++)
                        {
                            MoveInput mi = p.Idle ? default : p.Bot.Next(p.Client.Tick + 1);
                            p.Events.Clear();
                            p.Client.PredictTick(mi, 0f, false, p.Events);
                        }
                        p.Client.UpdateVisual(frame / 1000.0);
                        if (!p.Client.Joined) continue; // the welcome has not arrived yet

                        MotorState pred = p.Client.Predicted;
                        Assert.That(MotorChecker.Inside(p.Client.Grid, Movement, pred), Is.False, $"seed {Seed} client {p.Conn}: prediction inside terrain at {pred.Position}");
                        Assert.That(double.IsNaN(pred.Position.X + pred.Position.Y + pred.Position.Z), Is.False, $"seed {Seed} client {p.Conn}: NaN prediction");
                        Assert.That(p.Client.CorrectionOffset.Length, Is.LessThanOrEqualTo(Config.CorrectionSnapDistance + 1e-9), "visual offset is bounded");
                    }
                }
            }
        }

        private static Harness Build(ulong seed, bool perfect, out string desc)
        {
            var h = new Harness(seed);
            if (perfect) h.HitchChance = 0;
            int clients = h.R.Int(2, 5);
            var parts = new List<string>();
            for (int i = 0; i < clients; i++)
            {
                LinkConditions Link() => perfect
                    ? new LinkConditions { LatencyMs = h.R.Range(0, 120) }
                    : new LinkConditions { LatencyMs = h.R.Range(0, 120), JitterMs = h.R.Range(0, 30), LossPercent = h.R.Pick(new[] { 0.0, 1, 3, 5, 10, 20 }) };
                LinkConditions up = Link(), down = Link();
                double fps = h.R.Pick(new[] { 30.0, 60, 75, 120, 144, 240 });
                double dup = perfect ? 0 : h.R.Pick(new[] { 0.0, 0.01, 0.05 });
                h.AddClient(up, down, fps, dup);
                parts.Add($"[{up.LatencyMs:0}/{down.LatencyMs:0} ms, jitter {up.JitterMs:0}/{down.JitterMs:0}, loss {up.LossPercent}/{down.LossPercent}%, {fps} fps, dup {dup}]");
            }
            desc = string.Join(" ", parts);
            return h;
        }

        /// <summary>Bad networks: everything stays legal, the server is a pure function of its commands, and clients converge.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void BadNetwork_StaysLegal_AndConverges(ulong seed)
        {
            Harness h = Build(seed, perfect: false, out string desc);
            h.Run(20000);

            // The server's trajectory is exactly what replaying its own consumed commands gives.
            foreach (var kv in h.Consumed)
            {
                MotorState s = h.Initial[kv.Key];
                List<MotorState> states = h.ServerStates[kv.Key];
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    NetSim.Simulate(h.Server.Grid, h.Movement, h.Config, ref s, kv.Value[i], h.Server.Players[0].Spawn, null);
                    // Spawn is only used on respawn, which the bots never request.
                    Assert.That(Protocol.SameMotor(s, states[i]), Is.True, $"seed {seed}: server replay of player {kv.Key} diverged at step {i}");
                }
            }

            // Stop all input: after the links drain, every prediction lands exactly on the server.
            foreach (Peer p in h.Peers) p.Idle = true;
            h.Run(3000);
            foreach (Peer p in h.Peers)
            {
                Assert.That(p.Client.Predicted.Position, Is.EqualTo(p.Server.State.Position), $"seed {seed} client {p.Conn} {desc}: did not converge");
                Assert.That(p.Client.Predicted.Mode, Is.EqualTo(p.Server.State.Mode));
            }
            TestContext.Out.WriteLine($"seed {seed} {desc}; hitches {h.Hitches}; " + string.Join(", ", h.Peers.ConvertAll(p => $"c{p.Conn}: corr {p.Client.Corrections} missed {p.Server.MissedInputs} late {p.Server.LateInputs}")));
        }

        /// <summary>Clean links (any latency, any frame rate, no hitches): prediction must match the server bit for bit, always.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void CleanNetwork_PredictionIsExact(ulong seed)
        {
            Harness h = Build(seed + 1, perfect: true, out string desc);
            h.Run(6000); // clock settles (see ISSUE-5: mid-latency joins can take ~4 s)
            var c0 = h.Peers.ConvertAll(p => p.Client.Corrections + p.Client.SilentResyncs);
            var m0 = h.Peers.ConvertAll(p => p.Server.MissedInputs);
            h.Run(15000);
            for (int i = 0; i < h.Peers.Count; i++)
            {
                Peer p = h.Peers[i];
                Assert.That(p.Client.Corrections + p.Client.SilentResyncs - c0[i], Is.Zero, $"seed {seed} client {p.Conn} {desc}: prediction disagreed with the server " +
                    $"(corrections {p.Client.Corrections}, silent {p.Client.SilentResyncs}, missed {p.Server.MissedInputs - m0[i]}, late {p.Server.LateInputs}, lead {p.Client.LastServerLead}, snaps {p.Client.ClockSnaps}, last corr {p.Client.LastCorrectionMeters:0.000} m)");
                Assert.That(p.Server.MissedInputs - m0[i], Is.Zero, $"seed {seed} client {p.Conn} {desc}: server ran without the real command");
            }
        }
    }
}
