using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Hotbar items, pickup and drop (Ben 2026-11-21: every item is a hotbar item picked up or dropped with interact).</summary>
    public class ItemTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();

        /// <summary>Flat ground (top at y 0) with a 4-tile wall at z 12 from x 6 to 12.</summary>
        private static ITerrain Arena()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 4, 2);
            w.Tiles.Changes.Clear();
            return new CompositeTerrain(g, w);
        }

        private static MotorState Standing(Vec3d feet)
        {
            var s = MotorState.At(feet);
            s.Mode = MoveMode.Ground;
            s.Grounded = true;
            return s;
        }

        private static Vec3d Eye(in MotorState s) => s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, Mc), 0);

        [Test]
        public void StandardLoadout_FourGunsAndTwoGrenadesInSlotFive()
        {
            HandState h = HandState.Initial;
            Assert.That(h.Bag[0], Is.EqualTo(Item.Pump));
            Assert.That(h.Bag[1], Is.EqualTo(Item.Smg));
            Assert.That(h.Bag[2], Is.EqualTo(Item.Ar));
            Assert.That(h.Bag[3], Is.EqualTo(Item.Pistol));
            Assert.That(h.Bag[4], Is.EqualTo(Item.Grenade));
            Assert.That(h.Bag.Grenades, Is.EqualTo(2));
            Assert.That(h.HeldItem, Is.EqualTo(Item.Pump));
            Assert.That(h.CanFire, Is.True);
        }

        [Test]
        public void SlotFiveDrawsGrenades_WhichCannotFire()
        {
            HandState h = HandState.Initial;
            HandRules.Step(ref h, 10, new HandInput { WeaponKey = 5 }, Mc.Hand);
            Assert.That(h.HeldItem, Is.EqualTo(Item.Grenade));
            Assert.That(h.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(h.ReadyTick, Is.EqualTo(10 + Mc.Hand.GrenadeDrawTicks));
            HandRules.Step(ref h, 10 + Mc.Hand.GrenadeDrawTicks, default, Mc.Hand);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Weapon));
            Assert.That(h.ItemReady, Is.True);
            Assert.That(h.CanFire, Is.False, "a grenade is not a gun");
        }

        [Test]
        public void NoGunInHand_FireNeverShoots()
        {
            var s = Standing(new Vec3d(8, 0, 8));
            var shots = new List<WeaponShot>();
            for (int t = 1; t <= 120; t++)
            {
                s.Tick = t;
                var hand = new HandInput { WeaponKey = (byte)(t == 1 ? 5 : 0) };
                HandRules.Step(ref s.Hand, t, hand, Mc.Hand);
                WeaponRules.Step(ref s, t, new MoveInput { FireHeld = true, FirePressed = t % 10 == 0, WantsToShoot = true }, 0, Mc, shots, t == 50);
            }
            Assert.That(shots, Is.Empty);
            Assert.That(s.Weapon.Reloading, Is.False);
        }

        [Test]
        public void EmptySlotKeyDoesNothing()
        {
            HandState h = HandState.Initial;
            h.Bag.Clear(2);
            HandRules.Step(ref h, 10, new HandInput { WeaponKey = 3 }, Mc.Hand);
            Assert.That(h.Slot, Is.EqualTo(0));
            Assert.That(h.Mode, Is.EqualTo(HandMode.Weapon), "no draw started");
        }

        [Test]
        public void DropHeldGun_LandsInFrontKeepsAmmo_PreviousSlotDrawn()
        {
            ITerrain t = Arena();
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Held = Weapon.Ar;          // slot 3
            s.Hand.PrevSlot = 1;              // came from the SMG
            s.Weapon.SetUsed(Weapon.Ar, 7);
            Assert.That(ItemRules.TryDrop(ref s, items, t, 0, 1, 100, Mc), Is.True);

            Assert.That(items.Count, Is.EqualTo(1));
            GroundItem g = items[0];
            Assert.That(g.Kind, Is.EqualTo(Item.Ar));
            Assert.That(g.AmmoUsed, Is.EqualTo(7), "a dropped gun keeps its magazine");
            Assert.That(g.Position.Z, Is.EqualTo(6 + Mc.Items.DropForward).Within(1e-6), "in front (yaw 0 faces +Z)");
            Assert.That(g.Position.Y, Is.EqualTo(0).Within(1e-6), "resting on the ground");
            Assert.That(s.Hand.Bag[2], Is.EqualTo(Item.None));
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Smg), "previous slot comes out");
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(s.Weapon.UsedOf(Weapon.Ar), Is.EqualTo(0));
        }

        [Test]
        public void DropFacingAWall_LandsAtYourFeetNotThroughIt()
        {
            ITerrain t = Arena();
            var items = new GroundItems();
            var s = Standing(new Vec3d(7.5, 0, 11.4)); // the wall face is at z 12 (0.6 m ahead, inside the drop distance)
            Assert.That(ItemRules.TryDrop(ref s, items, t, 0, 1, 100, Mc), Is.True);
            Assert.That(items[0].Position.Z, Is.LessThan(12), "stays on your side of the wall");
            Assert.That(items[0].Position.Z, Is.EqualTo(11.4).Within(1e-6));
        }

        [Test]
        public void CannotDropInSculptModeOrEmptyHanded()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Mode = HandMode.Sculpt;
            Assert.That(ItemRules.TryDrop(ref s, items, null, 0, 1, 100, Mc), Is.False);
            s.Hand = HandState.Initial;
            for (int k = 0; k < Items.Slots; k++) s.Hand.Bag.Clear(k);
            Assert.That(ItemRules.TryDrop(ref s, items, null, 0, 1, 100, Mc), Is.False);
            Assert.That(items.Count, Is.EqualTo(0));
        }

        [Test]
        public void DropLastItem_LeavesEmptyHands()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            for (int k = 1; k < Items.Slots; k++) s.Hand.Bag.Clear(k);
            Assert.That(ItemRules.TryDrop(ref s, items, null, 0, 1, 100, Mc), Is.True);
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.None));
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Weapon));
            Assert.That(s.Hand.CanFire, Is.False);
        }

        [Test]
        public void PickupIntoEmptyHeldSlot_DrawsItWithItsAmmo()
        {
            ITerrain t = Arena();
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Held = Weapon.Ar;
            s.Weapon.SetUsed(Weapon.Ar, 5);
            ItemRules.TryDrop(ref s, items, t, 0, 1, 100, Mc);
            s.Hand.Held = Weapon.Smg;
            s.Hand.Slot = 2; // back to the (now empty) AR slot
            s.Hand.Mode = HandMode.Weapon;

            int id = items[0].Id;
            Assert.That(ItemRules.TryPickup(ref s, items, id, t, Eye(s), 1, 100 + Mc.Items.DropperLockTicks, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(items.Count, Is.EqualTo(0));
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Ar));
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(s.Weapon.UsedOf(Weapon.Ar), Is.EqualTo(5), "the magazine came back as it was dropped");
        }

        [Test]
        public void PickupGoesToFirstEmptySlotWhenHeldSlotIsFull_NoDraw()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Bag.Clear(3); // no pistol
            int id = items.Add(new GroundItem { Kind = Item.Pistol, Count = 1, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag[3], Is.EqualTo(Item.Pistol));
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Pump), "still holding the pump");
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Weapon));
        }

        [Test]
        public void DuplicateGunIsRefused()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            int id = items.Add(new GroundItem { Kind = Item.Ar, Count = 1, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.AlreadyHave));
            Assert.That(items.Count, Is.EqualTo(1));
        }

        [Test]
        public void GrenadesStackToTheCarryLimit_RestStayOnTheGround()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Bag.Grenades = 1;
            int id = items.Add(new GroundItem { Kind = Item.Grenade, Count = 3, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag.Grenades, Is.EqualTo(Mc.Items.GrenadeCarry));
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items[0].Count, Is.EqualTo(2), "took one, two left");
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.AlreadyHave), "full stack");
        }

        [Test]
        public void FullBag_SwapsWithHeldItem_WhichLandsWhereThePickupLay()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            // Full bag without an AR: pump, SMG, pistol, grenades, and a second pistol entry standing in for any
            // fifth item (one of each gun is the rule; the duplicate only fills the slot for this test). Hold the pump.
            s.Hand.Bag = new Bag { S0 = Item.Pump, S1 = Item.Smg, S2 = Item.Pistol, S3 = Item.Grenade, S4 = Item.Pistol, Grenades = 2 };
            s.Hand.Slot = 0;
            Vec3d lay = new Vec3d(8, 0, 7);
            int id = items.Add(new GroundItem { Kind = Item.Ar, Count = 1, AmmoUsed = 3, Position = lay });
            s.Weapon.SetUsed(Weapon.Pump, 2);
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.Swapped));
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Ar));
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(s.Weapon.UsedOf(Weapon.Ar), Is.EqualTo(3));
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items[0].Kind, Is.EqualTo(Item.Pump));
            Assert.That(items[0].AmmoUsed, Is.EqualTo(2));
            Assert.That(items[0].Position, Is.EqualTo(lay));
        }

        [Test]
        public void DropperCannotGrabItBackStraightAway_OthersCan()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            ItemRules.TryDrop(ref s, items, null, 0, 1, 100, Mc);
            int id = items[0].Id;
            var other = Standing(new Vec3d(8, 0, 6));
            other.Hand.Bag.Clear(0);
            Assert.That(ItemRules.Plan(s, items[0], 1, 101, Mc.Items).Result, Is.EqualTo(PickupResult.NotNow));
            Assert.That(ItemRules.Plan(other, items[0], 2, 101, Mc.Items).Allowed, Is.True);
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100 + Mc.Items.DropperLockTicks, Mc), Is.EqualTo(PickupResult.Ok));
        }

        [Test]
        public void CannotPickUpWhileDeadOrDisarmed()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Bag.Clear(1);
            int id = items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(8, 0, 7) });
            s.Hand.Mode = HandMode.Disarmed;
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.NotNow));
            s.Hand.Mode = HandMode.Weapon;
            s.Dead = true;
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.NotNow));
        }

        [Test]
        public void Target_ClosestToCrosshairInReachAndCone()
        {
            var items = new GroundItems();
            Vec3d eye = new Vec3d(8, 1.6, 6);
            int ahead = items.Add(new GroundItem { Kind = Item.Ar, Count = 1, Position = new Vec3d(8, 0, 7.2) });
            items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(9.2, 0, 7.2) });    // off to the side
            items.Add(new GroundItem { Kind = Item.Pump, Count = 1, Position = new Vec3d(8, 0, 9.5) });     // out of reach
            Vec3d look = (new Vec3d(8, 0.15, 7.2) - eye).Normalized;
            int i = ItemRules.FindTarget(items, null, eye, look, Mc.Items);
            Assert.That(items[i].Id, Is.EqualTo(ahead));
            Assert.That(ItemRules.FindTarget(items, null, eye, new Vec3d(0, 1, 0), Mc.Items), Is.EqualTo(-1), "looking at the sky");
        }

        [Test]
        public void Target_NotThroughWalls_AndServerRefusesIt()
        {
            ITerrain t = Arena();
            var items = new GroundItems();
            int id = items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(7.5, 0, 12.8) }); // behind the wall
            var s = Standing(new Vec3d(7.5, 0, 11.2));
            s.Hand.Bag.Clear(1);
            Vec3d eye = Eye(s);
            Vec3d look = (items[0].Centre - eye).Normalized;
            Assert.That(ItemRules.FindTarget(items, t, eye, look, Mc.Items), Is.EqualTo(-1));
            Assert.That(ItemRules.TryPickup(ref s, items, id, t, eye, 1, 100, Mc), Is.EqualTo(PickupResult.Blocked));
        }

        [Test]
        public void ServerRefusesFarPickups()
        {
            var items = new GroundItems();
            var s = Standing(new Vec3d(8, 0, 6));
            s.Hand.Bag.Clear(1);
            int id = items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(8, 0, 10) });
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, Eye(s), 1, 100, Mc), Is.EqualTo(PickupResult.TooFar));
        }

        [Test]
        public void GroundList_DropsOldestPastTheCap()
        {
            var items = new GroundItems();
            for (int k = 0; k < 5; k++) items.Add(new GroundItem { Kind = Item.Grenade, Count = 1 }, 3);
            Assert.That(items.Count, Is.EqualTo(3));
            Assert.That(items[0].Id, Is.EqualTo(3), "ids 1 and 2 went first");
        }

        // ---------------------------------------------------------------- Wire

        [Test]
        public void InputCmd_PickupAndDropRoundTrip()
        {
            var c = InputCmd.Quantize(new InputCmd { Tick = 50, Hand = new HandInput { Pickup = true, PickId = 123456, WeaponKey = 5 } });
            var d = InputCmd.Quantize(new InputCmd { Tick = 51, Hand = new HandInput { Drop = true } });
            var w = new ByteWriter(64);
            Protocol.Write(w, new InputsMsg { Cmds = new List<InputCmd> { c, d } });
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<InputCmd>();
            Protocol.ReadInputs(ref r, into);
            Assert.That(into[0].Hand.Pickup, Is.True);
            Assert.That(into[0].Hand.PickId, Is.EqualTo(123456));
            Assert.That(into[0].Hand.WeaponKey, Is.EqualTo(5));
            Assert.That(into[0].Hand.Drop, Is.False);
            Assert.That(into[1].Hand.Drop, Is.True);
            Assert.That(into[1].Hand.Pickup, Is.False);
        }

        [Test]
        public void MotorState_BagRoundTripsExactly()
        {
            var s = MotorState.At(new Vec3d(1, 2, 3));
            s.Hand.Bag = new Bag { S0 = Item.Ar, S1 = Item.None, S2 = Item.Grenade, S3 = Item.Pump, S4 = Item.None, Grenades = 1 };
            s.Hand.Slot = 2;
            s.Hand.PrevSlot = 3;
            var w = new ByteWriter(512);
            Protocol.WriteMotor(w, s);
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            MotorState back = Protocol.ReadMotor(ref r);
            Assert.That(Protocol.SameMotor(s, back), Is.True);
            Assert.That(back.Hand.HeldItem, Is.EqualTo(Item.Grenade));
            var t = back;
            t.Hand.Bag.Grenades = 2;
            Assert.That(Protocol.SameMotor(s, t), Is.False, "the bag is part of the reconciled state");
        }

        [Test]
        public void ItemsMsg_RoundTripsAndRejectsJunk()
        {
            var list = new List<GroundItem>
            {
                new GroundItem { Id = 7, Kind = Item.Pistol, Count = 1, AmmoUsed = 4, Position = new Vec3d(1.5, 0, -2.25), DroppedBy = 3, DroppedTick = 900 },
                new GroundItem { Id = 8, Kind = Item.Grenade, Count = 2, Position = new Vec3d(0, 4.5, 0), DroppedTick = -5 },
            };
            var w = new ByteWriter(128);
            ItemsProtocol.Write(w, new ItemsMsg { Tick = 77, Items = list });
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<GroundItem>();
            ItemsMsg m = ItemsProtocol.Read(ref r, into);
            Assert.That(m.Tick, Is.EqualTo(77));
            Assert.That(into.Count, Is.EqualTo(2));
            Assert.That(into[0].Kind, Is.EqualTo(Item.Pistol));
            Assert.That(into[0].AmmoUsed, Is.EqualTo(4));
            Assert.That(into[0].Position, Is.EqualTo(new Vec3d(1.5, 0, -2.25)));
            Assert.That(into[1].Count, Is.EqualTo(2));
            data[1 + 4 + 2 + 4] = 9; // first item's kind
            Assert.Throws<NetFormatException>(() => { var b = new ByteReader(data, 0, data.Length); ItemsProtocol.Read(ref b, into); });
        }

        // ---------------------------------------------------------------- Networked

        [Test]
        public void Networked_DropThenPickUp_ServerDecidesAndEveryoneSeesTheGround()
        {
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            });
            var a = h.AddClient(new Vec3d(8, 0, 6), team: 1, up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            var b = h.AddClient(new Vec3d(14, 0, 6), team: 2, up: SculptNetHarness.Lag(30), down: SculptNetHarness.Lag(30), seed: 2);
            int start = -1;
            int pickId = 0;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 30) i.Hand.WeaponKey = 3;           // AR out
                if (t == start + 80) i.Hand.Drop = true;              // drop it in front
                if (t == start + 160 && p.Client.Items.Count > 0)     // look down at it and pick it back up
                {
                    i.Hand.Pickup = true;
                    i.Hand.PickId = pickId = p.Client.Items[0].Id;
                }
                return i;
            };
            h.RunSeconds(110 / 60.0 + 0.2);
            Assert.That(h.Server.Items.Count, Is.EqualTo(1), "the server has the dropped AR");
            Assert.That(h.Server.Items[0].Kind, Is.EqualTo(Item.Ar));
            Assert.That(a.Client.Items.Count, Is.EqualTo(1), "the dropper's copy");
            Assert.That(b.Client.Items.Count, Is.EqualTo(1), "everyone else's copy");
            Assert.That(a.Client.Predicted.Hand.Bag[2], Is.EqualTo(Item.None), "the snapshot emptied the slot");
            Assert.That(a.Client.Predicted.Hand.HeldItem, Is.EqualTo(Item.Pump), "previous slot (pump) drawn");

            var late = h.AddClient(new Vec3d(20, 0, 6), team: 2, seed: 3);
            h.RunSeconds(1.2);
            Assert.That(pickId, Is.Not.EqualTo(0));
            Assert.That(h.Server.Items.Count, Is.EqualTo(0), "picked back up");
            Assert.That(a.Client.Items.Count, Is.EqualTo(0));
            Assert.That(b.Client.Items.Count, Is.EqualTo(0));
            Assert.That(late.Client.Items.Count, Is.EqualTo(0));
            Assert.That(a.Client.Predicted.Hand.Bag[2], Is.EqualTo(Item.Ar), "back in its slot");
            Assert.That(a.ServerSide.State.Hand.Bag[2], Is.EqualTo(Item.Ar));
        }

        [Test]
        public void Networked_LateJoinerGetsTheGroundList()
        {
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            });
            h.Server.SpawnItem(Item.Grenade, 2, new Vec3d(10, 3, 10));
            var a = h.AddClient(new Vec3d(8, 0, 6));
            h.RunSeconds(0.3);
            Assert.That(a.Client.Items.Count, Is.EqualTo(1));
            Assert.That(a.Client.Items[0].Position.Y, Is.EqualTo(0).Within(1e-6), "spawned items settle onto the ground");
            Assert.That(a.Client.Items[0].Count, Is.EqualTo(2));
        }
    }
}
