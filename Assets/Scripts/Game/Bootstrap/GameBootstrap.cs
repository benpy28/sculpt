using System;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Net;
using Sculpt.Game.Cameras;
using Sculpt.Game.Diagnostics;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Player;
using Sculpt.Game.Sculpting;
using Sculpt.Game.Settings;
using Sculpt.Game.UI;
using Sculpt.Game.Weapons;
using Sculpt.Game.World;
using UnityEngine;

namespace Sculpt.Game.Bootstrap
{
    /// <summary>
    /// The single object in the Bootstrap scene. Builds the world, player, camera, light and HUD from code,
    /// and runs the fixed 60 Hz simulation loop (own accumulator, so input is sampled right before ticking).
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Tooltip("Optional. Assets > Create > SCULPT > Movement Settings. Edits apply live in Play mode.")]
        [SerializeField] private MovementSettings movementSettings;
        [Tooltip("Optional. Assets > Create > SCULPT > Weapon Settings. Edits apply live in Play mode.")]
        [SerializeField] private WeaponSettings weaponSettings;
        [Tooltip("Optional. Assets > Create > SCULPT > Breach Settings. Destruction, healing and bar numbers, plus the parked block-moving switch.")]
        [SerializeField] private BreachSettings breachSettings;
        [Tooltip("On: the breach demo map (buildings, movement course, F6 showcase). Off: the old map picked below.")]
        [SerializeField] private bool breachMap = true;

        public enum MapKind { Arena, MovementCourse }

        // ---- What the next scene load starts (the main menu picks it, then reloads the scene) ----
        public enum Launch { Menu, Offline, Host, Join, VsBot }
        public enum MapChoice { Duel, Breach }
        public static Launch NextLaunch = Launch.Menu;
        public static MapChoice NextMap = MapChoice.Duel;
        public static string NextAddress = "";
        /// <summary>How good the bot is in a "Play vs bot" launch.</summary>
        public static BotLevel NextBotLevel = BotLevel.Normal;
        private static bool _commandLineDone;

        /// <summary>Reloads the game with a new map and start (menu buttons). Everything is rebuilt from code.</summary>
        public static void Relaunch(Launch launch, MapChoice map, string address = "")
        {
            NextLaunch = launch;
            NextMap = map;
            NextAddress = address ?? "";
            UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }

        public static MapChoice CurrentMap { get; private set; }

        /// <summary>Each Play press starts at the menu, even with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLaunch()
        {
            NextLaunch = Launch.Menu;
            NextMap = MapChoice.Duel;
            NextAddress = "";
            _commandLineDone = false;
        }

        [Tooltip("Arena: the seeded 5v5 test arena. Movement Course: the M2 movement test map.")]
        [SerializeField] private MapKind map = MapKind.Arena;
        [SerializeField] private int mapSeed = 1;

        private SubGrid _grid;
        private WorldView _world;
        private LocalPlayer _player;
        private CameraRig _rig;
        private GameInput _input;
        private NetSession _session;
        private WeaponView _weapons;
        private MovementConfig _movement;

        private void Awake()
        {
            Time.fixedDeltaTime = 1f / SimTime.TicksPerSecond;
            Physics.simulationMode = SimulationMode.Script; // Core does all collision
            QualitySettings.vSyncCount = 0;                 // lowest input latency
            Application.targetFrameRate = -1;
            Application.runInBackground = true;           // two windows on one machine both keep simulating

            var sculptConfig = new SculptConfig();
            var movement = new MovementConfig();
            _movement = movement;
            if (weaponSettings != null) weaponSettings.ApplyTo(movement.Weapons);
            if (breachSettings == null) breachSettings = ScriptableObject.CreateInstance<BreachSettings>(); // code defaults
            breachSettings.ApplyTo(sculptConfig);
            breachSettings.ApplyTo(movement.Items);
            breachSettings.ApplyTo(movement.Punch);
            var breach = new BreachConfig();
            breachSettings.ApplyTo(breach);
            var netConfig = new NetConfig { AllowDevWarp = GameInput.DevBuild };
            // The client's own copy of the map (what it draws and predicts against). The server builds its own.
            Vec3d[][] spawns;
            double[] yaw;
            Func<SubGrid> build;
            MapInfo mapInfo = null;
            BreachMapInfo breachInfo = null;
            BrushWorld brushes = null;
            Func<SubGrid, ITerrain> terrain = null;
            if (!_commandLineDone) ReadLaunchArgs();
            CurrentMap = NextMap;
            if (NextMap == MapChoice.Duel)
            {
                // The 1v1 friend demo map (Ben 2026-11-25).
                build = DuelMap.BuildGround;
                _grid = DuelMap.BuildGround();
                brushes = DuelMap.BuildBrushes(out breachInfo, breach);
                terrain = g => DuelMap.Terrain(g, DuelMap.BuildBrushes(out _, breach));
                spawns = new[] { breachInfo.TeamSpawns[0].ToArray(), breachInfo.TeamSpawns[1].ToArray() };
                yaw = breachInfo.SpawnYaw;
            }
            else if (breachMap)
            {
                build = BreachMap.BuildGround;
                _grid = BreachMap.BuildGround();
                // Both sides build the same brushes from code; only tile changes travel (server to client).
                brushes = BreachMap.BuildBrushes(out breachInfo, breach); // drawn (static parts); tiles draw the client's copy
                terrain = g => BreachMap.Terrain(g, BreachMap.BuildBrushes(out _, breach)); // the server and each client get their own copy
                spawns = new[] { breachInfo.TeamSpawns[0].ToArray(), breachInfo.TeamSpawns[1].ToArray() };
                yaw = breachInfo.SpawnYaw;
                netConfig.DevWarpPoint = breachInfo.Course.Start;
            }
            else if (map == MapKind.Arena)
            {
                int seed = mapSeed;
                build = () => MapGen.Generate(seed, out _);
                _grid = MapGen.Generate(seed, out MapInfo info);
                mapInfo = info;
                spawns = new[] { info.TeamSpawns[0].ToArray(), info.TeamSpawns[1].ToArray() };
                yaw = info.SpawnYaw;
            }
            else
            {
                build = () => TestMaps.BuildM2(out _, out _);
                _grid = TestMaps.BuildM2(out Vec3d spawn, out double spawnYaw);
                Vec3d[] pads = SpawnPads(spawn);
                spawns = new[] { pads, pads };
                yaw = new[] { spawnYaw, spawnYaw };
            }
            Action<NetServer> setup = null;
            bool vsBot = NextLaunch == Launch.VsBot && NextMap == MapChoice.Duel;
            if (vsBot)
            {
                // Play vs bot: the same 1v1 match on the local server, the bot is the second player (team 2).
                BreachMapInfo bi = breachInfo;
                Vec3d botSpawn = spawns[1][0];
                double botYaw = yaw[1];
                BotLevel level = NextBotLevel;
                setup = server =>
                {
                    foreach (GroundItem g in bi.ItemSpawns) server.SpawnItem(g.Kind, g.Count, g.Position);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    BotNav nav = BotNav.Build(server.Grid, 0, 0, bi.SizeX, bi.SizeZ);
                    server.AddBot(new DuelBot(nav, DuelBotConfig.For(level), Environment.TickCount), 2, botSpawn, botYaw);
                    Debug.Log($"[SCULPT] Bot ({level}) added; nav {nav.Nodes.Count} spots in {sw.ElapsedMilliseconds} ms.");
                };
            }
            else if (breachInfo != null)
            {
                BreachMapInfo bi = breachInfo;
                var dummy = new DummyConfig();
                breachSettings.ApplyTo(dummy);
                bool withDummy = breachSettings.practiceDummy && bi.Room != null;
                setup = server =>
                {
                    foreach (GroundItem g in bi.ItemSpawns) server.SpawnItem(g.Kind, g.Count, g.Position);
                    // Practice dummy in the sealed room: break in, push through, watch it heal (Ben, step 7).
                    if (withDummy) server.AddDummy(new PracticeDummy(bi.RoomCentre, Math.PI, bi.Room, dummy), team: 2);
                };
            }
            _session = new NetSession(build, _grid, movement, netConfig, sculptConfig, spawns, yaw, terrain, setup);
            _session.MatchWhenHosting = CurrentMap == MapChoice.Duel;
            if (vsBot) { _session.MatchOffline = true; _session.Server.MatchConfig.Enabled = true; }

            _world = new GameObject("World").AddComponent<WorldView>();
            _world.Init(_grid, sculptConfig, mapInfo);
            _world.Bind(_session.ClientGrid, _session.Client.Sculpt);
            _session.ClientChanged += () => _world.Bind(_session.ClientGrid, _session.Client.Sculpt);
            if (brushes != null)
            {
                var buildings = new GameObject("Buildings").AddComponent<BrushView>();
                buildings.Build(brushes);
                buildings.BindTiles(_session.Client.Walls);
                _session.ClientChanged += () => buildings.BindTiles(_session.Client.Walls);
            }

            // Sky, haze, fixed sun and the distant hills around the arena.
            float half = (float)(_grid.SizeX * SubGrid.SubSize * 0.5);
            Scenery.Build(half, (float)(_grid.SizeZ * SubGrid.SubSize * 0.5));

            _input = new GameInput();
            var client = ClientSettings.Load();

            _player = new GameObject("Local Player").AddComponent<LocalPlayer>();
            _player.Init(_session, movement, movementSettings, _input, client);
            if (breachInfo?.Course != null) _player.SetCourse(breachInfo.Course);
            _world.TrackPlayers(_player, _session);

            new GameObject("Remote Players").AddComponent<RemotePlayersView>().Init(_session, movement);
            new GameObject("Ground Items").AddComponent<ItemsView>().Init(_session, _player);

            _rig = new GameObject("Camera Rig").AddComponent<CameraRig>();
            _rig.Init();
            var firstPerson = new FirstPersonMode(_player, client);
            _rig.Mode = firstPerson;

            _weapons = new GameObject("Weapon View").AddComponent<WeaponView>();
            _weapons.Init(_player, _session, _rig, firstPerson, client, movement);
            _weapons.Grenades = new GameObject("Grenades").AddComponent<GrenadeView>();
            _weapons.Grenades.Init(_session, firstPerson.Shake);

            gameObject.AddComponent<DevHud>().Init(_player, client, _session);
            _player.gameObject.AddComponent<MovementAudio>().Init(_player);
            gameObject.AddComponent<AmbientAudio>().Init(client, Perches(mapInfo));

            var sculptView = new GameObject("Sculpt View").AddComponent<SculptView>();
            sculptView.Init(_player, _session, _rig, _world, firstPerson.Shake);
            gameObject.AddComponent<SculptHud>().Init(_player, _session, sculptView, _rig.Camera);
            new GameObject("Tile Heal View").AddComponent<TileHealView>().Init(_player, _session, _rig);
            gameObject.AddComponent<CombatHud>().Init(_player, _session, _weapons, _rig.Camera);
            gameObject.AddComponent<FeelHud>().Init(_player, client);
            gameObject.AddComponent<SettingsMenu>().Init(_player, client, _input);
            gameObject.AddComponent<MatchHud>().Init(_player, _session);
            var menu = gameObject.AddComponent<MainMenu>();
            menu.Init(_player, _session);

            switch (NextLaunch)
            {
                case Launch.Host: _session.StartHost(FishNetTransport.DefaultPort); break;
                case Launch.Join: JoinAddress(NextAddress); break;
                case Launch.Menu: menu.Open(); break;
            }
            ApplyCommandLine(); // sim ping/loss/jitter, and a non-default -port
            // Frame pacing, lean rendering, the performance overlay and the -benchmark run.
            PerfSetup.Install(gameObject, _session, _player, _input, _rig, _grid);

            Debug.Log($"[SCULPT] Bootstrap OK. Sim tick rate {SimTime.TicksPerSecond} Hz. Map {(NextMap == MapChoice.Duel ? "1v1 duel" : breachMap ? "Breach" : map.ToString())} {_grid.SizeX / SubGrid.TileSubs}x{_grid.SizeZ / SubGrid.TileSubs} tiles, {_grid.ChunkCount} chunks ({_grid.AllocatedChunks} with detail).");
        }

        private void Update()
        {
            double dt = Math.Min(Time.unscaledDeltaTime, 0.25f);
#if UNITY_EDITOR
            if (weaponSettings != null) weaponSettings.ApplyTo(_movement.Weapons); // live tuning in Play mode
#endif
            _session.BeginFrame(dt);       // read network, run server ticks (offline/host), deliver snapshots
            _player.Events.Clear();        // this frame's movement events (read in LateUpdate by HUD and audio)
            _player.SampleFrame();

            NetClient net = _session.Client;
            int ticks = net.BeginFrame(dt); // client clock runs a little ahead of the server
            for (int i = 0; i < ticks; i++) _player.Tick();
            _session.EndFrame();            // inputs leave this frame
            PerfRuntime.PacketsSent();      // latency overlay: this frame's commands are on the wire

            net.UpdateVisual(dt);
            _player.Render((float)net.Alpha);
            _weapons.PreCamera(Time.unscaledDeltaTime);  // this frame's shots kick the view before it is placed
            _rig.Apply(Time.unscaledDeltaTime);
            _weapons.PostCamera(Time.unscaledDeltaTime); // tracers from the muzzle as drawn this frame
            _world.RebuildDirty();
        }

        /// <summary>Points in the tree canopies where birds sing (none on the movement course).</summary>
        private static System.Collections.Generic.List<Vector3> Perches(MapInfo info)
        {
            var list = new System.Collections.Generic.List<Vector3>();
            if (info == null) return list;
            foreach (TreeInfo t in info.Trees)
                if (t.Leaves.Count > 0)
                    list.Add((t.Leaves[t.Leaves.Count / 2].ToUnity() + Vector3.one * 0.5f) * (float)SubGrid.SubSize);
            return list;
        }

        /// <summary>Spawn spots on the 3x3 spawn pad, centre first.</summary>
        private static Vec3d[] SpawnPads(Vec3d centre)
        {
            double b = SubGrid.TileSize;
            return new[]
            {
                centre,
                centre + new Vec3d(b, 0, 0),
                centre + new Vec3d(-b, 0, 0),
                centre + new Vec3d(0, 0, -b),
                centre + new Vec3d(b, 0, -b),
                centre + new Vec3d(-b, 0, -b),
            };
        }

        /// <summary>Joins "address" or "address:port".</summary>
        private void JoinAddress(string text)
        {
            string addr = (text ?? "").Trim();
            ushort port = FishNetTransport.DefaultPort;
            int colon = addr.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(addr.Substring(colon + 1), out ushort cp)) { port = cp; addr = addr.Substring(0, colon); }
            if (addr.Length > 0) _session.Join(addr, port);
        }

        /// <summary>First boot only: -host / -connect address[:port] / -breach skip the menu.</summary>
        private static void ReadLaunchArgs()
        {
            _commandLineDone = true;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-host") NextLaunch = Launch.Host;
                else if (a == "-connect" && i + 1 < args.Length) { NextLaunch = Launch.Join; NextAddress = args[i + 1]; }
                else if (a == "-offline") NextLaunch = Launch.Offline;
                else if (a == "-bot") NextLaunch = Launch.VsBot;
                else if (a == "-breach") NextMap = MapChoice.Breach;
            }
        }

        /// <summary>-port N, -simping ms, -simloss percent, -simjitter ms (host and join come from ReadLaunchArgs).</summary>
        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            ushort port = FishNetTransport.DefaultPort;
            string connect = null;
            bool host = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "-host") host = true;
                else if (a == "-connect" && next != null) connect = next;
                else if (a == "-port" && next != null && ushort.TryParse(next, out ushort p)) port = p;
                else if (a == "-simping" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ping))
                { _session.SimUp.LatencyMs = ping / 2; _session.SimDown.LatencyMs = ping / 2; }
                else if (a == "-simloss" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double loss))
                { _session.SimUp.LossPercent = loss; _session.SimDown.LossPercent = loss; }
                else if (a == "-simjitter" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double jit))
                { _session.SimUp.JitterMs = jit; _session.SimDown.JitterMs = jit; }
            }
            if (host && port != FishNetTransport.DefaultPort) _session.StartHost(port);
            if (connect != null && port != FishNetTransport.DefaultPort && connect.LastIndexOf(':') < 0) _session.Join(connect, port);
        }

        private void OnDestroy()
        {
            _input?.Dispose();
            _session?.Dispose();
        }
    }
}
