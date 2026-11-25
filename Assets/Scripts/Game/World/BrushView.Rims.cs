using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Wall-break visuals (Ben 2026-11-25: keep the grid in the simulation for fairness, hide it in the look; bullets
    /// chip the wall where they hit; breaks blast out as rough, roundish holes). Everything here is drawn only: it never
    /// changes collision, bullets or aiming.
    ///
    /// Rules that keep it fair:
    /// - The see-through part of a hole stays the real hole. Rough teeth along a hole's edge reach at most
    ///   <see cref="TeethMax"/> into it, and the rounded hole corners fill at most about 0.17 m along the diagonal
    ///   (radius up to <see cref="FilletMax"/>). Neither is big enough to hide a head (0.36 m), so nothing visual hides
    ///   a player that bullets would reach.
    /// - The rest of the "blast" look is surface only: a scorched, chipped band on the wall around the hole whose outer
    ///   edge is rounded at the corners, so a square hole reads as a round crater without any geometry in the hole.
    /// - A hole's rim is drawn by the broken tile, from the states of the tiles around it, so it updates the moment a
    ///   neighbour breaks or is healed. Frame (columns, beams) keeps clean steel edges: it is the building's skeleton.
    /// </summary>
    public sealed partial class BrushView
    {
        /// <summary>Vertex alpha read by SCULPT/Greybox: which surface a face is.</summary>
        private const float Greybox = 1f, Rough = 0.5f, BrickSurface = 0f;

        private const float TeethMin = 0.025f, TeethMax = 0.11f;
        private const float FilletMin = 0.30f, FilletMax = 0.42f;
        private const float BandMin = 0.10f, BandMax = 0.34f;

        private float _surface = Greybox;
        private BrushWorld _brushWorld;
        private WallMarks _marks;

        // In-plane neighbours of every wall and floor tile: 0 +a1, 1 -a1, 2 +a2, 3 -a2, then the diagonals
        // 4 (+a1,+a2), 5 (+a1,-a2), 6 (-a1,+a2), 7 (-a1,-a2). -1 = no tile of the same kind and plane there.
        private int[] _nb;

        private static readonly int[] EdgeAxis = { 1, 1, 2, 2 };
        private static readonly int[] EdgeSign = { 1, -1, 1, -1 };

        private void OnEnable() => WallImpacts.OnBullet = OnBullet;

        private void OnDisable()
        {
            if (WallImpacts.OnBullet == OnBullet) WallImpacts.OnBullet = null;
        }

        /// <summary>The plane of a wall or floor tile: n across the thin side, a1 and a2 along the face.</summary>
        private static bool Basis(in WallTile t, out Vector3 n, out Vector3 a1, out Vector3 a2)
        {
            switch (t.Kind)
            {
                case TileKind.Wall:
                    if (t.Facing == Dir.North) { n = Vector3.forward; a1 = Vector3.right; }
                    else { n = Vector3.right; a1 = Vector3.forward; }
                    a2 = Vector3.up;
                    return true;
                case TileKind.Floor:
                    n = Vector3.up; a1 = Vector3.right; a2 = Vector3.forward;
                    return true;
            }
            n = a1 = a2 = default;
            return false;
        }

        private static float Along(Vector3 size, Vector3 axis) => Mathf.Abs(Vector3.Dot(size, axis));

        private void BindRims()
        {
            if (_marks == null)
            {
                _marks = new GameObject("Bullet Marks").AddComponent<WallMarks>();
                _marks.transform.SetParent(transform, false);
            }
            _marks.Init(_mat, _linear);
            _marks.ClearAll();
            int count = _tiles.Count;
            _nb = new int[count * 8];
            for (int i = 0; i < _nb.Length; i++) _nb[i] = -1;

            // Coarse 1.5 m hash of wall and floor tiles, for probing just past each edge and corner.
            const float cell = 1.5f;
            var grid = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                if (!Basis(_tiles[i], out _, out _, out _)) continue;
                Aabb b = _tiles.BoundsOf(i);
                Vector3Int lo = Vector3Int.FloorToInt(b.Min.ToUnity() / cell + Vector3.one * 0.001f);
                Vector3Int hi = Vector3Int.FloorToInt(b.Max.ToUnity() / cell - Vector3.one * 0.001f);
                for (int x = lo.x; x <= hi.x; x++)
                    for (int y = lo.y; y <= hi.y; y++)
                        for (int z = lo.z; z <= hi.z; z++)
                        {
                            var k = new Vector3Int(x, y, z);
                            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                            list.Add(i);
                        }
            }
            int Find(Vector3 p, in WallTile like)
            {
                if (!grid.TryGetValue(Vector3Int.FloorToInt(p / cell), out var list)) return -1;
                foreach (int j in list)
                {
                    WallTile o = _tiles[j];
                    if (o.Kind != like.Kind || o.Facing != like.Facing) continue;
                    Aabb b = _tiles.BoundsOf(j);
                    if (p.x > b.Min.X && p.x < b.Max.X && p.y > b.Min.Y && p.y < b.Max.Y && p.z > b.Min.Z && p.z < b.Max.Z) return j;
                }
                return -1;
            }
            for (int i = 0; i < count; i++)
            {
                WallTile t = _tiles[i];
                if (!Basis(t, out _, out Vector3 a1, out Vector3 a2)) continue;
                Aabb b = _tiles.BoundsOf(i);
                Vector3 lo = b.Min.ToUnity(), hi = b.Max.ToUnity(), c = (lo + hi) * 0.5f, size = hi - lo;
                float e1 = Along(size, a1) * 0.5f + 0.05f, e2 = Along(size, a2) * 0.5f + 0.05f;
                _nb[i * 8 + 0] = Find(c + a1 * e1, t);
                _nb[i * 8 + 1] = Find(c - a1 * e1, t);
                _nb[i * 8 + 2] = Find(c + a2 * e2, t);
                _nb[i * 8 + 3] = Find(c - a2 * e2, t);
                _nb[i * 8 + 4] = Find(c + a1 * e1 + a2 * e2, t);
                _nb[i * 8 + 5] = Find(c + a1 * e1 - a2 * e2, t);
                _nb[i * 8 + 6] = Find(c - a1 * e1 + a2 * e2, t);
                _nb[i * 8 + 7] = Find(c - a1 * e1 - a2 * e2, t);
            }
        }

        private void DirtyNeighbours(int tile)
        {
            if (_nb == null) return;
            for (int k = 0; k < 8; k++)
            {
                int j = _nb[tile * 8 + k];
                if (j >= 0) _dirty.Add(BlockOf(_tiles.World[_tiles[j].Brush]));
            }
        }

        /// <summary>The colour a tile's surface is drawn in (what its rim, chips and marks take).</summary>
        private static Color SurfaceColour(in WallTile t)
        {
            if (t.State == TileState.Patched) return PatchCement;
            if (t.State == TileState.Reinforced) return Color.Lerp(SteelBand, t.Kind == TileKind.Wall ? Brick : Clay, 0.45f);
            switch (t.Kind)
            {
                case TileKind.Frame: return Frame;
                case TileKind.Floor:
                case TileKind.Stair: return t.State == TileState.Damaged ? ClayDamaged : Clay;
                case TileKind.Ramp: return ClayRamp;
                default: return t.State == TileState.Damaged ? BrickDamaged : Brick;
            }
        }

        // ---- the rim of a hole ----

        private void AddRims(MeshBits m, int id)
        {
            if (_nb == null) return;
            WallTile t = _tiles[id];
            if (!Basis(t, out Vector3 n, out Vector3 a1, out Vector3 a2)) return;
            Aabb b = _tiles.BoundsOf(id);
            Vector3 lo = b.Min.ToUnity(), hi = b.Max.ToUnity(), c = (lo + hi) * 0.5f, size = hi - lo;
            float e1 = Along(size, a1) * 0.5f, e2 = Along(size, a2) * 0.5f, th = Along(size, n) * 0.5f;
            var solid = new bool[8];
            var col = new Color[8];
            for (int k = 0; k < 8; k++)
            {
                int j = _nb[id * 8 + k];
                solid[k] = j >= 0 && _tiles[j].State != TileState.Broken;
                if (solid[k]) col[k] = SurfaceColour(_tiles[j]);
            }
            for (int k = 0; k < 4; k++)
            {
                if (!solid[k]) continue;
                Vector3 A = EdgeAxis[k] == 1 ? a1 : a2, B = EdgeAxis[k] == 1 ? a2 : a1;
                float eA = EdgeAxis[k] == 1 ? e1 : e2, eB = EdgeAxis[k] == 1 ? e2 : e1;
                RimEdge(m, c + A * (EdgeSign[k] * eA), B, eB, -A * EdgeSign[k], n, th, col[k]);
            }
            // Corners: k1 along a1 (0 or 1), k2 along a2 (2 or 3); diagonal index 4 + 2*(k1) + (k2 - 2).
            for (int k1 = 0; k1 < 2; k1++)
                for (int k2 = 2; k2 < 4; k2++)
                {
                    if (!solid[k1] || !solid[k2]) continue;
                    float s1 = EdgeSign[k1], s2 = EdgeSign[k2];
                    Vector3 K = c + a1 * (s1 * e1) + a2 * (s2 * e2);
                    Fillet(m, K, -a1 * s1, -a2 * s2, n, th, col[k1]);
                    int diag = 4 + 2 * k1 + (k2 - 2);
                    if (solid[diag]) CornerBand(m, K, a1 * s1, a2 * s2, n, th, col[diag]);
                }
        }

        /// <summary>
        /// Broken-brick teeth along one edge of a hole (into the hole, both faces cut differently so the break goes
        /// raggedly through the wall), and a scorched band on the solid tile's faces beside it.
        /// </summary>
        private void RimEdge(MeshBits m, Vector3 mid, Vector3 along, float half, Vector3 inward, Vector3 n, float th, Color col)
        {
            int segs = Mathf.Max(2, Mathf.RoundToInt(half * 2f / 0.1f));
            Color exposed = col * 0.66f, rubble = col * 0.46f, scorch = col * 0.82f;
            var p = new Vector3[segs + 1];
            var dF = new float[segs + 1];
            var dB = new float[segs + 1];
            var w = new float[segs + 1];
            for (int j = 0; j <= segs; j++)
            {
                p[j] = mid + along * (-half + 2f * half * j / segs);
                dF[j] = Mathf.Lerp(TeethMin, TeethMax, Hash(p[j], 1));
                dB[j] = Mathf.Lerp(TeethMin, TeethMax, Hash(p[j], 2));
                w[j] = BandWidth(p[j], along);
            }
            Vector3 outward = -inward;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 fn = n * side;
                Vector3 o = fn * th;
                float[] d = side > 0 ? dF : dB;
                for (int j = 0; j < segs; j++)
                {
                    // Teeth: flush with the face, continuing its brick.
                    _surface = BrickSurface;
                    QuadFacing(m, p[j] + o, p[j + 1] + o, p[j + 1] + o + inward * d[j + 1], p[j] + o + inward * d[j], exposed, fn);
                    // Scorched band on the solid tile, and a darker chipped strip right at the break.
                    Vector3 o1 = fn * (th + 0.005f), o2 = fn * (th + 0.0065f);
                    QuadFacing(m, p[j] + o1, p[j + 1] + o1, p[j + 1] + o1 + outward * w[j + 1], p[j] + o1 + outward * w[j], scorch, fn);
                    _surface = Rough;
                    QuadFacing(m, p[j] + o2, p[j + 1] + o2, p[j + 1] + o2 + outward * (w[j + 1] * 0.38f), p[j] + o2 + outward * (w[j] * 0.38f), exposed, fn);
                }
            }
            // The broken faces through the wall's thickness.
            _surface = Rough;
            for (int j = 0; j < segs; j++)
                QuadFacing(m, p[j] + n * th + inward * dF[j], p[j + 1] + n * th + inward * dF[j + 1],
                    p[j + 1] - n * th + inward * dB[j + 1], p[j] - n * th + inward * dB[j], rubble, inward);
        }

        /// <summary>Rubble filling the corner of a hole along a rough arc, so the hole's corners read round.</summary>
        private void Fillet(MeshBits m, Vector3 K, Vector3 dA, Vector3 dB, Vector3 n, float th, Color col)
        {
            const int Steps = 6;
            float r = Mathf.Lerp(FilletMin, FilletMax, Hash(K, 5));
            Vector3 O = K + (dA + dB) * r;
            var p = new Vector3[Steps + 1];
            for (int k = 0; k <= Steps; k++)
            {
                float a = k * Mathf.PI * 0.5f / Steps;
                float rr = r * (k == 0 || k == Steps ? 1f : 1f + (Hash(K, 10 + k) - 0.5f) * 0.22f);
                p[k] = O - (dB * Mathf.Cos(a) + dA * Mathf.Sin(a)) * rr;
            }
            Color exposed = col * 0.66f, rubble = col * 0.46f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 fn = n * side, o = fn * th;
                _surface = BrickSurface;
                for (int k = 0; k < Steps; k++) TriFacing(m, K + o, p[k] + o, p[k + 1] + o, exposed, fn);
            }
            _surface = Rough;
            for (int k = 0; k < Steps; k++)
            {
                Vector3 midArc = (p[k] + p[k + 1]) * 0.5f;
                QuadFacing(m, p[k] + n * th, p[k + 1] + n * th, p[k + 1] - n * th, p[k] - n * th, rubble, (O - midArc).normalized);
            }
        }

        /// <summary>The scorched band's rounded outer corner, on the tile diagonal to a hole's corner.</summary>
        private void CornerBand(MeshBits m, Vector3 K, Vector3 uA, Vector3 uB, Vector3 n, float th, Color col)
        {
            const int Steps = 6;
            float w = BandWidth(K, uA);
            Color scorch = col * 0.82f, exposed = col * 0.66f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 fn = n * side;
                Vector3 o1 = fn * (th + 0.005f), o2 = fn * (th + 0.0065f);
                for (int k = 0; k < Steps; k++)
                {
                    float a0 = k * Mathf.PI * 0.5f / Steps, a1 = (k + 1) * Mathf.PI * 0.5f / Steps;
                    Vector3 q0 = uA * Mathf.Cos(a0) + uB * Mathf.Sin(a0), q1 = uA * Mathf.Cos(a1) + uB * Mathf.Sin(a1);
                    _surface = BrickSurface;
                    TriFacing(m, K + o1, K + o1 + q0 * w, K + o1 + q1 * w, scorch, fn);
                    _surface = Rough;
                    TriFacing(m, K + o2, K + o2 + q0 * (w * 0.38f), K + o2 + q1 * (w * 0.38f), exposed, fn);
                }
            }
        }

        /// <summary>How far the scorched band reaches onto the wall at a point of a hole's edge (smooth along the edge).</summary>
        private static float BandWidth(Vector3 p, Vector3 along)
        {
            const float Step = 0.3f;
            float s = Vector3.Dot(p, along);
            float t = s / Step, i = Mathf.Floor(t), f = t - i;
            Vector3 basePt = p - along * s;
            float h0 = Hash(basePt + along * (i * Step), 3), h1 = Hash(basePt + along * ((i + 1) * Step), 3);
            float h = Mathf.Lerp(h0, h1, f * f * (3f - 2f * f));
            return Mathf.Lerp(BandMin, BandMax, h * 0.85f + Hash(p, 4) * 0.15f);
        }

        /// <summary>0..1 from a world position (5 cm lattice) and a salt: the same point always gives the same value.</summary>
        private static float Hash(Vector3 p, int salt)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(p.x * 20f) * 73856093u ^ (uint)Mathf.RoundToInt(p.y * 20f) * 19349663u
                         ^ (uint)Mathf.RoundToInt(p.z * 20f) * 83492791u ^ (uint)salt * 2654435761u;
                h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        // ---- decals on a tile's face ----

        /// <summary>An irregular flat blob facing n (spalls, craters).</summary>
        private void Blob(MeshBits m, Vector3 c, Vector3 n, float radius, float jitter, int verts, uint seed, Color col)
        {
            Basis2(n, out Vector3 r, out Vector3 u);
            float Rand()
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                return (seed & 0xFFFFFF) / (float)0x1000000;
            }
            Vector3 first = default, prev = default;
            float a0 = Rand() * Mathf.PI * 2f;
            _surface = Rough;
            for (int k = 0; k < verts; k++)
            {
                float a = a0 + k * Mathf.PI * 2f / verts;
                float rr = radius * (1f + (Rand() - 0.5f) * 2f * jitter);
                Vector3 q = c + (r * Mathf.Cos(a) + u * Mathf.Sin(a)) * rr;
                if (k == 0) first = q; else TriFacing(m, c, prev, q, col, n);
                prev = q;
            }
            TriFacing(m, c, prev, first, col, n);
        }

        /// <summary>A Patched tile: a pale cement patch, roughly oval and filling most of the tile, with a darker edge.</summary>
        private void PatchBlob(MeshBits m, int id, Vector3 fc, Vector3 fn, Vector3 up, float w, float h, Color baseCol)
        {
            const int Verts = 16;
            Vector3 r = Vector3.Cross(fn, up).normalized;
            uint seed = (uint)(id * 2246822519u + 0x165667B1u);
            float Rand()
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                return (seed & 0xFFFFFF) / (float)0x1000000;
            }
            var ring = new Vector2[Verts];
            for (int k = 0; k < Verts; k++)
            {
                float a = k * Mathf.PI * 2f / Verts;
                float j = 0.86f + Rand() * 0.12f;
                // A superellipse (squarish oval) so the patch covers the tile without a rectangle's straight edges.
                float cx = Mathf.Cos(a), sy = Mathf.Sin(a);
                float ex = Mathf.Sign(cx) * Mathf.Pow(Mathf.Abs(cx), 0.6f), ey = Mathf.Sign(sy) * Mathf.Pow(Mathf.Abs(sy), 0.6f);
                ring[k] = new Vector2(ex * (w - 0.06f) * j, ey * (h - 0.06f) * j);
            }
            _surface = Rough;
            Color edge = Color.Lerp(PatchCement, baseCol, 0.35f) * 0.8f;
            for (int k = 0; k < Verts; k++)
            {
                Vector2 a = ring[k], b = ring[(k + 1) % Verts];
                Vector3 pa = fc + r * a.x + up * a.y, pb = fc + r * b.x + up * b.y;
                TriFacing(m, fc, pa, pb, edge, fn);
                Vector3 qa = fc + (r * a.x + up * a.y) * 0.9f + fn * 0.0006f, qb = fc + (r * b.x + up * b.y) * 0.9f + fn * 0.0006f;
                TriFacing(m, fc + fn * 0.0006f, qa, qb, PatchCement, fn);
            }
        }

        /// <summary>Two unit vectors along a face with Cross(r, u) = n.</summary>
        internal static void Basis2(Vector3 n, out Vector3 r, out Vector3 u)
        {
            Vector3 u0 = Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up;
            r = Vector3.Cross(u0, n).normalized;
            u = Vector3.Cross(n, r);
        }

        /// <summary>A quad that faces <paramref name="want"/> whatever order its corners came in.</summary>
        private void QuadFacing(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector3 want)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, d - a), want) < 0) Quad(m, a, d, c, b, col);
            else Quad(m, a, b, c, d, col);
        }

        private void TriFacing(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 want)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0) Tri(m, a, c, b, col);
            else Tri(m, a, b, c, col);
        }

        // ---- bullets ----

        /// <summary>A bullet traced on this client hit brush <paramref name="brush"/>: chips, a puff and a lasting mark.</summary>
        private bool OnBullet(Vector3 at, Vector3 normal, int brush, float amount)
        {
            if (_brushWorld == null || brush < 0 || brush >= _brushWorld.Count) return false;
            Brush shape = _brushWorld[brush];
            int tile = _tiles != null ? _tiles.TileOfBrush(brush) : -1;
            Color col = tile >= 0 ? SurfaceColour(_tiles[tile]) : ColorOf(shape.Kind);
            if (_debris == null)
            {
                _debris = new GameObject("Tile Debris").AddComponent<TileDebris>();
                _debris.transform.SetParent(transform, false);
            }
            _debris.Chip(at, normal, col, amount);
            if (_marks != null) _marks.Add(at, normal, tile, shape.Bounds, !shape.IsRamp, col);
            return true;
        }
    }
}
