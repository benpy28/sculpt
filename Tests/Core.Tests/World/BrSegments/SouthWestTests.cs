using System;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    public class SouthWestTests
    {
        [Test]
        public void DebugLevel()
        {
            HeightField f = BrMap.Field;
            BrMap.BuildBrushes(out BreachMapInfo info, out _);
            foreach (Aabb b in info.Block.Buildings)
            {
                if (b.Min.X != 300 || b.Min.Z != 342) continue;
                for (double z = b.Min.Z; z <= b.Max.Z; z += 1.3)
                    for (double x = b.Min.X; x <= b.Max.X; x += 1.3)
                        if (Math.Abs(f.HeightAt(x, z) - b.Min.Y) > 1e-4) TestContext.WriteLine($"{x:0.00},{z:0.00}: {f.HeightAt(x, z):0.0000} natural {BrMap.Natural(x, z):0.0000}");
            }
            int i0 = (int)Math.Floor((300 - f.OriginX) / f.Cell), j0 = (int)Math.Floor((342 - f.OriginZ) / f.Cell);
            for (int dj = -1; dj <= 2; dj++) for (int di = -1; di <= 2; di++)
                TestContext.WriteLine($"v {f.VertexX(i0 + di):0.00},{f.VertexZ(j0 + dj):0.00} = {f[i0 + di, j0 + dj]:0.0000}");
            var fs = BrMap.FootingsUsed;
            for (int k = 0; k < fs.Count; k++)
            {
                var ft = fs[k];
                if (GroundShapes.ToRect(306, 348, ft.X0, ft.Z0, ft.X1, ft.Z1) < 40) TestContext.WriteLine($"footing {k} {ft.X0},{ft.Z0}..{ft.X1},{ft.Z1} level {ft.Level} apron {ft.Apron}");
            }
        }

        [Test]
        public void Dump()
        {
            BrBuild b = BrMap.Template.Segments[0];
            TestContext.WriteLine($"area {b.Area} anchor {b.Anchor}");
            foreach (Rect2 r in b.Reserved) TestContext.WriteLine($"reserved {r}");
            foreach (var l in b.Lanes) TestContext.WriteLine($"lane {l.From} -> {l.To} half {l.Half}");
            BrRegion reg = BrMap.Template.Br.Regions.Find(x => x.Segment == 0);
            TestContext.WriteLine($"spawn {reg.Building.Min} .. {reg.Building.Max}");
            for (int i = 0; i < 4; i++) TestContext.WriteLine($"pad {reg.Pads[i]} loot {reg.LootSpots[i]}");
            foreach (PropRequest q in BrNature.GetPlan().Dropped) if (q.Segment == 0) TestContext.WriteLine($"dropped {q.Shape} at {q.X},{q.Z}");
        }
    }
}
