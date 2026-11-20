using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>Per-player view and control settings, saved in PlayerPrefs. The settings menu comes in M6.</summary>
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
            };
        }

        public void Save()
        {
            PlayerPrefs.SetFloat("sculpt.fov", Fov);
            PlayerPrefs.SetFloat("sculpt.sens", Sensitivity);
            PlayerPrefs.SetInt("sculpt.bob", HeadBob ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.fovkick", SpeedFovKick ? 1 : 0);
            PlayerPrefs.SetFloat("sculpt.vmsize", ViewmodelSize);
            PlayerPrefs.Save();
        }
    }
}
