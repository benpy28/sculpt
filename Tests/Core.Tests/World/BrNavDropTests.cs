using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Bots on the battle royale map can get down from every first floor. The walking grid used to link a first floor
    /// to the ground under its own slab ("drop a storey" with the slab in the way): bots upstairs stood against the floor
    /// until the circle killed them (2026-11-27, half the deaths in a 32-bot match).
    /// </summary>
    public class BrNavDropTests
    {
        [Test]
        public void EveryDropOnTheWayDownFromAFirstFloor_FallsThroughOpenAir()
        {
            BotNav n = BrMap.BuildNav(1.0);
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out _);
            ITerrain t = BrMap.Terrain(BrMap.BuildGround(), w);
            var path = new List<int>();
            // First floors of two-storey buildings (spawn regions and houses), where bots were found stuck.
            int tried = 0;
            for (int k = 0; k < info.Block.Buildings.Count && tried < 4; k++)
            {
                string name = info.Block.Names[k];
                if (name != "Spawn region" && name != "House") continue;
                Aabb b = info.Block.Buildings[k];
                double floor = b.Min.Y + GreyboxBlock.StoreyHeight, ground = b.Min.Y;
                int a = -1;
                for (double z = b.Min.Z + 3; z < b.Max.Z - 3 && a < 0; z += 2)
                    for (double x = b.Min.X + 3; x < b.Max.X - 3 && a < 0; x += 2)
                    {
                        int c = n.Nearest(new Vec3d(x, floor, z), 1);
                        if (c >= 0 && Math.Abs(n.Feet(c).Y - floor) < 0.3) a = c;
                    }
                Assert.That(a, Is.GreaterThanOrEqualTo(0), $"no walking spot upstairs in the {name} at {b.Min}");
                tried++;
                Vec3d at = n.Feet(a);
                // Somewhere on the ground 40 m off (not every spot is reachable: sealed rooms are only for breakers).
                bool found = false;
                foreach (var off in new[] { new Vec3d(40, 0, 0), new Vec3d(-40, 0, 0), new Vec3d(0, 0, 40), new Vec3d(0, 0, -40) })
                {
                    int e = n.Nearest(new Vec3d(at.X + off.X, ground, at.Z + off.Z), 8);
                    if (e >= 0 && n.Feet(e).Y < floor - 2 && n.FindPath(a, e, path)) { found = true; break; }
                }
                Assert.That(found, Is.True, $"no way down from {at}");
                bool down = false;
                for (int i = 1; i < path.Count; i++)
                {
                    Vec3d p = n.Feet(path[i - 1]), q = n.Feet(path[i]);
                    if (q.Y < floor - 2) down = true;
                    if (n.LinkBetween(path[i - 1], path[i]) != BotNav.Link.Drop) continue;
                    TerrainHit h = t.Raycast(new Vec3d(q.X, p.Y + 0.3, q.Z), new Vec3d(0, -1, 0), p.Y + 0.3 - q.Y + 0.2);
                    Assert.That(h.Hit && h.Point.Y <= q.Y + 0.2, Is.True, $"drop {p} -> {q} lands on something at {h.Point.Y:0.00} first");
                }
                Assert.That(down, Is.True, $"the path from {at} reaches the ground");
            }
            Assert.That(tried, Is.EqualTo(4));
        }
    }
}
