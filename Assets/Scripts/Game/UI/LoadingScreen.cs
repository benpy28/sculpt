using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The loading screen between the menu, matches and replays (Ben 2026-11-26: "loading screens would also be nice
    /// instead of harsh jumps"). Every relaunch goes through it: the screen fades to the loading card (0.15 s), the
    /// scene reloads under it (the build is one frozen frame, so the card is what stays on screen), then it waits for
    /// the new scene to draw smoothly (the first frames after a load pay for shaders and fonts) and fades away (0.35 s).
    ///
    /// Lives outside the scene (DontDestroyOnLoad) so it covers the reload. Drawn on top of every HUD (GUI.depth), one
    /// batched draw for the shapes plus a few labels; nothing is built per frame. A second request while one is running
    /// is ignored, so a double click can't load twice.
    /// </summary>
    public sealed class LoadingScreen : MonoBehaviour
    {
        public const float FadeInSeconds = 0.15f;
        public const float FadeOutSeconds = 0.35f;
        /// <summary>Frames the new scene must draw, each under <see cref="SmoothFrameSeconds"/>, before the card fades.</summary>
        public const int SmoothFrames = 3;
        public const float SmoothFrameSeconds = 0.1f;
        /// <summary>Fade out by this long after the load even if frames never settle (a very slow PC).</summary>
        public const float MaxWaitSeconds = 4f;

        private enum Step { FadeIn, Load, Settle, FadeOut }

        private static LoadingScreen _current;
        /// <summary>True from the request until the card has faded away.</summary>
        public static bool Busy => _current != null;

        private Step _step;
        private float _t;
        private int _opaqueFrames, _smooth;
        private Action _load;
        private string _title, _detail, _tip;
        private Material _gl;

        /// <summary>
        /// Fades to the loading card, then runs <paramref name="load"/> (which reloads the scene) and fades back in once
        /// the new scene draws smoothly. Returns false (and does nothing) while another load is running.
        /// </summary>
        public static bool Run(string title, string detail, Action load)
        {
            if (_current != null) return false;
            var go = new GameObject("Loading Screen");
            DontDestroyOnLoad(go);
            _current = go.AddComponent<LoadingScreen>();
            _current._title = title ?? "";
            _current._detail = detail ?? "";
            _current._load = load;
            _current._tip = Tips[UnityEngine.Random.Range(0, Tips.Length)];
            return true;
        }

        private static readonly string[] Tips =
        {
            "Sprint into a wall you've already shot and you burst straight through it.",
            "Q heals the wall you aim at. Hold Alt as you press it to reinforce instead.",
            "Slides follow your aim: start a turn early and you keep nearly all your speed.",
            "Your own grenades never hurt you, but they throw you a long way.",
            "E opens and shuts doors. A sprint goes straight through a shut one.",
            "C4 is for opening walls, not for kills: right mouse sets off your charges.",
            "Stairs, ramps and hills are where speed is built: slide down them.",
            "In a battle royale, stay inside the circle: outside it you take damage every second.",
        };

        private void Awake()
        {
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            SceneManager.sceneLoaded += OnLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            if (_gl != null) Destroy(_gl);
            if (_current == this) _current = null;
        }

        private void OnLoaded(Scene s, LoadSceneMode mode)
        {
            if (_step != Step.Load) return;
            _step = Step.Settle;
            _t = 0;
            _smooth = 0;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            // Fades and the settle wait count at most a short frame each: the first frames after a big load can take
            // seconds (a 32-player map), and counting those would end the wait and skip the fade in one frame, showing
            // a scene that is still hitching (seen in the first real-Unity check, 2026-11-26).
            float step = Mathf.Min(dt, 1f / 30f);
            switch (_step)
            {
                case Step.FadeIn:
                    _t += step;
                    if (_t >= FadeInSeconds) { _step = Step.Load; _t = 0; _opaqueFrames = 0; }
                    break;
                case Step.Load:
                    // Two whole frames of the opaque card on screen first, so the freeze shows the card, not the menu.
                    if (++_opaqueFrames == 3)
                    {
                        Action load = _load;
                        _load = null;
                        try { load?.Invoke(); }
                        catch (Exception e) { Debug.LogException(e); _step = Step.FadeOut; _t = 0; }
                    }
                    break;
                case Step.Settle:
                    _t += Mathf.Min(dt, SmoothFrameSeconds);
                    _smooth = dt < SmoothFrameSeconds ? _smooth + 1 : 0;
                    if (_smooth >= SmoothFrames || _t >= MaxWaitSeconds) { _step = Step.FadeOut; _t = 0; }
                    break;
                case Step.FadeOut:
                    _t += step;
                    if (_t >= FadeOutSeconds) Destroy(gameObject);
                    break;
            }
        }

        private float Alpha => _step switch
        {
            Step.FadeIn => Mathf.SmoothStep(0, 1, _t / FadeInSeconds),
            Step.FadeOut => 1 - Mathf.SmoothStep(0, 1, _t / FadeOutSeconds),
            _ => 1,
        };

        private static readonly Color Bg = new Color(0.035f, 0.045f, 0.06f, 1f);
        private static readonly Color BgLow = new Color(0.07f, 0.06f, 0.05f, 1f);
        private static readonly Color AmberLo = new Color(0.96f, 0.58f, 0.17f, 1f);

        private void OnGUI()
        {
            GUI.depth = -1000; // over every HUD and the menu
            if (Event.current.type != EventType.Repaint) return;
            float a = Alpha;
            if (a <= 0) return;
            float k = Screen.height / 1080f, w = Screen.width, h = Screen.height;

            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.BeginBatch();
                HudDraw.Quad(new Vector2(0, 0), new Vector2(0, h), new Vector2(w, h), new Vector2(w, 0), HudDraw.A(BgLow, a), HudDraw.A(Bg, a));
                // The logo disc and a thin amber line under the title, like the home screen.
                float lx = 96 * k, ly = h - 120 * k;
                HudDraw.Arc(lx, ly, 13.5f * k, 27 * k, 0, 360, HudDraw.A(HudDraw.Amber, a));
                HudDraw.Quad(96 * k - 27 * k, h - 640 * k, 90 * k, 4 * k, HudDraw.A(AmberLo, a));
                // Bottom right: a short bar that reads as "working" without pretending to know how far.
                HudDraw.RoundRect(w - 260 * k, 70 * k, 180 * k, 6 * k, 3 * k, HudDraw.A(new Color(1, 1, 1, 0.12f), a));
                HudDraw.RoundRect(w - 260 * k, 70 * k, 60 * k, 6 * k, 3 * k, HudDraw.A(HudDraw.Amber, a));
            }
            finally
            {
                HudDraw.EndBatch();
                GL.PopMatrix();
            }
            HudDraw.Label(new Rect(96 * k - 27 * k, 120 * k - 27 * k, 54 * k, 54 * k), "S", Mathf.RoundToInt(40 * k), HudDraw.A(new Color(0.07f, 0.06f, 0.05f, 1f), a), TextAnchor.MiddleCenter, true);
            HudDraw.Label(new Rect(140 * k, 120 * k - 26 * k, 600 * k, 52 * k), "SCULPT", Mathf.RoundToInt(40 * k), HudDraw.A(HudDraw.Text, a), TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(69 * k, 440 * k, w - 140 * k, 120 * k), _title, Mathf.RoundToInt(96 * k), HudDraw.A(HudDraw.Text, a), TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(69 * k, 560 * k, w - 140 * k, 40 * k), _detail, Mathf.RoundToInt(26 * k), HudDraw.A(HudDraw.Dim, a), TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(69 * k, 660 * k, Mathf.Min(w - 140 * k, 1100 * k), 30 * k), "TIP", Mathf.RoundToInt(15 * k), HudDraw.A(HudDraw.Amber, a), TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(69 * k, 690 * k, Mathf.Min(w - 140 * k, 1400 * k), 34 * k), _tip, Mathf.RoundToInt(22 * k), HudDraw.A(HudDraw.Text, a * 0.8f), TextAnchor.MiddleLeft);
            HudDraw.Label(new Rect(w - 260 * k, h - 120 * k, 180 * k, 30 * k), "LOADING", Mathf.RoundToInt(15 * k), HudDraw.A(HudDraw.Dim, a), TextAnchor.MiddleLeft, true);
        }
    }
}
