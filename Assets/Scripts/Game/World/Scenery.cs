using System.Collections.Generic;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Backdrop for the plains: procedural sky (SCULPT/Sky), haze fog that matches its horizon, the fixed sun, and a
    /// ring of distant hills and tree lines far outside the arena wall. All of it is out of reach and behind the wall,
    /// so none of it can hide a player.
    /// </summary>
    public static class Scenery
    {
        public static readonly Color Horizon = new Color(0.80f, 0.87f, 0.92f);
        public static readonly Color Zenith = new Color(0.36f, 0.57f, 0.86f);

        /// <summary>
        /// How far anyone sees (Ben's map v3 plan: about 800 m, so towers and hills read across the map). The haze is the
        /// same on every setting and always on (Video > Picture > Fog only adds the atmospheric look on top), so nobody
        /// sees further than anyone else: soft from <see cref="HazeFrom"/>, everything gone by the view distance, where
        /// the camera stops drawing.
        /// </summary>
        public const float ViewDistance = 800f, HazeFrom = 100f;

        /// <summary>Builds sky, fog and backdrop around an arena centred at (centreX, centreZ). Returns the sun.</summary>
        public static Light Build(float centreX, float centreZ)
        {
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(55f, -35f, 0f); // fixed sun direction
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.None; // Video > Picture > Shadows turns them on (Rendering/PictureEffects)
            RenderSettings.sun = sun;

            Shader sky = Shader.Find("SCULPT/Sky");
            if (sky != null)
            {
                var m = new Material(sky) { name = "Sky (runtime)" };
                m.SetColor("_Horizon", Horizon);
                m.SetColor("_Zenith", Zenith);
                m.SetVector("_SunDir", -sun.transform.forward);
                RenderSettings.skybox = m;
            }
            else Debug.LogError("[SCULPT] Shader SCULPT/Sky not found; plain sky colour.");

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Horizon;
            RenderSettings.fogStartDistance = HazeFrom;
            RenderSettings.fogEndDistance = ViewDistance;

            BuildBackdrop(centreX, centreZ);
            return sun;
        }

        private static void BuildBackdrop(float cx, float cz)
        {
            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null) return;
            BuildSkirt(cx, cz, shader);
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var v = new List<Vector3>();
            var col = new List<Color>();
            var tri = new List<int>();
            // Tree line (closer, darker, crowns as bumps), then rolling hills behind it.
            // Out past the map's corners: on the big maps (the battle royale) fixed radii put the rings inside the play area.
            float near = Mathf.Max(125f, Mathf.Sqrt(cx * cx + cz * cz) + 40f);
            Ring(v, col, tri, cx, cz, near, 12f, 18f, 0.9f, new Color(0.20f, 0.34f, 0.19f), new Color(0.14f, 0.24f, 0.14f), 7, linear);
            Ring(v, col, tri, cx, cz, near + 60f, 20f, 46f, 0.05f, new Color(0.42f, 0.56f, 0.40f), new Color(0.30f, 0.42f, 0.30f), 3, linear);

            var mesh = new Mesh { name = "Backdrop", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(v);
            mesh.SetColors(col);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Backdrop");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(shader) { name = "Backdrop (runtime)" };
            r.sharedMaterial.SetFloat("_Scenery", 1f);
            r.sharedMaterial.SetFloat("_Detail", 0.03f);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>
        /// Meadow ground from the map's edge out under the backdrop (visual only, nobody can go there), so the play
        /// area doesn't end in a floating slab with sky under it. A square frame around the map at y 0, drawn with the
        /// terrain's own ground look; the map's own ground fills the hole exactly.
        /// </summary>
        private static void BuildSkirt(float cx, float cz, Shader shader)
        {
            float sx = cx * 2, sz = cz * 2, far = Mathf.Max(420f, Mathf.Sqrt(cx * cx + cz * cz) + 220f - Mathf.Min(cx, cz)); // past the backdrop
            var v = new List<Vector3>();
            var tri = new List<int>();
            void Rect(float x0, float z0, float x1, float z1)
            {
                int i = v.Count;
                v.Add(new Vector3(x0, 0, z0)); v.Add(new Vector3(x0, 0, z1)); v.Add(new Vector3(x1, 0, z1)); v.Add(new Vector3(x1, 0, z0));
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
            }
            // The frame reaches 0.3 m under the map's own ground: the terrain mesh stops a few centimetres short of the
            // grid's far edges, and the overlap is the same meadow colour at the same height, so nothing shows.
            const float lap = 0.3f;
            Rect(-far, -far, sx + far, lap);              // south
            Rect(-far, sz - lap, sx + far, sz + far);     // north
            Rect(-far, lap, lap, sz - lap);               // west
            Rect(sx - lap, lap, sx + far, sz - lap);      // east
            var mesh = new Mesh { name = "Ground skirt" };
            mesh.SetVertices(v);
            mesh.SetTriangles(tri, 0);
            var n = new Vector3[v.Count];
            var c = new Color[v.Count];
            var k = new Vector2[v.Count];
            for (int i = 0; i < v.Count; i++) { n[i] = Vector3.up; c[i] = Color.white; k[i] = new Vector2(1f, 0f); } // kind: ground
            mesh.normals = n;
            mesh.colors = c;
            mesh.SetUVs(1, k);
            mesh.RecalculateBounds();
            var go = new GameObject("Ground skirt");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(shader) { name = "Ground skirt (runtime)" };
            r.sharedMaterial.SetFloat("_Scenery", 1f);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = true;
        }

        /// <summary>A closed band facing the arena: bottom below ground, ragged top (bumps = crowns or hilltops).</summary>
        private static void Ring(List<Vector3> v, List<Color> col, List<int> tri, float cx, float cz, float radius,
                                 float minH, float maxH, float bumpy, Color top, Color bottom, int seed, bool linear)
        {
            const int segments = 360;
            int start = v.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                float t = i / (float)segments * 40f;
                float shape = Mathf.PerlinNoise(t * 0.35f + seed * 10f, seed) * 0.7f + Mathf.PerlinNoise(t * 1.3f, seed + 5f) * 0.3f;
                float bumps = Mathf.Abs(Mathf.Sin(a * 140f + Mathf.PerlinNoise(t * 4f, seed) * 6f)) * bumpy;
                float h = Mathf.Lerp(minH, maxH, shape) + bumps * 2.5f;
                float r = radius + Mathf.PerlinNoise(t * 0.5f, seed + 9f) * 20f;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v.Add(new Vector3(cx, -4f, cz) + dir * r);
                v.Add(new Vector3(cx, h, cz) + dir * (r + h * 0.8f));
                col.Add(linear ? bottom.linear : bottom);
                Color tc = Color.Lerp(top, top * 1.12f, Mathf.PerlinNoise(t * 2f, seed + 3f));
                tc.a = 1;
                col.Add(linear ? tc.linear : tc);
            }
            for (int i = 0; i < segments; i++)
            {
                int b0 = start + i * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                // Faces the centre (front faces seen from inside the ring).
                tri.AddRange(new[] { b0, b1, t0, t0, b1, t1 });
            }
        }
    }
}
