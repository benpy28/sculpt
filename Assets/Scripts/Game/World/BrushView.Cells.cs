using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Walls and floors drawn by cell (Ben 2026-11-25: "remove the grid", cracks by area). The shape is the simulation's
    /// exact one (<see cref="TileWalls.QuadOf"/>: quarter cells, hole edges cut on the diagonal), so what you see is what
    /// you hit (collision is within 2.4 cm of it). Damage, cement and steel are smooth fields over the wall: every vertex
    /// takes the cells around it, across tile borders, and SCULPT/Greybox draws cracks, patches and steel bands from them,
    /// so nothing ever follows the tile grid. Big flat undamaged areas merge into large quads.
    /// </summary>
    public sealed partial class BrushView
    {
        private const int Q = TileWalls.Quads; // quarter cells per tile side (16)
        private const int NC = Brush.CellsPerSide;

        private Vector2 _uv1, _uv2; // the fields Quad and Tri give their vertices (zero unless set)

        /// <summary>Most a hole's drawn edge moves off the exact shape (collision is within 2.4 cm of the exact shape too,
        /// so drawn and hit stay within about 4 cm, the agreed limit for visual bevels).</summary>
        private const float EdgeWobble = 0.016f;

        // Per-tile scratch: fields of the cells with a one-cell border from the neighbours (10 x 10), quad fills.
        private readonly Vector4[] _field = new Vector4[(NC + 2) * (NC + 2)];
        private readonly QuadFill[] _fills = new QuadFill[Q * Q];
        private readonly bool[] _used = new bool[Q * Q];

        /// <summary>One cell's fields: damage 0..1, patched, reinforced, open (the break edge).</summary>
        private Vector4 CellFields(int tile, int cell)
        {
            if (tile < 0 || _tiles[tile].State == TileState.Broken || _tiles.CellOpen(tile, cell)) return new Vector4(1, 0, 0, 1);
            WallTile t = _tiles[tile];
            ulong bit = 1ul << cell;
            float max = _tiles.CellMaxHealth(tile, cell);
            float dmg = Mathf.Clamp01(1f - _tiles.CellHealth(tile, cell) / Mathf.Max(1f, max));
            return new Vector4(dmg, (t.Patch & bit) != 0 ? 1 : 0, (t.Steel & bit) != 0 ? 1 : 0, 0);
        }

        /// <summary>The neighbour cell's fields, or the cell's own past the grid's edge.</summary>
        private Vector4 Across(int tile, int cell, int dir, Vector4 own, out int t2, out int c2)
        {
            t2 = _tiles.CellNeighbour(tile, cell, dir, out c2);
            if (t2 < 0 || !_tiles.HasCells(t2)) return own;
            return CellFields(t2, c2);
        }

        private void FillFields(int id)
        {
            const int W = NC + 2;
            for (int j = 0; j < NC; j++)
                for (int i = 0; i < NC; i++)
                    _field[(i + 1) + W * (j + 1)] = CellFields(id, i + NC * j);
            // Border from the neighbours (dir 0 +a1, 1 -a1, 2 +a2, 3 -a2), corners through two steps.
            for (int k = 0; k < NC; k++)
            {
                _field[(NC + 1) + W * (k + 1)] = Across(id, (NC - 1) + NC * k, 0, _field[NC + W * (k + 1)], out _, out _);
                _field[0 + W * (k + 1)] = Across(id, 0 + NC * k, 1, _field[1 + W * (k + 1)], out _, out _);
                _field[(k + 1) + W * (NC + 1)] = Across(id, k + NC * (NC - 1), 2, _field[(k + 1) + W * NC], out _, out _);
                _field[(k + 1) + W * 0] = Across(id, k, 3, _field[(k + 1) + W * 1], out _, out _);
            }
            for (int corner = 0; corner < 4; corner++)
            {
                int di = corner % 2 == 0 ? 0 : 1, dj = corner < 2 ? 2 : 3;
                int ci = di == 0 ? NC - 1 : 0, cj = dj == 2 ? NC - 1 : 0;
                int gi = di == 0 ? NC + 1 : 0, gj = dj == 2 ? NC + 1 : 0;
                Vector4 own = _field[(ci + 1) + W * (cj + 1)];
                Vector4 a = Across(id, ci + NC * cj, di, own, out int ta, out int ca);
                Vector4 v = a;
                if (ta >= 0 && _tiles.HasCells(ta)) v = Across(ta, ca, dj, a, out _, out _);
                else
                {
                    Vector4 b = Across(id, ci + NC * cj, dj, own, out int tb, out int cb);
                    v = tb >= 0 && _tiles.HasCells(tb) ? Across(tb, cb, di, b, out _, out _) : own;
                }
                _field[gi + W * gj] = v;
            }
        }

        /// <summary>The fields at a point of the face, in quarter cells (0..16 each way): bilinear between cell centres.</summary>
        private Vector4 FieldAt(float x, float y)
        {
            const int W = NC + 2;
            float fu = x * 0.5f - 0.5f, fv = y * 0.5f - 0.5f;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(fu), -1, NC - 1), j0 = Mathf.Clamp(Mathf.FloorToInt(fv), -1, NC - 1);
            float tu = Mathf.Clamp01(fu - i0), tv = Mathf.Clamp01(fv - j0);
            Vector4 a = _field[(i0 + 1) + W * (j0 + 1)], b = _field[(i0 + 2) + W * (j0 + 1)];
            Vector4 c = _field[(i0 + 1) + W * (j0 + 2)], d = _field[(i0 + 2) + W * (j0 + 2)];
            return Vector4.Lerp(Vector4.Lerp(a, b, tu), Vector4.Lerp(c, d, tu), tv);
        }

        private static bool Flat(Vector4 v) => v.sqrMagnitude < 1e-6f;

        /// <summary>0..1 from a world position (mm lattice, offset so lattice points never sit on a rounding edge, so two
        /// tiles sharing a vertex always agree) and a salt.</summary>
        private static float Hash(Vector3 p, int salt)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(p.x * 1000f + 0.37f) * 73856093u ^ (uint)Mathf.RoundToInt(p.y * 1000f + 0.37f) * 19349663u
                         ^ (uint)Mathf.RoundToInt(p.z * 1000f + 0.37f) * 83492791u ^ (uint)salt * 2654435761u;
                h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        /// <summary>A wall or floor tile, by cell: its two big faces, the sides of every hole edge, and its own edges where
        /// no tile of the same wall carries on.</summary>
        /// <summary>The tile and the tiles round it (edges and corners) never left their built state.</summary>
        private bool Untouched(int id)
        {
            if (!AsBuilt(id)) return false;
            for (int d = 0; d < 4; d++)
            {
                int j = _tiles.EdgeNeighbour(id, d);
                if (j < 0) continue;
                if (!AsBuilt(j)) return false;
                for (int e = 0; e < 4; e++)
                {
                    int k = _tiles.EdgeNeighbour(j, e);
                    if (k >= 0 && !AsBuilt(k)) return false;
                }
            }
            return true;
        }

        private bool AsBuilt(int id)
        {
            WallTile t = _tiles[id];
            return !t.Changed && t.State == TileState.Intact && t.Open == 0 && t.Patch == 0 && t.Steel == 0 && t.Health == _tiles.Config.IntactHealth;
        }

        private void AddCelledTile(MeshBits m, int id, in WallTile t)
        {
            if (t.State == TileState.Broken) return; // nothing left; the tiles round it draw the hole's sides
            Brush b = _tiles.ShapeOf(id);
            bool floor = t.Kind == TileKind.Floor;
            Color col = floor ? Clay : Brick;
            _surface = floor ? Rough : BrickSurface;
            Vector3 a1 = AxisVec(b.CellA1), a2 = AxisVec(b.CellA2), n = AxisVec(3 - b.CellA1 - b.CellA2);
            Vector3 lo = b.Bounds.Min.ToUnity(), hi = b.Bounds.Max.ToUnity();
            float s1 = Along(hi - lo, a1) / Q, s2 = Along(hi - lo, a2) / Q, th = Along(hi - lo, n);
            if (Untouched(id))
            {
                // As built, and so is everything round it: the plain box, without working out a single field (the
                // battle royale's first draw is about 200 thousand tiles).
                _uv1 = _uv2 = Vector2.zero;
                AddBox(m, lo, hi, col);
                _surface = Rough;
                return;
            }
            FillFields(id);
            bool plain = true;
            for (int k = 0; k < _field.Length && plain; k++) if (!Flat(_field[k])) plain = false;
            for (int qj = 0; qj < Q; qj++)
                for (int qi = 0; qi < Q; qi++)
                {
                    QuadFill f = _tiles.QuadOf(id, qi, qj);
                    _fills[qi + Q * qj] = f;
                    if (f != QuadFill.Full) plain = false;
                }
            if (plain)
            {
                // Untouched: the plain box (the common case, 6 quads).
                _uv1 = _uv2 = Vector2.zero;
                AddBox(m, lo, hi, col);
                _surface = Rough;
                return;
            }

            // Points near a break wobble a little in the face (at most EdgeWobble, fixed per spot), so hole edges read as
            // broken brick rather than ruled lines; flat wall never moves, so the mesh stays watertight.
            Vector3 P(float x, float y, float side)
            {
                Vector3 p = lo + a1 * (x * s1) + a2 * (y * s2);
                float e = Mathf.Clamp01(FieldAt(x, y).w * 2f);
                if (e > 0f)
                {
                    float ang = Hash(p, 21) * Mathf.PI * 2f, r = EdgeWobble * e * (0.35f + 0.65f * Hash(p, 22));
                    p += (a1 * Mathf.Cos(ang) + a2 * Mathf.Sin(ang)) * r;
                }
                return p + n * (side > 0 ? th : 0f);
            }

            // The two big faces.
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 fn = n * side;
                System.Array.Clear(_used, 0, _used.Length);
                for (int qj = 0; qj < Q; qj++)
                    for (int qi = 0; qi < Q; qi++)
                    {
                        int k = qi + Q * qj;
                        if (_used[k]) continue;
                        QuadFill f = _fills[k];
                        if (f == QuadFill.Empty) continue;
                        if (f == QuadFill.Full && FlatQuad(qi, qj))
                        {
                            // Greedy rectangle of flat (undamaged, plain) full quarter cells.
                            int w = 1;
                            while (qi + w < Q && !_used[k + w] && _fills[k + w] == QuadFill.Full && FlatQuad(qi + w, qj)) w++;
                            int h = 1;
                            for (bool grow = true; grow && qj + h < Q; )
                            {
                                for (int x = 0; x < w; x++)
                                {
                                    int kk = qi + x + Q * (qj + h);
                                    if (_used[kk] || _fills[kk] != QuadFill.Full || !FlatQuad(qi + x, qj + h)) { grow = false; break; }
                                }
                                if (grow) h++;
                            }
                            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) _used[k + x + Q * y] = true;
                            Face4(m, P(qi, qj, side), P(qi + w, qj, side), P(qi + w, qj + h, side), P(qi, qj + h, side),
                                Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, col, fn);
                            continue;
                        }
                        _used[k] = true;
                        if (f == QuadFill.Full)
                        {
                            Face4(m, P(qi, qj, side), P(qi + 1, qj, side), P(qi + 1, qj + 1, side), P(qi, qj + 1, side),
                                FieldAt(qi, qj), FieldAt(qi + 1, qj), FieldAt(qi + 1, qj + 1), FieldAt(qi, qj + 1), col, fn);
                            continue;
                        }
                        // A cut corner: the solid triangle, in the quad's own corner frame (u, v) = (0, 0) at the cell centre.
                        Local(qi, qj, 0, 0, out float x0, out float y0);
                        Local(qi, qj, 1, 0, out float x1, out float y1);
                        Local(qi, qj, 0, 1, out float x2, out float y2);
                        Local(qi, qj, 1, 1, out float x3, out float y3);
                        if (f == QuadFill.Inner)
                            Face3(m, P(x0, y0, side), P(x1, y1, side), P(x2, y2, side), FieldAt(x0, y0), FieldAt(x1, y1), FieldAt(x2, y2), col, fn);
                        else
                            Face3(m, P(x1, y1, side), P(x3, y3, side), P(x2, y2, side), FieldAt(x1, y1), FieldAt(x3, y3), FieldAt(x2, y2), col, fn);
                    }
            }

            // Sides: every edge between solid and open, through the wall's thickness, in broken-brick colour.
            Color rubble = SurfaceColour(t) * 0.55f;
            _surface = Rough;
            var broken = new Vector4(1, 0, 0, 1);
            for (int qj = 0; qj < Q; qj++)
                for (int qi = 0; qi < Q; qi++)
                {
                    QuadFill f = _fills[qi + Q * qj];
                    if (f == QuadFill.Empty) continue;
                    Local(qi, qj, 1, 0, out float x1, out float y1);
                    Local(qi, qj, 0, 1, out float x2, out float y2);
                    if (f == QuadFill.Inner || f == QuadFill.Outer)
                    {
                        Local(qi, qj, f == QuadFill.Inner ? 1 : 0, f == QuadFill.Inner ? 1 : 0, out float ox, out float oy);
                        Local(qi, qj, 0.5f, 0.5f, out float mx, out float my);
                        Vector3 outward = (P(ox, oy, 0) - P(mx, my, 0)).normalized;
                        Side(m, P(x1, y1, -1), P(x2, y2, -1), P(x2, y2, 1), P(x1, y1, 1), broken, rubble, outward);
                    }
                    // The four straight sides of the quarter cell (x0 = qi, x1 = qi + 1, y0 = qj, y1 = qj + 1).
                    for (int s = 0; s < 4; s++)
                    {
                        if (!Covers(f, qi, qj, s)) continue;
                        if (NeighbourCovers(id, qi, qj, s)) continue;
                        float ax, bx, ay, by;
                        if (s < 2) { ax = bx = s == 0 ? qi + 1 : qi; ay = qj; by = qj + 1; }
                        else { ay = by = s == 2 ? qj + 1 : qj; ax = qi; bx = qi + 1; }
                        Vector3 outward = s == 0 ? a1 : s == 1 ? -a1 : s == 2 ? a2 : -a2;
                        bool tileEdge = (s == 0 && qi == Q - 1) || (s == 1 && qi == 0) || (s == 2 && qj == Q - 1) || (s == 3 && qj == 0);
                        bool outer = tileEdge && _tiles.EdgeNeighbour(id, s) < 0; // the wall's own edge (a doorway side)
                        if (outer) Face4(m, P(ax, ay, -1), P(bx, by, -1), P(bx, by, 1), P(ax, ay, 1), FieldAt(ax, ay), FieldAt(bx, by), FieldAt(bx, by), FieldAt(ax, ay), SideColour(t), outward);
                        else Side(m, P(ax, ay, -1), P(bx, by, -1), P(bx, by, 1), P(ax, ay, 1), broken, rubble, outward);
                    }
                }
            _uv1 = _uv2 = Vector2.zero;
        }

        /// <summary>The colour of a tile's outer edge (a doorway's side): the wall, a little shaded.</summary>
        private Color SideColour(in WallTile t) => (t.Kind == TileKind.Floor ? Clay : Brick) * 0.92f;

        private bool FlatQuad(int qi, int qj) =>
            Flat(FieldAt(qi, qj)) && Flat(FieldAt(qi + 1, qj)) && Flat(FieldAt(qi, qj + 1)) && Flat(FieldAt(qi + 1, qj + 1));

        /// <summary>Quad-corner frame to face coordinates: (u, v) = (0, 0) at the cell centre, (1, 1) at the cell corner.</summary>
        private static void Local(int qi, int qj, float u, float v, out float x, out float y)
        {
            x = (qi & 1) == 1 ? qi + u : qi + 1 - u;
            y = (qj & 1) == 1 ? qj + v : qj + 1 - v;
        }

        /// <summary>Does a quarter cell's fill reach its side s (0 +a1, 1 -a1, 2 +a2, 3 -a2) all along it?</summary>
        private static bool Covers(QuadFill f, int qi, int qj, int s)
        {
            if (f == QuadFill.Full) return true;
            if (f == QuadFill.Empty) return false;
            // The side at u = 0 (toward the cell centre) is x = qi when the corner is at +a1 (odd qi), else x = qi + 1.
            bool uSide = s < 2;
            bool atPositive = s == 0 || s == 2;
            bool odd = ((uSide ? qi : qj) & 1) == 1;
            bool centreSide = odd ? !atPositive : atPositive; // that side is the u (or v) = 0 one
            return f == QuadFill.Inner ? centreSide : !centreSide;
        }

        /// <summary>Does the quarter cell across side s cover its side facing back (so no face shows between them)?</summary>
        private bool NeighbourCovers(int id, int qi, int qj, int s)
        {
            int ni = qi + (s == 0 ? 1 : s == 1 ? -1 : 0), nj = qj + (s == 2 ? 1 : s == 3 ? -1 : 0);
            int back = s ^ 1;
            if (ni >= 0 && ni < Q && nj >= 0 && nj < Q) return Covers(_fills[ni + Q * nj], ni, nj, back);
            int other = _tiles.EdgeNeighbour(id, s);
            if (other < 0)
            {
                // Past the wall's grid: something solid there (frame, the ground) hides the side; open air shows it.
                int beyond = _tiles.CellNeighbour(id, (qi >> 1) + NC * (qj >> 1), s, out _);
                return beyond == -1;
            }
            if (_tiles[other].State == TileState.Broken) return false;
            ni = (ni + Q) % Q;
            nj = (nj + Q) % Q;
            return Covers(_tiles.QuadOf(other, ni, nj), ni, nj, back);
        }

        // ---- emitting with fields ----

        private void Face4(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 fa, Vector4 fb, Vector4 fc, Vector4 fd, Color col, Vector3 want)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, d - a), want) < 0) { Emit4(m, a, d, c, b, fa, fd, fc, fb, col, want); return; }
            Emit4(m, a, b, c, d, fa, fb, fc, fd, col, want);
        }

        private void Face3(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector4 fa, Vector4 fb, Vector4 fc, Color col, Vector3 want)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0) { Emit3(m, a, c, b, fa, fc, fb, col, want); return; }
            Emit3(m, a, b, c, fa, fb, fc, col, want);
        }

        private void Side(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 f, Color col, Vector3 want) =>
            Face4(m, a, b, c, d, f, f, f, f, col, want);

        private Color Shaded(Color col, Vector3 n)
        {
            Color s = col * Shade(n);
            s.a = _surface;
            return _linear ? s.linear : s;
        }

        private void Emit4(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 fa, Vector4 fb, Vector4 fc, Vector4 fd, Color col, Vector3 n)
        {
            Color s = Shaded(col, n);
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            for (int k = 0; k < 4; k++) { m.N.Add(n); m.C.Add(s); }
            AddF(m, fa); AddF(m, fb); AddF(m, fc); AddF(m, fd);
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
            m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
        }

        private void Emit3(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector4 fa, Vector4 fb, Vector4 fc, Color col, Vector3 n)
        {
            Color s = Shaded(col, n);
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c);
            for (int k = 0; k < 3; k++) { m.N.Add(n); m.C.Add(s); }
            AddF(m, fa); AddF(m, fb); AddF(m, fc);
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
        }

        private static void AddF(MeshBits m, Vector4 f)
        {
            m.U1.Add(new Vector2(f.x, f.y));
            m.U2.Add(new Vector2(f.z, f.w));
        }
    }
}
