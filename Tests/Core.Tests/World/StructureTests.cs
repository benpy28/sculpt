using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Step 8: breakable floors, stairs, ramps and frame. Every hole is made of whole big tiles; thin frame pieces only
    /// go when the big tiles around them have gone, so a hole is never a slit you can be shot through.
    /// </summary>
    public class StructureTests
    {
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
        private TileWalls T => _w.Tiles;

        /// <summary>Two wall tiles side by side with a 0.3 m column between them (linked to both).</summary>
        private static int[] Pair(BrushWorld w)
        {
            TileWalls t = w.Tiles;
            int a = t.AddTile(new Aabb(new Vec3d(0, 0, 10), new Vec3d(1.5, 1.5, 10.3)), Dir.North);
            int b = t.AddTile(new Aabb(new Vec3d(1.8, 0, 10), new Vec3d(3.3, 1.5, 10.3)), Dir.North);
            int col = t.AddFollower(new Aabb(new Vec3d(1.5, 0, 10), new Vec3d(1.8, 1.5, 10.3)), new[] { a, b });
            return new[] { a, b, col };
        }

        [Test]
        public void AFollowerGoesOnlyWhenEveryTileAroundItHasGone_AndComesBackWithAnyOfThem()
        {
            var w = new BrushWorld();
            int[] p = Pair(w);
            TileWalls t = w.Tiles;
            Assert.That(t.IsFollower(p[2]), Is.True);
            Assert.That(t.FollowersOf(p[0]), Is.EquivalentTo(new[] { p[2] }));
            Assert.That(t.Damage(p[2], 1000, TileCause.Grenade), Is.False, "frame takes no damage of its own");
            Assert.That(t[p[2]].State, Is.EqualTo(TileState.Intact));

            t.Damage(p[0], 1000, TileCause.Bullet);
            Assert.That(t[p[2]].State, Is.EqualTo(TileState.Intact), "one side still stands: the column stays (no slit beside it)");
            Assert.That(w.BoxOverlapsSolid(new Aabb(new Vec3d(1.55, 0.5, 10.1), new Vec3d(1.75, 1.0, 10.2))), Is.True);

            t.Changes.Clear();
            t.Damage(p[1], 1000, TileCause.Bullet, by: 7);
            Assert.That(t[p[2]].State, Is.EqualTo(TileState.Broken), "both sides gone: the column goes too, one 3.3 m hole");
            Assert.That(w.BoxOverlapsSolid(new Aabb(new Vec3d(0.1, 0.1, 9.9), new Vec3d(3.2, 1.4, 10.4))), Is.False);
            Assert.That(t.Changes.Count, Is.EqualTo(2), "the tile, then the column it took with it");
            Assert.That(t.Changes[1].Tile, Is.EqualTo(p[2]));
            Assert.That(t.Changes[1].Cause, Is.EqualTo(TileCause.Bullet));
            Assert.That(t.Changes[1].By, Is.EqualTo(7));

            t.Set(p[1], TileState.Patched, 50, TileCause.Heal);
            Assert.That(t[p[2]].State, Is.EqualTo(TileState.Intact), "healing either side brings the column back whole");
            Assert.That(t[p[2]].Health, Is.EqualTo(t.Config.IntactHealth));
        }

        [Test]
        public void AFollowerWithNothingAroundIt_IsAnOrdinaryBreakableFramePiece()
        {
            var w = new BrushWorld();
            int col = w.Tiles.AddFollower(new Aabb(new Vec3d(0, 0, 0), new Vec3d(0.3, 1.5, 0.3)), new int[0]);
            Assert.That(w.Tiles.IsFollower(col), Is.False);
            Assert.That(w.Tiles[col].Kind, Is.EqualTo(TileKind.Frame));
            Assert.That(w.Tiles.Damage(col, 100, TileCause.Bullet), Is.True);
        }

        /// <summary>
        /// The no-slit rule, checked on every frame piece of the block: across each of its thin (0.3 m) axes, one of its
        /// two sides is either open or made only of tiles it is linked to. So when it goes, at least one side is already
        /// open and the gap joins that hole: never a thin gap between two solid tiles.
        /// </summary>
        [Test]
        public void NoFramePieceCanEverLeaveASlit_EveryThinSideIsLinkedOrOpen()
        {
            var near = new List<int>();
            int checkedPieces = 0;
            foreach (int f in B.Frame)
            {
                Aabb b = T.BoundsOf(f);
                var links = new HashSet<int>(T[f].Links ?? new int[0]);
                for (int axis = 0; axis < 3; axis++)
                {
                    double size = axis == 0 ? b.Max.X - b.Min.X : axis == 1 ? b.Max.Y - b.Min.Y : b.Max.Z - b.Min.Z;
                    if (size > 0.31) continue;
                    bool lowOk = SideOk(b, axis, false, links, near, f), highOk = SideOk(b, axis, true, links, near, f);
                    Assert.That(lowOk || highOk, Is.True, $"frame tile {f} at {b.Min} could leave a slit across axis {axis}");
                    checkedPieces++;
                }
            }
            Assert.That(checkedPieces, Is.GreaterThan(500));
        }

        /// <summary>Every tile touching that face of the box (a thin probe just outside it) is in the links.</summary>
        private bool SideOk(Aabb b, int axis, bool high, HashSet<int> links, List<int> near, int self)
        {
            const double d = 0.05, s = 0.02;
            Vec3d lo = b.Min + new Vec3d(s, s, s), hi = b.Max - new Vec3d(s, s, s);
            if (axis == 0) { lo = new Vec3d(high ? b.Max.X : b.Min.X - d, lo.Y, lo.Z); hi = new Vec3d(high ? b.Max.X + d : b.Min.X, hi.Y, hi.Z); }
            if (axis == 1) { lo = new Vec3d(lo.X, high ? b.Max.Y : b.Min.Y - d, lo.Z); hi = new Vec3d(hi.X, high ? b.Max.Y + d : b.Min.Y, hi.Z); }
            if (axis == 2) { lo = new Vec3d(lo.X, lo.Y, high ? b.Max.Z : b.Min.Z - d); hi = new Vec3d(hi.X, hi.Y, high ? b.Max.Z + d : b.Min.Z); }
            var probe = new Aabb(lo, hi);
            T.TilesNear(probe, near);
            foreach (int t in near)
            {
                if (t == self || !BrushWorld.Overlaps(T.ShapeOf(t), probe)) continue;
                // Stair flights and the roof ramp run along walls: a wall or column beside one opens onto the climb's
                // side, which is only solid under its slope (the wall's own hole is the same shape), so they don't count.
                if (T[t].Kind == TileKind.Stair || T[t].Kind == TileKind.Ramp) continue;
                if (!links.Contains(t)) return false;
            }
            return true;
        }

        [Test]
        public void RandomDamage_FrameAlwaysMatchesItsLinks_AndHealingPutsItBack()
        {
            var rng = new Random(8);
            var bigs = new List<int>();
            foreach (int t in B.Tiles) if (!T.IsFollower(t)) bigs.Add(t);
            for (int round = 0; round < 6; round++)
            {
                for (int k = 0; k < 400; k++) T.Damage(bigs[rng.Next(bigs.Count)], 60, TileCause.Bullet);
                CheckFollowers();
                for (int k = 0; k < 150; k++)
                {
                    int t = bigs[rng.Next(bigs.Count)];
                    if (T[t].State == TileState.Broken) T.Set(t, TileState.Patched, 50, TileCause.Heal);
                }
                CheckFollowers();
            }
        }

        private void CheckFollowers()
        {
            foreach (int f in B.Frame)
            {
                int[] l = T[f].Links;
                if (l == null) continue;
                bool all = Array.TrueForAll(l, x => T[x].State == TileState.Broken);
                Assert.That(T[f].State == TileState.Broken, Is.EqualTo(all), $"frame tile {f}");
                Assert.That(_w[T[f].Brush].Solid, Is.EqualTo(!all));
            }
        }

        [Test]
        public void TheBlocksFloorsStairsRampAndFrame_AreAllBreakableTiles()
        {
            Assert.That(B.Floors.Count, Is.GreaterThan(600));
            Assert.That(B.Frame.Count, Is.GreaterThan(500));
            foreach (int f in B.Floors) Assert.That(T[f].Kind, Is.EqualTo(TileKind.Floor));
            int stairs = 0, ramps = 0;
            foreach (int[] c in B.ClimbTiles)
                foreach (int t in c)
                {
                    Assert.That(T.ShapeOf(t).IsRamp, Is.True);
                    if (T[t].Kind == TileKind.Stair) stairs++; else if (T[t].Kind == TileKind.Ramp) ramps++;
                }
            Assert.That(stairs, Is.EqualTo(6 * 8 * 2), "six flights of eight segments, two across");
            Assert.That(ramps, Is.EqualTo(16 * 2));
            // No plain unbreakable floor, frame, stair or ramp brush is left inside the buildings.
            foreach (Brush b in _w.Brushes)
            {
                if (b.Kind == BrushKind.Tile || b.Kind == BrushKind.Ground || b.Kind == BrushKind.Marker) continue;
                foreach (Aabb box in B.Buildings)
                    Assert.That(box.Expand(-0.01).Overlaps(b.Bounds), Is.False, $"unbreakable {b.Kind} at {b.Bounds.Min} inside a building");
            }
        }

        [Test]
        public void EveryHoleIsAtLeastPlayerSized_NoBigTileIsSmallerThanAPlayerCanFitThrough()
        {
            foreach (int t in B.Tiles)
            {
                if (T[t].Kind == TileKind.Frame) continue;
                Aabb b = T.BoundsOf(t);
                double[] s = { b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z };
                Array.Sort(s);
                // The two face sides (the third is the 0.3 m thickness, or a segment's rise).
                Assert.That(s[1], Is.GreaterThanOrEqualTo(1.2 - 1e-9), $"tile {t} at {b.Min}");
                Assert.That(s[2], Is.GreaterThanOrEqualTo(1.2 - 1e-9), $"tile {t} at {b.Min}");
            }
        }

        /// <summary>A first-floor tile in the middle of hall A's south-east room.</summary>
        private int FloorTileAt(double x, double y, double z)
        {
            foreach (int f in B.Floors)
            {
                Aabb b = T.BoundsOf(f);
                if (x > b.Min.X && x < b.Max.X && z > b.Min.Z && z < b.Max.Z && Math.Abs(b.Max.Y - y) < 1e-6) return f;
            }
            return -1;
        }

        [Test]
        public void BreakTheFloorUnderYou_AndYouDropToTheStoreyBelow()
        {
            Aabb hall = B.Buildings[0];
            var p = new Vec3d(hall.Max.X - 5, GreyboxBlock.StoreyHeight, hall.Min.Z + 5); // first floor, south-east room
            int tile = FloorTileAt(p.X, p.Y, p.Z);
            Assert.That(tile, Is.GreaterThanOrEqualTo(0));
            Aabb tb = T.BoundsOf(tile);
            var at = new Vec3d((tb.Min.X + tb.Max.X) / 2, p.Y, (tb.Min.Z + tb.Max.Z) / 2);
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 20; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput());
            Assert.That(s.Position.Y, Is.EqualTo(GreyboxBlock.StoreyHeight).Within(1e-6), "standing on it");
            T.Damage(tile, 1000, TileCause.Bullet);
            for (int k = 0; k < 60; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput());
            Assert.That(s.Position.Y, Is.EqualTo(0).Within(1e-6), "through a 1.5 m hole to the ground floor");
        }

        [Test]
        public void AGrenadeOnAFloor_BlowsAThreeByThreeTileHole()
        {
            // The second tile in from a corner of hall A's south-east first-floor room, so the blast stays inside one cell.
            Aabb hall = B.Buildings[0];
            int tile = FloorTileAt(hall.Min.X + 2 * GreyboxBlock.Module + 0.3 + 2.25, GreyboxBlock.StoreyHeight, hall.Min.Z + 0.3 + 2.25);
            Aabb tb = T.BoundsOf(tile);
            var blast = new Vec3d((tb.Min.X + tb.Max.X) / 2, tb.Max.Y, (tb.Min.Z + tb.Max.Z) / 2);
            var hit = new List<int>();
            GrenadeRules.TilesInBlast(T, blast, new GrenadeConfig(), hit);
            foreach (int t in hit) T.Damage(t, T.Config.GrenadeTileDamage, TileCause.Grenade);
            int broken = 0;
            double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
            foreach (int f in B.Floors)
            {
                if (T[f].State != TileState.Broken) continue;
                broken++;
                Aabb b = T.BoundsOf(f);
                x0 = Math.Min(x0, b.Min.X); x1 = Math.Max(x1, b.Max.X); z0 = Math.Min(z0, b.Min.Z); z1 = Math.Max(z1, b.Max.Z);
            }
            Assert.That(broken, Is.EqualTo(9));
            Assert.That(x1 - x0, Is.EqualTo(4.5).Within(1e-9));
            Assert.That(z1 - z0, Is.EqualTo(4.5).Within(1e-9));
            var player = new Aabb(new Vec3d(blast.X - 1.5, blast.Y - 0.31, blast.Z - 1.5), new Vec3d(blast.X + 1.5, blast.Y + 0.5, blast.Z + 1.5));
            Assert.That(_w.BoxOverlapsSolid(player), Is.False, "a clean square hole");
        }

        [Test]
        public void BreakAStairSegment_AndItIsAHoleYouFallThrough_TheRestOfTheFlightStands()
        {
            int ci = 0; // hall A's lower flight
            Brush flight = B.Climbs[ci];
            int[] seg = B.ClimbTiles[ci];
            int k = 5, tile = seg[k * 2];
            Brush s5 = T.ShapeOf(tile);
            Assert.That(s5.Bounds.Min.Y, Is.EqualTo(flight.Bounds.Min.Y + k * GreyboxBlock.SegmentRise).Within(1e-9));
            Vec3d c = (s5.Bounds.Min + s5.Bounds.Max) * 0.5;
            MotorState st = MotorState.At(new Vec3d(c.X, s5.Bounds.Max.Y + 0.1, c.Z));
            st.Tick = 1000;
            for (int i = 0; i < 30; i++) PlayerMovement.Step(_t, _c, ref st, new MoveInput());
            Assert.That(st.Position.Y, Is.GreaterThan(s5.Bounds.Min.Y), "stands on the flight");
            T.Damage(tile, 1000, TileCause.Bullet);
            for (int i = 0; i < 90; i++) PlayerMovement.Step(_t, _c, ref st, new MoveInput());
            Assert.That(st.Position.Y, Is.EqualTo(0).Within(1e-6), "through the stair to the floor under it");
            foreach (int t in seg) if (t != tile) Assert.That(T[t].State, Is.EqualTo(TileState.Intact));
        }

        [Test]
        public void ChargingNeverBreaksFloorsOrStairs()
        {
            // Sprint up hall A's lower flight over cracked segments and on across a cracked floor: nothing breaks.
            Brush flight = B.Climbs[0];
            foreach (int t in B.ClimbTiles[0]) T.Set(t, TileState.Damaged, 40, TileCause.Bullet);
            var cracked = new List<int>();
            foreach (int f in B.Floors)
            {
                Aabb b = T.BoundsOf(f);
                if (b.Max.Z > flight.Bounds.Max.Z && b.Min.Z < flight.Bounds.Max.Z + 6 && b.Max.X > flight.Bounds.Min.X && b.Min.X < flight.Bounds.Max.X && b.Max.Y < 5)
                { T.Set(f, TileState.Damaged, 40, TileCause.Bullet); cracked.Add(f); }
            }
            Assert.That(cracked.Count, Is.GreaterThan(2));
            var start = new Vec3d((flight.Bounds.Min.X + flight.Bounds.Max.X) / 2, 0, flight.Bounds.Min.Z - 2.2);
            MotorState s = MotorState.At(start);
            s.Tick = 1000;
            for (int k = 0; k < 150; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = 0, MoveZ = 1, SprintHeld = true });
            foreach (int t in B.ClimbTiles[0]) Assert.That(T[t].State, Is.EqualTo(TileState.Damaged));
            foreach (int t in cracked) Assert.That(T[t].State, Is.EqualTo(TileState.Damaged));
            Assert.That(s.Position.Z, Is.GreaterThan(flight.Bounds.Max.Z + 1), "ran on over the cracked floor");
            Assert.That(s.Position.Y, Is.EqualTo(GreyboxBlock.StoreyHeight).Within(0.05), "and ran up it");
        }

        [Test]
        public void AHealCantFinishWhileYouStandWhereTheFrameItRestoresWouldBe()
        {
            var w = new BrushWorld();
            int[] p = Pair(w);
            TileWalls t = w.Tiles;
            t.Damage(p[0], 1000, TileCause.Bullet);
            t.Damage(p[1], 1000, TileCause.Bullet);
            var heal = new TileHealing(t);
            // A thin box only inside the column's space, not either tile's.
            var inColumn = new List<HealerStatus> { new HealerStatus { Id = 2, Alive = true, Box = new Aabb(new Vec3d(1.55, 0.2, 9.9), new Vec3d(1.75, 1.2, 10.4)) } };
            Assert.That(heal.Occupied(p[0], inColumn), Is.True, "healing either tile brings the column back");
            Assert.That(heal.Occupied(p[1], inColumn), Is.True);
            t.Set(p[1], TileState.Patched, 50, TileCause.Heal);
            Assert.That(heal.Occupied(p[0], inColumn), Is.False, "the column is already back; it isn't part of this heal");
            Assert.That(TileHealing.StateCheck(t[p[2]], HealKind.Heal, t.Config), Is.EqualTo(HealResult.NothingToHeal), "frame isn't healed on its own");
        }

        [Test]
        public void StandingOnAStairSegmentAboveTheSlope_DoesNotBlockItsHeal()
        {
            int tile = B.ClimbTiles[0][4 * 2];
            Brush s = T.ShapeOf(tile);
            T.Damage(tile, 1000, TileCause.Bullet);
            var heal = new TileHealing(T);
            Vec3d c = (s.Bounds.Min + s.Bounds.Max) * 0.5;
            // A box resting on the slope's high end: inside the segment's bounding box, above its solid.
            var above = new Aabb(new Vec3d(c.X - 0.3, s.Bounds.Max.Y + 0.01, s.Bounds.Max.Z - 0.2), new Vec3d(c.X + 0.3, s.Bounds.Max.Y + 1.8, s.Bounds.Max.Z + 0.4));
            var inside = new Aabb(new Vec3d(c.X - 0.3, s.Bounds.Min.Y - 1, s.Bounds.Max.Z - 0.4), new Vec3d(c.X + 0.3, s.Bounds.Min.Y + 0.4, s.Bounds.Max.Z - 0.1));
            Assert.That(heal.Occupied(tile, new[] { new HealerStatus { Alive = true, Box = above } }), Is.False);
            Assert.That(heal.Occupied(tile, new[] { new HealerStatus { Alive = true, Box = inside } }), Is.True);
        }

        [Test]
        public void AimingAtFrame_IsNeverAHealTarget_AndABrokenColumnIsAimedThrough()
        {
            var w = new BrushWorld();
            int[] p = Pair(w);
            int behind = w.Tiles.AddTile(new Aabb(new Vec3d(1.2, 0, 12), new Vec3d(2.7, 1.5, 12.3)), Dir.North);
            w.Tiles.Damage(behind, 60, TileCause.Bullet);
            var eye = new Vec3d(1.65, 0.75, 8);
            var dir = new Vec3d(0, 0, 1);
            Assert.That(TileAim.Ray(w, w.Tiles, eye, dir, 6).Tile, Is.EqualTo(-1), "an intact column blocks like frame");
            w.Tiles.Damage(p[0], 1000, TileCause.Bullet);
            w.Tiles.Damage(p[1], 1000, TileCause.Bullet);
            TileTarget t = TileAim.Ray(w, w.Tiles, eye, dir, 6);
            Assert.That(t.Tile, Is.Not.EqualTo(p[2]));
            Assert.That(t.Tile, Is.EqualTo(p[0]).Or.EqualTo(p[1]).Or.EqualTo(behind), "the broken tiles around it, or the wall behind");
        }

        [Test]
        public void ClientsSeeTheFrameGoWithTheTilesAroundIt()
        {
            int[] pair = null;
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            }, wrap: g =>
            {
                var w = new BrushWorld();
                pair = Pair(w);
                w.Tiles.Changes.Clear();
                return new CompositeTerrain(g, w);
            });
            var watcher = h.AddClient(new Vec3d(1.6, 0, 4), team: 1);
            h.RunSeconds(0.3);
            h.Server.Walls.Damage(pair[0], 1000, TileCause.Bullet);
            h.Server.Walls.Damage(pair[1], 1000, TileCause.Bullet);
            h.RunSeconds(0.3);
            Assert.That(watcher.Client.Walls[pair[2]].State, Is.EqualTo(TileState.Broken));
            h.Server.Walls.Set(pair[0], TileState.Patched, 50, TileCause.Heal);
            h.RunSeconds(0.3);
            Assert.That(watcher.Client.Walls[pair[2]].State, Is.EqualTo(TileState.Intact));
        }
    }
}
