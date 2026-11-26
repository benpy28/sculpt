using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// World-state red-team hunt (cheat client). Each test drives a real <see cref="NetServer"/> with forged
    /// heal/door/charge/pickup commands the way a modified client would, and asserts the server's behaviour.
    /// </summary>
    public class Hunt_world
    {
        private static readonly MovementConfig Mc = new MovementConfig();

        // ---------------------------------------------------------------- arena: a door with a solid wall in front of it

        private static SubGrid MakeGround()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        /// <summary>
        /// Flat ground with a shut door leaf facing north at z 12 (x 8..9.5), and a solid 2-tile-tall wall right in
        /// front of it at z 10. A player standing at z 9.3 is &lt; 3.25 m (DoorReach + slack) from the door but has NO
        /// line of sight to it: the wall is between them.
        /// </summary>
        private static BrushWorld BuildBrushes()
        {
            var w = new BrushWorld();
            double s = w.Tiles.Config.TileSize;      // 1.5
            double th = w.Tiles.Config.TileThickness; // 0.3
            // Shut door leaf (whole-tile, no cells), facing north (thin in Z).
            w.Tiles.AddTile(new Aabb(new Vec3d(8, 0, 12), new Vec3d(8 + s, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            // A solid wall between the player and the door, covering the door's x range and eye height.
            w.Tiles.AddWall(new Vec3d(7.5, 0, 10), Dir.East, 2, 2); // x 7.5..10.5, z 10..10.3, 3 m tall
            w.Tiles.Changes.Clear();
            return w;
        }

        private static SculptNetHarness Arena()
            => new SculptNetHarness(MakeGround, wrap: g => new CompositeTerrain(g, BuildBrushes()));

        private static readonly Vec3d PlayerFeet = new Vec3d(8.75, 0, 9.3);

        /// <summary>
        /// REGRESSION GUARD (was a finding): a modified client forges a door interact (Door + DoorTile + DoorOpen) for a
        /// door it cannot see. The door is in reach (&lt; 3.25 m) but a solid wall is between the eye and the door, so a
        /// fair client's targeting (<see cref="DoorRules.FindTarget"/>) would never pick it. HandleDoor used to re-check
        /// reach but NOT line of sight, so the server honoured the forged tile id. Fixed: HandleDoor now casts a ray
        /// (ItemRules.Visible, like heal and item pickup) and refuses + flags (DoorNoSight) a door it cannot see.
        /// </summary>
        [Test]
        public void ForgedDoorInteract_ThroughAWall_IsRefusedAndFlagged()
        {
            SculptNetHarness h = Arena();
            int door = h.Server.Walls.Doors[0];
            Assert.That(h.Server.Walls[door].State, Is.EqualTo(TileState.Intact), "door starts shut");

            var cheat = h.AddClient(PlayerFeet, team: 1);
            h.RunSeconds(1); // settle on the ground

            // Sanity: the setup really is "in reach, but no line of sight", i.e. a fair client could not target it.
            Vec3d eye = cheat.ServerSide.State.Position + new Vec3d(0, PlayerMovement.EyeOf(cheat.ServerSide.State, Mc), 0);
            Aabb db = h.Server.Walls.BoundsOf(door);
            Assert.That(DoorRules.DistanceTo(db, eye), Is.LessThan(h.Server.Walls.Config.DoorReach + 0.75),
                "door is within server reach (DoorReach + slack)");
            Vec3d doorCentre = (db.Min + db.Max) * 0.5;
            Assert.That(ItemRules.Visible(h.Server.Grid, eye, doorCentre), Is.False, "a solid wall blocks the line of sight");
            Vec3d toDoor = doorCentre - eye;
            Assert.That(DoorRules.FindTarget(h.Server.Walls, h.Server.Grid, eye, toDoor, h.Server.Walls.Config.DoorReach), Is.EqualTo(-1),
                "a fair client's door targeting refuses it (the wall is in the way)");

            // The cheat forges the interact once.
            int pressTick = -1;
            cheat.Script = (p, t) =>
            {
                var input = new SculptTickInput { Move = new MoveInput { Yaw = 0 } };
                if (pressTick < 0) { pressTick = t; input.Hand = new HandInput { Door = true, DoorTile = door, DoorOpen = true }; }
                return input;
            };
            h.RunSeconds(1.5);

            // A door you cannot see must not open, and the forged press is flagged.
            Assert.That(h.Server.Walls[door].State, Is.Not.EqualTo(TileState.DoorOpen),
                "a door the cheat had no line of sight to must stay shut");
            Assert.That(h.Server.Flags.Count(FlagKind.DoorNoSight), Is.GreaterThan(0),
                "the forged out-of-sight door interact is flagged");
        }
    }
}
