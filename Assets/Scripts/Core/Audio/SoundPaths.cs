using System;
using System.Collections.Generic;

namespace Sculpt.Core.Audio
{
    /// <summary>How a sound reaches the listener.</summary>
    public enum SoundWay : byte
    {
        /// <summary>Clear straight line: heard from where it is, full and bright.</summary>
        Direct = 0,
        /// <summary>Round corners: through a doorway, a hole, a window or over a wall top. Heard from the opening it comes
        /// through, as far away as the route is long, a little duller per corner.</summary>
        Around = 1,
        /// <summary>Through walls: heard from where it really is, much quieter and only the low end.</summary>
        Through = 2,
        /// <summary>Out of range by every way, or walled off by too much solid.</summary>
        Silent = 3,
    }

    /// <summary>The way a sound reaches the listener and how to play it.</summary>
    public struct SoundRoute
    {
        public SoundWay Way;
        /// <summary>Where to put the voice: along <see cref="ArrivesFrom"/>, <see cref="Distance"/> from the listener (the
        /// source itself for a direct sound).</summary>
        public Vec3d Apparent;
        /// <summary>Unit direction from the listener the sound arrives from.</summary>
        public Vec3d ArrivesFrom;
        /// <summary>How far the sound travelled (route length): use it for distance fall-off.</summary>
        public double Distance;
        /// <summary>Extra loss on top of distance (0 or less).</summary>
        public double GainDb;
        /// <summary>Low-pass cutoff to play it with (<see cref="SoundConfig.OpenCutoffHz"/> = unfiltered).</summary>
        public double CutoffHz;
        /// <summary>Corners on the route (Around), walls on the straight line (Through).</summary>
        public int Bends, Walls;

        // What each way gave (for tests and the dev overlay).
        public double DirectDistance;
        public bool AroundFound;
        public double AroundDistance;
        public Vec3d AroundFrom;
        public bool ThroughPossible;
        /// <summary>A loud sound walled off by a lot of solid with no way round in the search range (see
        /// <see cref="SoundConfig.SearchRange"/>): played distant and dull instead of not at all.</summary>
        public bool Obstructed;
        public double ThroughDb;
        public double SolidMetres;

        public bool Audible => Way != SoundWay.Silent;

        /// <summary>Linear gain of <see cref="GainDb"/>.</summary>
        public double Gain => Way == SoundWay.Silent ? 0 : Math.Pow(10, GainDb / 20);
    }

    /// <summary>
    /// Routes sound through the world so it can be placed by ear (Ben 2026-11-26, directional sound pass). For each sound
    /// it finds the clear straight line, else the shortest way round through openings (doorways, blast holes, windows,
    /// stairwells, over wall tops) on a 0.5 m air grid, pulled tight into straight legs, else the straight line through
    /// the walls (muffled). The louder of "round" and "through" wins. Pure Core: same answer on every machine for the same
    /// world, unit-tested. The listener hears from the opening a sound comes through, so a footstep behind a wall with a
    /// door comes from the door, and one in the next room with no opening is a dull thump from its real direction.
    /// Symmetric: a route from A to B is the same as from B to A (same openings, same length), so whoever can hear the
    /// other can be heard back.
    /// </summary>
    public sealed class SoundPaths
    {
        public readonly ITerrain World;
        public readonly SoundConfig Config;

        /// <summary>Face tests run since creation (each up to 4 short rays). Stats only.</summary>
        public long FaceTests { get; private set; }
        /// <summary>Searches for a way round run since creation (cache misses that weren't direct). Stats only.</summary>
        public long Searches { get; private set; }
        /// <summary>Cells opened by the last search. Stats only.</summary>
        public int LastExpansions { get; private set; }

        private const int Off = 1 << 20;
        private const int ChunkCells = 4;

        // Persistent caches, cleared near any change of the world (Invalidate).
        private readonly Dictionary<long, bool>[] _faces = { new Dictionary<long, bool>(), new Dictionary<long, bool>(), new Dictionary<long, bool>() };
        private readonly Dictionary<long, bool> _chunkEmpty = new Dictionary<long, bool>();
        private readonly Dictionary<(long, long, int), CachedRoute> _routes = new Dictionary<(long, long, int), CachedRoute>();

        // Search scratch (reused, never shrunk).
        private readonly List<Vec3d> _chain = new List<Vec3d>();

        private struct Node
        {
            public double G;
            public long Parent;
            public int X, Y, Z;
            public bool Closed;
        }

        private struct CachedRoute
        {
            public SoundRoute Route;
            public double Time;
        }

        /// <summary>The air grid read straight from the world's shapes (null if the world has a part it can't read; then
        /// faces are tested with ray casts as they are reached and remembered).</summary>
        public readonly SoundGrid Grid;

        /// <summary>Builds the air grid for the whole world now (at map load: a few milliseconds on the duel map).</summary>
        public SoundPaths(ITerrain world, SoundConfig config = null)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            Config = config ?? new SoundConfig();
            var g = new SoundGrid(world, Config.CellSize);
            if (g.Supported && Math.Abs(Config.FaceSampleOffset * 4 - Config.CellSize) < 1e-9)
            {
                g.Build();
                Grid = g;
            }
        }

        // ------------------------------------------------------------------ the route

        /// <summary>
        /// How a sound at <paramref name="source"/> reaches <paramref name="listener"/>, if it carries
        /// <paramref name="range"/> metres. <paramref name="now"/> (seconds, any clock) lets a route be reused for
        /// <see cref="SoundConfig.RouteCacheSeconds"/> while both ends stay in their cells; pass a negative value to
        /// always route afresh.
        /// </summary>
        /// <param name="sourceRadius">How far into a solid the source may sit and still be heard directly: a door or a wall
        /// tile sounds from its middle, which is inside it (both sides hear it). 0.05 m for sounds in the air.</param>
        /// <param name="allowSearch">False: no search for a way round this time (the caller is over its budget for the
        /// frame); a blocked sound is then only heard through the walls, and the answer is not cached.</param>
        public SoundRoute Route(Vec3d listener, Vec3d source, double range, double now = -1, double sourceRadius = 0.05, bool allowSearch = true)
        {
            double cs = Config.CellSize;
            Vec3d d = source - listener;
            double dist = d.Length;
            var r = new SoundRoute { DirectDistance = dist, CutoffHz = Config.OpenCutoffHz };
            if (dist >= range) { r.Way = SoundWay.Silent; r.Distance = dist; return r; }
            if (dist < 1e-6) { r.Way = SoundWay.Direct; r.Apparent = source; r.ArrivesFrom = new Vec3d(0, 0, 1); return r; }

            double rc = Config.RouteCacheCell;
            (long, long, int) key = (KeyOf(Cell(listener.X), Cell(listener.Y), Cell(listener.Z)),
                                     KeyOf(Cell(source.X), Cell(source.Y), Cell(source.Z)), (int)Math.Ceiling(range * 8 + sourceRadius * 64));
            if (now >= 0 && _routes.TryGetValue(key, out CachedRoute c) && now - c.Time <= Config.RouteCacheSeconds && now >= c.Time)
                return Reposition(c.Route, listener, source);

            r = Solve(listener, source, range, sourceRadius, allowSearch, out bool searched);
            if (now >= 0 && (searched || r.Way == SoundWay.Direct))
            {
                if (_routes.Count > 4096) _routes.Clear();
                _routes[key] = new CachedRoute { Route = r, Time = now };
            }
            return r;

            int Cell(double v) => (int)Math.Floor(v / rc);
        }

        /// <summary>A cached route for ends that moved within their cells: same way, gains and opening; the direct case and
        /// the through case re-aim at the real source.</summary>
        private static SoundRoute Reposition(SoundRoute r, Vec3d listener, Vec3d source)
        {
            Vec3d d = source - listener;
            double dist = d.Length;
            r.DirectDistance = dist;
            if (r.Way == SoundWay.Direct || r.Way == SoundWay.Through)
            {
                r.Distance = dist;
                r.ArrivesFrom = dist > 1e-9 ? d / dist : r.ArrivesFrom;
                r.Apparent = r.Way == SoundWay.Direct ? source : listener + r.ArrivesFrom * dist;
            }
            else if (r.Way == SoundWay.Around)
                r.Apparent = listener + r.ArrivesFrom * r.Distance;
            return r;
        }

        private SoundRoute Solve(Vec3d listener, Vec3d source, double range, double slack, bool allowSearch, out bool searched)
        {
            searched = false;
            Vec3d d = source - listener;
            double dist = d.Length;
            Vec3d dir = d / dist;
            var r = new SoundRoute { DirectDistance = dist, CutoffHz = Config.OpenCutoffHz };

            if (Clear(listener, source, slack))
            {
                r.Way = SoundWay.Direct;
                r.Apparent = source;
                r.ArrivesFrom = dir;
                r.Distance = dist;
                return r;
            }

            // Through the walls on the straight line.
            r.ThroughPossible = Through(listener, dir, Math.Max(0, dist - slack), out int walls, out double solid);
            r.Walls = walls;
            r.SolidMetres = solid;
            r.ThroughDb = -(Config.WallLossDb * walls + Config.SolidLossDbPerMetre * solid);

            // Round through the openings.
            searched = allowSearch;
            double aroundLen = 0;
            Vec3d firstCorner = source;
            int bends = 0;
            r.AroundFound = allowSearch && Around(listener, source, range, slack, out aroundLen, out firstCorner, out bends);
            if (r.AroundFound)
            {
                r.AroundDistance = aroundLen;
                Vec3d f = firstCorner - listener;
                r.AroundFrom = f.Length > 1e-9 ? f.Normalized : dir;
                r.Bends = bends;
            }
            else if (!r.ThroughPossible && range > Config.SearchRange)
            {
                // A loud sound (a shot, a blast) with no way round inside the search range and too much in the straight
                // line: still heard, distant and dull, from its real direction (sound bends over and round big things).
                r.ThroughPossible = true;
                r.Obstructed = true;
                r.ThroughDb = -Config.FarObstructedLossDb;
            }
            return Mix(r, listener, dir, range, Config);
        }

        /// <summary>
        /// Picks the louder of "round" and "through" (gain over distance, distances under the reference count as the
        /// reference) and fills in how to play it. Pure: exposed for tests.
        /// </summary>
        public static SoundRoute Mix(SoundRoute r, Vec3d listener, Vec3d trueDir, double range, SoundConfig c)
        {
            int charged = Math.Min(r.Bends, c.MaxChargedBends);
            double aroundDb = -c.BendLossDb * charged;
            bool around = r.AroundFound && r.AroundDistance < range;
            bool through = r.ThroughPossible && r.DirectDistance < range;
            double la = around ? Loud(aroundDb, r.AroundDistance, c.ReferenceDistance) : 0;
            double lt = through ? Loud(r.ThroughDb, r.DirectDistance, c.ReferenceDistance) : 0;
            if (!around && !through)
            {
                r.Way = SoundWay.Silent;
                r.Distance = r.DirectDistance;
                r.ArrivesFrom = trueDir;
                r.Apparent = listener + trueDir * r.DirectDistance;
                r.GainDb = double.NegativeInfinity;
                return r;
            }
            if (around && la >= lt)
            {
                r.Way = SoundWay.Around;
                r.Distance = r.AroundDistance;
                r.ArrivesFrom = r.AroundFrom;
                r.GainDb = aroundDb;
                r.CutoffHz = Math.Max(c.MinBendCutoffHz, c.OpenCutoffHz * Math.Pow(c.BendCutoffScale, r.Bends));
                if (r.Bends == 0) r.CutoffHz = c.OpenCutoffHz;
            }
            else
            {
                r.Way = SoundWay.Through;
                r.Distance = r.DirectDistance;
                r.ArrivesFrom = trueDir;
                r.GainDb = r.ThroughDb;
                r.CutoffHz = r.Obstructed ? c.FarObstructedCutoffHz : c.ThroughCutoffHz / Math.Pow(2, Math.Max(0, r.Walls - 1));
                r.Bends = 0;
            }
            r.Apparent = listener + r.ArrivesFrom * r.Distance;
            return r;
        }

        /// <summary>
        /// Head shadow: a sound from behind loses a little top end and level, so front and back can be told apart on
        /// plain stereo (panning alone sounds the same in front and behind). 0 straight ahead or to the side, full straight
        /// behind. Pure.
        /// </summary>
        public static void HeadShadow(Vec3d forward, Vec3d arrivesFrom, SoundConfig c, out double lossDb, out double cutoffHz)
        {
            double f = forward.X * arrivesFrom.X + forward.Z * arrivesFrom.Z;
            double len = Math.Sqrt(forward.X * forward.X + forward.Z * forward.Z) * Math.Sqrt(arrivesFrom.X * arrivesFrom.X + arrivesFrom.Z * arrivesFrom.Z);
            double behind = len > 1e-9 ? Math.Max(0, -f / len) : 0;
            lossDb = -c.BehindLossDb * behind;
            cutoffHz = c.OpenCutoffHz + (c.BehindCutoffHz - c.OpenCutoffHz) * behind;
        }

        /// <summary>Relative loudness used to compare ways: linear gain over distance (inverse distance law).</summary>
        public static double Loud(double gainDb, double distance, double reference) => Math.Pow(10, gainDb / 20) / Math.Max(distance, reference);

        // ------------------------------------------------------------------ through walls

        /// <summary>Walls and metres of solid on the straight line. False if too much solid for anything to come through.</summary>
        private bool Through(Vec3d from, Vec3d dir, double dist, out int walls, out double solid)
        {
            walls = 0;
            solid = 0;
            const double step = 0.05, nudge = 0.001;
            Vec3d p = from;
            double left = dist;
            while (left > nudge)
            {
                TerrainHit h = World.Raycast(p, dir, left);
                if (!h.Hit) return true;
                walls++;
                if (walls > Config.MaxWalls) return false;
                // Walk through the solid to where it ends.
                double t = h.Distance, s = 0;
                while (true)
                {
                    s += step;
                    if (t + s >= left) { s = left - t; break; } // the source is inside or right behind the solid
                    if (!Inside(p + dir * (t + s))) break;
                    if (solid + s > Config.MaxSolidMetres) return false;
                }
                solid += s;
                if (solid > Config.MaxSolidMetres) return false;
                double moved = t + s + nudge;
                p = p + dir * moved;
                left -= moved;
            }
            return true;
        }

        // ------------------------------------------------------------------ round through openings

        /// <summary>One side of the two-way search: its cells, its frontier and the end it heads for.</summary>
        private sealed class Side
        {
            public readonly Dictionary<long, Node> Nodes = new Dictionary<long, Node>();
            public readonly MinHeap Open = new MinHeap();
            public Vec3d Target;
            public void Clear() { Nodes.Clear(); Open.Clear(); }
        }

        private readonly Side _fromListener = new Side(), _fromSource = new Side();

        /// <summary>
        /// The shortest way round, searched from both ends at once (each heading for the other end) until they meet. A
        /// sealed room on either side runs out of cells quickly, so "no way round" costs little. The meeting cell joins
        /// the two halves; the chain is then pulled tight into straight legs.
        /// </summary>
        private bool Around(Vec3d listener, Vec3d source, double range, double slack, out double length, out Vec3d firstCorner, out int bends)
        {
            length = 0;
            firstCorner = source;
            bends = 0;
            Searches++;
            double cs = Config.CellSize;
            double bound = Math.Min(range, Config.SearchRange) * Config.GridSlack;
            Side a = _fromListener, b = _fromSource;
            a.Clear();
            b.Clear();
            a.Target = source;
            b.Target = listener;
            int expansions = 0, budget = Config.MaxNewFaceTests;
            Seed(a, listener, 0);
            Seed(b, source, slack);
            long meet = long.MinValue;
            if (a.Open.Count > 0 && b.Open.Count > 0)
            {
                while (a.Open.Count > 0 && b.Open.Count > 0 && expansions < Config.MaxExpansions)
                {
                    // Grow the smaller frontier: a sealed side empties fast and ends the search.
                    Side s = a.Open.Count <= b.Open.Count ? a : b;
                    Side o = s == a ? b : a;
                    long k = Expand(s, o, bound, ref budget);
                    expansions++;
                    if (k != long.MinValue) { meet = k; break; }
                }
            }
            LastExpansions = expansions;
            if (meet == long.MinValue) return false;

            // listener -> ... -> meet (from a), then meet -> ... -> source (from b).
            _chain.Clear();
            for (long k = meet; k != long.MinValue; k = a.Nodes[k].Parent)
            {
                Node n = a.Nodes[k];
                _chain.Add(Centre(n.X, n.Y, n.Z));
            }
            _chain.Add(listener);
            _chain.Reverse();
            for (long k = b.Nodes[meet].Parent; k != long.MinValue; k = b.Nodes[k].Parent)
            {
                Node n = b.Nodes[k];
                _chain.Add(Centre(n.X, n.Y, n.Z));
            }
            _chain.Add(source);

            int last = _chain.Count - 1, at = 0;
            bool first = true;
            while (at < last)
            {
                int j = at + 1;
                while (j + 1 <= last && Clear(_chain[at], _chain[j + 1], j + 1 == last ? slack : 0)) j++;
                length += (_chain[j] - _chain[at]).Length;
                if (j != last)
                {
                    bends++;
                    if (first) firstCorner = _chain[j];
                }
                first = false;
                at = j;
            }
            if (bends == 0) firstCorner = source;
            return length < range;
        }

        /// <summary>Starts a side at the up to eight cell centres round <paramref name="p"/> that it can see.</summary>
        private void Seed(Side s, Vec3d p, double slack)
        {
            Corners(p, out int sx, out int sy, out int sz);
            for (int i = 0; i < 8; i++)
            {
                int x = sx + (i & 1), y = sy + ((i >> 1) & 1), z = sz + ((i >> 2) & 1);
                Vec3d c = Centre(x, y, z);
                if (!Clear(c, p, slack)) continue;
                double g = (c - p).Length;
                s.Nodes[KeyOf(x, y, z)] = new Node { G = g, Parent = long.MinValue, X = x, Y = y, Z = z };
                s.Open.Push(g + H(c, s.Target), KeyOf(x, y, z));
            }
        }

        /// <summary>Expands the best cell of <paramref name="s"/>; returns a cell the other side has reached, if any.</summary>
        private long Expand(Side s, Side other, double bound, ref int budget)
        {
            double cs = Config.CellSize;
            while (s.Open.Count > 0)
            {
                s.Open.Pop(out _, out long k);
                Node n = s.Nodes[k];
                if (n.Closed) continue;
                n.Closed = true;
                s.Nodes[k] = n;
                if (other.Nodes.ContainsKey(k)) return k;
                for (int d = 0; d < 6; d++)
                {
                    int axis = d >> 1, sign = (d & 1) == 0 ? 1 : -1;
                    int nx = n.X + (axis == 0 ? sign : 0), ny = n.Y + (axis == 1 ? sign : 0), nz = n.Z + (axis == 2 ? sign : 0);
                    long nk = KeyOf(nx, ny, nz);
                    double g = n.G + cs;
                    if (s.Nodes.TryGetValue(nk, out Node m) && (m.Closed || m.G <= g)) continue;
                    Vec3d c = Centre(nx, ny, nz);
                    if (g + (c - s.Target).Length > bound) continue;
                    // The face between two cells is stored on the lower one.
                    int lx = sign > 0 ? n.X : nx, ly = sign > 0 ? n.Y : ny, lz = sign > 0 ? n.Z : nz;
                    if (!FaceOpen(lx, ly, lz, axis, ref budget)) continue;
                    s.Nodes[nk] = new Node { G = g, Parent = k, X = nx, Y = ny, Z = nz };
                    s.Open.Push(g + H(c, s.Target), nk);
                    if (other.Nodes.ContainsKey(nk)) return nk;
                }
                return long.MinValue;
            }
            return long.MinValue;
        }

        private double H(Vec3d c, Vec3d target)
        {
            Vec3d e = c - target;
            return Config.HeuristicWeight * (Math.Abs(e.X) + Math.Abs(e.Y) + Math.Abs(e.Z));
        }

        private void Corners(Vec3d p, out int x, out int y, out int z)
        {
            double cs = Config.CellSize;
            x = (int)Math.Floor(p.X / cs - 0.5);
            y = (int)Math.Floor(p.Y / cs - 0.5);
            z = (int)Math.Floor(p.Z / cs - 0.5);
        }

        private Vec3d Centre(int x, int y, int z)
        {
            double cs = Config.CellSize;
            return new Vec3d((x + 0.5) * cs, (y + 0.5) * cs, (z + 0.5) * cs);
        }

        /// <summary>Can sound pass from cell (x,y,z) to its neighbour one step along +axis? Cached.</summary>
        private bool FaceOpen(int x, int y, int z, int axis, ref int budget)
        {
            if (Grid != null) return Grid.Open(x, y, z, axis);
            long k = KeyOf(x, y, z);
            if (_faces[axis].TryGetValue(k, out bool open)) return open;
            // Both cells inside one chunk that holds no solid at all: open, no rays.
            int cx = FloorDiv(x), cy = FloorDiv(y), cz = FloorDiv(z);
            int x2 = x + (axis == 0 ? 1 : 0), y2 = y + (axis == 1 ? 1 : 0), z2 = z + (axis == 2 ? 1 : 0);
            if (cx == FloorDiv(x2) && cy == FloorDiv(y2) && cz == FloorDiv(z2) && ChunkEmpty(cx, cy, cz)) return true;
            if (budget <= 0) return false; // out of budget this search: treat as shut, but don't remember it
            budget--;
            FaceTests++;
            open = TestFace(x, y, z, axis);
            _faces[axis][k] = open;
            return open;
        }

        /// <summary>The same face tested with ray casts and box queries (the fallback, and the check on the fast grid).</summary>
        internal bool TestFace(int x, int y, int z, int axis)
        {
            double cs = Config.CellSize, o = Config.FaceSampleOffset;
            Vec3d a = Centre(x, y, z);
            Vec3d step = axis == 0 ? new Vec3d(cs, 0, 0) : axis == 1 ? new Vec3d(0, cs, 0) : new Vec3d(0, 0, cs);
            // Nothing solid in the box spanning both cells: open.
            Vec3d half = new Vec3d(cs * 0.5, cs * 0.5, cs * 0.5);
            if (!World.BoxOverlapsSolid(new Aabb(a - half, a + step + half))) return true;
            Vec3d u = axis == 0 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0);
            Vec3d v = axis == 2 ? new Vec3d(0, 1, 0) : new Vec3d(0, 0, 1);
            Vec3d dir = step / cs;
            for (int i = 0; i < 4; i++)
            {
                Vec3d p = a + u * ((i & 1) == 0 ? -o : o) + v * ((i & 2) == 0 ? -o : o);
                if (World.Raycast(p, dir, cs).Hit) continue;
                if (Inside(p) || Inside(p + step)) continue;
                return true;
            }
            return false;
        }

        private bool ChunkEmpty(int cx, int cy, int cz)
        {
            long k = KeyOf(cx, cy, cz);
            if (_chunkEmpty.TryGetValue(k, out bool e)) return e;
            double size = Config.CellSize * ChunkCells;
            var box = new Aabb(new Vec3d(cx * size, cy * size, cz * size), new Vec3d((cx + 1) * size, (cy + 1) * size, (cz + 1) * size));
            e = !World.BoxOverlapsSolid(box);
            _chunkEmpty[k] = e;
            return e;
        }

        private static int FloorDiv(int v) => v >= 0 ? v / ChunkCells : -((-v + ChunkCells - 1) / ChunkCells);

        // ------------------------------------------------------------------ world changes

        /// <summary>Forget everything known about cells touching <paramref name="region"/> (a wall tile broke, healed, a
        /// door opened). Cheap: only the cells and chunks in the region are dropped; every cached route is dropped.</summary>
        public void Invalidate(Aabb region)
        {
            _routes.Clear();
            if (Grid != null) { Grid.Rebuild(region); return; }
            double cs = Config.CellSize;
            Aabb b = region.Expand(cs);
            int x0 = (int)Math.Floor(b.Min.X / cs), x1 = (int)Math.Floor(b.Max.X / cs);
            int y0 = (int)Math.Floor(b.Min.Y / cs), y1 = (int)Math.Floor(b.Max.Y / cs);
            int z0 = (int)Math.Floor(b.Min.Z / cs), z1 = (int)Math.Floor(b.Max.Z / cs);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 200000) { InvalidateAll(); return; }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = KeyOf(x, y, z);
                        _faces[0].Remove(k);
                        _faces[1].Remove(k);
                        _faces[2].Remove(k);
                    }
            for (int cx = FloorDiv(x0); cx <= FloorDiv(x1); cx++)
                for (int cy = FloorDiv(y0); cy <= FloorDiv(y1); cy++)
                    for (int cz = FloorDiv(z0); cz <= FloorDiv(z1); cz++)
                        _chunkEmpty.Remove(KeyOf(cx, cy, cz));
            _routes.Clear();
        }

        /// <summary>Forget everything (a new map, or terrain changed in bulk).</summary>
        public void InvalidateAll()
        {
            Grid?.Build();
            _faces[0].Clear();
            _faces[1].Clear();
            _faces[2].Clear();
            _chunkEmpty.Clear();
            _routes.Clear();
        }

        /// <summary>
        /// Works out every face inside <paramref name="bounds"/> now (at map load), so no sound ever pays for it in play.
        /// Returns the number of faces tested (faces in chunks with nothing solid cost nothing).
        /// </summary>
        public int Prepare(Aabb bounds)
        {
            double cs = Config.CellSize;
            int x0 = (int)Math.Floor(bounds.Min.X / cs), x1 = (int)Math.Floor(bounds.Max.X / cs);
            int y0 = (int)Math.Floor(bounds.Min.Y / cs), y1 = (int)Math.Floor(bounds.Max.Y / cs);
            int z0 = (int)Math.Floor(bounds.Min.Z / cs), z1 = (int)Math.Floor(bounds.Max.Z / cs);
            int budget = int.MaxValue;
            long before = FaceTests;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        for (int a = 0; a < 3; a++)
                            FaceOpen(x, y, z, a, ref budget);
            return (int)(FaceTests - before);
        }

        /// <summary>Cached face results (stats, tests).</summary>
        public int CachedFaces => _faces[0].Count + _faces[1].Count + _faces[2].Count;

        // ------------------------------------------------------------------ helpers

        /// <summary>Clear straight line between two points (nothing solid on it, except within
        /// <paramref name="endSlack"/> of <paramref name="b"/>).</summary>
        public bool Clear(Vec3d a, Vec3d b, double endSlack = 0)
        {
            Vec3d d = b - a;
            double len = d.Length;
            if (len < 1e-9) return !Inside(a);
            TerrainHit h = World.Raycast(a, d / len, len);
            return !h.Hit || h.Distance >= len - endSlack;
        }

        private bool Inside(Vec3d p)
        {
            const double e = 1e-4;
            return World.BoxOverlapsSolid(new Aabb(new Vec3d(p.X - e, p.Y - e, p.Z - e), new Vec3d(p.X + e, p.Y + e, p.Z + e)));
        }

        private static long KeyOf(int x, int y, int z) => ((long)(x + Off) << 42) | ((long)(y + Off) << 21) | (long)(z + Off);

        /// <summary>Binary min-heap of (priority, key). Ties go to the one pushed first.</summary>
        private sealed class MinHeap
        {
            private (double P, long K, int Seq)[] _a = new (double, long, int)[256];
            private int _seq;
            public int Count { get; private set; }

            public void Clear() { Count = 0; _seq = 0; }

            public void Push(double p, long k)
            {
                if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
                int i = Count++;
                _a[i] = (p, k, _seq++);
                while (i > 0)
                {
                    int up = (i - 1) >> 1;
                    if (!Less(_a[i], _a[up])) break;
                    (_a[i], _a[up]) = (_a[up], _a[i]);
                    i = up;
                }
            }

            public void Pop(out double p, out long k)
            {
                p = _a[0].P;
                k = _a[0].K;
                _a[0] = _a[--Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, s = i;
                    if (l < Count && Less(_a[l], _a[s])) s = l;
                    if (r < Count && Less(_a[r], _a[s])) s = r;
                    if (s == i) break;
                    (_a[i], _a[s]) = (_a[s], _a[i]);
                    i = s;
                }
            }

            private static bool Less((double P, long K, int Seq) x, (double P, long K, int Seq) y) => x.P < y.P || (x.P == y.P && x.Seq < y.Seq);
        }
    }
}
