using System;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Cameras;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using Sculpt.Game.UI;
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

        public enum MapKind { Arena, MovementCourse }

        [Tooltip("Arena: the seeded 5v5 test arena. Movement Course: the M2 movement test map.")]
        [SerializeField] private MapKind map = MapKind.Arena;
        [SerializeField] private int mapSeed = 1;

        private SubGrid _grid;
        private WorldView _world;
        private LocalPlayer _player;
        private CameraRig _rig;
        private GameInput _input;
        private NetSession _session;
        private void Awake()
        {
            Time.fixedDeltaTime = 1f / SimTime.TicksPerSecond;
            Physics.simulationMode = SimulationMode.Script; // Core does all collision
            QualitySettings.vSyncCount = 0;                 // lowest input latency
            Application.targetFrameRate = -1;
            Application.runInBackground = true;           // two windows on one machine both keep simulating

            var sculptConfig = new SculptConfig();
            var movement = new MovementConfig();
            // The client's own copy of the map (what it draws and predicts against). The server builds its own.
            Vec3d[][] spawns;
            double[] yaw;
            Func<SubGrid> build;
            if (map == MapKind.Arena)
            {
                int seed = mapSeed;
                build = () => MapGen.Generate(seed, out _);
                _grid = MapGen.Generate(seed, out MapInfo info);
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
            _session = new NetSession(build, _grid, movement, new NetConfig(), sculptConfig, spawns, yaw);

            _world = new GameObject("World").AddComponent<WorldView>();
            _world.Init(_grid, sculptConfig);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = CameraRig.Sky;
            RenderSettings.fogStartDistance = 70f;
            RenderSettings.fogEndDistance = 220f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(55f, -35f, 0f); // fixed sun direction
            sun.intensity = 1.1f;

            _input = new GameInput();
            var client = ClientSettings.Load();

            _player = new GameObject("Local Player").AddComponent<LocalPlayer>();
            _player.Init(_session, movement, movementSettings, _input, client);

            new GameObject("Remote Players").AddComponent<RemotePlayersView>().Init(_session, movement);

            _rig = new GameObject("Camera Rig").AddComponent<CameraRig>();
            _rig.Init();
            _rig.Mode = new FirstPersonMode(_player, client);

            gameObject.AddComponent<DevHud>().Init(_player, client, _session);

            ApplyCommandLine();

            Debug.Log($"[SCULPT] Bootstrap OK. Sim tick rate {SimTime.TicksPerSecond} Hz. Map {map} {_grid.SizeX / SubGrid.TileSubs}x{_grid.SizeZ / SubGrid.TileSubs} tiles, {_grid.ChunkCount} chunks ({_grid.AllocatedChunks} with detail).");
        }

        private void Update()
        {
            double dt = Math.Min(Time.unscaledDeltaTime, 0.25f);
            _session.BeginFrame(dt);       // read network, run server ticks (offline/host), deliver snapshots
            _player.SampleFrame();

            NetClient net = _session.Client;
            int ticks = net.BeginFrame(dt); // client clock runs a little ahead of the server
            for (int i = 0; i < ticks; i++) _player.Tick();
            _session.EndFrame();            // inputs leave this frame

            net.UpdateVisual(dt);
            _player.Render((float)net.Alpha);
            _rig.Apply(Time.unscaledDeltaTime);
            _world.RebuildDirty();
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

        /// <summary>-host, -connect address[:port], -port N, -simping ms, -simloss percent, -simjitter ms.</summary>
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
            if (connect != null)
            {
                int colon = connect.LastIndexOf(':');
                if (colon > 0 && ushort.TryParse(connect.Substring(colon + 1), out ushort cp)) { port = cp; connect = connect.Substring(0, colon); }
                _session.Join(connect, port);
            }
            else if (host) _session.StartHost(port);
        }

        private void OnDestroy()
        {
            _input?.Dispose();
            _session?.Dispose();
        }
    }
}
