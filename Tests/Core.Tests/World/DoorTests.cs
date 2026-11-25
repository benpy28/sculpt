using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Doors (Ben 2026-11-25: "they usually just get broken instantly"): an easy way in that you still have to break.</summary>
    public class DoorTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();

        /// <summary>Flat ground with a 4-tile wall at z 12 (x 6..12) whose middle two columns are a shut door (x 7.5..10.5, 3 m tall).</summary>
        private static CompositeTerrain Arena(out TileWalls walls, out int left, out int right)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            double s = w.Tiles.Config.TileSize, th = w.Tiles.Config.TileThickness;
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(10.5, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(7.5, 3, 12), Dir.East, 2, 1);
            left = w.Tiles.AddTile(new Aabb(new Vec3d(7.5, 0, 12), new Vec3d(9, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            right = w.Tiles.AddTile(new Aabb(new Vec3d(9, 0, 12), new Vec3d(10.5, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            w.Tiles.Changes.Clear();
            walls = w.Tiles;
            return new CompositeTerrain(g, w);
        }

        [Test]
        public void ADoorLeaf_GoesWithOnePunchOrTwoArBullets_AWallTileDoesNot()
        {
            Arena(out TileWalls w, out int left, out int right);
            BreachConfig c = w.Config;
            Assert.That(w.Damage(left, c.PunchTileDamage, TileCause.Punch), Is.True, "one punch");
            Assert.That(w.Damage(right, c.ArTileDamage, TileCause.Bullet), Is.False);
            Assert.That(w.Damage(right, c.ArTileDamage, TileCause.Bullet), Is.True, "two AR bullets");
            Assert.That(w[left].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[left].Kind, Is.EqualTo(TileKind.Door));

            int wall = 0; // the wall tile beside the door
            Assert.That(w.Damage(wall, c.PunchTileDamage, TileCause.Punch), Is.False, "a wall tile takes two punches");
        }

        [Test]
        public void ABrokenDoor_IsAStandingSizedOpening()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out _);
            w.Set(left, TileState.Broken, 0, TileCause.Punch);
            var player = new Aabb(new Vec3d(8.25 - Mc.HalfWidth, 0.01, 11.8), new Vec3d(8.25 + Mc.HalfWidth, Mc.StandHeight, 12.5));
            Assert.That(t.BoxOverlapsSolid(player), Is.False);
        }

        [Test]
        public void ASprintCharge_GoesThroughAnUncrackedDoor()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out int right);
            var s = MotorState.At(new Vec3d(9, 0, 2));
            for (int k = 1; k <= 30 * SimTime.Per60; k++) { s.Tick = k - 1; PlayerMovement.Step(t, Mc, ref s, default, null); }
            var breaker = new ServerChargeBreaker { By = 1 };
            int start = s.Tick + 1;
            for (int k = 0; k < 150 * SimTime.Per60; k++)
            {
                InputCmd cmd = InputCmd.Quantize(new InputCmd { Move = new MoveInput { MoveZ = 1, Yaw = 0, SprintHeld = true } });
                cmd.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, cmd, s.Position, null, null, null, null, breaker);
            }
            Assert.That(w[left].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[right].State, Is.EqualTo(TileState.Broken));
            Assert.That(s.Position.Z, Is.GreaterThan(13), "ran on through");
        }
    }
}
