using Sculpt.Core;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Cameras
{
    /// <summary>A camera behaviour. Swappable so kill-cam, replay and free spectator can slot in later.</summary>
    public interface ICameraMode
    {
        void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov);
    }

    /// <summary>Owns the single game camera and asks the active mode where to put it each frame.</summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public static readonly Color Sky = new Color(0.64f, 0.76f, 0.88f);

        public Camera Camera { get; private set; }
        public ICameraMode Mode { get; set; }

        public void Init()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.transform.SetParent(transform, false);
            Camera = go.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Sky;
            Camera.nearClipPlane = 0.05f;
            Camera.farClipPlane = 400f;
            go.AddComponent<AudioListener>();
        }

        /// <summary>Call after the simulation has ticked and the player has interpolated.</summary>
        public void Apply(float dt)
        {
            if (Mode == null || Camera == null) return;
            Mode.UpdatePose(dt, out Vector3 pos, out Quaternion rot, out float hFov);
            Camera.transform.SetPositionAndRotation(pos, rot);
            // Horizontal FOV is defined at 16:9; wider screens see more (Hor+).
            Camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(hFov, 16f / 9f);
        }
    }

    /// <summary>Cosmetic camera shake from guns and hits: roll, a few millimetres of offset and an FOV punch. Never pitch/yaw.</summary>
    public sealed class CameraShake
    {
        public float Roll;
        public Vector3 Offset;
        public float FovPunch;
    }

    /// <summary>First person view: interpolated feet + smoothed eye height, minimal head bob, speed FOV kick.</summary>
    public sealed class FirstPersonMode : ICameraMode
    {
        private readonly LocalPlayer _player;
        private readonly ClientSettings _settings;
        private float _eye = -1;
        private float _bobPhase;
        private float _bobAmount;
        private float _fovKick;
        private float _zoom = 1f;
        /// <summary>Current zoom factor (1 = none), for sensitivity scaling and the HUD.</summary>
        public float Zoom => _zoom;
        public readonly CameraShake Shake = new CameraShake();

        public FirstPersonMode(LocalPlayer player, ClientSettings settings)
        {
            _player = player;
            _settings = settings;
        }

        public void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov)
        {
            float target = _player.TargetEyeHeight;
            if (_eye < 0) _eye = target;
            _eye = Mathf.Lerp(_eye, target, 1f - Mathf.Exp(-20f * dt)); // crouch/slide transitions ~0.1 s

            MotorState s = _player.State;
            float speed = _player.RenderSpeed;
            bool walking = s.Mode == MoveMode.Ground && speed > 0.5f;
            _bobAmount = Mathf.MoveTowards(_bobAmount, walking && _settings.HeadBob ? 1f : 0f, dt * 6f);
            if (walking) _bobPhase += dt * speed * 1.7f;
            float bob = Mathf.Sin(_bobPhase * 2f) * 0.012f * _bobAmount; // minimal: 1.2 cm

            position = _player.RenderFeet + new Vector3(0, _eye + bob, 0);
            // Recoil is part of the real aim (Core), so the camera follows it exactly; Shake is cosmetic roll only and never
            // moves the crosshair off the bullet's line.
            Vector2 recoil = _player.RenderRecoil;
            rotation = Quaternion.Euler(_player.Pitch - recoil.x, _player.Yaw + recoil.y, Shake.Roll);
            position += Shake.Offset;

            MovementConfig c = _player.Config;
            float over = Mathf.InverseLerp((float)c.SprintSpeed + 0.2f, (float)c.SlideMaxSpeed, speed);
            float kickTarget = _settings.SpeedFovKick ? over * 8f : 0f;
            _fovKick = Mathf.Lerp(_fovKick, kickTarget, 1f - Mathf.Exp(-8f * dt));
            // Light zoom on right mouse (AR, pistol): scales the view by the gun's AimFovScale, eased in about 80 ms.
            float zoomTarget = 1f;
            if (_player.Zoomed) zoomTarget = (float)c.Weapons.Of(s.Hand.Held).AimFovScale;
            _zoom = Mathf.Lerp(_zoom, zoomTarget, 1f - Mathf.Exp(-28f * dt));
            float baseFov = _settings.Fov + _fovKick * _zoom;
            float half = Mathf.Atan(Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad) * _zoom) * Mathf.Rad2Deg;
            horizontalFov = 2f * half + Shake.FovPunch;
        }
    }
}
