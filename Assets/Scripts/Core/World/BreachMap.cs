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
        public double SizeX, SizeZ;
    }

    /// <summary>
    /// The breach demo map (Ben 2026-11-21): flat sub-cell ground (outdoor hills come with the greybox block) plus the
    /// building brushes. For now it holds the movement course along its west side; the greybox buildings come in
    /// step 5. Server and clients build it the same way, so only changes ever need to travel.
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

        public static BrushWorld BuildBrushes(out BreachMapInfo info)
        {
            var w = new BrushWorld();
            info = new BreachMapInfo
            {
                SizeX = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize,
                SizeZ = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize,
            };
            info.Course = MovementCourse.Build(w, 6, 6);
            double mid = info.SizeX / 2;
            for (int i = 0; i < 3; i++)
            {
                info.TeamSpawns[0].Add(new Vec3d(mid - 3 + 3 * i, 0, 30));
                info.TeamSpawns[1].Add(new Vec3d(mid - 3 + 3 * i, 0, info.SizeZ - 30));
            }
            return w;
        }

        /// <summary>The terrain movement and bullets use: ground and buildings as one.</summary>
        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) => new CompositeTerrain(ground, brushes);
    }
}
