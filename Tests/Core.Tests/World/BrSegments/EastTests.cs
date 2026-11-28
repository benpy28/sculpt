using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using static Sculpt.Core.EastPlan;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The East segment (Dam and reservoir valley): its own promises. The gorge is cut exactly where the dam stands (one
    /// cell rock walls, level floors), the crest is a walk from one plateau to the other, the dam's third storey opens onto
    /// the reservoir bed and the power house's roof, no crack beside the dam, the spill chute is a slide, sealed rooms are
    /// sealed and the tower roofs meet the plateau.
    /// </summary>
    public class EastTests
    {
        private static ITerrain _t;
        private static ITerrain T => _t ??= new CompositeTerrain(BrMap.Field, BrMap.Template.World);
        private static HeightField F => BrMap.Field;
        private static readonly MovementConfig C = new MovementConfig();

        private static bool Free(Vec3d feet, double half = -1)
        {
            if (half < 0) half = C.HalfWidth;
            return !T.BoxOverlapsSolid(new Aabb(feet + new Vec3d(-half, 0.02, -half), feet + new Vec3d(half, C.StandHeight, half)));
        }

        /// <summary>Something solid within 5 cm under the feet.</summary>
        private static bool Supported(Vec3d feet) =>
            T.BoxOverlapsSolid(new Aabb(feet + new Vec3d(-C.HalfWidth, -0.05, -C.HalfWidth), feet + new Vec3d(C.HalfWidth, 0.0, C.HalfWidth)));

        [Test]
        public void TheGorge_IsCutExactly_LevelFloorsBetweenOneCellRockWalls()
        {
            // Under the dam and in the canyon: the canyon floor; in the throat: the bed; on the plateaus: the crest's level.
            for (double z = DamZ0; z <= DamZ1 + 1e-6; z += 1.5)
            {
                Assert.That(F.HeightAt(DamX0 + 0.1, z), Is.EqualTo(Floor).Within(1e-4), $"under the dam at z {z}");
                Assert.That(F.HeightAt(DamX1 + 20, z), Is.EqualTo(Floor).Within(1e-4), $"the canyon at z {z}");
                Assert.That(F.HeightAt(DamX0 - 1.6, z), Is.EqualTo(Bed).Within(1e-4), $"the throat's bed at z {z}");
            }
            for (double x = PlateauX0; x <= 1222; x += 3)
            {
                Assert.That(F.HeightAt(x, GorgeZ0 - 1.5), Is.EqualTo(Crest).Within(1e-4), $"south rim at x {x}");
                Assert.That(F.HeightAt(x, GorgeZ1 + 1.5), Is.EqualTo(Crest).Within(1e-4), $"north rim at x {x}");
                // The rock walls take one cell: steep, not a walkable bank.
                Assert.That(F.NormalAt(x, GorgeZ0 - 0.75).Y, Is.LessThan(F.WalkableNormalY), $"south wall at x {x}");
                Assert.That(F.NormalAt(x, GorgeZ1 + 0.75).Y, Is.LessThan(F.WalkableNormalY), $"north wall at x {x}");
            }
        }

        [Test]
        public void TheCrest_IsAWalk_FromTheSouthPlateauOverTheDamToTheNorthPlateau()
        {
            double x = (DamX0 + DamX1) / 2;
            for (double z = GorgeZ0 - 6; z <= GorgeZ1 + 8; z += 0.25)
            {
                var feet = new Vec3d(x, Crest, z);
                Assert.That(Free(feet), Is.True, $"the crest road is blocked at z {z}");
                Assert.That(Supported(feet), Is.True, $"nothing to stand on along the crest road at z {z}");
            }
        }

        [Test]
        public void TheIntakeTower_IsBridgedToTheCrest()
        {
            double z = (EastDam.IntakeBridgeZ0 + EastDam.IntakeBridgeZ1) / 2;
            for (double x = DamX0 - 6; x <= DamX0 + 4; x += 0.25)
            {
                var feet = new Vec3d(x, Crest, z);
                Assert.That(Free(feet) && Supported(feet), Is.True, $"the intake bridge at x {x}");
            }
        }

        [Test]
        public void TheDamsThirdStorey_OpensOntoTheBed_AndThePowerHouseRoof()
        {
            // Out of the upstream wide door onto the bed, and out of the downstream doors onto the power house's roof.
            double zw = DamZ0 + 7 * EastBuild.M + EastBuild.Col + 3;
            foreach (double x in new[] { DamX0 - 3, DamX0 + 3 })
            {
                var feet = new Vec3d(x, Bed, zw);
                Assert.That(Free(feet) && Supported(feet), Is.True, $"at the upstream wide door, x {x}");
            }
            foreach (int j in new[] { 3, 5 })
            {
                double z = DamZ0 + j * EastBuild.M + EastBuild.Col + 3;
                foreach (double x in new[] { DamX1 - 3, DamX1 + 3 })
                {
                    var feet = new Vec3d(x, Bed, z);
                    Assert.That(Free(feet, 0.3) && Supported(feet), Is.True, $"at the downstream door {j}, x {x}");
                }
            }
        }

        [Test]
        public void NoCrackBesideTheDam_NobodyFallsBetweenItAndTheRock()
        {
            // The one-cell gaps between the dam and the rock are filled: standing there you are on something at the
            // level of the ground beside it.
            for (double x = DamX0 + 0.5; x < DamX1; x += 1)
                Assert.That(Supported(new Vec3d(x, Crest, DamZ1 + 0.75)), Is.True, $"north end at x {x}");
            for (double z = DamZ0 + 0.5; z < DamZ1; z += 1)
                Assert.That(Supported(new Vec3d(DamX0 - 0.75, Bed, z)), Is.True, $"upstream face at z {z}");
            for (double x = DamX0 + 0.5; x < DamX1; x += 1)
            {
                double y = Chute(x);
                Assert.That(Supported(new Vec3d(x, y, DamZ0 - 0.6)), Is.True, $"chute side at x {x}");
            }
        }

        [Test]
        public void TheSpillChute_RunsDownFromTheBedToTheCanyon_AsASlide()
        {
            double z = (GorgeZ0 + ChuteZ1) / 2, prev = double.PositiveInfinity;
            for (double x = ChuteX0 - 6; x <= DamX1 + 10; x += 0.5)
            {
                double h = F.HeightAt(x, z);
                Assert.That(h, Is.LessThanOrEqualTo(prev + 1e-6), $"the chute rises at x {x}");
                Assert.That(F.NormalAt(x, z).Y, Is.GreaterThan(Math.Cos(20 * Math.PI / 180)), $"the chute is too steep at x {x}");
                Assert.That(Free(new Vec3d(x, h + 0.3, z)), Is.True, $"the chute is blocked at x {x}");
                prev = h;
            }
            Assert.That(F.HeightAt(ChuteX0 - 6, z), Is.EqualTo(Bed).Within(1e-4));
            Assert.That(F.HeightAt(DamX1 + 10, z), Is.EqualTo(Floor).Within(1e-4));
        }

        [Test]
        public void TheTowersRoofs_MeetThePlateaus()
        {
            // The canyon tower's roof steps out onto the north plateau; the dam's and the intake tower's roofs are at the
            // crest's level; the power house's roof at the bed's.
            BrBuild b = BrMap.Template.Segments[5];
            bool Has(string name, Func<BrBuilding, bool> ok)
            {
                foreach (BrBuilding k in b.Buildings) if (k.Name == name && ok(k)) return true;
                return false;
            }
            Assert.That(Has("Greywater Dam", k => Math.Abs(k.Bounds.Max.Y - Crest) < 1e-6 && Math.Abs(k.Bounds.Min.Y - Floor) < 1e-6), Is.True);
            Assert.That(Has("Intake tower", k => Math.Abs(k.Bounds.Max.Y - Crest) < 1e-6), Is.True);
            Assert.That(Has("Power house", k => Math.Abs(k.Bounds.Max.Y - Bed) < 1e-6), Is.True);
            Assert.That(Has("Canyon tower", k => Math.Abs(k.Bounds.Max.Y - Crest) < 1e-6 && Math.Abs(k.Bounds.Max.Z - GorgeZ1) < 1e-6), Is.True);
            for (double x = 1199; x <= 1206; x += 1)
            {
                var feet = new Vec3d(x, Crest, GorgeZ1 - 1.5);
                Assert.That(Free(feet) && Supported(feet), Is.True, $"on the canyon tower's roof at x {x}");
                feet = new Vec3d(x, Crest, GorgeZ1 + 2);
                Assert.That(Free(feet) && Supported(feet), Is.True, $"stepping off it onto the plateau at x {x}");
            }
        }

        [Test]
        public void SealedRooms_HaveNoWayIn()
        {
            // A walk on a 0.4 m grid from a chest in each sealed room, at standing height, never gets out.
            const double M = EastBuild.M, Col = EastBuild.Col;
            var rooms = new List<(string, Vec3d)>
            {
                ("dam vault", new Vec3d(DamX0 + M + Col + 3, Floor, DamZ0 + 6 * M + Col + 3)),
                ("power house office", new Vec3d(PowerX0 + 5 * M + Col + 3, Floor, PowerZ0 + 4 * M + Col + 3)),
                ("valve house", new Vec3d(1140.0 + Col + 3, Crest, 724.5 + Col + 3)),
                ("canyon shed", new Vec3d(1181.1 + Col + 3, Floor, 749.4 + Col + 3)),
            };
            foreach (var (name, at) in rooms)
                Assert.That(Escapes(at, 14), Is.False, $"the {name} has a way in");
            // And a room that is open does let you out (the walk works).
            Assert.That(Escapes(new Vec3d(DamX0 + 3, Floor, DamZ0 + 3), 14), Is.True, "the dam's south gallery leads out");
        }

        /// <summary>True if a walk on a 0.4 m grid at the level of <paramref name="from"/> gets farther than <paramref name="reach"/> from it.</summary>
        private static bool Escapes(Vec3d from, double reach)
        {
            const double g = 0.4;
            int n = (int)Math.Ceiling(reach / g) + 2;
            var seen = new HashSet<(int, int)>();
            var todo = new Queue<(int, int)>();
            todo.Enqueue((0, 0));
            seen.Add((0, 0));
            while (todo.Count > 0)
            {
                var (i, j) = todo.Dequeue();
                if (Math.Abs(i) >= n - 1 || Math.Abs(j) >= n - 1) return true;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var c = (i + di, j + dj);
                    if (seen.Contains(c)) continue;
                    var p = new Vec3d(from.X + c.Item1 * g, from.Y, from.Z + c.Item2 * g);
                    if (T.BoxOverlapsSolid(new Aabb(p + new Vec3d(-g / 2, 0.3, -g / 2), p + new Vec3d(g / 2, 1.7, g / 2)))) continue;
                    seen.Add(c);
                    todo.Enqueue(c);
                }
            }
            return false;
        }

        [Test, Explicit("dev: prints the props East asked for that did not fit")]
        public void PrintDroppedProps()
        {
            foreach (PropRequest q in BrNature.GetPlan().Dropped)
                if (q.Segment == 5) TestContext.WriteLine($"dropped {q.Shape} at {q.X:0.0}, {q.Z:0.0}");
        }

        [Test, Explicit("dev: prints the natural ground")]
        public void PrintGround()
        {
            var sb = new StringBuilder();
            for (double z = 860; z >= 440; z -= 15)
            {
                sb.Append($"{z,4:0} ");
                for (double x = 880; x <= 1225; x += 15) sb.Append($"{BrGround.Natural(x, z),5:0.0}");
                sb.AppendLine();
            }
            sb.Append("     ");
            for (double x = 880; x <= 1225; x += 15) sb.Append($"{x,5:0}");
            TestContext.WriteLine(sb.ToString());
        }
    }
}
