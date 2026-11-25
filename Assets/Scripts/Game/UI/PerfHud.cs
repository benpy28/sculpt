using System.Text;
using Sculpt.Core;
using Sculpt.Core.Diagnostics;
using Sculpt.Game.Diagnostics;
using Sculpt.Game.Input;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The performance overlay: a small frame rate readout (Esc menu: Show FPS) and the latency panel (Show latency,
    /// F3 in dev builds). The panel breaks each click down the way Ben asked: local PC (click read to packet sent),
    /// network up, server (waiting for its tick plus processing), network down plus display, each as the last click
    /// and the running average, with the frame-time figures and a trace underneath.
    ///
    /// It draws only on repaint and rebuilds its text four times a second, so having it open costs next to nothing
    /// and makes almost no garbage of its own.
    /// </summary>
    public sealed class PerfHud : MonoBehaviour
    {
        private PerfRuntime _perf;
        private VideoSettings _settings;
        private GameInput _input;
        private Material _gl;
        private float _nextText;

        private const int Rows = 8;
        private readonly string[] _name = new string[Rows];
        private readonly string[] _last = new string[Rows];
        private readonly string[] _avg = new string[Rows];
        private string _title = "", _fps = "", _line1 = "", _line2 = "", _line3 = "", _traceScale = "";
        private float _traceMaxMs = 16.7f;
        private readonly StringBuilder _sb = new StringBuilder(128);

        public static PerfHud Install(GameObject host, PerfRuntime perf, GameInput input)
        {
            var h = host.AddComponent<PerfHud>();
            h._perf = perf;
            h._input = input;
            h._settings = VideoSettings.Current;
            h.useGUILayout = false; // plain GUI calls only: no layout pass, no layout garbage
            return h;
        }

        private void Update()
        {
            if (_settings == null) return;
            if (GameInput.DevBuild && _input != null && _input.DevPerf.WasPressedThisFrame())
            {
                _settings.ShowLatency = !_settings.ShowLatency;
                _settings.Changed();
                _settings.Save();
            }
            if (!_settings.ShowFps && !_settings.ShowLatency) return;
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;
            BuildText();
        }

        private void BuildText()
        {
            FrameStats f = _perf.Frames;
            _fps = _sb.Clear().Append(f.AverageFps.ToString("0")).Append(" fps   ").Append(f.AverageMs.ToString("0.0")).Append(" ms   1% low ")
                .Append(f.LowFps(0.01).ToString("0")).ToString();
            if (!_settings.ShowLatency) return;

            LatencyProbe p = _perf.Latency;
            ClickSample l = p.LastLocal, s = p.Last;
            bool hasL = p.HasLocal, hasS = p.HasLast;
            _title = hasL ? "LATENCY   last click: " + l.Kind : "LATENCY   click, jump or punch to measure";

            Row(0, "Click to frame sent (what you feel)", hasL, l.ClickToFrameMs, p.AvgClickToFrameMs);
            Row(1, "   system event to game read", hasL, l.InputWaitMs, p.AvgInputWaitMs);
            Row(2, "   wait for the next " + SimTime.TicksPerSecond + " Hz tick", hasL, l.TickWaitMs, p.AvgTickWaitMs);
            Row(3, "Local PC: click read to packet sent", hasL, l.LocalMs, p.AvgLocalMs);
            Row(4, "Network up (half the round trip)", hasS, s.NetUpMs, p.AvgNetUpMs);
            bool serverKnown = hasS && s.ServerKnown;
            Row(5, "Server: tick wait and processing", serverKnown, s.ServerMs, p.AvgServerMs);
            if (hasS && !serverKnown) _last[5] = "n/a";
            if (p.AvgServerMs < 0) _avg[5] = hasS ? "n/a" : "-";
            Row(6, "Network down and display", hasS, s.DownAndDisplayMs, p.AvgDownAndDisplayMs);
            Row(7, "Click to server result on screen", hasS, s.TotalMs, p.AvgTotalMs);

            _line1 = _sb.Clear().Append("Frames  avg ").Append(f.AverageFps.ToString("0")).Append("   1% low ").Append(f.LowFps(0.01).ToString("0"))
                .Append("   0.1% low ").Append(f.LowFps(0.001).ToString("0")).Append("   worst ").Append(f.MaxMs.ToString("0.0")).Append(" ms").ToString();
            _sb.Clear().Append("Per frame  ");
            if (_perf.CpuFrameMs > 0) _sb.Append("CPU ").Append(_perf.CpuFrameMs.ToString("0.00")).Append(" ms   ");
            _sb.Append("GPU ").Append(_perf.GpuKnown ? _perf.GpuFrameMs.ToString("0.00") + " ms" : "n/a");
            if (_perf.AllocBytesPerFrame >= 0) _sb.Append("   garbage ").Append((_perf.AllocBytesPerFrame / 1024.0).ToString("0.00")).Append(" KB");
            _sb.Append("   ").Append((_perf.Triangles / 1000.0).ToString("0")).Append("k tris");
            _line2 = _sb.ToString();
            _line3 = _perf.Profile != null ? _perf.Profile.Summary : "";

            // Trace scale: comfortably above the usual frame, never below one 240 Hz frame.
            _traceMaxMs = Mathf.Max(4.2f, (float)f.PercentileMs(0.99) * 1.6f);
            _traceScale = "trace to " + _traceMaxMs.ToString("0.0") + " ms";
        }

        private void Row(int i, string name, bool has, double last, double avg)
        {
            _name[i] = name;
            _last[i] = has ? last.ToString("0.0") : "-";
            _avg[i] = has ? avg.ToString("0.0") : "-";
        }

        private void OnGUI()
        {
            if (_settings == null || Event.current.type != EventType.Repaint) return;
            if (!_settings.ShowFps && !_settings.ShowLatency) return;
            if (_gl == null)
            {
                _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _gl.SetInt("_Cull", 0);
                _gl.SetInt("_ZWrite", 0);
                _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }

            const float w = 440f, pad = 10f, rowH = 18f, lastCol = 78f;
            float x = Screen.width - w - 14f;
            if (!_settings.ShowLatency)
            {
                HudDraw.Label(new Rect(x, 52f, w, rowH), _fps, 13, HudDraw.Text, TextAnchor.MiddleRight);
                return;
            }
            float y = 190f; // right edge, below the kill feed

            const float traceH = 46f;
            float h = pad + rowH + 4 + rowH + Rows * rowH + 8 + 3 * rowH + 6 + traceH + pad;
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.PanelRect(x, Screen.height - y - h, w, h);
                DrawTrace(x + pad, Screen.height - y - h + pad, w - 2 * pad, traceH);
            }
            finally { GL.PopMatrix(); }

            float cx = x + pad, cw = w - 2 * pad, cy = y + pad;
            HudDraw.Label(new Rect(cx, cy, cw, rowH), _title, 13, HudDraw.Text, TextAnchor.MiddleLeft, true);
            cy += rowH + 4;
            HudDraw.Label(new Rect(cx, cy, cw - lastCol, rowH), "last", 12, HudDraw.Dim, TextAnchor.MiddleRight);
            HudDraw.Label(new Rect(cx, cy, cw, rowH), "avg ms", 12, HudDraw.Dim, TextAnchor.MiddleRight);
            cy += rowH;
            for (int i = 0; i < Rows; i++)
            {
                bool strong = i == 0 || i == 7;
                Color c = strong ? HudDraw.Text : i == 1 || i == 2 ? HudDraw.Dim : HudDraw.A(HudDraw.Text, 0.85f);
                HudDraw.Label(new Rect(cx, cy, cw, rowH), _name[i], 13, c, TextAnchor.MiddleLeft, strong);
                HudDraw.Label(new Rect(cx, cy, cw - lastCol, rowH), _last[i], 13, c, TextAnchor.MiddleRight, strong);
                HudDraw.Label(new Rect(cx, cy, cw, rowH), _avg[i], 13, c, TextAnchor.MiddleRight, strong);
                cy += rowH;
            }
            cy += 8;
            HudDraw.Label(new Rect(cx, cy, cw, rowH), _line1, 13, HudDraw.Text);
            cy += rowH;
            HudDraw.Label(new Rect(cx, cy, cw, rowH), _line2, 13, HudDraw.A(HudDraw.Text, 0.85f));
            cy += rowH;
            HudDraw.Label(new Rect(cx, cy, cw, rowH), _line3, 13, HudDraw.Dim);
            HudDraw.Label(new Rect(cx, cy, cw, rowH), _traceScale, 11, HudDraw.Dim, TextAnchor.MiddleRight);
        }

        /// <summary>The newest frames as bars, newest on the right; a frame over twice the average shows amber, over four times red.</summary>
        private void DrawTrace(float x, float y, float w, float h)
        {
            FrameStats f = _perf.Frames;
            int n = Mathf.Min(f.Count, Mathf.FloorToInt(w / 2f));
            if (n <= 0) return;
            float avg = (float)f.AverageMs;
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1, 1, 1, 0.06f));
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
            for (int i = 0; i < n; i++)
            {
                float ms = (float)f.Recent(i);
                float bh = Mathf.Clamp01(ms / _traceMaxMs) * h;
                float bx = x + w - (i + 1) * 2f;
                GL.Color(ms > avg * 4f ? HudDraw.Red : ms > avg * 2f ? HudDraw.Amber : new Color(0.75f, 0.9f, 1f, 0.8f));
                GL.Vertex3(bx, y, 0); GL.Vertex3(bx + 2f, y, 0); GL.Vertex3(bx + 2f, y + bh, 0); GL.Vertex3(bx, y + bh, 0);
            }
            GL.End();
        }

        private void OnDestroy()
        {
            if (_gl != null) Destroy(_gl);
        }
    }
}
