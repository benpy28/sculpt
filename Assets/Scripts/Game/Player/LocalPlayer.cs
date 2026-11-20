using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Sculpting;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// The local player: samples input every frame and predicts Core movement, hand state and sculpting through the
    /// NetClient at 60 Hz (the server, local or remote, is always authoritative). Mouse look is applied every rendered
    /// frame for minimum latency; the simulation reads the latest yaw on each tick. Sculpt targeting also runs every
    /// frame, and a sculpt press is turned into a confirm with the aim of the frame it was pressed in.
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        public MotorState State => Net.Predicted;
        public MovementConfig Config => _cfg;
        public readonly List<MoveEvent> Events = new List<MoveEvent>();
        public NetClient Net => _session.Client;
        public SculptClient Sculpt => _session.Client.Sculpt;
        public SculptConfig SculptConfig => _session.SculptConfig;

        /// <summary>View angles in degrees. Yaw 0 = north, positive turns toward east.</summary>
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /// <summary>Interpolated feet position for rendering (includes the decaying correction offset).</summary>
        public Vector3 RenderFeet { get; private set; }
        public float RenderSpeed { get; private set; }
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;
        /// <summary>Set by UI that takes the keyboard (e.g. the join box). Movement input is ignored meanwhile.</summary>
        public bool InputBlocked { get; set; }

        // ---- Sculpt (read by the sculpt view and HUD) ----
        public bool InSculptMode => State.Hand.InSculptMode;
        public SculptTarget Target { get; private set; }
        /// <summary>The action a left click would confirm right now, and its full check (drives ghost and cost flash).</summary>
        public SculptAction HoverAction { get; private set; }
        public SculptCheck HoverCheck { get; private set; }
        public bool HasHover { get; private set; }
        public bool ReinforceHeld { get; private set; }

        private NetSession _session;
        private MovementConfig _cfg;
        private MovementSettings _settingsAsset;
        private GameInput _input;
        private ClientSettings _client;
        private bool _jumpLatch, _crouchLatch, _respawnLatch, _toggleLatch, _fireLatch, _reloadLatch;
        private bool _sprintOn, _crouchOn, _zoomOn;
        private Weapon _lastHeld = Weapon.Pump, _prevHeld = Weapon.Smg;

        /// <summary>Right mouse zoom (hold or toggle) while a gun is out. The camera scales the FOV by the gun's AimFovScale.</summary>
        public bool Zoomed { get; private set; }
        public GameInput Input => _input;

        /// <summary>Recoil offset of the aim (degrees, x = up, y = right), interpolated for drawing. The camera includes it,
        /// so the crosshair always sits exactly where the next bullet goes (before spread).</summary>
        public Vector2 RenderRecoil { get; private set; }
        /// <summary>Mouse movement this frame in degrees (viewmodel sway).</summary>
        public Vector2 LookDelta { get; private set; }
        private byte _weaponLatch;
        private bool _confirmLatch;
        private SculptAction _confirmAction;
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

        public static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void FaceSpawn()
        {
            Yaw = (float)(Net.SpawnYaw * Mathf.Rad2Deg);
            Pitch = 0;
        }

        /// <summary>Eye position the simulation (and the server) uses, and the look direction.</summary>
        public Vec3d SimEye => State.Position + new Vec3d(0, PlayerMovement.EyeOf(State, _cfg), 0);
        public Vec3d LookDir => SculptAim.LookDir(Yaw * Mathf.Deg2Rad, Pitch);

        /// <summary>Called once per rendered frame before any ticks.</summary>
        public void SampleFrame()
        {
            if (Net.Joined && !_hadJoined)
            {
                _hadJoined = true;
                FaceSpawn();
                RenderFeet = Net.Predicted.Position.ToUnity();
            }

            // Esc opens the settings menu (SettingsMenu frees the cursor and blocks input while it is open).
            var mouse = Mouse.current;
            bool clickToLock = !CursorLocked && !InputBlocked && mouse != null && mouse.leftButton.wasPressedThisFrame;
            if (clickToLock) LockCursor(true);

            if (CursorLocked)
            {
                Vector2 d = _input.Look.ReadValue<Vector2>();
                float k = _client.DegreesPerCount;
                Yaw = Mathf.Repeat(Yaw + d.x * k + 180f, 360f) - 180f;
                Pitch = Mathf.Clamp(Pitch - d.y * k, -89f, 89f);
                LookDelta = new Vector2(d.x * k, d.y * k);
            }
            else LookDelta = Vector2.zero;

            UpdateSculptAim();

            if (InputBlocked || !CursorLocked) return;
            if (_input.Jump.WasPressedThisFrame()) _jumpLatch = true;
            if (_input.Crouch.WasPressedThisFrame()) _crouchLatch = true;
            if (GameInput.DevBuild && _input.DevRespawn.WasPressedThisFrame()) _respawnLatch = true;
            if (_input.SculptToggle.WasPressedThisFrame()) _toggleLatch = true;
            for (int i = 0; i < 4; i++) if (_input.Hotbar[i].WasPressedThisFrame()) _weaponLatch = (byte)(i + 1);
            if (_input.LastWeapon.WasPressedThisFrame()) _weaponLatch = (byte)((int)_prevHeld + 1);
            if (!InSculptMode && !clickToLock && _input.Fire.WasPressedThisFrame()) _fireLatch = true;
            if (!InSculptMode && _input.Reload.WasPressedThisFrame()) _reloadLatch = true;
            if (_client.SprintToggle && _input.Sprint.WasPressedThisFrame()) _sprintOn = !_sprintOn;
            if (_client.CrouchToggle && _input.Crouch.WasPressedThisFrame()) _crouchOn = !_crouchOn;
            if (_client.ZoomToggle && !InSculptMode && _input.Aim.WasPressedThisFrame()) _zoomOn = !_zoomOn;

            if (!InSculptMode || clickToLock) return;
            // The direction press IS the confirm, with this frame's aim. The newest press in a frame wins.
            SculptTarget t = Target;
            if (!t.Valid) return;
            bool reinforce = ReinforceHeld;
            if (_input.SculptPush.WasPressedThisFrame()) Confirm(SculptAction.Move(t.Sub, t.PushDir, t.Point, reinforce));
            if (_input.SculptPull.WasPressedThisFrame()) Confirm(SculptAction.Move(t.Sub, t.PushDir.Opposite(), t.Point, reinforce));
            if (_input.SculptRaise.WasPressedThisFrame()) Confirm(SculptAction.Raise(t.Sub, t.Point, reinforce));
            if (_input.SculptSink.WasPressedThisFrame()) Confirm(SculptAction.Sink(t.Sub, t.Point, reinforce));
            if (_input.SculptThrow.WasPressedThisFrame()) Confirm(SculptAction.Throw(t.Sub, t.Point, LookDir));
        }

        private void Confirm(SculptAction a)
        {
            _confirmLatch = true;
            _confirmAction = a;
        }

        private void UpdateSculptAim()
        {
            HasHover = false;
            ReinforceHeld = CursorLocked && !InputBlocked && _input.SculptReinforce.IsPressed();
            if (!Net.Joined || Sculpt == null || Sculpt.LocalBody == null || !InSculptMode)
            {
                Target = default;
                return;
            }
            Target = SculptAim.Find(Net.Grid, Sculpt.Grid, SimEye, LookDir, SculptConfig, Pitch);
            if (!Target.Valid) return;
            Sculpt.SetLocalBody(State);
            HoverAction = SculptAction.Move(Target.Sub, Target.PushDir, Target.Point, ReinforceHeld);
            HoverCheck = Sculpt.Evaluate(HoverAction, Net.Tick + 1);
            HasHover = true;
        }

        /// <summary>One 60 Hz prediction tick.</summary>
        public void Tick()
        {
#if UNITY_EDITOR
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg); // live tuning in Play mode
#endif
            bool active = CursorLocked && !InputBlocked;
            Vector2 move = active ? _input.Move.ReadValue<Vector2>() : Vector2.zero;
            bool sculpting = State.Hand.InSculptMode;
            HandState h0 = State.Hand;
            if (h0.Held != _lastHeld) { _prevHeld = _lastHeld; _lastHeld = h0.Held; _zoomOn = false; }
            if (sculpting) _zoomOn = false;
            if (move.sqrMagnitude < 0.01f) _sprintOn = false; // a toggled sprint ends when you stop
            bool sprintHeld = active && (_client.SprintToggle ? _sprintOn : _input.Sprint.IsPressed());
            bool crouchHeld = active && (_client.CrouchToggle ? _crouchOn : _input.Crouch.IsPressed());
            bool aimHeld = active && !sculpting && (_client.ZoomToggle ? _zoomOn : _input.Aim.IsPressed());
            Zoomed = aimHeld && h0.Mode == HandMode.Weapon;
            var mi = new MoveInput
            {
                MoveX = move.x,
                MoveZ = move.y,
                Yaw = Yaw * Mathf.Deg2Rad,
                JumpPressed = _jumpLatch && active,
                JumpHeld = active && _input.Jump.IsPressed(),
                CrouchPressed = _crouchLatch && active,
                CrouchHeld = crouchHeld,
                SprintHeld = sprintHeld,
                // In sculpt mode the mouse buttons sculpt; they never stop your sprint.
                WantsToShoot = active && !sculpting && (_input.Fire.IsPressed() || aimHeld),
                FireHeld = active && !sculpting && _input.Fire.IsPressed(),
                FirePressed = active && !sculpting && _fireLatch,
            };
            _fireLatch = false;
            var hand = new HandInput { SculptToggle = _toggleLatch && active, WeaponKey = active ? _weaponLatch : (byte)0, Reload = _reloadLatch && active };
            _reloadLatch = false;
            bool respawn = _respawnLatch;
            bool confirm = _confirmLatch && active;
            SculptAction action = _confirmAction;
            _jumpLatch = false;
            _crouchLatch = false;
            _respawnLatch = false;
            _toggleLatch = false;
            _weaponLatch = 0;
            _confirmLatch = false;

            int before = Events.Count;
            Net.PredictTick(mi, Pitch, respawn, hand, confirm, action, Events);
            for (int i = before; i < Events.Count; i++)
                if (Events[i].Type == MoveEventType.Respawned) FaceSpawn();
        }

        /// <summary>Interpolates between the last two predicted ticks. alpha in [0, 1].</summary>
        public void Render(float alpha)
        {
            MotorState prev = Net.PreviousPredicted, cur = Net.Predicted;
            RenderFeet = Vector3.Lerp(prev.Position.ToUnity(), cur.Position.ToUnity(), alpha) + Net.CorrectionOffset.ToUnity();
            RenderSpeed = Mathf.Lerp((float)prev.HorizontalSpeed, (float)cur.HorizontalSpeed, alpha);
            // Recoil kicks are instant (no lerp from the previous tick's value when it rose), recovery is smooth.
            float pp = (float)prev.Weapon.RecoilPitch, cp = (float)cur.Weapon.RecoilPitch;
            float py = (float)prev.Weapon.RecoilYaw, cy = (float)cur.Weapon.RecoilYaw;
            RenderRecoil = cp > pp ? new Vector2(cp, cy) : new Vector2(Mathf.Lerp(pp, cp, alpha), Mathf.Lerp(py, cy, alpha));
        }

        public float TargetEyeHeight => (float)PlayerMovement.EyeOf(State, _cfg);
    }
}
