using System;
using System.Collections.Generic;
using System.IO;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;
using UnityEngine;

namespace Sculpt.Game.Net
{
    public enum NetMode
    {
        Offline,  // local server in memory, nobody else can join
        Host,     // local server in memory plus FishNet server for remote players
        Client,   // joined someone else's server over FishNet (to play, or to watch: Client.IsSpectator)
        Replay,   // playing a saved replay (no server; the client is fed from the file)
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

        // ---- Replays and spectating ----
        /// <summary>Record every local or hosted server to a replay file (off with -noreplay).</summary>
        public static bool RecordReplays = true;
        /// <summary>How far behind live spectators of this host watch (Ben's roadmap: 2 minutes; -specdelay seconds).</summary>
        public static int SpectatorDelaySeconds = 120;
        /// <summary>The replay being played (Mode == Replay), else null.</summary>
        public ReplayPlayer Replay { get; private set; }
        /// <summary>Joining to watch rather than play.</summary>
        public bool Spectating { get; private set; }
        /// <summary>The file being recorded now, or null.</summary>
        public string RecordingPath { get; private set; }
        /// <summary>Map name written into replays (and read back to rebuild the world when playing one).</summary>
        public string MapName = "";
        /// <summary>Mode label written into replays ("Vs bot", "1v1 hosted", "Practice").</summary>
        public string ModeLabel = "";
        private ReplayWriter _recWriter;
        private FileStream _recFile;
        private double _recMovedMeters;
        private Vec3d _recLastPos;
        private bool _recHadLast;
        private bool _recMatchPlayed;
        private readonly HashSet<int> _pendingConns = new HashSet<int>();

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
                          Func<SubGrid, ITerrain> terrain = null, Action<NetServer> setupServer = null, string mapName = "", string modeLabel = "")
        {
            MapName = mapName ?? "";
            ModeLabel = modeLabel ?? "";
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
            StopRecording();
            Replay = null;
            Spectating = false;
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

        /// <param name="spectate">Watch instead of play (the host decides the delay).</param>
        public void Join(string address, ushort port, bool spectate = false)
        {
            StopRecording();
            Replay = null;
            Spectating = spectate;
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
            Status = spectate ? $"Connecting to {address}:{port} to watch..." : $"Connecting to {address}:{port}...";
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
            Server.SpectatorDelayTicks = Math.Max(0, SpectatorDelaySeconds) * SimTime.TicksPerSecond;
            StartRecording(); // before the map setup adds bots, so the whole server is in the replay (and can be re-run)
            _setupServer?.Invoke(Server);
            _serverAccum = 0;
            _nextSpawn = 0;
            NewClient();
            ServerPlayer me = AddServerPlayer(LocalConnectionId);
            // Your locker gun (battle royale): you play on this server, so it knows your pick. Players who join over
            // the network start with the server's default until the hello carries their pick.
            if (Server.Br != null && LocalLocker != Item.None) Server.SetLocker(me, LocalLocker);
        }

        /// <summary>The local player's battle royale locker gun (SMG, AR or pistol; None = the default). Set before the session is made.</summary>
        public static Item LocalLocker = Item.None;
        /// <summary>The local player's slot auto-sort. Set before the session is made; <see cref="SetLocalPrefs"/> changes it live.</summary>
        public static SlotPrefs LocalSlotPrefs = SlotPrefs.Default;

        /// <summary>What every new client tells its server (after the welcome): the auto-sort and the locker gun.</summary>
        private static PrefsMsg LocalPrefs => new PrefsMsg { Slots = LocalSlotPrefs, Locker = LocalLocker };

        /// <summary>The settings menu changed the auto-sort: the server hears it at once (it applies from the next pickup).</summary>
        public void SetLocalPrefs(in SlotPrefs prefs)
        {
            LocalSlotPrefs = prefs;
            Client?.SetPrefs(LocalPrefs);
        }

        /// <summary>A fresh client with its own copy of the map (the server sends everything sculpted since).</summary>
        private void NewClient()
        {
            ClientGrid = _firstClientGrid ?? _makeGrid();
            _firstClientGrid = null;
            var sculpt = new SculptClient(ClientGrid, _sculptConfig, _movement);
            Client = new NetClient(_terrain(ClientGrid), _movement, _config, (d, r) => _up.Send(d, r, _nowMs), sculpt);
            Client.Prefs = LocalPrefs; // sent to the server after the welcome (a joiner's locker pick rides here too)
            ClientChanged?.Invoke();
        }

        private void NewLocalLinks()
        {
            _up = new SimulatedLink(SimUp, 1);
            _down = new SimulatedLink(SimDown, 2);
        }

        private ServerPlayer AddServerPlayer(int connectionId)
        {
            int k = _nextSpawn++;
            int team = 1 + (k % 2);
            Vec3d[] spots = _teamSpawns[team - 1];
            Vec3d spawn = spots[(k / 2) % spots.Length];
            return Server.AddPlayer(connectionId, team, spawn, _teamYaw[team - 1]);
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
                // Nothing is added until the connection says hello: play or watch (protocol 30).
                _pendingConns.Add(id);
                Debug.Log($"[SCULPT] Connection {id} opened.");
            };
            _fish.ServerClientDisconnected += id =>
            {
                _pendingConns.Remove(id);
                bool watcher = Server != null && Server.IsSpectator(id);
                Server?.RemovePlayer(id);
                Debug.Log($"[SCULPT] {(watcher ? "Spectator" : "Player")} left (connection {id}).");
            };
            _fish.ServerReceived += (id, data, reliable) =>
            {
                if (Server == null) return;
                if (_pendingConns.Contains(id)) { OnHello(id, data); return; }
                Server.Receive(id, data, 0, data.Length);
            };
            _fish.ClientConnected += () =>
            {
                if (Mode != NetMode.Client) return;
                Status = Spectating ? $"Watching {JoinAddress}:{Port}" : $"Connected to {JoinAddress}:{Port}";
                _fish?.SendToServer(SpecProtocol.Hello(Spectating), true);
            };
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

        /// <summary>A new connection's first packet: hello (play or watch), or anything else from an older build.</summary>
        private void OnHello(int id, byte[] data)
        {
            _pendingConns.Remove(id);
            if (!SpecProtocol.TryReadHello(data, 0, data.Length, out byte version, out bool spectate) || version != Protocol.Version)
            {
                Server.SendVersion(id); // their client shows "the host runs a newer/older build"
                Debug.Log($"[SCULPT] Connection {id} runs another build (protocol {(version == 0 ? "older than 30" : version.ToString())}).");
                return;
            }
            if (spectate)
            {
                Server.AddSpectator(id);
                Debug.Log($"[SCULPT] Spectator joined (connection {id}), watching {SpectatorDelaySeconds} s behind.");
            }
            else
            {
                AddServerPlayer(id);
                Debug.Log($"[SCULPT] Player joined (connection {id}).");
            }
            if (Mode == NetMode.Host)
            {
                int watchers = Server.SpectatorConnections.Count;
                Status = $"Hosting on port {Port}: {Server.Players.Count} player(s)" + (watchers > 0 ? $", {watchers} watching" : "");
            }
        }

        // ================================================================ Replays

        private void StartRecording()
        {
            if (!RecordReplays || Server == null) return;
            try
            {
                RecordingPath = ReplayLibrary.NewPath(MapName);
                _recFile = new FileStream(RecordingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                MatchStream stream = Server.StartStream();
                _recWriter = new ReplayWriter(_recFile, new ReplayHeader
                {
                    Map = MapName,
                    Mode = ModeLabel,
                    Build = Application.version,
                    StartedUtcTicks = DateTime.UtcNow.Ticks,
                }, stream);
                _recMovedMeters = 0;
                _recHadLast = false;
                _recMatchPlayed = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SCULPT] Replay recording off: " + e.Message);
                _recFile?.Dispose();
                _recFile = null;
                _recWriter = null;
                RecordingPath = null;
            }
        }

        /// <summary>Per frame while recording: is anything worth keeping happening (a match, or the local player moving about)?</summary>
        private void WatchRecording()
        {
            if (_recWriter == null || Server == null) return;
            if (Server.Match.Phase == MatchPhase.Playing) _recMatchPlayed = true;
            ServerPlayer me = LocalServerPlayer;
            if (me == null || me.State.Dead) { _recHadLast = false; return; }
            if (_recHadLast)
            {
                double d = (me.State.Position - _recLastPos).Length;
                if (d < 4) _recMovedMeters += d; // respawns don't count
            }
            _recLastPos = me.State.Position;
            _recHadLast = true;
        }

        /// <summary>Finishes the file. Keeps it if something happened (a kill, a match, or 50 m of moving about for 20 s);
        /// the menu's background practice server and quick peeks are deleted. Then trims to the newest 20.</summary>
        private void StopRecording()
        {
            if (_recWriter == null) return;
            string path = RecordingPath;
            bool keep = false;
            try
            {
                MatchStream stream = Server?.Stream;
                int kills = stream?.Markers.Count ?? 0;
                double seconds = stream != null && stream.LastTick != int.MinValue ? (stream.LastTick - stream.FirstTick) / (double)SimTime.TicksPerSecond : 0;
                keep = seconds >= 20 && (kills > 0 || _recMatchPlayed || _recMovedMeters >= 50);
                _recWriter.Finish();
                Server?.StopStream();
            }
            catch (Exception e) { Debug.LogWarning("[SCULPT] Replay didn't finish cleanly: " + e.Message); }
            _recFile?.Dispose();
            _recFile = null;
            _recWriter = null;
            RecordingPath = null;
            try
            {
                if (!keep && path != null) File.Delete(path);
                else if (path != null) Debug.Log($"[SCULPT] Replay saved: {path}");
                ReplayLibrary.Prune();
            }
            catch (Exception e) { Debug.LogWarning("[SCULPT] Replay folder: " + e.Message); }
        }

        /// <summary>Plays a saved replay (the scene must already be built for its map). False with a reason if it can't.</summary>
        public bool StartReplay(string path, out string error)
        {
            error = null;
            ReplayFile file;
            try { file = ReplayFile.Read(File.ReadAllBytes(path)); }
            catch (Exception e) { error = "The replay can't be read: " + e.Message; return false; }
            if (file.Incompatible != null) { error = "This replay was " + file.Incompatible + "."; return false; }
            if (file.Segments.Count == 0) { error = "The replay is empty."; return false; }
            StopRecording();
            ShutdownFishNet();
            Server = null;
            ServerSculpt = null;
            Spectating = false;
            NewLocalLinks();
            try
            {
                Replay = new ReplayPlayer(file, () => { NewClient(); return Client; });
            }
            catch (Exception e)
            {
                error = "The replay can't be played: " + e.Message;
                StartOffline();
                return false;
            }
            Mode = NetMode.Replay;
            Status = $"Replay: {file.Header.Mode} on {file.Header.Map}" + (file.Complete ? "" : " (cut short)");
            Debug.Log($"[SCULPT] Playing replay {path}: {file.Seconds:0} s, {file.Segments.Count} segments, {file.Markers.Count} kills.");
            return true;
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

            if (Replay != null && Mode == NetMode.Replay) Replay.Advance(dt);
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
            WatchRecording();
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
            StopRecording();
            ShutdownFishNet();
        }
    }
}
