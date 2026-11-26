using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>
    /// The inventory rework (Ben 2026-11-26): five generic slots (any item in any slot), auto-sort on pickup by the
    /// player's slot preferences (default pump 1, AR 2, heals 5), a manual swap (the I screen), duplicate guns that give
    /// no edge, and bandages (+25) and first-aid kits (+100).
    /// </summary>
    public class InventoryTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();
        private static HealItemConfig Heal => Mc.Items.Heal;

        private static SubGrid Flat()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private static MotorState Settled(ITerrain t, Bag bag, int slot = 0, int damage = 0)
        {
            var s = MotorState.At(new Vec3d(8, 0, 6));
            s.Hand.Bag = bag;
            s.Hand.Slot = (byte)slot;
            s.Hand.PrevSlot = (byte)slot;
            s.Damage = damage;
            for (int k = 1; k <= 30 * SimTime.Per60; k++)
            {
                s.Tick = k - 1;
                PlayerMovement.Step(t, Mc, ref s, default, null);
            }
            return s;
        }

        private static void Run(ITerrain t, ref MotorState s, int ticks, Func<int, InputCmd> cmd, List<WeaponShot> shots = null)
        {
            int start = s.Tick + 1;
            for (int k = 0; k < ticks; k++)
            {
                InputCmd c = InputCmd.Quantize(cmd(start + k));
                c.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, shots);
            }
        }

        private static InputCmd Cmd(int tick, bool fire = false, byte key = 0, bool punch = false, bool sprint = false, double forward = 0,
            bool swap = false, byte a = 0, byte b = 0) => new InputCmd
        {
            Tick = tick,
            Move = new MoveInput { FireHeld = fire, FirePressed = fire, WantsToShoot = fire, SprintHeld = sprint, MoveZ = forward },
            Hand = new HandInput { WeaponKey = key, Punch = punch, Swap = swap, SwapA = a, SwapB = b },
        };

        // ================================================================ placing pickups by preference

        [Test]
        public void Place_PreferredEmptySlot_ThenMovesAnOccupantThatDoesNotBelong_ThenAnyFreeSlot()
        {
            SlotPrefs prefs = SlotPrefs.Default;
            // Empty bag: pump to 1, AR to 2, bandage to 5, an SMG to the first free "any" slot (3).
            var empty = new Bag();
            Assert.That(prefs.Place(empty, Item.Pump, out _, out _), Is.EqualTo(0));
            Assert.That(prefs.Place(empty, Item.Ar, out _, out _), Is.EqualTo(1));
            Assert.That(prefs.Place(empty, Item.Bandage, out _, out _), Is.EqualTo(4));
            Assert.That(prefs.Place(empty, Item.Medkit, out _, out _), Is.EqualTo(4));
            Assert.That(prefs.Place(empty, Item.Smg, out int mf, out int mt), Is.EqualTo(2));
            Assert.That((mf, mt), Is.EqualTo((-1, -1)), "nothing to move");

            // An SMG sitting in the AR's slot moves to a free slot so the AR can have it.
            var b = new Bag { S0 = Item.Pump, S1 = Item.Smg };
            Assert.That(prefs.Place(b, Item.Ar, out mf, out mt), Is.EqualTo(1));
            Assert.That((mf, mt), Is.EqualTo((1, 2)));

            // A second pump finds its slot taken by a pump (it belongs there), so it takes a free "any" slot.
            Assert.That(prefs.Place(b, Item.Pump, out mf, out mt), Is.EqualTo(2));
            Assert.That(mf, Is.EqualTo(-1));

            // Full bag: no slot (the caller swaps with what you hold).
            var full = new Bag { S0 = Item.Pump, S1 = Item.Ar, S2 = Item.Smg, S3 = Item.Pistol, S4 = Item.Bandage, N4 = 2 };
            Assert.That(prefs.Place(full, Item.Launcher, out _, out _), Is.LessThan(0));
        }

        [Test]
        public void Pickup_FollowsThePlayersOwnPrefs_AndAFullBagSwapsWithTheHeldItem()
        {
            var items = new GroundItems();
            var s = MotorState.At(new Vec3d(8, 0, 6));
            s.Hand.Bag = new Bag { S0 = Item.Pump };
            Vec3d eye = s.Position + new Vec3d(0, 1.62, 0);
            var mine = new SlotPrefs { P0 = SlotPref.Pump, P1 = SlotPref.Smg, P2 = SlotPref.Heals };
            int smg = items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, smg, null, eye, 1, 100, Mc, mine), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag[1], Is.EqualTo(Item.Smg));
            int band = items.Add(new GroundItem { Kind = Item.Bandage, Count = 3, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, band, null, eye, 1, 100, Mc, mine), Is.EqualTo(PickupResult.Ok));
            Assert.That((s.Hand.Bag[2], s.Hand.Bag.CountIn(2)), Is.EqualTo((Item.Bandage, 3)));
            // More bandages stack into the same slot, up to the carry limit; the rest stay down.
            int band2 = items.Add(new GroundItem { Kind = Item.Bandage, Count = 5, Position = new Vec3d(8, 0, 7) });
            Assert.That(ItemRules.TryPickup(ref s, items, band2, null, eye, 1, 100, Mc, mine), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag.CountIn(2), Is.EqualTo(Mc.Items.BandageCarry));
            Assert.That(items[items.IndexOf(band2)].Count, Is.EqualTo(5 - (Mc.Items.BandageCarry - 3)));

            // Fill the bag, then a pickup swaps with the held item, which lands where the pickup lay.
            s.Hand.Bag.S3 = Item.Ar; s.Hand.Bag.S4 = Item.Pistol;
            s.Hand.Slot = 3;
            int kit = items.Add(new GroundItem { Kind = Item.Medkit, Count = 1, Position = new Vec3d(8, 0, 7.5) });
            Assert.That(ItemRules.TryPickup(ref s, items, kit, null, eye, 1, 100, Mc, mine), Is.EqualTo(PickupResult.Swapped));
            Assert.That(s.Hand.Bag[3], Is.EqualTo(Item.Medkit));
            bool arDown = false;
            for (int i = 0; i < items.Count; i++) if (items[i].Kind == Item.Ar && Math.Abs(items[i].Position.Z - 7.5) < 1e-6) arDown = true;
            Assert.That(arDown, "the AR went down where the kit lay");
        }

        // ================================================================ duplicates give no edge

        [Test]
        public void DoublePump_SwappingToTheSecondPump_NeverFiresSooner_ThanOnePump()
        {
            ITerrain t = Flat();
            // One pump: fire, keep holding fire. Two pumps: fire, swap to the second at once, keep holding fire.
            long Second(bool two)
            {
                var bag = two ? new Bag { S0 = Item.Pump, S1 = Item.Pump } : new Bag { S0 = Item.Pump, S1 = Item.Smg };
                var s = Settled(t, bag);
                var shots = new List<WeaponShot>();
                int t0 = s.Tick + 1;
                Run(t, ref s, 120 * SimTime.Per60, k => Cmd(k, fire: true, key: (byte)(two && k == t0 + 1 ? 2 : 0)), shots);
                var ticks = new List<int>();
                foreach (WeaponShot w in shots) if (!ticks.Contains(w.Tick)) ticks.Add(w.Tick);
                Assert.That(ticks.Count, Is.GreaterThanOrEqualTo(2), "two shots in two seconds");
                return ticks[1] - ticks[0];
            }
            long one = Second(false), two = Second(true);
            TestContext.WriteLine($"one pump: {one} ticks between shots, swap to a second pump: {two}");
            Assert.That(two, Is.GreaterThanOrEqualTo(one), "the second pump shares the fire timer");
        }

        [Test]
        public void DuplicateCopies_ShareOneMagazine_DroppingOneNeverRefillsTheOther()
        {
            ITerrain t = Flat();
            var items = new GroundItems();
            var s = Settled(t, new Bag { S0 = Item.Ar, S1 = Item.Ar, S2 = Item.Pump });
            s.Weapon.SetUsed(Weapon.Ar, 10);
            Assert.That(ItemRules.TryDrop(ref s, items, t, 0, 1, 200, Mc), Is.True);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items[0].AmmoUsed, Is.EqualTo(10), "the dropped copy keeps the shared magazine");
            Assert.That(s.Weapon.UsedOf(Weapon.Ar), Is.EqualTo(10), "the copy you keep is not refilled");
            // Dropping the last copy clears the per-type magazine (the next AR comes with its own).
            s.Hand.Slot = 1;
            Assert.That(ItemRules.TryDrop(ref s, items, t, 0, 1, 260, Mc), Is.True);
            Assert.That(s.Weapon.UsedOf(Weapon.Ar), Is.EqualTo(0));
        }

        [Test]
        public void DuplicateGun_Rarity_IsTheHeldCopy()
        {
            var b = new Bag { S0 = Item.Ar, S1 = Item.Ar };
            b.SetRarity(0, Rarity.Common);
            b.SetRarity(1, Rarity.Legendary);
            Assert.That(b.RarityOfGun(Weapon.Ar, 1), Is.EqualTo(Rarity.Legendary));
            Assert.That(b.RarityOfGun(Weapon.Ar, 0), Is.EqualTo(Rarity.Common));
        }

        // ================================================================ bandages and first-aid kits

        [Test]
        public void Bandage_HealsTwentyFiveAtTheEnd_NotBefore_AndHoldingFireChainsTheNext()
        {
            ITerrain t = Flat();
            var s = Settled(t, new Bag { S0 = Item.Pump, S4 = Item.Bandage, N4 = 2 }, damage: 60);
            int t0 = s.Tick + 1;
            int draw = Mc.Hand.HealItemDrawTicks, use = Heal.TicksOf(Item.Bandage);
            Run(t, ref s, draw + use - 1, k => Cmd(k, fire: true, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(s.Damage, Is.EqualTo(60), "nothing before the use time is up");
            Assert.That(s.Hand.Bag.CountIn(4), Is.EqualTo(2), "nothing used up early");
            Run(t, ref s, 3, k => Cmd(k, fire: true));
            Assert.That(s.Damage, Is.EqualTo(35));
            Assert.That(s.Hand.Bag.CountIn(4), Is.EqualTo(1));
            Run(t, ref s, use + 3, k => Cmd(k, fire: true));
            Assert.That(s.Damage, Is.EqualTo(10), "fire held: the next one");
            Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None), "the stack is gone");
            Assert.That(s.Hand.Slot, Is.EqualTo(0), "the previous slot is drawn");
        }

        [Test]
        public void Medkit_NeverHealsPastMax_AndNothingIsUsedAtFullHealth()
        {
            ITerrain t = Flat();
            var s = Settled(t, new Bag { S0 = Item.Pump, S4 = Item.Medkit, N4 = 2 }, damage: 30);
            int t0 = s.Tick + 1;
            Run(t, ref s, Mc.Hand.HealItemDrawTicks + Heal.TicksOf(Item.Medkit) + 2, k => Cmd(k, fire: k < t0 + 60, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(s.Damage, Is.EqualTo(0));
            Assert.That(s.Hand.Bag.CountIn(4), Is.EqualTo(1));
            Run(t, ref s, Heal.TicksOf(Item.Medkit) + 20, k => Cmd(k, fire: true));
            Assert.That(s.Hand.Bag.CountIn(4), Is.EqualTo(1), "full health: fire does nothing");
            Assert.That(s.Hand.Using, Is.False);
        }

        [Test]
        public void SwappingOrPunching_CancelsTheUse_AndNothingIsUsed_DamageDoesNot()
        {
            ITerrain t = Flat();
            foreach (string how in new[] { "swap", "punch", "damage" })
            {
                var s = Settled(t, new Bag { S0 = Item.Pump, S4 = Item.Medkit, N4 = 1 }, damage: 120);
                int t0 = s.Tick + 1, mid = t0 + Mc.Hand.HealItemDrawTicks + Heal.TicksOf(Item.Medkit) / 2;
                Run(t, ref s, Mc.Hand.HealItemDrawTicks + Heal.TicksOf(Item.Medkit) / 2 + 1, k => Cmd(k, fire: true, key: (byte)(k == t0 ? 5 : 0)));
                Assert.That(s.Hand.Using, Is.True, how);
                if (how == "damage") s.Damage += 20;
                Run(t, ref s, Heal.TicksOf(Item.Medkit), k => Cmd(k, fire: false, key: (byte)(how == "swap" && k == mid + 2 ? 1 : 0), punch: how == "punch" && k == mid + 2));
                if (how == "damage")
                {
                    Assert.That(s.Damage, Is.EqualTo(40), "taking damage does not stop it");
                    Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None));
                }
                else
                {
                    Assert.That(s.Damage, Is.EqualTo(120), how);
                    Assert.That(s.Hand.Bag.CountIn(4), Is.EqualTo(1), how + ": nothing used");
                    Assert.That(s.Hand.Using, Is.False, how);
                }
            }
        }

        [Test]
        public void WhileUsing_YouWalk_NoSprint()
        {
            ITerrain t = Flat();
            var s = Settled(t, new Bag { S0 = Item.Pump, S4 = Item.Medkit, N4 = 1 }, damage: 120);
            int t0 = s.Tick + 1;
            Run(t, ref s, Mc.Hand.HealItemDrawTicks + 60 * SimTime.Per60, k => Cmd(k, fire: true, sprint: true, forward: 1, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(s.Hand.Using, Is.True);
            Assert.That(s.Velocity.HorizontalLength, Is.EqualTo(Mc.RunSpeed * Heal.MoveSpeedScale).Within(0.05));
        }

        [Test]
        public void WhileUsing_NoNewSlideStarts_FromASprintOrALanding()
        {
            ITerrain t = Flat();
            // Sprinting at full speed, then switch to bandages and start one while pressing crouch: no slide.
            var s = Settled(t, new Bag { S0 = Item.Pump, S4 = Item.Bandage, N4 = 3 }, damage: 100);
            Run(t, ref s, 60 * SimTime.Per60, k => Cmd(k, sprint: true, forward: 1));
            Assert.That(s.Sprinting, Is.True, "sprinting before");
            int t0 = s.Tick + 1;
            int ready = t0 + Mc.Hand.HealItemDrawTicks;
            Run(t, ref s, Mc.Hand.HealItemDrawTicks + 4, k =>
            {
                InputCmd c = Cmd(k, fire: k >= ready, sprint: true, forward: 1, key: (byte)(k == t0 ? 5 : 0));
                if (k == ready) { c.Move.CrouchPressed = true; c.Move.CrouchHeld = true; }
                return c;
            });
            Assert.That(s.Hand.Using, Is.True, "the bandage is in use");
            Assert.That(s.Mode, Is.Not.EqualTo(MoveMode.Slide), "no slide start from a sprint while using a heal");

            // Using one and jumping at speed (a hop started before the heal), then holding crouch on landing: no slide-land.
            s.Mode = MoveMode.Ground;
            s.Velocity = new Vec3d(0, 0, 10);
            Run(t, ref s, 2, k => { InputCmd c = Cmd(k, fire: true, forward: 1); c.Move.JumpPressed = true; return c; });
            Run(t, ref s, 40 * SimTime.Per60, k => { InputCmd c = Cmd(k, fire: true, forward: 1); c.Move.CrouchHeld = true; c.Move.CrouchPressed = true; return c; });
            Assert.That(s.Hand.Using || s.Damage < 100, Is.True);
            Assert.That(s.Mode, Is.Not.EqualTo(MoveMode.Slide), "no slide-land while using a heal");
        }

        // ================================================================ manual swap (the I screen)

        [Test]
        public void SwapCommand_MovesTheItems_AndTheHandFollowsWhatItHeld()
        {
            ITerrain t = Flat();
            var s = Settled(t, new Bag { S0 = Item.Pump, S1 = Item.Smg, S4 = Item.Bandage, N4 = 4 }, slot: 0);
            s.Hand.PrevSlot = 1;
            Run(t, ref s, 1, k => Cmd(k, swap: true, a: 0, b: 4));
            Assert.That((s.Hand.Bag[0], s.Hand.Bag[4], s.Hand.Bag.CountIn(0)), Is.EqualTo((Item.Bandage, Item.Pump, 4)));
            Assert.That(s.Hand.Slot, Is.EqualTo(4), "still holding the pump");
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Pump));
            Assert.That(s.Hand.PrevSlot, Is.EqualTo(1));
            Assert.That(s.Hand.Mode, Is.EqualTo(HandMode.Weapon), "no redraw");
        }

        [Test]
        public void SwapCommand_RoundTripsOnTheWire_AndJunkIsDropped()
        {
            InputCmd c = InputCmd.Quantize(Cmd(77, swap: true, a: 1, b: 3));
            var cmds = new List<InputCmd> { c };
            var w = new ByteWriter(64);
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = 5, Cmds = cmds });
            byte[] d = w.ToArray();
            var r = new ByteReader(d, 0, d.Length);
            var into = new List<InputCmd>();
            Protocol.ReadInputs(ref r, into);
            Assert.That((into[0].Hand.Swap, into[0].Hand.SwapA, into[0].Hand.SwapB), Is.EqualTo((true, (byte)1, (byte)3)));
            foreach (var (a, b) in new[] { (2, 2), (0, 5), (7, 1) })
                Assert.That(InputCmd.Quantize(Cmd(1, swap: true, a: (byte)a, b: (byte)b)).Hand.Swap, Is.False, $"{a},{b}");
        }

        [Test]
        public void PrefsMsg_RoundTrips_AndRejectsJunk()
        {
            var m = new PrefsMsg { Slots = new SlotPrefs { P0 = SlotPref.Launcher, P1 = SlotPref.Heals, P4 = SlotPref.Pistol }, Locker = Item.Smg };
            var w = new ByteWriter(16);
            Protocol.Write(w, m);
            byte[] d = w.ToArray();
            var r = new ByteReader(d, 0, d.Length);
            PrefsMsg back = Protocol.ReadPrefs(ref r);
            Assert.That(back.Slots.Equals(m.Slots));
            Assert.That(back.Locker, Is.EqualTo(Item.Smg));
            byte[] bad = (byte[])d.Clone(); bad[1] = (byte)SlotPrefs.Last + 1;
            Assert.Throws<NetFormatException>(() => { var b = new ByteReader(bad, 0, bad.Length); Protocol.ReadPrefs(ref b); });
            bad = (byte[])d.Clone(); bad[6] = (byte)Item.Bandage;
            Assert.Throws<NetFormatException>(() => { var b = new ByteReader(bad, 0, bad.Length); Protocol.ReadPrefs(ref b); }, "a locker must be a gun");
        }

        // ================================================================ networked: predicted exactly

        [Test]
        public void Networked_HealAndSwap_ArePredictedWithNoCorrection_At100msPing()
        {
            var h = new SculptNetHarness(Flat);
            var a = h.AddClient(new Vec3d(8, 0, 6), up: SculptNetHarness.Lag(50, 5), down: SculptNetHarness.Lag(50, 5));
            h.RunSeconds(1.0);
            // Hurt them and give them bandages on the server (one resync), then let it settle.
            a.ServerSide.State.Damage = 80;
            a.ServerSide.State.Hand.Bag.S4 = Item.Bandage;
            a.ServerSide.State.Hand.Bag.N4 = 3;
            h.RunSeconds(0.6);
            Assert.That(a.Client.Predicted.Damage, Is.EqualTo(80));
            int before = a.Client.Corrections + a.Client.SilentResyncs;
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 2) i.Hand.WeaponKey = 5;
                i.Move.FireHeld = i.Move.WantsToShoot = t > start + 2 && t < start + 2 * SimTime.TicksPerSecond;
                i.Move.FirePressed = t == start + 30;
                i.Move.MoveZ = 1;
                if (t == start + 3 * SimTime.TicksPerSecond) { i.Hand.Swap = true; i.Hand.SwapA = 0; i.Hand.SwapB = 4; }
                return i;
            };
            h.RunSeconds(4.0);
            Assert.That(a.ServerSide.State.Damage, Is.LessThan(80), "it healed on the server");
            Assert.That(a.Client.Predicted.Damage, Is.EqualTo(a.ServerSide.State.Damage));
            Assert.That(a.ServerSide.State.Hand.Bag[0], Is.EqualTo(Item.Bandage), "the swap reached the server");
            Assert.That(a.Client.Corrections + a.Client.SilentResyncs - before, Is.Zero, "predicted bit for bit");
        }
    }
}
