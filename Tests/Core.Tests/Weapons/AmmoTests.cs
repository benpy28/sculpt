using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Weapons
{
    /// <summary>Ammo, reloads and per-gun bloom (Ben 2026-11-20), stepped through the shared simulation.</summary>
    public class AmmoTests
    {
        private const double B = SubGrid.TileSize;
        private MovementConfig _mc;
        private NetConfig _nc;
        private SubGrid _grid;
        private MotorState _s;
        private int _tick;
        private List<WeaponShot> _shots;
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
            Run(30 * SimTime.Per60, default);
            _shots.Clear();
        }

        private void Step(MoveInput m, HandInput hand = default)
        {
            _tick++;
            var cmd = InputCmd.Quantize(new InputCmd { Tick = _tick, Move = m, Hand = hand });
            NetSim.Simulate(_grid, _mc, _nc, ref _s, cmd, _spawn, null, _shots);
        }

        private void Run(int ticks, MoveInput m) { for (int i = 0; i < ticks; i++) Step(m); }

        private static MoveInput Hold => new MoveInput { FireHeld = true, WantsToShoot = true };
        private static HandInput Key(Weapon w) => new HandInput { WeaponKey = (byte)((int)w + 1) };
        private static HandInput ReloadKey => new HandInput { Reload = true };

        private void Equip(Weapon w)
        {
            if (_s.Hand.Held != w) Step(default, Key(w));
            Run(_mc.Hand.Draw(w) + 30, default);
            Assert.That(_s.Hand.Held, Is.EqualTo(w));
            Assert.That(_s.Hand.CanFire, Is.True);
            _shots.Clear();
        }

        private WeaponStats Of(Weapon w) => _mc.Weapons.Of(w);
        private int Ammo(Weapon w) => _s.Weapon.AmmoOf(w, _mc.Weapons);

        [Test]
        public void SprayingEmptiesTheMagazineThenItReloadsByItself()
        {
            Equip(Weapon.Smg);
            WeaponStats st = Of(Weapon.Smg);
            Run((st.MagSize * 6 + 10) * SimTime.Per60, Hold); // far more than a magazine's worth of ticks
            int fired = _shots.Count;
            Assert.That(fired, Is.EqualTo(st.MagSize), "a magazine and no more");
            Assert.That(_s.Weapon.Reloading, Is.True, "an empty gun starts reloading on its own");
            Run(st.ReloadTicks + 2, default);
            Assert.That(Ammo(Weapon.Smg), Is.EqualTo(st.MagSize), "full after the reload time");
        }

        [Test]
        public void MagazineReloadTakesItsTimeAndBlocksFire()
        {
            Equip(Weapon.Ar);
            WeaponStats st = Of(Weapon.Ar);
            Step(Hold);
            Run(20 * SimTime.Per60, default);
            Assert.That(Ammo(Weapon.Ar), Is.EqualTo(st.MagSize - 1));
            Step(default, ReloadKey);
            Assert.That(_s.Weapon.Reloading, Is.True);
            _shots.Clear();
            Run(st.ReloadTicks - 2, Hold);
            Assert.That(_shots, Is.Empty, "can't fire through a magazine reload");
            Assert.That(Ammo(Weapon.Ar), Is.EqualTo(st.MagSize - 1), "not in yet");
            Run(3 * SimTime.Per60, default);
            Assert.That(Ammo(Weapon.Ar), Is.EqualTo(st.MagSize));
        }

        [Test]
        public void ReloadPressOnAFullMagazineDoesNothing()
        {
            Equip(Weapon.Pistol);
            Step(default, ReloadKey);
            Assert.That(_s.Weapon.Reloading, Is.False);
        }

        [Test]
        public void SwappingCancelsAMagazineReloadAndLosesItsProgress()
        {
            Equip(Weapon.Ar);
            Step(Hold);
            Run(20 * SimTime.Per60, default);
            Step(default, ReloadKey);
            Run(Of(Weapon.Ar).ReloadTicks - 10, default);
            Equip(Weapon.Pistol);
            Assert.That(_s.Weapon.Reloading, Is.False);
            Assert.That(Ammo(Weapon.Ar), Is.EqualTo(Of(Weapon.Ar).MagSize - 1), "the swap threw the reload away");
        }

        [Test]
        public void SculptModeCancelsAReload()
        {
            Equip(Weapon.Smg);
            Step(Hold);
            Run(10 * SimTime.Per60, default);
            Step(default, ReloadKey);
            Step(default, new HandInput { SculptToggle = true });
            Step(default);
            Assert.That(_s.Weapon.Reloading, Is.False);
        }

        [Test]
        public void PumpReloadsShellByShellAndFiringInterruptsIt()
        {
            WeaponStats st = Of(Weapon.Pump);
            Equip(Weapon.Pump);
            for (int i = 0; i < 3; i++) { Step(Hold); Run(60 * SimTime.Per60, default); }
            Assert.That(_shots.Count, Is.EqualTo(3));
            Assert.That(Ammo(Weapon.Pump), Is.EqualTo(st.MagSize - 3));
            Step(default, ReloadKey);
            Run(st.ShellStartTicks + st.ShellTicks + 1, default);
            Assert.That(Ammo(Weapon.Pump), Is.EqualTo(st.MagSize - 2), "one shell in");
            Assert.That(_s.Weapon.Reloading, Is.True, "still loading");
            _shots.Clear();
            Step(Hold);
            Assert.That(_shots.Count, Is.EqualTo(1), "a loaded shell fires through the reload");
            Assert.That(_s.Weapon.Reloading, Is.False, "and stops it");
            Assert.That(Ammo(Weapon.Pump), Is.EqualTo(st.MagSize - 3));
        }

        [Test]
        public void EmptyPumpLoadsEveryShell()
        {
            WeaponStats st = Of(Weapon.Pump);
            Equip(Weapon.Pump);
            for (int i = 0; i < st.MagSize; i++) { Step(Hold); Run(56 * SimTime.Per60, default); }
            Assert.That(_shots.Count, Is.EqualTo(st.MagSize));
            Assert.That(_s.Weapon.Reloading, Is.True, "empty: loading by itself");
            Run(st.ShellStartTicks + st.ShellTicks * st.MagSize + 5, default);
            Assert.That(Ammo(Weapon.Pump), Is.EqualTo(st.MagSize));
            Assert.That(_s.Weapon.Reloading, Is.False);
        }

        [Test]
        public void EachGunKeepsItsOwnMagazine()
        {
            Equip(Weapon.Smg);
            Run(10 * SimTime.Per60, Hold);
            int smgLeft = Ammo(Weapon.Smg);
            Equip(Weapon.Ar);
            Assert.That(Ammo(Weapon.Ar), Is.EqualTo(Of(Weapon.Ar).MagSize));
            Equip(Weapon.Smg);
            Assert.That(Ammo(Weapon.Smg), Is.EqualTo(smgLeft));
        }

        [Test]
        public void RespawnRefillsEveryMagazine()
        {
            Equip(Weapon.Smg);
            Run(10 * SimTime.Per60, Hold);
            Step(default, default);
            _tick++;
            NetSim.Simulate(_grid, _mc, _nc, ref _s, InputCmd.Quantize(new InputCmd { Tick = _tick, Respawn = true }), _spawn, null, _shots);
            foreach (Weapon w in new[] { Weapon.Pump, Weapon.Smg, Weapon.Ar, Weapon.Pistol })
                Assert.That(Ammo(w), Is.EqualTo(Of(w).MagSize));
        }

        [Test]
        public void BloomIsPerGunWithASmallSwapPenalty()
        {
            Equip(Weapon.Ar);
            Run(40 * SimTime.Per60, Hold); // spray: bloom builds up
            Assert.That(_s.Weapon.Bloom, Is.GreaterThan(1.0));
            Step(default, Key(Weapon.Pump));
            Assert.That(_s.Weapon.Bloom, Is.EqualTo(Of(Weapon.Pump).SwapBloom), "the pump doesn't inherit the AR's spray");
            Run(_mc.Hand.Draw(Weapon.Pump) - 2, default);
            Assert.That(_s.Weapon.Bloom, Is.EqualTo(Of(Weapon.Pump).SwapBloom), "the penalty waits for the draw");
            _shots.Clear();
            Run(3 * SimTime.Per60, Hold);
            Assert.That(_shots.Count, Is.EqualTo(1));
            Assert.That(_shots[0].Spread, Is.LessThanOrEqualTo(Of(Weapon.Pump).SwapBloom + 1e-9), "first shot after a swap carries at most the small penalty");
        }

        [Test]
        public void SmgHeadshotIs150Percent()
        {
            // Balance pass 2 (2026-11-26): 1.5x, was 1.25x (Ben 2026-11-20). Eight headshots kill, twelve body shots.
            WeaponStats smg = Of(Weapon.Smg);
            Assert.That(smg.HeadDamage / smg.BodyDamage, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(Math.Ceiling(150 / smg.HeadDamage), Is.EqualTo(8));
            Assert.That(Math.Ceiling(150 / smg.BodyDamage), Is.EqualTo(12));
        }

        [Test]
        public void AmmoAndReloadSurviveTheWire()
        {
            Equip(Weapon.Ar);
            Run(5 * SimTime.Per60, Hold);
            Step(default, ReloadKey);
            var w = new ByteWriter();
            Protocol.WriteMotor(w, _s);
            byte[] bytes = w.ToArray();
            var r = new ByteReader(bytes, 0, bytes.Length);
            MotorState back = Protocol.ReadMotor(ref r);
            Assert.That(back.Weapon.Equals(_s.Weapon), Is.True);
        }
    }
}
