using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Weapons
{
    /// <summary>Gun rules on one player stepped through the shared simulation (NetSim), as client and server run it.</summary>
    public class WeaponTests
    {
        private const double B = SubGrid.TileSize;
        private MovementConfig _mc;
        private NetConfig _nc;
        private SubGrid _grid;
        private MotorState _s;
        private int _tick;
        private List<WeaponShot> _shots;
        private List<MoveEvent> _events;
        private Vec3d _spawn;

        [SetUp]
        public void SetUp()
        {
            _mc = new MovementConfig();
            _nc = new NetConfig();
            _grid = new SubGrid(4, 4, 4, -16);
            _grid.FillBaseGround();
            _spawn = new Vec3d(8 * B + 0.6, 0, 8 * B + 0.6);
            _s = MotorState.At(_spawn);
            _tick = 1000;
            _shots = new List<WeaponShot>();
            _events = new List<MoveEvent>();
            Run(30, default); // land and settle
            _shots.Clear();
        }

        private void Step(MoveInput m, HandInput hand = default, float pitch = 0)
        {
            _tick++;
            var cmd = InputCmd.Quantize(new InputCmd { Tick = _tick, Move = m, Pitch = pitch, Hand = hand });
            NetSim.Simulate(_grid, _mc, _nc, ref _s, cmd, _spawn, _events, _shots);
        }

        private void Run(int ticks, MoveInput m) { for (int i = 0; i < ticks; i++) Step(m); }

        private static MoveInput Hold => new MoveInput { FireHeld = true, WantsToShoot = true };
        private static MoveInput Press => new MoveInput { FireHeld = true, FirePressed = true, WantsToShoot = true };
        private static HandInput Key(Weapon w) => new HandInput { WeaponKey = (byte)((int)w + 1) };

        private void Equip(Weapon w)
        {
            Step(default, Key(w));
            Run(_mc.Hand.Draw(w) + 1, default);
            Assert.That(_s.Hand.Held, Is.EqualTo(w));
            Assert.That(_s.Hand.CanFire, Is.True);
            _shots.Clear();
        }

        // ---------------------------------------------------------------- Damage numbers

        [Test]
        public void FalloffMatchesSpec()
        {
            WeaponsConfig c = _mc.Weapons;
            WeaponStats pump = c.Of(Weapon.Pump), smg = c.Of(Weapon.Smg), ar = c.Of(Weapon.Ar), pistol = c.Of(Weapon.Pistol);
            Assert.That(pump.DamageFactorAt(2), Is.EqualTo(1.0));
            Assert.That(pump.DamageFactorAt(5), Is.EqualTo(1.0));
            Assert.That(pump.DamageFactorAt(12), Is.EqualTo(0.65).Within(1e-9));
            Assert.That(pump.DamageFactorAt(40), Is.EqualTo(0.2).Within(1e-9), "heavy past 12 m");
            Assert.That(smg.DamageFactorAt(10), Is.EqualTo(1.0));
            Assert.That(smg.DamageFactorAt(20), Is.LessThan(0.7), "heavy past 10 m");
            Assert.That(ar.DamageFactorAt(200), Is.EqualTo(1.0), "AR has no falloff");
            Assert.That(pistol.DamageFactorAt(25), Is.EqualTo(1.0));
            Assert.That(pistol.DamageFactorAt(37.5), Is.EqualTo(0.85).Within(1e-9));
            Assert.That(pistol.DamageFactorAt(50), Is.EqualTo(0.7).Within(1e-9));
            Assert.That(pistol.DamageFactorAt(80), Is.EqualTo(0.7).Within(1e-9));
            Assert.That(pump.BodyDamage * pump.Pellets, Is.EqualTo(84.6).Within(1e-9), "Ben 2026-11-25: ~15% less than 99");
            Assert.That(pump.HeadDamage / pump.BodyDamage, Is.EqualTo(1.5));
            Assert.That((ar.BodyDamage, ar.HeadDamage), Is.EqualTo((21.5, 34.0)));
            Assert.That((pistol.BodyDamage, pistol.HeadDamage), Is.EqualTo((25.5, 42.5)));
            Assert.That(c.MaxHealth, Is.EqualTo(150));
        }

        // ---------------------------------------------------------------- Fire rates

        private int CountShots(Weapon w, int ticks, Func<int, MoveInput> input)
        {
            Equip(w);
            for (int i = 0; i < ticks; i++) Step(input(i));
            return _shots.Count;
        }

        [Test]
        public void SmgFiresTwelveTimesASecond() => Assert.That(CountShots(Weapon.Smg, 60, _ => Hold), Is.EqualTo(12));

        [Test]
        public void ArFiresFivePointFiveTimesASecondOnAverage()
        {
            Assert.That(CountShots(Weapon.Ar, 120, _ => Hold), Is.EqualTo(11));
            // Spacing alternates 11 and 10 ticks: the fractional rate is kept exactly.
            for (int i = 1; i < _shots.Count; i++) Assert.That(_shots[i].Tick - _shots[i - 1].Tick, Is.InRange(10, 11));
        }

        [Test]
        public void PistolIsSemiAutoAndCapsAtFivePerSecond()
        {
            Assert.That(CountShots(Weapon.Pistol, 60, _ => Hold), Is.EqualTo(1), "holding fires once");
            // Spam clicking every other tick: max 5 shots/s.
            Assert.That(CountShots(Weapon.Pistol, 60, i => i % 2 == 0 ? Press : default), Is.EqualTo(5));
        }

        [Test]
        public void PumpHoldsFireEveryFiftyFourTicks()
        {
            Assert.That(CountShots(Weapon.Pump, 109, _ => Hold), Is.EqualTo(3));
            Assert.That(_shots[1].Tick - _shots[0].Tick, Is.EqualTo(54));
        }

        [Test]
        public void EarlyPistolPressIsBufferedNotEaten()
        {
            Equip(Weapon.Pistol);
            Step(Press);
            int first = _shots[0].Tick;
            Run(7, default);           // 8 ticks after the shot
            Step(Press);               // 4 ticks early (ready at +12)
            Run(6, default);
            Assert.That(_shots.Count, Is.EqualTo(2));
            Assert.That(_shots[1].Tick, Is.EqualTo(first + 12), "fires on the first ready tick");
        }

        [Test]
        public void TooEarlyPistolPressIsDropped()
        {
            Equip(Weapon.Pistol);
            Step(Press);
            Step(default);
            Step(Press); // 10 ticks early: outside the 6-tick buffer
            Run(20, default);
            Assert.That(_shots.Count, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------- Swaps and the pump cycle

        [Test]
        public void PumpCycleKeepsRunningThroughASwap()
        {
            Equip(Weapon.Pump);
            Step(Press);
            int shot = _shots[0].Tick;
            Step(default, Key(Weapon.Smg));           // swap away at +1
            Run(_mc.Hand.Draw(Weapon.Smg), default);
            Step(default, Key(Weapon.Pump));          // and straight back: draw ends at about +44
            for (int i = 0; i < 80 && _shots.Count < 2; i++) Step(Hold);
            Assert.That(_shots.Count, Is.EqualTo(2));
            Assert.That(_shots[1].Tick, Is.EqualTo(shot + 54), "waits out the remainder of the 54-tick cycle");
        }

        [Test]
        public void PumpComingBackLatePaysOnlyTheDraw()
        {
            Equip(Weapon.Pump);
            Step(Press);
            Step(default, Key(Weapon.Smg));
            Run(60, default);                         // cycle is long over
            Step(default, Key(Weapon.Pump));
            int drawStart = _tick;
            for (int i = 0; i < 80 && _shots.Count < 2; i++) Step(Hold);
            Assert.That(_shots[1].Tick, Is.EqualTo(drawStart + _mc.Hand.Draw(Weapon.Pump)), "draw time, no extra wait");
        }

        [Test]
        public void SwapAfterPumpShotPaysTheOtherGunsDraw()
        {
            Equip(Weapon.Pump);
            Step(Press);
            Step(default, Key(Weapon.Smg));
            int swap = _tick;
            for (int i = 0; i < 40 && _shots.Count < 2; i++) Step(Hold);
            Assert.That(_shots[1].Weapon, Is.EqualTo(Weapon.Smg));
            Assert.That(_shots[1].Tick, Is.EqualTo(swap + _mc.Hand.Draw(Weapon.Smg)));
        }

        [Test]
        public void PumpCycleKeepsRunningThroughSculptMode()
        {
            Equip(Weapon.Pump);
            Step(Press);
            int shot = _shots[0].Tick;
            Step(default, new HandInput { SculptToggle = true });
            Run(8, default);
            Step(default, new HandInput { SculptToggle = true }); // exit: 12 ticks for the pump
            for (int i = 0; i < 80 && _shots.Count < 2; i++) Step(Hold);
            Assert.That(_shots[1].Tick, Is.EqualTo(shot + 54));
        }

        [Test]
        public void NoFireWhileDrawingSculptingOrDisarmed()
        {
            Step(default, Key(Weapon.Ar));
            for (int i = 0; i < _mc.Hand.Draw(Weapon.Ar) - 1; i++) Step(Hold);
            Assert.That(_shots, Is.Empty, "drawing");
            Run(30, default);
            Step(default, new HandInput { SculptToggle = true });
            Run(20, Hold);
            Assert.That(_shots, Is.Empty, "sculpt mode");
            Step(default, new HandInput { SculptToggle = true });
            Run(30, default);
            HandRules.Disarm(ref _s.Hand, _tick, 20);
            Run(20, Hold);
            Assert.That(_shots, Is.Empty, "disarmed");
        }

        [Test]
        public void SprintBlocksFireThenSprintToAimDelay()
        {
            Equip(Weapon.Smg);
            var sprint = new MoveInput { MoveZ = 1, SprintHeld = true };
            Run(40, sprint);
            Assert.That(_s.Sprinting, Is.True);
            var fireWhileSprint = new MoveInput { MoveZ = 1, SprintHeld = true, FireHeld = true, WantsToShoot = true };
            int start = _tick + 1;
            for (int i = 0; i < 20 && _shots.Count == 0; i++) Step(fireWhileSprint);
            Assert.That(_shots[0].Tick - start, Is.EqualTo(_mc.SprintToAimTicks), "0.15 s sprint-to-aim");
        }

        // ---------------------------------------------------------------- Accuracy states

        [Test]
        public void ArFirstShotIsExactThenSpreadBuilds()
        {
            Equip(Weapon.Ar);
            Run(10, default); // the small swap penalty (0.4 deg) recovers in a few ticks once the gun is out
            Run(6, Hold);
            Assert.That(_shots[0].Spread, Is.EqualTo(0), "first shot accurate standing still");
            WeaponStats ar = _mc.Weapons.Of(Weapon.Ar);
            Assert.That(WeaponRules.PelletDir(_shots[0], ar, 0).Equals(_shots[0].Aim), Is.True);
            Run(60, Hold);
            Assert.That(_shots[_shots.Count - 1].Spread, Is.GreaterThan(1.5), "sustained fire blooms");
            Run(60, default);
            Assert.That(_s.Weapon.Bloom, Is.EqualTo(0), "recovers when you stop");
        }

        [Test]
        public void ArIsStronglyPenalisedInAirAndSliding()
        {
            WeaponStats ar = _mc.Weapons.Of(Weapon.Ar);
            var s = _s;
            s.Mode = MoveMode.Air;
            Assert.That(WeaponRules.Spread(s, _mc, ar), Is.GreaterThanOrEqualTo(4));
            s.Mode = MoveMode.Slide;
            Assert.That(WeaponRules.Spread(s, _mc, ar), Is.GreaterThanOrEqualTo(3));
            WeaponStats pump = _mc.Weapons.Of(Weapon.Pump);
            s.Mode = MoveMode.Air;
            Assert.That(WeaponRules.Spread(s, _mc, pump), Is.LessThan(pump.PatternSpread * 0.5), "pump fairly accurate in air");
        }

        [Test]
        public void CrouchingTightensAndMovingWidens()
        {
            WeaponStats smg = _mc.Weapons.Of(Weapon.Smg);
            var s = _s;
            double still = WeaponRules.Spread(s, _mc, smg);
            s.Crouched = true;
            Assert.That(WeaponRules.Spread(s, _mc, smg), Is.LessThan(still));
            s.Crouched = false;
            s.Velocity = new Vec3d(_mc.RunSpeed, 0, 0);
            Assert.That(WeaponRules.Spread(s, _mc, smg), Is.EqualTo(still + smg.MoveSpread).Within(1e-12));
        }

        [Test]
        public void RecoilLiftsTheAimAndRecovers()
        {
            Equip(Weapon.Ar);
            Run(40, Hold);
            Assert.That(_s.Weapon.RecoilPitch, Is.GreaterThan(1));
            Assert.That(_shots[1].Aim.Y, Is.GreaterThan(_shots[0].Aim.Y), "second shot goes higher");
            Run(90, default);
            Assert.That(_s.Weapon.RecoilPitch, Is.EqualTo(0));
            Assert.That(_s.Weapon.RecoilYaw, Is.EqualTo(0));
        }

        [Test]
        public void PelletsAreDeterministicAndPumpPatternIsFixed()
        {
            WeaponStats pump = _mc.Weapons.Of(Weapon.Pump);
            var shot = new WeaponShot { Aim = new Vec3d(0, 0, 1), Spread = 0, Seed = 7 };
            var dirs = new List<Vec3d>();
            for (int i = 0; i < pump.Pellets; i++) dirs.Add(WeaponRules.PelletDir(shot, pump, i));
            for (int i = 0; i < dirs.Count; i++)
            for (int j = i + 1; j < dirs.Count; j++)
                Assert.That(dirs[i].Equals(dirs[j]), Is.False, "pellets spread out");
            double maxDeg = 0;
            foreach (Vec3d d in dirs) maxDeg = Math.Max(maxDeg, Math.Acos(Math.Min(1, d.Z)) * 180 / Math.PI);
            Assert.That(maxDeg, Is.LessThanOrEqualTo(pump.PatternSpread * 1.07).And.GreaterThan(pump.PatternSpread));
            var shot2 = shot; shot2.Seed = 99;
            for (int i = 0; i < pump.Pellets; i++) Assert.That(WeaponRules.PelletDir(shot2, pump, i), Is.EqualTo(dirs[i]), "no random part standing still");
            shot.Spread = 1; shot2.Spread = 1;
            Assert.That(WeaponRules.PelletDir(shot, pump, 3), Is.EqualTo(WeaponRules.PelletDir(shot, pump, 3)));
            Assert.That(WeaponRules.PelletDir(shot, pump, 3), Is.Not.EqualTo(WeaponRules.PelletDir(shot2, pump, 3)));
        }

        [Test]
        public void RandomSpreadStaysInsideItsCone()
        {
            WeaponStats smg = _mc.Weapons.Of(Weapon.Smg);
            for (uint seed = 0; seed < 500; seed++)
            {
                var shot = new WeaponShot { Aim = WeaponRules.AimDir(0.7, 20, 0, 0), Spread = 2.0, Seed = seed };
                Vec3d d = WeaponRules.PelletDir(shot, smg, 0);
                double deg = Math.Acos(Math.Min(1, d.Dot(shot.Aim))) * 180 / Math.PI;
                Assert.That(deg, Is.LessThanOrEqualTo(2.0 * 1.001));
            }
        }

        [Test]
        public void AimDirMatchesViewConvention()
        {
            Vec3d north = WeaponRules.AimDir(0, 0, 0, 0);
            Assert.That(north.Z, Is.EqualTo(1).Within(1e-12));
            Vec3d east = WeaponRules.AimDir(Math.PI / 2, 0, 0, 0);
            Assert.That(east.X, Is.EqualTo(1).Within(1e-12));
            Vec3d down = WeaponRules.AimDir(0, 45, 0, 0);
            Assert.That(down.Y, Is.LessThan(0), "positive pitch looks down");
            Vec3d kicked = WeaponRules.AimDir(0, 0, 5, 0);
            Assert.That(kicked.Y, Is.GreaterThan(0), "recoil kicks up");
        }

        // ---------------------------------------------------------------- Health, fall damage, death

        [Test]
        public void FallDamageIsPredictedAndCanKill()
        {
            _s.Position = new Vec3d(_s.Position.X, 8.5 * B, _s.Position.Z); // 8.5 levels up: 25 damage
            _s.Mode = MoveMode.Air;
            _s.AirPeakY = _s.Position.Y;
            Run(120, default);
            Assert.That(_s.Damage, Is.EqualTo(25));
            Assert.That(_s.Dead, Is.False);

            _s.Damage = 140;
            _s.Position = new Vec3d(_s.Position.X, 8.5 * B, _s.Position.Z);
            _s.Mode = MoveMode.Air;
            _s.AirPeakY = _s.Position.Y;
            _events.Clear();
            Run(120, default);
            Assert.That(_s.Dead, Is.True);
            Assert.That(_events.Exists(e => e.Type == MoveEventType.Died), Is.True);
        }

        [Test]
        public void DeadPlayersAreFrozenThenRespawnFull()
        {
            Equip(Weapon.Smg);
            Assert.That(WeaponRules.ApplyDamage(ref _s, 149, _tick, _mc.Weapons), Is.False);
            Assert.That(_s.Health(_mc.Weapons), Is.EqualTo(1));
            Assert.That(WeaponRules.ApplyDamage(ref _s, 30, _tick, _mc.Weapons), Is.True);
            Vec3d where = _s.Position;
            int died = _tick;
            for (int i = 0; i < _mc.Weapons.RespawnDelayTicks - 1; i++) Step(new MoveInput { MoveZ = 1, FireHeld = true });
            Assert.That(_s.Dead, Is.True);
            Assert.That(_s.Position, Is.EqualTo(where), "frozen where they fell");
            Assert.That(_shots, Is.Empty, "the dead don't shoot");
            _events.Clear();
            Step(default);
            Assert.That(_tick, Is.EqualTo(died + _mc.Weapons.RespawnDelayTicks));
            Assert.That(_s.Dead, Is.False);
            Assert.That(_s.Damage, Is.EqualTo(0));
            Assert.That(_s.Position, Is.EqualTo(_spawn));
            Assert.That(_events.Exists(e => e.Type == MoveEventType.Respawned), Is.True);
        }

        [Test]
        public void MotorStateWithGunsRoundTripsExactly()
        {
            Equip(Weapon.Ar);
            Run(17, Hold);
            _s.Damage = 37;
            var w = new ByteWriter();
            Protocol.WriteMotor(w, _s);
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            MotorState back = Protocol.ReadMotor(ref r);
            Assert.That(Protocol.SameMotor(back, _s), Is.True);
            back.Weapon.Bloom += 1e-9;
            Assert.That(Protocol.SameMotor(back, _s), Is.False, "gun state is part of reconciliation");
        }
    }
}
