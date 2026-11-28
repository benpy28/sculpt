using System;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>The server checks the pickup aim cone too (it used to check only reach and sight).</summary>
    public class PickupConeTests
    {
        private static SculptNetHarness Arena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        });

        [Test]
        public void APickupFarOffTheCrosshair_IsRefusedAndFlagged()
        {
            var h = Arena();
            Vec3d feet = SculptNetHarness.FeetIn(10, 10);
            var a = h.AddClient(feet);
            h.RunSeconds(0.3);
            int id = h.Server.SpawnItem(Item.Bandage, 1, feet + new Vec3d(0, 0.5, 1.5)); // north of the player
            h.RunSeconds(0.2);
            int pressAt = a.Client.Tick + 3;
            a.Script = (p, t) =>
            {
                var i = new SculptTickInput { Pitch = 40 };
                i.Move.Yaw = Math.PI; // looking south, away from it
                if (t == pressAt) { i.Hand.Pickup = true; i.Hand.PickId = id; }
                return i;
            };
            h.RunSeconds(0.4);
            Assert.That(h.Server.Items.TryGet(id, out _), Is.True, "still on the ground");
            Assert.That(h.Server.Flags.Count(FlagKind.PickupOffAim), Is.EqualTo(1));

            // Looking at it, the same press works.
            pressAt = a.Client.Tick + 3;
            a.Script = (p, t) =>
            {
                var i = new SculptTickInput { Pitch = 40 };
                if (t == pressAt) { i.Hand.Pickup = true; i.Hand.PickId = id; }
                return i;
            };
            h.RunSeconds(0.4);
            Assert.That(h.Server.Items.TryGet(id, out _), Is.False, "picked up");
            Assert.That(h.Server.Flags.Count(FlagKind.PickupOffAim), Is.EqualTo(1));
        }

        /// <summary>
        /// An honest player spinning at 900 degrees a second presses the moment the crosshair finds the item, at 100 ms
        /// ping with jitter and loss (so many presses arrive late and are applied on a later tick, when the view has
        /// turned a long way on): never refused, never flagged.
        /// </summary>
        [Test]
        public void HonestPickupsWhileTurningFast_At100msWithJitterAndLoss_AreNeverRefused()
        {
            var h = Arena();
            Vec3d feet = SculptNetHarness.FeetIn(10, 10);
            var a = h.AddClient(feet, up: SculptNetHarness.Lag(50, 30, 10), down: SculptNetHarness.Lag(50, 30, 10));
            h.RunSeconds(0.5);
            var rng = new Random(3);
            int picked = 0, pressed = 0, id = -1;
            int lastPress = int.MinValue / 2;
            double spin = 900 * Math.PI / 180 / SimTime.TicksPerSecond * (rng.Next(2) == 0 ? 1 : -1);
            a.Script = (p, t) =>
            {
                var i = new SculptTickInput { Pitch = 40 };
                i.Move.Yaw = InputCmd.QuantizeYaw(t * spin);
                MotorState s = p.Client.Predicted;
                Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, p.Client.Movement), 0);
                Vec3d look = WeaponRules.AimDir(i.Move.Yaw, i.Pitch, 0, 0);
                int k = ItemRules.FindTarget(p.Client.Items, p.Client.Grid, eye, look, p.Client.Movement.Items);
                if (k >= 0 && p.Client.Items[k].Id == id && t - lastPress > 30)
                {
                    i.Hand.Pickup = true;
                    i.Hand.PickId = id;
                    lastPress = t;
                    pressed++;
                }
                return i;
            };
            for (int trial = 0; trial < 30; trial++)
            {
                a.ServerSide.State.Hand.Bag = default; // room for it
                double ang = rng.NextDouble() * 2 * Math.PI;
                id = h.Server.SpawnItem(Item.Bandage, 1, a.ServerSide.State.Position + new Vec3d(Math.Sin(ang) * 1.4, 0.5, Math.Cos(ang) * 1.4));
                for (int k = 0; k < 300 && h.Server.Items.TryGet(id, out _); k++) h.RunMs(SculptNetHarness.TickMs);
                if (!h.Server.Items.TryGet(id, out _)) picked++;
                else h.Server.Items.Remove(id);
            }
            Assert.That(h.Server.Flags.Count(FlagKind.PickupOffAim), Is.Zero, "no honest press is flagged");
            Assert.That(picked, Is.EqualTo(30), $"every item picked up ({pressed} presses)");
            Assert.That(a.ServerSide.LateInputs, Is.GreaterThan(0), "some presses really arrived late");
        }
    }
}
