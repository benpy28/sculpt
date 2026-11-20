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

        public float DegreesPerCount => Sensitivity * 0.022f;

        public static ClientSettings Load()
        {
            return new ClientSettings
            {
                Fov = PlayerPrefs.GetFloat("sculpt.fov", 95f),
                Sensitivity = PlayerPrefs.GetFloat("sculpt.sens", 2.0f),
                HeadBob = PlayerPrefs.GetInt("sculpt.bob", 1) == 1,
                SpeedFovKick = PlayerPrefs.GetInt("sculpt.fovkick", 1) == 1,
            };
        }

        public void Save()
        {
            PlayerPrefs.SetFloat("sculpt.fov", Fov);
            PlayerPrefs.SetFloat("sculpt.sens", Sensitivity);
            PlayerPrefs.SetInt("sculpt.bob", HeadBob ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.fovkick", SpeedFovKick ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
