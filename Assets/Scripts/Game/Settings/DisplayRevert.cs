using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>
    /// The "keep this display setting?" timer. A display change you can't see (a resolution the monitor can't show) must
    /// go back by itself, so the timer lives on its own hidden object that survives scene reloads and runs whether or not
    /// the settings menu is open. Before, it only ran while the menu was open and Esc kept the change, so closing the menu
    /// left a bad resolution for good. The menu calls <see cref="Begin"/> before a change, <see cref="Keep"/> for "Keep",
    /// and <see cref="GoBack"/> for "Go back" and when it closes with the question still open.
    /// </summary>
    public sealed class DisplayRevert : MonoBehaviour
    {
        /// <summary>How long the question waits before going back by itself (seconds, unscaled time).</summary>
        public static float PromptSeconds = 12f;

        private static DisplayRevert _instance;
        private static float _revertAt;
        private static int _w, _h;
        private static FullScreenMode _mode;

        /// <summary>A change is waiting to be kept.</summary>
        public static bool Pending => _revertAt > 0;

        public static float SecondsLeft => Pending ? Mathf.Max(0f, _revertAt - Time.unscaledTime) : 0f;

        /// <summary>Changes the display and starts (or restarts) the timer. The setting to go back to is the one from
        /// before the first unanswered change, so stepping through several resolutions still goes back to the last one
        /// that was kept.</summary>
        public static void Begin(int width, int height, FullScreenMode mode)
        {
            Ensure();
            if (!Pending)
            {
                _w = Screen.width; _h = Screen.height;
                _mode = Screen.fullScreenMode;
            }
            Screen.SetResolution(width, height, mode);
            _revertAt = Time.unscaledTime + PromptSeconds;
        }

        public static void Keep() => _revertAt = 0;

        public static void GoBack()
        {
            if (!Pending) return;
            _revertAt = 0;
            Screen.SetResolution(_w, _h, _mode);
        }

        private static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("SCULPT display revert") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DisplayRevert>();
        }

        private void Update()
        {
            if (Pending && Time.unscaledTime > _revertAt) GoBack();
        }
    }
}
