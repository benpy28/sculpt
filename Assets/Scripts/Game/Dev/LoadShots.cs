using System;
using System.IO;
using System.Text;
using Sculpt.Game.Bootstrap;
using Sculpt.Game.Input;
using Sculpt.Game.UI;
using UnityEngine;

namespace Sculpt.Game.Dev
{
    /// <summary>
    /// "-loadshots dir [map]" (dev builds and the Editor): checks the loading screen without a person at the PC. From the
    /// home screen it starts an offline match (map: duel, br or breach; default duel), saves a picture every 0.12 s from
    /// the press until 4 s after the card has gone, logs the card's alpha and each frame time, then quits.
    /// </summary>
    public sealed class LoadShots : MonoBehaviour
    {
        private string _dir;
        private GameBootstrap.MapChoice _map;
        private float _t, _nextShot, _goneAt = -1f;
        private int _shot;
        private bool _pressed;
        private readonly StringBuilder _log = new StringBuilder();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!(GameInput.DevBuild || Application.isEditor)) return;
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => string.Equals(a, "-loadshots", StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at + 1 >= args.Length) return;
            var go = new GameObject("Load Shots");
            DontDestroyOnLoad(go);
            var s = go.AddComponent<LoadShots>();
            s._dir = args[at + 1];
            string m = at + 2 < args.Length ? args[at + 2].ToLowerInvariant() : "";
            s._map = m == "br" ? GameBootstrap.MapChoice.BattleRoyale : m == "breach" ? GameBootstrap.MapChoice.Breach : GameBootstrap.MapChoice.Duel;
            Directory.CreateDirectory(s._dir);
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _t += dt;
            if (!_pressed)
            {
                if (_t < 3f) return; // the home screen settles first
                _pressed = true;
                _t = 0;
                _nextShot = 0;
                GameBootstrap.Relaunch(GameBootstrap.Launch.Offline, _map);
            }
            _log.Append($"{_t:0.000} s  frame {dt * 1000:0.0} ms  loading {(LoadingScreen.Busy ? "yes" : "no")}\n");
            if (!LoadingScreen.Busy && _goneAt < 0) _goneAt = _t;
            if (_t >= _nextShot && _shot < 200)
            {
                _nextShot = _t + 0.12f;
                ScreenCapture.CaptureScreenshot(Path.Combine(_dir, $"load_{_shot++:000}_{_t:00.00}s.png"));
            }
            if (_goneAt >= 0 && _t > _goneAt + 4f || _t > 60f)
            {
                File.WriteAllText(Path.Combine(_dir, "frames.txt"), _log.ToString());
                Debug.Log($"[SCULPT] Load shots: {_shot} pictures, card gone at {_goneAt:0.00} s.");
                Application.Quit();
            }
        }
    }
}
