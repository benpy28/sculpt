using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Wall finishes per building (graphics pass 2026-11-28, Ben approved): each building's breakable walls are drawn in
    /// one of a few finishes (red brick, buff brick, dark brick, whitewashed render, grey block) so a village reads as
    /// different houses rather than one brick box repeated. Visual only.
    ///
    /// Fairness: the finish comes from a hash of the building's distance from the map's centre along the two axes,
    /// sorted (|dx|, |dz| in order), which is the same for a building and every twin the map's symmetry makes (the battle
    /// royale's eight turns and mirrors, the 1v1 map's half turn), so no side or spawn gets a better backdrop.
    ///
    /// The shader finds a wall's building from a 1 m map of building footprints (R8: finish index, 0 = none), sampled a
    /// little inside the wall, so no per-vertex data or mesh change is needed and damage, cracks and patches keep working.
    /// </summary>
    public static class BuildingPalettes
    {
        public const int Count = 5; // finishes 1..5 in SculptGreybox.shader; 0 = keep the vertex colour
        private const float Texel = 1f;
        private static readonly int IdMap = Shader.PropertyToID("_PaletteMap");
        private static readonly int IdInfo = Shader.PropertyToID("_PaletteInfo");
        private static Texture2D _tex;

        /// <summary>The finish (1..Count) for a building with this footprint on a map centred at (cx, cz).</summary>
        public static int FinishOf(Aabb b, double cx, double cz)
        {
            double dx = System.Math.Abs((b.Min.X + b.Max.X) * 0.5 - cx), dz = System.Math.Abs((b.Min.Z + b.Max.Z) * 0.5 - cz);
            double lo = System.Math.Min(dx, dz), hi = System.Math.Max(dx, dz);
            // Round to 0.1 m so floating error between twins never changes the pick.
            uint h = (uint)System.Math.Round(lo * 10) * 73856093u ^ (uint)System.Math.Round(hi * 10) * 19349663u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            // Red brick is the most common, the others share the rest.
            uint r = h % 100u;
            return r < 36 ? 1 : r < 56 ? 2 : r < 72 ? 3 : r < 88 ? 4 : 5;
        }

        /// <summary>Builds the footprint map for these buildings (call once per map); none = palettes off.</summary>
        public static void Build(IList<Aabb> buildings, double cx, double cz)
        {
            if (_tex != null) { Object.Destroy(_tex); _tex = null; }
            if (buildings == null || buildings.Count == 0) { Shader.SetGlobalVector(IdInfo, Vector4.zero); return; }
            double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
            foreach (Aabb b in buildings)
            {
                x0 = System.Math.Min(x0, b.Min.X); z0 = System.Math.Min(z0, b.Min.Z);
                x1 = System.Math.Max(x1, b.Max.X); z1 = System.Math.Max(z1, b.Max.Z);
            }
            x0 -= 4; z0 -= 4; x1 += 4; z1 += 4;
            int w = Mathf.CeilToInt((float)(x1 - x0) / Texel), h = Mathf.CeilToInt((float)(z1 - z0) / Texel);
            int size = Mathf.Max(w, h);
            if (size > 4096) { Debug.LogWarning("[SCULPT] Building finishes: map too big for the footprint map; plain brick."); Shader.SetGlobalVector(IdInfo, Vector4.zero); return; }
            var px = new byte[size * size];
            foreach (Aabb b in buildings)
            {
                byte f = (byte)FinishOf(b, cx, cz);
                int i0 = Mathf.FloorToInt((float)(b.Min.X - 0.6 - x0) / Texel), i1 = Mathf.CeilToInt((float)(b.Max.X + 0.6 - x0) / Texel);
                int j0 = Mathf.FloorToInt((float)(b.Min.Z - 0.6 - z0) / Texel), j1 = Mathf.CeilToInt((float)(b.Max.Z + 0.6 - z0) / Texel);
                for (int j = Mathf.Max(0, j0); j < Mathf.Min(size, j1); j++)
                    for (int i = Mathf.Max(0, i0); i < Mathf.Min(size, i1); i++)
                        px[i + j * size] = f;
            }
            _tex = new Texture2D(size, size, TextureFormat.R8, false, true) { name = "Building finishes", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            _tex.SetPixelData(px, 0);
            _tex.Apply(false, true);
            Shader.SetGlobalTexture(IdMap, _tex);
            Shader.SetGlobalVector(IdInfo, new Vector4((float)x0, (float)z0, 1f / (size * Texel), 1f));
        }
    }
}
