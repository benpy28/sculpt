using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>The battle royale map: clean geometry, the same spawn region everywhere, and Ben's spawn rules.</summary>
    public class BrMapTests
    {
        private static BrushWorld _w;
        private static BreachMapInfo _info;
        private static BrMapInfo _br;
        private static ITerrain _t;
        private MovementConfig _c;
        private GreyboxInfo B => _info.Block;

        [OneTimeSetUp]
        public void Build()
        {
            if (_w != null) return;
            _w = BrMap.BuildBrushes(out _info, out _br);
            _t = BrMap.Terrain(BrMap.BuildGround(), _w);
        }

        [SetUp]
        public void SetUp() => _c = new MovementConfig();

        private static string Key(BrushKind k, Aabb b, Dir rise) =>
            $"{k} {Math.Round(b.Min.X, 3)} {Math.Round(b.Min.Y, 3)} {Math.Round(b.Min.Z, 3)} {Math.Round(b.Max.X, 3)} {Math.Round(b.Max.Y, 3)} {Math.Round(b.Max.Z, 3)} {rise}";

        [Test]
        public void EightRegions_OnePerOuterSegment_AllIntact_TileIdsFitTheWire()
        {
            Assert.That(_br.Regions.Count, Is.EqualTo(8));
            var segs = new HashSet<int>();
            foreach (BrRegion r in _br.Regions)
            {
                Assert.That(r.Segment, Is.Not.EqualTo(BrLayout.Centre));
                Assert.That(segs.Add(r.Segment), Is.True, "one region per segment");
                Assert.That(BrLayout.Owner(r.Building.Min.X, r.Building.Min.Z), Is.EqualTo(r.Segment));
                Assert.That(BrLayout.Owner(r.Building.Max.X, r.Building.Max.Z), Is.EqualTo(r.Segment));
                Assert.That(r.Poi, Is.GreaterThanOrEqualTo(0), $"{r.Name} names its home POI");
            }
            Assert.That(_w.Tiles.Count, Is.LessThan(ByteWriter.MaxU24), "tile ids ride the wire in 24 bits");
            foreach (int t in B.Tiles) Assert.That(_w.Tiles[t].State, Is.EqualTo(TileState.Intact));
            Assert.That(_w.Tiles.Changes, Is.Empty);
            foreach (Aabb b in B.Buildings)
            {
                Assert.That(b.Min.X, Is.GreaterThan(10));
                Assert.That(b.Min.Z, Is.GreaterThan(10));
                Assert.That(b.Max.X, Is.LessThan(_br.Size - 10));
                Assert.That(b.Max.Z, Is.LessThan(_br.Size - 10));
                Assert.That(b.Max.Y, Is.LessThan(BrMap.NavTop - 5), "under the walking grid's top");
            }
            TestContext.WriteLine($"{_w.Count} brushes, {_w.Tiles.Count} tiles, {B.Buildings.Count} buildings, {_br.Chests.Count} chests, {_br.Pois.Count} POIs");
        }

        [Test]
        public void EverySpawnRegion_IsTheSameBuilding_TurnedOrMirrored()
        {
            // Each region's tiles, taken back to the template's frame and measured from its anchor, are the same set.
            double c = _br.Size / 2;
            HashSet<string> first = null;
            foreach (BrRegion r in _br.Regions)
            {
                int[] m = BrLayout.KitSymmetry(r.Segment);
                int[] inv = { m[0], m[2], m[1], m[3] }; // orthogonal: the inverse is the transpose
                BrLayout.TemplateAnchor(BrLayout.IsCorner(r.Segment), out double au, out double av);
                var set = new HashSet<string>();
                Aabb area = r.Building.Expand(0.01);
                for (int t = 0; t < _w.Tiles.Count; t++)
                {
                    Aabb box = _w.Tiles.BoundsOf(t);
                    if (box.Min.X < area.Min.X || box.Max.X > area.Max.X || box.Min.Z < area.Min.Z || box.Max.Z > area.Max.Z) continue;
                    Aabb back = BrMap.Tr(box, inv, c);
                    set.Add($"{Math.Round(back.Min.X - c - au, 3)} {Math.Round(back.Min.Y, 3)} {Math.Round(back.Min.Z - c - av, 3)} {Math.Round(back.Max.X - c - au, 3)} {Math.Round(back.Max.Y, 3)} {Math.Round(back.Max.Z - c - av, 3)} {_w.Tiles[t].Kind}");
                }
                Assert.That(set.Count, Is.GreaterThan(500), r.Name);
                if (first == null) { first = set; continue; }
                Assert.That(set.SetEquals(first), Is.True, $"{r.Name}'s spawn building differs from {_br.Regions[0].Name}'s");
            }
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
        public void EveryStairHasHeadroom_AndCanBeRunUp()
        {
            Assert.That(B.Climbs.Count, Is.GreaterThanOrEqualTo(8 * 2 + 20), "the spawn regions' stairwells, the towers' and the village houses'");
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
                int ticks = (int)Math.Ceiling((len + 2) / _c.SprintSpeed * SimTime.TicksPerSecond) + 30 * SimTime.Per60;
                MotorState end = Run(start, YawOf(s.Rise), ticks);
                Assert.That(end.Position.Y, Is.EqualTo(s.Bounds.Max.Y).Within(0.05), $"ran up the {s.Kind} at {s.Bounds.Min} (ended at {end.Position})");
            }
        }

        [Test]
        public void EveryDoorwayIsClear()
        {
            Assert.That(B.Doorways.Count, Is.GreaterThan(80));
            foreach (Aabb d in B.Doorways)
            {
                // A player fits through it: at its middle, or at either end (a tower's stair door opens onto the foot
                // of the flight on every other floor; you walk in beside the first step).
                Vec3d c = (d.Min + d.Max) * 0.5;
                bool alongX = d.Max.X - d.Min.X > d.Max.Z - d.Min.Z;
                double room = (alongX ? d.Max.X - d.Min.X : d.Max.Z - d.Min.Z) / 2 - _c.HalfWidth - 0.01;
                bool fits = false;
                foreach (double off in new[] { 0, -room, room })
                {
                    Vec3d p = c + (alongX ? new Vec3d(off, 0, 0) : new Vec3d(0, 0, off));
                    var player = new Aabb(new Vec3d(p.X - _c.HalfWidth, d.Min.Y + 0.01, p.Z - _c.HalfWidth), new Vec3d(p.X + _c.HalfWidth, d.Min.Y + _c.StandHeight, p.Z + _c.HalfWidth));
                    if (!_w.BoxOverlapsSolid(player)) { fits = true; break; }
                }
                Assert.That(fits, Is.True, $"doorway at {d.Min} is blocked");
            }
        }

        [Test]
        public void ThirtyTwoPads_AllClear_SameDistanceToTheirPoi_SameWalkToTheirLoot()
        {
            var all = new List<Vec3d>();
            foreach (BrRegion r in _br.Regions)
            {
                Vec3d poi = r.Anchor;
                Assert.That(r.Building.Min.X < r.Pads[0].X && r.Pads[0].X < r.Building.Max.X, Is.True, "the pads are in the building");
                for (int i = 0; i < 4; i++)
                {
                    Vec3d p = r.Pads[i];
                    all.Add(p);
                    Assert.That(Stands(p), Is.True, $"pad {r.Name} {i} at {p}");
                    Assert.That(Stands(r.LootSpots[i] + new Vec3d(0.5, 0, 0)) || Stands(r.LootSpots[i] - new Vec3d(0.5, 0, 0)), Is.True);
                    double toPoi = (new Vec3d(p.X, 0, p.Z) - new Vec3d(poi.X, 0, poi.Z)).Length;
                    Assert.That(toPoi, Is.EqualTo(_br.PadToPoi).Within(1e-6), $"{r.Name} pad {i} is {toPoi:0.000} m from its anchor");
                    Assert.That((p - r.LootSpots[i]).HorizontalLength, Is.EqualTo(_br.PadToLoot).Within(1e-6));
                    // The walk to the loot is a straight, clear line out of the doorway.
                    Vec3d d = (r.LootSpots[i] - p).WithY(0);
                    for (double f = 0; f <= 1.0001; f += 0.02)
                    {
                        Vec3d q = p + d * f;
                        var box = new Aabb(q + new Vec3d(-_c.HalfWidth, 0.05, -_c.HalfWidth), q + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
                        Assert.That(_t.BoxOverlapsSolid(box), Is.False, $"{r.Name} pad {i}: the way to its loot is blocked at {q}");
                    }
                }
                foreach (Vec3d s in r.SquadSpots) Assert.That(Stands(s), Is.True, $"squad spot in {r.Name}");
            }
            Assert.That(all.Count, Is.EqualTo(32));
            TestContext.WriteLine($"pad to POI {_br.PadToPoi:0.0} m, pad to loot {_br.PadToLoot:0.0} m");
            Assert.That(_br.PadToLoot, Is.InRange(8.0, 20.0), "a short walk");
            Assert.That(_br.PadToPoi, Is.EqualTo(BrSpawnKit.PadToAnchor).Within(1e-6));
            foreach (BrRegion r in _br.Regions)
                Assert.That((r.Anchor - _br.Centre).HorizontalLength, Is.EqualTo(BrLayout.AnchorRing).Within(1e-6), "every anchor is as far from the middle");
        }

        [Test]
        public void NobodyCanBeShotOffSpawn_NoPadSeesAnother_AndPadsAreSpreadOut()
        {
            double h = _c.StandHeight;
            var pads = new List<Vec3d>();
            foreach (BrRegion r in _br.Regions) pads.AddRange(r.Pads);
            double nearest = double.MaxValue;
            var seen = new List<string>();
            for (int a = 0; a < pads.Count; a++)
                for (int b = 0; b < pads.Count; b++)
                {
                    if (a == b) continue;
                    nearest = Math.Min(nearest, (pads[a] - pads[b]).HorizontalLength);
                    Vec3d eye = pads[a] + new Vec3d(0, _c.StandEye, 0);
                    if (DuelBot.CanSee(_t, eye, pads[b], h)) seen.Add($"pad {a} at {pads[a]} sees pad {b} at {pads[b]}, {(pads[a] - pads[b]).HorizontalLength:0} m");
                }
            TestContext.WriteLine($"nearest two pads: {nearest:0.0} m");
            Assert.That(seen, Is.Empty, string.Join("\n", seen));
            Assert.That(nearest, Is.GreaterThanOrEqualTo(12.0));
        }

        [Test]
        public void ChestsAndItemsRestOnFloors_InTheClear()
        {
            Assert.That(_br.Chests.Count, Is.GreaterThan(9 * BrLayout.MinChests), "plenty of loot, much of it behind walls");
            foreach (BrChestSpot ch in _br.Chests)
            {
                Vec3d p = ItemRules.Settle(_t, ch.Position + new Vec3d(0, 0.5, 0));
                Assert.That(p.Y, Is.EqualTo(ch.Position.Y).Within(0.01), $"chest at {ch.Position} rests on a floor");
                Assert.That(_t.BoxOverlapsSolid(new Aabb(p + new Vec3d(-0.4, 0.02, -0.4), p + new Vec3d(0.4, 0.6, 0.4))), Is.False, $"chest at {p}");
            }
            foreach (GroundItem g in _info.ItemSpawns)
                Assert.That(_t.BoxOverlapsSolid(new Aabb(g.Position + new Vec3d(-0.2, 0.01, -0.2), g.Position + new Vec3d(0.2, 0.3, 0.2))), Is.False);
        }

        [Test]
        public void TheRimKeepsEveryoneIn()
        {
            double c = _br.Size / 2;
            // Sprint flat out at the edge from the middle of each side: the cliff stops you.
            foreach (double yaw in new[] { 0, Math.PI / 2, Math.PI, -Math.PI / 2 })
            {
                var dir = new Vec3d(Math.Sin(yaw), 0, Math.Cos(yaw));
                Vec3d start = new Vec3d(c, 0, c) + dir * 128 + new Vec3d(dir.Z, 0, -dir.X) * 20;
                MotorState end = Run(start, yaw, 8 * SimTime.TicksPerSecond, climb: true);
                double out_ = Math.Max(Math.Abs(end.Position.X - c), Math.Abs(end.Position.Z - c));
                Assert.That(out_, Is.LessThan(BrMap.CliffStart), $"ran off the map heading {yaw:0.00} (ended at {end.Position})");
            }
        }

        // ---------------------------------------------------------------- helpers

        private bool Stands(Vec3d p)
        {
            var box = new Aabb(p + new Vec3d(-_c.HalfWidth, 0.02, -_c.HalfWidth), p + new Vec3d(_c.HalfWidth, _c.StandHeight, _c.HalfWidth));
            if (_t.BoxOverlapsSolid(box)) return false;
            TerrainHit down = _t.Raycast(p + new Vec3d(0, 0.3, 0), new Vec3d(0, -1, 0), 0.5);
            return down.Hit;
        }

        private static double YawOf(Dir d) => d == Dir.North ? 0 : d == Dir.East ? Math.PI / 2 : d == Dir.South ? Math.PI : -Math.PI / 2;
        private static Vec3d DirVec(Dir d) => d == Dir.North ? new Vec3d(0, 0, 1) : d == Dir.South ? new Vec3d(0, 0, -1) : d == Dir.East ? new Vec3d(1, 0, 0) : new Vec3d(-1, 0, 0);

        private MotorState Run(Vec3d at, double yaw, int ticks, bool climb = false)
        {
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 30 * SimTime.Per60; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = yaw });
            bool held = false;
            for (int k = 0; k < ticks; k++)
            {
                bool jump = climb && k > 10 * SimTime.Per60 && (held || (s.Grounded && s.HorizontalSpeed < 2));
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
    }
}
