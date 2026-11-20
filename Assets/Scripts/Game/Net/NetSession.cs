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

        /// <summary>Simulated bad network on the local player's link (one way each).</summary>
        public readonly LinkConditions SimUp = new LinkConditions();
        public readonly LinkConditions SimDown = new LinkConditions();

        /// <summary>Raised when Client is replaced (joining or leaving a server). The local player re-reads it.</summary>
        public event Action ClientChanged;

        private readonly Func<VoxelGrid> _makeGrid;
        private readonly VoxelGrid _clientGrid;
        private readonly MovementConfig _movement;
        private readonly NetConfig _config;
        private readonly SculptConfig _sculptConfig;
        private readonly Vec3d[] _spawns;
        private readonly double _spawnYaw;
        private int _nextSpawn;

        private SimulatedLink _up, _down;
        private FishNetTransport _fish;
        private double _nowMs;
        private double _serverAccum;
        private bool _pendingOffline;
        private const double TickSeconds = 1.0 / SimTime.TicksPerSecond;

        public NetSession(Func<VoxelGrid> makeGrid, VoxelGrid clientGrid, MovementConfig movement, NetConfig config, SculptConfig sculpt, Vec3d[] spawns, double spawnYaw)
        {
            _makeGrid = makeGrid;
            _clientGrid = clientGrid;
            _movement = movement;
            _config = config;
            _sculptConfig = sculpt;
            _spawns = spawns;
            _spawnYaw = spawnYaw;
            StartOffline();
        }

        // ================================================================ Modes

        public void StartOffline()
        {
            ShutdownFishNet();
            CreateLocalServer();
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
            NewLocalLinks();
            Client = new NetClient(_clientGrid, _movement, _config, (d, r) => _up.Send(d, r, _nowMs));
            ClientChanged?.Invoke();
            EnsureFishNet();
            Mode = NetMode.Client;
            Status = $"Connecting to {address}:{port}...";
            if (!_fish.StartClient(address, port)) { Status = _fish.LastError; }
            Debug.Log($"[SCULPT] Joining {address}:{port}.");
        }

        private void CreateLocalServer()
        {
            NewLocalLinks();
            Server = new NetServer(_makeGrid(), _movement, _config, SendFromServer);
            ServerSculpt = new SculptSystem(Server.Grid, _sculptConfig);
            Server.Sculpt = ServerSculpt;
            _serverAccum = 0;
            _nextSpawn = 0;
            Client = new NetClient(_clientGrid, _movement, _config, (d, r) => _up.Send(d, r, _nowMs));
            ClientChanged?.Invoke();
            AddServerPlayer(LocalConnectionId);
        }

        private void NewLocalLinks()
        {
            _up = new SimulatedLink(SimUp, 1);
            _down = new SimulatedLink(SimDown, 2);
        }

        private void AddServerPlayer(int connectionId)
        {
            int k = _nextSpawn++;
            Vec3d spawn = _spawns[k % _spawns.Length];
            int team = 1 + (k % 2);
            Server.AddPlayer(connectionId, team, spawn, _spawnYaw);
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
            _fish.ClientDisconnected += () =>
            {
                if (Mode != NetMode.Client) return;
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
                StartOffline();
                Status = "Disconnected (offline now)";
            }
            _fish?.PumpIncoming();
            DeliverUp();

            if (Server != null)
            {
                _serverAccum += Math.Min(dt, 0.25);
                while (_serverAccum >= TickSeconds)
                {
                    Server.Step();
                    ServerSculpt.Step();
                    ServerSculpt.ClearEvents();
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
