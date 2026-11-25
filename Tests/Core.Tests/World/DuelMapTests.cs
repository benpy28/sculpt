using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The 1v1 duel map: clean geometry, and the same map from both spawns.</summary>
    public class DuelMapTests
    {
        private BrushWorld _w;
        private BreachMapInfo _info;
        private ITerrain _t;
        private MovementConfig _c;
        private double Size => _info.SizeX;
        private GreyboxInfo B => _info.Block;

        [SetUp]
        public void SetUp()
        {
            _w = DuelMap.BuildBrushes(out _info);
            _t = DuelMap.Terrain(DuelMap.BuildGround(), _w);
            _c = new MovementConfig();
        }

        private static double YawOf(Dir d) => d == Dir.North ? 0 : d == Dir.East ? Math.PI / 2 : d == Dir.South ? Math.PI : -Math.PI / 2;
        private static Vec3d DirVec(Dir d) => d == Dir.North ? new Vec3d(0, 0, 1) : d == Dir.South ? new Vec3d(0, 0, -1) : d == Dir.East ? new Vec3d(1, 0, 0) : new Vec3d(-1, 0, 0);

        /// <param name="climb">Jump when stopped by something (a ledge is climbed by jumping at it since the vault
        /// and mantle merge, Ben 2026-11-25), holding jump so an air touch climbs too.</param>
        private MotorState Run(Vec3d at, double yaw, int ticks, bool climb = false)
        {
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 30; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = yaw });
            bool held = false;
            for (int k = 0; k < ticks; k++)
            {
                bool jump = climb && k > 10 && (held || (s.Grounded && s.HorizontalSpeed < 2));
                PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = yaw, MoveZ = 1, SprintHeld = true, JumpPressed = jump && !held, JumpHeld = jump });
                held = jump && s.Mode != MoveMode.Ground;
            }
            return s;
        }

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
        public void ThreeBuildings_InsideTheMap_AllIntact_TileIdsFitTheWire()
        {
            Assert.That(B.Buildings.Count, Is.EqualTo(3));
            foreach (Aabb b in B.Buildings)
            {
                Assert.That(b.Min.X, Is.GreaterThan(3));
                Assert.That(b.Min.Z, Is.GreaterThan(3));
                Assert.That(b.Max.X, Is.LessThan(Size - 3));
                Assert.That(b.Max.Z, Is.LessThan(Size - 3));
            }
            Assert.That(_w.Tiles.Count, Is.LessThan(ushort.MaxValue));
            foreach (int t in B.Tiles) Assert.That(_w.Tiles[t].State, Is.EqualTo(TileState.Intact));
            Assert.That(_w.Tiles.Changes, Is.Empty);
        }

        [Test]
        public void TheMapIsTheSameFromBothSpawns_EveryBrushHasATurnedTwin()
        {
            var keys = new HashSet<string>();
            string Key(BrushKind k, Aabb b, Dir rise) => $"{k} {Math.Round(b.Min.X, 3)} {Math.Round(b.Min.Y, 3)} {Math.Round(b.Min.Z, 3)} {Math.Round(b.Max.X, 3)} {Math.Round(b.Max.Y, 3)} {Math.Round(b.Max.Z, 3)} {rise}";
            Dir Turn(Dir d) => d == Dir.North ? Dir.South : d == Dir.South ? Dir.North : d == Dir.East ? Dir.West : d == Dir.West ? Dir.East : d;
            for (int i = 0; i < _w.Count; i++) keys.Add(Key(_w[i].Kind, _w[i].Bounds, _w[i].IsRamp ? _w[i].Rise : Dir.Up));
            for (int i = 0; i < _w.Count; i++)
            {
                Brush b = _w[i];
                var turned = new Aabb(new Vec3d(Size - b.Bounds.Max.X, b.Bounds.Min.Y, Size - b.Bounds.Max.Z), new Vec3d(Size - b.Bounds.Min.X, b.Bounds.Max.Y, Size - b.Bounds.Min.Z));
                Assert.That(keys.Contains(Key(b.Kind, turned, b.IsRamp ? Turn(b.Rise) : Dir.Up)), Is.True, $"{b.Kind} at {b.Bounds.Min}..{b.Bounds.Max} has no twin");
            }
            Assert.That(_info.TeamSpawns[1][0], Is.EqualTo(DuelMap.Turn(_info.TeamSpawns[0][0], Size)));
        }

        [Test]
        public void NoTileOverlapsAnythingSolid()
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
        }

        [Test]
        public void EveryStairAndRampHasHeadroom_AndCanBeRunUp()
        {
            Assert.That(B.Climbs.Count, Is.EqualTo(10), "two flights in each of four stairwells, and two roof ramps");
            foreach (Brush s in B.Climbs)
            {
                double len = Math.Max(s.Bounds.Max.X - s.Bounds.Min.X, s.Bounds.Max.Z - s.Bounds.Min.Z);
                for (double u = 0.02; u <= 0.98; u += 0.04)
                {
                    Vec3d p = Lerp(s, u, 0.5, 0);
                    double feet = Lerp(s, Math.Min(1, u + (_c.HalfWidth + 0.01) / len), 0.5, -0.02).Y;
                    p = new Vec3d(p.X, feet, p.Z);
                    var player = new Aabb(p - new Vec3d(_c.HalfWidth, 0, _c.HalfWidth), p + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
                    Assert.That(_t.BoxOverlapsSolid(player), Is.False, $"no headroom on the {s.Kind} at {s.Bounds.Min}, {u:0.00} up");
                }
                Vec3d start = Lerp(s, 0, 0.5, -0.02) - DirVec(s.Rise) * 1.0;
                start = new Vec3d(start.X, s.Bounds.Min.Y, start.Z);
                // Stairs arrive on a floor; the roof ramps end on a 3 m walkway, so stop a step past the top.
                int ticks = s.Kind == BrushKind.Ramp ? (int)Math.Ceiling((len + 1.5) / _c.SprintSpeed * 60) : (int)Math.Ceiling((len + 2) / _c.SprintSpeed * 60) + 30;
                MotorState end = Run(start, YawOf(s.Rise), ticks);
                Assert.That(end.Position.Y, Is.EqualTo(s.Bounds.Max.Y).Within(0.05), $"ran up the {s.Kind} at {s.Bounds.Min} (ended at {end.Position})");
            }
        }

        [Test]
        public void EveryDoorwayIsClear()
        {
            Assert.That(B.Doorways.Count, Is.GreaterThan(10));
            foreach (Aabb d in B.Doorways)
            {
                Vec3d c = (d.Min + d.Max) * 0.5;
                var player = new Aabb(new Vec3d(c.X - _c.HalfWidth, d.Min.Y + 0.01, c.Z - _c.HalfWidth), new Vec3d(c.X + _c.HalfWidth, d.Min.Y + _c.StandHeight, c.Z + _c.HalfWidth));
                Assert.That(_w.BoxOverlapsSolid(player), Is.False, $"doorway at {d.Min} is blocked");
            }
        }

        [Test]
        public void FromSpawnYouCanSprintOverTheBermAndIntoTheHall()
        {
            Aabb hall = B.Buildings[0];
            double doorX = (hall.Min.X + hall.Max.X) / 2;
            MotorState s = Run(new Vec3d(doorX, 0, 4), 0, SimTime.TicksPerSecond * 6, climb: true);
            Assert.That(s.Position.Z, Is.GreaterThan(hall.Min.Z + 3), $"inside the hall (ended at {s.Position})");
        }

        [Test]
        public void BridgesLinkTheSideRoofsToTheHallRoof()
        {
            Assert.That(B.Bridges.Count, Is.EqualTo(4), "two to the hall, and a walkway from each roof ramp");
            Aabb west = B.Buildings[1], hall = B.Buildings[0];
            Aabb br = B.Bridges[2]; // after the two ramp walkways
            double z = (br.Min.Z + br.Max.Z) / 2;
            MotorState s = Run(new Vec3d(west.Max.X - 3, west.Max.Y, z), Math.PI / 2, 180);
            Assert.That(s.Position.X, Is.GreaterThan(hall.Min.X + 2), "across to the hall's roof");
            Assert.That(s.Position.Y, Is.EqualTo(hall.Max.Y).Within(1e-6));
        }

        [Test]
        public void SpawnsAndItemsAreClear()
        {
            foreach (var team in _info.TeamSpawns)
                foreach (Vec3d p in team)
                {
                    var box = new Aabb(p + new Vec3d(-_c.HalfWidth, 0.01, -_c.HalfWidth), p + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
                    Assert.That(_t.BoxOverlapsSolid(box), Is.False);
                }
            foreach (GroundItem g in _info.ItemSpawns)
                Assert.That(_t.BoxOverlapsSolid(new Aabb(g.Position + new Vec3d(-0.2, 0.01, -0.2), g.Position + new Vec3d(0.2, 0.3, 0.2))), Is.False);
        }
    }
}
