using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests.Weapons
{
    public class HitscanTests
    {
        private const double B = SubGrid.TileSize;
        private WeaponsConfig _cfg;
        private SubGrid _grid;

        [SetUp]
        public void SetUp()
        {
            _cfg = new WeaponsConfig();
            _grid = new SubGrid(4, 4, 4, -16);
            _grid.FillBaseGround();
            // A 3-level wall across x at tile z 10 (z 12.0..13.2 m), tiles x 0..15.
            new TileMap(_grid).Fill(new Int3(0, 0, 10), new Int3(15, 2, 10), Tile.Solid);
        }

        private HitTarget Target(int id, int team, double x, double z, double height = 1.8) =>
            HitTarget.Of(id, team, new Vec3d(x, 0, z), 0.35, height, _cfg);

        [Test]
        public void RayBoxHitsNearFaceWithNormal()
        {
            var box = new Aabb(new Vec3d(-1, -1, 4), new Vec3d(1, 1, 6));
            Assert.That(Hitscan.RayBox(Vec3d.Zero, new Vec3d(0, 0, 1), box, out double d, out Vec3d n), Is.True);
            Assert.That(d, Is.EqualTo(4).Within(1e-12));
            Assert.That(n, Is.EqualTo(new Vec3d(0, 0, -1)));
            Assert.That(Hitscan.RayBox(Vec3d.Zero, new Vec3d(0, 0, -1), box, out _, out _), Is.False, "behind");
            Assert.That(Hitscan.RayBox(new Vec3d(2, 0, 0), new Vec3d(0, 0, 1), box, out _, out _), Is.False, "beside");
            Assert.That(Hitscan.RayBox(new Vec3d(0, 0, 5), new Vec3d(1, 0, 0), box, out d, out _), Is.True, "from inside");
            Assert.That(d, Is.EqualTo(0));
        }

        [Test]
        public void HeadZoneIsTheTopOfTheBoxAsDrawn()
        {
            var targets = new List<HitTarget> { Target(2, 2, 5, 5) };
            Vec3d o = new Vec3d(5, 1.7, 1);
            BulletHit h = Hitscan.Trace(_grid, o, new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player));
            Assert.That(h.Head, Is.True, "1.7 m is in the top 0.33 m");
            h = Hitscan.Trace(_grid, new Vec3d(5, 1.40, 1), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Head, Is.False, "1.40 m is body");
            Assert.That(_cfg.HeadZone(1.1), Is.EqualTo(0.33).Within(1e-12), "crouched head is 30% of the box");
        }

        [Test]
        public void HeadIsNarrowerThanTheBody_ShotsPastItsSidesMiss()
        {
            // Ben 2026-11-25: head a little too big. Head 0.6 m wide on a 0.7 m body, 0.33 m tall: the top 5 cm either
            // side of the head is open air (the drawn shoulders are below it), and the head's sides are exact.
            Assert.That(_cfg.HeadHeight, Is.EqualTo(0.33));
            Assert.That(_cfg.HeadWidth, Is.EqualTo(0.6));
            var targets = new List<HitTarget> { Target(2, 2, 5, 5) }; // feet at (5, 0, 5), box x 4.65..5.35
            BulletHit h = Hitscan.Trace(_grid, new Vec3d(5.29, 1.7, 1), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player));
            Assert.That(h.Head, Is.True, "just inside the head's side");
            Assert.That(h.Point.Z, Is.EqualTo(5 - 0.3).Within(1e-9), "hits the head's own face, not the body box's");
            h = Hitscan.Trace(_grid, new Vec3d(5.31, 1.7, 1), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.Not.EqualTo(HitKind.Player), "past the head's side at head height: a miss");
            h = Hitscan.Trace(_grid, new Vec3d(5.31, 1.45, 1), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player));
            Assert.That(h.Head, Is.False, "shoulder height is body at full width");
            // From above: a bullet coming down past the head onto the shoulder hits the body's top.
            h = Hitscan.Trace(_grid, new Vec3d(5.33, 3, 5), new Vec3d(0, -1, 0), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Player));
            Assert.That(h.Head, Is.False);
            Assert.That(h.Point.Y, Is.EqualTo(1.8 - 0.33).Within(1e-9));
            // From the side, along x: the head is 0.6 deep too, so the side face of the head is 0.3 from the centre.
            h = Hitscan.Trace(_grid, new Vec3d(1, 1.7, 5), new Vec3d(1, 0, 0), 100, targets, 1, 1, false);
            Assert.That(h.Head, Is.True);
            Assert.That(h.Point.X, Is.EqualTo(5 - 0.3).Within(1e-9));
        }

        [Test]
        public void TerrainStopsBulletsCompletely()
        {
            var targets = new List<HitTarget> { Target(2, 2, 5, 16) }; // behind the wall
            BulletHit h = Hitscan.Trace(_grid, new Vec3d(5, 1.6, 5), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(h.Kind, Is.EqualTo(HitKind.Terrain));
            Assert.That(h.Point.Z, Is.EqualTo(12.0).Within(1e-6), "stops on the wall face");
        }

        [Test]
        public void NearestPlayerWinsAndTeammatesPassThrough()
        {
            var targets = new List<HitTarget> { Target(3, 2, 5, 8), Target(2, 1, 5, 6) };
            BulletHit h = Hitscan.Trace(_grid, new Vec3d(5, 1.0, 2), new Vec3d(0, 0, 1), 100, targets, 1, 1, false);
            Assert.That(targets[h.Target].Id, Is.EqualTo(3), "teammate 2 is skipped");
            h = Hitscan.Trace(_grid, new Vec3d(5, 1.0, 2), new Vec3d(0, 0, 1), 100, targets, 1, 1, true);
            Assert.That(targets[h.Target].Id, Is.EqualTo(2), "with friendly fire the nearer teammate is hit");
            h = Hitscan.Trace(_grid, new Vec3d(5, 1.0, 2), new Vec3d(0, 0, 1), 100, new List<HitTarget> { Target(1, 1, 5, 2) }, 1, 1, true);
            Assert.That(h.Kind, Is.Not.EqualTo(HitKind.Player), "never hits the shooter");
        }

        [Test]
        public void PointBlankPumpBodyIs85AndAllHeadsIs127()
        {
            WeaponStats pump = _cfg.Of(Weapon.Pump);
            var victims = new List<ShotVictim>();
            var shot = new WeaponShot { ShooterId = 1, Origin = new Vec3d(5, 1.0, 3), Aim = new Vec3d(0, 0, 1), Seed = 1 };
            Hitscan.Resolve(_grid, shot, pump, _cfg, 1, new List<HitTarget> { Target(2, 2, 5, 4) }, victims);
            Assert.That(victims.Count, Is.EqualTo(1));
            Assert.That(victims[0].Pellets, Is.EqualTo(9));
            Assert.That(victims[0].Damage, Is.EqualTo(85), "9 x 9.4 = 84.6");
            shot.Origin = new Vec3d(5, 1.65, 3.3);
            Hitscan.Resolve(_grid, shot, pump, _cfg, 1, new List<HitTarget> { Target(2, 2, 5, 3.7) }, victims);
            Assert.That(victims[0].HeadPellets, Is.EqualTo(9));
            Assert.That(victims[0].Damage, Is.EqualTo(127), "9 x 14.1 = 126.9, rounded once: a pump never one-shots");
        }

        [Test]
        public void PumpFallsOffAtRange()
        {
            WeaponStats pump = _cfg.Of(Weapon.Pump);
            var victims = new List<ShotVictim>();
            var shot = new WeaponShot { ShooterId = 1, Origin = new Vec3d(5, 1.0, 0.5), Aim = new Vec3d(0, 0, 1) };
            var far = new List<HitTarget> { HitTarget.Of(2, 2, new Vec3d(5, 0, 11.0), 0.35, 1.8, _cfg) };
            Hitscan.Resolve(_grid, shot, pump, _cfg, 1, far, victims);
            Assert.That(victims.Count, Is.EqualTo(1));
            Assert.That(victims[0].Damage, Is.LessThan(victims[0].Pellets * 11 * 0.75), "about 10 m: well under full damage");
        }
    }
}
