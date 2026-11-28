using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Draws the building brushes (greybox, Ben 2026-11-21) from code: one mesh per 16 m block of the map, built once.
    /// Colour-coded so unbreakable parts read at a glance: floors warm concrete, frame (columns, beams) dark blue
    /// steel, stairs concrete with a yellow nose on every step, ramps lighter concrete with yellow edges, plain walls
    /// slate. Never purple (reserved for sculptable). Stairs are drawn as steps but collide as a smooth ramp: each
    /// step's tread is centred on the slope, so the drawn steps stay within half a step (7.5 cm) of what you touch.
    /// Breakable wall tiles (step 2) are drawn from the local client's copy of their state in their own per-block meshes,
    /// rebuilt only when a tile in that block changes look: terracotta brick when Intact, cracked below half health,
    /// a pale cement patch inside a brick border when Patched, bright steel bands when Reinforced, nothing when Broken.
    /// Step 8: the block's floors, stairs and roof ramp are breakable tiles too, in warm clay (terracotta, lighter than
    /// the wall brick, so breakable still reads apart from the unbreakable greybox) with the same state looks on their
    /// top and bottom faces; stairs keep their yellow noses. Frame tiles stay dark steel: they only ever go together
    /// with the tiles around them.
    /// Wall-break visuals (BrushView.Rims.cs): breakable tiles have no seams and wear world-space brick, so a wall reads as
    /// one wall; bullets leave chips; holes get rough rims and rounded corners (all visual, see the rules there).
    /// </summary>
    public sealed partial class BrushView : MonoBehaviour
    {
        private const float BlockSize = 16f;
        /// <summary>Breakable tiles are drawn in smaller blocks than the static brushes, so a change rebuilds less.</summary>
        private const float TileBlockSize = 8f;
        /// <summary>Tile blocks this close to the camera are rebuilt on the frame they change; farther ones share
        /// <see cref="RebuildBudgetMs"/> a frame, nearest first (a far wall's new look may wait a few frames).</summary>
        private const float RebuildAlwaysMetres = 30f;
        private const double RebuildBudgetMs = 1.5;
        private const double StepRise = 0.15;

        public static readonly Color Floor = new Color(0.66f, 0.62f, 0.56f);
        public static readonly Color Frame = new Color(0.27f, 0.33f, 0.42f);
        public static readonly Color Stairs = new Color(0.60f, 0.58f, 0.55f);
        public static readonly Color StairNose = new Color(0.95f, 0.76f, 0.18f);
        public static readonly Color Ramp = new Color(0.72f, 0.70f, 0.66f);
        public static readonly Color Wall = new Color(0.40f, 0.43f, 0.46f);
        public static readonly Color Ground = new Color(0.46f, 0.52f, 0.38f);
        public static readonly Color Marker = new Color(0.20f, 0.85f, 0.45f);
        public static readonly Color Brick = new Color(0.72f, 0.42f, 0.30f);
        // Only a little darker than Brick: cracks and spalls carry the damaged look, a big colour step would draw the grid.
        public static readonly Color BrickDamaged = new Color(0.66f, 0.385f, 0.275f);
        public static readonly Color PatchCement = new Color(0.80f, 0.78f, 0.72f);
        public static readonly Color SteelBand = new Color(0.80f, 0.84f, 0.87f);
        public static readonly Color Crack = new Color(0.10f, 0.08f, 0.07f);
        /// <summary>Breakable floors, stairs and ramps (step 8).</summary>
        public static readonly Color Clay = new Color(0.78f, 0.56f, 0.42f);
        public static readonly Color ClayDamaged = new Color(0.71f, 0.51f, 0.38f);
        public static readonly Color ClayRamp = new Color(0.84f, 0.64f, 0.50f);
        /// <summary>Door leaves: dark timber, so a weak way in reads at a glance against brick.</summary>
        public static readonly Color Wood = new Color(0.42f, 0.29f, 0.17f);
        public static readonly Color WoodDamaged = new Color(0.33f, 0.22f, 0.13f);

        /// <summary>The colour debris from a tile in this state takes.</summary>
        public static Color DebrisColour(TileState s) => s == TileState.Patched ? PatchCement : s == TileState.Reinforced ? SteelBand : Brick;

        public static Color DebrisColour(TileKind k, TileState s)
        {
            if (s == TileState.Patched || s == TileState.Reinforced) return DebrisColour(s);
            switch (k)
            {
                case TileKind.Frame: return Frame;
                case TileKind.Floor:
                case TileKind.Stair: return Clay;
                case TileKind.Ramp: return ClayRamp;
                case TileKind.Door: return Wood;
                default: return Brick;
            }
        }

        private readonly List<GameObject> _blocks = new List<GameObject>();
        private Material _mat;
        private Material _groundMat;
        private bool _linear;
        /// <summary>While building hills: where their yellow edge lines go (the greybox mesh, not the meadow one).</summary>
        private MeshBits _stripeSink;

        // Tiles: the client's copy, which block each tile is drawn in, and the look each block was last built with.
        private TileWalls _tiles;
        private int _tilesRev = -1;
        private int[] _look;
        private readonly Dictionary<Vector2Int, List<int>> _tilesByBlock = new Dictionary<Vector2Int, List<int>>();
        private readonly Dictionary<Vector2Int, Mesh> _tileMeshes = new Dictionary<Vector2Int, Mesh>();
        private readonly HashSet<Vector2Int> _dirty = new HashSet<Vector2Int>();
        private TileDebris _debris;

        private static Vector2Int TileBlockOf(in Brush b)
        {
            Vector3 c = ((b.Bounds.Min + b.Bounds.Max) * 0.5).ToUnity();
            return new Vector2Int(Mathf.FloorToInt(c.x / TileBlockSize), Mathf.FloorToInt(c.z / TileBlockSize));
        }

        // Tiles drawn without cells (frame, stairs, ramps, doors): the only ones whose look is found by scanning.
        private int[] _plainTiles;
        private bool _firstScan;
        private readonly MeshBits _scratch = new MeshBits { Cells = true };
        private readonly List<Vector2Int> _order = new List<Vector2Int>();
        private Vector3 _camAt;
        private System.Comparison<Vector2Int> _nearest;

        private float BlockDistance(Vector2Int k)
        {
            float x = (k.x + 0.5f) * TileBlockSize - _camAt.x, z = (k.y + 0.5f) * TileBlockSize - _camAt.z;
            return x * x + z * z;
        }

        private static Vector2Int BlockOf(in Brush b)
        {
            Vector3 c = ((b.Bounds.Min + b.Bounds.Max) * 0.5).ToUnity();
            return new Vector2Int(Mathf.FloorToInt(c.x / BlockSize), Mathf.FloorToInt(c.z / BlockSize));
        }

        /// <summary>
        /// Draw the tiles of this client copy (call again when the session makes a new client). Tile changes the
        /// server sent become debris and dust here; this view drains the copy's change list.
        /// </summary>
        public void BindTiles(TileWalls tiles)
        {
            _tiles = tiles;
            _tilesRev = -1;
            foreach (Mesh m in _tileMeshes.Values) m.Clear();
            _tilesByBlock.Clear();
            _dirty.Clear();
            if (tiles == null) return;
            tiles.Changes.Clear();
            _look = new int[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                Vector2Int key = TileBlockOf(tiles.World[tiles[i].Brush]);
                if (!_tilesByBlock.TryGetValue(key, out var list)) _tilesByBlock[key] = list = new List<int>();
                list.Add(i);
                _look[i] = -1;
            }
            var plain = new List<int>();
            for (int i = 0; i < tiles.Count; i++) if (!tiles.HasCells(i)) plain.Add(i);
            _plainTiles = plain.ToArray();
            _firstScan = true;
            if (_debris == null) _debris = new GameObject("Tile Debris").AddComponent<TileDebris>();
            _debris.transform.SetParent(transform, false);
            BindRims();
        }

        /// <summary>What a tile looks like, as a number: state, plus a crack level from its health.</summary>
        private int LookOf(in WallTile t)
        {
            int crack = 0;
            int max = _tiles.Config.MaxHealthOf(t.State);
            if (t.State != TileState.Broken && max > 0)
            {
                if (t.State == TileState.Damaged || t.Health * 2 < max) crack = 1;
                if (t.Health * 4 < max) crack = 2;
            }
            return (int)t.State * 4 + crack;
        }

        private void LateUpdate()
        {
            if (_tiles == null || _mat == null) return;
            foreach (TileChange c in _tiles.Changes)
            {
                ulong before = _openFx[c.Tile];
                ulong after = c.To == TileState.Broken ? Brush.AllCells : c.Open;
                _openFx[c.Tile] = after;
                // Bullet marks go when the tile breaks, loses cells or is healed (a join snapshot counts too).
                if (c.To == TileState.Broken || (after & ~before) != 0 || c.Cause == TileCause.Heal || c.Cause == TileCause.Reinforce)
                    _marks?.ClearTile(c.Tile);
                // Cells drawn round this tile changed (cracks spread by area, so its neighbours redraw too).
                if (_tiles.HasCells(c.Tile)) { _dirty.Add(TileBlockOf(_tiles.World[_tiles[c.Tile].Brush])); DirtyNeighbours(c.Tile); }
                else LookAgain(c.Tile); // stairs, frame, doors: their look changes only with a change of their own
                // Sound finds its way through what is open now (a new hole, a healed wall, a door).
                Audio.SoundRouter.Invalidate(_tiles.BoundsOf(c.Tile));
                if (c.Cause == TileCause.Server || c.Cause == TileCause.Built) continue;
                // A wall giving way is heard (where, by the way it reaches you): whole tiles, or cells blown out.
                {
                    int lost = c.To == TileState.Broken && c.From != TileState.Broken ? Brush.CellCount : PopCount(after & ~before);
                    if (lost > 0 && c.Cause != TileCause.Door) PlayBreak(_tiles.BoundsOf(c.Tile), lost, _tiles[c.Tile].Kind == TileKind.Door);
                }
                WallTile ct = _tiles[c.Tile];
                if (c.Cause == TileCause.Door)
                {
                    // One sound per door (the two leaves change together).
                    int partner = _tiles.DoorPartner(c.Tile);
                    if (partner < 0 || c.Tile < partner) PlayDoor(_tiles.BoundsOf(c.Tile), c.To == TileState.DoorOpen);
                    continue;
                }
                if (_tiles.HasCells(c.Tile))
                {
                    // Blast holes: chunks and dust from exactly the cells that went.
                    CellDebris(c.Tile, before, after, c.From);
                    continue;
                }
                Aabb b = _tiles.BoundsOf(c.Tile);
                Dir facing = ct.Facing;
                bool thin = ct.Kind == TileKind.Frame; // frame pieces are small: fewer chunks
                if (c.To == TileState.Broken) _debris.Burst(b, facing, DebrisColour(ct.Kind, c.From), thin ? 4 : 16, !thin);
                else if (c.To == TileState.Damaged && c.From == TileState.Intact)
                    _debris.Burst(b, facing, ct.Kind == TileKind.Wall ? BrickDamaged : ct.Kind == TileKind.Door ? WoodDamaged : ClayDamaged, 5, false);
            }
            _tiles.Changes.Clear();
            if (_tiles.Revision == _tilesRev && _dirty.Count == 0) return;
            if (_tiles.Revision != _tilesRev)
            {
                _tilesRev = _tiles.Revision;
                // Everything redraws from its changes (above); only the first pass scans every tile (the village map
                // has about 200 thousand: scanning the plain ones every frame something changed cost milliseconds).
                if (_firstScan)
                    for (int i = 0; i < _tiles.Count; i++) LookAgain(i);
            }
            bool all = _firstScan;
            _firstScan = false;
            RebuildDirty(all);
        }

        /// <summary>
        /// Rebuilds changed tile blocks, nearest the camera first: everything within <see cref="RebuildAlwaysMetres"/>
        /// at once, the rest while this frame's budget lasts (all of it on the first build).
        /// </summary>
        /// <summary>Works out a tile's look again; if it changed, its block (and the blocks round it) redraw.</summary>
        private void LookAgain(int i)
        {
            if (_tiles.HasCells(i) && _look[i] != -1) return;
            int look = LookOf(_tiles[i]);
            ulong open = _tiles[i].Open;
            if (look == _look[i] && open == _openSeen[i]) return;
            _look[i] = look;
            _openSeen[i] = open;
            _dirty.Add(TileBlockOf(_tiles.World[_tiles[i].Brush]));
            DirtyNeighbours(i); // a hole's rim is drawn by the broken tile and depends on the tiles around it
        }

        private void RebuildDirty(bool all)
        {
            if (_dirty.Count == 0) return;
            Camera cam = Camera.main;
            _camAt = cam != null ? cam.transform.position : Vector3.zero;
            _order.Clear();
            foreach (Vector2Int key in _dirty) _order.Add(key);
            if (!all)
            {
                _nearest ??= (a, b) => BlockDistance(a).CompareTo(BlockDistance(b));
                _order.Sort(_nearest);
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            double budgetTicks = RebuildBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            float always = RebuildAlwaysMetres * RebuildAlwaysMetres;
            foreach (Vector2Int key in _order)
            {
                if (!all && BlockDistance(key) > always && System.Diagnostics.Stopwatch.GetTimestamp() - start > budgetTicks) break;
                RebuildTiles(key);
                _dirty.Remove(key);
            }
        }

        private void RebuildTiles(Vector2Int key)
        {
            if (!_tileMeshes.TryGetValue(key, out Mesh mesh))
            {
                mesh = new Mesh { name = $"Tiles {key}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.MarkDynamic();
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on (players never do)
                r.receiveShadows = true;
                _tileMeshes[key] = mesh;
                _blocks.Add(go);
            }
            MeshBits m = _scratch;
            m.Clear();
            if (_tilesByBlock.TryGetValue(key, out var ids))
                foreach (int id in ids) AddTile(m, id, _tiles[id]);
            mesh.Clear();
            mesh.SetVertices(m.V);
            mesh.SetNormals(m.N);
            mesh.SetColors(m.C);
            mesh.SetUVs(1, m.U1);
            mesh.SetUVs(2, m.U2);
            mesh.SetTriangles(m.T, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// An open door leaf has slid into the frame on its hinge side: drawn as a 12 cm strip of timber there, inside
        /// the leaf's own box (it has no collision, so it must never be wide enough to hide anyone).
        /// </summary>
        private void AddOpenLeaf(MeshBits m, int id, Aabb b)
        {
            const double strip = 0.12;
            bool alongX = b.Max.X - b.Min.X > b.Max.Z - b.Min.Z;
            int partner = _tiles.DoorPartner(id);
            Vec3d c = (b.Min + b.Max) * 0.5;
            bool hingeLow = true; // the side away from the other leaf
            if (partner >= 0)
            {
                Vec3d pc = (_tiles.BoundsOf(partner).Min + _tiles.BoundsOf(partner).Max) * 0.5;
                hingeLow = alongX ? pc.X > c.X : pc.Z > c.Z;
            }
            Vec3d lo = b.Min, hi = b.Max;
            if (alongX) { if (hingeLow) hi = new Vec3d(lo.X + strip, hi.Y, hi.Z); else lo = new Vec3d(hi.X - strip, lo.Y, lo.Z); }
            else { if (hingeLow) hi = new Vec3d(hi.X, hi.Y, lo.Z + strip); else lo = new Vec3d(lo.X, lo.Y, hi.Z - strip); }
            _surface = Rough;
            AddBox(m, lo.ToUnity(), hi.ToUnity(), Wood);
        }

        private static AudioClip _break;
        private static readonly List<Vector4> _recentBreaks = new List<Vector4>();

        private static int PopCount(ulong v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }

        /// <summary>
        /// A wall giving way: a hard crack, then brick breaking up and rubble spilling (doors: higher, a splintering
        /// crack). Generated, nothing licensed. A blast that breaks a dozen tiles plays one or two, not a dozen: breaks
        /// within 2.5 m and 0.1 s of one already playing are folded into it. Heard up to 60 m, routed like every sound.
        /// </summary>
        private static void PlayBreak(Aabb b, int cellsLost, bool door)
        {
            if (_break == null)
            {
                float phase = 0, lp = 0;
                _break = Audio.Synth.Make("Wall Break", 0.8f, false, (t, n) =>
                {
                    float f = 70f + 90f * Mathf.Exp(-t * 20f);
                    phase += 2 * Mathf.PI * f / Audio.Synth.Rate;
                    float body = Mathf.Sin(phase) * Mathf.Exp(-t * 14f) * 0.6f;
                    float crack = Audio.Synth.Noise(n, 0x6b1u) * Mathf.Exp(-t * 45f) * 0.8f;
                    lp += (Audio.Synth.Noise(n, 0x6b3u) - lp) * 0.14f;
                    float gate = Audio.Synth.Noise(n / 700, 0x6b5u) > 0.15f ? 1f : 0.2f;
                    float rubble = lp * 2.4f * gate * Mathf.Exp(-t * 4.5f) * Mathf.Min(1f, t * 30f);
                    return Audio.Synth.Soft(body + crack + rubble);
                });
            }
            Vector3 at = ((b.Min + b.Max) * 0.5).ToUnity();
            float now = Time.unscaledTime;
            for (int i = _recentBreaks.Count - 1; i >= 0; i--)
            {
                Vector4 r = _recentBreaks[i];
                if (now - r.w > 0.1f) { _recentBreaks.RemoveAt(i); continue; }
                if ((new Vector3(r.x, r.y, r.z) - at).sqrMagnitude < 2.5f * 2.5f) return;
            }
            _recentBreaks.Add(new Vector4(at.x, at.y, at.z, now));
            float vol = 0.45f + 0.5f * Mathf.Clamp01(cellsLost / 24f);
            float pitch = (door ? 1.35f : 1f) * Random.Range(0.92f, 1.06f);
            Audio.Synth.Play(_break, at, vol, pitch, true, 3f, 60f, sourceRadius: 0.8f, bus: Audio.SoundBus.World, caption: Audio.SoundCaption.WallBreaking);
        }

        private static AudioClip _doorOpen, _doorShut;

        /// <summary>A wooden slide and a clack (open), or a slide and a heavier knock (shut). Generated, nothing licensed.</summary>
        private static void PlayDoor(Aabb b, bool open)
        {
            if (_doorOpen == null)
            {
                AudioClip Make(string name, float knockAt, float knockGain, float pitch) => Audio.Synth.Make(name, 0.32f, false, (t, i) =>
                {
                    float slide = t < knockAt ? Audio.Synth.Noise(i, 0x51u) * 0.18f * Mathf.Sin(Mathf.PI * t / knockAt) : 0f;
                    float k = t - knockAt;
                    float knock = k >= 0 ? Mathf.Exp(-k * 38f) * (Mathf.Sin(2 * Mathf.PI * 140f * pitch * k) * 0.8f + Audio.Synth.Noise(i, 0x77u) * 0.35f) * knockGain : 0f;
                    return slide + knock;
                });
                _doorOpen = Make("Door Open", 0.16f, 0.5f, 1.25f);
                _doorShut = Make("Door Shut", 0.13f, 0.9f, 0.9f);
            }
            Vec3d c = (b.Min + b.Max) * 0.5;
            // The door's middle is inside the door: heard straight from both sides (routed, like every sound).
            Audio.Synth.Play(open ? _doorOpen : _doorShut, c.ToUnity(), 0.8f, 1f, true, 2f, 35f, sourceRadius: 0.4f,
                             caption: open ? Audio.SoundCaption.DoorOpening : Audio.SoundCaption.DoorClosing);
        }

        private void AddTile(MeshBits m, int id, in WallTile t)
        {
            if (_tiles.HasCells(id)) { AddCelledTile(m, id, t); return; } // walls and floors: by cell (BrushView.Cells)
            if (t.State == TileState.Broken) return;
            Brush shape = _tiles.World[t.Brush];
            if (t.State == TileState.DoorOpen) { AddOpenLeaf(m, id, shape.Bounds); return; }
            bool damaged = t.State == TileState.Damaged;
            _surface = Rough;
            switch (t.Kind)
            {
                case TileKind.Frame:
                    AddBox(m, shape.Bounds.Min.ToUnity(), shape.Bounds.Max.ToUnity(), damaged ? Frame * 0.8f : Frame);
                    return;
                case TileKind.Stair:
                case TileKind.Ramp:
                    // The look of the state is the segment's colour (its faces are slopes and steps, not panels).
                    Color c = t.State == TileState.Patched ? PatchCement
                        : t.State == TileState.Reinforced ? Color.Lerp(SteelBand, Clay, 0.35f)
                        : damaged || (_look[id] & 3) > 0 ? ClayDamaged
                        : t.Kind == TileKind.Ramp ? ClayRamp : Clay;
                    if (t.Kind == TileKind.Stair) AddStairs(m, shape, c); else AddWedge(m, shape, c);
                    return;
                case TileKind.Door:
                    AddBox(m, shape.Bounds.Min.ToUnity(), shape.Bounds.Max.ToUnity(), damaged ? WoodDamaged : Wood);
                    return;
                default:
                    AddBox(m, shape.Bounds.Min.ToUnity(), shape.Bounds.Max.ToUnity(), Brick);
                    return;
            }
        }

        public void Build(BrushWorld world)
        {
            foreach (GameObject g in _blocks) Destroy(g);
            _blocks.Clear();
            _tileMeshes.Clear();
            _tilesRev = -1;
            if (_look != null) for (int i = 0; i < _look.Length; i++) _look[i] = -1;
            Shader shader = Shader.Find("SCULPT/Greybox");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Greybox not found; buildings are not drawn.");
                return;
            }
            _mat = new Material(shader) { name = "Greybox (runtime)" };
            _mat.SetFloat("_Scenery", 1f); // takes the picture settings (shadows, AO, lighting, atmosphere)
            _brushWorld = world;
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            _surface = Greybox;

            var byBlock = new Dictionary<Vector2Int, MeshBits>();
            // Hills and berms are drawn as meadow (the terrain's own ground look: grass on top, soil down the sides), so
            // they read as part of the land around them instead of green greybox slabs. Their yellow edge lines stay.
            var hills = new MeshBits { Raw = true };
            for (int i = 0; i < world.Count; i++)
            {
                Brush b = world[i];
                if (b.Kind == BrushKind.Tile) continue; // drawn from the client's tile state (BindTiles)
                Vector2Int key = BlockOf(b);
                if (!byBlock.TryGetValue(key, out MeshBits m)) byBlock[key] = m = new MeshBits();
                if (b.Kind == BrushKind.Ground)
                {
                    int first = hills.V.Count;
                    _stripeSink = m;
                    AddBrush(hills, b);
                    _stripeSink = null;
                    for (int v = first; v < hills.V.Count; v++)
                    {
                        Vector3 p = hills.V[v];
                        double top = b.IsRamp ? b.Bounds.Min.Y + (b.Bounds.Max.Y - b.Bounds.Min.Y) * System.Math.Max(0, System.Math.Min(1, b.RiseFraction(p.x, p.z))) : b.Bounds.Max.Y;
                        hills.U1.Add(new Vector2(ChunkMesher.KindGround, Mathf.Clamp((float)(top - p.y), 0f, 2.4f)));
                    }
                    continue;
                }
                AddBrush(m, b);
            }
            Shader voxel = Shader.Find("SCULPT/Voxel");
            if (hills.V.Count > 0 && voxel != null)
            {
                _groundMat = new Material(voxel) { name = "Hills (runtime)" };
                _groundMat.SetFloat("_Scenery", 1f);
                var mesh = new Mesh { name = "Hills", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(hills.V);
                mesh.SetNormals(hills.N);
                mesh.SetColors(hills.C);
                mesh.SetUVs(1, hills.U1);
                mesh.SetTriangles(hills.T, 0);
                mesh.RecalculateBounds();
                var go = new GameObject("Hills");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _groundMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on (players never do)
                r.receiveShadows = true;
                _blocks.Add(go);
            }
            foreach (var kv in byBlock)
            {
                var mesh = new Mesh { name = $"Brushes {kv.Key}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(kv.Value.V);
                mesh.SetNormals(kv.Value.N);
                mesh.SetColors(kv.Value.C);
                mesh.SetTriangles(kv.Value.T, 0);
                mesh.RecalculateBounds();
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on (players never do)
                r.receiveShadows = true;
                _blocks.Add(go);
            }
        }

        private sealed class MeshBits
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
            public readonly List<Vector2> U1 = new List<Vector2>();
            public readonly List<Vector2> U2 = new List<Vector2>();
            /// <summary>A tile mesh: every vertex carries the cell fields (uv1, uv2; zero unless set, see BrushView.Cells).</summary>
            public bool Cells;
            /// <summary>Colours are shade only, not linearised (the voxel shader's nature palette does the colour).</summary>
            public bool Raw;

            public void Clear()
            {
                V.Clear(); N.Clear(); C.Clear(); T.Clear(); U1.Clear(); U2.Clear();
            }
        }

        private static Color ColorOf(BrushKind k)
        {
            switch (k)
            {
                case BrushKind.Floor: return Floor;
                case BrushKind.Frame: return Frame;
                case BrushKind.Stairs: return Stairs;
                case BrushKind.Ramp: return Ramp;
                case BrushKind.Wall: return Wall;
                case BrushKind.Marker: return Marker;
                default: return Ground;
            }
        }

        private void AddBrush(MeshBits m, in Brush b)
        {
            Color col = m.Raw ? Color.white : ColorOf(b.Kind);
            if (b.Kind == BrushKind.Stairs && b.IsRamp) { AddStairs(m, b, col); return; }
            if (b.IsRamp) { AddWedge(m, b, col); return; }
            AddBox(m, b.Bounds.Min.ToUnity(), b.Bounds.Max.ToUnity(), col);
        }

        // ---- shapes ----

        /// <summary>
        /// Hills: shade from the fixed sun (Scenery's direction), so a meadow slope reads as a slope against the flat
        /// meadow around it (lighter facing the sun, darker away). 1 on flat ground.
        /// </summary>
        private static float HillShade(Vector3 n)
        {
            Vector3 toSun = -(Quaternion.Euler(55f, -35f, 0f) * Vector3.forward);
            float d = Vector3.Dot(n, toSun);
            return Mathf.Clamp((0.25f + 0.75f * d) / (0.25f + 0.75f * toSun.y), 0.45f, 1.15f);
        }

        /// <summary>Fixed sun shading per face, baked into the colour (tops brightest, sides darker).</summary>
        private static float Shade(Vector3 n)
        {
            if (n.y > 0.5f) return 1.0f - (1f - n.y) * 0.4f;
            if (n.y < -0.5f) return 0.45f;
            return Mathf.Abs(n.z) > Mathf.Abs(n.x) ? 0.80f : 0.68f;
        }

        private void Quad(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            Color s = col * (m.Raw ? HillShade(n) : Shade(n));
            s.a = m.Raw ? 1f : _surface; // hills use the voxel shader (alpha 1); greybox alpha is the surface code
            if (_linear && !m.Raw) s = s.linear;
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            for (int k = 0; k < 4; k++) { m.N.Add(n); m.C.Add(s); }
            if (m.Cells) for (int k = 0; k < 4; k++) { m.U1.Add(_uv1); m.U2.Add(_uv2); }
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
            m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
        }

        private void Tri(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            Color s = col * (m.Raw ? HillShade(n) : Shade(n));
            s.a = m.Raw ? 1f : _surface; // hills use the voxel shader (alpha 1); greybox alpha is the surface code
            if (_linear && !m.Raw) s = s.linear;
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c);
            for (int k = 0; k < 3; k++) { m.N.Add(n); m.C.Add(s); }
            if (m.Cells) for (int k = 0; k < 3; k++) { m.U1.Add(_uv1); m.U2.Add(_uv2); }
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
        }

        private void AddBox(MeshBits m, Vector3 lo, Vector3 hi, Color col)
        {
            Vector3 p000 = lo, p111 = hi;
            Vector3 p100 = new Vector3(hi.x, lo.y, lo.z), p010 = new Vector3(lo.x, hi.y, lo.z), p001 = new Vector3(lo.x, lo.y, hi.z);
            Vector3 p110 = new Vector3(hi.x, hi.y, lo.z), p101 = new Vector3(hi.x, lo.y, hi.z), p011 = new Vector3(lo.x, hi.y, hi.z);
            // Winding: clockwise seen from outside (Unity front faces).
            Quad(m, p010, p011, p111, p110, col); // top
            Quad(m, p000, p100, p101, p001, col); // bottom
            Quad(m, p000, p010, p110, p100, col); // south (-z)
            Quad(m, p101, p111, p011, p001, col); // north (+z)
            Quad(m, p001, p011, p010, p000, col); // west (-x)
            Quad(m, p100, p110, p111, p101, col); // east (+x)
        }

        /// <summary>Maps (u along the rise 0..1, v across 0..1, y) to world for a wedge's footprint.</summary>
        private static Vector3 Local(in Brush b, double u, double v, double y)
        {
            Aabb w = b.Bounds;
            double x, z;
            switch (b.Rise)
            {
                case Dir.East: x = w.Min.X + u * (w.Max.X - w.Min.X); z = w.Min.Z + v * (w.Max.Z - w.Min.Z); break;
                case Dir.West: x = w.Max.X - u * (w.Max.X - w.Min.X); z = w.Max.Z - v * (w.Max.Z - w.Min.Z); break;
                case Dir.North: z = w.Min.Z + u * (w.Max.Z - w.Min.Z); x = w.Max.X - v * (w.Max.X - w.Min.X); break;
                default: z = w.Max.Z - u * (w.Max.Z - w.Min.Z); x = w.Min.X + v * (w.Max.X - w.Min.X); break;
            }
            return new Vector3((float)x, (float)y, (float)z);
        }

        private void AddWedge(MeshBits m, in Brush b, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            Vector3 l0 = Local(b, 0, 0, y0), l1 = Local(b, 0, 1, y0);   // low edge
            Vector3 h0 = Local(b, 1, 0, y1), h1 = Local(b, 1, 1, y1);   // high edge (top)
            Vector3 b0 = Local(b, 1, 0, y0), b1 = Local(b, 1, 1, y0);   // high end, bottom
            Quad(m, l0, l1, h1, h0, col);          // slope
            Quad(m, b0, h0, h1, b1, col);          // high end face
            Quad(m, l0, b0, b1, l1, col);          // bottom
            Tri(m, l0, h0, b0, col);               // sides
            Tri(m, l1, b1, h1, col);
            // Yellow edge lines along the low edge and the crest so the ramp's ends read.
            AddStripe(_stripeSink ?? m, b, 0.0, 0.08, y0, y0, StairNose);
            AddStripe(_stripeSink ?? m, b, 1.0 - 0.04, 1.0, y1, y1, StairNose);
        }

        /// <summary>A thin painted band across the wedge at rise fraction u0..u1, lifted 1 cm (cosmetic only).</summary>
        private void AddStripe(MeshBits m, in Brush b, double u0, double u1, double ya, double yb, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            double h0 = y0 + (y1 - y0) * u0 + 0.01, h1 = y0 + (y1 - y0) * u1 + 0.01;
            Quad(m, Local(b, u0, 0, h0), Local(b, u0, 1, h0), Local(b, u1, 1, h1), Local(b, u1, 0, h1), col);
        }

        private void AddStairs(MeshBits m, in Brush b, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            double rise = y1 - y0;
            int n = Mathf.Max(1, (int)System.Math.Round(rise / StepRise));
            for (int i = 0; i < n; i++)
            {
                double u0 = i / (double)n, u1 = (i + 1) / (double)n;
                double top = y0 + rise * (i + 0.5) / n; // tread centred on the slope
                Vector3 lo = Local(b, u0, 0, y0), hi = Local(b, u1, 1, top);
                AddBox(m, Vector3.Min(lo, hi), Vector3.Max(lo, hi), col);
                // Yellow nose strip on the front 6 cm of each tread.
                // The nose is the step's low-side edge (the edge you walk off going down).
                double um = u0 + (u1 - u0) * 0.18;
                Vector3 a = Local(b, u0, 0, top + 0.005), c = Local(b, um, 1, top + 0.005);
                Vector3 mn = Vector3.Min(a, c), mx = Vector3.Max(a, c);
                Quad(m, new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mn.z), StairNose);
            }
        }
    }
}
