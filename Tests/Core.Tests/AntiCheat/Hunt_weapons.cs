using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Weapons
{
    /// <summary>
    /// Red-team hunt (weapons): a fully modified client is a bot here (it only ever produces an InputCmd, the exact thing
    /// a hacked client can put on the wire), so these drive a real <see cref="NetServer"/> with forged fire/swap/reload
    /// command streams and assert the server's behaviour. Confirms what is SAFE (the server owns the fire gate, ammo,
    /// recoil and the sprint/dead/sculpt refusals) and proves one miscalibrated integrity threshold.
    /// </summary>
    public class HuntWeaponsTests
    {
        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        /// <summary>A bot that emits whatever InputCmd the test's function returns (a forged client).</summary>
        private sealed class Puppet : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        private static NetServer Server() => new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });

        /// <summary>Puts a gun in the puppet's hand, ready to fire (selects the slot and waits out the draw).</summary>
        private static ServerPlayer HoldingGun(NetServer s, Weapon gun, Func<bool> fireHeld, Func<bool> firePressed = null)
        {
            int slot = (int)gun + 1; // standard loadout: slot 1 pump, 2 SMG, 3 AR, 4 pistol
            bool selected = false;
            var puppet = new Puppet
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    if (!selected) { c.Hand.WeaponKey = (byte)slot; selected = true; }
                    c.Move.FireHeld = fireHeld != null && fireHeld();
                    c.Move.FirePressed = firePressed != null && firePressed();
                    return c;
                },
            };
            ServerPlayer p = s.AddBot(puppet, 1, new Vec3d(20, 0, 20), 0);
            for (int i = 0; i < 70; i++) s.Step(); // select + draw (longest draw is the pump, 24*Per60 = 48 ticks)
            Assert.That(p.State.Hand.Held, Is.EqualTo(gun));
            Assert.That(p.State.Hand.Mode, Is.EqualTo(HandMode.Weapon));
            return p;
        }

        /// <summary>Shot ticks of a player from the server's combat events over <paramref name="ticks"/> steps.</summary>
        private static List<int> ShotTicks(NetServer s, int playerId, int ticks)
        {
            var at = new List<int>();
            for (int i = 0; i < ticks; i++)
            {
                s.Step();
                foreach (CombatEvent e in s.CombatEvents)
                    if (e.Type == CombatEventType.Shot && e.Attacker == playerId) at.Add(s.Tick);
            }
            return at;
        }

        // ============================================================ SAFE: the fire gate can't be beaten by input

        [Test]
        public void ForgedFireSpam_CannotBeatAnyGunsFireInterval_NorRaiseFireTooFast()
        {
            // The client only sends button bits; the server runs WeaponRules.Step and gates every shot with now < next.
            // Holding fire every single tick must still produce shots no closer than the gun's real minimum gap.
            // The minimum achievable integer gap is floor(intervalMilliTicks / 1000): the fire gate re-anchors each shot
            // so the long-run rate is exact, and ceil() of the fractional fire times can shrink one gap to the floor.
            foreach ((Weapon gun, bool automatic, int minGap) in new[]
            {
                (Weapon.Pump, true, 90),   // 90000 milli-ticks = 90.0  -> floor 90
                (Weapon.Smg, true, 10),    // 10910 milli-ticks = 10.91 -> floor 10
                (Weapon.Ar, true, 21),     // 21818 milli-ticks = 21.818 -> floor 21
                (Weapon.Pistol, false, 24) // 24000 milli-ticks = 24.0  -> floor 24, semi-auto
            })
            {
                NetServer s = Server();
                // Spam both bits every tick (a turbo-fire macro): autos read FireHeld, semi-auto reads FirePressed.
                ServerPlayer p = HoldingGun(s, gun, () => true, () => true);
                List<int> ticks = ShotTicks(s, p.Id, 3 * SimTime.TicksPerSecond);
                Assert.That(ticks.Count, Is.GreaterThanOrEqualTo(3), $"{gun} should fire repeatedly");
                for (int i = 1; i < ticks.Count; i++)
                    Assert.That(ticks[i] - ticks[i - 1], Is.GreaterThanOrEqualTo(minGap),
                        $"{gun} fired {ticks[i] - ticks[i - 1]} ticks apart, under its {minGap}-tick minimum");
                Assert.That(s.Flags.Count(FlagKind.FireTooFast), Is.Zero, $"{gun}: legit rate must not be flagged");
            }
        }

        [Test]
        public void SemiAutoPistol_TurboFireMacro_ReachesTheRateCap_OneClickPerTick()
        {
            // The pistol is "semi-auto, one shot per click". A macro sending FirePressed every tick is accepted and fires
            // at exactly the 24-tick cap (5/s) with zero clicking skill. Still capped by the server, so this is a turbo
            // macro (low severity), NOT a fire-rate bypass.
            NetServer s = Server();
            ServerPlayer p = HoldingGun(s, Weapon.Pistol, null, () => true);
            List<int> ticks = ShotTicks(s, p.Id, 2 * SimTime.TicksPerSecond);
            Assert.That(ticks.Count, Is.GreaterThanOrEqualTo(9), "a macro gets the full 5/s cap effortlessly");
            for (int i = 1; i < ticks.Count; i++)
                Assert.That(ticks[i] - ticks[i - 1], Is.EqualTo(24), "but never faster than the 24-tick cap");
            Assert.That(s.Flags.Count(FlagKind.FireTooFast), Is.Zero);
        }

        // ============================================================ SAFE: refusals

        [Test]
        public void Firing_IsRefused_WhileSprinting()
        {
            NetServer s = Server();
            // Sprint forward (MoveZ>0, SprintHeld) while holding fire: movement sets Sprinting, so CanShoot is false.
            bool fire = false;
            var puppet = new Puppet
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    c.Hand.WeaponKey = me.State.Hand.Held == Weapon.Ar && me.State.Hand.Mode == HandMode.Weapon ? (byte)0 : (byte)3;
                    c.Move.SprintHeld = true;
                    c.Move.MoveZ = 1;
                    c.Move.FireHeld = fire;
                    return c;
                },
            };
            ServerPlayer p = s.AddBot(puppet, 1, new Vec3d(20, 0, 20), 0);
            for (int i = 0; i < 70; i++) s.Step();
            fire = true;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(p.State.Sprinting, Is.True, "still sprinting");
            int shots = 0;
            foreach (CombatEvent e in s.CombatEvents) if (e.Type == CombatEventType.Shot && e.Attacker == p.Id) shots++;
            Assert.That(shots, Is.Zero, "a sprinting player can't fire");
        }

        [Test]
        public void Magazine_CannotBeExceeded_ByForgedFire_NorMagOverflowRaised()
        {
            NetServer s = Server();
            ServerPlayer p = HoldingGun(s, Weapon.Ar, () => true, () => true);
            // Hold fire far longer than a magazine lasts (AR mag 25).
            for (int i = 0; i < 6 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(p.State.Weapon.UsedOf(Weapon.Ar), Is.LessThanOrEqualTo(25), "can never fire past the magazine");
            Assert.That(s.Flags.Count(FlagKind.MagOverflow), Is.Zero);
        }

        // ============================================================ SAFE: FireTooFast threshold matches the real minimum gap

        [Test]
        public void FireTooFast_SmgThreshold_ExactlyMatchesTheMinimumAchievableGap()
        {
            // interval = (int)(FireIntervalMilliTicks / 1000) = floor(10.91) = 10. That floor is exactly the smallest
            // integer gap the fire gate can legitimately produce (the averaging shrinks one gap in ~10 to the floor), so
            // a 10-tick gap is legit and correctly NOT flagged, while a 9-tick gap (impossible by the rules) IS flagged.
            // The threshold is tight and correct: no false positive and no sub-legal gap slips through.
            NetServer s = Server();
            bool fire = false;
            var puppet = new Puppet
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    if (me.State.Hand.Held != Weapon.Smg) c.Hand.WeaponKey = 2;
                    c.Move.FireHeld = fire;
                    return c;
                },
            };
            ServerPlayer p = s.AddBot(puppet, 1, new Vec3d(20, 0, 20), 0);
            for (int i = 0; i < 40; i++) s.Step(); // draw the SMG

            // Fire the SMG at a chosen gap by opening its fire gate on exactly the ticks we want it to shoot.
            void FireNow() { p.State.Weapon.SetNext(Weapon.Smg, 0); fire = true; s.Step(); fire = false; }
            void Idle(int n) { for (int i = 0; i < n; i++) s.Step(); }

            // Control: a 9-tick gap is below the truncated threshold (9 < 10) and IS flagged -> the mechanism works.
            int before9 = s.Flags.Count(FlagKind.FireTooFast);
            FireNow(); Idle(8); FireNow();        // two shots 9 ticks apart
            int flagged9 = s.Flags.Count(FlagKind.FireTooFast) - before9;
            Assert.That(flagged9, Is.GreaterThan(0), "a 9-tick SMG gap is correctly flagged");

            // A 10-tick gap is the legit minimum (continuous fire produces it), so it is correctly NOT flagged.
            Idle(40);
            int before10 = s.Flags.Count(FlagKind.FireTooFast);
            FireNow(); Idle(9); FireNow();        // two shots 10 ticks apart
            int flagged10 = s.Flags.Count(FlagKind.FireTooFast) - before10;
            Assert.That(flagged10, Is.Zero, "a 10-tick SMG gap is achievable by the rules, so it must not be flagged");
        }
    }
}
