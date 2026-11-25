using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>
    /// Remote C4 and the grenade launcher (friend demo step 6). Ben's intent: C4 is a breaching tool for attacking a
    /// defender, not a kill weapon (detonate any time, goes off 8 s after landing, only kills someone careless on low
    /// health); the launcher is slow and clunky so you usually lose with it up close, strong for longer-range plays.
    /// </summary>
    public class ThrowablesTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();
        private static GrenadeConfig C4 => Mc.Items.C4;
        private static GrenadeConfig Gl => Mc.Items.Launcher;

        /// <summary>Flat ground (top y 0) with an 8 x 3 tile wall facing south at z 12, x 6..18.</summary>
        private static CompositeTerrain Arena(out TileWalls walls, out int[] ids)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            ids = w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 8, 3);
            w.Tiles.Changes.Clear();
            walls = w.Tiles;
            return new CompositeTerrain(g, w);
        }

        /// <summary>A settled player with <paramref name="item"/> x <paramref name="count"/> in slot 5 instead of grenades.</summary>
        private static MotorState Settled(ITerrain t, Vec3d feet, Item item, int count)
        {
            var s = MotorState.At(feet);
            s.Hand.Bag.S4 = item;
            s.Hand.Bag.N4 = (byte)count;
            for (int k = 1; k <= 30; k++)
            {
                s.Tick = k - 1;
                PlayerMovement.Step(t, Mc, ref s, default, null);
            }
            return s;
        }

        private static List<GrenadeEvent> Run(ITerrain t, ref MotorState s, int ticks, Func<int, InputCmd> cmd)
        {
            var ev = new List<GrenadeEvent>();
            int start = s.Tick + 1;
            for (int k = 0; k < ticks; k++)
            {
                InputCmd c = InputCmd.Quantize(cmd(start + k));
                c.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, null, ev);
            }
            return ev;
        }

        private static InputCmd Cmd(int tick, bool fire, bool aim = false, byte key = 0, double yaw = 0, float pitch = 0,
            bool sprint = false, double forward = 0) => new InputCmd
        {
            Tick = tick, Pitch = pitch,
            Move = new MoveInput { Yaw = yaw, FireHeld = fire, FirePressed = fire, WantsToShoot = fire || aim, AimPressed = aim, SprintHeld = sprint, MoveZ = forward },
            Hand = new HandInput { WeaponKey = key },
        };

        private static List<GrenadeEvent> Of(List<GrenadeEvent> ev, GrenadeEventType t) => ev.FindAll(e => e.Type == t);

        // ================================================================ C4

        [Test]
        public void C4_SticksToTheWall_SlotStaysAsDetonator_AimButtonSetsItOff()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 1);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks;
            var ev = Run(t, ref s, 90, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            List<GrenadeEvent> stuck = Of(ev, GrenadeEventType.Stuck);
            Assert.That(Of(ev, GrenadeEventType.Thrown).Count, Is.EqualTo(1));
            Assert.That(stuck.Count, Is.EqualTo(1), "it sticks instead of going off");
            Assert.That(stuck[0].Kind, Is.EqualTo(ThrowKind.C4));
            Assert.That(stuck[0].Position.Z, Is.EqualTo(12 - 0.05).Within(1e-6), "on the wall's face");
            Assert.That(Of(ev, GrenadeEventType.Exploded), Is.Empty);
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.C4), "the empty C4 slot is the detonator while a charge is out");
            Assert.That(s.Hand.Bag.N4, Is.EqualTo(0));
            Assert.That(s.Nade0.Stuck && s.Nade0.Velocity.Equals(Vec3d.Zero), Is.True);

            int det = s.Tick + 1;
            ev = Run(t, ref s, 3, k => Cmd(k, false, aim: k == det));
            List<GrenadeEvent> boom = Of(ev, GrenadeEventType.Exploded);
            Assert.That(boom.Count, Is.EqualTo(1));
            Assert.That(boom[0].Tick, Is.EqualTo(det), "goes off on the press tick");
            Assert.That(boom[0].Position, Is.EqualTo(stuck[0].Position));
            Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None), "detonator gone once no charge is out");
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Pump), "previous slot drawn");
        }

        [Test]
        public void C4_GoesOffByItself_EightSecondsAfterLanding()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 1);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks;
            var ev = Run(t, ref s, 700, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            GrenadeEvent stuck = Of(ev, GrenadeEventType.Stuck)[0];
            List<GrenadeEvent> boom = Of(ev, GrenadeEventType.Exploded);
            Assert.That(boom.Count, Is.EqualTo(1));
            Assert.That(boom[0].Tick - stuck.Tick, Is.EqualTo(480), "8 s after it landed");
        }

        [Test]
        public void C4_CanNotBeSetOffInTheFirstQuarterSecond_FireWithNoChargeLeftDetonates()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 1);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks;
            int early = ready + C4.DetonateArmTicks - 3, late = ready + C4.DetonateArmTicks + 10;
            var ev = Run(t, ref s, 60, k => Cmd(k, k == ready || k == late, aim: k == early, key: (byte)(k == t0 ? 5 : 0)));
            List<GrenadeEvent> boom = Of(ev, GrenadeEventType.Exploded);
            Assert.That(boom.Count, Is.EqualTo(1), "the early press did nothing");
            Assert.That(boom[0].Tick, Is.EqualTo(late), "fire with no charge in hand is the detonator");
        }

        [Test]
        public void C4_TwoCharges_OnePressSetsOffBoth()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 2);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks, second = ready + C4.ThrowRecoveryTicks + 1;
            int det = second + 60;
            var ev = Run(t, ref s, det - t0 + 5, k => Cmd(k, k == ready || k == second, aim: k == det, key: (byte)(k == t0 ? 5 : 0), yaw: k < second ? -0.2 : 0.2));
            Assert.That(Of(ev, GrenadeEventType.Thrown).Count, Is.EqualTo(2));
            Assert.That(Of(ev, GrenadeEventType.Stuck).Count, Is.EqualTo(2));
            List<GrenadeEvent> boom = Of(ev, GrenadeEventType.Exploded);
            Assert.That(boom.Count, Is.EqualTo(2));
            Assert.That(boom[0].Tick == det && boom[1].Tick == det, Is.True);
            Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None));
        }

        [Test]
        public void C4_AimButtonWithChargesStillInHand_SetsOffTheOneOut_ThenThrowsAgain()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 2);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks, det = ready + 50, again = det + 5;
            var ev = Run(t, ref s, again - t0 + 40, k => Cmd(k, k == ready || k == again, aim: k == det, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(Of(ev, GrenadeEventType.Exploded)[0].Tick, Is.EqualTo(det));
            Assert.That(Of(ev, GrenadeEventType.Thrown).Count, Is.EqualTo(2));
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.C4));
        }

        [Test]
        public void C4_ItsWallBreaks_ItDropsAndSticksToTheFloor_FuseKeepsRunning()
        {
            CompositeTerrain t = Arena(out TileWalls walls, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 1);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks;
            var ev = Run(t, ref s, 60, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            GrenadeEvent first = Of(ev, GrenadeEventType.Stuck)[0];
            var near = new List<int>();
            walls.TilesNear(new Aabb(first.Position - new Vec3d(0.2, 0.2, 0.2), first.Position + new Vec3d(0.2, 0.2, 0.2)), near);
            Assert.That(near.Count, Is.GreaterThan(0));
            foreach (int k in near) walls.Set(k, TileState.Broken, 0, TileCause.Bullet);
            ev = Run(t, ref s, 200, k => Cmd(k, false));
            List<GrenadeEvent> stuck = Of(ev, GrenadeEventType.Stuck);
            Assert.That(stuck.Count, Is.EqualTo(1), "lands again");
            Assert.That(stuck[0].Position.Y, Is.EqualTo(0.05).Within(1e-6), "on the floor");
            Assert.That(s.Nade0.StuckTick, Is.EqualTo(first.Tick), "the fuse counts from the first landing");
            ev = Run(t, ref s, 300, k => Cmd(k, false));
            Assert.That(Of(ev, GrenadeEventType.Exploded)[0].Tick - first.Tick, Is.EqualTo(480));
        }

        [Test]
        public void C4_OwnerDies_ChargesFizzle()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.C4, 1);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.C4DrawTicks;
            Run(t, ref s, 60, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(s.Nade0.Active, Is.True);
            WeaponRules.ApplyDamage(ref s, 500, s.Tick, Mc.Weapons);
            var ev = Run(t, ref s, 2, k => Cmd(k, false));
            Assert.That(Of(ev, GrenadeEventType.Fizzled).Count, Is.EqualTo(1));
            Assert.That(Of(ev, GrenadeEventType.Exploded), Is.Empty);
            Assert.That(s.Nade0.Active, Is.False);
        }

        [Test]
        public void C4_Damage_StandingOnItIs45_SmallRadius_NotAKillWeapon()
        {
            var feet = new Vec3d(0, 0, 0);
            double h = Mc.StandHeight;
            Assert.That(GrenadeRules.Damage(null, new Vec3d(0, 0.05, 0), feet, h, C4, Mc.HalfWidth), Is.EqualTo(45), "standing on it");
            Assert.That(GrenadeRules.Damage(null, new Vec3d(1.3, 0.05, 0), feet, h, C4, Mc.HalfWidth), Is.InRange(30, 44), "a step away");
            Assert.That(GrenadeRules.Damage(null, new Vec3d(2.9, 0.05, 0), feet, h, C4, Mc.HalfWidth), Is.EqualTo(0), "a few steps away");
            Assert.That(C4.EnemyDamage * 3, Is.LessThan(Mc.Weapons.MaxHealth), "never a kill on a healthy player, even with every charge");
            var bc = new BreachConfig();
            Assert.That(bc.C4TileDamage, Is.GreaterThanOrEqualTo(bc.ReinforcedHealth), "breaks reinforced tiles");
            Assert.That(bc.GrenadeTileDamage, Is.LessThan(bc.ReinforcedHealth), "a grenade still doesn't");
        }

        // ================================================================ Launcher

        [Test]
        public void Launcher_UpClose_IsADud()
        {
            CompositeTerrain t = Arena(out TileWalls walls, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7), Item.Launcher, 4);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.LauncherDrawTicks;
            var ev = Run(t, ref s, 80, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            Assert.That(Of(ev, GrenadeEventType.Thrown).Count, Is.EqualTo(1));
            Assert.That(Of(ev, GrenadeEventType.Fizzled).Count, Is.EqualTo(1), "5 m: not armed");
            Assert.That(Of(ev, GrenadeEventType.Exploded), Is.Empty);
            Assert.That(s.Hand.Bag.N4, Is.EqualTo(3));
            Assert.That(s.Velocity.HorizontalLength, Is.LessThan(0.01), "no self push either");
        }

        [Test]
        public void Launcher_PastTheArmingDistance_GoesOffOnTheWall()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 1), Item.Launcher, 4);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.LauncherDrawTicks;
            var ev = Run(t, ref s, 80, k => Cmd(k, k == ready, key: (byte)(k == t0 ? 5 : 0)));
            List<GrenadeEvent> boom = Of(ev, GrenadeEventType.Exploded);
            Assert.That(boom.Count, Is.EqualTo(1));
            Assert.That(boom[0].Kind, Is.EqualTo(ThrowKind.Launcher));
            Assert.That(boom[0].Position.Z, Is.EqualTo(12 - 0.05).Within(1e-6));
            Assert.That(boom[0].Tick - ready, Is.LessThan(25), "fast round: 11 m in well under half a second");
        }

        [Test]
        public void Launcher_OneRoundPerCycle_NoFireWhileSprinting_LastRoundEmptiesTheSlot()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 1), Item.Launcher, 2);
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.LauncherDrawTicks;
            // Hold fire from the moment it is out: rounds leave exactly one cycle apart.
            var ev = Run(t, ref s, 200, k => new InputCmd
            {
                Tick = k,
                Move = new MoveInput { FireHeld = k >= ready, FirePressed = k == ready, WantsToShoot = k >= ready },
                Hand = new HandInput { WeaponKey = (byte)(k == t0 ? 5 : 0) },
            });
            List<GrenadeEvent> thrown = Of(ev, GrenadeEventType.Thrown);
            Assert.That(thrown.Count, Is.EqualTo(1), "holding fire is one round: it is not automatic");

            s = Settled(t, new Vec3d(9, 0, 1), Item.Launcher, 2);
            t0 = s.Tick + 1;
            ready = t0 + Mc.Hand.LauncherDrawTicks;
            int second = ready + 10, third = ready + Gl.ThrowRecoveryTicks + 2;
            ev = Run(t, ref s, 200, k => Cmd(k, k == ready || k == second || k == third, key: (byte)(k == t0 ? 5 : 0)));
            thrown = Of(ev, GrenadeEventType.Thrown);
            Assert.That(thrown.Count, Is.EqualTo(2));
            Assert.That(thrown[1].Tick - thrown[0].Tick, Is.EqualTo(Gl.ThrowRecoveryTicks + 2), "the press inside the cycle is dropped");
            Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None), "out of rounds");
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Pump));

            s = Settled(t, new Vec3d(20, 0, 1), Item.Launcher, 2);
            t0 = s.Tick + 1;
            ready = t0 + Mc.Hand.LauncherDrawTicks;
            ev = Run(t, ref s, 60, k => new InputCmd
            {
                Tick = k,
                Move = new MoveInput { SprintHeld = true, MoveZ = 1, FirePressed = k == ready + 6, FireHeld = k == ready + 6 },
                Hand = new HandInput { WeaponKey = (byte)(k == t0 ? 5 : 0) },
            });
            Assert.That(Of(ev, GrenadeEventType.Thrown), Is.Empty, "a fire press while still sprinting does nothing");
        }

        [Test]
        public void Launcher_InHand_RunsAndSprintsAtNinetyPercent()
        {
            CompositeTerrain t = Arena(out _, out _);
            foreach (Item item in new[] { Item.Grenade, Item.Launcher })
            {
                MotorState s = Settled(t, new Vec3d(20, 0, 1), item, 2);
                int t0 = s.Tick + 1;
                Run(t, ref s, 90, k => Cmd(k, false, key: (byte)(k == t0 ? 5 : 0), sprint: true, forward: 1));
                double expect = Mc.SprintSpeed * (item == Item.Launcher ? Gl.MoveSpeedScale : 1);
                Assert.That(s.Velocity.HorizontalLength, Is.EqualTo(expect).Within(0.05), item.ToString());
            }
        }

        // ================================================================ Bag, pickups, wire

        [Test]
        public void Pickups_StackUpToTheCarryLimit_DetonatorIsReplacedNotDropped()
        {
            var items = new GroundItems();
            var s = MotorState.At(new Vec3d(0, 0, 0));
            s.Hand.Bag.S4 = Item.C4;
            s.Hand.Bag.N4 = 1;
            Vec3d eye = new Vec3d(0, 1.62, 0);
            int id = items.Add(new GroundItem { Kind = Item.C4, Count = 3, Position = new Vec3d(0, 0, 1) });
            Assert.That(ItemRules.TryPickup(ref s, items, id, null, eye, 1, 100, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag.N4, Is.EqualTo(Mc.Items.C4Carry));
            Assert.That(items[0].Count, Is.EqualTo(2), "the rest stays on the ground");

            // A launcher with the bag full swaps with what you hold; a second launcher only tops up rounds.
            int gl = items.Add(new GroundItem { Kind = Item.Launcher, Count = 4, Position = new Vec3d(0, 0, 1) });
            Assert.That(ItemRules.TryPickup(ref s, items, gl, null, eye, 1, 100, Mc), Is.EqualTo(PickupResult.Swapped));
            Assert.That(s.Hand.Bag[0], Is.EqualTo(Item.Launcher));
            Assert.That(s.Hand.Bag.N0, Is.EqualTo(4));
            s.Hand.Bag.N0 = 1;
            int gl2 = items.Add(new GroundItem { Kind = Item.Launcher, Count = 4, Position = new Vec3d(0, 0, 1) });
            Assert.That(ItemRules.TryPickup(ref s, items, gl2, null, eye, 1, 100, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag.N0, Is.EqualTo(Mc.Items.LauncherCarry));

            // The empty detonator can't be dropped, and a pickup into its slot replaces it instead of dropping a 0 stack.
            s.Hand.Slot = 4;
            s.Hand.Bag.N4 = 0;
            s.Nade0 = new GrenadeFlight { Active = true, Kind = ThrowKind.C4, Stuck = true };
            Assert.That(ItemRules.TryDrop(ref s, items, null, 0, 1, 200, Mc), Is.False);
            int before = items.Count;
            int ar = items.Add(new GroundItem { Kind = Item.Ar, Count = 1, Position = new Vec3d(0, 0, 1) });
            s.Hand.Bag.S2 = Item.None; // the AR slot is free, but the held slot is checked after empty slots
            Assert.That(ItemRules.TryPickup(ref s, items, ar, null, eye, 1, 200, Mc), Is.EqualTo(PickupResult.Ok));
            Assert.That(s.Hand.Bag[2], Is.EqualTo(Item.Ar), "the empty slot is used first");
            s.Hand.Bag.S1 = Item.None; s.Hand.Bag.S1 = Item.Pistol; // full bag again
            int smg = items.Add(new GroundItem { Kind = Item.Smg, Count = 1, Position = new Vec3d(0, 0, 1) });
            s.Hand.Bag.S1 = Item.Pistol;
            s.Hand.Bag.S3 = Item.Pump;
            PickupPlan plan = ItemRules.Plan(s, items[items.IndexOf(smg)], 1, 200, Mc.Items);
            Assert.That(plan.Slot, Is.EqualTo(4));
            Assert.That(plan.SwapOut, Is.EqualTo(Item.None), "nothing with a count of 0 goes on the ground");
            Assert.That(items.Count, Is.EqualTo(before + 1));
        }

        [Test]
        public void Wire_StuckC4AndCounts_RoundTripExactly()
        {
            var s = MotorState.At(new Vec3d(3, 1, 2));
            s.Hand.Bag = new Bag { S0 = Item.Launcher, N0 = 3, S1 = Item.C4, N1 = 0, S2 = Item.Ar, S3 = Item.Pistol, S4 = Item.Grenade, N4 = 1 };
            s.Nade0 = new GrenadeFlight { Active = true, Kind = ThrowKind.C4, Position = new Vec3d(1.1, 2.2, 3.3), ThrowTick = 40, Seq = 7, Stuck = true, Landed = true, StuckTick = 55, Normal = new Vec3d(0, 0, -1), Travel = 4.25 };
            s.Nade2 = new GrenadeFlight { Active = true, Kind = ThrowKind.Launcher, Position = new Vec3d(5, 6, 7), Velocity = new Vec3d(0, 1, 30), ThrowTick = 61, Seq = 8, Travel = 9.5 };
            s.Nade3 = new GrenadeFlight { Active = true, Kind = ThrowKind.C4, Position = new Vec3d(1, 1, 1), Landed = true, StuckTick = 70, Normal = new Vec3d(0, 1, 0), ThrowTick = 62, Seq = 9 };
            s.LauncherReadyTick = 127;
            var w = new ByteWriter(512);
            Protocol.WriteMotor(w, s);
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            MotorState back = Protocol.ReadMotor(ref r);
            Assert.That(Protocol.SameMotor(s, back), Is.True);
            Assert.That(back.Hand.Bag.N0, Is.EqualTo(3));
            Assert.That(back.Nade2.Kind, Is.EqualTo(ThrowKind.Launcher));
            Assert.That(back.Nade0.Normal, Is.EqualTo(new Vec3d(0, 0, -1)));

            var m = new CombatMsg { Tick = 5, Events = new List<CombatEvent>
            {
                new CombatEvent { Type = CombatEventType.Grenade, Attacker = 3, Source = DamageSource.C4, Nade = GrenadeEventType.Stuck, NadeSeq = 7, Point = new Vec3d(1, 2, 3) },
                new CombatEvent { Type = CombatEventType.Grenade, Attacker = 3, Source = DamageSource.Launcher, Nade = GrenadeEventType.Fizzled, NadeSeq = 8, Point = new Vec3d(1, 2, 3) },
            } };
            w = new ByteWriter(128);
            CombatProtocol.Write(w, m);
            r = new ByteReader(w.ToArray(), 0, w.Length);
            var into = new List<CombatEvent>();
            CombatProtocol.Read(ref r, into);
            Assert.That(into[0].Source, Is.EqualTo(DamageSource.C4));
            Assert.That(into[0].Nade, Is.EqualTo(GrenadeEventType.Stuck));
            Assert.That(into[1].Source, Is.EqualTo(DamageSource.Launcher));
            Assert.That(into[1].Nade, Is.EqualTo(GrenadeEventType.Fizzled));
        }

        // ================================================================ Networked

        private static SculptNetHarness WallArena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }, wrap: g =>
        {
            var w = new BrushWorld();
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 8, 3);
            w.Tiles.Changes.Clear();
            return new CompositeTerrain(g, w);
        });

        [Test]
        public void Networked_C4Breach_PredictedExactly_BreaksReinforced_ShieldsTheDefender_HurtsTheCareless()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(9, 0, 7), team: 1, up: SculptNetHarness.Lag(50), down: SculptNetHarness.Lag(50));
            var behind = h.AddClient(new Vec3d(9.5, 0, 12.8), team: 2, seed: 2); // right behind the wall
            var enemy = h.AddClient(new Vec3d(11, 0, 10.8), team: 2, seed: 4); // careless, a couple of steps from it
            var watcher = h.AddClient(new Vec3d(20, 0, 4), team: 2, seed: 3);
            h.RunSeconds(0.5);
            a.ServerSide.State.Hand.Bag.S4 = Item.C4;
            a.ServerSide.State.Hand.Bag.N4 = 1;
            TileWalls walls = h.Server.Walls;
            for (int k = 0; k < walls.Count; k++) walls.Set(k, TileState.Reinforced, h.Server.Walls.Config.ReinforcedHealth, TileCause.Reinforce);
            h.RunSeconds(0.5); // the client takes the new bag and tiles

            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 5) i.Hand.WeaponKey = 5;
                bool fire = t == start + 5 + Mc.Hand.C4DrawTicks + 2;
                i.Move.FireHeld = i.Move.FirePressed = i.Move.WantsToShoot = fire;
                i.Move.AimPressed = t == start + 90;
                return i;
            };
            int before = a.Client.Corrections;
            h.RunSeconds(2.5);
            Assert.That(a.Client.Corrections, Is.EqualTo(before), "throw, stick and blast are predicted exactly");
            int broken = 0;
            for (int k = 0; k < walls.Count; k++) if (walls[k].State == TileState.Broken) broken++;
            Assert.That(broken, Is.GreaterThanOrEqualTo(4), "a breach even through reinforced tiles");
            int dmg = enemy.ServerSide.State.Damage;
            Assert.That(dmg, Is.InRange(1, 45), "hurt, never more than 45");
            Assert.That(enemy.ServerSide.State.Dead, Is.False);
            Assert.That(behind.ServerSide.State.Damage, Is.EqualTo(0), "the wall it breaks still shields whoever is behind it");
            Assert.That(a.ServerSide.State.Damage, Is.EqualTo(0));
            Assert.That(a.ServerSide.State.Hand.Bag[4], Is.EqualTo(Item.None), "detonator gone after the blast");
            h.RunSeconds(0.5);
            foreach (var peer in new[] { a, behind, enemy, watcher }) AssertSameTiles(h.Server.Walls, peer.Client.Walls);
        }

        [Test]
        public void Networked_LauncherRound_ObserverSeesTheKind()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(9, 0, 1), team: 1, up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            var watcher = h.AddClient(new Vec3d(20, 0, 4), team: 2, seed: 2);
            h.RunSeconds(0.5);
            a.ServerSide.State.Hand.Bag.S4 = Item.Launcher;
            a.ServerSide.State.Hand.Bag.N4 = 4;
            h.RunSeconds(0.5);
            var seen = new List<CombatEvent>();
            watcher.Script = (p, t) =>
            {
                foreach (var kv in p.Client.Combat) seen.Add(kv.Value);
                p.Client.Combat.Clear();
                return default;
            };
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 5) i.Hand.WeaponKey = 5;
                bool fire = t == start + 5 + Mc.Hand.LauncherDrawTicks + 2;
                i.Move.FireHeld = i.Move.FirePressed = i.Move.WantsToShoot = fire;
                return i;
            };
            int before = a.Client.Corrections;
            h.RunSeconds(2.0);
            Assert.That(a.Client.Corrections, Is.EqualTo(before));
            List<CombatEvent> nades = seen.FindAll(e => e.Type == CombatEventType.Grenade);
            Assert.That(nades.Count, Is.EqualTo(2));
            Assert.That(nades.TrueForAll(e => e.Source == DamageSource.Launcher), Is.True);
            Assert.That(nades[1].Nade, Is.EqualTo(GrenadeEventType.Exploded));
            Assert.That(a.ServerSide.State.Hand.Bag.N4, Is.EqualTo(3));
            h.RunSeconds(0.5);
            AssertSameTiles(h.Server.Walls, a.Client.Walls);
            AssertSameTiles(h.Server.Walls, watcher.Client.Walls);
        }

        /// <summary>Every client draws and collides with exactly the server's holes (state, health and every cell).</summary>
        private static void AssertSameTiles(TileWalls server, TileWalls client)
        {
            Assert.That(client.Count, Is.EqualTo(server.Count));
            int holed = 0;
            for (int k = 0; k < server.Count; k++)
            {
                Assert.That(client[k].State, Is.EqualTo(server[k].State), "tile " + k + " state");
                Assert.That(client[k].Health, Is.EqualTo(server[k].Health), "tile " + k + " health");
                Assert.That(client[k].Open, Is.EqualTo(server[k].Open), "tile " + k + " cells");
                if (server[k].State == TileState.Broken || server[k].Open != 0) holed++;
            }
            Assert.That(holed, Is.GreaterThan(0), "the blast made a hole to compare");
        }
    }
}
