using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Shared buildings for the battle royale segments (framework-owned: segment threads call these but don't change
    /// them; a segment that wants a variant copies it into its own file). All of them reward breaching: sealed loot rooms,
    /// long sides with no door, party walls, compound walls too tall to climb, towers whose fast way up is a hole.
    ///
    /// Positions are map coordinates, levels absolute (put the building on a footing first, <see cref="BrBuild.Foot(GreyboxBlock.Building, double, double)"/>,
    /// or pass the level of a footing you already cut).
    /// </summary>
    public static class BrKit
    {
        private const double Col = GreyboxBlock.Column, M = GreyboxBlock.Module;

        /// <summary>
        /// A row of one-room houses, one storey, one module deep: each room has its own shut door on the front (or on
        /// the east side for a row along z) except room <paramref name="sealedRoom"/>, which has no way in at all and two
        /// chests: break in through the next room's party wall or from outside. The long back has no doors.
        /// </summary>
        public static GreyboxBlock.Building Terrace(BrBuild b, double x, double y, double z, int rooms, bool alongX, int sealedRoom)
        {
            var t = alongX ? new GreyboxBlock.Building(x, z, rooms, 1, 1) : new GreyboxBlock.Building(x, z, 1, rooms, 1);
            t.Y0 = y;
            for (int k = 0; k < rooms; k++)
            {
                if (k > 0)
                {
                    if (alongX) t.AlongZ[0, k, 0] = WallKind.Wall; else t.AlongX[0, k, 0] = WallKind.Wall; // party walls
                }
                if (k == sealedRoom) continue;
                if (alongX) t.AlongX[0, 0, k] = WallKind.ShutDoor; else t.AlongZ[0, 1, k] = WallKind.ShutDoor;
                if (alongX && k % 2 == 0) t.AlongX[0, 1, k] = WallKind.Window; // a few back windows
            }
            b.Raise(t, "Row houses");
            int si = alongX ? sealedRoom : 0, sj = alongX ? 0 : sealedRoom;
            b.Chest(new Vec3d(t.Cx(si) + Col + 1.6, y, t.Cz(sj) + Col + 3), Math.PI / 2);
            b.Chest(new Vec3d(t.Cx(si) + Col + 4.4, y, t.Cz(sj) + Col + 3), -Math.PI / 2);
            return t;
        }

        /// <summary>
        /// A one-storey warehouse, 4 x 3 modules: a wide door and a shut door at its ends only (the long sides have only
        /// two windows: a hole is the short way through). The back row is four storerooms; only the first opens onto the
        /// hall, the other three are sealed with a chest each.
        /// </summary>
        public static GreyboxBlock.Building Warehouse(BrBuild b, double x, double y, double z)
        {
            var t = new GreyboxBlock.Building(x, z, 4, 3, 1) { Y0 = y };
            t.AlongZ[0, 0, 0] = WallKind.WideDoor;  // west end
            t.AlongZ[0, 4, 1] = WallKind.ShutDoor;  // east end
            for (int i = 0; i < 4; i++) t.AlongX[0, 2, i] = i == 0 ? WallKind.Door : WallKind.Wall; // the storerooms' front
            for (int i = 1; i < 4; i++) t.AlongZ[0, i, 2] = WallKind.Wall;                           // between storerooms
            t.AlongX[0, 0, 1] = WallKind.Window;
            t.AlongX[0, 0, 2] = WallKind.Window;
            b.Raise(t, "Warehouse");
            b.Chest(t, 2, 0, 0, 0);
            for (int i = 1; i < 4; i++) b.Chest(t, i, 2, 0, Math.PI);
            return t;
        }

        /// <summary>A two-storey house, 2 x 3 modules: stairs in the east lane, a room on each floor, a chest on each.</summary>
        public static GreyboxBlock.Building House(BrBuild b, double x, double y, double z)
        {
            var t = new GreyboxBlock.Building(x, z, 2, 3) { Y0 = y };
            b.Stairwell(t, 1, true, +1);
            t.AlongZ[0, 0, 1] = WallKind.ShutDoor;  // west door
            t.AlongZ[0, 0, 2] = WallKind.Window;
            t.AlongX[0, 3, 0] = WallKind.Window;    // north
            t.AlongZ[0, 1, 0] = WallKind.Wall;      // room column | stair lane, open in the middle
            t.AlongZ[0, 1, 2] = WallKind.Wall;
            t.AlongZ[1, 1, 0] = WallKind.Wall;
            t.AlongZ[1, 1, 1] = WallKind.Wall;
            t.AlongZ[1, 1, 2] = WallKind.Door;      // where the stairs arrive
            t.AlongZ[1, 0, 0] = WallKind.Window;
            t.AlongZ[1, 0, 2] = WallKind.Window;
            t.AlongX[1, 0, 0] = WallKind.Window;
            b.Raise(t, "House");
            b.Chest(t, 0, 2, 0, Math.PI / 2);
            b.Chest(t, 0, 0, 1, Math.PI / 2);
            return t;
        }

        /// <summary>A sealed one-module shed: no door, no window, a chest inside. Pure breach loot.</summary>
        public static GreyboxBlock.Building Shed(BrBuild b, double x, double y, double z, bool chest = true)
        {
            var t = new GreyboxBlock.Building(x, z, 1, 1, 1) { Y0 = y };
            b.Raise(t, "Shed");
            if (chest) b.Chest(t, 0, 0, 0, 0);
            return t;
        }

        /// <summary>
        /// A walled yard, 22.5 m square: walls three tiles (4.5 m) tall, too tall to climb, one 3 m gate on the south
        /// side; a one-room hut inside with a chest, another chest in the yard. Break the wall to get in anywhere else.
        /// </summary>
        public static void Compound(BrBuild b, double x, double y, double z)
        {
            const double size = 22.5, gate = 3.0, th = 0.3;
            double half = (size - gate) / 2;
            b.Strip(new Vec3d(x, y, z), half, true, 3);
            b.Strip(new Vec3d(x + half + gate, y, z), half, true, 3);
            b.Strip(new Vec3d(x, y, z + size - th), size, true, 3);
            b.Strip(new Vec3d(x, y, z + th), size - 2 * th, false, 3);
            b.Strip(new Vec3d(x + size - th, y, z + th), size - 2 * th, false, 3);
            var hut = new GreyboxBlock.Building(x + 12, z + 12, 1, 1, 1) { Y0 = y };
            hut.AlongX[0, 0, 0] = WallKind.ShutDoor;
            b.Raise(hut, "Compound");
            b.Chest(hut, 0, 0, 0, 0);
            b.Chest(new Vec3d(x + 5, y, z + 6), 0);
        }

        /// <summary>
        /// A low wall of tiles one row (1.5 m) tall round a flat roof's edge: chest-high cover that stops bullets, on top
        /// of the outer walls (call after <see cref="BrBuild.Raise"/>).
        /// </summary>
        public static void Parapet(BrBuild b, GreyboxBlock.Building t, int rows = 1)
        {
            double y = t.Top, x0 = t.X0, z0 = t.Z0, x1 = t.Cx(t.Mx) + Col, z1 = t.Cz(t.Mz) + Col;
            b.Strip(new Vec3d(x0, y, z0), x1 - x0, true, rows);
            b.Strip(new Vec3d(x0, y, z1 - Col), x1 - x0, true, rows);
            b.Strip(new Vec3d(x0, y, z0 + Col), z1 - z0 - 2 * Col, false, rows);
            b.Strip(new Vec3d(x1 - Col, y, z0 + Col), z1 - z0 - 2 * Col, false, rows);
        }

        /// <summary>
        /// A tower: <paramref name="storeys"/> storeys (any number; 10 is tall, 12 is a landmark) of
        /// <paramref name="mx"/> x <paramref name="mz"/> modules (mz at least 3), a switchback stairwell up its west lane to
        /// the roof, a flat walkable roof with a parapet. Ground floor: a wide door on the south side and a shut door on
        /// the north; every floor above has windows on two sides only (so the fast way between floors outside the stairs
        /// is a hole). A chest on every <paramref name="chestEvery"/>-th floor, in the far corner from the stairs, and one
        /// on the roof. The building comes back raised: to change its walls first use <see cref="TowerPlan"/>.
        /// </summary>
        public static GreyboxBlock.Building Tower(BrBuild b, double x, double y, double z, int mx, int mz, int storeys, int chestEvery = 2, string name = "Tower")
        {
            GreyboxBlock.Building t = TowerPlan(b, x, y, z, mx, mz, storeys);
            b.Raise(t, name);
            Parapet(b, t);
            for (int s = 0; s < storeys; s += Math.Max(1, chestEvery)) b.Chest(t, mx - 1, (s / Math.Max(1, chestEvery)) % 2 == 0 ? 0 : mz - 1, s, Math.PI);
            b.Chest(t, mx - 1, mz - 1, storeys, Math.PI); // on the roof
            return t;
        }

        /// <summary>The walls and stairs of <see cref="Tower"/> at level <paramref name="y"/> (cut the footing first: the
        /// stairs are built at that level), not yet raised: change what you like, then <see cref="BrBuild.Raise"/> and <see cref="Parapet"/>.</summary>
        public static GreyboxBlock.Building TowerPlan(BrBuild b, double x, double y, double z, int mx, int mz, int storeys)
        {
            if (mx < 2 || mz < 3) throw new ArgumentException("A tower needs at least 2 x 3 modules.");
            var t = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            b.Stairwell(t, 0, true, +1);
            t.AlongX[0, 0, mx - 1] = WallKind.WideDoor;  // south
            t.AlongX[0, mz, mx - 1] = WallKind.ShutDoor; // north
            for (int s = 0; s < storeys; s++)
            {
                // The stair lane's side wall, open where the stairs arrive and start (cells 2 and 0 of the lane).
                for (int j = 0; j < mz; j++) t.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                for (int i = 1; i < mx; i++)
                {
                    if (s == 0) continue;
                    t.AlongX[s, 0, i] = (i + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;  // south: some windows
                }
                if (s > 0) for (int j = 0; j < mz; j++) t.AlongZ[s, mx, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall; // east
            }
            return t;
        }

        /// <summary>
        /// A placeholder home POI round an outer segment's anchor: four one-storey 3 x 3 buildings round a 12.6 m square
        /// (two chests each, one sealed), two chests in the middle: <see cref="BrLayout.HomeChests"/> in all. The four
        /// buildings stand across every pad's view past the anchor (so no pad sees the next region's). Segment threads
        /// replace this with their own home POI.
        /// </summary>
        public static void HomeSquare(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = BrGround.Base, s = 3 * M + Col, gap = 6.3;
            for (int k = 0; k < 4; k++)
            {
                double sx = k % 2 == 0 ? -1 : 1, sz = k < 2 ? -1 : 1;
                double x = sx < 0 ? a.X - gap - s : a.X + gap, z = sz < 0 ? a.Z - gap - s : a.Z + gap;
                var t = new GreyboxBlock.Building(x, z, 3, 3, 1);
                b.Foot(t, 2, y);
                // Doors onto the square (the one facing the middle on each side), except the sealed one.
                if (k != 3)
                {
                    t.AlongX[0, sz < 0 ? 3 : 0, sx < 0 ? 2 : 0] = WallKind.Door;
                    t.AlongZ[0, sx < 0 ? 3 : 0, sz < 0 ? 2 : 0] = WallKind.Door;
                    t.AlongX[0, sz < 0 ? 0 : 3, 1] = WallKind.Window;
                }
                b.Raise(t, b.Name + " home");
                b.Chest(t, 0, 0, 0, 0);
                b.Chest(t, 2, 2, 0, Math.PI);
            }
            b.Chest(new Vec3d(a.X + 2, y, a.Z), Math.PI / 2);
            b.Chest(new Vec3d(a.X - 2, y, a.Z), -Math.PI / 2);
            b.Poi(BrSegments.All[b.Id].HomePoiName, a);
        }

        // ---------------------------------------------------------------- villages

        /// <summary>Village blocks: squares this wide, this far apart (the rest is street); then small lots in the gaps.</summary>
        public const double BlockSize = 30, BlockPitch = 40, LotSize = 14, LotPitch = 19;
        /// <summary>A village block or lot needs ground that varies less than this under it (it stands on one footing).</summary>
        public const double VillageRise = 4.2;

        /// <summary>
        /// Fills <paramref name="area"/> with village blocks on a grid, then small lots in the gaps: wherever a square
        /// plus a street round it is clear (<see cref="BrBuild.IsClear"/>), outside the home plaza, and on ground gentle
        /// enough for one level footing. What each holds is a fixed hash of its place and <paramref name="salt"/>. Stops
        /// before the segment's chests pass <paramref name="stopAtChests"/>.
        /// </summary>
        public static int Villages(BrBuild b, Rect2 area, uint salt = 0, double maxSlopeDegrees = 24, int stopAtChests = int.MaxValue)
        {
            double maxSlope = Math.Tan(maxSlopeDegrees * Math.PI / 180);
            int made = 0;
            bool Fits(double x, double z, double size, double street, double apron, out double level)
            {
                level = 0;
                var r = new Rect2(x, z, x + size, z + size);
                if (!b.IsClear(r, street) || b.InHome(r, street)) return false;
                double x0 = x - apron, z0 = z - apron, span = size + 2 * apron;
                int n = (int)Math.Ceiling(span / 3);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity, sum = 0;
                var row = new double[n + 1];
                for (int j = 0; j <= n; j++)
                {
                    double prev = 0;
                    for (int i = 0; i <= n; i++)
                    {
                        double h = b.Natural(x0 + span * i / n, z0 + span * j / n);
                        lo = Math.Min(lo, h); hi = Math.Max(hi, h); sum += h;
                        if (i > 0 && Math.Abs(h - prev) > maxSlope * span / n) return false;
                        if (j > 0 && Math.Abs(h - row[i]) > maxSlope * span / n) return false;
                        prev = h; row[i] = h;
                    }
                    if (hi - lo > VillageRise) return false;
                }
                if (!b.FootAllowed(x, z, x + size, z + size, apron, out double flo, out double fhi)) return false;
                level = Math.Round(Math.Max(flo, Math.Min(fhi, sum / ((n + 1) * (n + 1)))) / 0.3) * 0.3;
                if (level < flo - 1e-9 || level > fhi + 1e-9) return false;
                if (level - lo > VillageRise * 0.75 || hi - level > VillageRise * 0.75) return false;
                return b.TryFoot(x, z, x + size, z + size, apron, out level, level);
            }
            for (int i = 0; area.X0 + i * BlockPitch + BlockSize <= area.X1; i++)
                for (int j = 0; area.Z0 + j * BlockPitch + BlockSize <= area.Z1; j++)
                {
                    double x = area.X0 + i * BlockPitch, z = area.Z0 + j * BlockPitch;
                    if (b.Chests.Count >= stopAtChests - 8 || !Fits(x, z, BlockSize, 4, 3, out double y)) continue;
                    uint h = Hash(i, j, 1 + salt * 7 + (uint)b.Id * 101);
                    Block(b, x, y, z, (int)(h % 6), (int)(h >> 8));
                    made++;
                }
            for (int i = 0; area.X0 + i * LotPitch + LotSize <= area.X1; i++)
                for (int j = 0; area.Z0 + j * LotPitch + LotSize <= area.Z1; j++)
                {
                    double x = area.X0 + i * LotPitch, z = area.Z0 + j * LotPitch;
                    if (b.Chests.Count >= stopAtChests - 2 || !Fits(x, z, LotSize, 3, 2.5, out double y)) continue;
                    uint h = Hash(i, j, 2 + salt * 7 + (uint)b.Id * 101);
                    Lot(b, x, y, z, (int)(h % 5), (int)(h >> 8));
                    made++;
                }
            return made;
        }

        /// <summary>One small lot in a 14 m square at (x, z), on level ground at y.</summary>
        public static void Lot(BrBuild b, double x, double y, double z, int kind, int h)
        {
            switch (kind)
            {
                case 0: Terrace(b, x + 0.5, y, z + 3.7, 2, true, h % 2); break;
                case 1: Terrace(b, x + 3.7, y, z + 0.5, 2, false, h % 2); break;
                case 2:
                    Shed(b, x + 3.7, y, z + 6.5);
                    b.Strip(new Vec3d(x + 2, y, z + 1.5), 9, true, 1);
                    break;
                case 3:
                    b.Strip(new Vec3d(x + 2, y, z + 2), 9, true, 2);
                    b.Strip(new Vec3d(x + 2, y, z + 2.3), 9, false, 2);
                    break;
                default: Shed(b, x + 3.7, y, z + 3.7); break;
            }
        }

        /// <summary>One village block in a 30 m square at (x, z), on level ground at y.</summary>
        public static void Block(BrBuild b, double x, double y, double z, int kind, int h)
        {
            switch (kind)
            {
                case 0:
                    Terrace(b, x + 2, y, z + 1, 4, true, h % 4);
                    Terrace(b, x + 2, y, z + 22.4, 4, true, (h / 4 + 2) % 4);
                    b.Strip(new Vec3d(x + 9, y, z + 14.6), 6, true, 1);
                    break;
                case 1: Warehouse(b, x + 2, y, z + 5); break;
                case 2:
                    House(b, x, y, z + 5);
                    Terrace(b, x + 18, y, z + 15, 2, false, h % 2);
                    Shed(b, x + 18, y, z + 2);
                    break;
                case 3: Compound(b, x + 3, y, z + 3); break;
                case 4:
                    House(b, x + 17, y, z + 5);
                    Terrace(b, x + 2, y, z + 3, 3, false, h % 3);
                    break;
                default:
                    Shed(b, x + 11.7, y, z + 11.7);
                    b.Strip(new Vec3d(x + 2, y, z + 4), 7.5, true, 1);
                    b.Strip(new Vec3d(x + 20, y, z + 26), 7.5, true, 1);
                    b.Strip(new Vec3d(x + 4, y, z + 16), 6, false, 2);
                    b.Strip(new Vec3d(x + 25, y, z + 8), 6, false, 2);
                    break;
            }
        }

        public static uint Hash(int i, int j, uint salt)
        {
            uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ salt * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return h;
        }
    }
}
