using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Breaks tiles for a charge. The server breaks them for real; the client predicts the break on its own copy.</summary>
    public interface IChargeBreaker
    {
        /// <summary>Break these (whole plain tiles, or these cells of wall tiles) on tick <paramref name="tick"/>.</summary>
        void Break(TileWalls walls, IReadOnlyList<ChargeCut> cuts, int tick);
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
        [ThreadStatic] private static List<ChargeCut> _hit;

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
            List<ChargeCut> hit = _hit ??= new List<ChargeCut>();
            hit.Clear();
            w.TilesNear(swept, near);
            near.Sort(); // fixed order: same breaks on every machine
            double minCos = Math.Cos(c.ChargeMaxAngleDegrees * Math.PI / 180);
            bool charged = false;
            int pathCells = 0, hitCells = 0;
            foreach (int t in near)
            {
                WallTile tile = w[t];
                if (tile.Kind != TileKind.Wall && tile.Kind != TileKind.Door) continue; // floors, stairs and frame never take a charge (collision still stops you)
                if (w.ChargedAt(t) == tick) charged = true; // replaying the tick we charged through it
                if (tile.State == TileState.Broken) continue;
                if (!w.HasCells(t))
                {
                    bool patchedWeak = tile.State == TileState.Patched && (c.ChargePatchedCounts || tile.Health < c.PatchedHealth);
                    if (tile.Kind != TileKind.Door && tile.State != TileState.Damaged && !patchedWeak) return false; // a solid wall stops you (a door never does); a fresh heal too
                    if (!HeadOn(tile, dir, minCos)) return false;
                    hit.Add(new ChargeCut { Tile = t, Cells = ulong.MaxValue });
                    continue;
                }
                // By cell (Ben 2026-11-25: "sprint through low health walls"): no reinforced cell in your way, and enough
                // of the wall in your way has been hit (one AR bullet at chest height is enough standing, any hit when
                // sliding). Then all of it in your way bursts.
                if (!BrushWorld.Overlaps(w.ShapeOf(t), swept)) continue;
                ulong cells = w.CellsTouching(t, swept);
                if (cells == 0) continue;
                for (int k = 0; k < 64; k++)
                {
                    if (((cells >> k) & 1) == 0) continue;
                    if (w.CellReinforced(t, k)) return false; // reinforced wall always stops you
                    pathCells++;
                    if (w.CellWeak(t, k)) hitCells++;
                }
                if (!HeadOn(tile, dir, minCos)) return false; // a glancing scrape: the wall wins
                // The hole is a cell wider than you on every side, so its cut corners never catch you on the way through.
                double m = Brush.CellsPerSide > 0 ? w.Config.TileSize / Brush.CellsPerSide : 0.2;
                Aabb wide = new Aabb(swept.Min - new Vec3d(m, m, m), swept.Max + new Vec3d(m, m, m));
                hit.Add(new ChargeCut { Tile = t, Cells = cells });
                AddMargin(w, t, wide, hit);
            }
            if (pathCells > 0 && hitCells < c.ChargeHitShare * pathCells) return false; // a sound wall stops you
            if (hit.Count == 0 && !charged) return false;
            if (hit.Count > 0) breaker.Break(w, hit, tick);
            s.Velocity = new Vec3d(s.Velocity.X * c.ChargeKeep, s.Velocity.Y, s.Velocity.Z * c.ChargeKeep);
            s.ChargeTick = tick;
            events?.Add(new MoveEvent { Type = MoveEventType.ChargedThrough, Tick = tick, Value = hit.Count, Amount = speed });
            return true;
        }

        /// <summary>The margin round a charge hole: the cells of this tile and its neighbours inside the widened box, except
        /// reinforced ones (they stay: they never stop you, they are beside your path).</summary>
        private static void AddMargin(TileWalls w, int tile, Aabb wide, List<ChargeCut> hit)
        {
            for (int d = -1; d < 4; d++)
            {
                int u = d < 0 ? tile : w.EdgeNeighbour(tile, d);
                if (u < 0 || w[u].Kind != TileKind.Wall || w[u].State == TileState.Broken || !w.HasCells(u)) continue;
                ulong extra = w.CellsTouching(u, wide), weak = 0;
                for (int k = 0; k < 64; k++)
                    if (((extra >> k) & 1) != 0 && !w.CellReinforced(u, k)) weak |= 1ul << k;
                if (weak != 0) hit.Add(new ChargeCut { Tile = u, Cells = weak });
            }
        }

        private static bool HeadOn(in WallTile tile, Vec3d dir, double minCos)
        {
            bool thinInX = tile.Facing == Dir.East || tile.Facing == Dir.West;
            return Math.Abs(thinInX ? dir.X : dir.Z) >= minCos;
        }

        /// <summary>Breaks the cuts on a tile copy (the plain tiles whole, the celled ones by cell).</summary>
        public static void Apply(TileWalls walls, IReadOnlyList<ChargeCut> cuts, int by)
        {
            var celled = new List<ChargeCut>();
            foreach (ChargeCut cut in cuts)
            {
                if (walls.HasCells(cut.Tile)) celled.Add(cut);
                else walls.Set(cut.Tile, TileState.Broken, 0, TileCause.Charge, by);
            }
            walls.ChargeOpen(celled, by);
        }
    }

    /// <summary>The server's breaker: breaks it for real (reliable Tiles message to everyone).</summary>
    public sealed class ServerChargeBreaker : IChargeBreaker
    {
        public int By;

        public void Break(TileWalls walls, IReadOnlyList<ChargeCut> cuts, int tick)
        {
            walls.Tick = tick;
            ChargeRules.Apply(walls, cuts, By);
        }
    }

    /// <summary>
    /// The client's breaker: breaks the tile on the local copy at once (so your own charge never stutters at any ping)
    /// and remembers what it was. If the server's state for that tick shows you did not charge, the tile is put back.
    /// </summary>
    public sealed class PredictedChargeBreaker : IChargeBreaker
    {
        private struct Pending { public int Tile, Tick; public TileState State; public int Health; public TileWalls.CellSnapshot Cells; }
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly List<int> _near = new List<int>();

        public int PendingCount => _pending.Count;

        public void Break(TileWalls walls, IReadOnlyList<ChargeCut> cuts, int tick)
        {
            // Remember every tile the break can touch (the tidy can reach the tiles beside a cut), then break.
            _near.Clear();
            foreach (ChargeCut cut in cuts)
            {
                if (!_near.Contains(cut.Tile)) _near.Add(cut.Tile);
                if (!walls.HasCells(cut.Tile)) continue;
                for (int d = 0; d < 4; d++)
                {
                    int n = walls.EdgeNeighbour(cut.Tile, d);
                    if (n >= 0 && !_near.Contains(n)) _near.Add(n);
                }
            }
            foreach (int t in _near)
            {
                WallTile w = walls[t];
                if (w.State == TileState.Broken) continue;
                _pending.Add(new Pending { Tile = t, Tick = tick, State = w.State, Health = w.Health, Cells = walls.HasCells(t) ? walls.Snapshot(t) : default });
            }
            int keep = walls.Tick;
            walls.Tick = tick;
            ChargeRules.Apply(walls, cuts, 0);
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
                if (walls.ChargedAt(p.Tile) != p.Tick) continue; // the server has changed it since
                if (walls.HasCells(p.Tile)) walls.Restore(p.Tile, p.Cells, TileCause.Server);
                else walls.Set(p.Tile, p.State, p.Health, TileCause.Server);
            }
        }

        public void Clear() => _pending.Clear();
    }
}
