using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Buildings and site furniture for the West segment's construction site (<see cref="WestSegment"/>): unfinished
    /// frame towers with a concrete stair core, finished apartment blocks with sealed flats, site containers, cement
    /// silos, stacked materials (crates you can stand on), scaffold bridges and site hoarding. Everything is breakable
    /// tiles. Owned by the West segment thread (the shared kit is <see cref="BrKit"/>).
    /// </summary>
    public static class WestKit
    {
        private const double Col = GreyboxBlock.Column, M = GreyboxBlock.Module, SH = GreyboxBlock.StoreyHeight;

        /// <summary>A room with no way in (no door, window or open wall on any side, slabs over and under it): breach loot.</summary>
        public struct SealedRoom
        {
            public GreyboxBlock.Building Building;
            public int Storey;
            public (int i, int j)[] Cells;
            public string Name;
        }

        /// <summary>A tower roof (or a high floor) the tests check you can walk to from the spawn region.</summary>
        public struct Lookout
        {
            public Vec3d At;
            public string Name;
        }

        /// <summary>A scaffold bridge: its deck (the tests walk across it).</summary>
        public struct Span
        {
            public Aabb Deck;
            public bool AlongX;
        }

        // ---------------------------------------------------------------- frame towers

        /// <summary>
        /// An unfinished frame tower: <paramref name="storeys"/> storeys of <paramref name="mx"/> x <paramref name="mz"/>
        /// modules (mx 3 or 4, mz 3) on level ground at <paramref name="y"/> (cut the footing first). A concrete stair
        /// core rises the whole height in the west lane (east lane if <paramref name="coreEast"/>), walled on its outer
        /// sides, with a switchback stair to the roof. The first <paramref name="clad"/> storeys are clad: walls with
        /// windows, a wide door at the far lane's south end, a shut door north, and a sealed site store (chest) in the
        /// far lane's north cell of the ground floor. Above that the floors are open slabs on the bare frame: no walls,
        /// a crate of materials on each, one slab with a missing bay (a drop through). A chest on every floor above the
        /// ground, alternating ends of the far lane; a parapet and a chest on the roof.
        /// </summary>
        public static GreyboxBlock.Building FrameTower(BrBuild b, List<SealedRoom> sealedRooms, List<Lookout> lookouts,
            double x, double y, double z, int mx, int mz, int storeys, int clad, bool coreEast, string name)
        {
            var t = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            int core = coreEast ? mx - 1 : 0, side = coreEast ? mx - 1 : 1, far = coreEast ? 0 : mx - 1, mid = coreEast ? mx - 2 : 1;
            int outerFar = coreEast ? 0 : mx;
            int storeWall = coreEast ? 1 : mx - 1; // the line between the far lane and the next
            b.Stairwell(t, core, true, +1);
            for (int s = 0; s < storeys; s++)
            {
                // The core's inner wall: open where the stairs start and arrive (like the shared tower).
                for (int j = 0; j < mz; j++) t.AlongZ[s, side, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                if (s >= clad)
                {
                    // The bare frame: only the core keeps its walls.
                    for (int i = 0; i < mx; i++)
                    {
                        if (i == core) continue;
                        t.AlongX[s, 0, i] = WallKind.None;
                        t.AlongX[s, mz, i] = WallKind.None;
                    }
                    for (int j = 0; j < mz; j++) t.AlongZ[s, outerFar, j] = WallKind.None;
                    continue;
                }
                for (int i = 0; i < mx; i++)
                {
                    if (i == core) continue;
                    t.AlongX[s, 0, i] = (i + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
                    t.AlongX[s, mz, i] = (i + s) % 2 == 1 ? WallKind.Window : WallKind.Wall;
                }
                for (int j = 0; j < mz; j++) t.AlongZ[s, outerFar, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
            }
            // Ground floor: doors, and the sealed store in the far lane's north cell.
            for (int i = 0; i < mx; i++) if (i != core) t.AlongX[0, 0, i] = WallKind.Window;
            for (int j = 0; j < mz; j++) t.AlongZ[0, outerFar, j] = WallKind.Window;
            t.AlongX[0, 0, far] = WallKind.WideDoor;
            for (int i = 0; i < mx; i++) if (i != core) t.AlongX[0, mz, i] = WallKind.Wall;
            t.AlongX[0, mz, mid] = WallKind.ShutDoor;
            t.AlongX[0, mz - 1, far] = WallKind.Wall;
            t.AlongZ[0, storeWall, mz - 1] = WallKind.Wall;
            t.AlongZ[0, outerFar, mz - 1] = WallKind.Wall;
            // A missing bay in the slab under the top storey: a drop through, and the way round the stair hole goes
            // through the far lane.
            if (storeys >= 5) t.Hole[storeys - 2, mid, 1] = true;
            b.Raise(t, name);
            BrKit.Parapet(b, t);
            sealedRooms.Add(new SealedRoom { Building = t, Storey = 0, Cells = new[] { (far, mz - 1) }, Name = name + " site store" });

            b.Chest(t, far, mz - 1, 0, Math.PI);
            for (int s = 1; s < storeys; s++) b.Chest(t, far, s % 2 == 1 ? 0 : mz - 1, s, s % 2 == 1 ? 0 : Math.PI);
            b.Chest(t, far, 1, storeys, coreEast ? Math.PI / 2 : -Math.PI / 2);
            // Materials on the open floors (cover in the middle of the far lane), two stacks on the roof.
            for (int s = clad; s < storeys; s++) CellCrate(b, t, far, 1, s, 1.5, 3.0, 1);
            CellCrate(b, t, mid, 0, storeys, 3.0, 1.5, 1);
            CellCrate(b, t, mid, mz - 1, storeys, 1.5, 1.5, 2);
            lookouts.Add(new Lookout { At = new Vec3d(t.Cx(far) + Col + 3, t.Top, t.Cz(1) + Col + 1), Name = name + " roof" });
            return t;
        }

        /// <summary>A crate centred in building cell (i, j) on storey <paramref name="storey"/> (the roof is storey = Storeys).</summary>
        public static void CellCrate(BrBuild b, GreyboxBlock.Building t, int i, int j, int storey, double sx, double sz, int rows)
        {
            double cx = t.Cx(i) + Col + 3, cz = t.Cz(j) + Col + 3;
            Crate(b, cx - sx / 2, t.Y0 + storey * SH, cz - sz / 2, sx, sz, rows);
        }

        // ---------------------------------------------------------------- apartments

        /// <summary>
        /// A finished apartment block, 4 x 3 modules, <paramref name="storeys"/> storeys: the stair core in the west
        /// lane, a corridor down the next lane (lobby on the ground floor: a wide door south, a shut door north), and
        /// three flats per floor across the east half, each two bays with windows. One flat per floor is sealed (no door,
        /// no window): break in from the corridor or the next flat. A chest in every sealed flat, one in an open flat on
        /// every other floor, one on the roof (parapet).
        /// </summary>
        public static GreyboxBlock.Building Apartments(BrBuild b, List<SealedRoom> sealedRooms, List<Lookout> lookouts,
            double x, double y, double z, int storeys, int seed, string name)
        {
            const int mx = 4, mz = 3;
            var t = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            b.Stairwell(t, 0, true, +1);
            var sealedJ = new int[storeys];
            for (int s = 0; s < storeys; s++)
            {
                int sj = sealedJ[s] = (s + seed) % 3;
                for (int j = 0; j < mz; j++)
                {
                    t.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;     // core | corridor
                    t.AlongZ[s, 2, j] = j == sj ? WallKind.Wall : WallKind.Door;    // corridor | flat
                    t.AlongZ[s, 4, j] = j == sj ? WallKind.Wall : WallKind.Window;  // east face
                }
                for (int i = 2; i < 4; i++)
                {
                    t.AlongX[s, 1, i] = WallKind.Wall;                              // between flats
                    t.AlongX[s, 2, i] = WallKind.Wall;
                    t.AlongX[s, 0, i] = sj == 0 ? WallKind.Wall : WallKind.Window;
                    t.AlongX[s, 3, i] = sj == 2 ? WallKind.Wall : WallKind.Window;
                }
                t.AlongX[s, 0, 1] = s == 0 ? WallKind.WideDoor : WallKind.Window;
                t.AlongX[s, 3, 1] = s == 0 ? WallKind.ShutDoor : WallKind.Window;
                sealedRooms.Add(new SealedRoom { Building = t, Storey = s, Cells = new[] { (2, sj), (3, sj) }, Name = $"{name} sealed flat, floor {s}" });
            }
            b.Raise(t, name);
            BrKit.Parapet(b, t);
            for (int s = 0; s < storeys; s++)
            {
                b.Chest(t, 3, sealedJ[s], s, -Math.PI / 2);
                if (s % 2 == 0) b.Chest(t, 3, (sealedJ[s] + 1) % 3, s, -Math.PI / 2);
            }
            b.Chest(t, 2, 1, storeys, Math.PI / 2);
            CellCrate(b, t, 3, 0, storeys, 3.0, 1.5, 1);
            CellCrate(b, t, 3, 2, storeys, 3.0, 1.5, 1);
            lookouts.Add(new Lookout { At = new Vec3d(t.Cx(2) + Col + 3, t.Top, t.Cz(1) + Col + 1), Name = name + " roof" });
            return t;
        }

        // ---------------------------------------------------------------- small buildings

        /// <summary>
        /// A site container (one storey, two modules long): a shut door at its west (or south) end and a chest inside,
        /// or sealed with a chest (breach it).
        /// </summary>
        public static GreyboxBlock.Building Container(BrBuild b, List<SealedRoom> sealedRooms, double x, double y, double z, bool alongX, bool door, int colour)
        {
            var t = alongX ? new GreyboxBlock.Building(x, z, 2, 1, 1) : new GreyboxBlock.Building(x, z, 1, 2, 1);
            t.Y0 = y;
            if (door)
            {
                if (alongX) t.AlongZ[0, 0, 0] = WallKind.ShutDoor; else t.AlongX[0, 0, 0] = WallKind.ShutDoor;
            }
            BrStyle was = b.Style; int wasV = b.Variant;
            b.Style = BrStyle.Steel; b.Variant = colour;
            b.Raise(t, door ? "Site container" : "Sealed container");
            b.Style = was; b.Variant = wasV;
            if (alongX) b.Chest(t, 1, 0, 0, -Math.PI / 2); else b.Chest(t, 0, 1, 0, Math.PI);
            if (!door) sealedRooms.Add(new SealedRoom { Building = t, Storey = 0, Cells = alongX ? new[] { (0, 0), (1, 0) } : new[] { (0, 0), (0, 1) }, Name = "Sealed container" });
            return t;
        }

        /// <summary>A cement silo: one module, <paramref name="storeys"/> storeys tall, sealed, a chest at the bottom.</summary>
        public static GreyboxBlock.Building Silo(BrBuild b, List<SealedRoom> sealedRooms, double x, double y, double z, int storeys)
        {
            var t = new GreyboxBlock.Building(x, z, 1, 1, storeys) { Y0 = y };
            b.Raise(t, "Cement silo");
            b.Chest(t, 0, 0, 0, 0);
            sealedRooms.Add(new SealedRoom { Building = t, Storey = 0, Cells = new[] { (0, 0) }, Name = "Cement silo" });
            return t;
        }

        // ---------------------------------------------------------------- materials, decks, hoarding

        /// <summary>
        /// A stack of materials: a closed box of tiles <paramref name="sx"/> x <paramref name="sz"/>, <paramref name="rows"/>
        /// tile rows tall plus a lid you can stand on (one row: 1.8 m, full cover you can climb; two rows: 3.3 m, climb it
        /// from a one-row stack). Its corner at (x, y, z) on level ground or a floor.
        /// </summary>
        public static void Crate(BrBuild b, double x, double y, double z, double sx, double sz, int rows)
        {
            double th = b.World.Tiles.Config.TileThickness;
            b.Strip(new Vec3d(x, y, z), sx, true, rows);
            b.Strip(new Vec3d(x, y, z + sz - th), sx, true, rows);
            b.Strip(new Vec3d(x, y, z + th), sz - 2 * th, false, rows);
            b.Strip(new Vec3d(x + sx - th, y, z + th), sz - 2 * th, false, rows);
            double top = y + rows * b.World.Tiles.Config.TileSize;
            Deck(b, x, z, x + sx, z + sz, top + th);
        }

        /// <summary>Floor tiles (about 1.5 m, equal pieces) over the rectangle, their top at <paramref name="top"/>.</summary>
        public static void Deck(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            double s = b.World.Tiles.Config.TileSize, th = b.World.Tiles.Config.TileThickness;
            double sx = x1 - x0, sz = z1 - z0;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int a = 0; a < nx; a++)
                for (int c = 0; c < nz; c++)
                {
                    var box = new Aabb(new Vec3d(x0 + sx * a / nx, top - th, z0 + sz * c / nz), new Vec3d(x0 + sx * (a + 1) / nx, top, z0 + sz * (c + 1) / nz));
                    int id = b.World.Tiles.AddTile(box, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
        }

        /// <summary>
        /// A scaffold bridge between two buildings: a 3 m deck at <paramref name="top"/> over the rectangle, with a one-row
        /// rail (chest-high cover) along one long side (<paramref name="railHigh"/>: the +z or +x side).
        /// </summary>
        public static Span Bridge(BrBuild b, double x0, double z0, double x1, double z1, double top, bool alongX, bool railHigh)
        {
            Deck(b, x0, z0, x1, z1, top);
            double th = b.World.Tiles.Config.TileThickness;
            if (alongX) b.Strip(new Vec3d(x0, top, railHigh ? z1 - th : z0), x1 - x0, true, 1);
            else b.Strip(new Vec3d(railHigh ? x1 - th : x0, top, z0), z1 - z0, false, 1);
            var deck = new Aabb(new Vec3d(x0, top - th, z0), new Vec3d(x1, top, z1));
            b.Info.Bridges.Add(deck);
            return new Span { Deck = deck, AlongX = alongX };
        }

        /// <summary>Site hoarding: a wall three rows (4.5 m) tall, too tall to climb, from (x0, z0) to (x1, z1) along one axis.</summary>
        public static void Hoarding(BrBuild b, double x0, double z0, double x1, double z1, double y)
        {
            bool alongX = Math.Abs(z1 - z0) < 1e-9;
            double len = alongX ? x1 - x0 : z1 - z0;
            if (len <= 0.5) return;
            b.Strip(new Vec3d(x0, y, z0), len, alongX, 3);
        }
    }
}
