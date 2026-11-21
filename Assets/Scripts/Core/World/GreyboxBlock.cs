using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>How one wall segment between two frame columns (4 tiles across, 3 up) is built.</summary>
    public enum WallKind : byte
    {
        /// <summary>No wall (interior default).</summary>
        None = 0,
        /// <summary>Solid tiles.</summary>
        Wall = 1,
        /// <summary>A 3 m x 3 m doorway (the middle two tiles of the bottom two rows left out), tiles above it.</summary>
        Door = 2,
        /// <summary>A 6 m x 3 m doorway (the whole bottom two rows left out), a row of tiles above it.</summary>
        WideDoor = 3,
        /// <summary>A 3 m x 1.5 m window with a 1.5 m sill (vaultable): the middle two tiles of the middle row left out.</summary>
        Window = 4,
        /// <summary>Open on purpose (stairwells): no tiles, and it doesn't ask for columns.</summary>
        Open = 5,
    }

    /// <summary>What the greybox block holds (tests and the game).</summary>
    public sealed class GreyboxInfo
    {
        /// <summary>Each building's outer box (ground to roof top).</summary>
        public readonly List<Aabb> Buildings = new List<Aabb>();
        public readonly List<string> Names = new List<string>();
        /// <summary>Stair and ramp brushes of the block (index into the brush world).</summary>
        public readonly List<int> Climbs = new List<int>();
        /// <summary>The clear space of every doorway (Door and WideDoor), for tests.</summary>
        public readonly List<Aabb> Doorways = new List<Aabb>();
        /// <summary>Rooftop bridges.</summary>
        public readonly List<Aabb> Bridges = new List<Aabb>();
        /// <summary>Outdoor hill wedges (brush indices).</summary>
        public readonly List<int> Hills = new List<int>();
        /// <summary>Every tile of the block.</summary>
        public readonly List<int> Tiles = new List<int>();
    }

    /// <summary>
    /// The greybox test block (Ben 2026-11-21, step 6): three two-storey buildings at 1.5x real scale on a 6.3 m module
    /// (four 1.5 m tiles plus a 0.3 m frame column), wide doors, a long corridor, open stairwells (two flights stacked
    /// side by side, so ground to first floor and first floor to roof), an outdoor ramp up to a roof, rooftop bridges,
    /// and outdoor hills (straight berms and a long slope; exact wedges, so slides gain and lose speed cleanly).
    /// Storey = 3 tiles (4.5 m) of wall plus a 0.3 m slab: floors at 0, 4.8, roofs at 9.6. Floors, frame, stairs, ramps
    /// and hills are unbreakable for now and colour-coded; every wall is breakable tiles.
    /// </summary>
    public static class GreyboxBlock
    {
        public const double Module = 6.3, Column = 0.3, StoreyHeight = 4.8, WallHeight = 4.5;
        /// <summary>Stairs climb one storey over this run (4.8 m over 9.6 m, 26.6 degrees, like the course's long flight).</summary>
        public const double StairRun = 9.6, StairWidth = 3.0;

        /// <summary>One building on the module grid, built from its edges and slab holes.</summary>
        private sealed class Building
        {
            public double X0, Z0;
            public int Mx, Mz, Storeys = 2;
            /// <summary>Walls along X (thin in Z) on z-line j, segment i: [s, j, i]. Walls along Z on x-line i, segment j: [s, i, j].</summary>
            public WallKind[,,] AlongX, AlongZ;
            /// <summary>Slab holes: [slab s (above storey s), cell i, cell j].</summary>
            public bool[,,] Hole;

            public Building(double x0, double z0, int mx, int mz)
            {
                X0 = x0; Z0 = z0; Mx = mx; Mz = mz;
                AlongX = new WallKind[Storeys, mz + 1, mx];
                AlongZ = new WallKind[Storeys, mx + 1, mz];
                Hole = new bool[Storeys, mx, mz];
                for (int s = 0; s < Storeys; s++)
                {
                    for (int i = 0; i < mx; i++) { AlongX[s, 0, i] = WallKind.Wall; AlongX[s, mz, i] = WallKind.Wall; }
                    for (int j = 0; j < mz; j++) { AlongZ[s, 0, j] = WallKind.Wall; AlongZ[s, mx, j] = WallKind.Wall; }
                }
            }

            public double Cx(int i) => X0 + i * Module;
            public double Cz(int j) => Z0 + j * Module;
            public double Top => Storeys * StoreyHeight;
            public Aabb Bounds => new Aabb(new Vec3d(X0, 0, Z0), new Vec3d(Cx(Mx) + Column, Top, Cz(Mz) + Column));
        }

        public static GreyboxInfo Build(BrushWorld w)
        {
            var info = new GreyboxInfo();

            // ---- Building A "Hall": 3 x 3 modules, stairwell in its west column rising north. ----
            var a = new Building(92, 56, 3, 3);
            Stairwell(w, info, a, 0, true, +1);
            a.AlongX[0, 0, 1] = WallKind.WideDoor;  // south: main entrance from the south hill
            a.AlongX[0, 0, 2] = WallKind.Window;
            a.AlongZ[0, 3, 1] = WallKind.Door;      // east: across to C
            a.AlongX[0, 3, 1] = WallKind.Door;      // north: the street to B
            a.AlongZ[0, 0, 1] = WallKind.Window;    // west
            a.AlongZ[1, 1, 0] = WallKind.Wall;      // first floor: the stairwell's own side wall...
            a.AlongZ[1, 1, 1] = WallKind.Wall;
            a.AlongZ[1, 1, 2] = WallKind.Door;      // ...with a door where the stairs arrive
            a.AlongX[1, 1, 2] = WallKind.Door;      // first floor: a room in the south-east corner
            a.AlongZ[1, 2, 0] = WallKind.Door;
            foreach (int i in new[] { 1, 2 }) a.AlongX[1, 0, i] = WallKind.Window;
            a.AlongX[1, 3, 2] = WallKind.Window;
            a.AlongZ[1, 3, 1] = WallKind.Window;
            Raise(w, info, a, "A hall");

            // ---- Building C "Tower": 2 x 3 modules east of A, stairwell in its east column rising south, ramp to the roof. ----
            var c = new Building(117, 56, 2, 3);
            Stairwell(w, info, c, 1, true, -1);
            c.AlongZ[0, 0, 1] = WallKind.Door;      // west: across to A
            c.AlongX[0, 0, 0] = WallKind.WideDoor;  // south
            c.AlongX[0, 3, 0] = WallKind.Door;      // north: the street to B
            c.AlongZ[0, 2, 0] = WallKind.Window;    // east, along the ramp
            c.AlongZ[0, 2, 2] = WallKind.Window;
            c.AlongX[0, 1, 0] = WallKind.Door;      // ground floor: a room in the south-west corner
            c.AlongZ[0, 1, 0] = WallKind.Wall;
            c.AlongZ[1, 0, 0] = WallKind.Window;
            c.AlongZ[1, 0, 2] = WallKind.Window;
            c.AlongX[1, 3, 0] = WallKind.Window;
            c.AlongZ[1, 2, 1] = WallKind.Window;
            Raise(w, info, c, "C tower");
            // Outdoor ramp up the east side to C's roof (9.6 m over 27 m, 19.6 degrees), flush with the wall.
            double rx = c.Cx(2) + Column, rz1 = c.Cz(3) + Column;
            info.Climbs.Add(w.AddRamp(new Vec3d(rx, 0, rz1 - 27), new Vec3d(rx + 3, c.Top, rz1), Dir.North, BrushKind.Ramp));

            // ---- Building B "Long block": 6 x 2 modules north of A and C. The south row is a 38 m corridor; rooms on the north row. ----
            var b = new Building(92, 82, 6, 2);
            // North row: a room (cell 0), the stair hall (cells 1-3), two rooms (cells 4, 5). Doors onto the corridor: on
            // the ground floor into the hall's open south lane (the upper flight floats over it), upstairs where the
            // lower flight arrives.
            WallKind[][] corridorSide =
            {
                new[] { WallKind.Door, WallKind.Wall, WallKind.Door, WallKind.Wall, WallKind.Door, WallKind.Door },
                new[] { WallKind.Door, WallKind.Door, WallKind.Wall, WallKind.Wall, WallKind.Door, WallKind.Door },
            };
            for (int s = 0; s < 2; s++)
            {
                for (int i = 0; i < 6; i++) b.AlongX[s, 1, i] = corridorSide[s][i];
                foreach (int i in new[] { 1, 4, 5 }) b.AlongZ[s, i, 1] = WallKind.Wall; // room dividers
            }
            b.AlongZ[0, 0, 0] = WallKind.WideDoor;  // corridor ends: wide open doors
            b.AlongZ[0, 6, 0] = WallKind.WideDoor;
            b.AlongZ[1, 0, 0] = WallKind.Window;
            b.AlongZ[1, 6, 0] = WallKind.Window;
            b.AlongX[0, 0, 1] = WallKind.Door;      // south side, facing A and C
            b.AlongX[0, 0, 4] = WallKind.Door;
            foreach (int i in new[] { 0, 2, 3, 5 }) { b.AlongX[0, 0, i] = WallKind.Window; b.AlongX[1, 0, i] = WallKind.Window; }
            b.AlongX[0, 2, 1] = WallKind.WideDoor;  // north side: out to the north hill, beside the stair hall
            foreach (int i in new[] { 0, 4 }) { b.AlongX[0, 2, i] = WallKind.Window; b.AlongX[1, 2, i] = WallKind.Window; }
            b.AlongX[1, 2, 1] = WallKind.Window;
            b.AlongZ[0, 6, 1] = WallKind.Door;      // the east room has its own door
            Stairwell(w, info, b, 1, false, -1, flip: true);
            Raise(w, info, b, "B long block");

            // ---- Rooftop bridges (3 m wide, at roof level). ----
            Bridge(w, info, new Vec3d(a.Cx(3) + Column, 0, 64.1), new Vec3d(c.X0, 0, 67.1));          // A <-> C
            Bridge(w, info, new Vec3d(100, 0, a.Cz(3) + Column), new Vec3d(103, 0, b.Z0));             // A <-> B
            Bridge(w, info, new Vec3d(121, 0, c.Cz(3) + Column), new Vec3d(124, 0, b.Z0));             // C <-> B
            Bridge(w, info, new Vec3d(rx, 0, rz1), new Vec3d(rx + 3, 0, b.Z0 + Module));                // the ramp's top on to B's roof

            // ---- Outdoor hills: exact wedges, unbreakable ground. ----
            // South berm between team 1's spawn and A: up 3 m over 9 m (18.4 degrees), 3 m flat top, down over 9 m.
            Berm(w, info, 89, 127, 28, 9, 3, 3.0);
            // North hill between B and team 2's spawn: up 4.5 m over 13.5 m, 5 m flat top, down over 13.5 m.
            Berm(w, info, 90, 135, 100, 13.5, 5, 4.5);
            // East slope: a long run from 6 m down to the street beside the ramp (18.4 degrees), flat on top to the map edge.
            info.Hills.Add(w.AddRamp(new Vec3d(138, 0, 40), new Vec3d(156, 6, 120), Dir.East, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(156, 0, 40), new Vec3d(163.2, 6, 120), BrushKind.Ground));
            return info;
        }

        /// <summary>A berm along X: rises north from z0 over <paramref name="run"/>, flat for <paramref name="flat"/>, falls over the same run.</summary>
        private static void Berm(BrushWorld w, GreyboxInfo info, double x0, double x1, double z0, double run, double flat, double h)
        {
            info.Hills.Add(w.AddRamp(new Vec3d(x0, 0, z0), new Vec3d(x1, h, z0 + run), Dir.North, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(x0, 0, z0 + run), new Vec3d(x1, h, z0 + run + flat), BrushKind.Ground));
            info.Hills.Add(w.AddRamp(new Vec3d(x0, 0, z0 + run + flat), new Vec3d(x1, h, z0 + 2 * run + flat), Dir.South, BrushKind.Ground));
        }

        private static void Bridge(BrushWorld w, GreyboxInfo info, Vec3d lo, Vec3d hi)
        {
            double top = 2 * StoreyHeight;
            var box = new Aabb(new Vec3d(lo.X, top - Column, lo.Z), new Vec3d(hi.X, top, hi.Z));
            w.Add(Brush.Box(box, BrushKind.Floor));
            info.Bridges.Add(box);
        }

        /// <summary>
        /// An open stairwell in module column (or row) <paramref name="lane"/>: two flights side by side, ground to first
        /// floor and first floor to roof, over three cells in a line. The flights rise toward <paramref name="dir"/> (+1 or
        /// -1 along the axis): each starts in the far cell, crosses the middle cell (open above, through every slab, so
        /// there is headroom) and arrives at the near cell's floor. Opens the walls across the stairwell.
        /// </summary>
        private static void Stairwell(BrushWorld w, GreyboxInfo info, Building b, int lane, bool alongZ, int dir, bool flip = false)
        {
            int n = alongZ ? b.Mz : b.Mx;
            if (n < 3) throw new ArgumentException("A stairwell needs three cells in a line.");
            // Cells along the axis: arrive (A), open middle (B), start (C).
            int ca = dir > 0 ? 2 : 0, cb = 1, cc = dir > 0 ? 0 : 2;
            if (!alongZ) { ca = dir > 0 ? 3 : 1; cb = 2; cc = dir > 0 ? 1 : 3; }
            if (!alongZ && n < 4) throw new ArgumentException("Along X the stairwell uses cells 1 to 3.");
            double Line(int k) => alongZ ? b.Cz(k) : b.Cx(k);
            // The high end meets the arrive cell's slab edge.
            double high = dir > 0 ? Line(ca) : Line(ca + 1) + Column;
            double low = high - dir * StairRun;
            double laneLo = (alongZ ? b.Cx(lane) : b.Cz(lane)) + Column;
            for (int s = 0; s < b.Storeys; s++)
            {
                double y0 = s * StoreyHeight, y1 = y0 + StoreyHeight;
                double l0 = laneLo + (flip ? 1 - s : s) * StairWidth, l1 = l0 + StairWidth;
                double a0 = Math.Min(low, high), a1 = Math.Max(low, high);
                Vec3d lo = alongZ ? new Vec3d(l0, y0, a0) : new Vec3d(a0, y0, l0);
                Vec3d hi = alongZ ? new Vec3d(l1, y1, a1) : new Vec3d(a1, y1, l1);
                Dir rise = alongZ ? (dir > 0 ? Dir.North : Dir.South) : (dir > 0 ? Dir.East : Dir.West);
                info.Climbs.Add(w.AddRamp(lo, hi, rise, BrushKind.Stairs));
                // Open above the middle cell, and no walls across the run.
                if (alongZ) b.Hole[s, lane, cb] = true; else b.Hole[s, cb, lane] = true;
                foreach (int line in new[] { Math.Min(cb, ca) == cb ? cb + 1 : cb, Math.Min(cb, cc) == cb ? cb + 1 : cb })
                {
                    if (alongZ) b.AlongX[s, line, lane] = WallKind.Open;
                    else b.AlongZ[s, line, lane] = WallKind.Open;
                }
            }
        }

        /// <summary>Puts up a building: frame columns, tile walls, slabs.</summary>
        private static void Raise(BrushWorld w, GreyboxInfo info, Building b, string name)
        {
            TileWalls t = w.Tiles;
            // Columns at every grid point a wall touches (full height).
            for (int i = 0; i <= b.Mx; i++)
                for (int j = 0; j <= b.Mz; j++)
                {
                    bool any = false;
                    for (int s = 0; s < b.Storeys && !any; s++)
                    {
                        if (i < b.Mx && Solidish(b.AlongX[s, j, i])) any = true;
                        if (i > 0 && Solidish(b.AlongX[s, j, i - 1])) any = true;
                        if (j < b.Mz && Solidish(b.AlongZ[s, i, j])) any = true;
                        if (j > 0 && Solidish(b.AlongZ[s, i, j - 1])) any = true;
                    }
                    if (!any) continue;
                    w.AddBox(new Vec3d(b.Cx(i), 0, b.Cz(j)), new Vec3d(b.Cx(i) + Column, b.Top, b.Cz(j) + Column), BrushKind.Frame);
                }
            // Walls.
            for (int s = 0; s < b.Storeys; s++)
            {
                double y = s * StoreyHeight;
                for (int j = 0; j <= b.Mz; j++)
                    for (int i = 0; i < b.Mx; i++)
                        Segment(t, info, b.AlongX[s, j, i], new Vec3d(b.Cx(i) + Column, y, b.Cz(j)), true);
                for (int i = 0; i <= b.Mx; i++)
                    for (int j = 0; j < b.Mz; j++)
                        Segment(t, info, b.AlongZ[s, i, j], new Vec3d(b.Cx(i), y, b.Cz(j) + Column), false);
            }
            // Slabs: one per row of cells per storey, split at holes. Each covers the column lines around it.
            for (int s = 0; s < b.Storeys; s++)
            {
                double y1 = (s + 1) * StoreyHeight, y0 = y1 - Column;
                for (int j = 0; j < b.Mz; j++)
                {
                    int i = 0;
                    while (i < b.Mx)
                    {
                        if (b.Hole[s, i, j]) { i++; continue; }
                        int e = i;
                        while (e + 1 < b.Mx && !b.Hole[s, e + 1, j]) e++;
                        w.AddBox(new Vec3d(b.Cx(i), y0, b.Cz(j)), new Vec3d(b.Cx(e + 1) + Column, y1, b.Cz(j + 1) + Column), BrushKind.Floor);
                        i = e + 1;
                    }
                }
            }
            info.Buildings.Add(b.Bounds);
            info.Names.Add(name);
        }

        private static bool Solidish(WallKind k) => k != WallKind.None && k != WallKind.Open;

        /// <summary>One 4 x 3 tile segment with the openings its kind asks for.</summary>
        private static void Segment(TileWalls t, GreyboxInfo info, WallKind kind, Vec3d corner, bool alongX)
        {
            if (!Solidish(kind)) return;
            double s = t.Config.TileSize, th = t.Config.TileThickness;
            for (int r = 0; r < 3; r++)
                for (int k = 0; k < 4; k++)
                {
                    bool gap = kind == WallKind.Door ? r < 2 && (k == 1 || k == 2)
                        : kind == WallKind.WideDoor ? r < 2
                        : kind == WallKind.Window && r == 1 && (k == 1 || k == 2);
                    if (gap) continue;
                    Vec3d lo = alongX ? new Vec3d(corner.X + k * s, corner.Y + r * s, corner.Z) : new Vec3d(corner.X, corner.Y + r * s, corner.Z + k * s);
                    Vec3d hi = alongX ? lo + new Vec3d(s, s, th) : lo + new Vec3d(th, s, s);
                    info.Tiles.Add(t.AddTile(new Aabb(lo, hi), alongX ? Dir.North : Dir.East));
                }
            if (kind == WallKind.Door || kind == WallKind.WideDoor)
            {
                int k0 = kind == WallKind.Door ? 1 : 0, k1 = kind == WallKind.Door ? 3 : 4;
                Vec3d lo = alongX ? new Vec3d(corner.X + k0 * s, corner.Y, corner.Z) : new Vec3d(corner.X, corner.Y, corner.Z + k0 * s);
                Vec3d hi = alongX ? new Vec3d(corner.X + k1 * s, corner.Y + 2 * s, corner.Z + th) : new Vec3d(corner.X + th, corner.Y + 2 * s, corner.Z + k1 * s);
                info.Doorways.Add(new Aabb(lo, hi));
            }
        }
    }
}
