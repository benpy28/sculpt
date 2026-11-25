using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Breaks tiles for a charge. The server breaks them for real; the client predicts the break on its own copy.</summary>
    public interface IChargeBreaker
    {
        void Break(TileWalls walls, int tile, int tick);
    }

    /// <summary>
    /// Charge breach (Ben 2026-11-21): running into a Damaged or Patched tile at 80%+ of top sprint speed breaks it and
    /// keeps most of your speed; Intact and Reinforced tiles stop you. Every solid tile your box is about to hit must be
    /// chargeable, so a standing player (two tiles tall) needs both tiles cracked, while a sliding player fits through
    /// one. Runs inside the player's simulation just before movement, so the same tick's movement passes through.
    /// </summary>
    public static class ChargeRules
    {
        [ThreadStatic] private static List<int> _near;
        [ThreadStatic] private static List<int> _hit;

        /// <summary>
        /// Checks the box's path this tick. If it charges, the breakable tiles go to <paramref name="breaker"/>, the
        /// horizontal speed is cut to <see cref="BreachConfig.ChargeKeep"/> and <see cref="MotorState.ChargeTick"/> is
        /// set. A tile already charged through on this same tick (a replay) counts as charged, so a replay matches.
        /// No breaker = no charging (plain tests and tools).
        /// </summary>
        public static bool Step(ITerrain terrain, MovementConfig mc, ref MotorState s, int tick, IChargeBreaker breaker, List<MoveEvent> events = null)
        {
            if (breaker == null || s.Dead) return false;
            if (s.Mode == MoveMode.Vault || s.Mode == MoveMode.Mantle) return false;
            TileWalls w = TileWalls.Find(terrain);
            if (w == null) return false;
            BreachConfig c = w.Config;
            double speed = s.HorizontalSpeed;
            if (speed < c.ChargeSpeedFraction * mc.SprintSpeed || speed < 1e-6) return false;

            double dt = mc.Dt;
            var dir = new Vec3d(s.Velocity.X / speed, 0, s.Velocity.Z / speed);
            double hw = mc.HalfWidth - 0.01;
            double h = PlayerMovement.HeightOf(s, mc);
            var box = new Aabb(new Vec3d(s.Position.X - hw, s.Position.Y + 0.01, s.Position.Z - hw),
                               new Vec3d(s.Position.X + hw, s.Position.Y + h - 0.01, s.Position.Z + hw));
            Vec3d reach = dir * (speed * dt + 0.02);
            var swept = new Aabb(new Vec3d(Math.Min(box.Min.X, box.Min.X + reach.X), box.Min.Y, Math.Min(box.Min.Z, box.Min.Z + reach.Z)),
                                 new Vec3d(Math.Max(box.Max.X, box.Max.X + reach.X), box.Max.Y, Math.Max(box.Max.Z, box.Max.Z + reach.Z)));

            List<int> near = _near ??= new List<int>();
            List<int> hit = _hit ??= new List<int>();
            hit.Clear();
            w.TilesNear(swept, near);
            near.Sort(); // fixed order: same breaks on every machine
            double minCos = Math.Cos(c.ChargeMaxAngleDegrees * Math.PI / 180);
            bool charged = false;
            foreach (int t in near)
            {
                WallTile tile = w[t];
                if (tile.Kind != TileKind.Wall && tile.Kind != TileKind.Door) continue; // floors, stairs and frame never take a charge (collision still stops you)
                if (tile.State == TileState.Broken)
                {
                    if (w.ChargedAt(t) == tick) charged = true; // replaying the tick we charged through it
                    continue;
                }
                // Blast holes: only the part of the tile still standing is in your way.
                if (tile.Holed && !BrushWorld.Overlaps(w.ShapeOf(t), swept)) continue;
                if (tile.Kind != TileKind.Door && tile.State != TileState.Damaged && tile.State != TileState.Patched) return false; // a solid wall stops you (a door never does)
                bool thinInX = tile.Facing == Dir.East || tile.Facing == Dir.West;
                double headOn = Math.Abs(thinInX ? dir.X : dir.Z);
                if (headOn < minCos) return false; // a glancing scrape: the wall wins
                hit.Add(t);
            }
            if (hit.Count == 0 && !charged) return false;
            foreach (int t in hit) breaker.Break(w, t, tick);
            s.Velocity = new Vec3d(s.Velocity.X * c.ChargeKeep, s.Velocity.Y, s.Velocity.Z * c.ChargeKeep);
            s.ChargeTick = tick;
            events?.Add(new MoveEvent { Type = MoveEventType.ChargedThrough, Tick = tick, Value = hit.Count, Amount = speed });
            return true;
        }
    }

    /// <summary>The server's breaker: breaks the tile for real (reliable Tiles message to everyone).</summary>
    public sealed class ServerChargeBreaker : IChargeBreaker
    {
        public int By;

        public void Break(TileWalls walls, int tile, int tick)
        {
            walls.Tick = tick;
            walls.Set(tile, TileState.Broken, 0, TileCause.Charge, By);
        }
    }

    /// <summary>
    /// The client's breaker: breaks the tile on the local copy at once (so your own charge never stutters at any ping)
    /// and remembers what it was. If the server's state for that tick shows you did not charge, the tile is put back.
    /// </summary>
    public sealed class PredictedChargeBreaker : IChargeBreaker
    {
        private struct Pending { public int Tile, Tick; public TileState State; public int Health; }
        private readonly List<Pending> _pending = new List<Pending>();

        public int PendingCount => _pending.Count;

        public void Break(TileWalls walls, int tile, int tick)
        {
            WallTile t = walls[tile];
            if (t.State == TileState.Broken) return;
            _pending.Add(new Pending { Tile = tile, Tick = tick, State = t.State, Health = t.Health });
            int keep = walls.Tick;
            walls.Tick = tick;
            walls.Set(tile, TileState.Broken, 0, TileCause.Charge);
            walls.Tick = keep;
        }

        /// <summary>
        /// The server's state at <paramref name="serverTick"/> arrived. Predicted breaks up to that tick are confirmed
        /// if it charged on or after them, else refused and restored (unless the server has changed the tile since).
        /// </summary>
        public void OnServerState(TileWalls walls, int serverTick, int serverChargeTick)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (p.Tick > serverTick) continue;
                _pending.RemoveAt(i);
                if (serverChargeTick >= p.Tick) continue; // confirmed (the Tiles message makes it official)
                if (walls[p.Tile].State == TileState.Broken && walls.ChargedAt(p.Tile) == p.Tick)
                    walls.Set(p.Tile, p.State, p.Health, TileCause.Server);
            }
        }

        public void Clear() => _pending.Clear();
    }
}
