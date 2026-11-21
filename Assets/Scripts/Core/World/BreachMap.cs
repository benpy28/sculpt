using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>What the game needs to know about the breach demo map.</summary>
    public sealed class BreachMapInfo
    {
        /// <summary>Spawn feet positions per team (index 0 = team 1, index 1 = team 2).</summary>
        public readonly List<Vec3d>[] TeamSpawns = { new List<Vec3d>(), new List<Vec3d>() };
        public double[] SpawnYaw = { 0, System.Math.PI };
        public CourseInfo Course;
        /// <summary>Tile ids of the two test walls (team 1 side, team 2 side) and the sealed room in the middle.</summary>
        public int[] TestWallA, TestWallB, Room;
        /// <summary>Feet position in the middle of the sealed room (the practice dummy's post).</summary>
        public Vec3d RoomCentre;
        public double SizeX, SizeZ;
        /// <summary>Items lying on the map at the start (the server spawns them): grenade refills by each spawn.</summary>
        public readonly List<GroundItem> ItemSpawns = new List<GroundItem>();
        /// <summary>The greybox test block: three buildings, stairwells, ramps, rooftop links, hills.</summary>
        public GreyboxInfo Block;
    }

    /// <summary>
    /// The breach demo map (Ben 2026-11-21): flat sub-cell ground plus the building brushes. The movement course runs
    /// along its west side, the test walls and the sealed room sit in the middle, and the greybox block (three
    /// buildings with hills around them, <see cref="GreyboxBlock"/>) fills the east half between the two spawns.
    /// Server and clients build it the same way, so only changes ever need to travel.
    /// </summary>
    public static class BreachMap
    {
        public const int ChunksAcross = 34;   // 34 x 4.8 m = 163.2 m square

        public static SubGrid BuildGround()
        {
            var g = new SubGrid(ChunksAcross, 1, ChunksAcross, -16);
            g.FillBaseGround();
            return g;
        }

        public static BrushWorld BuildBrushes(out BreachMapInfo info, BreachConfig breach = null)
        {
            var w = new BrushWorld(breach);
            info = new BreachMapInfo
            {
                SizeX = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize,
                SizeZ = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize,
            };
            info.Course = MovementCourse.Build(w, 6, 6);
            double mid = info.SizeX / 2;
            info.TestWallA = TestWall(w.Tiles, mid - 6, 50);
            info.TestWallB = TestWall(w.Tiles, mid - 6, info.SizeZ - 50 - w.Tiles.Config.TileThickness);
            info.Room = SealedRoom(w, mid - 3.3, mid - 3.3);
            info.RoomCentre = new Vec3d(mid, 0, mid);
            info.Block = GreyboxBlock.Build(w);
            w.Tiles.Changes.Clear(); // the build itself is not news
            // Spawns face each other across the block: team 1 south of the south berm, team 2 north of the north hill.
            const double spawnX = 110;
            for (int i = 0; i < 3; i++)
            {
                info.TeamSpawns[0].Add(new Vec3d(spawnX - 3 + 3 * i, 0, 18));
                info.TeamSpawns[1].Add(new Vec3d(spawnX - 3 + 3 * i, 0, info.SizeZ - 18));
            }
            // Grenade refills a few steps in front of each spawn row (two stacks of two per side).
            foreach (double x in new[] { spawnX - 5.0, spawnX + 5.0 })
            {
                info.ItemSpawns.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(x, 0, 22) });
                info.ItemSpawns.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(x, 0, info.SizeZ - 22) });
            }
            return w;
        }

        /// <summary>
        /// A test wall 8 tiles wide and 2 high (12 m x 3 m) running east from (x0, 0, z), with one tile of every state
        /// on show: bottom row tile 2 Damaged, 3 Patched, 4 Reinforced, 5 Broken (a one-tile hole you slide through)
        /// and column 6 broken top and bottom (a doorway you walk through).
        /// </summary>
        private static int[] TestWall(TileWalls t, double x0, double z)
        {
            int[] ids = t.AddWall(new Vec3d(x0, 0, z), Dir.East, 8, 2);
            t.Set(ids[2], TileState.Damaged, t.Config.IntactHealth * 2 / 5, TileCause.Built);
            t.Set(ids[3], TileState.Patched, t.Config.PatchedHealth, TileCause.Built);
            t.Set(ids[4], TileState.Reinforced, t.Config.ReinforcedHealth, TileCause.Built);
            t.Set(ids[5], TileState.Broken, 0, TileCause.Built);
            t.Set(ids[6], TileState.Broken, 0, TileCause.Built);
            t.Set(ids[8 + 6], TileState.Broken, 0, TileCause.Built);
            return ids;
        }

        /// <summary>
        /// A sealed room in the middle: 4 x 4 tiles inside (6 m square), 3 tiles high (one storey), frame columns at the
        /// corners and a floor slab on top. The only way in is to break a wall.
        /// </summary>
        private static int[] SealedRoom(BrushWorld w, double x0, double z0)
        {
            TileWalls t = w.Tiles;
            double c = t.Config.TileThickness, s = t.Config.TileSize, span = 4 * s, h = 3 * s;
            double a = x0 + c, b = a + span, za = z0 + c, zb = za + span;
            var ids = new List<int>();
            ids.AddRange(t.AddWall(new Vec3d(a, 0, z0), Dir.East, 4, 3));   // south
            ids.AddRange(t.AddWall(new Vec3d(a, 0, zb), Dir.East, 4, 3));   // north
            ids.AddRange(t.AddWall(new Vec3d(x0, 0, za), Dir.North, 4, 3)); // west
            ids.AddRange(t.AddWall(new Vec3d(b, 0, za), Dir.North, 4, 3));  // east
            foreach (double x in new[] { x0, b })
                foreach (double z in new[] { z0, zb })
                    w.AddBox(new Vec3d(x, 0, z), new Vec3d(x + c, h, z + c), BrushKind.Frame);
            w.AddBox(new Vec3d(x0, h, z0), new Vec3d(b + c, h + c, zb + c), BrushKind.Floor);
            return ids.ToArray();
        }

        /// <summary>The terrain movement and bullets use: ground and buildings as one.</summary>
        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) => new CompositeTerrain(ground, brushes);
    }
}
