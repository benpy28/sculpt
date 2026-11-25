using System;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The "Video and latency" block of the Esc menu: V-Sync, frame rate limit, low latency (frame queue), render
    /// scale, lean rendering and the two overlays. Drawn with GUILayout inside the settings menu's column. Changes
    /// apply at once and are saved as soon as no control is being dragged.
    /// </summary>
    public static class VideoSettingsPanel
    {
        private static bool _unsaved;

        public static void Draw(GUIStyle header, GUIStyle label, GUIStyle value)
        {
            VideoSettings v = VideoSettings.Current;
            bool changed = false;
            GUILayout.Label("Video and latency", header);

            changed |= Toggle("V-Sync (off = lowest latency)", ref v.VSync, label);

            GUILayout.BeginHorizontal();
            GUILayout.Label(v.VSync ? "Frame rate limit (V-Sync sets it)" : "Frame rate limit", label, GUILayout.Width(300));
            int at = Mathf.Max(0, Array.IndexOf(VideoSettings.CapChoices, v.FpsCap));
            GUI.enabled = !v.VSync;
            if (GUILayout.Button("<", GUILayout.Width(28))) { at = (at + VideoSettings.CapChoices.Length - 1) % VideoSettings.CapChoices.Length; v.FpsCap = VideoSettings.CapChoices[at]; changed = true; }
            string name = VideoSettings.CapName(v.FpsCap);
            if (v.FpsCap == VideoSettings.CapBelowRefresh)
            {
                int cap = v.CapFor(Rendering.RenderProfile.RefreshHz);
                if (cap > 0) name += " (" + cap + ")";
            }
            GUILayout.Label(name, value, GUILayout.Width(150));
            if (GUILayout.Button(">", GUILayout.Width(28))) { at = (at + 1) % VideoSettings.CapChoices.Length; v.FpsCap = VideoSettings.CapChoices[at]; changed = true; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            changed |= Toggle("Low latency (one queued frame)", ref v.LowLatency, label);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Render scale", label, GUILayout.Width(300));
            float scale = GUILayout.HorizontalSlider(v.RenderScale, VideoSettings.MinRenderScale, VideoSettings.MaxRenderScale, GUILayout.Width(300));
            scale = Mathf.Round(scale * 20f) / 20f; // 5% steps
            if (!Mathf.Approximately(scale, v.RenderScale)) { v.RenderScale = scale; changed = true; }
            GUILayout.Label(Mathf.RoundToInt(v.RenderScale * 100f) + "%", value, GUILayout.Width(70));
            GUILayout.EndHorizontal();

            changed |= Toggle("Lean rendering (recommended)", ref v.LeanRendering, label);
            changed |= Toggle("Show FPS", ref v.ShowFps, label);
            changed |= Toggle("Show latency panel", ref v.ShowLatency, label);

            if (changed)
            {
                v.Clamped();
                v.Changed();
                _unsaved = true;
            }
            // Save once the mouse lets go, not on every step of a slider drag.
            if (_unsaved && GUIUtility.hotControl == 0)
            {
                _unsaved = false;
                v.Save();
            }
        }

        private static bool Toggle(string name, ref bool on, GUIStyle label)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, label, GUILayout.Width(300));
            bool n = GUILayout.Toggle(on, on ? " On" : " Off");
            GUILayout.EndHorizontal();
            if (n == on) return false;
            on = n;
            return true;
        }
    }
}
