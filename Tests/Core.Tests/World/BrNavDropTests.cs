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
            ITerrain t = BrMap.Terrain(BrMap.BuildGround(), BrMap.BuildBrushes(out _, out _));
            var path = new List<int>();
            // First floors of the big POI halls (both halves), the outpost and the farmhouse, where bots were found stuck.
            var upstairs = new[] { new Vec3d(682.5, 4.8, 653.2), new Vec3d(651.5, 4.8, 681.9), new Vec3d(693.5, 4.8, 406.8), new Vec3d(542.5, 4.8, 319.6) };
            foreach (Vec3d at in upstairs)
            {
                int a = n.Nearest(at);
                Assert.That(a, Is.GreaterThanOrEqualTo(0), $"no walking spot at {at}");
                Assert.That(n.Feet(a).Y, Is.EqualTo(4.8).Within(0.7), $"{at} is upstairs");
                // Somewhere on the ground 40 m off (not every spot is reachable: sealed rooms are only for breakers).
                bool found = false;
                foreach (var off in new[] { new Vec3d(40, 0, 0), new Vec3d(-40, 0, 0), new Vec3d(0, 0, 40), new Vec3d(0, 0, -40) })
                {
                    int b = n.Nearest(new Vec3d(at.X + off.X, 0, at.Z + off.Z), 8);
                    if (b >= 0 && n.Feet(b).Y < 0.5 && n.FindPath(a, b, path)) { found = true; break; }
                }
                Assert.That(found, Is.True, $"no way down from {at}");
                bool down = false;
                for (int i = 1; i < path.Count; i++)
                {
                    Vec3d p = n.Feet(path[i - 1]), q = n.Feet(path[i]);
                    if (q.Y < 0.5) down = true;
                    if (n.LinkBetween(path[i - 1], path[i]) != BotNav.Link.Drop) continue;
                    TerrainHit h = t.Raycast(new Vec3d(q.X, p.Y + 0.3, q.Z), new Vec3d(0, -1, 0), p.Y + 0.3 - q.Y + 0.2);
                    Assert.That(h.Hit && h.Point.Y <= q.Y + 0.2, Is.True, $"drop {p} -> {q} lands on something at {h.Point.Y:0.00} first");
                }
                Assert.That(down, Is.True, $"the path from {at} reaches the ground");
            }
        }
    }
}
