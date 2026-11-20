using System;
using NUnit.Framework;
using Sculpt.Core;
namespace Sculpt.Core.Tests
{
    public class DebugTests
    {
        [Test, Explicit]
        public void TraceRamp()
        {
            var g = new VoxelGrid(2, 2, 2, -16);
            g.FillBaseGround();
            g.Fill(new Int3(5, 0, 0), new Int3(5, 0, 31), Block.Ramp(Dir.West));
            var c = new MovementConfig();
            var s = MotorState.At(new Vec3d(3.5 * 1.2, 0, 5.5 * 1.2));
            for (int i = 0; i < 40; i++)
            {
                PlayerMovement.Step(g, c, ref s, new MoveInput { MoveZ = 1, Yaw = Math.PI / 2, SprintHeld = true });
                if (i > 15) TestContext.Out.WriteLine($"{i} {s.Mode} {s.Position} v {s.Velocity} n {s.GroundNormal} up {s.UphillTicks}");
            }
        }
        [Test, Explicit]
        public void TraceJumpWall()
        {
            var g = new VoxelGrid(2, 2, 2, -16);
            g.FillBaseGround();
            g.Fill(new Int3(6, 0, 0), new Int3(6, 2, 31), Block.Solid);
            var c = new MovementConfig();
            var s = MotorState.At(new Vec3d(3.5 * 1.2, 0, 5.5 * 1.2));
            for (int i = 0; i < 120; i++)
            {
                PlayerMovement.Step(g, c, ref s, new MoveInput { MoveZ = 1, Yaw = Math.PI / 2, JumpPressed = i == 60 });
                if (i > 55 && i < 110) TestContext.Out.WriteLine($"{i} {s.Mode} {s.Position} v {s.Velocity} n {s.GroundNormal}");
            }
        }
    }
}
