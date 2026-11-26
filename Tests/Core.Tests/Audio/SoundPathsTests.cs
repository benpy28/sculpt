using System;
using System.Diagnostics;
using NUnit.Framework;
using Sculpt.Core.Audio;

namespace Sculpt.Core.Tests.Audio
{
    /// <summary>
    /// Directional sound (Ben 2026-11-26): sounds are heard from the way they really reach you. Straight line clear: from
    /// the source. Otherwise round through the openings (from the opening, as far as the route), or through the walls
    /// (from the source, quiet and dull), whichever is louder. Hand-worked cases on a small room.
    /// </summary>
    public class SoundPathsTests
    {
        private const double Range = 40;

        /// <summary>
        /// Flat ground (top y 0) and a 6 x 6 m room, x 10..16, z 10..16, walls 4.5 m tall (0.3 m thick), with a 0.3 m roof
        /// at 4.5..4.8. South wall at z 10..10.3. With <paramref name="door"/> the south wall's second tile column from the
        /// west (x 11.5..13) is open to 3 m.
        /// </summary>
        private static CompositeTerrain Room(bool door, bool roof, out TileWalls walls, out int[] south)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            walls = w.Tiles;
            south = walls.AddWall(new Vec3d(10, 0, 10), Dir.East, 4, 3);
            walls.AddWall(new Vec3d(10, 0, 15.7), Dir.East, 4, 3);
            walls.AddWall(new Vec3d(10, 0, 10), Dir.North, 4, 3);
            walls.AddWall(new Vec3d(15.7, 0, 10), Dir.North, 4, 3);
            if (roof) w.AddBox(new Vec3d(10, 4.5, 10), new Vec3d(16, 4.8, 16), BrushKind.Floor);
            if (door)
            {
                // Bottom two tiles of column 1 (x 11.5..13): rows 0 and 1 (tile ids row * 4 + column).
                walls.Set(south[0 * 4 + 1], TileState.Broken, 0, TileCause.Punch);
                walls.Set(south[1 * 4 + 1], TileState.Broken, 0, TileCause.Punch);
            }
            walls.Changes.Clear();
            return new CompositeTerrain(g, w);
        }

        private static readonly Vec3d Inside = new Vec3d(13, 1.6, 14);   // in the room, north half
        private static readonly Vec3d Outside = new Vec3d(16.5, 1.6, 6);  // south-east, outside: the line to Inside crosses the south wall east of the door

        [Test]
        public void OpenGround_IsDirect_FromTheSource_Unfiltered()
        {
            Room(false, true, out _, out _);
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            var paths = new SoundPaths(g);
            var a = new Vec3d(5, 1.6, 5);
            var b = new Vec3d(20, 0.3, 25);
            SoundRoute r = paths.Route(a, b, Range);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Direct));
            Assert.That(r.Apparent, Is.EqualTo(b));
            Assert.That(r.Distance, Is.EqualTo((b - a).Length).Within(1e-9));
            Assert.That(r.GainDb, Is.EqualTo(0));
            Assert.That(r.CutoffHz, Is.EqualTo(paths.Config.OpenCutoffHz));
        }

        [Test]
        public void BehindAWallWithADoor_ComesFromTheDoor_AsFarAsTheWayRound()
        {
            CompositeTerrain t = Room(door: true, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            Assert.That(paths.Clear(Outside, Inside), Is.False, "the straight line is walled");
            SoundRoute r = paths.Route(Outside, Inside, Range);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Around));
            // Hand-worked: the tight way runs past the door's east jamb (x 13, z 10..10.3) at about head height.
            // Outside -> (13, ~1.6, 10.3) -> Inside: |(-3.5, 0, 4.3)| + |(0, 0, 3.7)| = 5.54 + 3.7 = 9.24 m.
            Assert.That(r.AroundDistance, Is.EqualTo(9.24).Within(0.45));
            Assert.That(r.Distance, Is.EqualTo(r.AroundDistance));
            Assert.That(r.Bends, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(2));
            // Heard from the door: the arrival direction points at the doorway (x 11.5..13 at z 10), not at the source.
            Vec3d toDoor = (new Vec3d(12.6, 1.6, 10.15) - Outside).Normalized;
            Vec3d toSource = (Inside - Outside).Normalized;
            Assert.That(r.ArrivesFrom.Dot(toDoor), Is.GreaterThan(0.99), "arrives from the doorway");
            Assert.That(r.ArrivesFrom.Dot(toDoor), Is.GreaterThan(r.ArrivesFrom.Dot(toSource)));
            Assert.That(r.GainDb, Is.EqualTo(-paths.Config.BendLossDb * r.Bends).Within(1e-9));
            Assert.That(r.CutoffHz, Is.LessThan(paths.Config.OpenCutoffHz).And.GreaterThanOrEqualTo(paths.Config.MinBendCutoffHz));
            Assert.That((r.Apparent - Outside).Length, Is.EqualTo(r.Distance).Within(1e-9), "the voice sits as far away as the route");
        }

        [Test]
        public void ASealedRoom_IsHeardThroughTheWall_FromTheSource_DullAndQuiet()
        {
            CompositeTerrain t = Room(door: false, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            SoundRoute r = paths.Route(Outside, Inside, Range);
            Assert.That(r.AroundFound, Is.False);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(r.Walls, Is.EqualTo(1));
            // The line crosses the 0.3 m south wall at a slant: 0.3 / cos(angle) of solid.
            Vec3d d = (Inside - Outside).Normalized;
            double solid = 0.3 / Math.Abs(d.Z);
            Assert.That(r.SolidMetres, Is.EqualTo(solid).Within(0.06));
            double db = -(paths.Config.WallLossDb + paths.Config.SolidLossDbPerMetre * r.SolidMetres);
            Assert.That(r.GainDb, Is.EqualTo(db).Within(1e-9));
            Assert.That(r.ArrivesFrom.Dot(d), Is.GreaterThan(0.9999), "from its real direction");
            Assert.That(r.Distance, Is.EqualTo((Inside - Outside).Length).Within(1e-9));
            Assert.That(r.CutoffHz, Is.EqualTo(paths.Config.ThroughCutoffHz));
        }

        [Test]
        public void ThroughAWallStraightOn_CountsExactlyItsThickness()
        {
            // Square on through the 0.3 m south wall: 0.3 m of solid (was counted 0.35 m, one 5 cm march step too many:
            // 0.9 dB too quiet through every wall, and a wall just under the 2 m limit counted as over it).
            CompositeTerrain t = Room(door: false, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            SoundRoute r = paths.Route(new Vec3d(12.6, 1.6, 7), new Vec3d(12.6, 1.6, 13), Range);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(r.Walls, Is.EqualTo(1));
            Assert.That(r.SolidMetres, Is.EqualTo(0.3).Within(0.002));
            // 1.96 m of solid is under the 2 m limit: still heard through.
            var w = new BrushWorld();
            w.AddBox(new Vec3d(0, 0, 10), new Vec3d(20, 9, 11.96), BrushKind.Frame);
            var thick = new SoundPaths(w);
            SoundRoute r2 = thick.Route(new Vec3d(5, 1.6, 8), new Vec3d(5, 1.6, 14), Range);
            Assert.That(r2.ThroughPossible, Is.True);
            Assert.That(r2.SolidMetres, Is.EqualTo(1.96).Within(0.002));
        }

        [Test]
        public void NoRoof_ASoundInsideGoesOverTheWallTop()
        {
            CompositeTerrain t = Room(door: false, roof: false, out _, out _);
            var paths = new SoundPaths(t);
            SoundRoute r = paths.Route(Outside, Inside, Range);
            Assert.That(r.AroundFound, Is.True, "over the 4.5 m wall top");
            Assert.That(r.ArrivesFrom.Y, Is.GreaterThan(0.25), "from above the wall");
            // Over the top is ~ 2 x 2.9 m of climb on a 9.6 m line: longer than straight, shorter than the double.
            Assert.That(r.AroundDistance, Is.GreaterThan((Inside - Outside).Length).And.LessThan(16));
        }

        [Test]
        public void BreakingAHole_OpensTheWayRound_AfterInvalidate()
        {
            CompositeTerrain t = Room(door: false, roof: true, out TileWalls w, out int[] south);
            var paths = new SoundPaths(t);
            Assert.That(paths.Route(Outside, Inside, Range).Way, Is.EqualTo(SoundWay.Through));
            int tile = south[1 * 4 + 3]; // row 1, east column (x 14.5..16, y 1.5..3)
            w.Set(tile, TileState.Broken, 0, TileCause.Bullet);
            paths.Invalidate(w.BoundsOf(tile));
            SoundRoute r = paths.Route(Outside, Inside, Range);
            Assert.That(r.Way, Is.Not.EqualTo(SoundWay.Through));
            Assert.That(r.Way == SoundWay.Direct || r.Way == SoundWay.Around);
        }

        [Test]
        public void ASmallBlastHole_LetsSoundThrough()
        {
            // A 0.375 x 0.375 m hole (2 x 2 of the 8 x 8 cells) in the middle of a sealed room's south wall, low down.
            CompositeTerrain t = Room(door: false, roof: true, out TileWalls w, out int[] south);
            int tile = south[0 * 4 + 2]; // row 0, column 2: x 13..14.5, y 0..1.5
            Assert.That(w.HasCells(tile), Is.True);
            ulong open = 0;
            for (int a = 3; a <= 4; a++)
                for (int b = 3; b <= 4; b++)
                    open |= 1ul << (b * 8 + a);
            w.Set(tile, w[tile].State, w[tile].Health, TileCause.Bullet, open: open);
            var paths = new SoundPaths(t);
            var listener = new Vec3d(13.75, 1.8, 7);
            var source = new Vec3d(13.75, 0.4, 13);
            Assert.That(paths.Clear(listener, source), Is.False);
            SoundRoute r = paths.Route(listener, source, Range);
            Assert.That(r.AroundFound, Is.True, "found the hole");
            Assert.That(r.Way, Is.EqualTo(SoundWay.Around));
            Assert.That(r.AroundDistance, Is.LessThan(8), "straight through the hole, not round the room");
        }

        [Test]
        public void ARouteIsTheSameBothWays()
        {
            CompositeTerrain t = Room(door: true, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            SoundRoute ab = paths.Route(Outside, Inside, Range);
            SoundRoute ba = paths.Route(Inside, Outside, Range);
            Assert.That(ba.Way, Is.EqualTo(ab.Way));
            Assert.That(ba.Distance, Is.EqualTo(ab.Distance).Within(0.5));
            Assert.That(ba.GainDb, Is.EqualTo(ab.GainDb).Within(paths.Config.BendLossDb + 1e-9));
        }

        [Test]
        public void DuelMap_RandomPairs_SoundTheSameBothWays()
        {
            // Spec: "Symmetric: whoever can hear you, you can hear back." Random ears and sources on the duel map, each at
            // least a player's half-width clear of every wall (where ears and footsteps really are).
            CompositeTerrain t = DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out BreachMapInfo info));
            var paths = new SoundPaths(t);
            var rng = new Random(11);
            double size = info.SizeX;
            var hb = new Vec3d(0.36, 0.36, 0.36);
            int n = 0, wayDiff = 0, distDiff = 0;
            string first = null;
            while (n < 1500)
            {
                var a = new Vec3d(size * 0.2 + rng.NextDouble() * size * 0.6, 0.3 + rng.Next(3) * 4.8 + 1.3, size * 0.2 + rng.NextDouble() * size * 0.6);
                double ang = rng.NextDouble() * Math.PI * 2, d = 3 + rng.NextDouble() * 27;
                var b = new Vec3d(a.X + Math.Cos(ang) * d, 0.3 + rng.Next(3) * 4.8 + 1.3, a.Z + Math.Sin(ang) * d);
                if (t.BoxOverlapsSolid(new Aabb(a - hb, a + hb)) || t.BoxOverlapsSolid(new Aabb(b - hb, b + hb))) continue;
                n++;
                SoundRoute ab = paths.Route(a, b, 34), ba = paths.Route(b, a, 34);
                if (ab.Way != ba.Way) { wayDiff++; first ??= $"{a} -> {b}: {ab.Way} ({ab.Distance:0.00} m) but back {ba.Way} ({ba.Distance:0.00} m)"; }
                else if (ab.Audible && Math.Abs(ab.Distance - ba.Distance) > 0.05) { distDiff++; first ??= $"({a.X:R}, {a.Y:R}, {a.Z:R}) -> ({b.X:R}, {b.Y:R}, {b.Z:R}): {ab.Way} {ab.Distance:0.00} m but back {ba.Distance:0.00} m"; }
            }
            TestContext.Out.WriteLine($"{n} pairs: way differs {wayDiff}, distance differs {distDiff}. {first}");
            Assert.That(wayDiff, Is.EqualTo(0), first);
            Assert.That(distDiff, Is.EqualTo(0), first);
        }

        [Test]
        public void ANearSoundThroughOneWall_BeatsAFarWayRound()
        {
            // Door on the south wall; listener hugging the outside of the east wall, source just inside it: 0.8 m through
            // one wall against ~12 m round through the door.
            CompositeTerrain t = Room(door: true, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            var listener = new Vec3d(16.5, 1.6, 14.5);
            var source = new Vec3d(15.2, 1.6, 14.5);
            SoundRoute r = paths.Route(listener, source, Range);
            Assert.That(r.AroundFound, Is.True);
            Assert.That(r.AroundDistance, Is.GreaterThan(8));
            Assert.That(r.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(r.ArrivesFrom.X, Is.LessThan(-0.99), "from its real side");
        }

        [Test]
        public void TooFar_IsSilent_AndOutOfRangeRoundTheCornerIsOnlyHeardThrough()
        {
            CompositeTerrain t = Room(door: true, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            Assert.That(paths.Route(new Vec3d(1, 1, 1), new Vec3d(30, 1, 30), 20).Way, Is.EqualTo(SoundWay.Silent));
            // Range just over the straight line but under the way round: only the muffled straight line is left.
            double straight = (Inside - Outside).Length;
            SoundRoute r = paths.Route(Outside, Inside, straight + 0.2);
            Assert.That(r.AroundFound, Is.False);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Through));
        }

        [Test]
        public void ThickSolid_StopsEverything()
        {
            // A sound in a sealed 3 m thick bunker: nothing gets out.
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            var w = new BrushWorld();
            w.AddBox(new Vec3d(8, 0, 8), new Vec3d(20, 1, 20), BrushKind.Frame); // floor slab
            w.AddBox(new Vec3d(8, 1, 8), new Vec3d(11, 6, 20), BrushKind.Frame);
            w.AddBox(new Vec3d(17, 1, 8), new Vec3d(20, 6, 20), BrushKind.Frame);
            w.AddBox(new Vec3d(11, 1, 8), new Vec3d(17, 6, 11), BrushKind.Frame);
            w.AddBox(new Vec3d(11, 1, 17), new Vec3d(17, 6, 20), BrushKind.Frame);
            w.AddBox(new Vec3d(8, 6, 8), new Vec3d(20, 9, 20), BrushKind.Frame);
            var paths = new SoundPaths(new CompositeTerrain(g, w));
            SoundRoute r = paths.Route(new Vec3d(14, 1.6, 4), new Vec3d(14, 1.6, 14), Range);
            Assert.That(r.Way, Is.EqualTo(SoundWay.Silent));
            Assert.That(r.Gain, Is.EqualTo(0));
            // A gunshot (220 m) in there is still heard, distant and dull, from its real direction.
            SoundRoute shot = paths.Route(new Vec3d(14, 1.6, 4), new Vec3d(14, 1.6, 14), 220);
            Assert.That(shot.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(shot.Obstructed, Is.True);
            Assert.That(shot.GainDb, Is.EqualTo(-paths.Config.FarObstructedLossDb));
            Assert.That(shot.CutoffHz, Is.EqualTo(paths.Config.FarObstructedCutoffHz));
            Assert.That(shot.ArrivesFrom.Z, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void ASoundInsideASolid_LikeADoorsMiddle_IsHeardDirectlyFromBothSides()
        {
            CompositeTerrain t = Room(door: false, roof: true, out TileWalls w, out int[] south);
            var paths = new SoundPaths(t);
            Vec3d mid = w.BoundsOf(south[1]).Center; // x 12.25, y 0.75, z 10.15
            Assert.That(paths.Route(new Vec3d(12.25, 1.6, 6), mid, Range, sourceRadius: 0.4).Way, Is.EqualTo(SoundWay.Direct));
            Assert.That(paths.Route(new Vec3d(12.25, 1.6, 13), mid, Range, sourceRadius: 0.4).Way, Is.EqualTo(SoundWay.Direct));
        }

        [Test]
        public void ARouteIsReusedInsideItsCells_AndDroppedWhenTheWorldChanges()
        {
            CompositeTerrain t = Room(door: false, roof: true, out TileWalls w, out int[] south);
            var paths = new SoundPaths(t);
            SoundRoute a = paths.Route(Outside, Inside, Range, now: 1.0);
            long searches = paths.Searches;
            Assert.That(searches, Is.EqualTo(1));
            SoundRoute b = paths.Route(Outside + new Vec3d(0.05, 0, 0), Inside, Range, now: 1.1);
            Assert.That(paths.Searches, Is.EqualTo(searches), "reused");
            Assert.That(b.Way, Is.EqualTo(a.Way));
            Assert.That(b.ArrivesFrom.Dot((Inside - Outside - new Vec3d(0.05, 0, 0)).Normalized), Is.GreaterThan(0.99999), "re-aimed at the real source");
            int tile = south[1 * 4 + 3];
            w.Set(tile, TileState.Broken, 0, TileCause.Bullet);
            paths.Invalidate(w.BoundsOf(tile));
            Assert.That(paths.Route(Outside, Inside, Range, now: 1.15).Way, Is.Not.EqualTo(SoundWay.Through));
            // Stale by time.
            var p2 = new SoundPaths(Room(door: false, roof: true, out _, out _));
            p2.Route(Outside, Inside, Range, now: 1.0);
            p2.Route(Outside, Inside, Range, now: 1.0 + p2.Config.RouteCacheSeconds + 0.01);
            Assert.That(p2.Searches, Is.EqualTo(2), "routed again");
        }

        [Test]
        public void TheFastGrid_AgreesWithRayCasts_OnEveryBuildingFace()
        {
            // Buildings only (no sub-cell ground, whose chamfers the grid counts as solid), with holes and broken tiles.
            BrushWorld w = DuelMap.BuildBrushes(out BreachMapInfo info);
            var rng = new Random(3);
            int broke = 0;
            for (int i = 0; i < w.Tiles.Count; i += 7)
            {
                if (!w.Tiles.HasCells(i)) continue;
                ulong open = 0;
                int a0 = rng.Next(6), b0 = rng.Next(6);
                for (int a = a0; a < a0 + 3; a++)
                    for (int b = b0; b < b0 + 2; b++) open |= 1ul << (b * 8 + a);
                if (i % 3 == 0) w.Tiles.Set(i, TileState.Broken, 0, TileCause.Bullet);
                else w.Tiles.Set(i, w.Tiles[i].State, w.Tiles[i].Health, TileCause.Bullet, open: open);
                broke++;
            }
            var paths = new SoundPaths(w);
            Assert.That(paths.Grid, Is.Not.Null);
            int checkedFaces = 0, mismatches = 0;
            string first = null;
            for (int n = 0; n < 60000; n++)
            {
                int x = rng.Next((int)(info.SizeX / 0.5)), y = rng.Next(-2, 30), z = rng.Next((int)(info.SizeZ / 0.5)), a = rng.Next(3);
                bool fast = paths.Grid.Open(x, y, z, a);
                bool slow = paths.TestFace(x, y, z, a);
                checkedFaces++;
                if (fast != slow) { mismatches++; first ??= $"cell {x},{y},{z} axis {a}: grid {fast}, rays {slow}"; }
            }
            TestContext.Out.WriteLine($"{broke} tiles holed or broken, {checkedFaces} faces, {mismatches} mismatches {first}");
            Assert.That(mismatches, Is.EqualTo(0), first);
        }

        [Test]
        public void OverBudget_NoSearch_OnlyThroughTheWall_AndNotRemembered()
        {
            CompositeTerrain t = Room(door: true, roof: true, out _, out _);
            var paths = new SoundPaths(t);
            SoundRoute cheap = paths.Route(Outside, Inside, Range, now: 1.0, allowSearch: false);
            Assert.That(cheap.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(paths.Searches, Is.EqualTo(0));
            SoundRoute full = paths.Route(Outside, Inside, Range, now: 1.01);
            Assert.That(full.Way, Is.EqualTo(SoundWay.Around), "the cheap answer was not cached");
            Assert.That(paths.Searches, Is.EqualTo(1));
        }

        [Test]
        public void HeadShadow_OnlyBehind()
        {
            var c = new SoundConfig();
            var fwd = new Vec3d(0, 0, 1);
            SoundPaths.HeadShadow(fwd, new Vec3d(0, 0, 1), c, out double db, out double hz);
            Assert.That(db, Is.EqualTo(0)); Assert.That(hz, Is.EqualTo(c.OpenCutoffHz));
            SoundPaths.HeadShadow(fwd, new Vec3d(1, 0, 0), c, out db, out hz);
            Assert.That(db, Is.EqualTo(0).Within(1e-12), "beside you: none"); Assert.That(hz, Is.EqualTo(c.OpenCutoffHz).Within(1e-9));
            SoundPaths.HeadShadow(fwd, new Vec3d(0, 0, -1), c, out db, out hz);
            Assert.That(db, Is.EqualTo(-c.BehindLossDb)); Assert.That(hz, Is.EqualTo(c.BehindCutoffHz));
            SoundPaths.HeadShadow(fwd, new Vec3d(0, -1, 0), c, out db, out hz);
            Assert.That(db, Is.EqualTo(0), "straight below: no front or back");
        }

        [Test]
        public void Mix_PicksTheLouderWay()
        {
            var c = new SoundConfig();
            var r = new SoundRoute { DirectDistance = 5, AroundFound = true, AroundDistance = 9, Bends = 1, AroundFrom = new Vec3d(1, 0, 0), ThroughPossible = true, ThroughDb = -20, Walls = 1 };
            SoundRoute m = SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), 40, c);
            // Round: -2.5 dB over 9 m = 0.0833; through: -20 dB over 5 m = 0.02. Round wins.
            Assert.That(m.Way, Is.EqualTo(SoundWay.Around));
            Assert.That(m.Apparent, Is.EqualTo(new Vec3d(9, 0, 0)));
            r.AroundDistance = 60;
            Assert.That(SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), 40, c).Way, Is.EqualTo(SoundWay.Through), "round is out of range");
            r.AroundDistance = 30; r.ThroughDb = -6; // 0.5 / 5 = 0.1 vs 0.75 / 30 = 0.025
            m = SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), 40, c);
            Assert.That(m.Way, Is.EqualTo(SoundWay.Through));
            Assert.That(m.Apparent, Is.EqualTo(new Vec3d(0, 0, 5)));
            r.ThroughPossible = false; r.AroundFound = false;
            Assert.That(SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), 40, c).Way, Is.EqualTo(SoundWay.Silent));
            // Two walls: half the cutoff.
            r.ThroughPossible = true; r.Walls = 2; r.ThroughDb = -30;
            Assert.That(SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), 40, c).CutoffHz, Is.EqualTo(c.ThroughCutoffHz / 2));
        }

        [Test]
        public void DuelMap_RandomPairs_StayCheap()
        {
            CompositeTerrain t = DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out BreachMapInfo info));
            var paths = new SoundPaths(t);
            var rng = new Random(7);
            double size = info.SizeX;
            int around = 0, through = 0, direct = 0, silent = 0, n = 300;
            int worst = 0;
            var sw = Stopwatch.StartNew();
            double worstMs = 0;
            for (int i = 0; i < n; i++)
            {
                var a = new Vec3d(size * 0.2 + rng.NextDouble() * size * 0.6, 0.3 + rng.Next(3) * 4.8 + 1.3, size * 0.2 + rng.NextDouble() * size * 0.6);
                double ang = rng.NextDouble() * Math.PI * 2, dist = 3 + rng.NextDouble() * 27;
                var b = new Vec3d(a.X + Math.Cos(ang) * dist, 0.3 + rng.Next(3) * 4.8 + 0.3, a.Z + Math.Sin(ang) * dist);
                long t0 = sw.ElapsedTicks;
                SoundRoute r = paths.Route(a, b, 34);
                double ms = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
                worstMs = Math.Max(worstMs, ms);
                worst = Math.Max(worst, paths.LastExpansions);
                switch (r.Way)
                {
                    case SoundWay.Direct: direct++; break;
                    case SoundWay.Around: around++; break;
                    case SoundWay.Through: through++; break;
                    default: silent++; break;
                }
                Assert.That(r.Distance, Is.GreaterThanOrEqualTo(r.DirectDistance - 1e-6), "never shorter than the straight line");
            }
            double avg = sw.Elapsed.TotalMilliseconds / n;
            TestContext.Out.WriteLine($"duel map, {n} routes: direct {direct}, around {around}, through {through}, silent {silent}; avg {avg:0.000} ms, worst {worstMs:0.00} ms, worst expansions {worst}, faces cached {paths.CachedFaces}, face tests {paths.FaceTests}");
            // The same routes again, faces now known (what play costs once the map is prepared).
            rng = new Random(7);
            sw.Restart();
            double worstWarm = 0;
            for (int i = 0; i < n; i++)
            {
                var a = new Vec3d(size * 0.2 + rng.NextDouble() * size * 0.6, 0.3 + rng.Next(3) * 4.8 + 1.3, size * 0.2 + rng.NextDouble() * size * 0.6);
                double ang = rng.NextDouble() * Math.PI * 2, dist = 3 + rng.NextDouble() * 27;
                var b = new Vec3d(a.X + Math.Cos(ang) * dist, 0.3 + rng.Next(3) * 4.8 + 0.3, a.Z + Math.Sin(ang) * dist);
                long t0 = sw.ElapsedTicks;
                paths.Route(a, b, 34);
                worstWarm = Math.Max(worstWarm, (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
            }
            TestContext.Out.WriteLine($"warm: avg {sw.Elapsed.TotalMilliseconds / n:0.000} ms, worst {worstWarm:0.00} ms");
            var fresh = new SoundPaths(t);
            sw.Restart();
            int tested = fresh.Prepare(new Aabb(new Vec3d(0, -0.5, 0), new Vec3d(size, 15, size)));
            TestContext.Out.WriteLine($"prepare whole map: {tested} faces tested in {sw.Elapsed.TotalMilliseconds:0} ms");
            Assert.That(worst, Is.LessThanOrEqualTo(paths.Config.MaxExpansions + 1));
            Assert.That(around, Is.GreaterThan(0));
        }
    }
}
