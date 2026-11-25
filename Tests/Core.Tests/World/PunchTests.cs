using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>The punch (Ben 2026-11-21): its own bind, not an item; half an unreinforced wall tile (50) and 30 to
    /// players. Claude's defaults: 2 m reach, hit 0.1 s after the press, 0.45 s with the gun locked.</summary>
    public class PunchTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();
        private static PunchConfig Pc => Mc.Punch;

        /// <summary>Flat ground (top y 0) with an 8 x 3 tile wall facing south at z 12, x 6..18.</summary>
        private static CompositeTerrain Arena(out TileWalls walls)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 8, 3);
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

        private static List<PunchSwing> Run(ITerrain t, ref MotorState s, int ticks, Func<int, InputCmd> cmd, List<WeaponShot> shots = null)
        {
            var swings = new List<PunchSwing>();
            int start = s.Tick + 1;
            for (int k = 0; k < ticks; k++)
            {
                InputCmd c = InputCmd.Quantize(cmd(start + k));
                c.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, shots, null, swings);
            }
            return swings;
        }

        private static InputCmd Cmd(bool punch, bool fire = false) => new InputCmd
        {
            Move = new MoveInput { FireHeld = fire, FirePressed = fire, WantsToShoot = fire },
            Hand = new HandInput { Punch = punch },
        };

        [Test]
        public void Press_HitLandsAfterTheWindup_GunLockedForTheWholePunch()
        {
            CompositeTerrain t = Arena(out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            s = DrawnPump(t, s);
            int t0 = s.Tick + 1;
            var shots = new List<WeaponShot>();
            var swings = Run(t, ref s, Pc.TotalTicks + 30 * SimTime.Per60, k => Cmd(k == t0, k > t0 && k < t0 + Pc.TotalTicks + 10), shots);
            Assert.That(swings.Count, Is.EqualTo(1));
            Assert.That(swings[0].Tick, Is.EqualTo(t0 + Pc.HitTicks));
            Assert.That(shots.Count, Is.GreaterThan(0), "fire held through the punch shoots once it ends");
            Assert.That(shots[0].Tick, Is.EqualTo(t0 + Pc.TotalTicks), "not a tick earlier");
        }

        private static MotorState DrawnPump(ITerrain t, MotorState s)
        {
            Run(t, ref s, Mc.Hand.Draw(Item.Pump) + 2, k => Cmd(false));
            Assert.That(s.Hand.CanFire, Is.True);
            return s;
        }

        [Test]
        public void PressDuringAPunch_BufferedOnlyNearTheEnd()
        {
            CompositeTerrain t = Arena(out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            int t0 = s.Tick + 1;
            // Mid-punch press (far from the end) is dropped; a press within the buffer of the end fires on the first free tick.
            int late = t0 + Pc.TotalTicks - Pc.BufferTicks;
            var swings = Run(t, ref s, 2 * Pc.TotalTicks + 30 * SimTime.Per60, k => Cmd(k == t0 || k == t0 + 8 || k == late));
            Assert.That(swings.Count, Is.EqualTo(2));
            Assert.That(swings[1].Tick - swings[0].Tick, Is.EqualTo(Pc.TotalTicks), "second punch starts the tick the first ends");
        }

        [Test]
        public void NoPunch_InSculptModeOrWhileMantling_OrDead()
        {
            CompositeTerrain t = Arena(out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            s.Hand.Mode = HandMode.Sculpt;
            Assert.That(Run(t, ref s, 20, k => Cmd(true)).Count, Is.EqualTo(0));
            s = Settled(t, new Vec3d(20, 0, 4));
            s.Dead = true;
            s.RespawnTick = int.MaxValue;
            Assert.That(Run(t, ref s, 20, k => Cmd(true)).Count, Is.EqualTo(0));
        }

        [Test]
        public void NoGrenadeThrowDuringAPunch()
        {
            CompositeTerrain t = Arena(out _);
            MotorState s = Settled(t, new Vec3d(20, 0, 4));
            int t0 = s.Tick + 1;
            Run(t, ref s, Mc.Hand.GrenadeDrawTicks + 4, k => new InputCmd { Hand = new HandInput { WeaponKey = (byte)(k == t0 ? 5 : 0) } });
            Assert.That(s.Hand.ItemReady, Is.True);
            int t1 = s.Tick + 1;
            var nades = new List<GrenadeEvent>();
            for (int k = 0; k < 10; k++)
            {
                var c = InputCmd.Quantize(new InputCmd { Tick = t1 + k, Hand = new HandInput { Punch = k == 0 }, Move = new MoveInput { FirePressed = k == 2, FireHeld = k == 2 } });
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, null, nades, null);
            }
            Assert.That(nades.Count, Is.EqualTo(0), "the press 2 ticks into the punch waits (it is past the buffer once the punch ends)");
        }

        // ---------------------------------------------------------------- What it hits

        private static PunchSwing Swing(Vec3d eye, Vec3d aim) => new PunchSwing { PlayerId = 1, Origin = eye, Aim = aim.Normalized };

        private static List<HitTarget> Enemy(Vec3d feet, int team = 2) =>
            new List<HitTarget> { HitTarget.Of(2, team, feet, Mc.HalfWidth, Mc.StandHeight, Mc.Weapons) };

        [Test]
        public void HitsAPlayerInReach_NotPastIt()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            var eye = new Vec3d(20, 1.62, 4);
            BulletHit h = PunchRules.Resolve(t, w, Swing(eye, new Vec3d(0, 0, 1)), Pc, Enemy(new Vec3d(20, 0, 5.5)), 1, false, out int tile);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player), "box face at 1.15 m");
            Assert.That(tile, Is.EqualTo(-1));
            h = PunchRules.Resolve(t, w, Swing(eye, new Vec3d(0, 0, 1)), Pc, Enemy(new Vec3d(20, 0, 6.5)), 1, false, out _);
            Assert.That(h.Kind, Is.Not.EqualTo(HitKind.Player), "box face at 2.15 m is past the 2 m reach");
        }

        [Test]
        public void Timings_ArePickaxeLike_InSeconds()
        {
            // Ben 2026-11-25: "too spammable", a slightly longer time to land and a longer recovery.
            var pc = new PunchConfig();
            Assert.That(pc.HitTicks, Is.EqualTo(SimTime.SecondsToTicks(0.2)), "lands 0.2 s after the press (was 0.1)");
            Assert.That(pc.TotalTicks, Is.EqualTo(SimTime.SecondsToTicks(0.8)), "one swing every 0.8 s (was 0.45)");
            Assert.That(pc.BufferTicks, Is.EqualTo(SimTime.SecondsToTicks(0.1)));
        }

        [Test]
        public void SlightlyOffAim_StillHitsThePlayer_TeammateNever()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            var eye = new Vec3d(20, 1.62, 4);
            // Aim 0.45 m to the side of the enemy's centre at 1.5 m: the centre ray misses the 0.35 m half-width box.
            Vec3d aim = new Vec3d(-0.45, 0, 1.5);
            BulletHit h = PunchRules.Resolve(t, w, Swing(eye, aim), Pc, Enemy(new Vec3d(20, 0, 5.5)), 1, false, out _);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player), "the forgiving ring finds them");
            h = PunchRules.Resolve(t, w, Swing(eye, aim), Pc, Enemy(new Vec3d(20, 0, 5.5), team: 1), 1, false, out _);
            Assert.That(h.Kind, Is.Not.EqualTo(HitKind.Player), "friendly fire off");
        }

        [Test]
        public void TileUnderTheCrosshair_TakesHalf()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            var eye = new Vec3d(9, 1.62, 10.5);
            PunchRules.Resolve(t, w, Swing(eye, new Vec3d(0, 0, 1)), Pc, null, 1, false, out int tile);
            Assert.That(tile, Is.GreaterThanOrEqualTo(0), "1.5 m from the wall face");
            Assert.That(w.BoundsOf(tile).Min.Y, Is.EqualTo(1.5).Within(1e-9), "the row at eye height");
            Assert.That(PunchRules.TileDamage(TileState.Intact, w.Config), Is.EqualTo(50));
            Assert.That(PunchRules.TileDamage(TileState.Patched, w.Config), Is.EqualTo(50));
            Assert.That(PunchRules.TileDamage(TileState.Reinforced, w.Config), Is.EqualTo(50), "reinforced wall just has more health (four punches)");
            PunchRules.Resolve(t, w, Swing(new Vec3d(9, 1.62, 9.5), new Vec3d(0, 0, 1)), Pc, null, 1, false, out int far);
            Assert.That(far, Is.EqualTo(-1), "2.5 m away is out of reach");
        }

        [Test]
        public void PlayerInFront_OfTheWall_IsHitInstead()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            var eye = new Vec3d(9, 1.62, 10.2);
            BulletHit h = PunchRules.Resolve(t, w, Swing(eye, new Vec3d(0, 0, 1)), Pc, Enemy(new Vec3d(9, 0, 11.4)), 1, false, out int tile);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player));
            Assert.That(tile, Is.EqualTo(-1), "one punch hits one thing");
        }

        // ---------------------------------------------------------------- Wire

        [Test]
        public void InputCmd_PunchRoundTrips_AndMotorStateCarriesThePunch()
        {
            var c = InputCmd.Quantize(new InputCmd { Tick = 50, Hand = new HandInput { Punch = true } });
            var d = InputCmd.Quantize(new InputCmd { Tick = 51 });
            var w = new ByteWriter(64);
            Protocol.Write(w, new InputsMsg { Cmds = new List<InputCmd> { c, d } });
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<InputCmd>();
            Protocol.ReadInputs(ref r, into);
            Assert.That(into[0].Hand.Punch, Is.True);
            Assert.That(into[1].Hand.Punch, Is.False);
            Assert.That(c.AsRepeat(52).Hand.Punch, Is.False, "a repeated command never punches again");

            var s = MotorState.At(new Vec3d(1, 2, 3));
            s.PunchTick = 1234;
            s.PunchBufferTick = 1230;
            var mw = new ByteWriter(256);
            Protocol.WriteMotor(mw, s);
            byte[] md = mw.ToArray();
            var mr = new ByteReader(md, 0, md.Length);
            MotorState back = Protocol.ReadMotor(ref mr);
            Assert.That(back.PunchTick, Is.EqualTo(1234));
            Assert.That(back.PunchBufferTick, Is.EqualTo(1230));
            Assert.That(Protocol.SameMotor(s, back), Is.True);
            back.PunchTick++;
            Assert.That(Protocol.SameMotor(s, back), Is.False, "a punch difference is a misprediction");
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
        public void Networked_TwoPunchesBreakATile_NoCorrections()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(9, 0, 10.5), up: SculptNetHarness.Lag(50), down: SculptNetHarness.Lag(50));
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                i.Hand.Punch = t == start + 30 || t == start + 30 + Pc.TotalTicks + 3;
                return i;
            };
            h.RunSeconds(0.4);
            int before = a.Client.Corrections;
            h.RunSeconds(1.6);
            TileWalls walls = h.Server.Walls;
            int broken = 0, touched = 0;
            for (int k = 0; k < walls.Count; k++)
            {
                if (walls[k].State == TileState.Broken || walls[k].Holed) broken++; // blast holes: the break may cut into neighbours
                else if (walls[k].Health < walls.Config.IntactHealth) touched++;
            }
            Assert.That(broken, Is.GreaterThanOrEqualTo(1), "50 + 50 breaks an intact tile");
            Assert.That(touched, Is.EqualTo(0), "only the tile under the crosshair");
            Assert.That(a.Client.Corrections, Is.EqualTo(before), "the punch is predicted exactly");
            int clientBroken = 0;
            for (int k = 0; k < a.Client.Walls.Count; k++) if (a.Client.Walls[k].State == TileState.Broken || a.Client.Walls[k].Holed) clientBroken++;
            Assert.That(clientBroken, Is.EqualTo(broken), "the client copies the break");
        }

        [Test]
        public void Networked_PunchHurtsAnEnemy30_NotATeammate()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(20, 0, 4), team: 1, up: SculptNetHarness.Lag(30), down: SculptNetHarness.Lag(30));
            var enemy = h.AddClient(new Vec3d(20, 0, 5.3), team: 2, seed: 2);
            var mate = h.AddClient(new Vec3d(16, 0, 5.3), team: 1, seed: 3);
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                i.Hand.Punch = t == start + 40;
                return i;
            };
            h.RunSeconds(1.5);
            Assert.That(enemy.ServerSide.State.Damage, Is.EqualTo(Pc.PlayerDamage));
            Assert.That(mate.ServerSide.State.Damage, Is.EqualTo(0));
            Assert.That(a.ServerSide.State.Damage, Is.EqualTo(0));
        }
    }
}
