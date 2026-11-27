using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Weapons
{
    /// <summary>One built gun: its root, the muzzle point, and the part that cycles (pump grip or pistol slide).</summary>
    public sealed class GunModel
    {
        public Weapon Weapon;
        public GameObject Root;
        public Transform Muzzle;
        /// <summary>Pump grip (slides back and forward after a shot) or pistol slide (snaps back). Null for the others.</summary>
        public Transform Cycler;
        public Vector3 CyclerRest;
    }

    /// <summary>
    /// Placeholder guns built from boxes in code (real models later). Sizes are real-world metres so they read as
    /// heavy, solid objects: steel receivers, long barrels, wood on the pump. Local +Z is the barrel direction.
    /// </summary>
    public static class GunModels
    {
        private static readonly Color Steel = new Color(0.22f, 0.23f, 0.25f);
        private static readonly Color DarkSteel = new Color(0.13f, 0.135f, 0.15f);
        private static readonly Color Wood = new Color(0.42f, 0.27f, 0.15f);
        private static readonly Color Polymer = new Color(0.17f, 0.17f, 0.16f);
        private static readonly Color Tan = new Color(0.55f, 0.48f, 0.36f);

        public static GunModel Build(Weapon w, Material mat, int layer = 0)
        {
            var m = new GunModel { Weapon = w, Root = new GameObject("Gun " + w) };
            var b = new BoxBuilder();
            var cyc = new BoxBuilder();
            float muzzleZ;
            switch (w)
            {
                case Weapon.Pump:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.062f, 0.085f, 0.30f), Steel);              // receiver
                    b.Box(new Vector3(0, 0.022f, 0.38f), new Vector3(0.036f, 0.036f, 0.48f), DarkSteel); // barrel
                    b.Box(new Vector3(0, -0.022f, 0.33f), new Vector3(0.03f, 0.03f, 0.38f), DarkSteel);  // magazine tube
                    b.Box(new Vector3(0, 0.048f, 0.6f), new Vector3(0.008f, 0.012f, 0.01f), Steel);      // bead sight
                    b.Box(new Vector3(0, -0.075f, -0.09f), new Vector3(0.04f, 0.1f, 0.05f), Wood, 18f);  // grip
                    b.Box(new Vector3(0, -0.03f, -0.29f), new Vector3(0.052f, 0.1f, 0.28f), Wood, -4f);  // stock
                    b.Box(new Vector3(0, -0.055f, -0.04f), new Vector3(0.012f, 0.03f, 0.06f), DarkSteel); // trigger guard
                    cyc.Box(Vector3.zero, new Vector3(0.058f, 0.052f, 0.15f), Wood);                      // pump grip
                    m.CyclerRest = new Vector3(0, -0.03f, 0.29f);
                    muzzleZ = 0.62f;
                    break;
                case Weapon.Smg:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.055f, 0.085f, 0.27f), Polymer);
                    b.Box(new Vector3(0, 0.015f, 0.2f), new Vector3(0.028f, 0.028f, 0.14f), DarkSteel);
                    b.Box(new Vector3(0, -0.11f, 0.05f), new Vector3(0.034f, 0.16f, 0.05f), DarkSteel, -6f);  // magazine
                    b.Box(new Vector3(0, -0.075f, -0.08f), new Vector3(0.036f, 0.095f, 0.045f), Polymer, 16f); // grip
                    b.Box(new Vector3(0, 0.055f, -0.02f), new Vector3(0.02f, 0.025f, 0.08f), Steel);          // sight
                    b.Box(new Vector3(0, -0.01f, -0.22f), new Vector3(0.012f, 0.06f, 0.2f), Steel);           // wire stock
                    muzzleZ = 0.27f;
                    break;
                case Weapon.Ar:
                    b.Box(new Vector3(0, 0, 0), new Vector3(0.06f, 0.09f, 0.40f), Tan);
                    b.Box(new Vector3(0, 0.005f, 0.30f), new Vector3(0.062f, 0.07f, 0.22f), Polymer);          // handguard
                    b.Box(new Vector3(0, 0.012f, 0.47f), new Vector3(0.026f, 0.026f, 0.16f), DarkSteel);       // barrel
                    b.Box(new Vector3(0, 0.012f, 0.56f), new Vector3(0.036f, 0.036f, 0.04f), DarkSteel);       // muzzle brake
                    b.Box(new Vector3(0, -0.125f, 0.07f), new Vector3(0.034f, 0.17f, 0.07f), Polymer, -12f);   // curved mag
                    b.Box(new Vector3(0, -0.08f, -0.1f), new Vector3(0.036f, 0.1f, 0.045f), Polymer, 18f);     // grip
                    b.Box(new Vector3(0, -0.02f, -0.33f), new Vector3(0.052f, 0.11f, 0.24f), Polymer);         // stock
                    b.Box(new Vector3(0, 0.065f, 0.0f), new Vector3(0.024f, 0.04f, 0.1f), DarkSteel);          // optic
                    muzzleZ = 0.58f;
                    break;
                default: // pistol
                    b.Box(new Vector3(0, -0.012f, 0.0f), new Vector3(0.032f, 0.025f, 0.17f), Polymer);         // frame
                    b.Box(new Vector3(0, -0.075f, -0.055f), new Vector3(0.033f, 0.12f, 0.055f), Polymer, 14f); // grip
                    cyc.Box(Vector3.zero, new Vector3(0.036f, 0.04f, 0.19f), Steel);                           // slide
                    cyc.Box(new Vector3(0, 0.024f, 0.085f), new Vector3(0.006f, 0.008f, 0.008f), DarkSteel);
                    m.CyclerRest = new Vector3(0, 0.02f, 0.005f);
                    muzzleZ = 0.11f;
                    break;
            }
            Attach(m.Root.transform, "Body", b.ToMesh("Gun " + w), mat, layer);
            if (cyc.Count > 0)
            {
                m.Cycler = Attach(m.Root.transform, "Cycler", cyc.ToMesh("Gun cycler " + w), mat, layer);
                m.Cycler.localPosition = m.CyclerRest;
            }
            m.Muzzle = new GameObject("Muzzle").transform;
            m.Muzzle.SetParent(m.Root.transform, false);
            m.Muzzle.localPosition = new Vector3(0, w == Weapon.Pistol ? 0.02f : 0.015f, muzzleZ);
            m.Root.layer = layer;
            return m;
        }

        private static readonly Color Olive = new Color(0.30f, 0.34f, 0.22f);

        /// <summary>A frag grenade in the hand (or on the ground): olive body, steel fuse head and spoon. Origin at its centre.</summary>
        public static GameObject BuildGrenade(Material mat, int layer = 0)
        {
            var root = new GameObject("Grenade") { layer = layer };
            var b = new BoxBuilder();
            b.Box(Vector3.zero, new Vector3(0.062f, 0.075f, 0.062f), Olive);                          // body
            b.Box(Vector3.zero, new Vector3(0.07f, 0.05f, 0.05f), Olive);                             // rounded sides
            b.Box(Vector3.zero, new Vector3(0.05f, 0.05f, 0.07f), Olive);
            b.Box(new Vector3(0, 0.048f, 0), new Vector3(0.026f, 0.022f, 0.026f), DarkSteel);         // fuse head
            b.Box(new Vector3(0, 0.03f, -0.03f), new Vector3(0.014f, 0.05f, 0.008f), Steel, -12f);    // spoon
            b.Box(new Vector3(0.018f, 0.055f, 0), new Vector3(0.012f, 0.016f, 0.004f), Steel);        // pin ring
            Attach(root.transform, "Body", b.ToMesh("Grenade"), mat, layer);
            return root;
        }

        private static readonly Color Gauze = new Color(0.92f, 0.89f, 0.80f);
        private static readonly Color KitGreen = new Color(0.16f, 0.55f, 0.32f);

        /// <summary>A bandage roll (Ben 2026-11-26: +25 health): a pale gauze roll with a loose tail. Origin at its middle.</summary>
        public static GameObject BuildBandage(Material mat, int layer = 0)
        {
            var root = new GameObject("Bandage") { layer = layer };
            var b = new BoxBuilder();
            b.Box(Vector3.zero, new Vector3(0.07f, 0.06f, 0.07f), Gauze);                              // roll
            b.Box(Vector3.zero, new Vector3(0.08f, 0.045f, 0.045f), Gauze);                            // rounded sides
            b.Box(Vector3.zero, new Vector3(0.045f, 0.045f, 0.08f), Gauze);
            b.Box(new Vector3(0.0f, -0.025f, 0.06f), new Vector3(0.05f, 0.006f, 0.06f), Gauze, 8f);    // loose tail
            Attach(root.transform, "Body", b.ToMesh("Bandage"), mat, layer);
            return root;
        }

        /// <summary>A first-aid kit (+100 health): a green case with a white cross (the first-aid sign) and a dark handle.
        /// Origin at the middle of its base.</summary>
        public static GameObject BuildMedkit(Material mat, int layer = 0)
        {
            var root = new GameObject("First-aid kit") { layer = layer };
            var b = new BoxBuilder();
            b.Box(new Vector3(0, 0.06f, 0), new Vector3(0.26f, 0.12f, 0.18f), KitGreen);               // case
            b.Box(new Vector3(0, 0.122f, 0), new Vector3(0.1f, 0.006f, 0.032f), Color.white);           // cross on the lid
            b.Box(new Vector3(0, 0.122f, 0), new Vector3(0.032f, 0.006f, 0.1f), Color.white);
            b.Box(new Vector3(0, 0.06f, 0.092f), new Vector3(0.07f, 0.022f, 0.006f), Color.white);      // cross on the front
            b.Box(new Vector3(0, 0.06f, 0.092f), new Vector3(0.022f, 0.07f, 0.006f), Color.white);
            b.Box(new Vector3(0, 0.135f, 0), new Vector3(0.09f, 0.018f, 0.02f), DarkSteel);              // handle
            Attach(root.transform, "Body", b.ToMesh("First-aid kit"), mat, layer);
            return root;
        }

        // (Wood is shared with the gun stocks.)
        private static readonly Color WoodDark = new Color(0.30f, 0.19f, 0.10f);
        private static readonly Color Brass = new Color(0.86f, 0.66f, 0.28f);

        /// <summary>
        /// A battle royale loot chest: a wooden box with a curved-looking lid (stepped), dark iron bands and a brass lock
        /// on the front (+Z). Origin at the middle of its base.
        /// </summary>
        public static GameObject BuildChest(Material mat, int layer = 0)
        {
            var root = new GameObject("Chest") { layer = layer };
            var b = new BoxBuilder();
            b.Box(new Vector3(0, 0.22f, 0), new Vector3(0.9f, 0.44f, 0.56f), Wood);                    // body
            b.Box(new Vector3(0, 0.49f, 0), new Vector3(0.9f, 0.1f, 0.52f), WoodDark);                 // lid, stepped
            b.Box(new Vector3(0, 0.56f, 0), new Vector3(0.9f, 0.06f, 0.4f), WoodDark);
            b.Box(new Vector3(0, 0.6f, 0), new Vector3(0.9f, 0.04f, 0.24f), WoodDark);
            foreach (float x in new[] { -0.34f, 0.34f })
                b.Box(new Vector3(x, 0.3f, 0), new Vector3(0.06f, 0.62f, 0.58f), DarkSteel);           // iron bands
            b.Box(new Vector3(0, 0.36f, 0.29f), new Vector3(0.12f, 0.14f, 0.03f), Brass);              // lock
            Attach(root.transform, "Body", b.ToMesh("Chest"), mat, layer);
            return root;
        }

        /// <summary>A thin glowing plate under a battle royale gun, in its rarity's colour.</summary>
        public static GameObject BuildRarityPlate(Material mat, Color colour, int layer = 0)
        {
            var root = new GameObject("Rarity") { layer = layer };
            var b = new BoxBuilder();
            b.Box(new Vector3(0, 0.006f, 0), new Vector3(0.75f, 0.012f, 0.32f), colour);
            Attach(root.transform, "Plate", b.ToMesh("Rarity plate"), mat, layer);
            return root;
        }

        private static readonly Color Putty = new Color(0.66f, 0.62f, 0.50f);
        private static readonly Color Tape = new Color(0.10f, 0.10f, 0.10f);
        private static readonly Color Blink = new Color(1f, 0.16f, 0.10f);

        /// <summary>
        /// A C4 charge: a taped block of putty with a small receiver box and a red light (child "Light", which the view
        /// blinks). Flat side (-Y) is the side that sticks. Origin at its centre.
        /// </summary>
        public static GameObject BuildC4(Material mat, int layer = 0)
        {
            var root = new GameObject("C4") { layer = layer };
            var b = new BoxBuilder();
            b.Box(Vector3.zero, new Vector3(0.2f, 0.06f, 0.12f), Putty);                               // block
            b.Box(new Vector3(-0.05f, 0, 0), new Vector3(0.018f, 0.064f, 0.124f), Tape);              // tape bands
            b.Box(new Vector3(0.05f, 0, 0), new Vector3(0.018f, 0.064f, 0.124f), Tape);
            b.Box(new Vector3(0, 0.042f, 0), new Vector3(0.06f, 0.026f, 0.05f), DarkSteel);           // receiver
            b.Box(new Vector3(0.022f, 0.07f, 0.012f), new Vector3(0.004f, 0.05f, 0.004f), Steel);     // antenna
            Attach(root.transform, "Body", b.ToMesh("C4"), mat, layer);
            var l = new BoxBuilder();
            l.Box(Vector3.zero, new Vector3(0.016f, 0.012f, 0.016f), Blink);
            Transform light = Attach(root.transform, "Light", l.ToMesh("C4 light"), mat, layer);
            light.localPosition = new Vector3(-0.012f, 0.058f, 0.0f);
            return root;
        }

        /// <summary>The detonator in the hand: a grip with a thumb switch and an antenna. +Z forward.</summary>
        public static GameObject BuildDetonator(Material mat, int layer = 0)
        {
            var root = new GameObject("Detonator") { layer = layer };
            var b = new BoxBuilder();
            b.Box(Vector3.zero, new Vector3(0.04f, 0.11f, 0.035f), Polymer, 10f);                     // grip
            b.Box(new Vector3(0, 0.06f, 0.006f), new Vector3(0.03f, 0.014f, 0.024f), Blink);         // switch
            b.Box(new Vector3(0.012f, 0.1f, -0.006f), new Vector3(0.005f, 0.08f, 0.005f), Steel);    // antenna
            Attach(root.transform, "Body", b.ToMesh("Detonator"), mat, layer);
            return root;
        }

        /// <summary>
        /// The grenade launcher: a fat single tube with a heavy receiver, a ladder sight and a thick stock. Child "Muzzle"
        /// marks the tube end. Big on purpose: it reads as a slow, heavy tool.
        /// </summary>
        public static GunModel BuildLauncher(Material mat, int layer = 0)
        {
            var m = new GunModel { Root = new GameObject("Launcher") { layer = layer } };
            var b = new BoxBuilder();
            b.Box(new Vector3(0, 0, 0.0f), new Vector3(0.09f, 0.1f, 0.22f), DarkSteel);                // receiver
            b.Box(new Vector3(0, 0.012f, 0.26f), new Vector3(0.085f, 0.085f, 0.34f), Olive);           // tube
            b.Box(new Vector3(0, 0.012f, 0.44f), new Vector3(0.095f, 0.095f, 0.03f), DarkSteel);       // tube lip
            b.Box(new Vector3(0, -0.04f, 0.24f), new Vector3(0.05f, 0.05f, 0.16f), Wood);              // fore grip
            b.Box(new Vector3(0, -0.085f, -0.08f), new Vector3(0.04f, 0.11f, 0.05f), Polymer, 18f);    // grip
            b.Box(new Vector3(0, -0.02f, -0.27f), new Vector3(0.06f, 0.12f, 0.26f), Wood, -4f);        // stock
            b.Box(new Vector3(0, 0.08f, 0.05f), new Vector3(0.05f, 0.05f, 0.012f), Steel);             // ladder sight
            Attach(m.Root.transform, "Body", b.ToMesh("Launcher"), mat, layer);
            m.Muzzle = new GameObject("Muzzle").transform;
            m.Muzzle.SetParent(m.Root.transform, false);
            m.Muzzle.localPosition = new Vector3(0, 0.012f, 0.46f);
            return m;
        }

        /// <summary>A launcher round in flight or on the ground: a stubby brass-and-olive shell, +Z forward.</summary>
        public static GameObject BuildRound(Material mat, int layer = 0)
        {
            var root = new GameObject("Launcher round") { layer = layer };
            var b = new BoxBuilder();
            b.Box(Vector3.zero, new Vector3(0.042f, 0.042f, 0.08f), Olive);
            b.Box(new Vector3(0, 0, -0.05f), new Vector3(0.046f, 0.046f, 0.03f), new Color(0.62f, 0.5f, 0.26f));
            b.Box(new Vector3(0, 0, 0.05f), new Vector3(0.028f, 0.028f, 0.025f), Steel);
            Attach(root.transform, "Body", b.ToMesh("Launcher round"), mat, layer);
            return root;
        }

        private static readonly Color Glove = new Color(0.16f, 0.15f, 0.14f);
        private static readonly Color GloveCuff = new Color(0.26f, 0.24f, 0.21f);

        /// <summary>A gloved fist and forearm for the punch, knuckles along +Z.</summary>
        public static GameObject BuildFist(Material mat, int layer = 0)
        {
            var root = new GameObject("Fist") { layer = layer };
            var b = new BoxBuilder();
            b.Box(new Vector3(0, 0, 0), new Vector3(0.085f, 0.075f, 0.09f), Glove);                    // fist
            for (int k = 0; k < 4; k++)                                                                  // knuckles
                b.Box(new Vector3(-0.03f + k * 0.02f, 0.012f, 0.047f), new Vector3(0.018f, 0.03f, 0.012f), Glove);
            b.Box(new Vector3(-0.045f, -0.01f, 0.01f), new Vector3(0.02f, 0.035f, 0.05f), Glove);       // thumb
            b.Box(new Vector3(0, -0.005f, -0.07f), new Vector3(0.07f, 0.06f, 0.06f), GloveCuff);        // cuff
            b.Box(new Vector3(0, -0.01f, -0.22f), new Vector3(0.065f, 0.06f, 0.26f), Polymer);          // sleeve
            Attach(root.transform, "Body", b.ToMesh("Fist"), mat, layer);
            return root;
        }

        private static Transform Attach(Transform parent, string name, Mesh mesh, Material mat, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Boxes with per-face shading baked in vertex colours (top brightest, sides darker, bottom darkest).</summary>
        public sealed class BoxBuilder
        {
            private readonly List<Vector3> _v = new List<Vector3>();
            private readonly List<Vector3> _n = new List<Vector3>();
            private readonly List<Color> _c = new List<Color>();
            private readonly List<int> _t = new List<int>();
            public int Count => _v.Count;

            /// <param name="pitchDeg">Rotation about the local X axis (grips and magazines lean).</param>
            public void Box(Vector3 centre, Vector3 size, Color colour, float pitchDeg = 0f)
            {
                Quaternion q = Quaternion.Euler(pitchDeg, 0, 0);
                Vector3 h = size * 0.5f;
                void Face(Vector3 n, Vector3 u, Vector3 v, float shade)
                {
                    int i = _v.Count;
                    Vector3 c = Vector3.Scale(n, h);
                    Vector3 du = Vector3.Scale(u, h), dv = Vector3.Scale(v, h);
                    Vector3[] p = { c - du - dv, c - du + dv, c + du + dv, c + du - dv };
                    if (Vector3.Dot(Vector3.Cross(p[1] - p[0], p[2] - p[0]), n) < 0) { Vector3 t = p[1]; p[1] = p[3]; p[3] = t; }
                    Color s = new Color(colour.r * shade, colour.g * shade, colour.b * shade, 1);
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) { s = s.linear; s.a = 1; }
                    foreach (Vector3 pt in p) { _v.Add(centre + q * pt); _n.Add(q * n); _c.Add(s); }
                    _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
                    _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
                }
                Face(Vector3.up, Vector3.right, Vector3.forward, 1.0f);
                Face(Vector3.down, Vector3.right, Vector3.forward, 0.45f);
                Face(Vector3.forward, Vector3.right, Vector3.up, 0.8f);
                Face(Vector3.back, Vector3.right, Vector3.up, 0.7f);
                Face(Vector3.right, Vector3.forward, Vector3.up, 0.66f);
                Face(Vector3.left, Vector3.forward, Vector3.up, 0.6f);
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(_v);
                m.SetNormals(_n);
                m.SetColors(_c);
                m.SetTriangles(_t, 0);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
