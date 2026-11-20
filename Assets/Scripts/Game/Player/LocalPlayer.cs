using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// The local player: samples input every frame and predicts Core movement through the NetClient at 60 Hz
    /// (the server, local or remote, is always authoritative). Mouse look is applied every rendered frame for
    /// minimum latency; the simulation reads the latest yaw on each tick.
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        public MotorState State => Net.Predicted;
        public MovementConfig Config => _cfg;
        public readonly List<MoveEvent> Events = new List<MoveEvent>();
        public NetClient Net => _session.Client;

        /// <summary>View angles in degrees. Yaw 0 = north, positive turns toward east.</summary>
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /// <summary>Interpolated feet position for rendering (includes the decaying correction offset).</summary>
        public Vector3 RenderFeet { get; private set; }
        public float RenderSpeed { get; private set; }
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;
        /// <summary>Set by UI that takes the keyboard (e.g. the join box). Movement input is ignored meanwhile.</summary>
        public bool InputBlocked { get; set; }

        private NetSession _session;
        private MovementConfig _cfg;
        private MovementSettings _settingsAsset;
        private GameInput _input;
        private ClientSettings _client;
        private bool _jumpLatch, _crouchLatch, _respawnLatch;
        private bool _hadJoined;

        public void Init(NetSession session, MovementConfig cfg, MovementSettings settingsAsset, GameInput input, ClientSettings client)
        {
            _session = session;
            _cfg = cfg;
            _settingsAsset = settingsAsset;
            _input = input;
            _client = client;
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg);
            _session.ClientChanged += () => _hadJoined = false;
            LockCursor(true);
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void FaceSpawn()
        {
            Yaw = (float)(Net.SpawnYaw * Mathf.Rad2Deg);
            Pitch = 0;
        }

        /// <summary>Called once per rendered frame before any ticks.</summary>
        public void SampleFrame()
        {
            if (Net.Joined && !_hadJoined)
            {
                _hadJoined = true;
                FaceSpawn();
                RenderFeet = Net.Predicted.Position.ToUnity();
            }

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);
            if (!CursorLocked && !InputBlocked && mouse != null && mouse.leftButton.wasPressedThisFrame) LockCursor(true);

            if (CursorLocked)
            {
                Vector2 d = _input.Look.ReadValue<Vector2>();
                float k = _client.DegreesPerCount;
                Yaw = Mathf.Repeat(Yaw + d.x * k + 180f, 360f) - 180f;
                Pitch = Mathf.Clamp(Pitch - d.y * k, -89f, 89f);
            }

            if (InputBlocked) return;
            if (_input.Jump.WasPressedThisFrame()) _jumpLatch = true;
            if (_input.Crouch.WasPressedThisFrame()) _crouchLatch = true;
            if (_input.Respawn.WasPressedThisFrame()) _respawnLatch = true;
        }

        /// <summary>One 60 Hz prediction tick.</summary>
        public void Tick()
        {
#if UNITY_EDITOR
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg); // live tuning in Play mode
#endif
            bool active = CursorLocked && !InputBlocked;
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
            bool respawn = _respawnLatch;
            _jumpLatch = false;
            _crouchLatch = false;
            _respawnLatch = false;

            int before = Events.Count;
            Net.PredictTick(mi, Pitch, respawn, Events);
            for (int i = before; i < Events.Count; i++)
                if (Events[i].Type == MoveEventType.Respawned) FaceSpawn();
        }

        /// <summary>Interpolates between the last two predicted ticks. alpha in [0, 1].</summary>
        public void Render(float alpha)
        {
            MotorState prev = Net.PreviousPredicted, cur = Net.Predicted;
            RenderFeet = Vector3.Lerp(prev.Position.ToUnity(), cur.Position.ToUnity(), alpha) + Net.CorrectionOffset.ToUnity();
            RenderSpeed = Mathf.Lerp((float)prev.HorizontalSpeed, (float)cur.HorizontalSpeed, alpha);
        }

        public float TargetEyeHeight => (float)PlayerMovement.EyeOf(State, _cfg);
    }
}
