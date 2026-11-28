using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The South segment's own building kit (the market town, see <see cref="SouthSegment"/>). Townhouse rows with party
    /// walls, courtyard houses, walled gardens, stairs on either axis. Everything goes through <see cref="BrBuild"/>, and
    /// every chest through <see cref="Chest"/>, which keeps the home POI's count exact: a chest within the home radius
    /// must be asked for as a home chest, every other one must stand clear of it.
    /// </summary>
    internal sealed class SouthKit
    {
        public const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, SH = GreyboxBlock.StoreyHeight;

        /// <summary>The span of <paramref name="n"/> modules, walls included.</summary>
        public static double W(int n) => n * M + Col;

        /// <summary>Which side of a row the street is on (its front doors).</summary>
        public enum Face { South, North, West, East }

        public readonly BrBuild B;
        public int HomeChests;
        /// <summary>Every chest placed, with what it was (for the tests' design checks).</summary>
        public readonly List<(Vec3d At, string What)> Placed = new List<(Vec3d, string)>();

        public SouthKit(BrBuild b) { B = b; }

        // ---------------------------------------------------------------- loot

        /// <summary>A chest; <paramref name="home"/> ones must stand inside the home radius, every other one clear of it.</summary>
        public void Chest(Vec3d at, double yaw, string what, bool home = false)
        {
            double d = Math.Sqrt((at.X - B.Anchor.X) * (at.X - B.Anchor.X) + (at.Z - B.Anchor.Z) * (at.Z - B.Anchor.Z));
            if (home && d > BrLayout.HomeRadius - 0.5) throw new InvalidOperationException($"South: home chest '{what}' at {at} is {d:0.0} m from the anchor");
            if (!home && d < BrLayout.HomeRadius + 0.5) throw new InvalidOperationException($"South: chest '{what}' at {at} is {d:0.0} m from the anchor (inside the home POI)");
            B.Chest(at, yaw);
            Placed.Add((at, what));
            if (home) HomeChests++;
        }

        /// <summary>The middle of cell (i, j) on storey <paramref name="storey"/> (feet).</summary>
        public static Vec3d CellMid(GreyboxBlock.Building t, int i, int j, int storey) =>
            new Vec3d(t.Cx(i) + Col + 3.0, t.Y0 + storey * SH, t.Cz(j) + Col + 3.0);

        public void Chest(GreyboxBlock.Building t, int i, int j, int storey, double yaw, string what, bool home = false) =>
            Chest(CellMid(t, i, j, storey), yaw, what, home);

        // ---------------------------------------------------------------- placing

        /// <summary>Throws (a design error) unless the rectangle grown by <paramref name="street"/> is clear (see <see cref="BrBuild.IsClear"/>).</summary>
        public void NeedClear(Rect2 r, double street, string what)
        {
            if (!B.IsClear(r, street)) throw new InvalidOperationException($"South: {what} at {r} is not clear");
        }

        /// <summary>A building's plan rectangle.</summary>
        public static Rect2 RectOf(double x0, double z0, int mx, int mz) => new Rect2(x0, z0, x0 + W(mx), z0 + W(mz));

        /// <summary>Cuts a level footing for a whole group of buildings at <paramref name="want"/> (snapped by the footings).</summary>
        public double Level(Rect2 r, double apron, double want, string what)
        {
            if (!B.TryFoot(r.X0, r.Z0, r.X1, r.Z1, apron, out double y, want))
            {
                var why = new System.Text.StringBuilder();
                Footings f = B.Footings;
                for (int i = 0; i < f.Count; i++)
                {
                    Footings.Footing o = f[i];
                    double gap = Footings.RectGap(r.X0, r.Z0, r.X1, r.Z1, o.X0, o.Z0, o.X1, o.Z1) - apron - o.Apron;
                    if (gap < 8) why.Append($" [{o.X0:0},{o.Z0:0}..{o.X1:0},{o.Z1:0} at {o.Level:0.0}, gap {gap:0.0}]");
                }
                B.FootAllowed(r.X0, r.Z0, r.X1, r.Z1, apron, out double lo, out double hi);
                why.Append($" allowed {lo:0.00}..{hi:0.00}");
                throw new InvalidOperationException($"South: no level footing for {what} at {r} (natural {B.MeanNatural(r.X0, r.Z0, r.X1, r.Z1):0.0}); near:{why}");
            }
            return y;
        }

        public void Raise(GreyboxBlock.Building t, string name, BrStyle style, int variant)
        {
            B.Style = style;
            B.Variant = variant;
            int before = B.World.Tiles.Count;
            B.Raise(t, name);
            Cost.Add((name, B.World.Tiles.Count - before));
        }

        /// <summary>Tiles each raised building cost (walls, floors, frame; stairs are counted where they were built).</summary>
        public readonly List<(string Name, int Tiles)> Cost = new List<(string, int)>();

        /// <summary>The kit of the last build (for the South tests).</summary>
        public static SouthKit Last;

        // ---------------------------------------------------------------- stairs

        /// <summary>
        /// A switchback stairwell over three cells in a line (cells <paramref name="first"/>..first + 2), along either
        /// axis, like <see cref="GreyboxBlock.Stairwell"/> (a copy that also runs along X from any cell): each flight
        /// starts in the far cell, crosses the middle one (open through the slab) and arrives at the near cell's floor.
        /// Flights for storeys 0..<paramref name="flights"/> - 1 (default all, so the last one reaches the roof).
        /// </summary>
        public void Stairs(GreyboxBlock.Building b, int lane, bool alongZ, int dir, int first = 0, bool flip = false, int flights = -1)
        {
            int n = alongZ ? b.Mz : b.Mx;
            if (first < 0 || first + 3 > n) throw new ArgumentException("South: stairs need three cells in a line.");
            int ca = first + (dir > 0 ? 2 : 0), cb = first + 1;
            double Line(int k) => alongZ ? b.Cz(k) : b.Cx(k);
            double high = dir > 0 ? Line(ca) : Line(ca + 1) + Col;
            double low = high - dir * GreyboxBlock.StairRun;
            double laneLo = (alongZ ? b.Cx(lane) : b.Cz(lane)) + Col;
            int last = flights < 0 ? b.Storeys : Math.Min(flights, b.Storeys);
            for (int s = 0; s < last; s++)
            {
                double y0 = b.Y0 + s * SH, y1 = y0 + SH;
                double l0 = laneLo + (flip ? 1 - s % 2 : s % 2) * GreyboxBlock.StairWidth, l1 = l0 + GreyboxBlock.StairWidth;
                double a0 = Math.Min(low, high), a1 = Math.Max(low, high);
                Vec3d lo = alongZ ? new Vec3d(l0, y0, a0) : new Vec3d(a0, y0, l0);
                Vec3d hi = alongZ ? new Vec3d(l1, y1, a1) : new Vec3d(a1, y1, l1);
                Dir rise = alongZ ? (dir > 0 ? Dir.North : Dir.South) : (dir > 0 ? Dir.East : Dir.West);
                B.Climb(new Aabb(lo, hi), rise);
                if (alongZ) b.Hole[s, lane, cb] = true; else b.Hole[s, cb, lane] = true;
                foreach (int line in new[] { cb, cb + 1 })
                {
                    if (alongZ) b.AlongX[s, line, lane] = WallKind.Open;
                    else b.AlongZ[s, line, lane] = WallKind.Open;
                }
            }
        }

        // ---------------------------------------------------------------- townhouse rows

        /// <summary>
        /// A terrace of townhouses one module wide and <paramref name="depth"/> deep, party walls between them on every
        /// storey, fronts on the street side (<paramref name="face"/>). <paramref name="roles"/> gives each house in order
        /// along the row (west to east, or south to north): a letter, an optional width in modules (1 or 2, default 1)
        /// and an optional '*' for a chest, e.g. "H2*":
        /// S stair house up to the roof, T stair house to the top floor, H house, P shop (wide open front), X sealed
        /// (no way in at all), A carriage arch (open through to the back). Stair houses need depth 3; in a two-module
        /// house the stair takes its first lane. Upstairs, a house beside a stair house is joined to it by a door unless
        /// it holds a chest: then its upper floor is a loot room you breach into through a party wall. Chests stand in
        /// the back cell of the house's last lane (top floor for S, T and H, ground for P and X). A row with an S gets a
        /// parapet round its roof.
        /// </summary>
        public GreyboxBlock.Building Row(double x0, double z0, double y, int storeys, Face face, string roles, uint seed,
                                         int depth = 3, string name = "Town houses", BrStyle style = BrStyle.Brick, int variant = 0, bool backDoors = true)
        {
            var houses = ParseRoles(roles);
            int n = 0;
            foreach (var hs in houses) n += hs.Width;
            bool ns = face == Face.South || face == Face.North;
            var t = ns ? new GreyboxBlock.Building(x0, z0, n, depth, storeys) : new GreyboxBlock.Building(x0, z0, depth, n, storeys);
            t.Y0 = y;
            NeedClear(RectOf(x0, z0, t.Mx, t.Mz), 0.5, name);
            int frontLine = face == Face.South || face == Face.West ? 0 : depth, backLine = depth - frontLine;
            // Cell of module lane m at depth d (0 = front).
            (int i, int j) Cell(int m, int d)
            {
                int dd = face == Face.South || face == Face.West ? d : depth - 1 - d;
                return ns ? (m, dd) : (dd, m);
            }
            void Front(int s, int m, WallKind w) { if (ns) t.AlongX[s, frontLine, m] = w; else t.AlongZ[s, frontLine, m] = w; }
            void Back(int s, int m, WallKind w) { if (ns) t.AlongX[s, backLine, m] = w; else t.AlongZ[s, backLine, m] = w; }
            void Across(int s, int line, int d, WallKind w) { if (ns) t.AlongZ[s, line, Cell(0, d).j] = w; else t.AlongX[s, line, Cell(0, d).i] = w; }
            void Inner(int s, int m, int d, WallKind w)
            {
                // The wall between depth cell d and d + 1 of lane m.
                int line = face == Face.South || face == Face.West ? d + 1 : depth - 1 - d;
                if (ns) t.AlongX[s, line, m] = w; else t.AlongZ[s, line, m] = w;
            }
            bool IsStair(char r) => r == 'S' || r == 'T';
            uint h = seed;
            uint Next() { h = BrKit.Hash((int)h, 17, 0x5u + seed); return h; }

            for (int k = 0; k < houses.Count; k++)
            {
                var hs = houses[k];
                char r = hs.Role;
                bool stair = IsStair(r);
                if (stair && depth < 3) throw new ArgumentException("South: a stair house needs depth 3.");
                // A one-module stair house has no way round its stairwell upstairs: only one flight.
                if (stair && hs.Width == 1 && (r == 'S' ? storeys : storeys - 1) > 1) throw new ArgumentException($"South: {name}: a one-module stair house climbs one storey only.");
                if (k > 0) for (int s = 0; s < storeys; s++) for (int d = 0; d < depth; d++) Across(s, hs.Start, d, WallKind.Wall); // party wall
                for (int lane = 0; lane < hs.Width; lane++)
                {
                    int m = hs.Start + lane;
                    bool main = lane == 0;
                    // Ground floor front and back.
                    WallKind front = r == 'P' ? WallKind.WideDoor
                                   : r == 'A' ? (main ? WallKind.WideDoor : WallKind.Wall)
                                   : r == 'X' ? WallKind.Wall
                                   : main ? WallKind.ShutDoor : (Next() & 1) == 0 ? WallKind.Window : WallKind.Wall;
                    Front(0, m, front);
                    WallKind back = r == 'A' && main ? WallKind.WideDoor
                                  : r == 'X' ? WallKind.Wall
                                  : r == 'P' || !main ? ((Next() & 3) == 0 ? WallKind.Window : WallKind.Wall)
                                  : backDoors && (Next() & 3) != 0 ? WallKind.Door : WallKind.Window;
                    Back(0, m, back);
                    for (int s = 1; s < storeys; s++)
                    {
                        Front(s, m, (Next() & 7) == 0 ? WallKind.Wall : WallKind.Window);
                        Back(s, m, (Next() & 1) == 0 ? WallKind.Window : WallKind.Wall);
                    }
                    // A window over every door of a tall house (a wall solid from the door's lintel to the parapet is
                    // more than the walking grid looks through: bots would never find the door).
                    if (storeys >= 3 && (front == WallKind.ShutDoor || front == WallKind.WideDoor)) Front(1, m, WallKind.Window);
                    // A partition between the front room and the rest in some lanes (never across a stair or an arch).
                    if (!(stair && main) && r != 'A' && depth >= 2)
                        for (int s = 0; s < storeys; s++)
                            if ((Next() % 3) == 0) Inner(s, m, 0, WallKind.Door);
                }
                if (stair)
                {
                    bool southOrWest = face == Face.South || face == Face.West;
                    Stairs(t, hs.Start, ns, southOrWest ? +1 : -1, southOrWest ? 0 : depth - 3, (hs.Start & 1) == 1, r == 'S' ? storeys : storeys - 1);
                }
            }
            // Upstairs doors between a stair house and a neighbour without a chest, in the back cell (clear of flights).
            for (int k = 1; k < houses.Count; k++)
            {
                var a = houses[k - 1];
                var c = houses[k];
                bool join = (IsStair(a.Role) && !IsStair(c.Role) && c.Role != 'X' && !(c.Chest && c.Role == 'H')) ||
                            (IsStair(c.Role) && !IsStair(a.Role) && a.Role != 'X' && !(a.Chest && a.Role == 'H'));
                if (!join) continue;
                for (int s = 1; s < storeys; s++) Across(s, c.Start, depth - 1, WallKind.Door);
            }
            // Gable ends: windows upstairs.
            for (int s = 1; s < storeys; s++)
                for (int d = 0; d < depth; d++)
                {
                    if ((Next() & 1) == 0) continue;
                    Across(s, 0, d, WallKind.Window);
                    Across(s, n, d, WallKind.Window);
                }
            Raise(t, name, style, variant);
            bool roof = false;
            foreach (var hs in houses) roof |= hs.Role == 'S';
            if (roof) BrKit.Parapet(B, t);
            // Chests, in the back cell of the house's last lane.
            double yaw = face == Face.South ? 0 : face == Face.North ? Math.PI : face == Face.West ? Math.PI / 2 : -Math.PI / 2;
            foreach (var hs in houses)
            {
                if (!hs.Chest) continue;
                int storey = hs.Role == 'P' || hs.Role == 'X' || storeys == 1 ? 0 : storeys - 1;
                var (ci, cj) = Cell(hs.Start + hs.Width - 1, depth - 1);
                Chest(t, ci, cj, storey, yaw, $"{name} {hs.Role}");
            }
            return t;
        }

        /// <summary>The houses of a row's role string (see <see cref="Row"/>).</summary>
        public static List<(char Role, int Width, bool Chest, int Start)> ParseRoles(string roles)
        {
            var list = new List<(char, int, bool, int)>();
            int start = 0;
            for (int k = 0; k < roles.Length; k++)
            {
                char c = roles[k];
                if (c == ' ') continue;
                int width = 1;
                if (k + 1 < roles.Length && char.IsDigit(roles[k + 1])) { width = roles[k + 1] - '0'; k++; }
                if (width < 1 || width > 2) throw new ArgumentException("South: houses are one or two modules wide.");
                bool chest = k + 1 < roles.Length && roles[k + 1] == '*';
                if (chest) k++;
                list.Add((c, width, chest, start));
                start += width;
            }
            return list;
        }

        /// <summary>Modules a role string spans.</summary>
        public static int RowModules(string roles)
        {
            int n = 0;
            foreach (var hs in ParseRoles(roles)) n += hs.Width;
            return n;
        }

        // ---------------------------------------------------------------- walls

        /// <summary>A wall of <paramref name="rows"/> tiles from (x, z) along X or Z at level y, with an optional gap
        /// (a gate) of <paramref name="gate"/> metres starting <paramref name="gateAt"/> along it.</summary>
        public void Wall(double x, double z, double y, double length, bool alongX, int rows, double gateAt = -1, double gate = 3.0)
        {
            if (gateAt < 0) { B.Strip(new Vec3d(x, y, z), length, alongX, rows); return; }
            if (gateAt > 0.01) B.Strip(new Vec3d(x, y, z), gateAt, alongX, rows);
            double rest = length - gateAt - gate;
            if (rest > 0.01) B.Strip(alongX ? new Vec3d(x + gateAt + gate, y, z) : new Vec3d(x, y, z + gateAt + gate), rest, alongX, rows);
        }

        /// <summary>
        /// A walled rectangle (garden, yard, churchyard) at level y: walls <paramref name="rows"/> tiles tall on the
        /// inside of the rectangle's edge, gates given per side as an offset along it (negative: no gate). Corners are
        /// owned by the south and north walls.
        /// </summary>
        public void Walled(Rect2 r, double y, int rows, double gateS = -1, double gateN = -1, double gateW = -1, double gateE = -1, double gate = 3.0)
        {
            const double th = 0.3;
            double lx = r.X1 - r.X0, lz = r.Z1 - r.Z0;
            Wall(r.X0, r.Z0, y, lx, true, rows, gateS, gate);
            Wall(r.X0, r.Z1 - th, y, lx, true, rows, gateN, gate);
            Wall(r.X0, r.Z0 + th, y, lz - 2 * th, false, rows, gateW, gate);
            Wall(r.X1 - th, r.Z0 + th, y, lz - 2 * th, false, rows, gateE, gate);
        }

        // ---------------------------------------------------------------- courtyard houses

        /// <summary>
        /// A courtyard house (an inn, a merchant's house): <paramref name="mx"/> x <paramref name="mz"/> modules, a ring
        /// of rooms one module deep round an open courtyard (no slabs over it), <paramref name="storeys"/> storeys. A
        /// carriage arch through the middle of the south range (open both ends), a shut door on the north side, doors
        /// from the courtyard into every range, a stair in the west range up to the roof (parapet round it). One corner
        /// room (the north-east) has no door: its chest is breach loot. Chests: the courtyard, the far upstairs room, the
        /// sealed corner.
        /// </summary>
        public GreyboxBlock.Building Courtyard(double x0, double z0, double y, int mx, int mz, int storeys, string name, BrStyle style, int variant, uint seed)
        {
            if (mx < 4 || mz < 5) throw new ArgumentException("South: a courtyard house needs at least 4 x 5 modules.");
            var t = new GreyboxBlock.Building(x0, z0, mx, mz, storeys) { Y0 = y };
            NeedClear(RectOf(x0, z0, mx, mz), 0.5, name);
            uint h = seed;
            uint Next() { h = BrKit.Hash((int)h, 31, 0x9u + seed); return h; }
            // Courtyard: cells 1..mx-2, 1..mz-2 open to the sky.
            for (int s = 0; s < storeys; s++)
                for (int i = 1; i < mx - 1; i++)
                    for (int j = 1; j < mz - 1; j++) t.Hole[s, i, j] = true;
            for (int s = 0; s < storeys; s++)
            {
                // The courtyard's own walls (outer walls of the ranges facing in): windows, doors on the ground floor.
                for (int i = 1; i < mx - 1; i++)
                {
                    bool door = s == 0 && (i == 1 || i == mx - 2);
                    t.AlongX[s, 1, i] = door ? WallKind.Door : WallKind.Window;
                    t.AlongX[s, mz - 1, i] = door ? WallKind.Door : WallKind.Window;
                }
                for (int j = 1; j < mz - 1; j++)
                {
                    t.AlongZ[s, 1, j] = s == 0 && j == mz / 2 ? WallKind.Door : WallKind.Window;
                    t.AlongZ[s, mx - 1, j] = s == 0 && j == 1 ? WallKind.Door : WallKind.Window;
                }
                // Partitions in the ranges: between every second cell.
                for (int i = 2; i < mx - 1; i += 2) { t.AlongZ[s, i, 0] = WallKind.Door; t.AlongZ[s, i, mz - 1] = WallKind.Door; }
                for (int j = 2; j < mz - 1; j += 2) { t.AlongX[s, j, 0] = WallKind.Door; t.AlongX[s, j, mx - 1] = WallKind.Door; }
                // Corners: doors all round the ring upstairs (it is the way from the stair's top to its next flight);
                // on the ground floor the north-east corner room is sealed.
                t.AlongZ[s, 1, 0] = WallKind.Door; t.AlongZ[s, 1, mz - 1] = WallKind.Door;
                t.AlongZ[s, mx - 1, 0] = WallKind.Door;
                t.AlongZ[s, mx - 1, mz - 1] = s == 0 ? WallKind.Wall : WallKind.Door;
                t.AlongX[s, mz - 1, mx - 1] = s == 0 ? WallKind.Wall : WallKind.Door;
                // Outer walls: windows upstairs.
                if (s > 0)
                {
                    for (int i = 0; i < mx; i++) { if ((Next() & 1) == 0) t.AlongX[s, 0, i] = WallKind.Window; if ((Next() & 1) == 0) t.AlongX[s, mz, i] = WallKind.Window; }
                    for (int j = 0; j < mz; j++) { if ((Next() & 1) == 0) t.AlongZ[s, 0, j] = WallKind.Window; if ((Next() & 1) == 0) t.AlongZ[s, mx, j] = WallKind.Window; }
                }
            }
            // Ground floor: the arch through the south range's middle cell, a shut door north.
            int ai = mx / 2;
            t.AlongX[0, 0, ai] = WallKind.WideDoor;
            t.AlongX[0, 1, ai] = WallKind.WideDoor;
            t.AlongZ[0, ai, 0] = WallKind.Wall;
            t.AlongZ[0, ai + 1, 0] = WallKind.Wall;
            t.AlongX[0, mz, 1] = WallKind.ShutDoor;
            // Stairs: the west range, rising north from cell 1.
            Stairs(t, 0, true, +1, 1);
            for (int s = 0; s < storeys; s++) t.AlongZ[s, 1, 2] = WallKind.Wall; // beside the open stair cell
            Raise(t, name, style, variant);
            BrKit.Parapet(B, t);
            Chest(new Vec3d(t.Cx(mx / 2) + 3.3, y, t.Cz(mz / 2) + 3.3), 0, name + " courtyard");
            Chest(t, mx - 1, mz - 1, 0, Math.PI, name + " sealed room");
            Chest(t, mx - 1, 0, storeys - 1, -Math.PI / 2, name + " upstairs");
            return t;
        }
    }
}
