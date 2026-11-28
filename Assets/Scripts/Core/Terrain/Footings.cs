using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Level footings for every building on a map stamped eight ways (map v3 step 4). A footing levels a rectangle grown
    /// by its apron to one height, then cuts and fills the ground round it with a walkable bank
    /// (<see cref="GroundShapes.Footing"/>). Footings are added in the design eighth's map coordinates; each one stands
    /// for itself and its seven twins, so near the eighth's edges a footing's bank meets its twins' banks the same way in
    /// every eighth.
    ///
    /// Footings never fight: a new one is only given a level its neighbours (and their twins) allow, so no bank ever cuts
    /// into another building's level ground (<see cref="Allowed"/>). Then applying them all, in any one fixed order,
    /// leaves every footing's rectangle and apron exactly at its level (tested). The order is fixed anyway (footing,
    /// then twin), so the ground is the same on every machine.
    /// </summary>
    public sealed class Footings
    {
        public struct Footing
        {
            public double X0, Z0, X1, Z1, Level, Apron;
        }

        private struct Image
        {
            public double X0, Z0, X1, Z1, Level, Apron, Reach;
        }

        public readonly double CentreX, CentreZ;
        /// <summary>The eight symmetries as (u, v) -> (a u + b v, c u + d v) about the centre; one identity if none.</summary>
        private readonly int[][] _sym;
        public readonly double BatterDegrees;
        private readonly double _tan;
        private readonly List<Footing> _list = new List<Footing>();
        private readonly List<Image> _images = new List<Image>();

        // Lookup after Freeze: images by bucket, in the fixed order.
        private const double Bucket = 16;
        private double _bx0, _bz0;
        private int _bnx, _bnz;
        private int[][] _buckets;

        public Footings(double centreX, double centreZ, int[][] symmetries, double batterDegrees = 28)
        {
            CentreX = centreX;
            CentreZ = centreZ;
            _sym = symmetries ?? new[] { new[] { 1, 0, 0, 1 } };
            BatterDegrees = batterDegrees;
            _tan = Math.Tan(batterDegrees * Math.PI / 180);
        }

        public int Count => _list.Count;
        public Footing this[int i] => _list[i];
        public bool Frozen => _buckets != null;

        /// <summary>
        /// The levels a new footing over [x0, x1] x [z0, z1] with this apron could take without its bank or anyone
        /// else's cutting into level ground: between <paramref name="lo"/> and <paramref name="hi"/> (either may be
        /// infinite). False if no level is allowed (its level ground overlaps two that differ).
        /// </summary>
        public bool Allowed(double x0, double z0, double x1, double z1, double apron, out double lo, out double hi)
        {
            lo = double.NegativeInfinity;
            hi = double.PositiveInfinity;
            foreach (Image m in _images)
            {
                double gap = RectGap(x0, z0, x1, z1, m.X0, m.Z0, m.X1, m.Z1) - apron - m.Apron;
                double allow = Math.Max(0, gap) * _tan;
                lo = Math.Max(lo, m.Level - allow);
                hi = Math.Min(hi, m.Level + allow);
            }
            return lo <= hi + 1e-9;
        }

        /// <summary>The level on the <paramref name="lattice"/> nearest <paramref name="want"/> that <see cref="Allowed"/> allows; NaN if none.</summary>
        public double PickLevel(double x0, double z0, double x1, double z1, double apron, double want, double lattice = 0.3)
        {
            if (!Allowed(x0, z0, x1, z1, apron, out double lo, out double hi)) return double.NaN;
            double l = Math.Round(Math.Max(lo, Math.Min(hi, want)) / lattice) * lattice;
            if (l < lo - 1e-9) l += lattice;
            if (l > hi + 1e-9) l -= lattice;
            return l >= lo - 1e-9 && l <= hi + 1e-9 ? l : double.NaN;
        }

        /// <summary>Adds a footing (and its twins). Use a level from <see cref="PickLevel"/> so it never fights another.</summary>
        public int Add(double x0, double z0, double x1, double z1, double level, double apron)
        {
            if (Frozen) throw new InvalidOperationException("The footings are frozen.");
            if (x1 < x0 || z1 < z0) throw new ArgumentException("A footing's rectangle is backwards.");
            _list.Add(new Footing { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Level = level, Apron = apron });
            var seen = new HashSet<(long, long, long, long)>();
            foreach (int[] m in _sym)
            {
                Tr(x0, z0, m, out double ax, out double az);
                Tr(x1, z1, m, out double bx, out double bz);
                var img = new Image { X0 = Math.Min(ax, bx), Z0 = Math.Min(az, bz), X1 = Math.Max(ax, bx), Z1 = Math.Max(az, bz), Level = level, Apron = apron };
                // A footing that is its own mirror image (on an eighth's edge) counts once.
                if (!seen.Add((Key(img.X0), Key(img.Z0), Key(img.X1), Key(img.Z1)))) continue;
                _images.Add(img);
            }
            return _list.Count - 1;
        }

        private static long Key(double v) => (long)Math.Round(v * 1000);

        private void Tr(double x, double z, int[] m, out double tx, out double tz)
        {
            double u = x - CentreX, v = z - CentreZ;
            tx = CentreX + m[0] * u + m[1] * v;
            tz = CentreZ + m[2] * u + m[3] * v;
        }

        /// <summary>
        /// Gets ready to apply the footings: <paramref name="lowest"/> and <paramref name="highest"/> bound the natural
        /// ground the footings will be applied to (a footing's bank can't reach past where the ground is already within
        /// it, which bounds how far each one looks).
        /// </summary>
        public void Freeze(double lowest, double highest, double x0, double z0, double x1, double z1)
        {
            if (Frozen) return;
            foreach (Image m in _images) { lowest = Math.Min(lowest, m.Level); highest = Math.Max(highest, m.Level); }
            for (int k = 0; k < _images.Count; k++)
            {
                Image m = _images[k];
                m.Reach = m.Apron + Math.Max(Math.Abs(m.Level - lowest), Math.Abs(m.Level - highest)) / _tan + 0.01;
                _images[k] = m;
            }
            _bx0 = x0; _bz0 = z0;
            _bnx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / Bucket));
            _bnz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / Bucket));
            var lists = new List<int>[_bnx * _bnz];
            for (int k = 0; k < _images.Count; k++)
            {
                Image m = _images[k];
                int i0 = Math.Max(0, (int)Math.Floor((m.X0 - m.Reach - _bx0) / Bucket)), i1 = Math.Min(_bnx - 1, (int)Math.Floor((m.X1 + m.Reach - _bx0) / Bucket));
                int j0 = Math.Max(0, (int)Math.Floor((m.Z0 - m.Reach - _bz0) / Bucket)), j1 = Math.Min(_bnz - 1, (int)Math.Floor((m.Z1 + m.Reach - _bz0) / Bucket));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        (lists[i + _bnx * j] ??= new List<int>()).Add(k);
            }
            _buckets = new int[lists.Length][];
            for (int b = 0; b < lists.Length; b++) _buckets[b] = lists[b]?.ToArray() ?? Array.Empty<int>();
        }

        /// <summary>The ground at (x, z) once every footing (and twin) near it has cut and filled <paramref name="natural"/>.</summary>
        public double Apply(double natural, double x, double z)
        {
            if (!Frozen) throw new InvalidOperationException("Freeze the footings first.");
            int i = (int)Math.Floor((x - _bx0) / Bucket), j = (int)Math.Floor((z - _bz0) / Bucket);
            if (i < 0 || j < 0 || i >= _bnx || j >= _bnz) return natural;
            double h = natural;
            foreach (int k in _buckets[i + _bnx * j])
            {
                Image m = _images[k];
                double d = GroundShapes.ToRect(x, z, m.X0, m.Z0, m.X1, m.Z1);
                if (d > m.Reach) continue;
                double reach = Math.Max(0, d - m.Apron) * _tan;
                h = Math.Max(m.Level - reach, Math.Min(m.Level + reach, h));
            }
            return h;
        }

        /// <summary>True if (x, z) lies on a footing's level ground (its rectangle or apron) or one of its twins'.</summary>
        public bool OnLevelGround(double x, double z, double grow = 0)
        {
            foreach (Image m in _images)
                if (GroundShapes.ToRect(x, z, m.X0, m.Z0, m.X1, m.Z1) <= m.Apron + grow) return true;
            return false;
        }

        /// <summary>
        /// The same as <see cref="OnLevelGround"/> for any point of an eight-way map, quickly (after <see cref="Freeze"/>):
        /// the point is folded into the design eighth first, where every footing's twins are bucketed.
        /// </summary>
        public bool OnLevelGroundFast(double x, double z)
        {
            if (!Frozen) throw new InvalidOperationException("Freeze the footings first.");
            if (_sym.Length == 8)
            {
                double u = Math.Abs(x - CentreX), v = Math.Abs(z - CentreZ);
                if (v > u) (u, v) = (v, u);
                x = CentreX + u;
                z = CentreZ + v;
            }
            int i = (int)Math.Floor((x - _bx0) / Bucket), j = (int)Math.Floor((z - _bz0) / Bucket);
            if (i < 0 || j < 0 || i >= _bnx || j >= _bnz) return false;
            foreach (int k in _buckets[i + _bnx * j])
            {
                Image m = _images[k];
                if (GroundShapes.ToRect(x, z, m.X0, m.Z0, m.X1, m.Z1) <= m.Apron) return true;
            }
            return false;
        }

        /// <summary>Distance between two rectangles (0 if they touch or overlap).</summary>
        public static double RectGap(double ax0, double az0, double ax1, double az1, double bx0, double bz0, double bx1, double bz1)
        {
            double dx = Math.Max(0, Math.Max(bx0 - ax1, ax0 - bx1)), dz = Math.Max(0, Math.Max(bz0 - az1, az0 - bz1));
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
