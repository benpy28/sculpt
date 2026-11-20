using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>Small flat test world: 32 x 32 cells, ground top at height 0, one sculpt system.</summary>
    internal sealed class TestWorld
    {
        public const double B = VoxelGrid.BlockSize;

        public readonly VoxelGrid Grid;
        public readonly SculptConfig Config = new SculptConfig();
        public readonly SculptSystem Sculpt;

        public TestWorld()
        {
            Grid = new VoxelGrid(2, 2, 2, -16);
            Grid.FillBaseGround();
            Grid.ClearChanges();
            Sculpt = new SculptSystem(Grid, Config);
        }

        /// <summary>Feet position at the centre of a cell's floor.</summary>
        public static Vec3d FeetIn(int x, int y, int z) => new Vec3d((x + 0.5) * B, y * B, (z + 0.5) * B);

        public PlayerBody AddPlayer(int id, int team, int x, int y, int z)
        {
            var p = new PlayerBody(id, team, FeetIn(x, y, z));
            Sculpt.AddPlayer(p);
            return p;
        }

        /// <summary>Centre of the given face of a cell, as a client would send it as the aim point.</summary>
        public static Vec3d FaceCenter(Int3 cell, Dir face)
        {
            Vec3d c = VoxelGrid.CellBounds(cell).Center;
            return c + face.ToVec() * (B * 0.5);
        }

        public void Step(int ticks)
        {
            for (int i = 0; i < ticks; i++) Sculpt.Step();
        }

        public Block At(int x, int y, int z) => Grid.Get(x, y, z);
    }
}
