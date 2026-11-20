using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Game.World;
using UnityEngine;

namespace Sculpt.Game.Sculpting
{
    /// <summary>
    /// The landing snap of a sculpted piece: for ~0.15 s the piece is drawn as its own mesh that arrives the last fifth of
    /// the way, overshoots by about 5 cm and settles (a damped spring), while the terrain mesh leaves those sub-cells out.
    /// Purely visual and short on purpose: collision and bullets use the real grid from the landing tick, and the drawn
    /// piece is never more than ~25 cm (first frame) or ~5 cm (after 30 ms) from where it really is.
    /// </summary>
    public sealed class PieceSnap
    {
        // Damped cosine: offset(t) = -Start * exp(-Damping t) * cos(Omega t), in units of the move distance.
        // Crosses zero at 25 ms, overshoots to about +4% (5 cm on a 1.2 m move) at 50 ms, gone by 150 ms.
        public const float Start = 0.2f;
        public const float Omega = 62.8f;
        public const float Damping = 32f;
        public const float Duration = 0.15f;

        private sealed class Active
        {
            public Int3[] Cells;
            public GameObject Go;
            public Mesh Mesh;
            public Vector3 Origin;
            public Vector3 Move;
            public float Age;
        }

        private readonly WorldView _world;
        private readonly Transform _parent;
        private readonly PieceMesher _mesher = new PieceMesher();
        private readonly List<Active> _active = new List<Active>();

        public PieceSnap(WorldView world, Transform parent)
        {
            _world = world;
            _parent = parent;
        }

        /// <summary>Start a snap for a piece that just moved by <paramref name="delta"/> sub-cells into <paramref name="cells"/>.</summary>
        public void Snap(Int3[] cells, Int3 delta)
        {
            if (cells == null || cells.Length == 0 || delta == Int3.Zero || _world == null || _world.Material == null) return;
            // A newer snap on overlapping cells replaces the older one.
            for (int i = _active.Count - 1; i >= 0; i--)
                if (Overlaps(_active[i].Cells, cells)) End(i);

            var a = new Active { Cells = cells, Mesh = new Mesh { name = "Snapping piece" } };
            if (!_mesher.Build(cells, Int3.Zero, a.Mesh)) { Object.Destroy(a.Mesh); return; }
            a.Origin = _mesher.Origin;
            a.Move = delta.ToUnity() * (float)SubGrid.SubSize;
            a.Go = new GameObject("Snapping Piece");
            a.Go.transform.SetParent(_parent, false);
            a.Go.AddComponent<MeshFilter>().sharedMesh = a.Mesh;
            var r = a.Go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _world.Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _active.Add(a);
            Place(a);
            _world.Hide(cells);
        }

        public void Update(float dt)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Active a = _active[i];
                a.Age += dt;
                if (a.Age >= Duration || Moved(a)) { End(i); continue; }
                Place(a);
            }
        }

        public void Clear()
        {
            for (int i = _active.Count - 1; i >= 0; i--) End(i);
        }

        /// <summary>Offset along the move, as a fraction of the move distance, at time t.</summary>
        public static float Offset(float t) => -Start * Mathf.Exp(-Damping * t) * Mathf.Cos(Omega * t);

        private static void Place(Active a) => a.Go.transform.position = a.Origin + a.Move * Offset(a.Age);

        private void End(int i)
        {
            Active a = _active[i];
            _active.RemoveAt(i);
            _world.Unhide(a.Cells);
            Object.Destroy(a.Go);
            Object.Destroy(a.Mesh);
        }

        /// <summary>The piece left these cells already (e.g. pushed off a ledge and falling): stop drawing the copy.</summary>
        private bool Moved(Active a)
        {
            SubGrid g = _world.Grid;
            if (g == null) return true;
            Int3 c0 = a.Cells[0], c1 = a.Cells[a.Cells.Length - 1], cm = a.Cells[a.Cells.Length / 2];
            return !g.IsSolid(c0.X, c0.Y, c0.Z) || !g.IsSolid(c1.X, c1.Y, c1.Z) || !g.IsSolid(cm.X, cm.Y, cm.Z);
        }

        private static bool Overlaps(Int3[] a, Int3[] b)
        {
            var set = new HashSet<Int3>(a);
            foreach (var c in b) if (set.Contains(c)) return true;
            return false;
        }
    }
}
