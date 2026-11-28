using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The 1v1 anti-hiding ping on screen: a sonar blip when the server fires it, a short line under the match panel, and
    /// a diamond with the distance over each revealed enemy (kept on the screen edge when they are off screen). The
    /// see-through silhouette itself is drawn by <see cref="RemotePlayersView"/>. Nothing is predicted: only what the
    /// server's Reveal message says, for as long as it says.
    /// </summary>
    public sealed class PingHud : MonoBehaviour
    {
        private const string Banner = "PING   you can see each other through walls";

        private NetSession _session;
        private Camera _camera;
        private Material _gl;
        private AudioClip _blip;
        private float _bannerUntil;
        private readonly List<int> _ids = new List<int>();

        public void Init(NetSession session, Camera camera)
        {
            _session = session;
            _camera = camera;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); // HUD: never hidden behind the world's depth (walls covered the minimap)
            // Two falling sonar blips with a short tail: clear, not alarming, never masking footsteps for long.
            _blip = Synth.Make("Ping", 0.55f, false, (t, i) =>
            {
                float a = t < 0.22f ? Mathf.Exp(-t * 18f) : Mathf.Exp(-(t - 0.22f) * 9f) * 0.8f;
                float f = t < 0.22f ? 1480f : 1110f;
                float tt = t < 0.22f ? t : t - 0.22f;
                return Mathf.Sin(2 * Mathf.PI * f * tt) * a * Mathf.Clamp01(tt * 400f);
            }, 0.45f);
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            List<RevealEntry> started = _session.Client.RevealsStarted;
            if (started.Count == 0) return;
            float longest = 0;
            foreach (RevealEntry e in started)
                longest = Mathf.Max(longest, (float)((e.EndTick - _session.Client.ServerTimeEstimate) / SimTime.TicksPerSecond));
            started.Clear();
            _bannerUntil = Time.unscaledTime + Mathf.Max(0.5f, longest);
            Synth.Play(_blip, Vector3.zero, 0.8f, 1f, spatial: false, bus: SoundBus.Ui);
        }

        private void OnGUI()
        {
            if (_session == null || _camera == null) return;
            if (Event.current.type != EventType.Repaint) return;
            NetClient net = _session.Client;
            if (!net.Joined) return;
            float w = Screen.width, h = Screen.height;
            float s = Mathf.Max(0.75f, h / 1080f);
            bool banner = Time.unscaledTime < _bannerUntil;

            net.GetRemoteIds(_ids);
            bool any = false;
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.BeginBatch();
                foreach (int id in _ids)
                {
                    if (!net.IsRevealed(id, out RevealEntry e)) continue;
                    if (!net.TryGetRemote(id, out RemoteView v) || v.Dead) continue;
                    any = true;
                    Vector2 p = Marker(v.Position.ToUnity() + Vector3.up * 2.15f, w, h, 40 * s);
                    float r = 11 * s;
                    Color c = RemotePlayersView.TeamColours[Mathf.Abs(v.Team - 1) % RemotePlayersView.TeamColours.Length];
                    float fade = Mathf.Clamp01((float)((e.EndTick - net.ServerTimeEstimate) / SimTime.TicksPerSecond) / 0.4f);
                    Color edge = new Color(0, 0, 0, 0.55f * fade);
                    Diamond(p, r + 2.5f * s, edge);
                    Diamond(p, r, HudDraw.A(Color.Lerp(c, Color.white, 0.25f), fade));
                }
                if (banner && any) HudDraw.PanelRect(w / 2 - 250 * s, h - 160 * s, 500 * s, 34 * s);
            }
            finally
            {
                HudDraw.EndBatch();
                GL.PopMatrix();
            }
            if (!any) return;
            foreach (int id in _ids)
            {
                if (!net.IsRevealed(id, out _) || !net.TryGetRemote(id, out RemoteView v) || v.Dead) continue;
                Vector2 p = Marker(v.Position.ToUnity() + Vector3.up * 2.15f, w, h, 40 * s);
                int metres = Mathf.RoundToInt((v.Position.ToUnity() - _camera.transform.position).magnitude);
                Rect r = HudDraw.ToGui(p.x - 40 * s, p.y - 36 * s, 80 * s, 20 * s);
                HudDraw.Label(r, HudDraw.Num(metres), Mathf.RoundToInt(16 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
            }
            if (banner)
                HudDraw.Label(HudDraw.ToGui(w / 2 - 250 * s, h - 160 * s, 500 * s, 34 * s), Banner, Mathf.RoundToInt(17 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
        }

        /// <summary>Screen point (y up) of a world point, pushed to the screen edge (with a margin) when off screen or behind.</summary>
        private Vector2 Marker(Vector3 world, float w, float h, float margin)
        {
            Vector3 sp = _camera.WorldToScreenPoint(world);
            Vector2 c = new Vector2(w / 2, h / 2);
            Vector2 p = new Vector2(sp.x, sp.y);
            bool behind = sp.z < 0;
            if (behind) p = c - (p - c); // mirrored: it is behind you
            bool inside = !behind && p.x >= margin && p.x <= w - margin && p.y >= margin && p.y <= h - margin;
            if (inside) return p;
            Vector2 d = p - c;
            if (d.sqrMagnitude < 1e-4f) d = Vector2.down;
            float kx = Mathf.Abs(d.x) > 1e-4f ? (w / 2 - margin) / Mathf.Abs(d.x) : float.MaxValue;
            float ky = Mathf.Abs(d.y) > 1e-4f ? (h / 2 - margin) / Mathf.Abs(d.y) : float.MaxValue;
            return c + d * Mathf.Min(kx, ky);
        }

        private static void Diamond(Vector2 p, float r, Color c)
        {
            HudDraw.Quad(p + new Vector2(0, r), p + new Vector2(r, 0), p + new Vector2(0, -r), p + new Vector2(-r, 0), c);
        }
    }
}
