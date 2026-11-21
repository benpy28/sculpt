using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>Healing and reinforcing tiles (step 5) and the new bar maths: refill and damage gain.</summary>
    public class HealTests
    {
        private static readonly BreachConfig C = new BreachConfig();

        /// <summary>A 4 x 2 wall running east from (0, 0, 10), thin in Z (10..10.3). Players stand south of it.</summary>
        private static TileHealing Setup(out BrushWorld w, out int[] t)
        {
            w = new BrushWorld();
            t = w.Tiles.AddWall(new Vec3d(0, 0, 10), Dir.East, 4, 2);
            w.Tiles.Changes.Clear();
            return new TileHealing(w.Tiles);
        }

        private static HealerStatus Healer(int id, Vec3d feet, bool sculpt = true, bool alive = true) => new HealerStatus
        {
            Id = id,
            Alive = alive,
            InSculptMode = sculpt,
            Eye = feet + new Vec3d(0, 1.6, 0),
            Box = PlayerMovement.Box(feet, 0.4, 1.8),
        };

        /// <summary>Feet 3 m south of the wall, in front of tile column <paramref name="col"/>.</summary>
        private static Vec3d Front(int col, double back = 3) => new Vec3d(col * 1.5 + 0.75, 0, 10 - back);

        private static SculptBar Bar(int value = SculptConfig.BarMax)
        {
            var b = new SculptBar();
            b.SetValue(value);
            return b;
        }

        private static void Run(TileHealing h, int from, int to, List<HealerStatus> who, SculptBar bar)
        {
            for (int k = from; k <= to; k++) h.Step(k, who, _ => bar);
        }

        // ------------------------------------------------------------------ the state rule

        [Test]
        public void StateCheck_WhatCanBeHealedAndReinforced()
        {
            WallTile T(TileState s, int hp) => new WallTile { State = s, Health = hp };
            Assert.That(TileHealing.StateCheck(T(TileState.Intact, 100), HealKind.Heal, C), Is.EqualTo(HealResult.NothingToHeal));
            Assert.That(TileHealing.StateCheck(T(TileState.Intact, 60), HealKind.Heal, C), Is.EqualTo(HealResult.NothingToHeal), "no cracks yet");
            Assert.That(TileHealing.StateCheck(T(TileState.Damaged, 40), HealKind.Heal, C), Is.EqualTo(HealResult.Ok));
            Assert.That(TileHealing.StateCheck(T(TileState.Broken, 0), HealKind.Heal, C), Is.EqualTo(HealResult.Ok));
            Assert.That(TileHealing.StateCheck(T(TileState.Patched, 50), HealKind.Heal, C), Is.EqualTo(HealResult.NothingToHeal));
            Assert.That(TileHealing.StateCheck(T(TileState.Patched, 20), HealKind.Heal, C), Is.EqualTo(HealResult.Ok), "a chipped patch can be re-patched");
            Assert.That(TileHealing.StateCheck(T(TileState.Reinforced, 30), HealKind.Heal, C), Is.EqualTo(HealResult.NothingToHeal), "a heal would weaken it");
            foreach (TileState s in new[] { TileState.Intact, TileState.Damaged, TileState.Broken, TileState.Patched })
                Assert.That(TileHealing.StateCheck(T(s, s == TileState.Broken ? 0 : 30), HealKind.Reinforce, C), Is.EqualTo(HealResult.Ok), s.ToString());
            Assert.That(TileHealing.StateCheck(T(TileState.Reinforced, 120), HealKind.Reinforce, C), Is.EqualTo(HealResult.Ok), "top up a chipped reinforcement");
            Assert.That(TileHealing.StateCheck(T(TileState.Reinforced, 200), HealKind.Reinforce, C), Is.EqualTo(HealResult.AlreadyReinforced));
        }

        [Test]
        public void StartingNumbers_MatchBensSpec()
        {
            Assert.That(C.HealTicks, Is.EqualTo(48), "0.8 s");
            Assert.That(C.HealCost, Is.EqualTo(1000), "10%");
            Assert.That(C.PatchedHealth, Is.EqualTo(50));
            Assert.That(C.ReinforceCost, Is.EqualTo(2500), "25%");
            Assert.That(C.ReinforcedHealth, Is.EqualTo(200));
            Assert.That(C.HealRange, Is.EqualTo(6.0));
            Assert.That(C.BarRefillPerSecond, Is.EqualTo(200), "2%/s");
            Assert.That(C.BarPer10Damage, Is.EqualTo(100), "1% per 10 damage");
        }

        // ------------------------------------------------------------------ heal

        [Test]
        public void Heal_BrokenTileStaysOpenUntilTheEnd_ThenPatched()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[1], TileState.Broken, 0, TileCause.Bullet);
            Aabb hole = w.Tiles.BoundsOf(t[1]);
            var me = Healer(1, Front(1));
            var who = new List<HealerStatus> { me };
            SculptBar bar = Bar();
            Assert.That(h.TryBegin(w, me, t[1], HealKind.Heal, bar, 100), Is.EqualTo(HealResult.Ok));
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax - 1000), "10% at the start");
            Assert.That(h.Events[0].Type, Is.EqualTo(HealEventType.Began));
            Assert.That(h.Events[0].EndTick, Is.EqualTo(148));
            Run(h, 101, 147, who, bar);
            Assert.That(w.Tiles[t[1]].State, Is.EqualTo(TileState.Broken), "the hole stays open during the heal");
            Assert.That(w.BoxOverlapsSolid(hole.Expand(-0.05)), Is.False, "and you can still go through it");
            h.Step(148, who, _ => bar);
            Assert.That(w.Tiles[t[1]].State, Is.EqualTo(TileState.Patched));
            Assert.That(w.Tiles[t[1]].Health, Is.EqualTo(50));
            Assert.That(w.BoxOverlapsSolid(hole.Expand(-0.05)), Is.True, "solid again");
            Assert.That(h.Count, Is.Zero);
            Assert.That(h.Events[h.Events.Count - 1].Type, Is.EqualTo(HealEventType.Completed));
            Assert.That(w.Tiles.Changes[w.Tiles.Changes.Count - 1].Cause, Is.EqualTo(TileCause.Heal), "through the one Set function");
        }

        [Test]
        public void Heal_DamagedTileComesBackPatched_EvenIfShotDuringTheHeal()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 70, TileCause.Bullet);
            var me = Healer(1, Front(0));
            SculptBar bar = Bar();
            h.TryBegin(w, me, t[0], HealKind.Heal, bar, 10);
            w.Tiles.Damage(t[0], 100, TileCause.Bullet); // shot out mid-heal
            Run(h, 11, 58, new List<HealerStatus> { me }, bar);
            Assert.That(w.Tiles[t[0]].State, Is.EqualTo(TileState.Patched));
            Assert.That(w.Tiles[t[0]].Health, Is.EqualTo(50));
        }

        [Test]
        public void Heal_PlayerInsideTheTileAtTheEnd_CancelsAndRefunds()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[2], TileState.Broken, 0, TileCause.Grenade);
            var me = Healer(1, Front(2));
            SculptBar bar = Bar();
            h.TryBegin(w, me, t[2], HealKind.Heal, bar, 0);
            Aabb tile = w.Tiles.BoundsOf(t[2]);
            var runner = Healer(2, new Vec3d((tile.Min.X + tile.Max.X) / 2, 0, tile.Min.Z - 0.2)); // half in the hole
            Run(h, 1, 48, new List<HealerStatus> { me, runner }, bar);
            Assert.That(w.Tiles[t[2]].State, Is.EqualTo(TileState.Broken), "never closes on a player");
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax), "refunded");
            HealEvent e = h.Events[h.Events.Count - 1];
            Assert.That(e.Type, Is.EqualTo(HealEventType.Cancelled));
            Assert.That(e.Result, Is.EqualTo(HealResult.PlayerInside));
        }

        [Test]
        public void Heal_RunnerWhoIsThroughBeforeTheEnd_DoesNotStopIt()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[2], TileState.Broken, 0, TileCause.Grenade);
            var me = Healer(1, Front(2));
            SculptBar bar = Bar();
            h.TryBegin(w, me, t[2], HealKind.Heal, bar, 0);
            Aabb tile = w.Tiles.BoundsOf(t[2]);
            var runner = Healer(2, new Vec3d((tile.Min.X + tile.Max.X) / 2, 0, tile.Min.Z - 0.2));
            var who = new List<HealerStatus> { me, runner };
            Run(h, 1, 30, who, bar);
            who[1] = Healer(2, new Vec3d((tile.Min.X + tile.Max.X) / 2, 0, tile.Max.Z + 0.45)); // through, just clear of the wall
            Run(h, 31, 48, who, bar);
            Assert.That(w.Tiles[t[2]].State, Is.EqualTo(TileState.Patched));
        }

        [Test]
        public void Heal_StandingFlushAgainstTheTile_IsNotInside()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[0], TileState.Broken, 0, TileCause.Grenade);
            Aabb tile = w.Tiles.BoundsOf(t[0]);
            var hugger = Healer(1, new Vec3d(0.75, 0, tile.Min.Z - 0.4)); // box face exactly on the wall face
            Assert.That(h.Occupied(t[0], new List<HealerStatus> { hugger }), Is.False);
        }

        [Test]
        public void Reinforce_CostsAQuarter_GivesFullReinforcedHealth_EvenFromBroken()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[3], TileState.Broken, 0, TileCause.Bullet);
            var me = Healer(1, Front(3));
            SculptBar bar = Bar();
            Assert.That(h.TryBegin(w, me, t[3], HealKind.Reinforce, bar, 0), Is.EqualTo(HealResult.Ok));
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax - 2500));
            Run(h, 1, C.ReinforceTicks, new List<HealerStatus> { me }, bar);
            Assert.That(w.Tiles[t[3]].State, Is.EqualTo(TileState.Reinforced));
            Assert.That(w.Tiles[t[3]].Health, Is.EqualTo(200));
            Assert.That(w.Tiles.Changes[w.Tiles.Changes.Count - 1].Cause, Is.EqualTo(TileCause.Reinforce));

            Assert.That(h.TryBegin(w, me, t[3], HealKind.Reinforce, bar, 100), Is.EqualTo(HealResult.AlreadyReinforced));
            Assert.That(h.TryBegin(w, me, t[0], HealKind.Reinforce, bar, 100), Is.EqualTo(HealResult.Ok), "an intact tile can be reinforced");
        }

        [Test]
        public void Refusals_AreFree()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 60, TileCause.Bullet);
            SculptBar bar = Bar();
            Assert.That(h.TryBegin(w, Healer(1, Front(1)), t[1], HealKind.Heal, bar, 0), Is.EqualTo(HealResult.NothingToHeal));
            Assert.That(h.TryBegin(w, Healer(1, Front(0), sculpt: false), t[0], HealKind.Heal, bar, 0), Is.EqualTo(HealResult.NotInSculptMode));
            Assert.That(h.TryBegin(w, Healer(1, Front(0, 7.5)), t[0], HealKind.Heal, bar, 0), Is.EqualTo(HealResult.OutOfRange));
            Assert.That(h.TryBegin(w, Healer(1, Front(0)), -1, HealKind.Heal, bar, 0), Is.EqualTo(HealResult.NoTile));
            Assert.That(h.TryBegin(w, Healer(1, Front(0)), 999, HealKind.Heal, bar, 0), Is.EqualTo(HealResult.NoTile));
            SculptBar poor = Bar(999);
            Assert.That(h.TryBegin(w, Healer(1, Front(0)), t[0], HealKind.Heal, poor, 0), Is.EqualTo(HealResult.NotEnoughBar));
            Assert.That(poor.Value, Is.EqualTo(999));
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(h.Count, Is.Zero);
            Assert.That(h.Events.TrueForAll(e => e.Type == HealEventType.Rejected), Is.True);
            Assert.That(h.TryBegin(w, Healer(1, Front(0)), t[0], HealKind.Heal, Bar(1000), 0), Is.EqualTo(HealResult.Ok), "exactly enough is enough");
        }

        [Test]
        public void Range_IsMeasuredToTheNearestPointOfTheTile()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 60, TileCause.Bullet);
            // Eye at the tile's height, straight out from its face: distance = how far back you stand.
            var near = new HealerStatus { Id = 1, Alive = true, InSculptMode = true, Eye = new Vec3d(0.75, 0.75, 10 - 5.99), Box = PlayerMovement.Box(new Vec3d(0.75, 0, 4), 0.4, 1.8) };
            var far = near;
            far.Eye = new Vec3d(0.75, 0.75, 10 - 6.01);
            Assert.That(h.Check(w, 1, near.Eye, true, t[0], HealKind.Heal, 10000), Is.EqualTo(HealResult.Ok));
            Assert.That(h.Check(w, 1, far.Eye, true, t[0], HealKind.Heal, 10000), Is.EqualTo(HealResult.OutOfRange));
            Assert.That(h.Check(w, 1, far.Eye, true, t[0], HealKind.Heal, 10000, reachSlack: 0.75), Is.EqualTo(HealResult.Ok), "server slack");
        }

        [Test]
        public void WalkingOutOfRange_CancelsAndRefunds_ButTheLeashAllowsALittle()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 60, TileCause.Bullet);
            SculptBar bar = Bar();
            var who = new List<HealerStatus> { Healer(1, Front(0, 3)) };
            h.TryBegin(w, who[0], t[0], HealKind.Heal, bar, 0);
            who[0] = Healer(1, Front(0, 7)); // 7 m back (eye about 7.1 m away): past the range but inside the leash
            Run(h, 1, 10, who, bar);
            Assert.That(h.Count, Is.EqualTo(1));
            who[0] = Healer(1, Front(0, 8));
            h.Step(11, who, _ => bar);
            Assert.That(h.Count, Is.Zero);
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(h.Events[h.Events.Count - 1].Result, Is.EqualTo(HealResult.LeftRange));
        }

        [Test]
        public void LeavingSculptMode_Dying_OrLeaving_CancelsAndRefunds()
        {
            foreach (int how in new[] { 0, 1, 2 })
            {
                TileHealing h = Setup(out BrushWorld w, out int[] t);
                w.Tiles.Damage(t[0], 60, TileCause.Bullet);
                SculptBar bar = Bar();
                var who = new List<HealerStatus> { Healer(1, Front(0)) };
                h.TryBegin(w, who[0], t[0], HealKind.Heal, bar, 0);
                if (how == 0) who[0] = Healer(1, Front(0), sculpt: false);
                else if (how == 1) who[0] = Healer(1, Front(0), alive: false);
                else who.Clear();
                h.Step(5, who, _ => bar);
                Assert.That(h.Count, Is.Zero, how.ToString());
                Assert.That(h.Events[h.Events.Count - 1].Result, Is.EqualTo(HealResult.Stopped));
                if (how < 2) Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax), "refunded");
                Assert.That(w.Tiles[t[0]].State, Is.EqualTo(TileState.Damaged));
            }
        }

        [Test]
        public void OneTileAtATime_OneHealerPerTile_AndANewPressReplacesYourOld()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 60, TileCause.Bullet);
            w.Tiles.Damage(t[1], 60, TileCause.Bullet);
            SculptBar a = Bar(), b = Bar();
            var ha = Healer(1, Front(0));
            var hb = Healer(2, Front(1));
            Assert.That(h.TryBegin(w, ha, t[0], HealKind.Heal, a, 0), Is.EqualTo(HealResult.Ok));
            Assert.That(h.TryBegin(w, hb, t[0], HealKind.Heal, b, 0), Is.EqualTo(HealResult.TileBusy));
            Assert.That(b.Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(h.HealerOf(t[0]), Is.EqualTo(1));

            int n = h.Events.Count;
            Assert.That(h.TryBegin(w, ha, t[0], HealKind.Heal, a, 3), Is.EqualTo(HealResult.Ok), "pressing it again does nothing");
            Assert.That(h.Events.Count, Is.EqualTo(n));
            Assert.That(a.Value, Is.EqualTo(SculptConfig.BarMax - 1000));

            Assert.That(h.TryBegin(w, ha, t[1], HealKind.Heal, a, 5), Is.EqualTo(HealResult.Ok));
            Assert.That(a.Value, Is.EqualTo(SculptConfig.BarMax - 1000), "old one refunded, new one paid");
            Assert.That(h.HealerOf(t[0]), Is.EqualTo(0));
            Assert.That(h.HealerOf(t[1]), Is.EqualTo(1));
            Assert.That(h.Events[h.Events.Count - 2].Result, Is.EqualTo(HealResult.Replaced));
            Assert.That(h.TryBegin(w, hb, t[0], HealKind.Heal, b, 6), Is.EqualTo(HealResult.Ok), "the first tile is free again");
        }

        [Test]
        public void ReplacingYourHeal_CountsTheRefundWhenCheckingTheBar()
        {
            TileHealing h = Setup(out BrushWorld w, out int[] t);
            w.Tiles.Damage(t[0], 60, TileCause.Bullet);
            w.Tiles.Damage(t[1], 60, TileCause.Bullet);
            SculptBar bar = Bar(1000);
            var me = Healer(1, Front(0));
            Assert.That(h.TryBegin(w, me, t[0], HealKind.Heal, bar, 0), Is.EqualTo(HealResult.Ok));
            Assert.That(bar.Value, Is.Zero);
            Assert.That(h.TryBegin(w, me, t[1], HealKind.Heal, bar, 1), Is.EqualTo(HealResult.Ok), "the old heal's 10% pays for the new one");
            Assert.That(bar.Value, Is.Zero);
        }

        [Test]
        public void LineOfSight_ATileBehindAnotherWallCantBeHealed_ButOneSeenThroughAHoleCan()
        {
            var w = new BrushWorld();
            int[] back = w.Tiles.AddWall(new Vec3d(0, 0, 12), Dir.East, 2, 1);
            int[] front = w.Tiles.AddWall(new Vec3d(0, 0, 10), Dir.East, 2, 1);
            var h = new TileHealing(w.Tiles);
            w.Tiles.Damage(back[0], 60, TileCause.Bullet);
            var eye = new Vec3d(0.75, 0.75, 8);
            Assert.That(h.Check(w, 1, eye, true, back[0], HealKind.Heal, 10000), Is.EqualTo(HealResult.NoLineOfSight));
            w.Tiles.Set(front[0], TileState.Broken, 0, TileCause.Bullet);
            Assert.That(h.Check(w, 1, eye, true, back[0], HealKind.Heal, 10000), Is.EqualTo(HealResult.Ok), "through the hole");
            Assert.That(h.Check(w, 1, eye, true, front[0], HealKind.Heal, 10000), Is.EqualTo(HealResult.Ok), "the hole itself");
        }

        // ------------------------------------------------------------------ aiming

        [Test]
        public void Aim_PicksTheTileUnderTheCrosshair_HolesIncluded_NotThroughFrame()
        {
            Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[1], TileState.Broken, 0, TileCause.Bullet);
            var eye = new Vec3d(2.25, 0.75, 7);
            var ahead = new Vec3d(0, 0, 1);
            TileTarget hole = TileAim.Find(w, w.Tiles, eye, ahead, 6, HealKind.Heal);
            Assert.That(hole.Tile, Is.EqualTo(t[1]), "a broken tile is aimed at as a hole");
            Assert.That(hole.Magnetised, Is.False);
            Assert.That(hole.Point.Z, Is.EqualTo(10).Within(1e-9));

            TileTarget far = TileAim.Find(w, w.Tiles, new Vec3d(2.25, 0.75, 3.9), ahead, 6, HealKind.Heal);
            Assert.That(far.Valid, Is.False, "6.1 m away");

            w.AddBox(new Vec3d(1.5, 0, 8), new Vec3d(3, 3, 8.3), BrushKind.Frame); // a beam in front
            Assert.That(TileAim.Find(w, w.Tiles, eye, ahead, 6, HealKind.Heal).Valid, Is.False, "no healing through the frame");
        }

        [Test]
        public void Aim_NearMissOnAHealableTile_IsMagnetised_ButAnIntactOneUnderTheCrosshairStillShows()
        {
            Setup(out BrushWorld w, out int[] t);
            w.Tiles.Set(t[1], TileState.Broken, 0, TileCause.Bullet); // x 1.5..3
            var eye = new Vec3d(1.4, 0.75, 7); // crosshair on tile 0, just left of the hole
            TileTarget a = TileAim.Find(w, w.Tiles, eye, new Vec3d(0, 0, 1), 6, HealKind.Heal);
            Assert.That(a.Tile, Is.EqualTo(t[1]));
            Assert.That(a.Magnetised, Is.True);
            TileTarget b = TileAim.Find(w, w.Tiles, new Vec3d(0.5, 0.75, 7), new Vec3d(0, 0, 1), 6, HealKind.Heal);
            Assert.That(b.Tile, Is.EqualTo(t[0]), "nothing healable near: the intact tile under the crosshair");
            TileTarget c = TileAim.Find(w, w.Tiles, new Vec3d(0.5, 0.75, 7), new Vec3d(0, 0, 1), 6, HealKind.Reinforce);
            Assert.That(c.Tile, Is.EqualTo(t[0]), "reinforce takes what is under the crosshair");
            Assert.That(c.Magnetised, Is.False);
        }

        // ------------------------------------------------------------------ the bar

        [Test]
        public void Bar_RefillsTwoPercentPerSecond_Exactly()
        {
            var s = new SculptConfig();
            C.ApplyBarTo(s);
            Assert.That(s.RegenFullTicks, Is.EqualTo(3000), "empty to full in 50 s");
            Assert.That(s.RegenDelayTicks, Is.Zero);
            var bar = new SculptBar();
            bar.Spend(SculptConfig.BarMax, 0);
            for (int k = 1; k <= 60; k++) bar.Tick(k, s);
            Assert.That(bar.Value, Is.EqualTo(200), "2% after one second");
            for (int k = 61; k <= 3000; k++) bar.Tick(k, s);
            Assert.That(bar.Value, Is.EqualTo(SculptConfig.BarMax), "full after 50 s, no drift");
        }

        [Test]
        public void Bar_DamageGain_OnePercentPerTenDamage_FractionsCarry()
        {
            var bar = Bar(0);
            Assert.That(bar.GainForDamage(10, 100), Is.EqualTo(100));
            Assert.That(bar.GainForDamage(13, 100), Is.EqualTo(130));
            Assert.That(bar.GainForDamage(0, 100), Is.Zero);
            Assert.That(bar.GainForDamage(-5, 100), Is.Zero);
            Assert.That(bar.Value, Is.EqualTo(230));
            var odd = Bar(0);
            int got = 0;
            for (int i = 0; i < 10; i++) got += odd.GainForDamage(1, 33); // 3.3 bp per point
            Assert.That(got, Is.EqualTo(33), "ten 1-damage hits give exactly what one 10-damage hit would");
            var full = Bar(SculptConfig.BarMax - 50);
            Assert.That(full.GainForDamage(100, 100), Is.EqualTo(50), "capped at full");
            Assert.That(full.Value, Is.EqualTo(SculptConfig.BarMax));
        }

        [Test]
        public void Bar_HidingAndHealingForeverRunsOut()
        {
            // With only the refill, one heal every 5 s is the most you can sustain (10% per heal, 2% per second).
            var s = new SculptConfig();
            C.ApplyBarTo(s);
            var bar = new SculptBar();
            int heals = 0;
            for (int k = 0; k < 60 * 60; k++)
            {
                if (k % 60 == 0 && bar.CanAfford(C.HealCost)) { bar.Spend(C.HealCost, k); heals++; }
                bar.Tick(k, s);
            }
            Assert.That(heals, Is.InRange(20, 23), "one a second for 10 s, then one per 5 s");
        }

        // ------------------------------------------------------------------ wire

        [Test]
        public void HealPress_RoundTripsOnTheWire_AndNeverRepeats()
        {
            var c = InputCmd.Quantize(new InputCmd { Tick = 50, Hand = new HandInput { Heal = true, HealTile = 1234, HealReinforce = true } });
            var w = new ByteWriter(128);
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = 1, Cmds = new List<InputCmd> { c } });
            byte[] d = w.ToArray();
            var r = new ByteReader(d, 0, d.Length);
            var into = new List<InputCmd>();
            Protocol.ReadInputs(ref r, into);
            Assert.That(into[0].Hand.Heal, Is.True);
            Assert.That(into[0].Hand.HealTile, Is.EqualTo(1234));
            Assert.That(into[0].Hand.HealReinforce, Is.True);
            Assert.That(c.AsRepeat(51).Hand.Heal, Is.False);
            var bad = InputCmd.Quantize(new InputCmd { Hand = new HandInput { Heal = true, HealTile = 70000 } });
            Assert.That(bad.Hand.Heal, Is.False, "a tile id the wire can't carry is dropped");
            var stray = InputCmd.Quantize(new InputCmd { Hand = new HandInput { HealReinforce = true } });
            Assert.That(stray.Hand.HealReinforce, Is.False);
        }

        [Test]
        public void HealEvents_RoundTripInTheTilesMessage()
        {
            var heals = new List<HealEvent>
            {
                new HealEvent { Type = HealEventType.Began, Player = 3, Tile = 77, Kind = HealKind.Reinforce, Tick = 500, EndTick = 548 },
                new HealEvent { Type = HealEventType.Cancelled, Player = 4, Tile = 12, Kind = HealKind.Heal, Tick = 500, EndTick = 510, Result = HealResult.PlayerInside },
            };
            var w = new ByteWriter(128);
            TileProtocol.Write(w, new TilesMsg { Tick = 500, Changes = new List<TileChange>(), Heals = heals });
            byte[] d = w.ToArray();
            var r = new ByteReader(d, 0, d.Length);
            var back = new List<HealEvent>();
            TileProtocol.Read(ref r, new List<TileChange>(), back);
            Assert.That(back.Count, Is.EqualTo(2));
            Assert.That(back[0].Type, Is.EqualTo(HealEventType.Began));
            Assert.That(back[0].Kind, Is.EqualTo(HealKind.Reinforce));
            Assert.That(back[0].Player, Is.EqualTo(3));
            Assert.That(back[0].Tile, Is.EqualTo(77));
            Assert.That(back[0].EndTick, Is.EqualTo(548));
            Assert.That(back[1].Result, Is.EqualTo(HealResult.PlayerInside));
            d[d.Length - 8] = 0x0F; // bad event type
            var bad = new ByteReader(d, 0, d.Length);
            Assert.Throws<NetFormatException>(() => TileProtocol.Read(ref bad, new List<TileChange>(), back));
        }

        // ------------------------------------------------------------------ networked

        private static SculptNetHarness Arena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }, tweak: s => new BreachConfig().ApplyBarTo(s), wrap: g =>
        {
            var w = new BrushWorld();
            int[] ids = w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 8, 3);
            w.Tiles.Set(ids[2], TileState.Broken, 0, TileCause.Built);
            w.Tiles.Changes.Clear();
            return new CompositeTerrain(g, w);
        });

        /// <summary>Enter sculpt mode at once, press heal on tile 2 (broken, bottom row) half a second in.</summary>
        private static Func<SculptNetHarness.Peer, int, SculptTickInput> HealScript(Func<int> start, bool reinforce = false) => (p, t) =>
        {
            var i = new SculptTickInput();
            int s = start();
            i.Hand.SculptToggle = t == s + 2;
            if (t == s + 30) { i.Hand.Heal = true; i.Hand.HealTile = 2; i.Hand.HealReinforce = reinforce; }
            return i;
        };

        [Test]
        public void Networked_HealAtPing_ShowsAtOnceAndPatchesOnTheServerAndClient()
        {
            var h = Arena();
            var a = h.AddClient(new Vec3d(9.75, 0, 9), up: SculptNetHarness.Lag(60), down: SculptNetHarness.Lag(60));
            int start = -1;
            bool sawPredicted = false;
            int barAtPress = -1, shownAfterPress = -1;
            var script = HealScript(() => start);
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                if (t == start + 30) barAtPress = p.Client.DisplayBar;
                if (t == start + 32)
                {
                    shownAfterPress = p.Client.DisplayBar;
                    sawPredicted = p.Client.TryGetOwnHeal(out HealJob j, out bool confirmed) && !confirmed && j.Tile == 2;
                }
                return script(p, t);
            };
            h.RunSeconds(0.4);
            int corrections = a.Client.Corrections;
            h.RunSeconds(2.0);
            Assert.That(sawPredicted, Is.True, "your heal shows the tick you press, before the server answers");
            Assert.That(barAtPress - shownAfterPress, Is.InRange(990, 1000), "and the bar drops at once");
            Assert.That(h.Server.Walls[2].State, Is.EqualTo(TileState.Patched));
            Assert.That(h.Server.Walls[2].Health, Is.EqualTo(50));
            Assert.That(a.Client.Walls[2].State, Is.EqualTo(TileState.Patched), "the client copies it");
            Assert.That(a.Client.Heals.Count, Is.Zero);
            Assert.That(a.Client.HealEvents.Exists(e => e.Type == HealEventType.Completed && e.Tile == 2), Is.True);
            Assert.That(a.Client.Corrections, Is.EqualTo(corrections), "healing never corrects movement");
        }

        [Test]
        public void Networked_AnotherPlayerSeesTheHealAsItRuns()
        {
            var h = Arena();
            var a = h.AddClient(new Vec3d(9.75, 0, 9), up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            var b = h.AddClient(new Vec3d(14, 0, 16), team: 2, seed: 2, up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            int start = -1;
            var script = HealScript(() => start, reinforce: true);
            a.Script = (p, t) => { if (start < 0) start = t; return script(p, t); };
            bool bSaw = false;
            b.Script = (p, t) =>
            {
                if (p.Client.Heals != null && p.Client.Heals.HealerOf(2) == a.ServerSide.Id) bSaw = true;
                return default;
            };
            h.RunSeconds(2.4);
            Assert.That(bSaw, Is.True, "the enemy can see the tile is being reinforced");
            Assert.That(b.Client.Walls[2].State, Is.EqualTo(TileState.Reinforced));
            Assert.That(h.Server.BarOf(a.ServerSide.Id).Value, Is.LessThan(SculptConfig.BarMax - 2000), "25% spent (minus the refill since)");
        }

        [Test]
        public void Networked_LateJoinerSeesARunningHeal()
        {
            var h = Arena();
            var a = h.AddClient(new Vec3d(9.75, 0, 9));
            int start = -1;
            var script = HealScript(() => start);
            a.Script = (p, t) => { if (start < 0) start = t; return script(p, t); };
            for (int k = 0; k < 60 && h.Server.Healing.Count == 0; k++) h.RunMs(20); // pressed half a second in, runs 0.8 s
            Assert.That(h.Server.Healing.Count, Is.EqualTo(1));
            var late = h.AddClient(new Vec3d(14, 0, 16), team: 2, seed: 2);
            h.RunSeconds(0.2);
            Assert.That(late.Client.Heals.HealerOf(2), Is.EqualTo(a.ServerSide.Id));
        }

        [Test]
        public void Networked_DamageToAnEnemyEarnsBar_NotToATeammate()
        {
            var h = Arena();
            var a = h.AddClient(new Vec3d(20, 0, 4), team: 1);
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
            h.RunSeconds(0.3);
            SculptBar bar = h.Server.BarOf(a.ServerSide.Id);
            bar.SetValue(0);
            h.RunSeconds(1.2);
            Assert.That(enemy.ServerSide.State.Damage, Is.EqualTo(30));
            // 30 damage = 3%, plus 2%/s refill over 1.2 s (2.4%).
            Assert.That(bar.Value, Is.InRange(300 + 230, 300 + 250));
            Assert.That(mate.ServerSide.State.Damage, Is.Zero);
        }
    }
}
