using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Pieces
{
    /// <summary>Small flat terrain v2 world for sculpt tests: 32 x 32 tiles, ground top at height 0.</summary>
    internal sealed class SculptWorld
    {
        public const double B = SubGrid.TileSize;
        public const double S = SubGrid.SubSize;

        public readonly SubGrid Grid;
        public readonly TileMap Tiles;
        public readonly SculptConfig Config = new SculptConfig();
        public readonly SculptSystem Sculpt;

        public SculptWorld(bool bigBar = true)
        {
            Grid = new SubGrid(8, 4, 8, -16);
            Grid.FillBaseGround();
            Tiles = new TileMap(Grid);
            Grid.ClearChanges();
            Sculpt = new SculptSystem(Grid, Config);
        }

        public static Vec3d FeetIn(double tx, double ty, double tz) => new Vec3d((tx + 0.5) * B, ty * B, (tz + 0.5) * B);

        public PlayerBody AddPlayer(int id, int team, double tx, double ty, double tz)
        {
            var p = new PlayerBody(id, team, FeetIn(tx, ty, tz));
            Sculpt.AddPlayer(p);
            return p;
        }

        /// <summary>Aims from the player's eye at a world point: the action's target sub-cell and aim point.</summary>
        public static void Aim(SubGrid g, PlayerBody p, Vec3d point, out Int3 sub, out Vec3d aim)
        {
            Vec3d d = point - p.Eye;
            TerrainHit h = g.Raycast(p.Eye, d, d.Length + 1.0);
            Assert.That(h.Hit, Is.True, "aim ray hits terrain");
            sub = h.Sub;
            aim = h.Point;
        }

        public SculptAction Move(PlayerBody p, Vec3d point, Dir dir, bool reinforce = false)
        {
            Aim(Grid, p, point, out Int3 sub, out Vec3d aim);
            return SculptAction.Move(sub, dir, aim, reinforce);
        }

        public SculptAction Raise(PlayerBody p, Vec3d point, bool reinforce = false)
        {
            Aim(Grid, p, point, out Int3 sub, out Vec3d aim);
            return SculptAction.Raise(sub, aim, reinforce);
        }

        public SculptAction Sink(PlayerBody p, Vec3d point, bool reinforce = false)
        {
            Aim(Grid, p, point, out Int3 sub, out Vec3d aim);
            return SculptAction.Sink(sub, aim, reinforce);
        }

        public SculptAction Throw(PlayerBody p, Vec3d point, Vec3d look)
        {
            Aim(Grid, p, point, out Int3 sub, out Vec3d aim);
            return SculptAction.Throw(sub, aim, look);
        }

        public void Step(int ticks)
        {
            for (int i = 0; i < ticks; i++) Sculpt.Step();
        }

        /// <summary>Surface height straight below a world x/z.</summary>
        public double TopAt(double x, double z) => Grid.Raycast(new Vec3d(x, 30, z), new Vec3d(0, -1, 0), 60).Point.Y;

        public bool Inside(PlayerBody p) => Grid.BoxOverlapsSolid(p.Bounds);
    }
}
