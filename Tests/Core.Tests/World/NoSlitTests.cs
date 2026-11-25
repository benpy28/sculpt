using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Ben: holes must never be thin slits you can be shot through. Found by the 2026-11-25 map screenshot sweep: every
    /// stairwell's outer wall had a 0.3 m open slit at slab height, because the slab's beam strip was left out where
    /// the slab is open (no floor on either side). Here: around every building, wherever the wall is solid just under
    /// and just over a slab, the slab line itself must be solid too.
    /// </summary>
    public class NoSlitTests
    {
        private static void Check(BrushWorld w, GreyboxInfo b)
        {
            int checkedPoints = 0;
            foreach (Aabb bd in b.Buildings)
                for (int side = 0; side < 4; side++)
                {
                    bool alongX = side < 2;
                    double len = alongX ? bd.Max.X - bd.Min.X : bd.Max.Z - bd.Min.Z;
                    Vec3d outDir = side == 0 ? new Vec3d(0, 0, -1) : side == 1 ? new Vec3d(0, 0, 1) : side == 2 ? new Vec3d(-1, 0, 0) : new Vec3d(1, 0, 0);
                    for (double u = 0.25; u < len; u += 0.25)
                        for (double slab = GreyboxBlock.StoreyHeight; slab < bd.Max.Y - 0.1; slab += GreyboxBlock.StoreyHeight)
                        {
                            double x = alongX ? bd.Min.X + u : side == 2 ? bd.Min.X + 0.6 : bd.Max.X - 0.6;
                            double z = !alongX ? bd.Min.Z + u : side == 0 ? bd.Min.Z + 0.6 : bd.Max.Z - 0.6;
                            bool Hits(double y) => w.Raycast(new Vec3d(x, y, z), outDir, 1.2).Hit;
                            if (!Hits(slab - 0.45) || !Hits(slab + 0.25)) continue; // not wall both under and over
                            checkedPoints++;
                            Assert.That(Hits(slab - 0.15), Is.True, $"slit through the wall at ({x:0.00}, {slab - 0.15:0.00}, {z:0.00}) facing {outDir}");
                        }
                }
            Assert.That(checkedPoints, Is.GreaterThan(100));
        }

        [Test]
        public void DuelMap_NoSlitAtSlabHeight()
        {
            BrushWorld w = DuelMap.BuildBrushes(out BreachMapInfo info);
            Check(w, info.Block);
        }

        [Test]
        public void BreachBlock_NoSlitAtSlabHeight()
        {
            BrushWorld w = BreachMap.BuildBrushes(out BreachMapInfo info);
            Check(w, info.Block);
        }
    }
}
