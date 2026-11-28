using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The South East segment (the Quarry) keeps its promises: the pit's floor and every bench can be walked to from the
    /// rim on the ground alone (the haul ramps), the rock faces are too tall to climb, the sealed rooms really are sealed,
    /// every tower's stairs reach its roof, and the conveyor gallery is a clear walkway from the hopper to the crusher tower.
    /// </summary>
    public class SouthEastTests
    {
        private const int Id = 2;
        private static BrMap.TemplateBuild T => BrMap.Template;
        private static BrBuild B => T.Segments[Id];
        private static readonly MovementConfig C = new MovementConfig();
        private const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column;

        // ---------------------------------------------------------------- the pit

        [Test]
        public void ThePitFloorAndEveryBench_CanBeWalkedToFromTheRim_OnTheGroundAlone()
        {
            HeightField f = BrMap.Field;
            Rect2 seg = BrLayout.Bounds(Id);
            double cell = f.Cell;
            int I(double x) => (int)Math.Round((x - f.VertexX(0)) / cell);
            int J(double z) => (int)Math.Round((z - f.VertexZ(0)) / cell);
            int i0 = I(seg.X0), i1 = I(seg.X1), j0 = J(seg.Z0), j1 = J(seg.Z1);
            // A step between neighbouring vertices is walkable if it is no steeper than 35 degrees.
            double maxStep = cell * Math.Tan(35 * Math.PI / 180);
            var seen = new HashSet<long>();
            var queue = new Queue<(int, int)>();
            (int si, int sj) = (I(1050), J(290)); // the rim road north of the pit
            Assert.That(f[si, sj], Is.EqualTo(BrGround.Base).Within(1e-6), "the rim road is at the base level");
            queue.Enqueue((si, sj));
            seen.Add(((long)si << 32) | (uint)sj);
            var levels = new HashSet<int>();
            while (queue.Count > 0)
            {
                (int i, int j) = queue.Dequeue();
                double h = f[i, j];
                for (int k = 0; k <= 4; k++)
                    if (Math.Abs(h - SouthEastSegment.Quarry.Level(k)) < 1e-6 && SouthEastSegment.Quarry.Sdf(f.VertexX(i), f.VertexZ(j)) < SouthEastSegment.Quarry.Lip(3)) levels.Add(k);
                foreach ((int di, int dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < i0 || nj < j0 || ni > i1 || nj > j1) continue;
                    if (Math.Abs(f[ni, nj] - h) > maxStep) continue;
                    if (seen.Add(((long)ni << 32) | (uint)nj)) queue.Enqueue((ni, nj));
                }
            }
            for (int k = 0; k < 4; k++) Assert.That(levels.Contains(k), Is.True, $"level {k} ({SouthEastSegment.Quarry.Level(k)} m) can be walked to from the rim");
            double cx = SouthEastSegment.Quarry.Cx, cz = SouthEastSegment.Quarry.Cz;
            Assert.That(seen.Contains(((long)I(cx) << 32) | (uint)J(cz)), Is.True, "the middle of the pit floor");
        }

        [Test]
        public void TheRockFaces_AreTooTallToClimb_AndTheFloorIsDeep()
        {
            Assert.That(SouthEastSegment.Quarry.Rise, Is.GreaterThan(C.MantleMaxHeight + 0.5), "a face is taller than any ledge climb");
            HeightField f = BrMap.Field;
            Assert.That(f.HeightAt(SouthEastSegment.Quarry.Cx, SouthEastSegment.Quarry.Cz), Is.EqualTo(SouthEastSegment.Quarry.Floor).Within(1e-6));
            Assert.That(SouthEastSegment.Quarry.Floor, Is.GreaterThanOrEqualTo(BrGround.Lowest));
            // Walking straight out from the floor on each side, where no ramp runs, meets four faces, each a full rise
            // over less than two height-field cells (a cliff, not a slope).
            foreach ((double dx, double dz, double ox, double oz) in new[] { (-1.0, 0.0, 0.0, 21.0), (0.0, -1.0, -2.0, 0.0), (1.0, 0.0, 0.0, -20.0), (0.0, 1.0, -10.0, 0.0) })
            {
                int faces = 0;
                double x0 = SouthEastSegment.Quarry.Cx + ox, z0 = SouthEastSegment.Quarry.Cz + oz;
                double prev = f.HeightAt(x0, z0), streakFrom = prev;
                bool rising = false;
                for (double s = 0; s < 100; s += 0.25)
                {
                    double h = f.HeightAt(x0 + dx * s, z0 + dz * s);
                    bool up = h - prev > 0.3;
                    if (up && !rising) streakFrom = prev;
                    if (!up && rising && prev - streakFrom > SouthEastSegment.Quarry.Rise - 0.01) faces++;
                    rising = up;
                    prev = h;
                }
                if (rising && prev - streakFrom > SouthEastSegment.Quarry.Rise - 0.01) faces++;
                Assert.That(faces, Is.EqualTo(4), $"four rock faces heading ({dx}, {dz})");
            }
        }

        [Test]
        public void NoFootingsBankReachesIntoThePit()
        {
            // The pit as built is exactly its natural ground: no building's footing fills a face or cuts a bench.
            HeightField f = BrMap.Field;
            int n = 0;
            for (int j = 0; j <= f.NZ; j++)
                for (int i = 0; i <= f.NX; i++)
                {
                    double x = f.VertexX(i), z = f.VertexZ(j);
                    if (SouthEastSegment.Quarry.Sdf(x, z) > SouthEastSegment.Quarry.Lip(3) + 2) continue;
                    Assert.That(f[i, j], Is.EqualTo(BrGround.Natural(x, z)).Within(1e-4), $"the pit's ground at {x:0.0},{z:0.0} was changed by a footing");
                    n++;
                }
            Assert.That(n, Is.GreaterThan(10000));
        }

        // ---------------------------------------------------------------- sealed rooms

        /// <summary>Buildings that are one sealed room (no door, no window).</summary>
        private static readonly string[] SealedBuildings =
        {
            "Strongroom", "Pump house", "Powder shed", "Tool shed", "Fuel shed", "Drill shed", "Winch shed", "Lamp shed",
            "Magazine bunker", "Pay office", "Compressor house",
        };

        /// <summary>Sealed rooms inside buildings: the building, its ground-floor cells.</summary>
        private static readonly (string name, (int i, int j)[] cells)[] SealedRooms =
        {
            ("Site office", new[] { (0, 4), (0, 3) }), ("Canteen", new[] { (0, 0) }), ("Weighbridge hall", new[] { (0, 0) }),
            ("Screening plant", new[] { (0, 2), (1, 2) }), ("Workshop", new[] { (2, 5), (2, 4) }), ("Miners' institute", new[] { (2, 3), (2, 2) }),
            ("Boarding house", new[] { (2, 0) }), ("Chapel", new[] { (1, 1) }),
        };

        [Test]
        public void TheSealedRooms_HaveNoWayIn()
        {
            int n = 0;
            foreach (BrBuilding bd in B.Buildings)
            {
                if (Array.IndexOf(SealedBuildings, bd.Name) >= 0)
                {
                    var r = new Rect2(bd.Bounds.Min.X + Col, bd.Bounds.Min.Z + Col, bd.Bounds.Max.X - Col, bd.Bounds.Max.Z - Col);
                    Assert.That(Escapes(new Vec3d(r.CX, bd.Bounds.Min.Y, r.CZ), r), Is.False, $"{bd.Name} at {bd.Bounds.Min} can be walked into");
                    n++;
                }
                foreach (var room in SealedRooms)
                {
                    if (room.name != bd.Name) continue;
                    double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
                    foreach ((int i, int j) in room.cells)
                    {
                        x0 = Math.Min(x0, bd.Bounds.Min.X + i * M + Col); z0 = Math.Min(z0, bd.Bounds.Min.Z + j * M + Col);
                        x1 = Math.Max(x1, bd.Bounds.Min.X + (i + 1) * M); z1 = Math.Max(z1, bd.Bounds.Min.Z + (j + 1) * M);
                    }
                    var r = new Rect2(x0, z0, x1, z1);
                    (int ci, int cj) = room.cells[0];
                    var start = new Vec3d(bd.Bounds.Min.X + ci * M + Col + 3, bd.Bounds.Min.Y, bd.Bounds.Min.Z + cj * M + Col + 3);
                    Assert.That(Escapes(start, r), Is.False, $"the sealed room in {bd.Name} at {bd.Bounds.Min} can be walked into");
                    n++;
                }
            }
            Assert.That(n, Is.GreaterThanOrEqualTo(SealedBuildings.Length + SealedRooms.Length), "every sealed room was found");
        }

        [Test]
        public void TheFloodFill_FindsAWayOutOfARoomWithADoor()
        {
            // The check above would pass for anything if the flood never moved: a room with a shut door's leaves is
            // closed, but the canteen's main room has a window and its door, and the site hut is open.
            foreach (BrBuilding bd in B.Buildings)
            {
                if (bd.Name != "Dumper shelter") continue;
                var r = new Rect2(bd.Bounds.Min.X + Col, bd.Bounds.Min.Z + Col, bd.Bounds.Max.X - Col, bd.Bounds.Max.Z - Col);
                Assert.That(Escapes(new Vec3d(r.CX, bd.Bounds.Min.Y, r.CZ), r), Is.True, "the open dumper shelter has a way out");
                return;
            }
            Assert.Fail("no dumper shelter");
        }

        /// <summary>True if a crouched player (or one vaulting a sill: a box at window height) can get from <paramref name="start"/> out of <paramref name="room"/>.</summary>
        private static bool Escapes(Vec3d start, Rect2 room)
        {
            BrushWorld w = T.World;
            const double step = 0.25, half = 0.35;
            Rect2 area = room.Grow(1.5);
            double y = start.Y;
            bool Clear(double x, double z, double lo, double hi) =>
                !w.BoxOverlapsSolid(new Aabb(new Vec3d(x - half, y + lo, z - half), new Vec3d(x + half, y + hi, z + half)));
            bool Open(double x, double z) => Clear(x, z, 0.05, 1.1) || Clear(x, z, 1.55, 2.65);
            int nx = (int)Math.Ceiling((area.X1 - area.X0) / step), nz = (int)Math.Ceiling((area.Z1 - area.Z0) / step);
            int si = (int)((start.X - area.X0) / step), sj = (int)((start.Z - area.Z0) / step);
            var seen = new bool[nx + 1, nz + 1];
            var q = new Queue<(int, int)>();
            q.Enqueue((si, sj));
            seen[si, sj] = true;
            Assert.That(Open(area.X0 + si * step, area.Z0 + sj * step), Is.True, $"the flood starts in the clear at {start}");
            while (q.Count > 0)
            {
                (int i, int j) = q.Dequeue();
                double x = area.X0 + i * step, z = area.Z0 + j * step;
                if (!room.Contains(x, z)) return true;
                foreach ((int di, int dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni > nx || nj > nz || seen[ni, nj]) continue;
                    seen[ni, nj] = true;
                    if (Open(area.X0 + ni * step, area.Z0 + nj * step)) q.Enqueue((ni, nj));
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- towers and the gallery

        [Test]
        public void EveryTowersStairs_ReachItsRoof()
        {
            int towers = 0;
            foreach (BrBuilding bd in B.Buildings)
            {
                if (bd.Name != "Crusher tower" && bd.Name != "Silo tower" && bd.Name != "Loading hopper" && bd.Name != "Site office") continue;
                towers++;
                int storeys = (int)Math.Round((bd.Bounds.Max.Y - bd.Bounds.Min.Y) / GreyboxBlock.StoreyHeight);
                for (int s = 0; s < storeys; s++)
                {
                    double top = bd.Bounds.Min.Y + (s + 1) * GreyboxBlock.StoreyHeight;
                    bool found = false;
                    foreach (Brush c in T.Info.Block.Climbs)
                        if (Math.Abs(c.Bounds.Max.Y - top) < 1e-6 && Math.Abs(c.Bounds.Min.Y - (top - GreyboxBlock.StoreyHeight)) < 1e-6 &&
                            c.Bounds.Min.X >= bd.Bounds.Min.X && c.Bounds.Max.X <= bd.Bounds.Max.X && c.Bounds.Min.Z >= bd.Bounds.Min.Z && c.Bounds.Max.Z <= bd.Bounds.Max.Z)
                            found = true;
                    Assert.That(found, Is.True, $"{bd.Name}: a flight up from storey {s}");
                }
                // A parapet stands on the roof.
                var onRoof = new Aabb(new Vec3d(bd.Bounds.Min.X, bd.Bounds.Max.Y + 0.1, bd.Bounds.Min.Z), new Vec3d(bd.Bounds.Max.X, bd.Bounds.Max.Y + 1.4, bd.Bounds.Max.Z));
                Assert.That(T.World.BoxOverlapsSolid(onRoof), Is.True, $"{bd.Name} has a parapet");
            }
            Assert.That(towers, Is.EqualTo(4));
        }

        [Test]
        public void TheGallery_IsAClearWalkwayFromTheHopperToTheCrusherTower()
        {
            BrBuilding tower = Find("Crusher tower"), hopper = Find("Loading hopper");
            double y = hopper.Bounds.Min.Y + 3 * GreyboxBlock.StoreyHeight;
            Assert.That(tower.Bounds.Min.Y + 3 * GreyboxBlock.StoreyHeight, Is.EqualTo(y).Within(1e-6), "both third floors are level");
            double x = tower.Bounds.Min.X + 2 * M + Col + 3.0; // the middle of segment 2
            double z0 = hopper.Bounds.Max.Z - 2.0, z1 = tower.Bounds.Min.Z + 2.0; // from inside the hopper to inside the tower
            double h = C.HalfWidth;
            for (double z = z0; z <= z1; z += 0.5)
            {
                var player = new Aabb(new Vec3d(x - h, y + 0.02, z - h), new Vec3d(x + h, y + C.StandHeight, z + h));
                Assert.That(T.World.BoxOverlapsSolid(player), Is.False, $"the gallery is blocked at z {z:0.0}");
                var under = new Aabb(new Vec3d(x - 0.1, y - 0.2, z - 0.1), new Vec3d(x + 0.1, y - 0.05, z + 0.1));
                Assert.That(T.World.BoxOverlapsSolid(under), Is.True, $"nothing to stand on at z {z:0.0}");
            }
            Assert.That(z1 - z0, Is.GreaterThan(180), "it crosses the whole pit");
        }

        [Test]
        public void TheHomePoi_IsTheQuarryOffices_WithAllTenChests()
        {
            Vec3d a = BrLayout.Anchor(Id);
            int n = 0;
            foreach (BrChestSpot c in B.Chests) if ((c.Position - a).HorizontalLength <= BrLayout.HomeRadius) n++;
            Assert.That(n, Is.EqualTo(BrLayout.HomeChests));
            Assert.That(B.Pois.Exists(p => p.Name == "Quarry Offices"), Is.True);
        }

        private static BrBuilding Find(string name)
        {
            foreach (BrBuilding bd in B.Buildings) if (bd.Name == name) return bd;
            Assert.Fail($"no {name}");
            return default;
        }
    }
}
