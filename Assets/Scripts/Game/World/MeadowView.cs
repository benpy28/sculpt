using System;
using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Grass blades on the smooth height-map ground (SCULPT/Meadow; graphics pass 2026-11-28). The ground's heights and a
    /// "may grass grow here" mask go to the graphics card once, as two textures; each frame draws up to three rings of
    /// blades round the camera with <see cref="Graphics.RenderPrimitives"/>, every blade made in the vertex shader from
    /// its instance number. Nothing per blade lives on the CPU, so the cost is a few draw calls and the graphics card's
    /// time (about 0.2-0.6 ms on a mid-range card, by grass step).
    ///
    /// Visual only, no collision. Fairness: blades are under 0.28 m and pressed flat round every player on every step;
    /// the step (Video > Picture > Grass) only changes how many blades are drawn and how far out.
    /// </summary>
    public sealed class MeadowView : MonoBehaviour
    {
        private struct Ring { public float Spacing, Inner, Outer; }

        // Per grass step: sparse (Off), full, dense (Ultra). Inner rings are fine, outer ones coarse with wider blades.
        private static readonly Ring[][] Steps =
        {
            new[] { new Ring { Spacing = 0.26f, Inner = 0f, Outer = 22f } },
            new[] { new Ring { Spacing = 0.17f, Inner = 0f, Outer = 20f }, new Ring { Spacing = 0.34f, Inner = 18f, Outer = 42f } },
            new[] { new Ring { Spacing = 0.13f, Inner = 0f, Outer = 20f }, new Ring { Spacing = 0.26f, Inner = 18f, Outer = 44f }, new Ring { Spacing = 0.52f, Inner = 41f, Outer = 75f } },
        };

        private const int MaxActors = 16;
        private static readonly int IdRing = Shader.PropertyToID("_Ring");
        private static readonly int IdOrigin = Shader.PropertyToID("_Origin");
        private static readonly int IdMap = Shader.PropertyToID("_MeadowMap");
        private static readonly int IdHeight = Shader.PropertyToID("_MeadowHeight");
        private static readonly int IdMask = Shader.PropertyToID("_MeadowMask");
        private static readonly int IdActors = Shader.PropertyToID("_MeadowActors");

        private Material _mat;
        private Texture2D _height, _mask;
        // Made in Build (Unity refuses property blocks made in a field initializer, i.e. in the constructor).
        private MaterialPropertyBlock[] _props;
        private float _minY, _maxY;
        private LocalPlayer _local;
        private NetSession _session;
        private readonly Vector4[] _actors = new Vector4[MaxActors];
        private readonly List<int> _ids = new List<int>();
        private readonly List<(float d, Vector3 p)> _near = new List<(float, Vector3)>();

        /// <summary>
        /// Builds the textures. <paramref name="noGrass"/> says how much a spot is paving or building (0..1; grass goes
        /// where it is under about a quarter). <paramref name="pave"/> is the stone paving round buildings (0..1), which the
        /// ground shader draws from the same texture, so paving and grass always agree whatever detail the ground mesh
        /// near you is drawn at.
        /// </summary>
        public void Build(HeightField f, Func<double, double, float> noGrass, Func<double, double, float> pave = null)
        {
            Shader shader = Shader.Find("SCULPT/Meadow");
            if (shader == null) { Debug.LogError("[SCULPT] Shader SCULPT/Meadow not found: no grass."); enabled = false; return; }
            _mat = new Material(shader) { name = "Meadow (runtime)" };
            _props = new[] { new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
            int w = f.NX + 1, h = f.NZ + 1;
            var heights = new float[w * h];
            var mask = new byte[w * h * 2]; // R = grass may grow, G = paving (for the ground shader)
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double x = f.VertexX(i), z = f.VertexZ(j);
                    heights[i + j * w] = (float)f[i, j];
                    // Grass on gentle ground only (it thins out from about 25 degrees, none past ~35: rock faces and
                    // steep banks stay bare), never on paving or under buildings.
                    double ny = i > 0 && j > 0 && i < w - 1 && j < h - 1 ? f.NormalAt(x, z).Y : 0;
                    float slope = Mathf.InverseLerp(0.82f, 0.91f, (float)ny);
                    float paved = noGrass != null ? noGrass(x, z) : 0f;
                    float m = slope * (1f - Mathf.InverseLerp(0.1f, 0.3f, paved));
                    mask[(i + j * w) * 2] = (byte)Mathf.RoundToInt(Mathf.Clamp01(m) * 255f);
                    mask[(i + j * w) * 2 + 1] = (byte)Mathf.RoundToInt(Mathf.Clamp01(pave != null ? pave(x, z) : 0f) * 255f);
                }
            _minY = (float)f.MinHeight - 1f;
            _maxY = (float)f.MaxHeight + 1f;
            _height = new Texture2D(w, h, TextureFormat.RFloat, false, true) { name = "Meadow heights", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _height.SetPixelData(heights, 0);
            _height.Apply(false, true);
            _mask = new Texture2D(w, h, TextureFormat.RG16, false, true) { name = "Meadow mask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _mask.SetPixelData(mask, 0);
            _mask.Apply(false, true);
            // Texel (i, j) sits on vertex (i, j): uv = (x - origin) / (cell * texels) + half a texel.
            Shader.SetGlobalVector(IdMap, new Vector4((float)f.OriginX, (float)f.OriginZ, 1f / (float)(f.Cell * w), w));
            Shader.SetGlobalTexture(IdHeight, _height);
            Shader.SetGlobalTexture(IdMask, _mask);
            for (int a = 0; a < MaxActors; a++) _actors[a] = Vector4.zero;
            Shader.SetGlobalVectorArray(IdActors, _actors);
        }

        /// <summary>Players whose feet press the grass flat (the local player and everyone drawn remotely).</summary>
        public void TrackPlayers(LocalPlayer local, NetSession session)
        {
            _local = local;
            _session = session;
        }

        private void LateUpdate()
        {
            if (_mat == null || _props == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            FeedActors(eye);

            Ring[] rings = Steps[Mathf.Clamp(PictureEffects.GrassStep, 0, Steps.Length - 1)];
            for (int r = 0; r < rings.Length; r++)
            {
                Ring ring = rings[r];
                // Grass is never drawn from far above (a view from 75 m up sees no blades anyway).
                float above = eye.y - _maxY;
                if (above > ring.Outer) continue;
                int across = Mathf.CeilToInt(ring.Outer * 2f / ring.Spacing) + 1;
                int ox = Mathf.FloorToInt(eye.x / ring.Spacing) - across / 2, oz = Mathf.FloorToInt(eye.z / ring.Spacing) - across / 2;
                MaterialPropertyBlock mp = _props[r];
                mp.SetVector(IdRing, new Vector4(ring.Spacing, across, ring.Inner, ring.Outer));
                mp.SetVector(IdOrigin, new Vector4(ox, oz, 0, 0));
                var rp = new RenderParams(_mat)
                {
                    worldBounds = new Bounds(new Vector3(eye.x, (_minY + _maxY) * 0.5f, eye.z), new Vector3(ring.Outer * 2f + 2f, _maxY - _minY + 1f, ring.Outer * 2f + 2f)),
                    matProps = mp,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = true,
                    layer = gameObject.layer,
                };
                Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 15, across * across);
            }
        }

        /// <summary>The 16 players nearest the camera (the only ones whose grass you could see pressed).</summary>
        private void FeedActors(Vector3 eye)
        {
            _near.Clear();
            if (_local != null && _session != null && _session.Client.Joined && !_session.Client.Predicted.Dead)
                _near.Add((0f, _local.RenderFeet));
            if (_session != null)
            {
                NetClient net = _session.Client;
                net.GetRemoteIds(_ids);
                foreach (int id in _ids)
                    if (net.TryGetRemote(id, out RemoteView v) && !v.Dead)
                    {
                        Vector3 p = v.Position.ToUnity();
                        _near.Add(((p - eye).sqrMagnitude, p));
                    }
            }
            if (_near.Count > MaxActors) _near.Sort((a, b) => a.d.CompareTo(b.d));
            for (int a = 0; a < MaxActors; a++)
                _actors[a] = a < _near.Count ? new Vector4(_near[a].p.x, _near[a].p.y, _near[a].p.z, 1f) : Vector4.zero;
            Shader.SetGlobalVectorArray(IdActors, _actors);
        }

        private void OnDestroy()
        {
            Shader.SetGlobalVector(IdMap, Vector4.zero); // the next map's ground must not read this one's paving
            if (_mat != null) Destroy(_mat);
            if (_height != null) Destroy(_height);
            if (_mask != null) Destroy(_mask);
        }
    }
}
