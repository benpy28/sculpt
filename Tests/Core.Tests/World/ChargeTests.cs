using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Charge breach (Ben 2026-11-21): running into a Damaged or Patched tile at 80%+ of top sprint breaks it and
    /// keeps most of your speed; Intact and Reinforced tiles stop you.</summary>
    public class ChargeTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();
        private const double LaneX = 9.75; // centre of tile column 2 (x 9..10.5)

        /// <summary>Flat ground (top y 0) with an 8 x 3 tile wall facing south at z 12, x 6..18. ids[col + row * 8]? see Tile().</summary>
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

        /// <summary>The tile whose box holds this point.</summary>
        private static int Tile(TileWalls w, double x, double y)
        {
            for (int t = 0; t < w.Count; t++)
            {
                Aabb b = w.BoundsOf(t);
                if (x > b.Min.X && x < b.Max.X && y > b.Min.Y && y < b.Max.Y) return t;
            }
            throw new InvalidOperationException("no tile there");
        }

        private static void Crack(TileWalls w, int tile, TileState state = TileState.Damaged) =>
            w.Set(tile, state, state == TileState.Patched ? w.Config.PatchedHealth : 40, TileCause.Bullet);

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

        private static InputCmd Run(double yaw, bool sprint = true, bool crouchPress = false, bool crouchHeld = false) => new InputCmd
        {
            Move = new MoveInput { MoveZ = 1, Yaw = yaw, SprintHeld = sprint, CrouchPressed = crouchPress, CrouchHeld = crouchHeld },
        };

        /// <summary>Runs ticks with the server breaker; returns the peak speed just before reaching the wall and the
        /// speed right after the charge tick (if any).</summary>
        private static void Go(ITerrain t, ref MotorState s, int ticks, Func<int, InputCmd> cmd, IChargeBreaker breaker,
            out double before, out double after)
        {
            before = after = -1;
            int start = s.Tick + 1;
            for (int k = 0; k < ticks; k++)
            {
                InputCmd c = InputCmd.Quantize(cmd(k));
                c.Tick = start + k;
                double sp = s.HorizontalSpeed;
                int ct = s.ChargeTick;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, null, null, null, breaker);
                if (s.ChargeTick != ct && after < 0) { before = sp; after = s.HorizontalSpeed; }
            }
        }

        [Test]
        public void SprintIntoTwoDamagedTiles_BreaksThem_KeepsMostSpeed()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            int lo = Tile(w, LaneX, 0.75), hi = Tile(w, LaneX, 2.25);
            Crack(w, lo);
            Crack(w, hi, TileState.Patched);
            MotorState s = Settled(t, new Vec3d(LaneX, 0, 2));
            Go(t, ref s, 150, k => Run(0), new ServerChargeBreaker { By = 1 }, out double before, out double after);
            Assert.That(w[lo].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[hi].State, Is.EqualTo(TileState.Broken));
            Assert.That(s.Position.Z, Is.GreaterThan(13), "ran on through");
            Assert.That(before, Is.GreaterThanOrEqualTo(0.8 * Mc.SprintSpeed));
            Assert.That(after, Is.GreaterThan(before * 0.8), "most of the speed kept");
            Assert.That(after, Is.LessThan(before), "a charge costs some speed");
            Assert.That(w.ChargedAt(lo), Is.EqualTo(s.ChargeTick));
            Assert.That(w[Tile(w, LaneX, 3.75)].State, Is.EqualTo(TileState.Intact), "nothing above head height breaks");
            Assert.That(w[Tile(w, LaneX + 1.5, 0.75)].State, Is.EqualTo(TileState.Intact), "nor beside");
        }

        [Test]
        public void IntactOrReinforced_StopsYou()
        {
            foreach (TileState st in new[] { TileState.Intact, TileState.Reinforced })
            {
                CompositeTerrain t = Arena(out TileWalls w);
                int lo = Tile(w, LaneX, 0.75), hi = Tile(w, LaneX, 2.25);
                w.Set(lo, st, w.Config.MaxHealthOf(st), TileCause.Server);
                w.Set(hi, st, w.Config.MaxHealthOf(st), TileCause.Server);
                MotorState s = Settled(t, new Vec3d(LaneX, 0, 2));
                Go(t, ref s, 150, k => Run(0), new ServerChargeBreaker(), out _, out double after);
                Assert.That(after, Is.EqualTo(-1), st.ToString());
                Assert.That(s.Position.Z, Is.LessThan(12), st + " stops you");
                Assert.That(w[lo].State, Is.EqualTo(st));
            }
        }

        [Test]
        public void StandingNeedsBothTilesCracked_SlidingFitsThroughOne()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            int lo = Tile(w, LaneX, 0.75);
            Crack(w, lo);
            MotorState s = Settled(t, new Vec3d(LaneX, 0, 2));
            Go(t, ref s, 150, k => Run(0), new ServerChargeBreaker(), out _, out _);
            Assert.That(w[lo].State, Is.EqualTo(TileState.Damaged), "the intact tile above stops a standing charge, so nothing breaks");
            Assert.That(s.Position.Z, Is.LessThan(12));

            t = Arena(out w);
            lo = Tile(w, LaneX, 0.75);
            Crack(w, lo);
            s = Settled(t, new Vec3d(LaneX, 0, 4));
            // Sprint up, then slide the last few metres into the wall.
            Go(t, ref s, 150, k => k < 40 ? Run(0) : Run(0, true, k == 40, true), new ServerChargeBreaker(), out double before, out _);
            Assert.That(w[lo].State, Is.EqualTo(TileState.Broken), "a sliding player fits through one tile");
            Assert.That(s.Position.Z, Is.GreaterThan(12.5));
            Assert.That(before, Is.GreaterThanOrEqualTo(0.8 * Mc.SprintSpeed), "above the charge threshold");
            Assert.That(s.Crouched, Is.True, "went through at slide height");
        }

        [Test]
        public void TooSlow_ItIsJustAWall()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            int lo = Tile(w, LaneX, 0.75), hi = Tile(w, LaneX, 2.25);
            Crack(w, lo);
            Crack(w, hi);
            MotorState s = Settled(t, new Vec3d(LaneX, 0, 2));
            Go(t, ref s, 150, k => Run(0, sprint: false), new ServerChargeBreaker(), out _, out _);
            Assert.That(Mc.RunSpeed, Is.LessThan(0.8 * Mc.SprintSpeed), "run speed is below the charge threshold");
            Assert.That(w[lo].State, Is.EqualTo(TileState.Damaged));
            Assert.That(s.Position.Z, Is.LessThan(12));
        }

        [Test]
        public void GlancingRun_NeverBreaksTheWall()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            for (int k = 0; k < w.Count; k++) if (w.BoundsOf(k).Min.Y < 2.9) Crack(w, k);
            MotorState s = Settled(t, new Vec3d(7, 0, 9));
            // 70 degrees off head-on (yaw from north toward east).
            Go(t, ref s, 120, k => Run(70 * Math.PI / 180), new ServerChargeBreaker(), out _, out double after);
            Assert.That(after, Is.EqualTo(-1));
            for (int k = 0; k < w.Count; k++) Assert.That(w[k].State, Is.Not.EqualTo(TileState.Broken));
        }

        [Test]
        public void ReplayOfTheChargeTick_MatchesExactly()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            Crack(w, Tile(w, LaneX, 0.75));
            Crack(w, Tile(w, LaneX, 2.25));
            MotorState s = Settled(t, new Vec3d(LaneX, 0, 2));
            var breaker = new PredictedChargeBreaker();
            int start = s.Tick + 1;
            for (int k = 0; k < 150; k++)
            {
                InputCmd c = InputCmd.Quantize(Run(0));
                c.Tick = start + k;
                MotorState before = s;
                int ct = s.ChargeTick;
                NetSim.Simulate(t, Mc, Nc, ref s, c, s.Position, null, null, null, null, breaker);
                if (s.ChargeTick == ct) continue;
                // Replay the same tick from the same start (as reconciliation does): the tile is already broken locally.
                MotorState again = before;
                NetSim.Simulate(t, Mc, Nc, ref again, c, again.Position, null, null, null, null, breaker);
                Assert.That(Protocol.SameMotor(again, s), Is.True, "bit-identical");
                Assert.That(breaker.PendingCount, Is.EqualTo(2), "no double record");
                return;
            }
            Assert.Fail("never charged");
        }

        [Test]
        public void PredictedBreak_RefusedByTheServer_IsPutBack_ConfirmedStays()
        {
            CompositeTerrain t = Arena(out TileWalls w);
            int a = Tile(w, LaneX, 0.75), b = Tile(w, LaneX + 3, 0.75);
            Crack(w, a);
            Crack(w, b, TileState.Patched);
            var p = new PredictedChargeBreaker();
            p.Break(w, a, 100);
            p.Break(w, b, 140);
            Assert.That(w[a].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[b].State, Is.EqualTo(TileState.Broken));
            p.OnServerState(w, 120, 100);   // server charged on 100: a confirmed; b not yet known
            Assert.That(w[a].State, Is.EqualTo(TileState.Broken));
            Assert.That(p.PendingCount, Is.EqualTo(1));
            p.OnServerState(w, 141, 100);   // server state past 140 without a charge there: refused
            Assert.That(w[b].State, Is.EqualTo(TileState.Patched));
            Assert.That(w[b].Health, Is.EqualTo(w.Config.PatchedHealth));
            Assert.That(p.PendingCount, Is.EqualTo(0));
            Assert.That(w[a].State, Is.EqualTo(TileState.Broken));
        }

        [Test]
        public void MotorStateCarriesTheChargeTick()
        {
            var s = MotorState.At(new Vec3d(1, 2, 3));
            s.ChargeTick = 777;
            var mw = new ByteWriter(256);
            Protocol.WriteMotor(mw, s);
            byte[] md = mw.ToArray();
            var mr = new ByteReader(md, 0, md.Length);
            MotorState back = Protocol.ReadMotor(ref mr);
            Assert.That(back.ChargeTick, Is.EqualTo(777));
            back.ChargeTick++;
            Assert.That(Protocol.SameMotor(s, back), Is.False);
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
        public void Networked_ChargeThrough_PredictedWithoutCorrections()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(LaneX, 0, 2), up: SculptNetHarness.Lag(60), down: SculptNetHarness.Lag(60));
            h.RunSeconds(0.5);
            // Crack the lane on the server; clients copy it.
            TileWalls sw = h.Server.Walls;
            int lo = Tile(sw, LaneX, 0.75), hi = Tile(sw, LaneX, 2.25);
            Crack(sw, lo);
            Crack(sw, hi);
            h.RunSeconds(0.5);
            Assert.That(a.Client.Walls[lo].State, Is.EqualTo(TileState.Damaged), "client has the cracks");
            int before = a.Client.Corrections;
            a.Script = (p, t) => new SculptTickInput { Move = new MoveInput { MoveZ = 1, SprintHeld = true } };
            h.RunSeconds(2.5);
            Assert.That(sw[lo].State, Is.EqualTo(TileState.Broken));
            Assert.That(sw[hi].State, Is.EqualTo(TileState.Broken));
            Assert.That(a.ServerSide.State.Position.Z, Is.GreaterThan(13));
            Assert.That(a.Client.Corrections, Is.EqualTo(before), "the charge is predicted exactly");
            Assert.That(a.Client.Walls[lo].State, Is.EqualTo(TileState.Broken));
            Assert.That(a.Client.Charges.PendingCount, Is.EqualTo(0), "the server confirmed it");
        }

        [Test]
        public void Networked_ServerSaysNo_TileComesBack_AndYouAreStopped()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(LaneX, 0, 2), up: SculptNetHarness.Lag(60), down: SculptNetHarness.Lag(60));
            h.RunSeconds(0.5);
            // The client wrongly believes the lane is cracked (the server's tiles are intact).
            TileWalls cw = a.Client.Walls;
            int lo = Tile(cw, LaneX, 0.75), hi = Tile(cw, LaneX, 2.25);
            Crack(cw, lo);
            Crack(cw, hi);
            a.Script = (p, t) => new SculptTickInput { Move = new MoveInput { MoveZ = 1, SprintHeld = true } };
            h.RunSeconds(2.5);
            Assert.That(h.Server.Walls[lo].State, Is.EqualTo(TileState.Intact));
            Assert.That(a.ServerSide.State.Position.Z, Is.LessThan(12));
            Assert.That(a.Client.Predicted.Position.Z, Is.LessThan(12), "corrected back behind the wall");
            Assert.That(cw[lo].State, Is.EqualTo(TileState.Damaged), "predicted break put back");
            Assert.That(a.Client.Charges.PendingCount, Is.EqualTo(0));
        }
    }
}
