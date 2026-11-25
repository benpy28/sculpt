using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Walkable spots on a map for bots, found by probing the real terrain (the same one movement and bullets use).
    /// The map is cut into square columns; each column can hold several floors (ground, first floor, roof, a stair
    /// segment). A node is a floor a standing player fits on. Edges join neighbouring columns: walk (small height
    /// change, stairs and slopes), jump (up to a one-tile wall: the movement code vaults or mantles it) and drop
    /// (off a ledge, one way). Built once from the map as it starts (every breakable tile intact), so a path never
    /// relies on a hole someone made; a path over a floor that breaks later just makes the bot re-plan from where it lands.
    /// </summary>
    public sealed class BotNav
    {
        public enum Link : byte { Walk = 0, Jump = 1, Drop = 2 }

        public struct Node
        {
            public int Cx, Cz;
            public double Y;
            /// <summary>Walk edges to neighbours (8 means open floor all round; fewer means near a wall).</summary>
            public int Open;
            internal int First, Count;
            public Vec3d Feet(BotNav n) => new Vec3d(n.MinX + (Cx + 0.5) * n.Cell, Y, n.MinZ + (Cz + 0.5) * n.Cell);
        }

        public readonly double Cell, MinX, MinZ;
        public readonly int NX, NZ;
        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<int>[] _columns;
        private readonly List<int> _to = new List<int>();
        private readonly List<float> _cost = new List<float>();
        private readonly List<Link> _kind = new List<Link>();

        public IReadOnlyList<Node> Nodes => _nodes;
        public int EdgeCount => _to.Count;

        // Probe numbers (metres).
        private const double Half = 0.35;          // player half width
        private const double Lift = 0.3;           // box bottom above the floor (stairs and slopes)
        private const double Head = 1.75;          // box top above the floor
        private const double MaxWalkStep = 0.5;
        private const double MaxJumpUp = 1.6;      // one 1.5 m tile: vault or mantle
        private const double MaxDrop = 5.2;        // one storey and a bit
        private const double MinFloorNormalY = 0.7;

        private BotNav(double cell, double minX, double minZ, int nx, int nz)
        {
            Cell = cell; MinX = minX; MinZ = minZ; NX = nx; NZ = nz;
            _columns = new List<int>[nx * nz];
        }

        /// <summary>Probes the terrain inside the square [minX, minX + sizeX] x [minZ, minZ + sizeZ] between floorY and topY.</summary>
        public static BotNav Build(ITerrain t, double minX, double minZ, double sizeX, double sizeZ, double cell = 0.75, double topY = 20, double floorY = -3)
        {
            int nx = (int)Math.Floor(sizeX / cell), nz = (int)Math.Floor(sizeZ / cell);
            var n = new BotNav(cell, minX, minZ, nx, nz);
            var floors = new List<double>(4);
            for (int cz = 0; cz < nz; cz++)
                for (int cx = 0; cx < nx; cx++)
                {
                    double x = minX + (cx + 0.5) * cell, z = minZ + (cz + 0.5) * cell;
                    FindFloors(t, x, z, topY, floorY, floors);
                    foreach (double y in floors)
                    {
                        if (t.BoxOverlapsSolid(StandBox(x, z, y))) continue;
                        var list = n._columns[cz * nx + cx] ??= new List<int>(2);
                        list.Add(n._nodes.Count);
                        n._nodes.Add(new Node { Cx = cx, Cz = cz, Y = y });
                    }
                }
            n.Connect(t);
            return n;
        }

        private static Aabb StandBox(double x, double z, double y) =>
            new Aabb(new Vec3d(x - Half, y + Lift, z - Half), new Vec3d(x + Half, y + Head, z + Half));

        /// <summary>Every floor-like surface in a column, top down.</summary>
        private static void FindFloors(ITerrain t, double x, double z, double top, double bottom, List<double> into)
        {
            into.Clear();
            double y = top;
            for (int guard = 0; guard < 12 && y > bottom; guard++)
            {
                TerrainHit h = t.Raycast(new Vec3d(x, y, z), new Vec3d(0, -1, 0), y - bottom);
                if (!h.Hit) return;
                if (h.Normal.Y >= MinFloorNormalY) into.Add(h.Point.Y);
                // Step down through the solid to the air under it (if any).
                double s = h.Point.Y - 0.05;
                int k = 0;
                while (k < 40 && t.BoxOverlapsSolid(new Aabb(new Vec3d(x - 0.01, s - 0.01, z - 0.01), new Vec3d(x + 0.01, s + 0.01, z + 0.01)))) { s -= 0.1; k++; }
                if (k >= 40) return; // solid for 4 m: the ground
                y = s;
            }
        }

        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        private void Connect(ITerrain t)
        {
            var tmpTo = new List<int>(16);
            var tmpCost = new List<float>(16);
            var tmpKind = new List<Link>(16);
            for (int i = 0; i < _nodes.Count; i++)
            {
                Node a = _nodes[i];
                tmpTo.Clear(); tmpCost.Clear(); tmpKind.Clear();
                int open = 0;
                double ax = MinX + (a.Cx + 0.5) * Cell, az = MinZ + (a.Cz + 0.5) * Cell;
                for (int d = 0; d < 8; d++)
                {
                    int bx = a.Cx + Dx[d], bz = a.Cz + Dz[d];
                    if (bx < 0 || bz < 0 || bx >= NX || bz >= NZ) continue;
                    var col = _columns[bz * NX + bx];
                    if (col == null) continue;
                    bool diag = d >= 4;
                    double run = diag ? Cell * Math.Sqrt(2) : Cell;
                    double bxw = MinX + (bx + 0.5) * Cell, bzw = MinZ + (bz + 0.5) * Cell;
                    foreach (int j in col)
                    {
                        Node b = _nodes[j];
                        double dy = b.Y - a.Y;
                        Link kind;
                        if (Math.Abs(dy) <= MaxWalkStep) kind = Link.Walk;
                        else if (diag) continue; // jumps and drops only straight across
                        else if (dy > 0 && dy <= MaxJumpUp) kind = Link.Jump;
                        else if (dy < 0 && -dy <= MaxDrop) kind = Link.Drop;
                        else continue;
                        double lo = Math.Max(a.Y, b.Y) + Lift, hi = Math.Max(lo + 1.0, Math.Min(a.Y, b.Y) + Head);
                        if (kind != Link.Walk) hi = Math.Max(a.Y, b.Y) + Head;
                        var span = new Aabb(new Vec3d(Math.Min(ax, bxw) - Half, lo, Math.Min(az, bzw) - Half),
                                            new Vec3d(Math.Max(ax, bxw) + Half, hi, Math.Max(az, bzw) + Half));
                        if (t.BoxOverlapsSolid(span)) continue;
                        if (kind == Link.Walk) open++;
                        tmpTo.Add(j);
                        tmpKind.Add(kind);
                        double c = Math.Sqrt(run * run + dy * dy);
                        if (kind == Link.Jump) c += 2.0;
                        else if (kind == Link.Drop) c += 1.0 + -dy * 0.2;
                        tmpCost.Add((float)c);
                    }
                }
                a.First = _to.Count;
                a.Count = tmpTo.Count;
                a.Open = open;
                _to.AddRange(tmpTo);
                _cost.AddRange(tmpCost);
                _kind.AddRange(tmpKind);
                _nodes[i] = a;
            }
        }

        public Vec3d Feet(int node) => _nodes[node].Feet(this);

        /// <summary>The link kind from node a to its neighbour b (Walk if they aren't joined).</summary>
        public Link LinkBetween(int a, int b)
        {
            Node n = _nodes[a];
            for (int e = n.First; e < n.First + n.Count; e++) if (_to[e] == b) return _kind[e];
            return Link.Walk;
        }

        /// <summary>The node nearest a player's feet (same column or close by, the floor nearest their height), or -1.</summary>
        public int Nearest(Vec3d feet, int searchCells = 3)
        {
            int cx = (int)Math.Floor((feet.X - MinX) / Cell), cz = (int)Math.Floor((feet.Z - MinZ) / Cell);
            int best = -1;
            double bestScore = double.MaxValue;
            for (int r = 0; r <= searchCells; r++)
            {
                for (int z = cz - r; z <= cz + r; z++)
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != r) continue;
                        if (x < 0 || z < 0 || x >= NX || z >= NZ) continue;
                        var col = _columns[z * NX + x];
                        if (col == null) continue;
                        foreach (int i in col)
                        {
                            Node n = _nodes[i];
                            double dy = feet.Y - n.Y;
                            // A floor above the feet is a poor match (it's the ceiling); a floor far below is a drop.
                            double score = (dy < -0.6 ? 10 + -dy * 4 : Math.Abs(dy)) + r * Cell * 0.8;
                            if (n.Open < 8) score += 0.2;
                            if (score < bestScore) { bestScore = score; best = i; }
                        }
                    }
                if (best >= 0 && r >= 1) break;
            }
            return best;
        }

        // ================================================================ A*

        private float[] _g;
        private int[] _from;
        private int[] _seen;
        private int _stamp;
        private readonly List<(float f, int n)> _heap = new List<(float, int)>();

        /// <summary>Shortest path from node to node (inclusive), or false. Nodes near walls cost a little more, so paths keep off them.</summary>
        public bool FindPath(int start, int goal, List<int> path, int maxExpand = 40000)
        {
            path.Clear();
            if (start < 0 || goal < 0) return false;
            int n = _nodes.Count;
            if (_g == null || _g.Length != n) { _g = new float[n]; _from = new int[n]; _seen = new int[n]; }
            _stamp++;
            _heap.Clear();
            Vec3d gp = Feet(goal);
            _g[start] = 0; _from[start] = -1; _seen[start] = _stamp;
            Push(Heuristic(start, gp), start);
            int expanded = 0;
            while (_heap.Count > 0 && expanded < maxExpand)
            {
                var (f, cur) = Pop();
                if (cur == goal) break;
                if (f - Heuristic(cur, gp) > _g[cur] + 1e-3) continue; // stale entry
                expanded++;
                Node c = _nodes[cur];
                for (int e = c.First; e < c.First + c.Count; e++)
                {
                    int to = _to[e];
                    float step = _cost[e] * (_nodes[to].Open < 8 ? 1.6f : 1f);
                    float ng = _g[cur] + step;
                    if (_seen[to] == _stamp && ng >= _g[to]) continue;
                    _seen[to] = _stamp; _g[to] = ng; _from[to] = cur;
                    Push(ng + Heuristic(to, gp), to);
                }
            }
            if (_seen[goal] != _stamp) return false;
            for (int k = goal; k >= 0; k = _from[k]) path.Add(k);
            path.Reverse();
            return true;
        }

        private float Heuristic(int node, Vec3d goal)
        {
            Vec3d p = Feet(node);
            double dx = p.X - goal.X, dz = p.Z - goal.Z, dy = p.Y - goal.Y;
            return (float)Math.Sqrt(dx * dx + dz * dz + dy * dy);
        }

        private void Push(float f, int node)
        {
            _heap.Add((f, node));
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_heap[p].f <= _heap[i].f) break;
                (_heap[p], _heap[i]) = (_heap[i], _heap[p]);
                i = p;
            }
        }

        private (float, int) Pop()
        {
            var top = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < _heap.Count && _heap[l].f < _heap[m].f) m = l;
                if (r < _heap.Count && _heap[r].f < _heap[m].f) m = r;
                if (m == i) break;
                (_heap[m], _heap[i]) = (_heap[i], _heap[m]);
                i = m;
            }
            return top;
        }
    }
}
