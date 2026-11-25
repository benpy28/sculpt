using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The greybox test block (step 6): clean geometry you can actually move through at speed.</summary>
    public class GreyboxTests
    {
        private static readonly double North = 0, East = Math.PI / 2, South = Math.PI, West = -Math.PI / 2;
        private BrushWorld _w;
        private BreachMapInfo _info;
        private ITerrain _t;
        private MovementConfig _c;

        [SetUp]
        public void SetUp()
        {
            _w = BreachMap.BuildBrushes(out _info);
            _t = BreachMap.Terrain(BreachMap.BuildGround(), _w);
            _c = new MovementConfig();
        }

        private GreyboxInfo B => _info.Block;

        private static double YawOf(Dir d) => d == Dir.North ? North : d == Dir.East ? East : d == Dir.South ? South : West;

        /// <summary>Holds forward (sprinting) for <paramref name="ticks"/> from feet <paramref name="at"/>, facing <paramref name="yaw"/>.</summary>
        private MotorState Run(Vec3d at, double yaw, int ticks, bool sprint = true, Func<int, MoveInput, MoveInput> tweak = null)
        {
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 30; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = yaw }); // settle
            for (int k = 0; k < ticks; k++)
            {
                var i = new MoveInput { Yaw = yaw, MoveZ = 1, SprintHeld = sprint };
                if (tweak != null) i = tweak(k, i);
                PlayerMovement.Step(_t, _c, ref s, i);
            }
            return s;
        }

        [Test]
        public void ThreeTwoStoreyBuildings_OnTheLattice_InsideTheMap_TileIdsFitTheWire()
        {
            Assert.That(B.Buildings.Count, Is.EqualTo(3));
            foreach (Aabb b in B.Buildings)
            {
                Assert.That(b.Max.Y, Is.EqualTo(2 * GreyboxBlock.StoreyHeight).Within(1e-9), "two storeys, roof at 9.6 m");
                Assert.That(b.Min.X, Is.GreaterThan(0));
                Assert.That(b.Max.X, Is.LessThan(_info.SizeX));
                Assert.That(b.Max.Z, Is.LessThan(_info.SizeZ));
            }
            Assert.That(_w.Tiles.Count, Is.LessThan(ushort.MaxValue));
            Assert.That(B.Tiles.Count, Is.GreaterThan(500));
            foreach (int t in B.Tiles) Assert.That(_w.Tiles[t].State, Is.EqualTo(TileState.Intact));
            Assert.That(_w.Tiles.Changes, Is.Empty);
        }

        [Test]
        public void NoTileOverlapsAnythingSolid_AndNoClimbCutsIntoAWallFrameOrSlab()
        {
            var near = new List<int>();
            for (int t = 0; t < _w.Tiles.Count; t++)
            {
                Aabb box = _w.Tiles.BoundsOf(t);
                _w.BrushesNear(box, near);
                int self = _w.Tiles[t].Brush;
                foreach (int o in near)
                {
                    if (o == self || !_w[o].Solid || _w[o].Kind == BrushKind.Marker) continue;
                    Assert.That(BrushWorld.Overlaps(_w[o], box.Expand(-1e-6)), Is.False, $"tile {t} at {box.Min} overlaps a {_w[o].Kind} at {_w[o].Bounds.Min}");
                }
            }
            for (int ci = 0; ci < B.Climbs.Count; ci++)
            {
                Brush s = B.Climbs[ci];
                var own = new HashSet<int>();
                foreach (int t in B.ClimbTiles[ci]) own.Add(_w.Tiles[t].Brush);
                _w.BrushesNear(s.Bounds, near);
                foreach (int o in near)
                {
                    if (own.Contains(o) || !_w[o].Solid || _w[o].Kind == BrushKind.Marker) continue;
                    // Wedge vs box: probe the wedge's solid with small boxes under its slope.
                    Brush other = _w[o];
                    for (double u = 0.05; u < 1; u += 0.1)
                        for (double v = 0.1; v < 1; v += 0.2)
                        {
                            Vec3d p = Lerp(s, u, v, 0.02);
                            var probe = new Aabb(p - new Vec3d(0.04, 0, 0.04), p + new Vec3d(0.04, 0.04, 0.04));
                            if (!BrushWorld.Overlaps(s, probe)) continue;
                            Assert.That(BrushWorld.Overlaps(other, probe), Is.False, $"{s.Kind} at {s.Bounds.Min} cuts into a {other.Kind} at {other.Bounds.Min}");
                        }
                }
            }
        }

        /// <summary>A point on the slope of a wedge: u along the rise, v across, just below the surface by <paramref name="below"/>.</summary>
        private static Vec3d Lerp(in Brush b, double u, double v, double below)
        {
            Aabb x = b.Bounds;
            double y = x.Min.Y + (x.Max.Y - x.Min.Y) * u - below;
            switch (b.Rise)
            {
                case Dir.North: return new Vec3d(x.Min.X + (x.Max.X - x.Min.X) * v, y, x.Min.Z + (x.Max.Z - x.Min.Z) * u);
                case Dir.South: return new Vec3d(x.Min.X + (x.Max.X - x.Min.X) * v, y, x.Max.Z - (x.Max.Z - x.Min.Z) * u);
                case Dir.East: return new Vec3d(x.Min.X + (x.Max.X - x.Min.X) * u, y, x.Min.Z + (x.Max.Z - x.Min.Z) * v);
                default: return new Vec3d(x.Max.X - (x.Max.X - x.Min.X) * u, y, x.Min.Z + (x.Max.Z - x.Min.Z) * v);
            }
        }

        [Test]
        public void EveryStairAndRampHasStandingHeadroomAllTheWay()
        {
            foreach (Brush s in B.Climbs)
            {
                double len = Math.Max(s.Bounds.Max.X - s.Bounds.Min.X, s.Bounds.Max.Z - s.Bounds.Min.Z);
                for (double u = 0.02; u <= 0.98; u += 0.04)
                {
                    Vec3d p = Lerp(s, u, 0.5, 0);
                    // Feet just above the slope under the box's uphill edge (a box on a slope touches it there).
                    double feet = Lerp(s, Math.Min(1, u + (_c.HalfWidth + 0.01) / len), 0.5, -0.02).Y;
                    p = new Vec3d(p.X, feet, p.Z);
                    var player = new Aabb(p - new Vec3d(_c.HalfWidth, 0, _c.HalfWidth), p + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
                    Assert.That(_t.BoxOverlapsSolid(player), Is.False, $"no headroom on the {s.Kind} at {s.Bounds.Min}, {u:0.00} of the way up");
                }
            }
        }

        [Test]
        public void EveryFlightAndTheRoofRamp_CanBeRunUp()
        {
            Assert.That(B.Climbs.Count, Is.EqualTo(7), "two flights in each of three stairwells, and the ramp to C's roof");
            foreach (Brush s in B.Climbs)
            {
                Vec3d start = Lerp(s, 0, 0.5, -0.02) - DirVec(s.Rise) * 1.0; // a step before the bottom
                start = new Vec3d(start.X, s.Bounds.Min.Y, start.Z);
                double run = Math.Max(s.Bounds.Max.X - s.Bounds.Min.X, s.Bounds.Max.Z - s.Bounds.Min.Z);
                int ticks = (int)Math.Ceiling((run + 2) / _c.SprintSpeed * SimTime.TicksPerSecond) + 30 * SimTime.Per60; // to the top and a few steps on
                MotorState end = Run(start, YawOf(s.Rise), ticks);
                Assert.That(end.Position.Y, Is.EqualTo(s.Bounds.Max.Y).Within(0.05), $"ran to the top of the {s.Kind} at {s.Bounds.Min} (ended at {end.Position})");
                Assert.That(end.Mode, Is.Not.EqualTo(MoveMode.Air), "and stood on the floor it leads to");
            }
        }

        private static Vec3d DirVec(Dir d) => d == Dir.North ? new Vec3d(0, 0, 1) : d == Dir.South ? new Vec3d(0, 0, -1) : d == Dir.East ? new Vec3d(1, 0, 0) : new Vec3d(-1, 0, 0);

        [Test]
        public void StairsKeepYourSpeed_SprintingUpAFlightIsNotSlowedByBumps()
        {
            Brush s = B.Climbs[0];
            Vec3d start = new Vec3d((s.Bounds.Min.X + s.Bounds.Max.X) / 2, 0, s.Bounds.Min.Z - 6);
            double minSpeed = double.MaxValue;
            MotorState st = MotorState.At(start);
            st.Tick = 1000;
            for (int k = 0; k < 150; k++)
            {
                PlayerMovement.Step(_t, _c, ref st, new MoveInput { Yaw = North, MoveZ = 1, SprintHeld = true });
                if (k > 60 && st.Position.Z > s.Bounds.Min.Z + 0.5 && st.Position.Z < s.Bounds.Max.Z - 0.5) minSpeed = Math.Min(minSpeed, st.HorizontalSpeed);
            }
            Assert.That(minSpeed, Is.GreaterThan(_c.SprintSpeed * 0.8), "smooth as a ramp, no step bumps");
        }

        [Test]
        public void EveryDoorwayIsClearForAStandingPlayer()
        {
            Assert.That(B.Doorways.Count, Is.GreaterThan(15));
            foreach (Aabb d in B.Doorways)
            {
                Assert.That(Math.Max(d.Max.X - d.Min.X, d.Max.Z - d.Min.Z), Is.GreaterThanOrEqualTo(3.0 - 1e-9), "at least two tiles wide");
                Assert.That(d.Max.Y - d.Min.Y, Is.EqualTo(3.0).Within(1e-9), "two tiles high");
                Vec3d c = (d.Min + d.Max) * 0.5;
                var player = new Aabb(new Vec3d(c.X - _c.HalfWidth, d.Min.Y + 0.01, c.Z - _c.HalfWidth), new Vec3d(c.X + _c.HalfWidth, d.Min.Y + _c.StandHeight, c.Z + _c.HalfWidth));
                Assert.That(_w.BoxOverlapsSolid(player), Is.False, $"doorway at {d.Min} is blocked");
            }
        }

        [Test]
        public void YouCanSprintInThroughTheHallsMainDoor_AndDownTheLongCorridor()
        {
            Aabb hall = B.Buildings[0];
            // Hall A's south wide door is segment 1 of its south wall (x 98.6..104.6).
            MotorState s = Run(new Vec3d(101.6, 0, hall.Min.Z - 4), North, 90);
            Assert.That(s.Position.Z, Is.GreaterThan(hall.Min.Z + 3), "inside the hall");

            Aabb longBlock = B.Buildings[2];
            double corridorZ = longBlock.Min.Z + 0.3 + 3.0;
            MotorState c = Run(new Vec3d(longBlock.Min.X - 4, 0, corridorZ), East, SimTime.TicksPerSecond * 7);
            Assert.That(c.Position.X, Is.GreaterThan(longBlock.Max.X), "straight through the 38 m corridor and out the far wide door");
        }

        [Test]
        public void RooftopBridgesLinkTheRoofs()
        {
            Assert.That(B.Bridges.Count, Is.EqualTo(4), "A-C, A-B, C-B, and the roof ramp's top across to B");
            Aabb a = B.Buildings[0], c = B.Buildings[1];
            Aabb ac = B.Bridges[0];
            double z = (ac.Min.Z + ac.Max.Z) / 2;
            MotorState s = Run(new Vec3d(a.Max.X - 3, a.Max.Y, z), East, 90);
            Assert.That(s.Position.X, Is.GreaterThan(c.Min.X + 2), "across to C's roof");
            Assert.That(s.Position.Y, Is.EqualTo(c.Max.Y).Within(1e-6));
        }

        [Test]
        public void Hills_SlidingDownTheEastSlopeBuildsSpeed()
        {
            Brush slope = _w[B.Hills[B.Hills.Count - 2]];
            Assert.That(slope.IsRamp, Is.True);
            double z = (slope.Bounds.Min.Z + slope.Bounds.Max.Z) / 2;
            // Sprint west off the top, slide down.
            MotorState s = Run(new Vec3d(slope.Bounds.Max.X + 3, slope.Bounds.Max.Y, z), West, 120,
                tweak: (k, i) => { if (k == 30) i.CrouchPressed = true; if (k >= 30) i.CrouchHeld = true; return i; });
            Assert.That(s.HorizontalSpeed, Is.GreaterThan(_c.SprintSpeed), "downhill slides gain speed");
        }

        [Test]
        public void SpawnsAreClearAndOutsideTheBuildings()
        {
            foreach (var team in _info.TeamSpawns)
                foreach (Vec3d p in team)
                {
                    var box = new Aabb(p + new Vec3d(-_c.HalfWidth, 0.01, -_c.HalfWidth), p + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
                    Assert.That(_t.BoxOverlapsSolid(box), Is.False);
                    foreach (Aabb b in B.Buildings) Assert.That(b.Contains(p, 0), Is.False);
                }
        }
    }
}
