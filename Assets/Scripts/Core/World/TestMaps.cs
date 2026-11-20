namespace Sculpt.Core
{
    /// <summary>Hand-built maps defined in code (no Unity assets), so tests and the game share them.</summary>
    public static class TestMaps
    {
        /// <summary>
        /// M2 movement test map, 64 x 64 cells. Spawn faces north (+Z) on an anchor pad.
        /// North of spawn: 1-level wall (jump/vault), 2-level wall (mantle), 3-level wall with a 1-cell gap.
        /// East: ramp stairs to a +4 platform that drops off (ramp launch), a long slide slope into a -2 pit,
        /// single launch ramps, and a cover field of 1- and 2-level blocks. Anchor tower in the middle.
        /// </summary>
        public static VoxelGrid BuildM2(out Vec3d spawnFeet, out double spawnYaw)
        {
            var g = new VoxelGrid(4, 2, 4, -16);
            g.FillBaseGround();

            // Border wall, 4 levels, unsculptable.
            for (int i = 0; i < 64; i++)
            {
                g.Fill(new Int3(i, 0, 0), new Int3(i, 3, 0), Block.Anchor);
                g.Fill(new Int3(i, 0, 63), new Int3(i, 3, 63), Block.Anchor);
                g.Fill(new Int3(0, 0, i), new Int3(0, 3, i), Block.Anchor);
                g.Fill(new Int3(63, 0, i), new Int3(63, 3, i), Block.Anchor);
            }

            // Spawn pad.
            g.Fill(new Int3(7, -1, 7), new Int3(9, -1, 9), Block.Anchor);
            spawnFeet = new Vec3d(8.5 * VoxelGrid.BlockSize, 0, 8.5 * VoxelGrid.BlockSize);
            spawnYaw = 0;

            // Wall course north of spawn.
            g.Fill(new Int3(4, 0, 14), new Int3(14, 0, 14), Block.Solid);                // 1 level
            g.Fill(new Int3(4, 0, 20), new Int3(14, 1, 20), Block.Solid);                // 2 levels
            g.Fill(new Int3(4, 0, 26), new Int3(8, 2, 26), Block.Solid);                 // 3 levels...
            g.Fill(new Int3(10, 0, 26), new Int3(14, 2, 26), Block.Solid);               // ...with a gap at x = 9
            g.Fill(new Int3(4, 0, 32), new Int3(5, 0, 33), Block.Solid);                 // 1-level step
            g.Fill(new Int3(6, 0, 32), new Int3(7, 1, 33), Block.Solid);                 // 2-level step
            g.Fill(new Int3(8, 0, 32), new Int3(9, 2, 33), Block.Solid);                 // 3-level step (wall)

            // Ramp onto a 1-level block, and a ramp with a drop behind it (small launch).
            g.Fill(new Int3(18, 0, 8), new Int3(18, 0, 10), Block.Ramp(Dir.West));
            g.Fill(new Int3(19, 0, 8), new Int3(20, 0, 10), Block.Solid);
            g.Fill(new Int3(18, 0, 13), new Int3(18, 0, 15), Block.Ramp(Dir.West));

            // Ramp stairs up to a +4 platform, which ends in a drop to the north (ramp launch off the top ramp).
            for (int x = 23; x <= 25; x++)
            {
                for (int step = 0; step < 4; step++)
                {
                    int z = 14 + step;
                    if (step > 0) g.Fill(new Int3(x, 0, z), new Int3(x, step - 1, z), Block.Solid);
                    g.Set(x, step, z, Block.Ramp(Dir.South));
                }
                g.Fill(new Int3(x, 0, 18), new Int3(x, 3, 21), Block.Solid);
                g.Set(x, 4, 21, Block.Ramp(Dir.South)); // rises toward north above the platform, then air: ramp launch
            }

            // Long slide slope: +4 platform descending north into a -2 pit, with a ramp back out.
            for (int x = 34; x <= 40; x++)
            {
                for (int z = 24; z <= 27; z++) g.Set(x, 0 + (z - 24), z, Block.Ramp(Dir.South));   // way up
                for (int z = 25; z <= 27; z++) g.Fill(new Int3(x, 0, z), new Int3(x, z - 25, z), Block.Solid);
                g.Fill(new Int3(x, 0, 28), new Int3(x, 3, 30), Block.Solid);                       // platform top +4
                int y = 3;
                for (int z = 31; z <= 36; z++, y--)
                {
                    // Dig below ground where the slope goes under base level.
                    for (int dy = -1; dy >= y; dy--) g.Set(x, dy, z, Block.Empty);
                    if (y > 0) g.Fill(new Int3(x, 0, z), new Int3(x, y - 1, z), Block.Solid);
                    g.Set(x, y, z, Block.Ramp(Dir.North));
                }
                for (int z = 37; z <= 40; z++) g.Fill(new Int3(x, -2, z), new Int3(x, -1, z), Block.Empty); // pit floor at -2
                g.Set(x, -1, 41, Block.Empty);
                g.Set(x, -2, 41, Block.Ramp(Dir.South));  // out of the pit: rises toward north
                g.Set(x, -1, 42, Block.Ramp(Dir.South));
            }

            // Free-standing launch ramps.
            g.Set(30, 0, 40, Block.Ramp(Dir.South));
            g.Set(31, 0, 40, Block.Ramp(Dir.South));
            g.Set(20, 0, 44, Block.Ramp(Dir.West));
            g.Fill(new Int3(21, 0, 44), new Int3(21, 0, 44), Block.Solid);
            g.Set(21, 1, 44, Block.Ramp(Dir.West));

            // Cover field.
            for (int x = 44; x <= 58; x += 3)
                for (int z = 10; z <= 52; z += 3)
                {
                    int h = ((x * 7 + z * 13) % 5 == 0) ? 2 : 1;
                    if ((x + z) % 2 == 0) g.Fill(new Int3(x, 0, z), new Int3(x, h - 1, z), Block.Solid);
                    if ((x * 3 + z) % 7 == 0) g.Set(x + 1, 0, z, Block.Ramp(DirUtil.Horizontal[(x + z) % 4]));
                }

            // Landmark tower.
            g.Fill(new Int3(31, 0, 31), new Int3(32, 6, 32), Block.Anchor);

            g.ClearChanges();
            g.MarkAllDirty();
            return g;
        }
    }
}
