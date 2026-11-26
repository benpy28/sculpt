using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Cameras
{
    /// <summary>
    /// Battle royale, after you are out: a follow camera behind a living player until the next match. Teammates first
    /// (the server always sends them), else whoever is near where you went down. Left and right mouse pick the next or
    /// previous one; the mouse orbits. Nobody to watch: the camera hangs over where you died.
    /// </summary>
    public sealed class BrDeathCamera : ICameraMode
    {
        private readonly LocalPlayer _player;
        private readonly ClientSettings _settings;
        private readonly NetSession _session;
        private readonly GameInput _input;
        private readonly List<int> _ids = new List<int>();
        private int _target;
        private float _orbitYaw, _orbitPitch = 18f, _lookYaw, _lookPitch;
        private Vector3 _lastPos;
        private bool _started;

        public BrDeathCamera(LocalPlayer player, ClientSettings settings, NetSession session, GameInput input)
        {
            _player = player;
            _settings = settings;
            _session = session;
            _input = input;
        }

        /// <summary>Who is watched now (0: nobody).</summary>
        public int Target => _target;

        public void Reset() { _target = 0; _started = false; }

        public void HandleInput()
        {
            if (!_player.CursorLocked || _player.InputBlocked) return;
            if (_input.WatchNext.WasPressedThisFrame()) Cycle(+1);
            if (_input.WatchPrev.WasPressedThisFrame()) Cycle(-1);
        }

        private void Living(NetClient net)
        {
            _ids.Clear();
            net.GetRemoteIds(_ids);
            for (int i = _ids.Count - 1; i >= 0; i--)
                if (!net.TryGetRemote(_ids[i], out RemoteView v) || v.Dead) _ids.RemoveAt(i);
            // Teammates first, then by id (a steady order to cycle through).
            int team = net.Team;
            _ids.Sort((a, b) =>
            {
                net.TryGetRemote(a, out RemoteView va);
                net.TryGetRemote(b, out RemoteView vb);
                int ta = va.Team == team ? 0 : 1, tb = vb.Team == team ? 0 : 1;
                return ta != tb ? ta.CompareTo(tb) : a.CompareTo(b);
            });
        }

        private void Cycle(int dir)
        {
            NetClient net = _session.Client;
            Living(net);
            if (_ids.Count == 0) { _target = 0; return; }
            int at = _ids.IndexOf(_target);
            _target = _ids[at < 0 ? 0 : ((at + dir) % _ids.Count + _ids.Count) % _ids.Count];
        }

        public void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov)
        {
            horizontalFov = _settings.Fov;
            NetClient net = _session.Client;
            float dYaw = Mathf.DeltaAngle(_lookYaw, _player.Yaw), dPitch = _player.Pitch - _lookPitch;
            _lookYaw = _player.Yaw;
            _lookPitch = _player.Pitch;
            if (!_started) { _started = true; _lastPos = net.Predicted.Position.ToUnity() + new Vector3(0, 6, -4); dYaw = dPitch = 0; }

            if (_target == 0 || !net.TryGetRemote(_target, out RemoteView v) || v.Dead)
            {
                Living(net);
                _target = _ids.Count > 0 ? _ids[0] : 0;
            }
            if (_target != 0 && net.TryGetRemote(_target, out RemoteView t))
            {
                _orbitYaw += dYaw;
                _orbitPitch = Mathf.Clamp(_orbitPitch + dPitch, -15f, 70f);
                Vector3 pivot = t.Position.ToUnity() + new Vector3(0, 1.5f, 0);
                rotation = Quaternion.Euler(_orbitPitch, (float)(t.Yaw * Mathf.Rad2Deg) + _orbitYaw, 0);
                position = Clear(net.Grid, pivot, pivot - rotation * Vector3.forward * 4.5f);
            }
            else
            {
                // Nobody to watch: look down at where you went down.
                Vector3 body = net.Predicted.Position.ToUnity();
                _orbitYaw += dYaw;
                rotation = Quaternion.Euler(50f, _orbitYaw, 0);
                position = body + new Vector3(0, 1, 0) - rotation * Vector3.forward * 9f;
            }
            _lastPos = position;
        }

        private static Vector3 Clear(ITerrain grid, Vector3 pivot, Vector3 want)
        {
            Vector3 d = want - pivot;
            float len = d.magnitude;
            if (len < 1e-3f || grid == null) return want;
            TerrainHit hit = grid.Raycast(new Vec3d(pivot.x, pivot.y, pivot.z), new Vec3d(d.x / len, d.y / len, d.z / len), len);
            return hit.Hit ? pivot + d / len * Mathf.Max(0.3f, (float)hit.Distance - 0.25f) : want;
        }
    }
}
