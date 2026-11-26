using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Cameras
{
    /// <summary>
    /// The camera while watching (live or a replay). Four views, cycled with the camera key (V):
    /// POV (through the watched player's eyes), Follow (behind them, the mouse orbits), Free (fly anywhere: move keys,
    /// jump up, crouch down, sprint fast) and Top-down (high above the watched player). Next and previous player
    /// switch who is watched (left and right mouse). The mouse look comes from the local player's own look handling,
    /// so sensitivity and invert settings carry over.
    /// </summary>
    public sealed class SpectatorCamera : ICameraMode
    {
        public enum View { Pov, Follow, Free, TopDown }

        public View Mode { get; private set; } = View.Pov;
        /// <summary>Who is watched (0 = nobody yet: the first player that shows up).</summary>
        public int Target { get; private set; }

        private readonly LocalPlayer _player;
        private readonly ClientSettings _settings;
        private readonly NetSession _session;
        private readonly MovementConfig _mc;
        private readonly GameInput _input;
        private readonly List<int> _ids = new List<int>();

        private Vector3 _freePos;
        private bool _freeInit;
        private Vector3 _lastPos;
        private Quaternion _lastRot = Quaternion.identity;
        private float _eye = -1;
        private float _topHeight = 34f;
        private float _orbitYaw, _orbitPitch = 15f;
        private float _lookYaw, _lookPitch;

        /// <summary>Free fly speed, metres per second (sprint triples it).</summary>
        public float FreeSpeed = 9f;

        public SpectatorCamera(LocalPlayer player, ClientSettings settings, NetSession session, MovementConfig mc, GameInput input)
        {
            _player = player;
            _settings = settings;
            _session = session;
            _mc = mc;
            _input = input;
        }

        public static string ViewName(View v) => v == View.Pov ? "Player view" : v == View.Follow ? "Follow" : v == View.Free ? "Free camera" : "Top-down";

        /// <summary>Reads the watch keys (call once per frame while watching, before the rig places the camera).</summary>
        public void HandleInput()
        {
            bool active = _player.CursorLocked && !_player.InputBlocked;
            if (!active) return;
            if (_input.WatchCamera.WasPressedThisFrame())
            {
                View next = (View)(((int)Mode + 1) % 4);
                if (next == View.Free) _freePos = _lastPos; // fly off from where the camera is
                Mode = next;
            }
            if (_input.WatchNext.WasPressedThisFrame()) Cycle(+1);
            if (_input.WatchPrev.WasPressedThisFrame()) Cycle(-1);
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && Mode == View.TopDown)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0) _topHeight = Mathf.Clamp(_topHeight * (scroll > 0 ? 0.9f : 1.1f), 8f, 140f);
            }
        }

        private void Cycle(int dir)
        {
            NetClient net = _session.Client;
            _ids.Clear();
            foreach (int id in net.SpectatedIds) _ids.Add(id);
            if (_ids.Count == 0) return;
            int at = _ids.IndexOf(Target);
            int k = at < 0 ? 0 : ((at + dir) % _ids.Count + _ids.Count) % _ids.Count;
            Target = _ids[k];
            if (Mode == View.Free) Mode = View.Follow;
        }

        /// <summary>The watched player as drawn now, if there is one (picks the first if the target left).</summary>
        public bool TryGetTarget(out RemoteView view, out SpecPlayer info)
        {
            NetClient net = _session.Client;
            info = default;
            view = default;
            if (Target == 0 || !net.TryGetSpectated(Target, out info))
            {
                Target = 0;
                foreach (int id in net.SpectatedIds) { Target = id; break; }
                if (Target == 0 || !net.TryGetSpectated(Target, out info)) return false;
            }
            return net.TryGetRemote(Target, out view);
        }

        public void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov)
        {
            horizontalFov = _settings.Fov;
            // The mouse turns the free and follow cameras (the local player's look, read every frame).
            float dYaw = Mathf.DeltaAngle(_lookYaw, _player.Yaw), dPitch = _player.Pitch - _lookPitch;
            _lookYaw = _player.Yaw;
            _lookPitch = _player.Pitch;

            bool has = TryGetTarget(out RemoteView v, out SpecPlayer info);
            RemotePlayersView.HiddenPlayer = has && Mode == View.Pov && !v.Dead ? Target : 0;
            View mode = Mode;
            if (!has && mode != View.Free) mode = View.Free;
            if (!_freeInit)
            {
                // Nobody to watch yet: start high over the middle of the map.
                _freeInit = true;
                SubGrid g = _session.ClientGrid;
                if (g != null && _lastPos == Vector3.zero)
                    _freePos = new Vector3((float)(g.SizeX * SubGrid.SubSize * 0.5), 22f, (float)(g.SizeZ * SubGrid.SubSize * 0.5) - 30f);
            }

            switch (mode)
            {
                case View.Pov:
                {
                    float target = (float)(v.Crouched || v.Mode == MoveMode.Slide ? _mc.CrouchEye : _mc.StandEye);
                    if (_eye < 0) _eye = target;
                    _eye = Mathf.Lerp(_eye, target, 1f - Mathf.Exp(-20f * dt));
                    position = v.Position.ToUnity() + new Vector3(0, _eye, 0);
                    rotation = Quaternion.Euler((float)v.Pitch, (float)(v.Yaw * Mathf.Rad2Deg), 0);
                    if (v.Dead) { position = _lastPos; rotation = _lastRot; }
                    break;
                }
                case View.Follow:
                {
                    _orbitYaw += dYaw;
                    _orbitPitch = Mathf.Clamp(_orbitPitch + dPitch, -20f, 70f);
                    Vector3 pivot = v.Position.ToUnity() + new Vector3(0, 1.5f, 0);
                    rotation = Quaternion.Euler(_orbitPitch, (float)(v.Yaw * Mathf.Rad2Deg) + _orbitYaw, 0);
                    Vector3 want = pivot - rotation * Vector3.forward * 4.2f;
                    position = Clear(pivot, want);
                    break;
                }
                case View.TopDown:
                {
                    Vector3 c = v.Position.ToUnity();
                    position = c + new Vector3(0, _topHeight, -_topHeight * 0.18f);
                    rotation = Quaternion.LookRotation((c - position).normalized, Vector3.forward);
                    break;
                }
                default:
                {
                    rotation = Quaternion.Euler(_player.Pitch, _player.Yaw, 0);
                    Vector2 m = _player.CursorLocked && !_player.InputBlocked ? _input.Move.ReadValue<Vector2>() : Vector2.zero;
                    float up = 0;
                    if (_player.CursorLocked && !_player.InputBlocked)
                    {
                        if (_input.Jump.IsPressed()) up += 1;
                        if (_input.Crouch.IsPressed()) up -= 1;
                    }
                    float speed = FreeSpeed * (_input.Sprint.IsPressed() ? 3f : 1f);
                    Vector3 move = rotation * new Vector3(m.x, 0, m.y) + Vector3.up * up;
                    if (move.sqrMagnitude > 1) move.Normalize();
                    _freePos += move * speed * dt;
                    position = _freePos;
                    break;
                }
            }
            if (mode != View.Free) _freePos = position;
            _lastPos = position;
            _lastRot = rotation;
        }

        /// <summary>Pulls the follow camera in front of any wall between the player and it (client's own world copy).</summary>
        private Vector3 Clear(Vector3 pivot, Vector3 want)
        {
            ITerrain grid = _session.Client.Grid;
            Vector3 d = want - pivot;
            float len = d.magnitude;
            if (len < 1e-3f) return want;
            Vec3d o = new Vec3d(pivot.x, pivot.y, pivot.z), dir = new Vec3d(d.x / len, d.y / len, d.z / len);
            TerrainHit hit = grid.Raycast(o, dir, len);
            if (hit.Hit) return pivot + d / len * Mathf.Max(0.3f, (float)hit.Distance - 0.25f);
            return want;
        }
    }
}
