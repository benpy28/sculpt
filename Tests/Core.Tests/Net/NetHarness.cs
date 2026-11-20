using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// One server and any number of clients wired through simulated links, on a shared millisecond clock.
    /// Server ticks at exactly 60 Hz; each client renders at its own frame rate and ticks off its own dilated clock.
    /// </summary>
    internal sealed class NetHarness
    {
        public sealed class Peer
        {
            public int ConnectionId;
            public NetClient Client;
            public SimulatedLink Up, Down;
            public double FrameMs;
            public double NextFrameMs;
            public Func<int, MoveInput> Script = _ => default;
            public Func<int, bool> RespawnScript = _ => false;
            public readonly List<MoveEvent> Events = new List<MoveEvent>();
            public ServerPlayer ServerSide;
            public Action<Peer> OnFrame;
        }

        public readonly NetConfig Config = new NetConfig();
        public readonly MovementConfig Movement = new MovementConfig();
        public readonly NetServer Server;
        public readonly List<Peer> Peers = new List<Peer>();
        public readonly List<MoveEvent> ServerEvents = new List<MoveEvent>();
        public readonly Func<VoxelGrid> MakeGrid;
        public readonly Vec3d Spawn;
        public double NowMs { get; private set; }
        private double _nextServerTickMs;
        private readonly Dictionary<int, Peer> _byConn = new Dictionary<int, Peer>();
        private int _nextConn = 1;

        public const double TickMs = 1000.0 / 60.0;

        public NetHarness(Func<VoxelGrid> makeGrid, Vec3d spawn, Action<NetConfig> tweak = null)
        {
            MakeGrid = makeGrid;
            Spawn = spawn;
            tweak?.Invoke(Config);
            Server = new NetServer(makeGrid(), Movement, Config, (conn, data, reliable) =>
            {
                if (_byConn.TryGetValue(conn, out var p)) p.Down.Send(data, reliable, NowMs);
            });
            _nextServerTickMs = TickMs;
        }

        public static NetHarness Flat(Action<NetConfig> tweak = null)
        {
            return new NetHarness(() =>
            {
                var g = new VoxelGrid(2, 2, 2, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            }, TileMap.FeetIn(8, 0, 8), tweak);
        }

        public static NetHarness M2Map(out Vec3d spawn, Action<NetConfig> tweak = null)
        {
            TestMaps.BuildM2(out spawn, out _);
            return new NetHarness(() => new VoxelGrid(TestMaps.BuildM2(out _, out _)), spawn, tweak);
        }

        public Peer AddClient(LinkConditions up = null, LinkConditions down = null, double frameMs = 1000.0 / 144.0, int seed = 1, int team = 1, Vec3d? spawn = null)
        {
            var p = new Peer
            {
                ConnectionId = _nextConn++,
                Up = new SimulatedLink(up ?? new LinkConditions(), seed * 7919 + 1),
                Down = new SimulatedLink(down ?? new LinkConditions(), seed * 104729 + 3),
                FrameMs = frameMs,
                NextFrameMs = NowMs,
            };
            p.Client = new NetClient(MakeGrid(), Movement, Config, (data, reliable) => p.Up.Send(data, reliable, NowMs));
            _byConn[p.ConnectionId] = p;
            Peers.Add(p);
            p.ServerSide = Server.AddPlayer(p.ConnectionId, team, spawn ?? Spawn, 0);
            return p;
        }

        public void Remove(Peer p)
        {
            Server.RemovePlayer(p.ConnectionId);
            Peers.Remove(p);
            _byConn.Remove(p.ConnectionId);
        }

        public static LinkConditions Lag(double ms, double jitter = 0, double loss = 0) =>
            new LinkConditions { LatencyMs = ms, JitterMs = jitter, LossPercent = loss };

        public void RunMs(double ms)
        {
            double end = NowMs + ms;
            const double step = 0.25;
            while (NowMs < end)
            {
                NowMs += step;
                foreach (Peer p in Peers)
                {
                    Peer peer = p;
                    p.Up.Deliver(NowMs, d => Server.Receive(peer.ConnectionId, d, 0, d.Length));
                }
                while (NowMs >= _nextServerTickMs)
                {
                    Server.Step(ServerEvents);
                    _nextServerTickMs += TickMs;
                }
                foreach (Peer p in Peers.ToArray())
                {
                    p.Down.Deliver(NowMs, d => p.Client.Receive(d, 0, d.Length));
                    if (NowMs >= p.NextFrameMs)
                    {
                        double dt = p.FrameMs / 1000.0;
                        p.NextFrameMs += p.FrameMs;
                        int n = p.Client.BeginFrame(dt);
                        for (int i = 0; i < n; i++)
                        {
                            int t = p.Client.Tick + 1;
                            p.Client.PredictTick(p.Script(t), 0f, p.RespawnScript(t), p.Events);
                        }
                        p.Client.UpdateVisual(dt);
                        p.OnFrame?.Invoke(p);
                    }
                }
            }
        }

        public void RunSeconds(double s) => RunMs(s * 1000.0);
    }
}
