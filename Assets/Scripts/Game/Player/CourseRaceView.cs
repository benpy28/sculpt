using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Game.UI;
using UnityEngine;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// The F6 course race on screen: the TAS ghost (a pale player-sized figure that never collides and never stops a
    /// bullet; it only exists on your screen), a big 3-2-1 / GO, the clock under the compass with the TAS time beside
    /// it, and your result against the TAS at the finish. Reads <see cref="LocalPlayer.Race"/>; nothing here changes the
    /// simulation.
    /// </summary>
    public sealed class CourseRaceView : MonoBehaviour
    {
        private LocalPlayer _player;
        private MovementConfig _cfg;
        private GameObject _ghost;
        private Transform _body, _head, _visor;
        private Material _gl;
        // Clock texts by centisecond (a string per frame would be garbage per frame).
        private readonly Dictionary<int, string> _times = new Dictionary<int, string>();
        private int _resultFor = -1;
        private string _result = "", _resultLine = "", _tasLine = "";
        private CourseRace _raceSeen;
        private Color _resultColor;

        private static readonly Color GhostColour = new Color(0.78f, 0.96f, 0.92f); // pale mint: not a team colour, never purple

        public void Init(LocalPlayer player, MovementConfig cfg)
        {
            _player = player;
            _cfg = cfg;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);

            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { name = "TAS ghost (runtime)" };
            _ghost = new GameObject("TAS Ghost");
            _ghost.transform.SetParent(transform, false);
            _body = Part("Body", RemotePlayersView.BuildBox(GhostColour), mat);
            _head = Part("Head", RemotePlayersView.BuildBox(Color.Lerp(GhostColour, Color.white, 0.4f)), mat);
            _visor = Part("Visor", RemotePlayersView.BuildBox(new Color(0.1f, 0.12f, 0.14f), centred: true), mat);
            _ghost.SetActive(false);
        }

        private Transform Part(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_ghost.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        private void LateUpdate()
        {
            CourseRace race = _player != null ? _player.Race : null;
            bool show = race != null && race.Ghost.Length > 0;
            if (_ghost.activeSelf != show) _ghost.SetActive(show);
            if (!show) return;
            // The player is drawn between its last two ticks; the ghost is drawn at the same moment of its own run.
            double t = race.RunTicks - 1 + _player.RenderAlpha;
            Vec3d p = race.GhostAt(t);
            double yaw = race.GhostYawAt(t);
            _ghost.transform.SetPositionAndRotation(p.ToUnity(), Quaternion.Euler(0, (float)(yaw * Mathf.Rad2Deg), 0));
            float h = (float)_cfg.StandHeight, w = (float)(_cfg.HalfWidth * 2), headH = (float)_cfg.Weapons.HeadZone(h);
            float headW = Mathf.Min(w, (float)_cfg.Weapons.HeadWidth);
            _body.localScale = new Vector3(w, h - headH, w);
            _head.localScale = new Vector3(headW, headH, headW);
            _head.localPosition = new Vector3(0, h - headH, 0);
            _visor.localScale = new Vector3(0.42f, 0.1f, 0.08f);
            _visor.localPosition = new Vector3(0, (float)_cfg.StandEye - 0.05f, headW * 0.48f);
        }

        private string Time(int ticks)
        {
            int cs = Mathf.RoundToInt(ticks * 100f / SimTime.TicksPerSecond);
            if (!_times.TryGetValue(cs, out string s)) _times[cs] = s = $"{cs / 100}.{cs % 100:00}";
            return s;
        }

        private void OnGUI()
        {
            CourseRace race = _player != null ? _player.Race : null;
            if (race == null || Event.current.type != EventType.Repaint) return;
            float w = Screen.width, h = Screen.height;
            float s = Mathf.Max(0.75f, h / 1080f);

            float pw = 300 * s, ph = 46 * s, px = w / 2 - pw / 2, py = h - 70 * s - ph;
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.PanelRect(px, py, pw, ph);
                if (race.PlayerFinished) HudDraw.PanelRect(w / 2 - 330 * s, h * 0.62f - 70 * s, 660 * s, 140 * s);
            }
            finally { GL.PopMatrix(); }

            Rect panel = HudDraw.ToGui(px, py, pw, ph);
            int shown = race.PlayerFinished ? race.PlayerFinishTicks : race.RunTicks;
            HudDraw.Label(new Rect(panel.x + 14 * s, panel.y, 110 * s, panel.height), "TAS RACE", Mathf.RoundToInt(15 * s), HudDraw.Dim, TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(panel.x, panel.y, panel.width, panel.height), Time(shown), Mathf.RoundToInt(24 * s), race.PlayerFinished ? HudDraw.Amber : HudDraw.Text, TextAnchor.MiddleCenter, true);
            if (race != _raceSeen) { _raceSeen = race; _tasLine = "TAS " + Time(race.GhostFinishTicks); }
            HudDraw.Label(new Rect(panel.xMax - 124 * s, panel.y, 110 * s, panel.height), _tasLine, Mathf.RoundToInt(15 * s), HudDraw.Dim, TextAnchor.MiddleRight, true);

            Rect mid = new Rect(w / 2 - 330 * s, h * 0.38f - 70 * s, 660 * s, 140 * s);
            if (!race.Started)
            {
                HudDraw.Label(mid, HudDraw.Num(race.CountdownSeconds), Mathf.RoundToInt(96 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                HudDraw.Label(new Rect(mid.x, mid.yMax, mid.width, 34 * s), "Race the TAS to the finish pad   (F6 again to stop)", Mathf.RoundToInt(20 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
            }
            else if (race.RunTicks < SimTime.TicksPerSecond * 3 / 4)
                HudDraw.Label(mid, "GO", Mathf.RoundToInt(80 * s), HudDraw.Amber, TextAnchor.MiddleCenter, true);

            if (race.PlayerFinished)
            {
                if (_resultFor != race.PlayerFinishTicks)
                {
                    _resultFor = race.PlayerFinishTicks;
                    _resultLine = "You " + Time(race.PlayerFinishTicks) + "   vs   " + _tasLine;
                    int d = race.PlayerFinishTicks - race.GhostFinishTicks;
                    bool beat = race.GhostFinishTicks > 0 && d < 0;
                    _result = (beat ? "YOU BEAT THE TAS   -" : "TAS WINS   +") + Time(Mathf.Abs(d));
                    _resultColor = beat ? HudDraw.Amber : HudDraw.Text;
                }
                Rect res = HudDraw.ToGui(w / 2 - 330 * s, h * 0.62f - 70 * s, 660 * s, 140 * s);
                HudDraw.Label(new Rect(res.x, res.y + 14 * s, res.width, 60 * s), _result, Mathf.RoundToInt(44 * s), _resultColor, TextAnchor.MiddleCenter, true);
                HudDraw.Label(new Rect(res.x, res.y + 80 * s, res.width, 30 * s), _resultLine, Mathf.RoundToInt(20 * s), HudDraw.Text, TextAnchor.MiddleCenter);
            }
            else _resultFor = -1;
        }
    }
}
