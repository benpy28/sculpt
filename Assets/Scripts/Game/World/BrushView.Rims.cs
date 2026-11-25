using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Wall-break effects (Ben 2026-11-25): bullets chip the wall where they hit and leave marks; breaks throw debris and
    /// dust from exactly the cells that went. The holes themselves are drawn in BrushView.Cells from the simulation's
    /// exact shape; nothing here changes collision, bullets or aiming. Frame (columns, beams) keeps clean steel edges.
    /// </summary>
    public sealed partial class BrushView
    {
        /// <summary>Vertex alpha read by SCULPT/Greybox: which surface a face is.</summary>
        private const float Greybox = 1f, Rough = 0.5f, BrickSurface = 0f;

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

        /// <summary>A tile's cracks and hole edges depend on the cells around it: redraw the tiles around a change.</summary>
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
