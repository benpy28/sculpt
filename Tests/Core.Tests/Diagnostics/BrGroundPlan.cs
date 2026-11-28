using System;
using System.IO;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Dev tool: a top-down picture of the battle royale map's ground (map v3): height as colour (low blue-green to high
    /// tan), hill shading from the north-west, rock faces grey, footings pale stone, buildings dark red, chests yellow,
    /// pads blue, a faint line every 100 m. Run with SCULPT_PLAN_DIR set: dotnet test --filter BrGroundPlan.
    /// </summary>
    public class BrGroundPlan
    {
        [Test]
        public void WriteBrGroundPlan()
        {
            string dir = Environment.GetEnvironmentVariable("SCULPT_PLAN_DIR");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("set SCULPT_PLAN_DIR to write the plan");
            Directory.CreateDirectory(dir);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            HeightField f = BrMap.Field;
            long fieldMs = clock.ElapsedMilliseconds;
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            long brushMs = clock.ElapsedMilliseconds - fieldMs;
            Footings foot = BrMap.FootingsUsed;
            const double px = 1.0;
            int n = (int)Math.Ceiling(BrMap.Size / px);
            var img = new byte[n * n * 3];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = (i + 0.5) * px, z = (j + 0.5) * px;
                    (byte, byte, byte) c;
                    if (!f.Covers(x, z)) c = (20, 20, 24);
                    else
                    {
                        double h = f.HeightAt(x, z);
                        Vec3d nn = f.NormalAt(x, z);
                        double t = Math.Clamp((h + 16) / 42, 0, 1);
                        double r = 60 + 150 * t, g = 120 + 80 * Math.Sin(t * Math.PI) , b = 110 - 70 * t;
                        double light = Math.Clamp(0.55 + 0.6 * (-nn.X * 0.6 + nn.Y * 0.5 + nn.Z * 0.6), 0.35, 1.25);
                        if (nn.Y < f.WalkableNormalY) { r = g = b = 120; }
                        else if (foot.OnLevelGround(x, z) && Math.Abs(h - Math.Round(h / 0.3) * 0.3) < 1e-4) { r = 205; g = 198; b = 180; }
                        c = ((byte)Math.Clamp(r * light, 0, 255), (byte)Math.Clamp(g * light, 0, 255), (byte)Math.Clamp(b * light, 0, 255));
                        if (Math.Abs(x % 100) < px * 0.5 || Math.Abs(z % 100) < px * 0.5) c = ((byte)(c.Item1 * 0.8), (byte)(c.Item2 * 0.8), (byte)(c.Item3 * 0.8));
                    }
                    Put(img, n, i, j, c);
                }
            foreach (Aabb b in info.Block.Buildings)
                for (double z = b.Min.Z; z <= b.Max.Z; z += px * 0.5)
                    for (double x = b.Min.X; x <= b.Max.X; x += px * 0.5)
                    {
                        bool edge = x - b.Min.X < 0.8 || b.Max.X - x < 0.8 || z - b.Min.Z < 0.8 || b.Max.Z - z < 0.8;
                        Put(img, n, (int)(x / px), (int)(z / px), edge ? ((byte)120, (byte)30, (byte)24) : ((byte)170, (byte)80, (byte)60));
                    }
            foreach (BrChestSpot ch in br.Chests) Dot(img, n, ch.Position, px, 1, (250, 220, 40));
            foreach (BrRegion r in br.Regions) foreach (Vec3d p in r.Pads) Dot(img, n, p, px, 2, (60, 140, 255));
            File.WriteAllBytes(Path.Combine(dir, "br_ground.png"), MapPlanPng(img, n, n));
            File.WriteAllText(Path.Combine(dir, "br_ground.txt"), $"field {fieldMs} ms, brushes {brushMs} ms; heights {f.MinHeight:0.0}..{f.MaxHeight:0.0} m; " +
                                           $"{info.Block.Buildings.Count} buildings, {br.Chests.Count} chests, {w.Tiles.Count} tiles, {foot.Count} footings in the eighth");
        }

        private static void Put(byte[] img, int n, int x, int y, (byte r, byte g, byte b) c)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            int i = ((n - 1 - y) * n + x) * 3;
            img[i] = c.r; img[i + 1] = c.g; img[i + 2] = c.b;
        }

        private static void Dot(byte[] img, int n, Vec3d p, double px, int rad, (byte, byte, byte) c)
        {
            int cx = (int)(p.X / px), cz = (int)(p.Z / px);
            for (int dz = -rad; dz <= rad; dz++)
                for (int dx = -rad; dx <= rad; dx++) Put(img, n, cx + dx, cz + dz, c);
        }

        private static byte[] MapPlanPng(byte[] rgb, int w, int h)
        {
            var m = typeof(MapPlan).GetMethod("Png", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (byte[])m.Invoke(null, new object[] { rgb, w, h });
        }
    }
}
