using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Train Yard pieces for the North segment (<see cref="NorthSegment"/>): decks of floor tiles, closed tile boxes
    /// (goods wagons, shipping containers), raised island platforms, whole trains. Everything is breakable tiles on the
    /// 1.5 m grid like the buildings, so a wagon is cover you can also break into, and a deck is a floor you can shoot
    /// out from under someone. Owned by the North segment thread.
    /// </summary>
    internal static class NorthKit
    {
        private const double Th = 0.3;

        /// <summary>Floor tiles over a rectangle, top at <paramref name="top"/>, 0.3 m thick, in equal pieces no bigger than a tile.</summary>
        public static void Deck(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            double s = b.World.Tiles.Config.TileSize;
            double sx = x1 - x0, sz = z1 - z0;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int a = 0; a < nx; a++)
                for (int c = 0; c < nz; c++)
                {
                    var t = new Aabb(new Vec3d(x0 + sx * a / nx, top - Th, z0 + sz * c / nz),
                                     new Vec3d(x0 + sx * (a + 1) / nx, top, z0 + sz * (c + 1) / nz));
                    int id = b.World.Tiles.AddTile(t, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
        }

        /// <summary>What a box's long side has in it.</summary>
        public enum Side { Solid, Doorway, OpenEnd }

        /// <summary>
        /// A closed box of tile walls standing at <paramref name="y"/>: <paramref name="len"/> long along X (or Z),
        /// <paramref name="wide"/> across, <paramref name="rows"/> tiles (1.5 m each) tall, with a roof of floor tiles on
        /// top if asked. <paramref name="side"/>: a 3 m doorway in the middle of the first long side (full height), or
        /// the far end left open (a dead-end tunnel). Returns the roof's top (or the walls' top).
        /// </summary>
        public static double Box(BrBuild b, double x0, double z0, double len, double wide, bool alongX, double y, int rows, bool roof, Side side = Side.Solid)
        {
            double top = y + rows * 1.5;
            if (alongX)
            {
                if (side == Side.Doorway)
                {
                    double mid = x0 + len / 2;
                    b.Strip(new Vec3d(x0, y, z0), mid - 1.5 - x0, true, rows);
                    b.Strip(new Vec3d(mid + 1.5, y, z0), x0 + len - mid - 1.5, true, rows);
                }
                else b.Strip(new Vec3d(x0, y, z0), len, true, rows);
                b.Strip(new Vec3d(x0, y, z0 + wide - Th), len, true, rows);
                b.Strip(new Vec3d(x0, y, z0 + Th), wide - 2 * Th, false, rows);
                if (side != Side.OpenEnd) b.Strip(new Vec3d(x0 + len - Th, y, z0 + Th), wide - 2 * Th, false, rows);
                if (roof) Deck(b, x0, z0, x0 + len, z0 + wide, top + Th);
            }
            else
            {
                if (side == Side.Doorway)
                {
                    double mid = z0 + len / 2;
                    b.Strip(new Vec3d(x0, y, z0), mid - 1.5 - z0, false, rows);
                    b.Strip(new Vec3d(x0, y, mid + 1.5), z0 + len - mid - 1.5, false, rows);
                }
                else b.Strip(new Vec3d(x0, y, z0), len, false, rows);
                b.Strip(new Vec3d(x0 + wide - Th, y, z0), len, false, rows);
                b.Strip(new Vec3d(x0 + Th, y, z0), wide - 2 * Th, true, rows);
                if (side != Side.OpenEnd) b.Strip(new Vec3d(x0 + Th, y, z0 + len - Th), wide - 2 * Th, true, rows);
                if (roof) Deck(b, x0, z0, x0 + wide, z0 + len, top + Th);
            }
            return roof ? top + Th : top;
        }

        /// <summary>
        /// A raised platform (an island platform, a loading dock): kerb walls one tile (1.5 m) tall round the edge and a
        /// deck flush with their tops. Vault onto it, crouch behind it. Returns the top.
        /// </summary>
        public static double Platform(BrBuild b, double x0, double z0, double x1, double z1, double y)
        {
            double top = y + 1.5;
            b.Strip(new Vec3d(x0, y, z0), x1 - x0, true, 1);
            b.Strip(new Vec3d(x0, y, z1 - Th), x1 - x0, true, 1);
            b.Strip(new Vec3d(x0, y, z0 + Th), z1 - z0 - 2 * Th, false, 1);
            b.Strip(new Vec3d(x1 - Th, y, z0 + Th), z1 - z0 - 2 * Th, false, 1);
            Deck(b, x0 + Th, z0 + Th, x1 - Th, z1 - Th, top);
            return top;
        }

        /// <summary>A pier of tiles under a deck: <paramref name="rows"/> tall, one tile wide along X, 0.3 m thick.</summary>
        public static void Pier(BrBuild b, double x, double z, double y, int rows, bool alongX) => b.Strip(new Vec3d(x, y, z), 1.5, alongX, rows);

        /// <summary>The kinds of rolling stock (all 3 m wide, the length of eight tiles).</summary>
        public enum Wagon { Boxcar, BoxcarDoor, Open, Gap }

        public const double WagonLen = 12, WagonWide = 3, Coupling = 1.5;

        /// <summary>
        /// A train parked on a track along X centred on <paramref name="z"/>, from <paramref name="x0"/> while wagons
        /// fit before <paramref name="x1"/>: sealed boxcars (a chest inside every <paramref name="chestEvery"/>-th one,
        /// never where <paramref name="noChest"/> says), boxcars with an open sliding door, open wagons (one tile tall,
        /// no roof: vault in, crouch inside), and gaps you can run through. Which is which is a fixed hash of
        /// <paramref name="seed"/>. Returns how many chests it placed.
        /// </summary>
        public static int Train(BrBuild b, double x0, double x1, double z, double y, uint seed, int chestEvery, Func<double, double, bool> noChest, int maxChests = int.MaxValue)
        {
            int k = 0, sealedCount = 0, chests = 0;
            double x = x0;
            while (x + WagonLen <= x1 + 1e-9)
            {
                uint h = BrKit.Hash(k, (int)Math.Round(z * 10), seed);
                Wagon w = (h % 10) switch { 0 or 1 or 2 or 3 => Wagon.Boxcar, 4 or 5 => Wagon.BoxcarDoor, 6 or 7 or 8 => Wagon.Open, _ => Wagon.Gap };
                if (k == 0 && w == Wagon.Gap) w = Wagon.Boxcar;
                double z0 = z - WagonWide / 2;
                switch (w)
                {
                    case Wagon.Boxcar:
                        Box(b, x, z0, WagonLen, WagonWide, true, y, 2, true);
                        sealedCount++;
                        double cx = x + WagonLen / 2;
                        if (chestEvery > 0 && sealedCount % chestEvery == 0 && chests < maxChests && !noChest(cx, z))
                        {
                            b.Chest(new Vec3d(cx, y, z), (h >> 8) % 2 == 0 ? Math.PI / 2 : -Math.PI / 2);
                            chests++;
                        }
                        break;
                    case Wagon.BoxcarDoor:
                        // Door on the north or south side (a fixed hash), so the trains don't all open one way.
                        if ((h >> 9) % 2 == 0) Box(b, x, z0, WagonLen, WagonWide, true, y, 2, true, Side.Doorway);
                        else
                        {
                            // Doorway on the north side: build it mirrored (the doorway goes in the first long side, so swap sides by hand).
                            double mid = x + WagonLen / 2;
                            b.Strip(new Vec3d(x, y, z0), WagonLen, true, 2);
                            b.Strip(new Vec3d(x, y, z0 + WagonWide - Th), mid - 1.5 - x, true, 2);
                            b.Strip(new Vec3d(mid + 1.5, y, z0 + WagonWide - Th), x + WagonLen - mid - 1.5, true, 2);
                            b.Strip(new Vec3d(x, y, z0 + Th), WagonWide - 2 * Th, false, 2);
                            b.Strip(new Vec3d(x + WagonLen - Th, y, z0 + Th), WagonWide - 2 * Th, false, 2);
                            Deck(b, x, z0, x + WagonLen, z0 + WagonWide, y + 3 + Th);
                        }
                        break;
                    case Wagon.Open:
                        Box(b, x, z0, WagonLen, WagonWide, true, y, 1, false);
                        break;
                }
                x += WagonLen + ((h >> 12) % 4 == 0 ? 4.5 : Coupling);
                k++;
            }
            return chests;
        }
    }
}
