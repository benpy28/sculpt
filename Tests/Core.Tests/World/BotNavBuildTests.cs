using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The bot walking grid is probed on all cores and stops at flat ground: it must come out exactly as the plain
    /// one-core probe through the ground does (same spots in the same order, same edges).
    /// </summary>
    public class BotNavBuildTests
    {
        private static void AssertSame(BotNav a, BotNav b)
        {
            Assert.That(a.Nodes.Count, Is.EqualTo(b.Nodes.Count), "spots");
            Assert.That(a.EdgeCount, Is.EqualTo(b.EdgeCount), "edges");
            for (int i = 0; i < a.Nodes.Count; i++)
            {
                BotNav.Node x = a.Nodes[i], y = b.Nodes[i];
                Assert.That((x.Cx, x.Cz, x.Open), Is.EqualTo((y.Cx, y.Cz, y.Open)), $"spot {i}");
                Assert.That(x.Y, Is.EqualTo(y.Y).Within(1e-9), $"spot {i}"); // a ray from another height rounds its last bit differently
            }
            // Edges by what joins what: every neighbour pair has the same link kind both ways round.
            for (int i = 0; i < a.Nodes.Count; i += 97)
                for (int j = System.Math.Max(0, i - 3); j < System.Math.Min(a.Nodes.Count, i + 4); j++)
                    Assert.That(a.LinkBetween(i, j), Is.EqualTo(b.LinkBetween(i, j)));
        }

        [Test]
        public void TheDuelGrid_IsTheSameEveryBuild()
        {
            BotNav a = DuelMap.BuildNav(), b = DuelMap.BuildNav();
            AssertSame(a, b);
        }

        [Test]
        public void StartingColumnsJustOverEmptyGround_GivesTheSameGrid()
        {
            // A part of the battle royale map (a spawn region, the halls, hills, villages, props), with and without the
            // short start over empty ground.
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            SubGrid g = BrMap.BuildGround();
            ITerrain t = BrMap.Terrain(g, w);
            double x0 = br.Centre.X + 390, z0 = br.Centre.Z + 200;
            BotNav fast = BotNav.Build(t, x0, z0, 160, 120, 1.0, topY: BrMap.NavTop, floorY: BrGround.Lowest - 2, columnTop: BrMap.NavColumnTop(w, g));
            BotNav slow = BotNav.Build(t, x0, z0, 160, 120, 1.0, topY: BrMap.NavTop, floorY: BrGround.Lowest - 2);
            Assert.That(fast.Nodes.Count, Is.GreaterThan(15000));
            AssertSame(fast, slow);
        }

        [Test]
        public void EveryFloorOfATallTower_IsFound_DownToTheGround()
        {
            // Ground plus 18 slabs 4.8 m apart (a taller stack than the 12-storey landmark): every one is a floor.
            var w = new BrushWorld();
            w.AddBox(new Vec3d(-5, -1, -5), new Vec3d(5, 0, 5), BrushKind.Ground);
            const int slabs = 18;
            for (int k = 1; k <= slabs; k++) w.AddBox(new Vec3d(-5, k * 4.8 - 0.3, -5), new Vec3d(5, k * 4.8, 5), BrushKind.Floor);
            BotNav n = BotNav.Build(w, -2, -2, 4, 4, 1.0, topY: slabs * 4.8 + 5, floorY: -2);
            var levels = new HashSet<double>();
            foreach (BotNav.Node node in n.Nodes) levels.Add(Math.Round(node.Y, 2));
            Assert.That(levels.Count, Is.EqualTo(slabs + 1), "the ground and every slab");
        }

        [Test]
        public void AFloorUnderATallSolidWall_IsFound()
        {
            // A doorway 3 m high under 18 m of solid wall: the column through the wall still finds the floor under it.
            var w = new BrushWorld();
            w.AddBox(new Vec3d(-5, -1, -5), new Vec3d(5, 0, 5), BrushKind.Ground);
            w.AddBox(new Vec3d(-5, 3, -0.3), new Vec3d(5, 21, 0.3), BrushKind.Wall);
            BotNav n = BotNav.Build(w, -2, -2, 4, 4, 0.5, topY: 25, floorY: -2);
            bool underWall = false;
            for (int k = 0; k < n.Nodes.Count; k++)
            {
                Vec3d f = n.Feet(k);
                if (Math.Abs(f.Z) < 0.3 && Math.Abs(f.Y) < 0.01) underWall = true;
            }
            Assert.That(underWall, Is.True, "a walking spot in the doorway under the wall");
        }
    }
}
