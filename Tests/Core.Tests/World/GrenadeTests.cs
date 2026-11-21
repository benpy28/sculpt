using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Grenades (Ben 2026-11-21): slot 5, carry 2; own blasts push you hard and never hurt you; enemy blasts hurt
    /// and push less; a blast breaks a large, square-ish hole of tiles.</summary>
    public class GrenadeTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();
        private static GrenadeConfig Gc => Mc.Items.Grenade;

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

        private static MotorState Settled(ITerrain t, Vec3d feet)
        {
            var s = MotorState.At(feet);
            for (int k = 1; k <= 30; k++)
            {
                s.Tick = k - 1;
                PlayerMovement.Step(t, Mc, ref s, default, null);
            }
            return s;
        }

        /// <summary>Runs NetSim for a few ticks with one input function; collects grenade events.</summary>
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

        private static InputCmd Throw(int tick, bool fire, double yaw, float pitch, byte key = 0) => new InputCmd
        {
            Tick = tick, Pitch = pitch,
            Move = new MoveInput { Yaw = yaw, FireHeld = fire, FirePressed = fire, WantsToShoot = fire },
            Hand = new HandInput { WeaponKey = key },
        };

        [Test]
        public void ThrowUsesAGrenade_RecoveryThenSecond_LastOneEmptiesTheSlot()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            int t0 = s.Tick + 1;
            int ready = t0 + Mc.Hand.GrenadeDrawTicks;
            var ev = Run(t, ref s, 120, k =>
                Throw(k, k == ready || k == ready + 2 || k == ready + Gc.ThrowRecoveryTicks + 1, 0, -20, (byte)(k == t0 ? 5 : 0)));
            var thrown = ev.FindAll(e => e.Type == GrenadeEventType.Thrown);
            Assert.That(thrown.Count, Is.EqualTo(2), "the press 2 ticks after the first is inside the recovery and is dropped");
            Assert.That(thrown[1].Tick - thrown[0].Tick, Is.EqualTo(Gc.ThrowRecoveryTicks + 1));
            Assert.That(s.Hand.Bag[4], Is.EqualTo(Item.None), "both grenades gone");
            Assert.That(s.Hand.HeldItem, Is.EqualTo(Item.Pump), "previous slot drawn");
            Assert.That(ev.FindAll(e => e.Type == GrenadeEventType.Exploded).Count, Is.EqualTo(2));
        }

        [Test]
        public void PressBufferedDuringTheDraw_ThrowsWhenReady()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            int t0 = s.Tick + 1;
            int ready = t0 + Mc.Hand.GrenadeDrawTicks;
            var ev = Run(t, ref s, 40, k => Throw(k, k == ready - 3, 0, 0, (byte)(k == t0 ? 5 : 0)));
            var thrown = ev.FindAll(e => e.Type == GrenadeEventType.Thrown);
            Assert.That(thrown.Count, Is.EqualTo(1));
            Assert.That(thrown[0].Tick, Is.EqualTo(ready));
        }

        [Test]
        public void GunsDoNotThrow_GrenadesDoNotShoot()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            var ev = Run(t, ref s, 60, k => Throw(k, k % 10 == 0, 0, 0));
            Assert.That(ev, Is.Empty, "the pump is out");
            Assert.That(s.Hand.Bag.Grenades, Is.EqualTo(2));
        }

        [Test]
        public void ThrownAtAWall_GoesOffOnItsFace()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(9, 0, 7));
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.GrenadeDrawTicks;
            var ev = Run(t, ref s, 80, k => Throw(k, k == ready, 0, 0, (byte)(k == t0 ? 5 : 0)));
            GrenadeEvent boom = ev.Find(e => e.Type == GrenadeEventType.Exploded);
            Assert.That(boom.Tick, Is.GreaterThan(0));
            Assert.That(boom.Position.Z, Is.EqualTo(12 - 0.05).Within(1e-6), "just in front of the wall face");
            Assert.That(boom.Position.Y, Is.InRange(1.0, 2.5));
        }

        [Test]
        public void FuseRunsOut_InTheAir()
        {
            var s = MotorState.At(new Vec3d(0, 100, 0));
            s.Nade0 = new GrenadeFlight { Active = true, Position = new Vec3d(0, 200, 0), Velocity = new Vec3d(0, 40, 0), ThrowTick = 0, Seq = 1 };
            var ev = new List<GrenadeEvent>();
            for (int k = 1; k <= Gc.MaxFuseTicks; k++) GrenadeRules.Step(null, Mc, ref s, k, default, 0, ev);
            Assert.That(ev.Count, Is.EqualTo(1));
            Assert.That(ev[0].Tick, Is.EqualTo(Gc.MaxFuseTicks));
            Assert.That(s.Nade0.Active, Is.False);
        }

        [Test]
        public void OwnGrenadeAtYourFeet_LaunchesYou_NoDamage()
        {
            CompositeTerrain t = Arena(out _, out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            int t0 = s.Tick + 1, ready = t0 + Mc.Hand.GrenadeDrawTicks;
            double peak = 0, topVy = 0;
            var ev = new List<GrenadeEvent>();
            for (int k = t0; k < t0 + 120; k++)
            {
                InputCmd c = InputCmd.Quantize(Throw(k, k == ready, 0, 89, (byte)(k == t0 ? 5 : 0)));
                c.Tick = k;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, null, ev);
                peak = Math.Max(peak, s.Position.Y);
                topVy = Math.Max(topVy, s.Velocity.Y);
            }
            Assert.That(ev.Exists(e => e.Type == GrenadeEventType.Exploded), Is.True);
            Assert.That(s.Damage, Is.EqualTo(0), "own grenades never hurt you");
            Assert.That(topVy, Is.GreaterThan(10), "a big push up");
            Assert.That(peak, Is.GreaterThan(3.0), "well above a normal 1.3 m jump");
        }

        [Test]
        public void OwnGrenadeBehindYou_PushesYouForward()
        {
            var s = MotorState.At(new Vec3d(0, 0, 0));
            s.Mode = MoveMode.Ground;
            s.Grounded = true;
            Vec3d blast = new Vec3d(0, 0.3, -1.0);
            Vec3d push = GrenadeRules.Push(null, blast, s.Position, 1.8, Gc, Gc.SelfBoost);
            Assert.That(push.Z, Is.GreaterThan(5));
            GrenadeRules.Knock(ref s, push);
            Assert.That(s.Mode, Is.EqualTo(MoveMode.Air), "the lift takes you off the ground");
        }

        [Test]
        public void Falloff_FullThenLinearToNothing()
        {
            Assert.That(GrenadeRules.Falloff(0, Gc), Is.EqualTo(1));
            Assert.That(GrenadeRules.Falloff(Gc.FullRadius, Gc), Is.EqualTo(1));
            Assert.That(GrenadeRules.Falloff((Gc.FullRadius + Gc.Radius) / 2, Gc), Is.EqualTo(0.5).Within(1e-9));
            Assert.That(GrenadeRules.Falloff(Gc.Radius, Gc), Is.EqualTo(0));
            Assert.That(GrenadeRules.Damage(null, new Vec3d(0, 0.9, 0), new Vec3d(0, 0, 0), 1.8, Gc), Is.EqualTo((int)Gc.EnemyDamage));
            Assert.That(GrenadeRules.Damage(null, new Vec3d(0, 0.9, 10), new Vec3d(0, 0, 0), 1.8, Gc), Is.EqualTo(0));
            Assert.That(Gc.EnemyDamage, Is.LessThan(new WeaponsConfig().MaxHealth), "no one-grenade kills from full health");
        }

        [Test]
        public void WallsShieldBlasts()
        {
            CompositeTerrain t = Arena(out _, out _);
            Vec3d blast = new Vec3d(9, 0.9, 11.9);           // in front of the wall
            Vec3d behind = new Vec3d(9, 0, 13.0);            // 1.1 m away, other side
            Assert.That(GrenadeRules.Damage(t, blast, behind, 1.8, Gc), Is.EqualTo(0));
            Assert.That(GrenadeRules.Push(t, blast, behind, 1.8, Gc, Gc.OtherKnock).LengthSquared, Is.EqualTo(0));
            Assert.That(GrenadeRules.Damage(t, blast, new Vec3d(9, 0, 10.8), 1.8, Gc), Is.GreaterThan(0), "same side");
        }

        // ---------------------------------------------------------------- Tile holes

        private static void Grid(TileWalls w, List<int> tiles, out int cols, out int rows)
        {
            var xs = new HashSet<int>();
            var ys = new HashSet<int>();
            foreach (int id in tiles)
            {
                Aabb b = w.BoundsOf(id);
                xs.Add((int)Math.Round(b.Min.X / 1.5));
                ys.Add((int)Math.Round(b.Min.Y / 1.5));
            }
            cols = xs.Count;
            rows = ys.Count;
        }

        [Test]
        public void BlastMidTile_ExactlyThreeByThree()
        {
            Arena(out TileWalls w, out _);
            var tiles = new List<int>();
            // Tile column 3 (x 10.5..12), row 1 (y 1.5..3): centre (11.25, 2.25), wall face z 12.
            GrenadeRules.TilesInBlast(w, new Vec3d(11.25, 2.25, 11.95), Gc, tiles);
            Grid(w, tiles, out int cols, out int rows);
            Assert.That(tiles.Count, Is.EqualTo(9));
            Assert.That(cols, Is.EqualTo(3));
            Assert.That(rows, Is.EqualTo(3));
        }

        [Test]
        public void AnyBlastOnTheWallFace_HoleIsAFullRectangleAtLeastTwoByTwo()
        {
            // Ben: holes always large and roughly square, never thin or odd gaps.
            Arena(out TileWalls w, out _);
            var tiles = new List<int>();
            var rng = new Random(5);
            for (int n = 0; n < 500; n++)
            {
                // Away from the wall's own edges (x 6..18, y 0..4.5) so the hole isn't clipped by the wall ending.
                double x = 8.0 + rng.NextDouble() * 8.0, y = 1.5 + rng.NextDouble() * 1.5;
                GrenadeRules.TilesInBlast(w, new Vec3d(x, y, 11.95), Gc, tiles);
                Grid(w, tiles, out int cols, out int rows);
                Assert.That(cols, Is.GreaterThanOrEqualTo(2), $"blast at {x:0.00}, {y:0.00}");
                Assert.That(rows, Is.GreaterThanOrEqualTo(2), $"blast at {x:0.00}, {y:0.00}");
                Assert.That(tiles.Count, Is.EqualTo(cols * rows), $"a full rectangle at {x:0.00}, {y:0.00}");
                Assert.That(cols, Is.LessThanOrEqualTo(3));
                Assert.That(rows, Is.LessThanOrEqualTo(3));
            }
        }

        [Test]
        public void BlastTileDamage_BreaksAllButReinforced()
        {
            Arena(out TileWalls w, out int[] ids);
            var bc = w.Config;
            w.Set(ids[8 + 3], TileState.Reinforced, bc.ReinforcedHealth, TileCause.Reinforce);
            w.Set(ids[8 + 2], TileState.Patched, bc.PatchedHealth, TileCause.Heal);
            var tiles = new List<int>();
            GrenadeRules.TilesInBlast(w, new Vec3d(11.25, 2.25, 11.95), Gc, tiles);
            foreach (int t in tiles) w.Damage(t, bc.GrenadeTileDamage, TileCause.Grenade, 1);
            Assert.That(w[ids[8 + 3]].State, Is.EqualTo(TileState.Reinforced), "reinforced survives one blast");
            Assert.That(w[ids[8 + 3]].Health, Is.EqualTo(bc.ReinforcedHealth - bc.GrenadeTileDamage));
            Assert.That(w[ids[8 + 2]].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[ids[8 + 4]].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[ids[0 + 3]].State, Is.EqualTo(TileState.Broken));
        }

        // ---------------------------------------------------------------- Wire

        [Test]
        public void MotorState_FlightsRoundTripExactly()
        {
            var s = MotorState.At(new Vec3d(1, 2, 3));
            s.Nade1 = new GrenadeFlight { Active = true, Position = new Vec3d(1.1, 2.2, 3.3), Velocity = new Vec3d(-4, 5.5, 0.25), ThrowTick = 77, Seq = 9 };
            s.NadeSeq = 9;
            var w = new ByteWriter(512);
            Protocol.WriteMotor(w, s);
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            Assert.That(Protocol.SameMotor(s, Protocol.ReadMotor(ref r)), Is.True);
        }

        [Test]
        public void CombatGrenadeEvents_RoundTrip()
        {
            var list = new List<CombatEvent>
            {
                new CombatEvent { Type = CombatEventType.Grenade, Attacker = 3, Source = DamageSource.Grenade, Nade = GrenadeEventType.Thrown, NadeSeq = 4, Origin = new Vec3d(1.0 / 3, 2, 3), Aim = new Vec3d(0.1, 9.75, -20) },
                new CombatEvent { Type = CombatEventType.Grenade, Attacker = 3, Source = DamageSource.Grenade, Nade = GrenadeEventType.Exploded, NadeSeq = 4, Point = new Vec3d(5, 0.05, 6) },
            };
            var w = new ByteWriter(128);
            CombatProtocol.Write(w, new CombatMsg { Tick = 10, Events = list });
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<CombatEvent>();
            CombatProtocol.Read(ref r, into);
            Assert.That(into[0].Nade, Is.EqualTo(GrenadeEventType.Thrown));
            Assert.That(into[0].Origin, Is.EqualTo(list[0].Origin), "full precision so clients fly it exactly");
            Assert.That(into[0].Aim, Is.EqualTo(list[0].Aim));
            Assert.That(into[1].Nade, Is.EqualTo(GrenadeEventType.Exploded));
            Assert.That(into[1].Point, Is.EqualTo(new Vec3d(5, 0.05f, 6)));
        }

        // ---------------------------------------------------------------- Networked

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
        public void Networked_GrenadeJump_PredictedWithoutCorrections()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(20, 0, 4), up: SculptNetHarness.Lag(50), down: SculptNetHarness.Lag(50));
            int start = -1;
            double peak = 0;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput { Pitch = 89 };
                if (t == start + 60) i.Hand.WeaponKey = 5;
                bool fire = t == start + 60 + Mc.Hand.GrenadeDrawTicks + 2;
                i.Move.FireHeld = i.Move.FirePressed = i.Move.WantsToShoot = fire;
                peak = Math.Max(peak, p.Client.Predicted.Position.Y);
                return i;
            };
            h.RunSeconds(1.0);
            int before = a.Client.Corrections;
            h.RunSeconds(2.5);
            Assert.That(peak, Is.GreaterThan(3.0), "launched");
            Assert.That(a.Client.Corrections, Is.EqualTo(before), "own grenade flight and push are predicted exactly");
            Assert.That(a.ServerSide.State.Damage, Is.EqualTo(0));
            Assert.That(a.ServerSide.State.Hand.Bag.Grenades, Is.EqualTo(1));
        }

        [Test]
        public void Networked_EnemyBlastHurtsAndBreaksTheWall_TeammateUnhurt()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(9, 0, 7), team: 1, up: SculptNetHarness.Lag(30), down: SculptNetHarness.Lag(30));
            var enemy = h.AddClient(new Vec3d(10.5, 0, 10.6), team: 2, seed: 2);
            var mate = h.AddClient(new Vec3d(12.5, 0, 10.6), team: 1, seed: 3);
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 30) i.Hand.WeaponKey = 5;
                bool fire = t == start + 30 + Mc.Hand.GrenadeDrawTicks + 2;
                i.Move.FireHeld = i.Move.FirePressed = i.Move.WantsToShoot = fire;
                return i;
            };
            h.RunSeconds(2.0);
            TileWalls walls = h.Server.Walls;
            int broken = 0;
            for (int k = 0; k < walls.Count; k++) if (walls[k].State == TileState.Broken) broken++;
            Assert.That(broken, Is.GreaterThanOrEqualTo(4), "a big hole");
            Assert.That(enemy.ServerSide.State.Damage, Is.GreaterThan(0), "enemy hurt");
            Assert.That(mate.ServerSide.State.Damage, Is.EqualTo(0), "no friendly fire");
            Assert.That(a.ServerSide.State.Damage, Is.EqualTo(0));
            int clientBroken = 0;
            for (int k = 0; k < a.Client.Walls.Count; k++) if (a.Client.Walls[k].State == TileState.Broken) clientBroken++;
            Assert.That(clientBroken, Is.EqualTo(broken), "clients copy the hole");
        }
    }
}
