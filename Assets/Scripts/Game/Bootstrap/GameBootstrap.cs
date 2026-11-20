using Sculpt.Core;
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

        private VoxelGrid _grid;
        private SculptSystem _sculpt;
        private WorldView _world;
        private LocalPlayer _player;
        private CameraRig _rig;
        private GameInput _input;
        private double _accumulator;
        private const double TickSeconds = 1.0 / SimTime.TicksPerSecond;

        private void Awake()
        {
            Time.fixedDeltaTime = 1f / SimTime.TicksPerSecond;
            Physics.simulationMode = SimulationMode.Script; // Core does all collision
            QualitySettings.vSyncCount = 0;                 // lowest input latency
            Application.targetFrameRate = -1;

            var sculptConfig = new SculptConfig();
            _grid = TestMaps.BuildM2(out Vec3d spawn, out double spawnYaw);
            _sculpt = new SculptSystem(_grid, sculptConfig);

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
            _player.Init(_grid, new MovementConfig(), movementSettings, _input, client, spawn, spawnYaw, 1, 1);
            _sculpt.AddPlayer(_player.Body);

            _rig = new GameObject("Camera Rig").AddComponent<CameraRig>();
            _rig.Init();
            _rig.Mode = new FirstPersonMode(_player, client);

            gameObject.AddComponent<DevHud>().Init(_player, client);

            Debug.Log($"[SCULPT] Bootstrap OK. Sim tick rate {SimTime.TicksPerSecond} Hz. Map {_grid.SizeX}x{_grid.SizeZ} cells, {_grid.ChunkCount} chunks.");
        }

        private void Update()
        {
            _player.SampleFrame();

            _accumulator += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            while (_accumulator >= TickSeconds)
            {
                _player.Tick();
                _sculpt.Step();
                _sculpt.ClearEvents();
                _accumulator -= TickSeconds;
            }

            _player.Render((float)(_accumulator / TickSeconds));
            _rig.Apply(Time.unscaledDeltaTime);
            _world.RebuildDirty();
        }

        private void OnDestroy()
        {
            _input?.Dispose();
        }
    }
}
