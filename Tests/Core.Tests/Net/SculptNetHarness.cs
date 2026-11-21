using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>What a scripted client does on one tick.</summary>
    internal struct SculptTickInput
    {
        public MoveInput Move;
        public HandInput Hand;
        public bool Confirm;
        public SculptAction Action;
        /// <summary>View pitch in degrees (positive looks down).</summary>
        public float Pitch;
    }

    /// <summary>
    /// One authoritative server with a sculpt system and any number of sculpting clients over simulated links. Each
    /// client has its own copy of the map (built from the same function) and a <see cref="SculptClient"/>.
    /// </summary>
    internal sealed class SculptNetHarness
    {
        public sealed class Peer
        {
            public int ConnectionId;
            public SubGrid Grid;
            public NetClient Client;
            public SculptClient Sculpt => Client.Sculpt;
            public SimulatedLink Up, Down;
            public double FrameMs;
            public double NextFrameMs;
            public Func<Peer, int, SculptTickInput> Script = (_, __) => default;
            public readonly List<MoveEvent> Events = new List<MoveEvent>();
            public readonly List<ClientSculptEvent> SculptEvents = new List<ClientSculptEvent>();
            public ServerPlayer ServerSide;
            /// <summary>Client tick of every confirm and the seq it got.</summary>
            public readonly List<KeyValuePair<int, byte>> Confirms = new List<KeyValuePair<int, byte>>();
        }

        public readonly NetConfig Config = new NetConfig();
        public readonly MovementConfig Movement = new MovementConfig();
        public readonly SculptConfig SculptConfig = new SculptConfig();
        public readonly NetServer Server;
        public readonly SubGrid ServerGrid;
        public readonly SculptSystem ServerSculpt;
        public readonly List<Peer> Peers = new List<Peer>();
        public readonly List<SculptEvent> ServerEvents = new List<SculptEvent>();
        public readonly Func<SubGrid> MakeGrid;
        public double NowMs { get; private set; }
        private double _nextServerTickMs;
        private readonly Dictionary<int, Peer> _byConn = new Dictionary<int, Peer>();
        private int _nextConn = 1;

        public const double TickMs = 1000.0 / 60.0;
        public const double B = SubGrid.TileSize;

        /// <summary>Wraps each side's ground into what movement and bullets collide with (e.g. plus buildings).</summary>
        public readonly Func<SubGrid, ITerrain> Wrap;

        public SculptNetHarness(Func<SubGrid> makeGrid, Action<SculptConfig> tweak = null, Func<SubGrid, ITerrain> wrap = null)
        {
            MakeGrid = makeGrid;
            Wrap = wrap ?? (g => g);
            tweak?.Invoke(SculptConfig);
            ServerGrid = makeGrid();
            Server = new NetServer(Wrap(ServerGrid), Movement, Config, (conn, data, reliable) =>
            {
                if (_byConn.TryGetValue(conn, out var p)) p.Down.Send(data, reliable, NowMs);
            });
            ServerSculpt = new SculptSystem(ServerGrid, SculptConfig);
            Server.Sculpt = ServerSculpt;
            _nextServerTickMs = TickMs;
        }

        /// <summary>32 x 32 flat tiles with a one-level wall at tiles x 7, z 5..6 (x 8.4..9.6 m, z 6..8.4 m).</summary>
        public static SculptNetHarness Flat(Action<SculptConfig> tweak = null) => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            new TileMap(g).Fill(new Int3(7, 0, 5), new Int3(7, 0, 6), Tile.Solid);
            g.ClearChanges();
            return g;
        }, tweak);

        public static Vec3d FeetIn(double tx, double tz) => new Vec3d((tx + 0.5) * B, 0, (tz + 0.5) * B);

        public Peer AddClient(Vec3d spawn, int team = 1, LinkConditions up = null, LinkConditions down = null, double frameMs = 1000.0 / 144.0, int seed = 1)
        {
            var p = new Peer
            {
                ConnectionId = _nextConn++,
                Up = new SimulatedLink(up ?? new LinkConditions(), seed * 7919 + 1),
                Down = new SimulatedLink(down ?? new LinkConditions(), seed * 104729 + 3),
                FrameMs = frameMs,
                NextFrameMs = NowMs,
            };
            p.Grid = MakeGrid();
            var sc = new SculptClient(p.Grid, SculptConfig, Movement);
            p.Client = new NetClient(Wrap(p.Grid), Movement, Config, (data, reliable) => p.Up.Send(data, reliable, NowMs), sc);
            _byConn[p.ConnectionId] = p;
            Peers.Add(p);
            p.ServerSide = Server.AddPlayer(p.ConnectionId, team, spawn, 0);
            return p;
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
                    Server.Step();
                    _nextServerTickMs += TickMs;
                }
                foreach (Peer p in Peers.ToArray())
                {
                    p.Down.Deliver(NowMs, d => p.Client.Receive(d, 0, d.Length));
                    if (NowMs < p.NextFrameMs) continue;
                    double dt = p.FrameMs / 1000.0;
                    p.NextFrameMs += p.FrameMs;
                    int n = p.Client.BeginFrame(dt);
                    for (int i = 0; i < n; i++)
                    {
                        int t = p.Client.Tick + 1;
                        SculptTickInput input = p.Script(p, t);
                        byte seq = p.Client.PredictTick(input.Move, input.Pitch, false, input.Hand, input.Confirm, input.Action, p.Events);
                        if (seq != 0) p.Confirms.Add(new KeyValuePair<int, byte>(t, seq));
                    }
                    p.SculptEvents.AddRange(p.Sculpt.Events);
                    p.Sculpt.Events.Clear();
                    p.Client.UpdateVisual(dt);
                }
            }
        }

        public void RunSeconds(double s) => RunMs(s * 1000.0);

        // ---------------------------------------------------------------- Scripting helpers

        /// <summary>Aims from the client's predicted eye at a world point (its own grid): the action's target and aim point.</summary>
        public static bool Aim(Peer p, Vec3d point, out Int3 sub, out Vec3d aim)
        {
            PlayerBody body = p.Sculpt.LocalBody;
            Vec3d d = point - body.Eye;
            TerrainHit h = p.Grid.Raycast(body.Eye, d, d.Length + 1.0);
            sub = h.Sub;
            aim = h.Point;
            return h.Hit;
        }

        /// <summary>Script: press Q at tick <paramref name="toggleAt"/>, then confirm the action built at tick <paramref name="confirmAt"/>.</summary>
        public static Func<Peer, int, SculptTickInput> ToggleThen(int toggleAt, int confirmAt, Func<Peer, SculptAction> action)
        {
            return (p, t) =>
            {
                var i = new SculptTickInput { Move = new MoveInput { Yaw = 0 } };
                if (t == toggleAt) i.Hand.SculptToggle = true;
                if (t == confirmAt) { i.Confirm = true; i.Action = action(p); }
                return i;
            };
        }

        public static SculptAction PushWall(Peer p, Dir dir = Dir.East)
        {
            Assert.That(Aim(p, new Vec3d(8.4, 0.6, 6.6), out Int3 sub, out Vec3d aim), Is.True);
            return SculptAction.Move(sub, dir, aim);
        }

        public static SculptAction RaiseFloor(Peer p, double x, double z)
        {
            Assert.That(Aim(p, new Vec3d(x, 0, z), out Int3 sub, out Vec3d aim), Is.True);
            return SculptAction.Raise(sub, aim);
        }

        /// <summary>Every grid agrees once the dust settles: no predictions left, same sub-cells everywhere.</summary>
        public void AssertConverged()
        {
            ulong h = ServerGrid.Hash();
            foreach (var p in Peers)
            {
                Assert.That(p.Sculpt.Local.Count, Is.EqualTo(0), "no prediction left unanswered");
                Assert.That(p.Grid.Hash(), Is.EqualTo(h), $"client {p.ConnectionId} grid equals the server's");
                Assert.That(p.Sculpt.Mirror.Pieces.Count, Is.EqualTo(ServerSculpt.Pieces.Count), "same pieces");
            }
        }
    }
}
