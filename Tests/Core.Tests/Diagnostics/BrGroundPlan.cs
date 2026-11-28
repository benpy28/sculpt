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
                        else if (foot.OnLevelGroundFast(x, z) && Math.Abs(h - Math.Round(h / 0.3) * 0.3) < 1e-4) { r = 205; g = 198; b = 180; }
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
            // The segments: borders (white), the keep-out strips and reserved lanes (shaded), home plazas (ring), pad lanes.
            for (int id = 0; id < BrLayout.Count; id++)
            {
                Rect2 a = BrLayout.Buildable(id);
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        double x = (i + 0.5) * px, z = (j + 0.5) * px;
                        if (BrLayout.Owner(x, z) != id) continue;
                        if (!a.Contains(x, z)) Shade(img, n, i, j, 0.7);
                        if (BrLayout.ToInnerBorder(id, x, z) < px) Put(img, n, i, j, (255, 255, 255));
                        if (id != BrLayout.Centre)
                        {
                            Vec3d an = BrLayout.Anchor(id);
                            double d = Math.Sqrt((x - an.X) * (x - an.X) + (z - an.Z) * (z - an.Z));
                            if (Math.Abs(d - BrLayout.HomeRadius) < px * 0.7) Put(img, n, i, j, (255, 120, 220 - 120));
                        }
                    }
                BrBuild b = BrMap.Template.Segments[id];
                foreach (Rect2 r in b.Reserved)
                    for (double z = r.Z0; z <= r.Z1; z += px)
                        for (double x = r.X0; x <= r.X1; x += px) Shade(img, n, (int)(x / px), (int)(z / px), 0.8);
                foreach (var l in b.Lanes)
                    for (double t = 0; t <= 1; t += 0.002)
                        Put(img, n, (int)((l.From.X + (l.To.X - l.From.X) * t) / px), (int)((l.From.Z + (l.To.Z - l.From.Z) * t) / px), (90, 170, 255));
            }
            foreach (BrChestSpot ch in br.Chests) Dot(img, n, ch.Position, px, 1, (250, 220, 40));
            foreach (BrRegion r in br.Regions) foreach (Vec3d p in r.Pads) Dot(img, n, p, px, 2, (60, 140, 255));
            File.WriteAllBytes(Path.Combine(dir, "br_ground.png"), MapPlanPng(img, n, n));
            // One segment on its own (SCULPT_PLAN_SEGMENT = its stem, e.g. NorthEast): its square, cropped.
            string only = Environment.GetEnvironmentVariable("SCULPT_PLAN_SEGMENT");
            int sid = Array.IndexOf(BrLayout.Stems, only ?? "");
            if (sid >= 0)
            {
                Rect2 sq = BrLayout.Bounds(sid);
                int i0 = (int)(sq.X0 / px), j0 = (int)(sq.Z0 / px), m = Math.Min(n - i0, (int)Math.Ceiling(BrLayout.SegSize / px));
                var crop = new byte[m * m * 3];
                for (int j = 0; j < m; j++)
                    for (int i = 0; i < m; i++)
                    {
                        int src = ((n - 1 - (j0 + j)) * n + i0 + i) * 3, dst = ((m - 1 - j) * m + i) * 3;
                        if (src < 0 || src + 2 >= img.Length) continue;
                        crop[dst] = img[src]; crop[dst + 1] = img[src + 1]; crop[dst + 2] = img[src + 2];
                    }
                File.WriteAllBytes(Path.Combine(dir, $"br_{only}.png"), MapPlanPng(crop, m, m));
            }
            File.WriteAllText(Path.Combine(dir, "br_ground.txt"), $"field {fieldMs} ms, brushes {brushMs} ms; heights {f.MinHeight:0.0}..{f.MaxHeight:0.0} m; " +
                                           $"{info.Block.Buildings.Count} buildings, {br.Chests.Count} chests, {w.Tiles.Count} tiles, {foot.Count} footings");
        }

        private static void Put(byte[] img, int n, int x, int y, (byte r, byte g, byte b) c)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            int i = ((n - 1 - y) * n + x) * 3;
            img[i] = c.r; img[i + 1] = c.g; img[i + 2] = c.b;
        }

        private static void Shade(byte[] img, int n, int x, int y, double k)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            int i = ((n - 1 - y) * n + x) * 3;
            img[i] = (byte)(img[i] * k); img[i + 1] = (byte)(img[i + 1] * k); img[i + 2] = (byte)(img[i + 2] * k);
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
