using System.Collections.Generic;
using Sculpt.Core;

namespace Sculpt.Game.World
{
    /// <summary>
    /// What the natural ground looks like column by column (0.3 m sub-cell columns). Visual only. Paved: inside a
    /// building's footprint the ground floor is drawn as concrete, not meadow. Fixed at map load.
    /// </summary>
    public sealed class GroundCover
    {
        private readonly int _sizeX, _sizeZ;
        private readonly bool[] _paved;

        /// <summary>Concrete for building ground floors: the slab colour upstairs, a touch darker (it takes the traffic).</summary>
        public static readonly UnityEngine.Color Concrete = new UnityEngine.Color(0.62f, 0.60f, 0.56f);

        public GroundCover(SubGrid g, IEnumerable<Aabb> buildings)
        {
            _sizeX = g.SizeX;
            _sizeZ = g.SizeZ;
            _paved = new bool[_sizeX * _sizeZ];
            if (buildings != null)
                foreach (Aabb b in buildings) Mark(_paved, b, 0);
        }

        private void Mark(bool[] into, Aabb b, double margin)
        {
            double s = SubGrid.SubSize;
            int x0 = System.Math.Max(0, (int)System.Math.Floor((b.Min.X - margin) / s));
            int x1 = System.Math.Min(_sizeX - 1, (int)System.Math.Ceiling((b.Max.X + margin) / s) - 1);
            int z0 = System.Math.Max(0, (int)System.Math.Floor((b.Min.Z - margin) / s));
            int z1 = System.Math.Min(_sizeZ - 1, (int)System.Math.Ceiling((b.Max.Z + margin) / s) - 1);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    into[z * _sizeX + x] = true;
        }

        private bool In(int x, int z) => x >= 0 && z >= 0 && x < _sizeX && z < _sizeZ;

        public bool Paved(int x, int z) => In(x, z) && _paved[z * _sizeX + x];

    }
}
