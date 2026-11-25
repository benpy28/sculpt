using System.Collections;
using System.Collections.Generic;
using System.IO;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Dev
{
    /// <summary>
    /// Dev tool: "-phototour folder" (dev builds and the Editor only) flies the camera through a fixed list of views of
    /// the current map, saves one PNG per view with no HUD and no gun, then quits. For before/after comparisons of map
    /// visuals. The game runs offline underneath as normal; nothing here touches the simulation.
    /// </summary>
    [DefaultExecutionOrder(-200)] // place the camera before the world draws grass for it
    public sealed class PhotoTour : MonoBehaviour
    {
        public struct Shot
        {
            public string Name;
            public Vector3 Position;
            public float Yaw, Pitch;
        }

        private readonly List<Shot> _shots = new List<Shot>();
        private string _folder;
        private Camera _cam;
        private int _index = -1;

        /// <summary>The folder from "-phototour folder", or null.</summary>
        public static string RequestedFolder()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "-phototour") return args[i + 1];
            return null;
        }

        public void Init(string folder, Camera cam, BreachMapInfo info, bool duel)
        {
            _folder = folder;
            _cam = cam;
            Directory.CreateDirectory(folder);
            BuildShots(info, duel);
            foreach (MonoBehaviour b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                string n = b.GetType().Name;
                if (n.EndsWith("Hud") || n == "SettingsMenu" || n == "MainMenu") b.enabled = false;
            }
            StartCoroutine(Run());
        }

        private void Add(string name, Vector3 p, float yaw, float pitch) =>
            _shots.Add(new Shot { Name = $"{_shots.Count + 1:00}_{name}", Position = p, Yaw = yaw, Pitch = pitch });

        private static Vector3 V(Vec3d v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        private static float YawToward(Vector3 from, Vector3 to) => Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;

        private void BuildShots(BreachMapInfo info, bool duel)
        {
            // "-photoshots file": only the views listed there, one per line: name x y z yaw pitch.
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "-photoshots" && File.Exists(args[i + 1]))
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    foreach (string line in File.ReadAllLines(args[i + 1]))
                    {
                        string[] p = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 6) continue;
                        Add(p[0], new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)), float.Parse(p[4], inv), float.Parse(p[5], inv));
                    }
                    return;
                }
            float sx = (float)info.SizeX, sz = (float)info.SizeZ;
            var centre = new Vector3(sx / 2, 0, sz / 2);
            GreyboxInfo b = info.Block;
            if (b != null && b.Buildings.Count > 0)
            {
                Aabb first = b.Buildings[0];
                centre = new Vector3((float)(first.Min.X + first.Max.X) / 2, 0, (float)(first.Min.Z + first.Max.Z) / 2);
            }
            // Overviews.
            Add("overview_south", centre + new Vector3(0, 30, -48), 0, 30);
            Add("overview_corner", centre + new Vector3(40, 26, 40), 225, 28);
            Vector3 spawn = V(info.TeamSpawns[0][0]) + Vector3.up * 1.62f;
            Add("spawn_team1", spawn, (float)(info.SpawnYaw[0] * Mathf.Rad2Deg), 2);
            if (b == null) return;
            // Each building: ground floor from a corner, and a low view along a wall base.
            for (int i = 0; i < b.Buildings.Count; i++)
            {
                Aabb a = b.Buildings[i];
                var lo = new Vector3((float)a.Min.X, 0, (float)a.Min.Z);
                var hi = new Vector3((float)a.Max.X, 0, (float)a.Max.Z);
                var mid = (lo + hi) / 2;
                Vector3 corner = lo + new Vector3(1.4f, 1.62f, 1.4f);
                Add($"building{i}_inside", corner, YawToward(corner, mid), 14);
                Vector3 low = new Vector3(mid.x, 0.6f, lo.z + 1.2f);
                Add($"building{i}_floor_low", low, 90, 18);
                Vector3 outside = new Vector3(mid.x, 1.62f, lo.z - 7);
                Add($"building{i}_front", outside, 0, 4);
                Vector3 up = new Vector3(mid.x, 6.4f, mid.z);
                Add($"building{i}_upstairs", up + new Vector3(0, 0, 0), 30, 10);
            }
            // Every stair flight and ramp: from just before the bottom looking up, and from the side.
            for (int i = 0; i < b.Climbs.Count; i++)
            {
                Brush c = b.Climbs[i];
                Vector3 d = c.Rise == Dir.North ? Vector3.forward : c.Rise == Dir.South ? Vector3.back : c.Rise == Dir.East ? Vector3.right : Vector3.left;
                Vector3 bmin = V(c.Bounds.Min), bmax = V(c.Bounds.Max);
                Vector3 mid = (bmin + bmax) / 2;
                float len = Mathf.Abs(Vector3.Dot(bmax - bmin, d));
                Vector3 bottom = new Vector3(mid.x, bmin.y, mid.z) - d * (len / 2 + 2.2f) + Vector3.up * 1.62f;
                string kind = c.Kind == BrushKind.Stairs ? "stairs" : "ramp";
                Add($"{kind}{i}_bottom", bottom, YawToward(bottom, mid), -12);
                Vector3 side = Vector3.Cross(Vector3.up, d);
                Vector3 beside = mid - d * (len * 0.25f) + side * 2.2f + Vector3.up * 0.9f;
                Add($"{kind}{i}_close", beside, YawToward(beside, mid - d * (len * 0.25f) + Vector3.up * -0.5f), 30);
            }
        }

        private IEnumerator Run()
        {
            // Let the world mesh and the first frames settle.
            for (int f = 0; f < 30; f++) yield return null;
            for (_index = 0; _index < _shots.Count; _index++)
            {
                for (int f = 0; f < 6; f++) yield return null;
                yield return new WaitForEndOfFrame();
                string path = Path.Combine(_folder, _shots[_index].Name + ".png");
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Destroy(tex);
            }
            Debug.Log($"[SCULPT] Photo tour: {_shots.Count} shots in {_folder}");
            Application.Quit();
        }

        private void LateUpdate()
        {
            if (_cam == null || _index < 0 || _index >= _shots.Count) return;
            Shot s = _shots[_index];
            _cam.transform.SetPositionAndRotation(s.Position, Quaternion.Euler(s.Pitch, s.Yaw, 0));
            _cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(95, _cam.aspect);
            foreach (Renderer r in _cam.GetComponentsInChildren<Renderer>()) r.enabled = false; // no gun
        }
    }
}
