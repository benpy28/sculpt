namespace Sculpt.Core
{
    /// <summary>
    /// The 1v1 duel map for the friend demo (Ben 2026-11-25): a 96 m square of flat ground with a central two-storey
    /// hall, a two-storey building on each flank (stairwell inside, ramp up the outside to the roof), rooftop bridges
    /// to the hall, low tile walls in the streets and a berm in front of each spawn. Every piece comes in a pair turned
    /// 180 degrees about the centre, so neither spawn has a better side. All walls, floors, stairs and ramps are
    /// breakable tiles, like the greybox block. Same info shape as the breach map, with no movement course.
    /// </summary>
    public static class DuelMap
    {
        public const int ChunksAcross = 20;   // 20 x 4.8 m = 96 m square

        public static SubGrid BuildGround()
        {
            var g = new SubGrid(ChunksAcross, 1, ChunksAcross, -16);
            g.FillBaseGround();
            return g;
        }

        public static BrushWorld BuildBrushes(out BreachMapInfo info, BreachConfig breach = null)
        {
            var w = new BrushWorld(breach);
            double size = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize;
            info = new BreachMapInfo { SizeX = size, SizeZ = size };
            info.Block = GreyboxBlock.BuildDuel(w, size);
            w.Tiles.Changes.Clear(); // the build itself is not news
            double mid = size / 2;
            // Team 1 at the south edge facing north, team 2 turned at the north edge.
            for (int i = 0; i < 3; i++)
            {
                var p = new Vec3d(mid - 3 + 3 * i, 0, 4);
                info.TeamSpawns[0].Add(p);
                info.TeamSpawns[1].Add(Turn(p, size));
            }
            info.SpawnYaw = new[] { 0.0, System.Math.PI };
            foreach (double x in new[] { mid - 7.0, mid + 7.0 })
            {
                var p = new Vec3d(x, 0, 4);
                info.ItemSpawns.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = p });
                info.ItemSpawns.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = Turn(p, size) });
            }
            return w;
        }

        /// <summary>A point turned 180 degrees about the map centre.</summary>
        public static Vec3d Turn(Vec3d p, double size) => new Vec3d(size - p.X, p.Y, size - p.Z);

        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) => new CompositeTerrain(ground, brushes);
    }
}
