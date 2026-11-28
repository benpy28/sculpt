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
        public enum Launch { Menu, Offline, Host, Join, VsBot, Replay, Spectate }
        public enum MapChoice { Duel, Breach, BattleRoyale }
        public static Launch NextLaunch = Launch.Menu;
        public static MapChoice NextMap = MapChoice.Duel;
        public static string NextAddress = "";
        /// <summary>The UDP port this run hosts on (-port N on the command line; read once, at first boot).</summary>
        public static ushort HostPort = FishNetTransport.DefaultPort;
        /// <summary>The replay file a Replay launch plays.</summary>
        public static string NextReplayPath = "";
        /// <summary>Why the last replay couldn't be played (the menu shows it), or empty.</summary>
        public static string ReplayError = "";
        /// <summary>How good the bot is in a "Play vs bot" launch.</summary>
        public static BotLevel NextBotLevel = BotLevel.Normal;
        /// <summary>Solos, duos or squads in a battle royale launch.</summary>
        public static BrMode NextBrMode = BrMode.Solos;
        /// <summary>
        /// The battle royale bots' walking grid takes a few seconds to build, so it is built once per run on a worker
        /// thread (from its own copy of the map); bots stand still in the lobby until it is ready.
        /// </summary>
        private static System.Threading.Tasks.Task<BotNav> _brNav;
        private static bool _commandLineDone;

        /// <summary>Reloads the game with a new map and start (menu buttons). Everything is rebuilt from code.</summary>
        /// <summary>Plays a saved replay: reloads the scene on the replay's map, then feeds it in.</summary>
        public static void RelaunchReplay(string path, string mapName)
        {
            NextReplayPath = path ?? "";
            Relaunch(Launch.Replay, MapOf(mapName));
        }

        /// <param name="note">The loading card's second line, if the caller knows better (a room code and its ping).</param>
        public static void Relaunch(Launch launch, MapChoice map, string address = "", string note = null)
        {
            // Through the loading screen: it fades in, the scene reloads under it, then it fades away (Ben 2026-11-26).
            // A press while a load is already under way is ignored.
            if (LoadingScreen.Busy) return;
            LoadingTitle(launch, map, address, out string title, out string detail);
            if (!string.IsNullOrEmpty(note)) detail = note;
            LoadingScreen.Run(title, detail, () =>
            {
                NextLaunch = launch;
                NextMap = map;
                NextAddress = address ?? "";
                _relaunchClock = System.Diagnostics.Stopwatch.StartNew();
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            });
        }

        /// <summary>What the loading card says for a launch.</summary>
        public static void LoadingTitle(Launch launch, MapChoice map, string address, out string title, out string detail)
        {
            string mapName = map == MapChoice.BattleRoyale ? "Royale map" : map == MapChoice.Breach ? "Breach test map" : "1v1 map";
            string where = string.IsNullOrEmpty(address) ? "" : "  ·  " + address;
            switch (launch)
            {
                case Launch.Menu: title = "Main menu"; detail = "Back to the home screen"; break;
                case Launch.Host:
                    title = map == MapChoice.BattleRoyale ? "Hosting a battle royale" : "Hosting a 1v1";
                    detail = (map == MapChoice.BattleRoyale ? NextBrMode + "  ·  " : "") + mapName + "  ·  on your network";
                    break;
                case Launch.Join: title = "Joining"; detail = mapName + where; break;
                case Launch.VsBot: title = "1v1 vs bot"; detail = NextBotLevel + "  ·  " + mapName; break;
                case Launch.Replay: title = "Replay"; detail = mapName; break;
                case Launch.Spectate: title = "Watching live"; detail = "On " + (string.IsNullOrEmpty(address) ? "the host" : address); break;
                default:
                    title = map == MapChoice.BattleRoyale ? "Battle royale" : "Practice";
                    detail = (map == MapChoice.BattleRoyale ? NextBrMode + "  ·  32 players, bots fill  ·  " : "") + mapName;
                    break;
            }
        }

        private static System.Diagnostics.Stopwatch _relaunchClock;
        /// <summary>The duel map's bot nav never changes: built once per run, not on every menu reload (0.6 s).</summary>
        private static BotNav _duelNav;

        public static MapChoice CurrentMap { get; private set; }

        /// <summary>The map a replay header names.</summary>
        public static MapChoice MapOf(string mapName) => mapName == "Breach" ? MapChoice.Breach : mapName == "BattleRoyale" ? MapChoice.BattleRoyale : MapChoice.Duel;

        /// <summary>Each Play press starts at the menu, even with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLaunch()
        {
            NextLaunch = Launch.Menu;
            NextMap = MapChoice.Duel;
            NextAddress = "";
            HostPort = FishNetTransport.DefaultPort;
            _commandLineDone = false;
        }

        [Tooltip("Arena: the seeded 5v5 test arena. Movement Course: the M2 movement test map.")]
        [SerializeField] private MapKind map = MapKind.Arena;
        [SerializeField] private int mapSeed = 1;

        private SubGrid _grid;
        private FirstPersonMode _firstPerson;
        private SpectatorCamera _spectatorCam;
        private BrDeathCamera _brDeathCam;
        private WorldView _world;
        private LocalPlayer _player;
        private CameraRig _rig;
        private GameInput _input;
        private NetSession _session;
        private WeaponView _weapons;
        private MovementConfig _movement;

        private void Awake()
        {
            var loadClock = System.Diagnostics.Stopwatch.StartNew();
            _awakeClock = loadClock;
            var loadSteps = new System.Text.StringBuilder();
            void Mark(string what) { loadSteps.Append(what).Append(' ').Append(loadClock.ElapsedMilliseconds).Append(" ms, "); }
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
            breachSettings.ApplyTo(movement.Hand);
            var breach = new BreachConfig();
            breachSettings.ApplyTo(breach);
            var netConfig = new NetConfig { AllowDevWarp = GameInput.DevBuild, AllowDevRespawn = GameInput.DevBuild };
            // The client's own copy of the map (what it draws and predicts against). The server builds its own.
            Vec3d[][] spawns;
            double[] yaw;
            Func<SubGrid> build;
            MapInfo mapInfo = null;
            BreachMapInfo breachInfo = null;
            BrushWorld brushes = null;
            Func<SubGrid, ITerrain> terrain = null;
            if (!_commandLineDone) ReadLaunchArgs();
            HookCoreProfiling();
            if (!Array.Exists(Environment.GetCommandLineArgs(), a => a == "-nojitwarm")) JitWarmup.Start(typeof(NetServer).Assembly); // compile the simulation while the map loads
            CurrentMap = NextMap;
            BrMapInfo brInfo = null;
            if (NextMap == MapChoice.BattleRoyale)
            {
                // The battle royale (Ben 2026-11-26): 32 players, bots fill the lobby.
                // One build of the map; the server's and the client's copies share everything that never changes with
                // it and copy a page of tile state only when it is first damaged (map v3 groundwork: three full copies
                // of the 921 m map were about 3 x 115 MB).
                var groundTask = System.Threading.Tasks.Task.Run(BrMap.BuildGround); // side by side with the buildings
                brushes = BrMap.BuildBrushes(out breachInfo, out brInfo, breach);
                SubGrid ground = groundTask.Result;
                _grid = ground;
                build = ground.ShareCopy;
                brushes.Tiles.Prepare(); // which tile lies across each cell edge: worked out once, shared
                brushes.Freeze();        // before the bots' walking grid reads it on a worker
                BrushWorld template = brushes;
                terrain = g => BrMap.Terrain(g, template.ShareCopy());
                spawns = new[] { breachInfo.TeamSpawns[0].ToArray(), breachInfo.TeamSpawns[1].ToArray() };
                yaw = new double[] { brInfo.Regions[0].PadYaw[0], brInfo.Regions[0].PadYaw[0] };
                // The bots' walking grid from a shared copy of its own (it opens every door), made here before the worker starts.
                BreachMapInfo navInfo = breachInfo;
                if (_brNav == null)
                {
                    BrushWorld navCopy = brushes.ShareCopy();
                    SubGrid navGround = ground.ShareCopy();
                    _brNav = System.Threading.Tasks.Task.Run(() => BrMap.BuildNav(navCopy, navInfo, 1.0, navGround));
                }
                Sculpt.Game.UI.BrHud.SetBuildings(breachInfo.Block.Buildings); // the minimap
            }
            else if (NextMap == MapChoice.Duel)
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
            Mark("map");
            Action<NetServer> setup = null;
            bool vsBot = NextLaunch == Launch.VsBot && NextMap == MapChoice.Duel;
            if (brInfo != null)
            {
                BreachMapInfo bi = breachInfo;
                BrMapInfo bri = brInfo;
                var navTask = _brNav;
                var cfg = new BrConfig { Mode = NextBrMode, Seed = Environment.TickCount };
                cfg.MakeBot = i => new PendingBot(navTask, (BotLevel)(i % 3), cfg.Seed + 31 * i);
                setup = server =>
                {
                    server.EnableBattleRoyale(cfg, bri);
                    BrSetup.PlaceItems(server, bi, bri);
                };
            }
            else if (vsBot)
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
                    BotNav nav = NextMap == MapChoice.Duel ? (_duelNav ??= DuelMap.BuildNav()) : BotNav.Build(server.Grid, 0, 0, bi.SizeX, bi.SizeZ); // duel: doors count as open (the bot punches them)
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
            string mapName = NextMap == MapChoice.BattleRoyale ? "BattleRoyale" : NextMap == MapChoice.Duel ? "Duel" : breachMap ? "Breach" : map.ToString();
            string modeLabel = brInfo != null ? "Battle royale " + NextBrMode + (NextLaunch == Launch.Host ? " hosted" : "")
                : vsBot ? "Vs bot " + NextBotLevel : NextLaunch == Launch.Host ? "1v1 hosted" : "Practice";
            NetSession.LocalLocker = LockerPick.Load();
            NetSession.LocalSlotPrefs = SlotPrefsStore.Load();
            _session = new NetSession(build, _grid, movement, netConfig, sculptConfig, spawns, yaw, terrain, setup, mapName, modeLabel);
            // The server's sight grid (seconds on the village map) builds alongside the rest of the load: the server
            // doesn't tick until the first frame.
            NetServer sightServer = _session.Server;
            System.Threading.Tasks.Task sightTask = brInfo != null && sightServer != null ? System.Threading.Tasks.Task.Run(() => sightServer.PrepareSight()) : null;
            _session.MatchWhenHosting = CurrentMap == MapChoice.Duel;
            if (vsBot) { _session.MatchOffline = true; _session.Server.MatchConfig.Enabled = true; }

            Mark("session");
            _world = new GameObject("World").AddComponent<WorldView>();
            GroundCover cover = null;
            if (brushes != null)
            {
                // Building interiors get concrete ground floors: the block's buildings and the breach map's sealed room.
                var paved = new System.Collections.Generic.List<Aabb>();
                if (breachInfo?.Block != null) paved.AddRange(breachInfo.Block.Buildings);
                if (breachInfo?.Room != null && breachInfo.Room.Length > 0)
                {
                    Aabb r = brushes.Tiles.BoundsOf(breachInfo.Room[0]);
                    foreach (int t in breachInfo.Room)
                    {
                        Aabb o = brushes.Tiles.BoundsOf(t);
                        r = new Aabb(new Vec3d(Math.Min(r.Min.X, o.Min.X), 0, Math.Min(r.Min.Z, o.Min.Z)), new Vec3d(Math.Max(r.Max.X, o.Max.X), 1, Math.Max(r.Max.Z, o.Max.Z)));
                    }
                    paved.Add(r);
                }
                cover = new GroundCover(_grid, paved);
            }
            Mark("cover");
            _world.Init(_grid, sculptConfig, mapInfo, cover, brInfo != null ? BrNature.KindAt : (Func<Int3, byte>)null); // the battle royale plains
            Mark("world init");
            _world.Bind(_session.ClientGrid, _session.Client.Sculpt);
            _session.ClientChanged += () => _world.Bind(_session.ClientGrid, _session.Client.Sculpt);
            if (brInfo != null) new GameObject("Plains").AddComponent<Sculpt.Game.World.PropsView>().Build(_world.TerrainMaterial); // trees, boulders, bushes
            Mark("ground");
            if (brushes != null)
            {
                var buildings = new GameObject("Buildings").AddComponent<BrushView>();
                buildings.Build(brushes);
                buildings.BindTiles(_session.Client.Walls);
                _session.ClientChanged += () => buildings.BindTiles(_session.Client.Walls);
            }

            Mark("buildings");
            // Sky, haze, fixed sun and the distant hills around the arena.
            float half = (float)(_grid.SizeX * SubGrid.SubSize * 0.5);
            Scenery.Build(half, (float)(_grid.SizeZ * SubGrid.SubSize * 0.5));

            Mark("scenery");
            _input = new GameInput();
            var client = ClientSettings.Current;

            _player = new GameObject("Local Player").AddComponent<LocalPlayer>();
            _player.Init(_session, movement, movementSettings, _input, client);
            if (breachInfo?.Course != null)
            {
                _player.SetCourse(breachInfo.Course);
                new GameObject("Course Race").AddComponent<CourseRaceView>().Init(_player, movement);
            }
            _world.TrackPlayers(_player, _session);

            if (brInfo != null && _session.Server != null && Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-autoplay", StringComparison.OrdinalIgnoreCase)))
            {
                // Dev: a hard bot brain plays the local player (profiling a real first-person battle royale, no hands).
                var autoBot = new PendingBot(_brNav, BotLevel.Hard, 777);
                NetSession s = _session;
                InputCmd? autoLast = null;
                _player.AutoPilot = () =>
                {
                    // Never wait for the server thread (a person's input never does): mid-tick, hold the last command.
                    if (!System.Threading.Monitor.TryEnter(s.ServerLock)) return autoLast;
                    try
                    {
                        ServerPlayer sp = s.LocalServerPlayer;
                        if (sp == null || s.Server == null) return null;
                        autoLast = autoBot.Next(s.Server, sp);
                        return autoLast;
                    }
                    finally { System.Threading.Monitor.Exit(s.ServerLock); }
                };
                Debug.Log("[SCULPT] Autoplay: a bot plays the local player.");
            }
            new GameObject("Remote Players").AddComponent<RemotePlayersView>().Init(_session, movement);
            new GameObject("Ground Items").AddComponent<ItemsView>().Init(_session, _player);

            Mark("player");
            _rig = new GameObject("Camera Rig").AddComponent<CameraRig>();
            _rig.Init();
            var firstPerson = new FirstPersonMode(_player, client);
            _firstPerson = firstPerson;
            _rig.Mode = firstPerson;
            _spectatorCam = new SpectatorCamera(_player, client, _session, movement, _input);
            _brDeathCam = new BrDeathCamera(_player, client, _session, _input);

            _weapons = new GameObject("Weapon View").AddComponent<WeaponView>();
            _weapons.Init(_player, _session, _rig, firstPerson, client, movement);
            _weapons.Grenades = new GameObject("Grenades").AddComponent<GrenadeView>();
            _weapons.Grenades.Init(_session, firstPerson.Shake);

            Mark("weapons");
            gameObject.AddComponent<DevHud>().Init(_player, client, _session);
            _player.gameObject.AddComponent<MovementAudio>().Init(_player);
            gameObject.AddComponent<AmbientAudio>().Init(client, Perches(mapInfo));
            // Directional sound: every positional sound is heard the way it really reaches the camera's ears (round
            // through doorways and holes, or muffled through walls). One air grid per client world.
            SoundRouter.Bind(_session.Client.Grid, _rig.Camera.transform);
            _session.ClientChanged += () => SoundRouter.Bind(_session.Client.Grid, _rig.Camera.transform);
            Mark("sound routes");
            new GameObject("Remote Movement Audio").AddComponent<RemoteMovementAudio>().Init(_session);

            Mark("audio");
            var sculptView = new GameObject("Sculpt View").AddComponent<SculptView>();
            sculptView.Init(_player, _session, _rig, _world, firstPerson.Shake);
            gameObject.AddComponent<SculptHud>().Init(_player, _session, sculptView, _rig.Camera);
            new GameObject("Tile Heal View").AddComponent<TileHealView>().Init(_player, _session, _rig);
            gameObject.AddComponent<CombatHud>().Init(_player, _session, _weapons, _rig.Camera);
            gameObject.AddComponent<FeelHud>().Init(_player, client);
            CaptionsHud.Install(gameObject);
            gameObject.AddComponent<SettingsMenu>().Init(_player, client, _input);
            gameObject.AddComponent<InventoryHud>().Init(_player, _input);
            gameObject.AddComponent<MatchHud>().Init(_player, _session);
            gameObject.AddComponent<BrHud>().Init(_player, _session);
            if (brInfo != null) new GameObject("Storm").AddComponent<StormView>().Init(_session);
            gameObject.AddComponent<PingHud>().Init(_session, _rig.Camera);
            gameObject.AddComponent<SpectatorHud>().Init(_player, _session, _spectatorCam, _input);
            gameObject.AddComponent<LanRoomBeacon>().Init(_session); // room codes: answers LAN lookups while hosting
            var menu = gameObject.AddComponent<MainMenu>();
            menu.Init(_player, _session);

            Mark("huds");
            // The map was built several times over (the village map is about 90 MB a copy): clear the leftovers now, on
            // the loading screen, not in the first seconds of play (a collection then stalled the game for seconds).
            if (brInfo != null)
            {
                // Every big one-off build finishes here, on the loading screen: the bots' walking grid, the server's sight
                // grid and this client's sound grid ran on workers while the rest loaded. Finishing them in play cost
                // seconds of hitches (all cores busy, and garbage collections over a heap full of map copies).
                try { _brNav?.Wait(); } catch (System.Exception e) { Debug.LogException(e); }
                sightTask?.Wait();
                SoundRouter.WaitReady(20000);
                Mark("builds done");
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                Mark("collect");
            }
            Debug.Log("[SCULPT] Load: " + loadSteps + (_relaunchClock != null ? $" (menu press to here {_relaunchClock.ElapsedMilliseconds - loadClock.ElapsedMilliseconds} ms before this load)" : ""));
            _relaunchClock = null;
            switch (NextLaunch)
            {
                case Launch.Host: _session.StartHost(HostPort); break;
                case Launch.Join: JoinAddress(NextAddress); break;
                case Launch.Spectate: JoinAddress(NextAddress, spectate: true); break;
                case Launch.Replay:
                    if (!_session.StartReplay(NextReplayPath, out string why)) { ReplayError = why; Debug.LogWarning("[SCULPT] " + why); menu.Open(); }
                    else ReplayError = "";
                    break;
                case Launch.Menu: menu.Open(); break;
            }
            ApplyCommandLine(); // sim ping/loss/jitter
            // Frame pacing, lean rendering, the performance overlay and the -benchmark run.
            PerfSetup.Install(gameObject, _session, _player, _input, _rig, _grid);
            string tour = Sculpt.Game.Dev.PhotoTour.RequestedFolder();
            if (tour != null && breachInfo != null && (GameInput.DevBuild || Application.isEditor))
                gameObject.AddComponent<Sculpt.Game.Dev.PhotoTour>().Init(tour, _rig.Camera, breachInfo, CurrentMap == MapChoice.Duel, () => _session.Server?.Walls);
            Debug.Log($"[SCULPT] Bootstrap OK. Sim tick rate {SimTime.TicksPerSecond} Hz. Map {(NextMap == MapChoice.Duel ? "1v1 duel" : breachMap ? "Breach" : map.ToString())} {_grid.SizeX / SubGrid.TileSubs}x{_grid.SizeZ / SubGrid.TileSubs} tiles, {_grid.ChunkCount} chunks ({_grid.AllocatedChunks} with detail).");
        }

        private void Update()
        {
            if (_frameLog < 3 && _awakeClock != null) { _frameLog++; Debug.Log($"[SCULPT] Frame {_frameLog} at {_awakeClock.ElapsedMilliseconds} ms after Awake."); }
            double dt = Math.Min(Time.unscaledDeltaTime, 0.25f);
#if UNITY_EDITOR
            if (weaponSettings != null) weaponSettings.ApplyTo(_movement.Weapons); // live tuning in Play mode
#endif
            GarbageSections.Frame();
            GarbageSections.Begin(_gcSession);
            using (MkSession.Auto()) _session.BeginFrame(dt);
            GarbageSections.End(_gcSession); // read network, run server ticks (offline/host), deliver snapshots
            _player.Events.Clear();        // this frame's movement events (read in LateUpdate by HUD and audio)
            _player.SampleFrame();

            NetClient net = _session.Client;
            int ticks = net.BeginFrame(dt); // client clock runs a little ahead of the server
            GarbageSections.Begin(_gcPredict);
            using (MkPredict.Auto()) for (int i = 0; i < ticks; i++) _player.Tick();
            GarbageSections.End(_gcPredict);
            _session.EndFrame();            // inputs leave this frame
            PerfRuntime.PacketsSent();      // latency overlay: this frame's commands are on the wire

            GarbageSections.Begin(_gcVisual);
            using (MkVisual.Auto()) { net.UpdateVisual(dt); _player.Render((float)net.Alpha); }
            GarbageSections.End(_gcVisual);
            // Watching (live or a replay): the spectator camera, else first person.
            bool watching = net.IsSpectator;
            if (watching) _spectatorCam.HandleInput();
            else Player.RemotePlayersView.HiddenPlayer = 0;
            // Battle royale, out of the match: watch the living until the next one.
            bool brOut = !watching && net.Match.IsBr && net.Match.Phase == MatchPhase.Playing && net.Predicted.Dead;
            if (brOut) _brDeathCam.HandleInput();
            else _brDeathCam.Reset();
            if (!(_rig.Mode is Benchmark)) // a -benchmark run flies its own fixed route
                _rig.Mode = watching ? _spectatorCam : brOut ? _brDeathCam : (ICameraMode)_firstPerson;
            GarbageSections.Begin(_gcLate);
            _weapons.PreCamera(Time.unscaledDeltaTime);  // this frame's shots kick the view before it is placed
            _rig.Apply(Time.unscaledDeltaTime);
            _weapons.PostCamera(Time.unscaledDeltaTime); // tracers from the muzzle as drawn this frame
            using (MkWorld.Auto()) _world.RebuildDirty();
            GarbageSections.End(_gcLate);
        }

        // Named sections for -benchmarkmarkers and the Unity profiler.
        private static readonly Unity.Profiling.ProfilerMarker MkSession = new Unity.Profiling.ProfilerMarker("SCULPT.SessionFrame"),
            MkPredict = new Unity.Profiling.ProfilerMarker("SCULPT.Predict"), MkVisual = new Unity.Profiling.ProfilerMarker("SCULPT.Visual"),
            MkWorld = new Unity.Profiling.ProfilerMarker("SCULPT.WorldRebuild");
        private static Unity.Profiling.ProfilerMarker[] _coreMarkers;
        private static int _gcSession = 31, _gcPredict = 31, _gcVisual = 31, _gcLate = 31;

        /// <summary>Development builds: the server tick's sections (Core) show as SCULPT.S.* profiler markers.</summary>
        private static void HookCoreProfiling()
        {
            if (!Debug.isDebugBuild || _coreMarkers != null) return;
            int n = (int)global::Sculpt.Core.Diagnostics.Prof.Section.Count;
            _coreMarkers = new Unity.Profiling.ProfilerMarker[n];
            GarbageSections.Init();
            for (int i = 0; i < n; i++)
            {
                _coreMarkers[i] = new Unity.Profiling.ProfilerMarker("SCULPT.S." + (global::Sculpt.Core.Diagnostics.Prof.Section)i);
                GarbageSections.Register("S." + (global::Sculpt.Core.Diagnostics.Prof.Section)i); // ids 0..n-1 match the sections
            }
            _gcSession = GarbageSections.Register("SessionFrame");
            _gcPredict = GarbageSections.Register("Predict");
            _gcVisual = GarbageSections.Register("Visual");
            _gcLate = GarbageSections.Register("CameraAndWorld");
            if (GarbageSections.On)
            {
                global::Sculpt.Core.Diagnostics.Prof.BeginHook = i => { _coreMarkers[i].Begin(); GarbageSections.Begin(i); };
                global::Sculpt.Core.Diagnostics.Prof.EndHook = i => { GarbageSections.End(i); _coreMarkers[i].End(); };
            }
            else
            {
                global::Sculpt.Core.Diagnostics.Prof.BeginHook = i => _coreMarkers[i].Begin();
                global::Sculpt.Core.Diagnostics.Prof.EndHook = i => _coreMarkers[i].End();
            }
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

        /// <summary>Joins "address" or "address:port" (to play, or to watch).</summary>
        private void JoinAddress(string text, bool spectate = false)
        {
            string addr = (text ?? "").Trim();
            ushort port = FishNetTransport.DefaultPort;
            int colon = addr.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(addr.Substring(colon + 1), out ushort cp)) { port = cp; addr = addr.Substring(0, colon); }
            if (addr.Length > 0) _session.Join(addr, port, spectate);
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
                else if (a == "-br") { NextMap = MapChoice.BattleRoyale; if (NextLaunch == Launch.Menu) NextLaunch = Launch.Offline; }
                else if (a == "-duos") NextBrMode = BrMode.Duos;
                else if (a == "-squads") NextBrMode = BrMode.Squads;
                else if (a == "-phototour") { NextLaunch = Launch.Offline; NetSession.ThreadedServer = false; } // the tour damages walls on the server from the main thread
                else if (a == "-serverthread" && i + 1 < args.Length) NetSession.ThreadedServer = args[i + 1] != "0";
                else if (a == "-spectate" && i + 1 < args.Length) { NextLaunch = Launch.Spectate; NextAddress = args[i + 1]; }
                else if (a == "-replay" && i + 1 < args.Length)
                {
                    // Play a replay file at start (needs its map: read the header first).
                    NextLaunch = Launch.Replay;
                    NextReplayPath = args[i + 1];
                    try
                    {
                        var f = Sculpt.Core.Replay.ReplayFile.Read(System.IO.File.ReadAllBytes(NextReplayPath));
                        NextMap = MapOf(f.Header.Map);
                    }
                    catch (Exception) { /* the launch reports it */ }
                }
                else if (a == "-noreplay") NetSession.RecordReplays = false;
                else if (a == "-specdelay" && i + 1 < args.Length && int.TryParse(args[i + 1], out int sd)) NetSession.SpectatorDelaySeconds = Math.Max(0, sd);
                else if (a == "-port" && i + 1 < args.Length && ushort.TryParse(args[i + 1], out ushort port) && port != 0) HostPort = port;
            }
            // -connect address -port N: the port goes with the address (an address that names its own port keeps it).
            if ((NextLaunch == Launch.Join || NextLaunch == Launch.Spectate) && HostPort != FishNetTransport.DefaultPort && NextAddress.LastIndexOf(':') < 0)
                NextAddress += ":" + HostPort;
        }

        /// <summary>
        /// -simping ms, -simloss percent, -simjitter ms, on every load (one-run network simulation). Host, join and -port
        /// come from ReadLaunchArgs at first boot only: doing them here too started a second host (or join) on every load,
        /// menu reloads included.
        /// </summary>
        private void ApplyCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "-simping" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ping))
                { _session.SimUp.LatencyMs = ping / 2; _session.SimDown.LatencyMs = ping / 2; }
                else if (a == "-simloss" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double loss))
                { _session.SimUp.LossPercent = loss; _session.SimDown.LossPercent = loss; }
                else if (a == "-simjitter" && next != null && double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double jit))
                { _session.SimUp.JitterMs = jit; _session.SimDown.JitterMs = jit; }
            }
        }

        private System.Diagnostics.Stopwatch _awakeClock;
        private int _frameLog;

        private void OnDestroy()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _input?.Dispose();
            _session?.Dispose();
            Debug.Log($"[SCULPT] Unload: input and network shut down in {sw.ElapsedMilliseconds} ms.");
        }
    }

    /// <summary>A battle royale bot that waits (standing still) until the walking grid is built, then plays as a duel bot.</summary>
    internal sealed class PendingBot : IBotBrain
    {
        private readonly System.Threading.Tasks.Task<BotNav> _nav;
        private readonly BotLevel _level;
        private readonly int _seed;
        private DuelBot _bot;

        public PendingBot(System.Threading.Tasks.Task<BotNav> nav, BotLevel level, int seed)
        {
            _nav = nav;
            _level = level;
            _seed = seed;
        }

        public bool CountsAsPlayer => true;

        public InputCmd Next(NetServer server, ServerPlayer me)
        {
            if (_bot == null && _nav.IsCompleted && !_nav.IsFaulted) _bot = new DuelBot(_nav.Result, DuelBotConfig.For(_level), _seed);
            if (_bot != null) return _bot.Next(server, me);
            return new InputCmd { Tick = server.Tick, ViewTick = server.Tick, Move = new MoveInput { Yaw = InputCmd.QuantizeYaw(me.SpawnYaw) } };
        }
    }
}
