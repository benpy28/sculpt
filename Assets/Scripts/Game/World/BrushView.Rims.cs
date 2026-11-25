using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Wall-break visuals (Ben 2026-11-25: holes follow the hit and are never grid squares; bullets chip the wall where
    /// they hit; breaks blast out with rough edges and debris). The hole itself is the simulation's: each wall and floor
    /// tile is an 8 x 8 grid of 19 cm cells (Core TileHoles), and a break blows out a rough disc of them. This file only
    /// dresses its edges; it never changes collision, bullets or aiming.
    ///
    /// Rules that keep it fair: rough teeth reach at most <see cref="TeethMax"/> into an opening and corner rubble at
    /// most ~4 cm (radius <see cref="FilletMax"/>), against a smallest opening of 37.5 cm, so nothing drawn hides a player
    /// that bullets would reach. The scorched band lies on standing cells only (never wider than one cell), so it never
    /// floats over a hole. Frame (columns, beams) keeps clean steel edges.
    /// </summary>
    public sealed partial class BrushView
    {
        /// <summary>Vertex alpha read by SCULPT/Greybox: which surface a face is.</summary>
        private const float Greybox = 1f, Rough = 0.5f, BrickSurface = 0f;

        private const float TeethMin = 0.012f, TeethMax = 0.06f;
        private const float FilletMin = 0.05f, FilletMax = 0.09f;
        private const float BandMin = 0.05f, BandMax = 0.17f;

        private float _surface = Greybox;
        private BrushWorld _brushWorld;
        private WallMarks _marks;

        private ulong[] _openSeen;  // the open cells each tile was last drawn with
        private ulong[] _openFx;    // the open cells each tile last threw debris for

        private void OnEnable() => WallImpacts.OnBullet = OnBullet;

        private void OnDisable()
        {
            if (WallImpacts.OnBullet == OnBullet) WallImpacts.OnBullet = null;
        }

        private static Vector3 AxisVec(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;

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
            _openSeen = new ulong[_tiles.Count];
            _openFx = new ulong[_tiles.Count];
            for (int i = 0; i < _tiles.Count; i++)
            {
                _openSeen[i] = ulong.MaxValue - 1; // never a real mask of a drawn tile: forces the first draw
                _openFx[i] = _tiles.OpenCells(i);
            }
        }

        /// <summary>A hole's rim is drawn by its open cells from the cells around them: redraw the tiles around a change.</summary>
        private void DirtyNeighbours(int tile)
        {
            if (!_tiles.HasCells(tile)) return;
            for (int d = 0; d < 4; d++)
            {
                int j = _tiles.EdgeNeighbour(tile, d);
                if (j < 0) continue;
                _dirty.Add(BlockOf(_tiles.World[_tiles[j].Brush]));
                for (int e = 0; e < 4; e++) // diagonals: corner rubble and bands look across them
                {
                    int k = _tiles.EdgeNeighbour(j, e);
                    if (k >= 0) _dirty.Add(BlockOf(_tiles.World[_tiles[k].Brush]));
                }
            }
        }

        /// <summary>Debris for cells that just went: chunks and dust from where they were, in the tile's colour.</summary>
        private void CellDebris(int tile, ulong before, ulong after, TileState from)
        {
            ulong gone = after & ~before;
            if (gone == 0) return;
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int c = 0; c < Brush.CellCount; c++)
            {
                if (((gone >> c) & 1) == 0) continue;
                Aabb cb = _tiles.CellBox(tile, c);
                lo = Vector3.Min(lo, cb.Min.ToUnity());
                hi = Vector3.Max(hi, cb.Max.ToUnity());
            }
            int cells = TileWalls.PopCount(gone);
            WallTile t = _tiles[tile];
            var box = new Aabb(new Vec3d(lo.x, lo.y, lo.z), new Vec3d(hi.x, hi.y, hi.z));
            _debris.Burst(box, t.Facing, DebrisColour(t.Kind, from), Mathf.Clamp(cells / 3, 3, 18), cells >= 8);
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
                case TileKind.Door: return t.State == TileState.Damaged ? WoodDamaged : Wood;
                default: return t.State == TileState.Damaged ? BrickDamaged : Brick;
            }
        }

        /// <summary>Is the cell of a celled tile under this point open? (Decals skip open cells.)</summary>
        private bool OpenAt(int tile, Vector3 p)
        {
            ulong open = _tiles.OpenCells(tile);
            if (open == 0) return false;
            Brush b = _tiles.ShapeOf(tile);
            Vector3 lo = b.Bounds.Min.ToUnity(), hi = b.Bounds.Max.ToUnity();
            Vector3 a1 = AxisVec(b.CellA1), a2 = AxisVec(b.CellA2);
            float u = Vector3.Dot(p - lo, a1) / Vector3.Dot(hi - lo, a1), v = Vector3.Dot(p - lo, a2) / Vector3.Dot(hi - lo, a2);
            int i = Mathf.Clamp(Mathf.FloorToInt(u * Brush.CellsPerSide), 0, Brush.CellsPerSide - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt(v * Brush.CellsPerSide), 0, Brush.CellsPerSide - 1);
            return ((open >> (i + Brush.CellsPerSide * j)) & 1) != 0;
        }

        // ---- the rim of a hole, cell by cell ----

        private static readonly int[] DirAxis = { 1, 1, 2, 2 };
        private static readonly int[] DirSign = { 1, -1, 1, -1 };

        /// <summary>Is this neighbour cell standing wall or floor of the same kind (what gets a rim)?</summary>
        private bool StandingLike(int t2, int c2, TileKind kind)
        {
            if (t2 < 0) return false;
            WallTile o = _tiles[t2];
            if (o.Kind != kind || o.State == TileState.Broken) return false;
            return ((o.Open >> c2) & 1) == 0;
        }

        /// <summary>Rims for every open cell of a broken or holed wall or floor tile that borders standing cells.</summary>
        private void AddRims(MeshBits m, int id)
        {
            if (!_tiles.HasCells(id)) return;
            WallTile t = _tiles[id];
            Brush b = _tiles.ShapeOf(id);
            Vector3 a1 = AxisVec(b.CellA1), a2 = AxisVec(b.CellA2), n = AxisVec(3 - b.CellA1 - b.CellA2);
            float th = Along(b.Bounds.Max.ToUnity() - b.Bounds.Min.ToUnity(), n) * 0.5f;
            ulong open = _tiles.OpenCells(id);
            var solid = new bool[4];
            var col = new Color[4];
            var nbT = new int[4];
            var nbC = new int[4];
            for (int c = 0; c < Brush.CellCount; c++)
            {
                if (((open >> c) & 1) == 0) continue;
                Aabb cb = _tiles.CellBox(id, c);
                Vector3 lo = cb.Min.ToUnity(), hi = cb.Max.ToUnity(), cc = (lo + hi) * 0.5f, size = hi - lo;
                float e1 = Along(size, a1) * 0.5f, e2 = Along(size, a2) * 0.5f;
                bool any = false;
                for (int d = 0; d < 4; d++)
                {
                    nbT[d] = _tiles.CellNeighbour(id, c, d, out nbC[d]);
                    solid[d] = StandingLike(nbT[d], nbC[d], t.Kind);
                    if (solid[d]) { col[d] = SurfaceColour(_tiles[nbT[d]]); any = true; }
                }
                if (!any) continue;
                for (int d = 0; d < 4; d++)
                {
                    if (!solid[d]) continue;
                    Vector3 A = DirAxis[d] == 1 ? a1 : a2, B = DirAxis[d] == 1 ? a2 : a1;
                    float eA = DirAxis[d] == 1 ? e1 : e2, eB = DirAxis[d] == 1 ? e2 : e1;
                    RimEdge(m, cc + A * (DirSign[d] * eA), B, eB, -A * DirSign[d], n, th, col[d]);
                }
                for (int d1 = 0; d1 < 2; d1++)
                    for (int d2 = 2; d2 < 4; d2++)
                    {
                        if (!solid[d1] || !solid[d2]) continue;
                        float s1 = DirSign[d1], s2 = DirSign[d2];
                        Vector3 K = cc + a1 * (s1 * e1) + a2 * (s2 * e2);
                        Fillet(m, K, -a1 * s1, -a2 * s2, n, th, col[d1]);
                        // The diagonal cell, through the first neighbour.
                        int dt = _tiles.CellNeighbour(nbT[d1], nbC[d1], d2, out int dc);
                        if (StandingLike(dt, dc, t.Kind)) CornerBand(m, K, a1 * s1, a2 * s2, n, th, SurfaceColour(_tiles[dt]));
                    }
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
            Aabb face = shape.Bounds;
            if (tile >= 0 && _tiles[tile].Holed)
            {
                // Keep the mark on the cell it hit, so it never hangs over the hole beside it.
                Brush b = _tiles.ShapeOf(tile);
                Vector3 lo = b.Bounds.Min.ToUnity(), hi = b.Bounds.Max.ToUnity(), p = at - normal * 0.01f;
                Vector3 a1 = AxisVec(b.CellA1), a2 = AxisVec(b.CellA2);
                int i = Mathf.Clamp(Mathf.FloorToInt(Vector3.Dot(p - lo, a1) / Vector3.Dot(hi - lo, a1) * Brush.CellsPerSide), 0, Brush.CellsPerSide - 1);
                int j = Mathf.Clamp(Mathf.FloorToInt(Vector3.Dot(p - lo, a2) / Vector3.Dot(hi - lo, a2) * Brush.CellsPerSide), 0, Brush.CellsPerSide - 1);
                face = _tiles.CellBox(tile, i + Brush.CellsPerSide * j);
            }
            if (_marks != null) _marks.Add(at, normal, tile, face, !shape.IsRamp, col);
            return true;
        }
    }
}
