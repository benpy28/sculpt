using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// The local player: samples input every frame, steps Core movement at 60 Hz and exposes an
    /// interpolated pose for the camera. Mouse look is applied every rendered frame for minimum latency;
    /// the simulation reads the latest yaw on each tick.
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        public PlayerBody Body { get; private set; }
        public MotorState State => _state;
        public MovementConfig Config => _cfg;
        public readonly List<MoveEvent> Events = new List<MoveEvent>();

        /// <summary>View angles in degrees. Yaw 0 = north, positive turns toward east.</summary>
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /// <summary>Interpolated feet position for rendering.</summary>
        public Vector3 RenderFeet { get; private set; }
        public float RenderSpeed { get; private set; }
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        private VoxelGrid _grid;
        private MovementConfig _cfg;
        private MovementSettings _settingsAsset;
        private GameInput _input;
        private ClientSettings _client;
        private MotorState _state, _prev;
        private Vec3d _spawn;
        private double _spawnYaw;
        private bool _jumpLatch, _crouchLatch, _respawnLatch;

        public void Init(VoxelGrid grid, MovementConfig cfg, MovementSettings settingsAsset, GameInput input, ClientSettings client, Vec3d spawn, double spawnYaw, int id, int team)
        {
            _grid = grid;
            _cfg = cfg;
            _settingsAsset = settingsAsset;
            _input = input;
            _client = client;
            _spawn = spawn;
            _spawnYaw = spawnYaw;
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg);
            Body = new PlayerBody(id, team, spawn);
            Respawn();
            LockCursor(true);
        }

        public void Respawn()
        {
            _state = MotorState.At(_spawn);
            _prev = _state;
            Yaw = (float)(_spawnYaw * Mathf.Rad2Deg);
            Pitch = 0;
            PlayerMovement.WriteBody(_state, _cfg, Body);
            RenderFeet = _spawn.ToUnity();
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>Called once per rendered frame before any ticks.</summary>
        public void SampleFrame()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);
            if (!CursorLocked && mouse != null && mouse.leftButton.wasPressedThisFrame) LockCursor(true);

            if (CursorLocked)
            {
                Vector2 d = _input.Look.ReadValue<Vector2>();
                float k = _client.DegreesPerCount;
                Yaw = Mathf.Repeat(Yaw + d.x * k + 180f, 360f) - 180f;
                Pitch = Mathf.Clamp(Pitch - d.y * k, -89f, 89f);
            }

            if (_input.Jump.WasPressedThisFrame()) _jumpLatch = true;
            if (_input.Crouch.WasPressedThisFrame()) _crouchLatch = true;
            if (_input.Respawn.WasPressedThisFrame()) _respawnLatch = true;
        }

        /// <summary>One 60 Hz simulation tick.</summary>
        public void Tick()
        {
            if (_respawnLatch)
            {
                _respawnLatch = false;
                Respawn();
                return;
            }
#if UNITY_EDITOR
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg); // live tuning in Play mode
#endif
            // Sculpt shoves move the shared body between ticks.
            _state.Position = Body.Position;

            bool active = CursorLocked;
            Vector2 move = active ? _input.Move.ReadValue<Vector2>() : Vector2.zero;
            var mi = new MoveInput
            {
                MoveX = move.x,
                MoveZ = move.y,
                Yaw = Yaw * Mathf.Deg2Rad,
                JumpPressed = _jumpLatch && active,
                JumpHeld = active && _input.Jump.IsPressed(),
                CrouchPressed = _crouchLatch && active,
                CrouchHeld = active && _input.Crouch.IsPressed(),
                SprintHeld = active && _input.Sprint.IsPressed(),
                WantsToShoot = active && (_input.Fire.IsPressed() || _input.Aim.IsPressed()),
            };
            _jumpLatch = false;
            _crouchLatch = false;

            _prev = _state;
            PlayerMovement.Step(_grid, _cfg, ref _state, mi, Events);
            PlayerMovement.WriteBody(_state, _cfg, Body);

            if (_state.Position.Y < -30) Respawn();
        }

        /// <summary>Interpolates between the last two ticks. alpha in [0, 1).</summary>
        public void Render(float alpha)
        {
            RenderFeet = Vector3.Lerp(_prev.Position.ToUnity(), _state.Position.ToUnity(), alpha);
            RenderSpeed = Mathf.Lerp((float)_prev.HorizontalSpeed, (float)_state.HorizontalSpeed, alpha);
        }

        public float TargetEyeHeight => (float)PlayerMovement.EyeOf(_state, _cfg);
    }
}
