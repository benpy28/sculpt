using Sculpt.Game.Audio;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Sound captions (Settings > Accessibility > Captions): a short stack of lines above the hotbar, newest at the bottom,
    /// each with an arrow that points the way the sound reaches you (it turns as you turn) and "muffled" when it comes
    /// through a wall or round a corner. Lines fade out over their last half second. Draw-only, repaint only, no
    /// strings built per frame (every text is cached).
    /// </summary>
    public sealed class CaptionsHud : MonoBehaviour
    {
        private Material _gl;
        private static readonly string[] Plain = new string[32], Muffled = new string[32];

        public static CaptionsHud Install(GameObject host)
        {
            var h = host.AddComponent<CaptionsHud>();
            h.useGUILayout = false;
            return h;
        }

        private static string TextOf(SoundCaption c, bool muffled)
        {
            int i = (int)c;
            string[] cache = muffled ? Muffled : Plain;
            return cache[i] ?? (cache[i] = muffled ? SoundCaptions.Text(c) + "  (muffled)" : SoundCaptions.Text(c));
        }

        private void OnGUI()
        {
            if (!SoundCaptions.On || Event.current.type != EventType.Repaint) return;
            Transform ear = SoundRouter.Listener;
            if (ear == null) return;
            float now = Time.unscaledTime;
            ByAge(now);
            if (_count == 0) return;
            if (_gl == null)
            {
                _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _gl.SetInt("_Cull", 0);
                _gl.SetInt("_ZWrite", 0);
                _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }

            float w = Screen.width, h = Screen.height;
            const float lineH = 26f, boxW = 300f;
            float bottom = 150f; // pixels above the screen's bottom edge (clear of the hotbar)
            Vector3 fwd = ear.forward; fwd.y = 0;
            float yawEar = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

            // Order newest at the bottom: draw by age.
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.BeginBatch();
                int[] order = ByAge(now);
                for (int row = 0; row < _count; row++)
                {
                    int i = order[row];
                    ref SoundCaptions.Line l = ref SoundCaptions.Lines[i];
                    float a = Alpha(l, now);
                    float y = bottom + row * (lineH + 4f);
                    HudDraw.RoundRect(w / 2f - boxW / 2f, y, boxW, lineH, 7f, new Color(0.03f, 0.035f, 0.045f, 0.72f * a));
                    // Arrow on the left: up = ahead, turning with your view.
                    Vector3 to = l.At - ear.position; to.y = 0;
                    float bearing = to.sqrMagnitude < 0.25f ? 0f : Mathf.DeltaAngle(yawEar, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
                    float r = bearing * Mathf.Deg2Rad;
                    Vector2 c = new Vector2(w / 2f - boxW / 2f + 16f, y + lineH / 2f);
                    Vector2 dir = new Vector2(Mathf.Sin(r), Mathf.Cos(r)); // GL pixels: y up
                    Vector2 side = new Vector2(dir.y, -dir.x);
                    HudDraw.Tri(c + dir * 8f, c - dir * 5f + side * 6f, c - dir * 5f - side * 6f, new Color(1f, 1f, 1f, 0.95f * a));
                }
            }
            finally
            {
                HudDraw.EndBatch();
                GL.PopMatrix();
            }
            int[] lines = ByAge(now);
            for (int k = 0; k < _count; k++)
            {
                SoundCaptions.Line l = SoundCaptions.Lines[lines[k]];
                float y = bottom + k * (lineH + 4f);
                HudDraw.Label(HudDraw.ToGui(w / 2f - boxW / 2f + 32f, y, boxW - 40f, lineH), TextOf(l.Kind, l.Muffled), 14,
                              new Color(1f, 1f, 1f, Alpha(l, now)), TextAnchor.MiddleLeft, true);
            }
        }

        private static float Alpha(in SoundCaptions.Line l, float now) => Mathf.Clamp01((SoundCaptions.Life - (now - l.Time)) / 0.5f);

        private readonly int[] _order = new int[SoundCaptions.Max];

        /// <summary>Live lines, newest first (newest sits lowest).</summary>
        private int[] ByAge(float now)
        {
            int n = 0;
            for (int i = 0; i < SoundCaptions.Max; i++)
                if (SoundCaptions.Lines[i].Kind != SoundCaption.None && now - SoundCaptions.Lines[i].Time < SoundCaptions.Life) _order[n++] = i;
            for (int i = 1; i < n; i++)
                for (int j = i; j > 0 && SoundCaptions.Lines[_order[j]].Time > SoundCaptions.Lines[_order[j - 1]].Time; j--)
                    (_order[j], _order[j - 1]) = (_order[j - 1], _order[j]);
            for (int i = n; i < SoundCaptions.Max; i++) _order[i] = -1;
            _count = n;
            return _order;
        }

        private int _count;
    }
}
