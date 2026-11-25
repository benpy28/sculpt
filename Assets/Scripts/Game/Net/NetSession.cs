using System;
using Sculpt.Core;
using Sculpt.Core.Net;
using UnityEngine;

namespace Sculpt.Game.Net
{
    public enum NetMode
    {
        Offline,  // local server in memory, nobody else can join
        Host,     // local server in memory plus FishNet server for remote players
        Client,   // joined someone else's server over FishNet
    }

    /// <summary>
    /// Owns the netcode for this process. Offline and host run a real NetServer locally and the local player talks
    /// to it through an in-memory link, so every mode runs the same prediction and reconciliation path.
    /// Simulated latency, jitter and loss are applied to the local client's link in both directions, in every mode.
    /// </summary>
    public sealed class NetSession : IDisposable
    {
        public const int LocalConnectionId = -1000;

        public NetMode Mode { get; private set; } = NetMode.Offline;
        public string Status { get; private set; } = "";
        public NetServer Server { get; private set; }
        public NetClient Client { get; private set; }
        public ushort Port { get; private set; } = FishNetTransport.DefaultPort;
        public string JoinAddress { get; private set; } = "";
        public SculptSystem ServerSculpt { get; private set; }
        /// <summary>The grid the local client draws and predicts on. Replaced with the client (see ClientChanged).</summary>
        public SubGrid ClientGrid { get; private set; }
        public SculptConfig SculptConfig => _sculptConfig;
        /// <summary>Run the match (most kills in 5 minutes) on the local server while hosting. Offline it is always off.</summary>
        public bool MatchWhenHosting;
        /// <summary>Run the match offline too (playing against a bot on the local server).</summary>
        public bool MatchOffline;
        /// <summary>Seconds spent trying to join without a welcome (the menu shows a hint after a few).</summary>
        public float JoinWaitSeconds { get; private set; }
        /// <summary>Why the last join failed, in plain words (empty if it didn't).</summary>
        public string JoinError { get; private set; } = "";

        /// <summary>Simulated bad network on the local player's link (one way each).</summary>
        public readonly LinkConditions SimUp = new LinkConditions();
        public readonly LinkConditions SimDown = new LinkConditions();

        /// <summary>Raised when Client is replaced (joining or leaving a server). The local player re-reads it.</summary>
        public event Action ClientChanged;

        private readonly Func<SubGrid> _makeGrid;
        private readonly Func<SubGrid, ITerrain> _terrain;
        /// <summary>Map setup on every new local server (items lying on the map).</summary>
        private readonly Action<NetServer> _setupServer;
        private SubGrid _firstClientGrid;
        private readonly MovementConfig _movement;
        private readonly NetConfig _config;
        private readonly SculptConfig _sculptConfig;
        private readonly Vec3d[][] _teamSpawns;
        private readonly double[] _teamYaw;
        private int _nextSpawn;

        private SimulatedLink _up, _down;
        private FishNetTransport _fish;
        private double _nowMs;
        private double _serverAccum;
        private bool _pendingOffline;
        private const double TickSeconds = 1.0 / SimTime.TicksPerSecond;

        /// <summary>teamSpawns[0] / teamYaw[0] are team 1's spawn spots and facing; [1] team 2's.</summary>
        /// <param name="terrain">What movement and bullets collide with, built around each side's ground grid (the
        /// breach map adds its buildings). Null: the ground grid alone.</param>
        public NetSession(Func<SubGrid> makeGrid, SubGrid clientGrid, MovementConfig movement, NetConfig config, SculptConfig sculpt, Vec3d[][] teamSpawns, double[] teamYaw,
                          Func<SubGrid, ITerrain> terrain = null, Action<NetServer> setupServer = null)
        {
            _setupServer = setupServer;
            _makeGrid = makeGrid;
            _terrain = terrain ?? (g => g);
            _firstClientGrid = clientGrid;
            _movement = movement;
            _config = config;
            _sculptConfig = sculpt;
            _teamSpawns = teamSpawns;
            _teamYaw = teamYaw;
            StartOffline();
        }

        // ================================================================ Modes

        public void StartOffline()
        {
            ShutdownFishNet();
            CreateLocalServer();
            Server.MatchConfig.Enabled = MatchOffline;
            Mode = NetMode.Offline;
            Status = "Offline (local server)";
        }

        public void StartHost(ushort port)
        {
            if (Mode == NetMode.Client || Server == null) StartOffline();
            Port = port;
            EnsureFishNet();
            if (_fish.StartServer(port))
            {
                Mode = NetMode.Host;
                Server.MatchConfig.Enabled = MatchWhenHosting;
                Status = $"Hosting on port {port}";
                Debug.Log($"[SCULPT] Hosting on UDP port {port}.");
            }
            else Status = _fish.LastError;
        }

        public void Join(string address, ushort port)
        {
            ShutdownFishNet();
            Server = null;
            ServerSculpt = null;
            JoinAddress = address;
            Port = port;
            JoinWaitSeconds = 0;
            JoinError = "";
            NewLocalLinks();
            NewClient();
            EnsureFishNet();
            Mode = NetMode.Client;
            Status = $"Connecting to {address}:{port}...";
            if (!_fish.StartClient(address, port)) { Status = _fish.LastError; }
            Debug.Log($"[SCULPT] Joining {address}:{port}.");
        }

        private void CreateLocalServer()
        {
            NewLocalLinks();
            SubGrid serverGrid = _makeGrid();
            Server = new NetServer(_terrain(serverGrid), _movement, _config, SendFromServer);
            ServerSculpt = new SculptSystem(serverGrid, _sculptConfig);
            Server.Sculpt = ServerSculpt;
            _setupServer?.Invoke(Server);
            _serverAccum = 0;
            _nextSpawn = 0;
            NewClient();
            AddServerPlayer(LocalConnectionId);
        }

        /// <summary>A fresh client with its own copy of the map (the server sends everything sculpted since).</summary>
        private void NewClient()
        {
            ClientGrid = _firstClientGrid ?? _makeGrid();
            _firstClientGrid = null;
            var sculpt = new SculptClient(ClientGrid, _sculptConfig, _movement);
            Client = new NetClient(_terrain(ClientGrid), _movement, _config, (d, r) => _up.Send(d, r, _nowMs), sculpt);
            ClientChanged?.Invoke();
        }

        private void NewLocalLinks()
        {
            _up = new SimulatedLink(SimUp, 1);
            _down = new SimulatedLink(SimDown, 2);
        }

        private void AddServerPlayer(int connectionId)
        {
            int k = _nextSpawn++;
            int team = 1 + (k % 2);
            Vec3d[] spots = _teamSpawns[team - 1];
            Vec3d spawn = spots[(k / 2) % spots.Length];
            Server.AddPlayer(connectionId, team, spawn, _teamYaw[team - 1]);
        }

        private void SendFromServer(int connectionId, byte[] data, bool reliable)
        {
            if (connectionId == LocalConnectionId) _down.Send(data, reliable, _nowMs);
            else _fish?.SendToClient(connectionId, data, reliable);
        }

        // ================================================================ FishNet

        private void EnsureFishNet()
        {
            if (_fish != null) return;
            _fish = new FishNetTransport();
            _fish.ServerClientConnected += id =>
            {
                if (Server == null) return;
                AddServerPlayer(id);
                Debug.Log($"[SCULPT] Player joined (connection {id}).");
            };
            _fish.ServerClientDisconnected += id =>
            {
                Server?.RemovePlayer(id);
                Debug.Log($"[SCULPT] Player left (connection {id}).");
            };
            _fish.ServerReceived += (id, data, reliable) => Server?.Receive(id, data, 0, data.Length);
            _fish.ClientConnected += () => { if (Mode == NetMode.Client) Status = $"Connected to {JoinAddress}:{Port}"; };
            _fish.ServerClientConnected += id => { if (Server != null && Mode == NetMode.Host) Status = $"Hosting on port {Port}: {Server.Players.Count} player(s)"; };
            _fish.ClientDisconnected += () =>
            {
                if (Mode != NetMode.Client) return;
                if (!Client.Joined && JoinError.Length == 0) JoinError = $"Couldn't reach {JoinAddress}:{Port}.";
                _pendingOffline = true; // never tear FishNet down inside its own callback
            };
            // Received packets go through the simulated downlink, so fake latency applies to real traffic too.
            _fish.ClientReceived += (data, reliable) => _down.Send(data, reliable, _nowMs);
        }

        private void ShutdownFishNet()
        {
            if (_fish == null) return;
            FishNetTransport f = _fish;
            _fish = null; // detach first so its disconnect callbacks are ignored
            f.Dispose();
        }

        // ================================================================ Frame

        /// <summary>Start of frame: read the network, deliver due packets, run server ticks.</summary>
        public void BeginFrame(double dt)
        {
            _nowMs += dt * 1000.0;
            if (_pendingOffline)
            {
                _pendingOffline = false;
                Debug.Log("[SCULPT] Disconnected from server; back to offline.");
                bool joined = Client.Joined;
                StartOffline();
                Status = joined ? "Disconnected (offline now)" : "Join failed (offline now)";
                if (joined && JoinError.Length == 0) JoinError = "The host left or the connection dropped.";
            }
            _fish?.PumpIncoming();
            if (Mode == NetMode.Client && !Client.Joined)
            {
                JoinWaitSeconds += (float)dt;
                if (Client.JoinError.Length > 0 && JoinError.Length == 0) { JoinError = Client.JoinError; _pendingOffline = true; }
            }
            DeliverUp();

            if (Server != null)
            {
                _serverAccum += Math.Min(dt, 0.25);
                while (_serverAccum >= TickSeconds)
                {
                    Server.Step(); // also steps the sculpt system and sends world deltas
                    _serverAccum -= TickSeconds;
                }
            }
            DeliverDown();
        }

        /// <summary>End of frame, after the local player's ticks: hand new packets to the network now.</summary>
        public void EndFrame()
        {
            DeliverUp();
            _fish?.PumpOutgoing();
        }

        private void DeliverUp()
        {
            _up.Deliver(_nowMs, d =>
            {
                if (Server != null) Server.Receive(LocalConnectionId, d, 0, d.Length);
                else _fish?.SendToServer(d, false);
            });
        }

        private void DeliverDown()
        {
            NetClient c = Client;
            _down.Deliver(_nowMs, d => c.Receive(d, 0, d.Length));
        }

        public ServerPlayer LocalServerPlayer => Server?.FindByConnection(LocalConnectionId);

        public void Dispose()
        {
            ShutdownFishNet();
        }
    }
}
