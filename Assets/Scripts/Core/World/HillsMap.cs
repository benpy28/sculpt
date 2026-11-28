using System;

namespace Sculpt.Core
{
    /// <summary>
    /// A small practice map to feel the smooth height-map ground (map v3 step 3) before the big map uses it: a 192 m
    /// square with a big round hill (18 m, slopes up to about 32 degrees), a long ridge, a bowl to slide down and up the
    /// far side of, a row of humps for slide hops, a rock-faced mesa (68 degree faces) with a ramp up to its top, and two
    /// small buildings, one on the flat and one on a level footing cut into the hill's flank. The ground grid under it all
    /// is empty: the height map is the ground.
    /// </summary>
    public static class HillsMap
    {
        public const int ChunksAcross = 40; // 40 x 4.8 m = 192 m
        public const double Size = ChunksAcross * SubGrid.ChunkSize * SubGrid.SubSize;
        public const double Cell = 1.5;

        // The level footing on the hill's west flank.
        public const double FootX0 = 86, FootZ0 = 62, FootLevel = 6;

        private static HeightField _field;
        private static readonly object Gate = new object();

        /// <summary>The ground: built once, never changes (shared by the server, every client and the bots).</summary>
        public static HeightField Field
        {
            get
            {
                lock (Gate) return _field ??= BuildField();
            }
        }

        public static double HeightAt(double x, double z)
        {
            double h = 0.5 * Math.Sin(x * 0.07) * Math.Sin(z * 0.06) * GroundShapes.Smoothstep((x - 50) / 20);
            h += GroundShapes.Hill(x, z, 120, 70, 45, 18);
            h += GroundShapes.Ridge(x, z, 40, 118, 92, 146, 18, 6);
            h -= GroundShapes.Hill(x, z, 38, 168, 22, 7);
            foreach (double hx in new[] { 18.0, 30.0, 42.0 }) h += GroundShapes.Hill(x, z, hx, 62, 5, 1.2);
            // The mesa: flat 9 m top, rock faces at 68 degrees; a ramp 8 m wide climbs to it from the west at 18 degrees
            // (its sides are rock faces too).
            double mesa = GroundShapes.Mesa(x, z, 152, 138, 178, 172, 9, 68);
            double rampLen = 9 / Math.Tan(18 * Math.PI / 180), rx1 = 152, rx0 = rx1 - rampLen;
            double along = Math.Max(rx0, Math.Min(rx1, x));
            double rampTop = 9 * (along - rx0) / rampLen;
            double side = Math.Max(0, Math.Max(150 - z, z - 158)) + Math.Max(0, rx0 - x);
            double ramp = Math.Max(0, rampTop - side * Math.Tan(68 * Math.PI / 180));
            h = Math.Max(h, Math.Max(mesa, ramp));
            // A footing cut into the hill's flank for the second building (one module square plus a 3 m apron).
            h = GroundShapes.Footing(h, x, z, FootX0, FootZ0, FootX0 + 2 * GreyboxBlock.Module + GreyboxBlock.Column,
                                     FootZ0 + 2 * GreyboxBlock.Module + GreyboxBlock.Column, FootLevel, 3);
            // The flat building and the spawns stay level.
            h = GroundShapes.Footing(h, x, z, 60, 18, 73, 31, 0, 3);
            return h;
        }

        private static HeightField BuildField()
        {
            int n = (int)Math.Round(Size / Cell);
            return HeightField.FromFunction(0, 0, Cell, n, n, HeightAt);
        }

        /// <summary>An empty ground grid of the map's size (the height map is the ground).</summary>
        public static SubGrid BuildGround() => new SubGrid(ChunksAcross, 1, ChunksAcross, -16);

        public static BrushWorld BuildBrushes(out BreachMapInfo info, BreachConfig breach = null)
        {
            var w = new BrushWorld(breach);
            info = new BreachMapInfo { SizeX = Size, SizeZ = Size, Block = new GreyboxInfo() };
            var flat = new GreyboxBlock.Building(60, 18, 2, 2, 1);
            flat.AlongX[0, 0, 0] = WallKind.Door;
            flat.AlongZ[0, 2, 1] = WallKind.ShutDoor;
            GreyboxBlock.Raise(w, info.Block, flat, "Flat house");
            var hill = new GreyboxBlock.Building(FootX0, FootZ0, 2, 2, 1) { Y0 = FootLevel };
            hill.AlongZ[0, 0, 0] = WallKind.Door;
            hill.AlongX[0, 2, 1] = WallKind.Window;
            GreyboxBlock.Raise(w, info.Block, hill, "Hill house");
            // A plain wall round the edge (nothing walks off the map).
            const double t = 0.6, top = 12;
            w.AddBox(new Vec3d(-t, -10, -t), new Vec3d(Size + t, top, 0), BrushKind.Wall);
            w.AddBox(new Vec3d(-t, -10, Size), new Vec3d(Size + t, top, Size + t), BrushKind.Wall);
            w.AddBox(new Vec3d(-t, -10, 0), new Vec3d(0, top, Size), BrushKind.Wall);
            w.AddBox(new Vec3d(Size, -10, 0), new Vec3d(Size + t, top, Size), BrushKind.Wall);
            w.Tiles.Changes.Clear();
            for (int i = 0; i < 3; i++)
            {
                info.TeamSpawns[0].Add(new Vec3d(22 + 3 * i, 0, 20));
                info.TeamSpawns[1].Add(new Vec3d(22 + 3 * i, 0, 34));
            }
            info.SpawnYaw = new[] { Math.PI / 4, Math.PI / 4 };
            foreach (double x in new[] { 20.0, 30.0 })
                info.ItemSpawns.Add(new GroundItem { Kind = Item.Grenade, Count = 2, Position = new Vec3d(x, 0, 27) });
            return w;
        }

        /// <summary>The terrain movement and bullets use: the height map, the (empty) ground grid and the buildings.</summary>
        public static CompositeTerrain Terrain(SubGrid ground, BrushWorld brushes) =>
            new CompositeTerrain(new CompositeTerrain(Field, ground), brushes);
    }
}
