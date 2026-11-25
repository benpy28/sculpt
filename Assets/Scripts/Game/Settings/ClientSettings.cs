using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>Per-player view, audio and control settings, saved in PlayerPrefs. Edited in the settings menu (Esc).</summary>
    public sealed class ClientSettings
    {
        /// <summary>Horizontal field of view in degrees at 16:9.</summary>
        public float Fov = 95f;
        /// <summary>Degrees of turn per mouse count = Sensitivity * 0.022 (Source-style scale).</summary>
        public float Sensitivity = 2.0f;
        public bool HeadBob = true;
        public bool SpeedFovKick = true;
        /// <summary>First-person gun size, 0.7 .. 1.2 (1 = default, already small).</summary>
        public float ViewmodelSize = 1f;
        /// <summary>Faint streaks at the screen edge above sprint speed.</summary>
        public bool SpeedLines = true;
        /// <summary>Master volume 0..1.</summary>
        public float MasterVolume = 0.8f;
        /// <summary>Wind and birdsong 0..1 (scaled by master).</summary>
        public float AmbienceVolume = 0.7f;
        /// <summary>Hold (default) or toggle, per the keybind table.</summary>
        /// <summary>Sprint and crouch/slide default to toggle (Ben 2026-11-25: they share a finger; less holding). New keys so
        /// the new default reaches everyone once; hold stays a setting.</summary>
        public bool SprintToggle, CrouchToggle, ZoomToggle;

        public const float MinFov = 60f, MaxFov = 120f;
        public const float MinSensitivity = 0.05f, MaxSensitivity = 10f;
        public const float MinViewmodel = 0.7f, MaxViewmodel = 1.2f;

        public float DegreesPerCount => Sensitivity * 0.022f;

        public static ClientSettings Load()
        {
            return new ClientSettings
            {
                Fov = PlayerPrefs.GetFloat("sculpt.fov", 95f),
                Sensitivity = PlayerPrefs.GetFloat("sculpt.sens", 2.0f),
                HeadBob = PlayerPrefs.GetInt("sculpt.bob", 1) == 1,
                SpeedFovKick = PlayerPrefs.GetInt("sculpt.fovkick", 1) == 1,
                ViewmodelSize = PlayerPrefs.GetFloat("sculpt.vmsize", 1f),
                SpeedLines = PlayerPrefs.GetInt("sculpt.speedlines", 1) == 1,
                MasterVolume = PlayerPrefs.GetFloat("sculpt.volume", 0.8f),
                AmbienceVolume = PlayerPrefs.GetFloat("sculpt.ambience", 0.7f),
                SprintToggle = PlayerPrefs.GetInt("sculpt.sprintToggle.v2", 1) == 1,
                CrouchToggle = PlayerPrefs.GetInt("sculpt.crouchToggle.v2", 1) == 1,
                ZoomToggle = PlayerPrefs.GetInt("sculpt.zoomToggle", 0) == 1,
            }.Clamped();
        }

        /// <summary>Keeps every value in its legal range (old or hand-edited prefs).</summary>
        public ClientSettings Clamped()
        {
            Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
            Sensitivity = Mathf.Clamp(Sensitivity, MinSensitivity, MaxSensitivity);
            ViewmodelSize = Mathf.Clamp(ViewmodelSize, MinViewmodel, MaxViewmodel);
            MasterVolume = Mathf.Clamp01(MasterVolume);
            AmbienceVolume = Mathf.Clamp01(AmbienceVolume);
            return this;
        }

        public void Save()
        {
            PlayerPrefs.SetFloat("sculpt.fov", Fov);
            PlayerPrefs.SetFloat("sculpt.sens", Sensitivity);
            PlayerPrefs.SetInt("sculpt.bob", HeadBob ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.fovkick", SpeedFovKick ? 1 : 0);
            PlayerPrefs.SetFloat("sculpt.vmsize", ViewmodelSize);
            PlayerPrefs.SetInt("sculpt.speedlines", SpeedLines ? 1 : 0);
            PlayerPrefs.SetFloat("sculpt.volume", MasterVolume);
            PlayerPrefs.SetFloat("sculpt.ambience", AmbienceVolume);
            PlayerPrefs.SetInt("sculpt.sprintToggle.v2", SprintToggle ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.crouchToggle.v2", CrouchToggle ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.zoomToggle", ZoomToggle ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
