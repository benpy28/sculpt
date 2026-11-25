using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Weapons
{
    /// <summary>Guns end to end: client prediction, server hitscan with rewind, damage, death and the combat message.</summary>
    public class CombatNetTests
    {
        private const double B = SubGrid.TileSize;

        /// <summary>32 x 32 flat tiles with a 3-level wall at tiles x 0..31, z 20 (z 24..25.2 m).</summary>
        private static SculptNetHarness Arena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            new TileMap(g).Fill(new Int3(0, 0, 20), new Int3(31, 2, 20), Tile.Solid);
            g.ClearChanges();
            return g;
        });

        private static void AimAt(NetClient c, Vec3d point, out double yaw, out float pitch)
        {
            MotorState s = c.Predicted;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, c.Movement), 0);
            Vec3d d = point - eye;
            yaw = Math.Atan2(d.X, d.Z);
            pitch = (float)(-Math.Asin(d.Y / d.Length) * 180 / Math.PI);
        }

        /// <summary>
        /// Shooter script: equip a gun at tick 1, then aim at the target as drawn (its chest) and fire when told.
        /// <paramref name="fire"/> is asked with the time since the start in sixtieths of a second (the scripts were
        /// written at 60 Hz); a press lands on the first tick of that sixtieth, holding covers all of it.
        /// </summary>
        private static Func<SculptNetHarness.Peer, int, SculptTickInput> Shooter(Weapon w, Func<SculptNetHarness.Peer> target, Func<int, bool> fire, double chest = 1.0)
        {
            int start = int.MinValue;
            bool wants = false;
            return (p, t) =>
            {
                if (start == int.MinValue) start = t;
                var i = new SculptTickInput();
                if (t == start + 1) i.Hand.WeaponKey = (byte)((int)w + 1);
                SculptNetHarness.Peer victim = target();
                if (p.Client.TryGetRemote(victim.ServerSide.Id, out RemoteView v))
                {
                    AimAt(p.Client, v.Position + new Vec3d(0, chest, 0), out double yaw, out i.Pitch);
                    i.Move.Yaw = yaw;
                }
                int kk = t - start;
                if (kk % SimTime.Per60 == 0) wants = fire(kk / SimTime.Per60); // asked once per sixtieth (scripts count shots)
                bool f = kk > 40 * SimTime.Per60 && wants;
                i.Move.FireHeld = f;
                i.Move.FirePressed = f && kk % SimTime.Per60 == 0;
                i.Move.WantsToShoot = f;
                return i;
            };
        }

        private static int CountHits(SculptNetHarness.Peer p, int victim)
        {
            int n = 0;
            foreach (var kv in p.Client.Combat) if (kv.Value.Type == CombatEventType.Hit && kv.Value.Victim == victim) n++;
            return n;
        }

        [Test]
        public void PistolKillsInSixBodyShotsAndEveryoneHears()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            a.Script = Shooter(Weapon.Pistol, () => b, k => k % 20 == 0);
            h.RunSeconds(0.7 + 6 * 20.0 / 60 + 0.3); // the script counts sixtieths

            Assert.That(b.ServerSide.State.Dead, Is.True, "6 x 25.5 = 153 (5 x 25.5 = 127.5 is not enough)");
            Assert.That(CountHits(a, b.ServerSide.Id), Is.EqualTo(6), "the shooter gets a hit marker per shot");
            Assert.That(a.ServerSide.Kills, Is.EqualTo(1));
            Assert.That(b.Client.Predicted.Dead, Is.True, "the victim's client learns it died");
            bool killSeen = false, shotSeen = false;
            foreach (var kv in b.Client.Combat)
            {
                if (kv.Value.Type == CombatEventType.Kill && kv.Value.Victim == b.ServerSide.Id && kv.Value.Attacker == a.ServerSide.Id) killSeen = true;
                if (kv.Value.Type == CombatEventType.Shot && kv.Value.Attacker == a.ServerSide.Id) shotSeen = true;
            }
            Assert.That(killSeen && shotSeen, Is.True, "the victim sees the shots and the kill");
            foreach (var kv in a.Client.Combat) Assert.That(kv.Value.Type == CombatEventType.Shot && kv.Value.Attacker == a.ServerSide.Id, Is.False, "own shots aren't echoed");
        }

        [Test]
        public void DeadPlayerRespawnsAtSpawnWithFullHealth()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            a.Script = Shooter(Weapon.Pistol, () => b, k => k % 20 == 0 && k <= 160);
            h.RunSeconds(3.0);
            Assert.That(b.ServerSide.State.Dead, Is.True);
            h.RunSeconds(h.Movement.Weapons.RespawnDelayTicks / (double)SimTime.TicksPerSecond);
            Assert.That(b.ServerSide.State.Dead, Is.False);
            Assert.That(b.ServerSide.State.Damage, Is.EqualTo(0));
            Assert.That((b.ServerSide.State.Position - b.ServerSide.Spawn).HorizontalLength, Is.LessThan(0.01));
            h.RunSeconds(0.3);
            Assert.That(b.Client.Predicted.Dead, Is.False);
            Assert.That(b.Client.Predicted.Damage, Is.EqualTo(0));
        }

        [Test]
        public void WallsStopBullets()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 15), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 24), team: 2); // 3-level wall in between
            a.Script = Shooter(Weapon.Ar, () => b, k => true);
            h.RunSeconds(2);
            Assert.That(CountHits(a, b.ServerSide.Id), Is.EqualTo(0));
            Assert.That(b.ServerSide.State.Damage, Is.EqualTo(0));
        }

        [Test]
        public void ShootingPredictsExactly()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1, up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            var b = h.AddClient(SculptNetHarness.FeetIn(14, 12), team: 2);
            var aim = Shooter(Weapon.Smg, () => b, k => (k / 50) % 2 == 0);
            a.Script = (p, t) =>
            {
                SculptTickInput i = aim(p, t);
                i.Move.MoveX = Math.Sin(t * 0.05 / SimTime.Per60); // strafing while spraying
                return i;
            };
            h.RunSeconds(1); // join: the clock settles (one resync is normal here)
            int c0 = a.Client.Corrections, r0 = a.Client.SilentResyncs;
            h.RunSeconds(4);
            Assert.That(a.Client.Corrections - c0, Is.EqualTo(0), "recoil, bloom and fire timers match the server bit for bit");
            Assert.That(a.Client.SilentResyncs - r0, Is.EqualTo(0), "not even an invisible difference");
            Assert.That(CountHits(a, b.ServerSide.Id), Is.GreaterThanOrEqualTo(10), "B dies at 10 hits");
        }

        /// <summary>Strafing target at 150 ms round trip: aiming at the drawn target hits with rewind and mostly misses without.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void LagCompensationHitsWhatYouSee(bool rewind)
        {
            var h = Arena();
            if (!rewind) h.Movement.Weapons.MaxRewindTicks = 0;
            h.Movement.Weapons.MaxHealth = 100000; // nobody dies: count every hit
            h.Movement.Weapons.Of(Weapon.Pistol).MagSize = 250; // every press is a shot (no reload pauses)
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1, up: SculptNetHarness.Lag(75), down: SculptNetHarness.Lag(75));
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 14), team: 2);
            b.Script = (p, t) => new SculptTickInput { Move = new MoveInput { MoveX = (t / (50 * SimTime.Per60)) % 2 == 0 ? 1 : -1 } };
            int shots = 0;
            a.Script = Shooter(Weapon.Pistol, () => b, k => { bool f = k % 15 == 0 && k > 60; if (f) shots++; return f; });
            h.RunSeconds(6);
            int hits = CountHits(a, b.ServerSide.Id);
            Assert.That(shots, Is.GreaterThan(15));
            if (rewind) Assert.That(hits, Is.GreaterThanOrEqualTo((int)(shots * 0.85)), $"{hits}/{shots} hit");
            else Assert.That(hits, Is.LessThan(shots / 2), $"{hits}/{shots} hit without rewind");
        }

        [Test]
        public void RewindMatchesWhatTheClientDrew()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1, up: SculptNetHarness.Lag(60), down: SculptNetHarness.Lag(60));
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 14), team: 2);
            b.Script = (p, t) => new SculptTickInput { Move = new MoveInput { MoveX = (t / (37 * SimTime.Per60)) % 2 == 0 ? 1 : -1, MoveZ = 0.4 } };
            double worst = 0;
            int samples = 0;
            a.Script = (p, t) =>
            {
                if (p.Client.TryGetRemote(b.ServerSide.Id, out RemoteView v) && !v.Extrapolated)
                {
                    double view = p.Client.RemoteRenderTick;
                    if (h.Server.TryGetRewound(b.ServerSide, view, out Vec3d feet, out _, out _))
                    {
                        worst = Math.Max(worst, (feet - v.Position).Length);
                        samples++;
                    }
                }
                return default;
            };
            h.RunSeconds(4);
            Assert.That(samples, Is.GreaterThan(100));
            Assert.That(worst, Is.LessThan(1e-3), "server rewind == client interpolation (float snapshots)");
        }

        [Test]
        public void PumpCycleSurvivesSwapsOverTheNetwork()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1, up: SculptNetHarness.Lag(50), down: SculptNetHarness.Lag(50));
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 9), team: 2);
            var shotTicks = new List<int>();
            int start = int.MinValue;
            a.Script = (p, t) =>
            {
                if (start == int.MinValue) start = t;
                const int P = SimTime.Per60;
                int k = t - start;
                var i = new SculptTickInput();
                if (p.Client.TryGetRemote(b.ServerSide.Id, out RemoteView v))
                {
                    AimAt(p.Client, v.Position + new Vec3d(0, 1.0, 0), out double yaw, out i.Pitch);
                    i.Move.Yaw = yaw;
                }
                if (k == 60 * P || (k > 64 * P && k < 140 * P)) { i.Move.FireHeld = true; i.Move.FirePressed = k == 60 * P; i.Move.WantsToShoot = true; }
                if (k == 62 * P) i.Hand.WeaponKey = 2; // SMG
                if (k == 64 * P) i.Hand.WeaponKey = 1; // back to the pump at once
                return i;
            };
            h.RunSeconds(1);
            int c0 = a.Client.Corrections;
            h.RunSeconds(2);
            foreach (var kv in b.Client.Combat)
                if (kv.Value.Type == CombatEventType.Shot) shotTicks.Add(kv.Key);
            Assert.That(shotTicks.Count, Is.EqualTo(2), "pump, then pump again (the SMG never got drawn)");
            Assert.That(shotTicks[1] - shotTicks[0], Is.EqualTo(45 * SimTime.Per60));
            Assert.That(a.Client.Corrections - c0, Is.EqualTo(0));
        }

        [Test]
        public void ThrowDamageCountsTowardHealth()
        {
            var cfg = new WeaponsConfig();
            var s = MotorState.At(Vec3d.Zero);
            Assert.That(WeaponRules.ApplyDamage(ref s, 70, 10, cfg), Is.False);
            Assert.That(WeaponRules.ApplyDamage(ref s, 70, 11, cfg), Is.False);
            Assert.That(s.Health(cfg), Is.EqualTo(10));
            Assert.That(WeaponRules.ApplyDamage(ref s, 70, 12, cfg), Is.True);
            Assert.That(WeaponRules.ApplyDamage(ref s, 70, 13, cfg), Is.False, "no double kill");
        }
    }
}
