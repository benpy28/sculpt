using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using UnityEngine;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// Draws other players from the NetClient's interpolated views: a box the exact size of their collision box
    /// (in team colour, never purple) and a dark visor showing where they look. Placeholder art until real models.
    /// </summary>
    public sealed class RemotePlayersView : MonoBehaviour
    {
        public static readonly Color[] TeamColours =
        {
            new Color(0.20f, 0.50f, 0.95f), // team 1: blue
            new Color(0.95f, 0.45f, 0.15f), // team 2: orange
        };

        private NetSession _session;
        private MovementConfig _cfg;
        private Material _material;
        private Mesh _boxMesh;
        private readonly Dictionary<int, Avatar> _avatars = new Dictionary<int, Avatar>();
        private readonly List<int> _ids = new List<int>();
        private readonly List<int> _stale = new List<int>();

        private sealed class Avatar
        {
            public GameObject Root;
            public Transform Body, Head, Visor, Gun;
            public Item GunShown = (Item)255;
            public int Team = -1;
            public MeshRenderer BodyR, HeadR;
            public MaterialPropertyBlock Block;
        }

        public void Init(NetSession session, MovementConfig cfg)
        {
            _session = session;
            _cfg = cfg;
            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            _material = new Material(shader) { name = "Remote player (runtime)" };
            // Drawn after every transparent sculpt overlay (ghosts, tells, dust): nothing is ever drawn over an enemy.
            _material.renderQueue = 3100;
            _boxMesh = BuildBox();
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            NetClient net = _session.Client;
            net.GetRemoteIds(_ids);

            _stale.Clear();
            foreach (var kv in _avatars) if (!_ids.Contains(kv.Key)) _stale.Add(kv.Key);
            foreach (int id in _stale)
            {
                Destroy(_avatars[id].Root);
                _avatars.Remove(id);
            }

            foreach (int id in _ids)
            {
                if (!net.TryGetRemote(id, out RemoteView v)) continue;
                if (!_avatars.TryGetValue(id, out Avatar a))
                {
                    a = Create(id);
                    _avatars[id] = a;
                }
                if (a.Team != v.Team) SetTeam(a, v.Team);
                // The dead are gone from view (and from hit checks) until they respawn.
                if (a.Root.activeSelf == v.Dead) a.Root.SetActive(!v.Dead);
                if (v.Dead) continue;

                float h = (float)(v.Crouched ? _cfg.CrouchHeight : _cfg.StandHeight);
                float eye = (float)(v.Crouched ? _cfg.CrouchEye : _cfg.StandEye);
                float w = (float)(_cfg.HalfWidth * 2);
                a.Root.transform.SetPositionAndRotation(v.Position.ToUnity(), Quaternion.Euler(0, (float)(v.Yaw * Mathf.Rad2Deg), 0));
                // Body below the head; the head is exactly the headshot zone (top of the box, full width), so what you see
                // is what you hit. Together they fill the collision box.
                float headH = (float)_cfg.Weapons.HeadZone(h);
                a.Body.localScale = new Vector3(w, h - headH, w);
                a.Body.localPosition = Vector3.zero;
                a.Head.localScale = new Vector3(w, headH, w);
                a.Head.localPosition = new Vector3(0, h - headH, 0);
                a.Head.localRotation = Quaternion.identity;
                a.Visor.localPosition = new Vector3(0, eye - 0.05f, w * 0.4f);
                a.Visor.localRotation = Quaternion.Euler((float)v.Pitch, 0, 0);

                // Their gun: held at the right side, along their aim; lowered out of sight in sculpt mode (a tell).
                bool gunOut = (v.Hand == HandMode.Weapon || v.Hand == HandMode.Drawing) && v.Held != Item.None;
                if (a.Gun.gameObject.activeSelf != gunOut) a.Gun.gameObject.SetActive(gunOut);
                if (gunOut)
                {
                    if (a.GunShown != v.Held)
                    {
                        a.GunShown = v.Held;
                        // A grenade in hand is a small fist-sized lump (a tell: a throw is coming).
                        float len = v.Held == Item.Pump ? 0.95f : v.Held == Item.Ar ? 0.9f : v.Held == Item.Smg ? 0.55f : v.Held == Item.Grenade ? 0.12f
                            : v.Held == Item.C4 ? 0.14f : v.Held == Item.Launcher ? 0.85f : 0.25f;
                        bool small = v.Held == Item.Grenade || v.Held == Item.C4;
                        float thick = small ? 0.12f : v.Held == Item.Launcher ? 0.13f : 0.07f;
                        a.Gun.localScale = new Vector3(thick, small ? 0.12f : v.Held == Item.Launcher ? 0.14f : 0.11f, len);
                    }
                    a.Gun.localPosition = new Vector3(w * 0.5f + 0.02f, eye - 0.28f, 0.1f);
                    a.Gun.localRotation = Quaternion.Euler((float)v.Pitch, 0, 0);
                }
            }
        }

        private Avatar Create(int id)
        {
            var a = new Avatar { Root = new GameObject("Remote Player " + id), Block = new MaterialPropertyBlock() };
            a.Root.transform.SetParent(transform, false);
            a.Body = Part(a.Root.transform, "Body", out a.BodyR);
            a.Head = Part(a.Root.transform, "Head", out a.HeadR);
            a.Visor = Part(a.Root.transform, "Visor", out MeshRenderer visorR);
            a.Visor.localScale = new Vector3(0.42f, 0.1f, 0.08f);
            a.Visor.GetComponent<MeshFilter>().sharedMesh = BuildBox(new Color(0.08f, 0.08f, 0.1f), centred: true);
            a.Gun = Part(a.Root.transform, "Gun", out _);
            a.Gun.GetComponent<MeshFilter>().sharedMesh = GunBox();
            return a;
        }

        private Transform Part(Transform parent, string name, out MeshRenderer r)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _boxMesh;
            r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        private void SetTeam(Avatar a, int team)
        {
            a.Team = team;
            Color c = TeamColours[Mathf.Abs(team - 1) % TeamColours.Length];
            Mesh m = BuildBox(c, centred: false);
            a.Body.GetComponent<MeshFilter>().sharedMesh = m;
            a.Head.GetComponent<MeshFilter>().sharedMesh = BuildBox(Color.Lerp(c, Color.white, 0.25f), centred: false);
        }

        private Mesh _gunBox;
        /// <summary>A unit box whose back face sits at z 0 (it extends forward along the aim).</summary>
        private Mesh GunBox()
        {
            if (_gunBox != null) return _gunBox;
            _gunBox = BuildBox(new Color(0.16f, 0.16f, 0.17f), centred: true);
            var v = _gunBox.vertices;
            for (int i = 0; i < v.Length; i++) v[i].z += 0.5f;
            _gunBox.vertices = v;
            _gunBox.RecalculateBounds();
            return _gunBox;
        }

        /// <summary>Unit box (bottom-centred unless centred) with per-face shading baked into vertex colours.</summary>
        private static Mesh BuildBox(Color? colour = null, bool centred = false)
        {
            Color c = colour ?? Color.grey;
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float y0 = centred ? -0.5f : 0f;
            Vector3 o = new Vector3(-0.5f, y0, -0.5f);
            void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e, Vector3 normal, float shade)
            {
                int i = verts.Count;
                if (Vector3.Dot(Vector3.Cross(b - a, d - a), normal) < 0) { Vector3 t = b; b = e; e = t; } // Unity front-face winding
                verts.Add(o + a); verts.Add(o + b); verts.Add(o + d); verts.Add(o + e);
                Color s = new Color(c.r * shade, c.g * shade, c.b * shade, 1);
                if (QualitySettings.activeColorSpace == ColorSpace.Linear) s = s.linear;
                s.a = 1;
                cols.Add(s); cols.Add(s); cols.Add(s); cols.Add(s);
                uvs.Add(Vector2.zero); uvs.Add(Vector2.zero); uvs.Add(Vector2.zero); uvs.Add(Vector2.zero); // no seam, never "sculptable"
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            // Same per-axis shading as the world: top brightest, N/S darker, E/W darker again.
            Quad(new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0), Vector3.up, 1.00f); // up
            Quad(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), Vector3.down, 0.50f); // down
            Quad(new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), Vector3.forward, 0.80f); // north
            Quad(new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0), Vector3.back, 0.80f); // south
            Quad(new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1), Vector3.right, 0.66f); // east
            Quad(new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), Vector3.left, 0.66f); // west
            var mesh = new Mesh { name = "Player box" };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
