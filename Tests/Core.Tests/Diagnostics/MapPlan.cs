using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Dev tool: top-down plans of a brush map, one panel per level (ground floor, first floor, roofs), written as a PNG.
    /// Run with SCULPT_PLAN_DIR set to a folder: dotnet test --filter MapPlan. Colours: brick red = breakable wall,
    /// clay = breakable floor, yellow = stairs, sand = ramp, steel = frame, green = ground and hills (darker = higher),
    /// blue / orange dots = team 1 / team 2 spawns, white dots = item pickups. Faint lines every 6.3 m from the map corner.
    /// </summary>
    public class MapPlan
    {
        private const double Px = 0.15; // metres per pixel

        [Test]
        public void WritePlans()
        {
            string dir = Environment.GetEnvironmentVariable("SCULPT_PLAN_DIR");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("set SCULPT_PLAN_DIR to write map plans");
            Directory.CreateDirectory(dir);
            BrushWorld w = DuelMap.BuildBrushes(out BreachMapInfo info);
            File.WriteAllBytes(Path.Combine(dir, "duel_plan.png"), Render(w, info));
        }

        public static byte[] Render(BrushWorld w, BreachMapInfo info)
        {
            int n = (int)Math.Ceiling(info.SizeX / Px), m = (int)Math.Ceiling(info.SizeZ / Px);
            const int levels = 3, gap = 12;
            int width = levels * n + (levels - 1) * gap;
            var img = new byte[width * m * 3];
            for (int i = 0; i < img.Length; i++) img[i] = 30;
            var near = new System.Collections.Generic.List<int>();
            var wallMask = new bool[n * m];
            var wallCol = new (byte, byte, byte)[n * m];
            for (int lv = 0; lv < levels; lv++)
            {
                System.Array.Clear(wallMask, 0, wallMask.Length);
                double floor = lv * GreyboxBlock.StoreyHeight;
                int ox = lv * (n + gap);
                for (int pz = 0; pz < m; pz++)
                    for (int px = 0; px < n; px++)
                    {
                        double x = (px + 0.5) * Px, z = (pz + 0.5) * Px;
                        (byte r, byte g, byte b) c = (88, 128, 70); // ground
                        if (lv > 0) c = (52, 66, 50);              // nothing at this level: dim ground below
                        double bestTop = double.NegativeInfinity;
                        bool blocked = false;
                        (byte, byte, byte) block = default;
                        near.Clear();
                        w.BrushesNear(new Aabb(new Vec3d(x - 0.01, floor - 4.5, z - 0.01), new Vec3d(x + 0.01, floor + 2, z + 0.01)), near);
                        foreach (int k in near)
                        {
                            Brush br = w[k];
                            if (!br.Solid || br.Kind == BrushKind.Marker) continue;
                            Aabb a = br.Bounds;
                            if (x < a.Min.X || x > a.Max.X || z < a.Min.Z || z > a.Max.Z) continue;
                            double top = br.IsRamp ? TopOf(br, x, z) : a.Max.Y;
                            var col = Colour(w, k, br, top);
                            // An obstacle at standing height on this level.
                            if (a.Min.Y < floor + 1.6 && top > floor + 0.7) { blocked = true; block = col; continue; }
                            // A floor near this level (or ground below).
                            if (top <= floor + 0.7 && top >= floor - 4.0 && top > bestTop) { bestTop = top; c = col; }
                        }
                        if (lv > 0 && bestTop < floor - 0.5) c = ((byte)(c.r / 2), (byte)(c.g / 2), (byte)(c.b / 2));
                        if (blocked) { c = block; wallMask[pz * n + px] = true; wallCol[pz * n + px] = block; }
                        if (Math.Abs(x % GreyboxBlock.Module) < Px * 0.5 || Math.Abs(z % GreyboxBlock.Module) < Px * 0.5)
                            c = ((byte)Math.Min(255, c.r + 18), (byte)Math.Min(255, c.g + 18), (byte)Math.Min(255, c.b + 18));
                        Put(img, width, m, ox + px, pz, c);
                    }
                // Thicken walls by a pixel so 0.3 m walls read at this scale.
                for (int pz = 0; pz < m; pz++)
                    for (int px = 0; px < n; px++)
                    {
                        if (wallMask[pz * n + px]) continue;
                        for (int d = 0; d < 4; d++)
                        {
                            int qx = px + (d == 0 ? 1 : d == 1 ? -1 : 0), qz = pz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                            if (qx < 0 || qz < 0 || qx >= n || qz >= m || !wallMask[qz * n + qx]) continue;
                            Put(img, width, m, ox + px, pz, wallCol[qz * n + qx]);
                            break;
                        }
                    }
                for (int t = 0; t < 2; t++)
                    foreach (Vec3d s in info.TeamSpawns[t]) Dot(img, width, m, ox, s, t == 0 ? ((byte)60, (byte)140, (byte)255) : ((byte)255, (byte)140, (byte)40));
                foreach (GroundItem g in info.ItemSpawns) Dot(img, width, m, ox, g.Position, (240, 240, 240));
            }
            return Png(img, width, m);
        }

        private static double TopOf(in Brush b, double x, double z)
        {
            Aabb a = b.Bounds;
            double u = b.Rise == Dir.North ? (z - a.Min.Z) / (a.Max.Z - a.Min.Z)
                : b.Rise == Dir.South ? (a.Max.Z - z) / (a.Max.Z - a.Min.Z)
                : b.Rise == Dir.East ? (x - a.Min.X) / (a.Max.X - a.Min.X)
                : (a.Max.X - x) / (a.Max.X - a.Min.X);
            return a.Min.Y + (a.Max.Y - a.Min.Y) * Math.Clamp(u, 0, 1);
        }

        private static (byte, byte, byte) Colour(BrushWorld w, int k, in Brush b, double top)
        {
            byte shade(double v) => (byte)Math.Clamp(v, 0, 255);
            int t = w.Tiles.TileOfBrush(k);
            if (t >= 0)
            {
                switch (w.Tiles[t].Kind)
                {
                    case TileKind.Wall: return (178, 72, 52);
                    case TileKind.Floor: return (196, 160, 128);
                    case TileKind.Stair: return (226, 196, 60);
                    case TileKind.Ramp: return (214, 186, 130);
                    case TileKind.Door: return (250, 170, 40);
                    default: return (64, 80, 110); // frame
                }
            }
            switch (b.Kind)
            {
                case BrushKind.Ground: return (shade(88 - top * 6), shade(128 + top * 8), shade(70 - top * 4));
                case BrushKind.Stairs: return (200, 180, 70);
                case BrushKind.Ramp: return (190, 180, 150);
                case BrushKind.Frame: return (64, 80, 110);
                case BrushKind.Floor: return (150, 140, 125);
                default: return (110, 116, 126);
            }
        }

        private static void Put(byte[] img, int width, int height, int x, int y, (byte r, byte g, byte b) c)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int i = ((height - 1 - y) * width + x) * 3; // north up
            img[i] = c.r; img[i + 1] = c.g; img[i + 2] = c.b;
        }

        private static void Dot(byte[] img, int width, int height, int ox, Vec3d p, (byte, byte, byte) c)
        {
            int cx = (int)(p.X / Px), cz = (int)(p.Z / Px);
            for (int dz = -5; dz <= 5; dz++)
                for (int dx = -5; dx <= 5; dx++)
                    if (dx * dx + dz * dz <= 25) Put(img, width, height, ox + cx + dx, cz + dz, dx * dx + dz * dz > 16 ? ((byte)0, (byte)0, (byte)0) : c);
        }

        private static byte[] Png(byte[] rgb, int w, int h)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var hdr = new byte[13];
            Be(hdr, 0, w); Be(hdr, 4, h); hdr[8] = 8; hdr[9] = 2;
            Chunk(ms, "IHDR", hdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                    for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
                Chunk(ms, "IDAT", raw.ToArray());
            }
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void Be(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Array.Copy(data, 0, td, 4, data.Length);
            s.Write(td);
            uint crc = 0xFFFFFFFF;
            foreach (byte x in td) { crc ^= x; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
            crc ^= 0xFFFFFFFF;
            var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c);
        }
    }
}
