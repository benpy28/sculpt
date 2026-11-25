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
        /// <summary>Each stair flight and ramp as one whole wedge (not in the world: the world holds its segments).</summary>
        public readonly List<Brush> Climbs = new List<Brush>();
        /// <summary>The segment tiles of each climb, low end first, two across.</summary>
        public readonly List<int[]> ClimbTiles = new List<int[]>();
        /// <summary>Floor and roof tiles (bridges included).</summary>
        public readonly List<int> Floors = new List<int>();
        /// <summary>Frame tiles: column pieces, beam strips and nodes.</summary>
        public readonly List<int> Frame = new List<int>();
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
    /// Storey = 3 tiles (4.5 m) of wall plus a 0.3 m slab: floors at 0, 4.8, roofs at 9.6.
    /// Step 8: walls, floors, roofs, bridges, stairs, the roof ramp and the frame are all breakable tiles. Floors are
    /// 1.5 m tiles, stair flights 1.2 m x 1.5 m segments (0.6 m of rise each), the ramp 1.69 m x 1.5 m segments. The
    /// frame is split into column pieces (one per tile row), beam strips over the wall lines and nodes where they meet,
    /// and every frame piece is a follower of the big tiles around it (see <see cref="WallTile.Links"/>), so every hole
    /// is made of whole 1.5 m tiles and the frame only goes when the holes on both sides of it have: no slits, ever.
    /// Hills stay unbreakable ground.
    /// </summary>
    public static class GreyboxBlock
    {
        public const double Module = 6.3, Column = 0.3, StoreyHeight = 4.8, WallHeight = 4.5;
        /// <summary>Stairs climb one storey over this run (4.8 m over 9.6 m, 26.6 degrees, like the course's long flight).</summary>
        public const double StairRun = 9.6, StairWidth = 3.0;
        /// <summary>Stair and ramp segments rise this much each (on the 0.3 m lattice).</summary>
        public const double SegmentRise = 0.6;

        /// <summary>One building on the module grid, built from its edges and slab holes.</summary>
        private sealed class Building
        {
            public double X0, Z0;
            public int Mx, Mz, Storeys = 2;
            /// <summary>Walls along X (thin in Z) on z-line j, segment i: [s, j, i]. Walls along Z on x-line i, segment j: [s, i, j].</summary>
            public WallKind[,,] AlongX, AlongZ;
            /// <summary>Slab holes: [slab s (above storey s), cell i, cell j].</summary>
            public bool[,,] Hole;
            /// <summary>Tile ids while raising: wall tiles by (storey, alongX, line, segment, row, piece), floor tiles by (slab, i, j, a, b).</summary>
            public readonly Dictionary<(int, bool, int, int, int, int), int> WallTiles = new Dictionary<(int, bool, int, int, int, int), int>();
            public readonly Dictionary<(int, int, int, int, int), int> FloorTiles = new Dictionary<(int, int, int, int, int), int>();

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
            Climb(w, info, new Aabb(new Vec3d(rx, 0, rz1 - 27), new Vec3d(rx + 3, c.Top, rz1)), Dir.North, TileKind.Ramp);

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
            // The ramp's top on to B's roof: it runs up beside B's east edge, so past B's south wall it starts where B's roof ends.
            Bridge(w, info, new Vec3d(rx, 0, rz1), new Vec3d(rx + 3, 0, b.Z0 + Module), b.Z0, b.Cx(6) + Column);

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

        // ================================================================ The 1v1 duel map (friend demo, 2026-11-25)

        /// <summary>
        /// The duel block: a central hall with a stairwell on each side, and two side buildings (each with a stairwell
        /// and an outdoor ramp to its roof), linked to the hall's roof by bridges, with a berm in front of each spawn.
        /// Everything comes in pairs turned 180 degrees about the map centre, so both spawns see the same map.
        /// <paramref name="size"/> is the square map's side; the centre is (size/2, size/2).
        /// </summary>
        public static GreyboxInfo BuildDuel(BrushWorld w, double size)
        {
            var info = new GreyboxInfo();
            double mid = size / 2;
            double span3 = 3 * Module + Column, span2 = 2 * Module + Column; // 19.2 m and 12.9 m

            // ---- Central hall: 3 x 3, stairwells in the west lane (rising north) and the east lane (rising south). ----
            var hall = new Building(mid - span3 / 2, mid - span3 / 2, 3, 3);
            Stairwell(w, info, hall, 0, true, +1);
            Stairwell(w, info, hall, 2, true, -1, flip: true);
            SetTurned(hall, WallKind.WideDoor, 0, 0, 1, alongX: true);  // main doors (south and north)
            SetTurned(hall, WallKind.Window, 0, 0, 0, alongX: true);    // windows into the stair lanes
            SetTurned(hall, WallKind.Door, 0, 0, 1, alongX: false);     // side doors (west and east)
            SetTurned(hall, WallKind.Door, 0, 1, 1, alongX: true);      // the middle lane: three rooms in a row
            SetTurned(hall, WallKind.Window, 1, 0, 1, alongX: true);    // first floor windows over the main doors
            SetTurned(hall, WallKind.Window, 1, 0, 1, alongX: false);
            SetTurned(hall, WallKind.Window, 1, 0, 2, alongX: false);
            SetTurned(hall, WallKind.Window, 1, 1, 1, alongX: true);    // first floor: a room at each end
            Raise(w, info, hall, "Hall");

            // ---- Side buildings: 2 x 3, the west one south of centre, the east one (turned) north of it. ----
            const double streetGap = 12;
            var west = new Building(hall.X0 - streetGap - span2, mid - span3 / 2 - 8, 2, 3);
            Stairwell(w, info, west, 0, true, +1);
            west.AlongZ[0, 2, 1] = WallKind.Door;      // east: across the street to the hall
            west.AlongX[0, 0, 1] = WallKind.WideDoor;  // south, toward team 1's spawn
            west.AlongX[0, 3, 1] = WallKind.Door;      // north
            west.AlongX[0, 1, 1] = WallKind.Door;      // ground floor: two rooms in the east lane
            west.AlongZ[0, 2, 0] = WallKind.Window;
            west.AlongZ[0, 2, 2] = WallKind.Window;
            west.AlongZ[0, 0, 1] = WallKind.Window;    // west, onto the ramp
            west.AlongZ[1, 2, 0] = WallKind.Window;
            west.AlongZ[1, 2, 1] = WallKind.Window;
            west.AlongZ[1, 2, 2] = WallKind.Window;
            west.AlongX[1, 0, 1] = WallKind.Window;
            west.AlongX[1, 3, 1] = WallKind.Window;
            west.AlongZ[1, 0, 1] = WallKind.Window;
            var east = new Building(size - west.X0 - span2, size - west.Z0 - span3, 2, 3);
            Stairwell(w, info, east, 1, true, -1, flip: true);
            CopyTurned(west, east);
            Raise(w, info, west, "West");
            Raise(w, info, east, "East");

            // Outdoor ramps up each side building's outer wall to its roof (9.6 m over 27 m), flush with the wall.
            double wx = west.X0 - 3, wz1 = west.Cz(3) + Column;
            Climb(w, info, new Aabb(new Vec3d(wx, 0, wz1 - 27), new Vec3d(wx + 3, west.Top, wz1)), Dir.North, TileKind.Ramp);
            Climb(w, info, new Aabb(new Vec3d(size - wx - 3, 0, size - wz1), new Vec3d(size - wx, east.Top, size - wz1 + 27)), Dir.South, TileKind.Ramp);
            // A 3 m walkway at roof level along the far end of each side building, from the ramp's top across the roof edge.
            Bridge(w, info, new Vec3d(wx, 0, wz1), new Vec3d(west.Cx(2) + Column, 0, wz1 + 3));
            Bridge(w, info, new Vec3d(east.X0, 0, size - wz1 - 3), new Vec3d(size - wx, 0, size - wz1));

            // ---- Rooftop bridges from each side building's roof to the hall's (3 m wide). ----
            double bz = hall.Z0 + 4;
            Bridge(w, info, new Vec3d(west.Cx(2) + Column, 0, bz), new Vec3d(hall.X0, 0, bz + 3));
            Bridge(w, info, new Vec3d(hall.Cx(3) + Column, 0, size - bz - 3), new Vec3d(east.X0, 0, size - bz));

            // ---- Low tile walls in the streets (1.5 m: crouch cover you can vault), in turned pairs. ----
            double th = w.Tiles.Config.TileThickness, ts = w.Tiles.Config.TileSize;
            void LowWall(double x, double z, Dir along, int n)
            {
                info.Tiles.AddRange(w.Tiles.AddWall(new Vec3d(x, 0, z), along, n, 1));
                if (along == Dir.East) info.Tiles.AddRange(w.Tiles.AddWall(new Vec3d(size - x - n * ts, 0, size - z - th), along, n, 1));
                else info.Tiles.AddRange(w.Tiles.AddWall(new Vec3d(size - x - th, 0, size - z - n * ts), along, n, 1));
            }
            LowWall(west.Cx(2) + Column + 4.35, hall.Z0 - 6, Dir.North, 3); // west street, south end
            LowWall(mid - 3, hall.Z0 - 8, Dir.East, 4);                      // in front of the hall's main door
            LowWall(west.X0 + 3.3, west.Z0 - 7, Dir.East, 3);                // in front of the west building

            // ---- Berms between each spawn and the block: up 3 m over 9 m, 3 m flat, down over 9 m (18.4 degrees). ----
            const double bermZ = 7;
            Berm(w, info, hall.X0 - 6, hall.X0 + span3 + 6, bermZ, 9, 3, 3.0);
            Berm(w, info, size - (hall.X0 + span3 + 6), size - (hall.X0 - 6), size - bermZ - 21, 9, 3, 3.0);
            return info;
        }

        /// <summary>
        /// Sets one wall segment and its partner turned 180 degrees about the building's centre. alongX: (storey,
        /// z-line, segment); otherwise (storey, x-line, segment).
        /// </summary>
        private static void SetTurned(Building b, WallKind kind, int s, int line, int seg, bool alongX)
        {
            if (alongX) { b.AlongX[s, line, seg] = kind; b.AlongX[s, b.Mz - line, b.Mx - 1 - seg] = kind; }
            else { b.AlongZ[s, line, seg] = kind; b.AlongZ[s, b.Mx - line, b.Mz - 1 - seg] = kind; }
        }

        /// <summary>Copies a building's walls and slab holes onto <paramref name="to"/> turned 180 degrees (same size).</summary>
        private static void CopyTurned(Building from, Building to)
        {
            for (int s = 0; s < from.Storeys; s++)
            {
                for (int j = 0; j <= from.Mz; j++)
                    for (int i = 0; i < from.Mx; i++)
                        to.AlongX[s, from.Mz - j, from.Mx - 1 - i] = from.AlongX[s, j, i];
                for (int i = 0; i <= from.Mx; i++)
                    for (int j = 0; j < from.Mz; j++)
                        to.AlongZ[s, from.Mx - i, from.Mz - 1 - j] = from.AlongZ[s, i, j];
                for (int i = 0; i < from.Mx; i++)
                    for (int j = 0; j < from.Mz; j++)
                        to.Hole[s, from.Mx - 1 - i, from.Mz - 1 - j] |= from.Hole[s, i, j];
            }
        }

        /// <summary>A berm along X: rises north from z0 over <paramref name="run"/>, flat for <paramref name="flat"/>, falls over the same run.</summary>
        private static void Berm(BrushWorld w, GreyboxInfo info, double x0, double x1, double z0, double run, double flat, double h)
        {
            info.Hills.Add(w.AddRamp(new Vec3d(x0, 0, z0), new Vec3d(x1, h, z0 + run), Dir.North, BrushKind.Ground));
            info.Hills.Add(w.AddBox(new Vec3d(x0, 0, z0 + run), new Vec3d(x1, h, z0 + run + flat), BrushKind.Ground));
            info.Hills.Add(w.AddRamp(new Vec3d(x0, 0, z0 + run + flat), new Vec3d(x1, h, z0 + 2 * run + flat), Dir.South, BrushKind.Ground));
        }

        /// <summary>A rooftop bridge from <paramref name="lo"/> to <paramref name="hi"/> (x and z). Where it runs beside a
        /// roof, past <paramref name="splitZ"/> its tiles start at x <paramref name="xFromAtSplit"/> so they stay off the roof's own.</summary>
        private static void Bridge(BrushWorld w, GreyboxInfo info, Vec3d lo, Vec3d hi, double splitZ = double.NaN, double xFromAtSplit = double.NaN)
        {
            double top = 2 * StoreyHeight;
            var box = new Aabb(new Vec3d(lo.X, top - Column, lo.Z), new Vec3d(hi.X, top, hi.Z));
            if (double.IsNaN(splitZ)) BridgeTiles(w, info, lo, hi);
            else
            {
                BridgeTiles(w, info, lo, new Vec3d(hi.X, 0, splitZ));
                BridgeTiles(w, info, new Vec3d(xFromAtSplit, 0, splitZ), hi);
            }
            info.Bridges.Add(box);
        }

        private static void BridgeTiles(BrushWorld w, GreyboxInfo info, Vec3d lo, Vec3d hi)
        {
            double top = 2 * StoreyHeight;
            var box = new Aabb(new Vec3d(lo.X, top - Column, lo.Z), new Vec3d(hi.X, top, hi.Z));
            // Floor tiles about 1.5 m on a side: the span splits into equal pieces no longer than a tile.
            double s = w.Tiles.Config.TileSize;
            double sx = hi.X - lo.X, sz = hi.Z - lo.Z;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int a = 0; a < nx; a++)
                for (int b = 0; b < nz; b++)
                {
                    var t = new Aabb(new Vec3d(lo.X + sx * a / nx, box.Min.Y, lo.Z + sz * b / nz),
                                     new Vec3d(lo.X + sx * (a + 1) / nx, top, lo.Z + sz * (b + 1) / nz));
                    int id = w.Tiles.AddTile(t, Dir.Up, TileKind.Floor);
                    info.Tiles.Add(id);
                    info.Floors.Add(id);
                }
        }

        /// <summary>
        /// A stair flight or ramp as breakable segments: <see cref="SegmentRise"/> of rise each along the run, 1.5 m tiles
        /// across. Each segment is a wedge on the same slope, so the treads are one smooth climb; the underside is open
        /// (a sawtooth), so a broken segment is a hole you drop through.
        /// </summary>
        private static void Climb(BrushWorld w, GreyboxInfo info, Aabb box, Dir rise, TileKind kind)
        {
            bool alongZ = rise == Dir.North || rise == Dir.South;
            bool up = rise == Dir.North || rise == Dir.East;
            double a0 = alongZ ? box.Min.Z : box.Min.X, a1 = alongZ ? box.Max.Z : box.Max.X;
            double c0 = alongZ ? box.Min.X : box.Min.Z, c1 = alongZ ? box.Max.X : box.Max.Z;
            double h = box.Max.Y - box.Min.Y;
            int n = (int)Math.Round(h / SegmentRise);
            int across = Math.Max(1, (int)Math.Round((c1 - c0) / w.Tiles.Config.TileSize));
            double run = (a1 - a0) / n, wide = (c1 - c0) / across;
            var ids = new int[n * across];
            for (int k = 0; k < n; k++)
            {
                // Segment k climbs from k * rise to (k + 1) * rise, counted from the low end.
                double s0 = up ? a0 + run * k : a1 - run * (k + 1), s1 = up ? a0 + run * (k + 1) : a1 - run * k;
                if (k == n - 1) { if (up) s1 = a1; else s0 = a0; }
                if (k == 0) { if (up) s0 = a0; else s1 = a1; }
                double y0 = box.Min.Y + SegmentRise * k, y1 = k == n - 1 ? box.Max.Y : box.Min.Y + SegmentRise * (k + 1);
                for (int j = 0; j < across; j++)
                {
                    double q0 = c0 + wide * j, q1 = j == across - 1 ? c1 : c0 + wide * (j + 1);
                    var seg = alongZ ? new Aabb(new Vec3d(q0, y0, s0), new Vec3d(q1, y1, s1)) : new Aabb(new Vec3d(s0, y0, q0), new Vec3d(s1, y1, q1));
                    int id = w.Tiles.AddWedgeTile(seg, rise, kind);
                    ids[k * across + j] = id;
                    info.Tiles.Add(id);
                }
            }
            info.Climbs.Add(Brush.Wedge(box, rise, kind == TileKind.Stair ? BrushKind.Stairs : BrushKind.Ramp));
            info.ClimbTiles.Add(ids);
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
                Climb(w, info, new Aabb(lo, hi), rise, TileKind.Stair);
                // Open above the middle cell, and no walls across the run.
                if (alongZ) b.Hole[s, lane, cb] = true; else b.Hole[s, cb, lane] = true;
                foreach (int line in new[] { Math.Min(cb, ca) == cb ? cb + 1 : cb, Math.Min(cb, cc) == cb ? cb + 1 : cb })
                {
                    if (alongZ) b.AlongX[s, line, lane] = WallKind.Open;
                    else b.AlongZ[s, line, lane] = WallKind.Open;
                }
            }
        }

        /// <summary>
        /// Puts up a building: tile walls, floor tiles, then the frame as followers of them (beam strips over the wall
        /// lines, column pieces one tile row tall, nodes where strips and columns meet in a slab).
        /// </summary>
        private static void Raise(BrushWorld w, GreyboxInfo info, Building b, string name)
        {
            TileWalls t = w.Tiles;
            double ts = t.Config.TileSize;
            // Walls.
            for (int s = 0; s < b.Storeys; s++)
            {
                double y = s * StoreyHeight;
                for (int j = 0; j <= b.Mz; j++)
                    for (int i = 0; i < b.Mx; i++)
                        Segment(t, info, b, s, true, j, i, new Vec3d(b.Cx(i) + Column, y, b.Cz(j)));
                for (int i = 0; i <= b.Mx; i++)
                    for (int j = 0; j < b.Mz; j++)
                        Segment(t, info, b, s, false, i, j, new Vec3d(b.Cx(i), y, b.Cz(j) + Column));
            }
            // Floor tiles: 4 x 4 in every cell of every slab that isn't a hole.
            for (int s = 0; s < b.Storeys; s++)
            {
                double y1 = (s + 1) * StoreyHeight, y0 = y1 - Column;
                for (int i = 0; i < b.Mx; i++)
                    for (int j = 0; j < b.Mz; j++)
                    {
                        if (b.Hole[s, i, j]) continue;
                        for (int a = 0; a < 4; a++)
                            for (int c = 0; c < 4; c++)
                            {
                                var lo = new Vec3d(b.Cx(i) + Column + a * ts, y0, b.Cz(j) + Column + c * ts);
                                int id = t.AddTile(new Aabb(lo, new Vec3d(lo.X + ts, y1, lo.Z + ts)), Dir.Up, TileKind.Floor);
                                b.FloorTiles[(s, i, j, a, c)] = id;
                                info.Tiles.Add(id);
                                info.Floors.Add(id);
                            }
                    }
            }
            var links = new List<int>(6);
            int Wall(int s, bool alongX, int line, int seg, int row, int piece) =>
                s >= 0 && s < b.Storeys && b.WallTiles.TryGetValue((s, alongX, line, seg, row, piece), out int id) ? id : -1;
            int Floor(int s, int i, int j, int a, int c) =>
                i >= 0 && j >= 0 && i < b.Mx && j < b.Mz && b.FloorTiles.TryGetValue((s, i, j, a, c), out int id) ? id : -1;
            void Add(int id) { if (id >= 0) links.Add(id); }
            int Follower(Aabb box)
            {
                int id = t.AddFollower(box, links);
                info.Tiles.Add(id);
                info.Frame.Add(id);
                return id;
            }
            // Beam strips in each slab over every column line next to a floored cell: four 1.5 m pieces per segment,
            // each linked to the floor tiles on both sides and the wall tiles under and over it.
            var stripX = new Dictionary<(int, int, int, int), int>(); // (slab, z-line j, segment i, piece)
            var stripZ = new Dictionary<(int, int, int, int), int>(); // (slab, x-line i, segment j, piece)
            for (int s = 0; s < b.Storeys; s++)
            {
                double y1 = (s + 1) * StoreyHeight, y0 = y1 - Column;
                for (int j = 0; j <= b.Mz; j++)
                    for (int i = 0; i < b.Mx; i++)
                    {
                        if (Floor(s, i, j - 1, 0, 0) < 0 && Floor(s, i, j, 0, 0) < 0) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            links.Clear();
                            Add(Floor(s, i, j - 1, k, 3)); Add(Floor(s, i, j, k, 0));
                            Add(Wall(s, true, j, i, 2, k)); Add(Wall(s + 1, true, j, i, 0, k));
                            var lo = new Vec3d(b.Cx(i) + Column + k * ts, y0, b.Cz(j));
                            stripX[(s, j, i, k)] = Follower(new Aabb(lo, new Vec3d(lo.X + ts, y1, lo.Z + Column)));
                        }
                    }
                for (int i = 0; i <= b.Mx; i++)
                    for (int j = 0; j < b.Mz; j++)
                    {
                        if (Floor(s, i - 1, j, 0, 0) < 0 && Floor(s, i, j, 0, 0) < 0) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            links.Clear();
                            Add(Floor(s, i - 1, j, 3, k)); Add(Floor(s, i, j, 0, k));
                            Add(Wall(s, false, i, j, 2, k)); Add(Wall(s + 1, false, i, j, 0, k));
                            var lo = new Vec3d(b.Cx(i), y0, b.Cz(j) + Column + k * ts);
                            stripZ[(s, i, j, k)] = Follower(new Aabb(lo, new Vec3d(lo.X + Column, y1, lo.Z + ts)));
                        }
                    }
            }
            // Columns at every grid point a wall touches, one piece per tile row, linked to the wall tiles beside it in
            // that row; then a node in each slab, linked to the strips around it and the column pieces under and over it.
            var colPiece = new Dictionary<(int, int, int, int), int>(); // (i, j, storey, row)
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
                    for (int s = 0; s < b.Storeys; s++)
                        for (int r = 0; r < 3; r++)
                        {
                            links.Clear();
                            Add(Wall(s, true, j, i - 1, r, 3)); Add(Wall(s, true, j, i, r, 0));
                            Add(Wall(s, false, i, j - 1, r, 3)); Add(Wall(s, false, i, j, r, 0));
                            var lo = new Vec3d(b.Cx(i), s * StoreyHeight + r * ts, b.Cz(j));
                            colPiece[(i, j, s, r)] = Follower(new Aabb(lo, lo + new Vec3d(Column, ts, Column)));
                        }
                }
            for (int s = 0; s < b.Storeys; s++)
            {
                double y1 = (s + 1) * StoreyHeight, y0 = y1 - Column;
                for (int i = 0; i <= b.Mx; i++)
                    for (int j = 0; j <= b.Mz; j++)
                    {
                        links.Clear();
                        if (stripX.TryGetValue((s, j, i - 1, 3), out int a1)) links.Add(a1);
                        if (stripX.TryGetValue((s, j, i, 0), out int a2)) links.Add(a2);
                        if (stripZ.TryGetValue((s, i, j - 1, 3), out int a3)) links.Add(a3);
                        if (stripZ.TryGetValue((s, i, j, 0), out int a4)) links.Add(a4);
                        bool column = colPiece.TryGetValue((i, j, s, 2), out int under);
                        if (links.Count == 0 && !column) continue;
                        if (column) links.Add(under);
                        if (colPiece.TryGetValue((i, j, s + 1, 0), out int over)) links.Add(over);
                        var lo = new Vec3d(b.Cx(i), y0, b.Cz(j));
                        Follower(new Aabb(lo, new Vec3d(lo.X + Column, y1, lo.Z + Column)));
                    }
            }
            info.Buildings.Add(b.Bounds);
            info.Names.Add(name);
        }

        private static bool Solidish(WallKind k) => k != WallKind.None && k != WallKind.Open;

        /// <summary>One 4 x 3 tile segment with the openings its kind asks for.</summary>
        private static void Segment(TileWalls t, GreyboxInfo info, Building bd, int storey, bool alongX, int line, int seg, Vec3d corner)
        {
            WallKind kind = alongX ? bd.AlongX[storey, line, seg] : bd.AlongZ[storey, line, seg];
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
                    int id = t.AddTile(new Aabb(lo, hi), alongX ? Dir.North : Dir.East);
                    bd.WallTiles[(storey, alongX, line, seg, r, k)] = id;
                    info.Tiles.Add(id);
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
