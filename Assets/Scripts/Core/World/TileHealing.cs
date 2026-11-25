using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Heal (comes back Patched) or reinforce (comes back Reinforced).</summary>
    public enum HealKind : byte
    {
        Heal = 0,
        Reinforce = 1,
    }

    /// <summary>Why a heal or reinforce could not start, or why one stopped.</summary>
    public enum HealResult : byte
    {
        Ok = 0,
        /// <summary>No tile there (or a bad tile id).</summary>
        NoTile = 1,
        /// <summary>Heal where there is nothing to heal (no hole, no bad cracks; reinforced wall is never healed).</summary>
        NothingToHeal = 2,
        /// <summary>Reinforce where everything is already reinforced at full health.</summary>
        AlreadyReinforced = 3,
        OutOfRange = 4,
        NoLineOfSight = 5,
        NotEnoughBar = 6,
        /// <summary>Someone else is already healing or reinforcing this tile.</summary>
        TileBusy = 7,
        /// <summary>Healing is not possible right now (sculpt mode when block moves are on; disarmed).</summary>
        NotInSculptMode = 8,
        // ---- reasons a running heal stopped (cancelled and refunded) ----
        /// <summary>A player was inside the space it would fill when it would have finished.</summary>
        PlayerInside = 9,
        /// <summary>The healer walked out of range.</summary>
        LeftRange = 10,
        /// <summary>The healer died, was disarmed, left sculpt mode, or left the game.</summary>
        Stopped = 11,
        /// <summary>The healer started another heal (one at a time).</summary>
        Replaced = 12,
    }

    public enum HealEventType : byte
    {
        /// <summary>A heal started (bar spent). EndTick says when it finishes.</summary>
        Began = 0,
        /// <summary>It finished: what it covered is now Patched or Reinforced.</summary>
        Completed = 1,
        /// <summary>It stopped before finishing (Result says why); the bar was refunded.</summary>
        Cancelled = 2,
        /// <summary>A heal press the server refused (Result says why). Nothing was spent.</summary>
        Rejected = 3,
    }

    /// <summary>One heal or reinforce in progress, aimed at one cell of one tile.</summary>
    public struct HealJob
    {
        public int Player;
        public int Tile;
        /// <summary>The cell aimed at (walls and floors; 0 on other tiles).</summary>
        public int Cell;
        public HealKind Kind;
        public int StartTick;
        public int EndTick;
        /// <summary>Bar spent at the start (refunded on a cancel).</summary>
        public int Cost;
    }

    public struct HealEvent
    {
        public HealEventType Type;
        public int Player;
        public int Tile;
        public int Cell;
        public HealKind Kind;
        public int Tick;
        public int EndTick;
        public HealResult Result;
    }

    /// <summary>What the healing rules need to know about one player each tick.</summary>
    public struct HealerStatus
    {
        public int Id;
        public bool Alive;
        /// <summary>Able to heal right now (sculpt mode when block moves are on; otherwise not disarmed).</summary>
        public bool InSculptMode;
        public Vec3d Eye;
        /// <summary>The player's collision box (anyone inside the space a heal fills stops it finishing).</summary>
        public Aabb Box;
    }

    /// <summary>
    /// Healing and reinforcing (Ben 2026-11-21, by area since 2026-11-25). Aim at a hole or badly cracked wall in range
    /// and press heal: it takes <see cref="BreachConfig.HealTicks"/> (0.2 s), costs bar, and everything it covers comes
    /// back Patched: the cells within <see cref="BreachConfig.HealRadius"/> of where you aimed, plus the whole hole you
    /// aimed into if it is no bigger than about a person (see <see cref="TileWalls.HealPlan"/>). What it covers is worked
    /// out again when it finishes (the wall may have changed). A heal can't finish while a player is inside the space it
    /// would fill: it is cancelled and refunded. Reinforce costs more and makes it Reinforced. Stairs and ramps heal whole.
    /// Anyone can heal anything, one heal at a time each, and one healer per tile aimed at.
    /// Server-authoritative: the server runs this; clients copy the events (and check presses with the same rules first
    /// so a press that can't work goes red at once, for free). Every change still goes through the tiles' one writer.
    /// </summary>
    public sealed class TileHealing
    {
        public readonly TileWalls Walls;
        public BreachConfig Config => Walls.Config;
        private readonly Dictionary<int, HealJob> _byPlayer = new Dictionary<int, HealJob>();
        private readonly Dictionary<int, int> _healerOfTile = new Dictionary<int, int>();
        private readonly List<int> _scratch = new List<int>();
        private readonly Dictionary<int, ulong> _plan = new Dictionary<int, ulong>();
        private readonly List<Aabb> _space = new List<Aabb>();
        private readonly List<int> _frame = new List<int>();
        /// <summary>Events since the owner last cleared them.</summary>
        public readonly List<HealEvent> Events = new List<HealEvent>();

        public TileHealing(TileWalls walls)
        {
            Walls = walls ?? throw new ArgumentNullException(nameof(walls));
        }

        public int Count => _byPlayer.Count;
        public IEnumerable<HealJob> Jobs => _byPlayer.Values;
        public bool TryGetJob(int player, out HealJob job) => _byPlayer.TryGetValue(player, out job);
        /// <summary>Who is healing this tile, or 0.</summary>
        public int HealerOf(int tile) => _healerOfTile.TryGetValue(tile, out int p) ? p : 0;

        // ------------------------------------------------------------------ pure rules

        /// <summary>Can this kind of heal be done on a plain tile (stair, ramp) in this state?</summary>
        public static HealResult StateCheck(in WallTile t, HealKind kind, BreachConfig c)
        {
            if (t.Links != null) return HealResult.NothingToHeal; // frame followers come back with the tiles around them
            if (kind == HealKind.Reinforce)
                return t.State == TileState.Reinforced && t.Health >= c.ReinforcedHealth ? HealResult.AlreadyReinforced : HealResult.Ok;
            switch (t.State)
            {
                case TileState.Damaged:
                case TileState.Broken: return HealResult.Ok;
                case TileState.Patched: return t.Health < c.PatchedHealth ? HealResult.Ok : HealResult.NothingToHeal;
                default: return HealResult.NothingToHeal; // Intact (cracks show only below half), Reinforced (a heal would weaken it)
            }
        }

        /// <summary>Is there anything for this heal to do at that cell of that tile (walls and floors by area)?</summary>
        public HealResult WhatCheck(int tile, int cell, HealKind kind)
        {
            if (tile < 0 || tile >= Walls.Count) return HealResult.NoTile;
            WallTile t = Walls[tile];
            if (!Walls.HasCells(tile)) return StateCheck(t, kind, Config);
            if (Walls.HasHealWork(tile, cell, kind)) return HealResult.Ok;
            return kind == HealKind.Reinforce ? HealResult.AlreadyReinforced : HealResult.NothingToHeal;
        }

        public static int CostOf(HealKind kind, BreachConfig c) => kind == HealKind.Reinforce ? c.ReinforceCost : c.HealCost;
        public static int TicksOf(HealKind kind, BreachConfig c) => Math.Max(1, kind == HealKind.Reinforce ? c.ReinforceTicks : c.HealTicks);

        /// <summary>The spot a heal aims at: the aimed cell (walls and floors) or the whole tile.</summary>
        public Aabb SpotOf(int tile, int cell) => Walls.HasCells(tile) ? Walls.CellBox(tile, Math.Max(0, Math.Min(63, cell))) : Walls.BoundsOf(tile);

        /// <summary>Distance from the eye to the nearest point of the spot aimed at.</summary>
        public double DistanceTo(int tile, int cell, Vec3d eye) => (SpotOf(tile, cell).ClosestPoint(eye) - eye).Length;

        public double DistanceTo(int tile, Vec3d eye) => DistanceTo(tile, TileWalls.MiddleCell, eye);

        /// <summary>
        /// Can the eye see the spot? True if a ray to its centre, or to any of four points 0.3 m round it on the face,
        /// reaches it (or passes through it when it is open), or stops on the same face nearby. Anything else in front blocks.
        /// </summary>
        public bool CanSee(ITerrain terrain, int tile, int cell, Vec3d eye)
        {
            Aabb b = SpotOf(tile, cell);
            Vec3d c = (b.Min + b.Max) * 0.5;
            Dir facing = Walls[tile].Facing;
            bool thinX = facing == Dir.East; // the tile is thin in X: its face spans Y and Z
            bool flat = facing == Dir.Up;    // floors, stair and ramp segments: the face spans X and Z
            bool celled = Walls.HasCells(tile);
            double h = celled ? 0.3 : Config.TileSize * 0.25;
            int plane = celled ? Walls.PlaneOf(tile) : -1;
            for (int k = 0; k < 5; k++)
            {
                Vec3d off = k == 0 ? Vec3d.Zero
                    : flat ? (k <= 2 ? new Vec3d(k == 1 ? h : -h, 0, 0) : new Vec3d(0, 0, k == 3 ? h : -h))
                    : k == 1 ? new Vec3d(0, h, 0) : k == 2 ? new Vec3d(0, -h, 0)
                    : thinX ? new Vec3d(0, 0, k == 3 ? h : -h) : new Vec3d(k == 3 ? h : -h, 0, 0);
                Vec3d p = c + off;
                Vec3d d = p - eye;
                double len = d.Length;
                if (len < 1e-6) return true;
                TerrainHit hit = terrain.Raycast(eye, d / len, len);
                if (!hit.Hit) return true;
                int ht = Walls.TileOfHit(hit);
                if (ht == tile) return true;
                if (celled && ht >= 0 && Walls.HasCells(ht) && Walls.PlaneOf(ht) == plane && (hit.Point - c).Length <= Config.HealRadius + 0.3) return true;
                if (hit.Distance >= len - 0.02) return true;
            }
            return false;
        }

        public bool CanSee(ITerrain terrain, int tile, Vec3d eye) => CanSee(terrain, tile, TileWalls.MiddleCell, eye);

        /// <summary>
        /// Anyone's box inside the space this heal would fill now (every cell that comes back and any frame piece that
        /// would come back with it; a stair or ramp segment's exact shape)?
        /// </summary>
        public bool Occupied(int tile, int cell, HealKind kind, IReadOnlyList<HealerStatus> players)
        {
            if (!Walls.HasCells(tile))
            {
                if (Inside(tile, players)) return true;
                Walls.RestoredWith(tile, _frame);
                foreach (int f in _frame) if (Inside(f, players)) return true;
                return false;
            }
            Walls.HealPlan(tile, cell, kind, _plan);
            Walls.HealSpace(_plan, _space, _frame);
            foreach (Aabb box in _space)
                for (int i = 0; i < players.Count; i++)
                    if (players[i].Alive && box.Overlaps(players[i].Box)) return true;
            foreach (int f in _frame) if (Inside(f, players)) return true;
            return false;
        }

        public bool Occupied(int tile, IReadOnlyList<HealerStatus> players) => Occupied(tile, TileWalls.MiddleCell, HealKind.Heal, players);

        private bool Inside(int tile, IReadOnlyList<HealerStatus> players)
        {
            Brush b = Walls.ShapeOf(tile);
            for (int i = 0; i < players.Count; i++)
                if (players[i].Alive && BrushWorld.Overlaps(b, players[i].Box)) return true;
            return false;
        }

        /// <summary>
        /// Every check a press has to pass, without changing anything (the client runs it first; the server again with
        /// <paramref name="reachSlack"/> for the client's view being a little behind).
        /// </summary>
        public HealResult Check(ITerrain terrain, int player, Vec3d eye, bool canHeal, int tile, int cell, HealKind kind, int bar, double reachSlack = 0)
        {
            if (!canHeal) return HealResult.NotInSculptMode;
            if (tile < 0 || tile >= Walls.Count) return HealResult.NoTile;
            HealResult r = WhatCheck(tile, cell, kind);
            if (r != HealResult.Ok) return r;
            int healer = HealerOf(tile);
            if (healer != 0 && healer != player) return HealResult.TileBusy;
            if (DistanceTo(tile, cell, eye) > Config.HealRange + reachSlack) return HealResult.OutOfRange;
            int refund = _byPlayer.TryGetValue(player, out HealJob mine) ? mine.Cost : 0; // replacing your own heal refunds it first
            if (bar + refund < CostOf(kind, Config)) return HealResult.NotEnoughBar;
            if (terrain != null && !CanSee(terrain, tile, cell, eye)) return HealResult.NoLineOfSight;
            return HealResult.Ok;
        }

        public HealResult Check(ITerrain terrain, int player, Vec3d eye, bool canHeal, int tile, HealKind kind, int bar, double reachSlack = 0) =>
            Check(terrain, player, eye, canHeal, tile, TileWalls.MiddleCell, kind, bar, reachSlack);

        // ------------------------------------------------------------------ server

        /// <summary>
        /// A heal press. Pressing the tile you are already healing (same kind) does nothing. A press elsewhere replaces
        /// your heal (the old one is refunded). Logs Began or Rejected.
        /// </summary>
        public HealResult TryBegin(ITerrain terrain, in HealerStatus who, int tile, int cell, HealKind kind, SculptBar bar, int tick, double reachSlack = 0)
        {
            cell = Walls.HasCells(Math.Max(0, Math.Min(Walls.Count - 1, tile))) ? Math.Max(0, Math.Min(63, cell)) : 0;
            if (_byPlayer.TryGetValue(who.Id, out HealJob cur) && cur.Tile == tile && cur.Kind == kind) return HealResult.Ok;
            HealResult r = who.Alive ? Check(terrain, who.Id, who.Eye, who.InSculptMode, tile, cell, kind, bar.Value, reachSlack) : HealResult.Stopped;
            if (r != HealResult.Ok)
            {
                Events.Add(new HealEvent { Type = HealEventType.Rejected, Player = who.Id, Tile = Math.Max(0, tile), Cell = cell, Kind = kind, Tick = tick, Result = r });
                return r;
            }
            if (_byPlayer.ContainsKey(who.Id)) Cancel(who.Id, HealResult.Replaced, bar, tick);
            int cost = CostOf(kind, Config);
            bar.Spend(cost, tick);
            var job = new HealJob { Player = who.Id, Tile = tile, Cell = cell, Kind = kind, StartTick = tick, EndTick = tick + TicksOf(kind, Config), Cost = cost };
            _byPlayer[who.Id] = job;
            _healerOfTile[tile] = who.Id;
            Events.Add(new HealEvent { Type = HealEventType.Began, Player = who.Id, Tile = tile, Cell = cell, Kind = kind, Tick = tick, EndTick = job.EndTick });
            return HealResult.Ok;
        }

        public HealResult TryBegin(ITerrain terrain, in HealerStatus who, int tile, HealKind kind, SculptBar bar, int tick, double reachSlack = 0) =>
            TryBegin(terrain, who, tile, TileWalls.MiddleCell, kind, bar, tick, reachSlack);

        /// <summary>Stops a player's heal and refunds it (no-op if they have none). <paramref name="bar"/> may be null (they left).</summary>
        public void Cancel(int player, HealResult reason, SculptBar bar, int tick)
        {
            if (!_byPlayer.TryGetValue(player, out HealJob j)) return;
            _byPlayer.Remove(player);
            _healerOfTile.Remove(j.Tile);
            bar?.Refund(j.Cost);
            Events.Add(new HealEvent { Type = HealEventType.Cancelled, Player = player, Tile = j.Tile, Cell = j.Cell, Kind = j.Kind, Tick = tick, EndTick = j.EndTick, Result = reason });
        }

        /// <summary>
        /// One server tick: a heal whose healer died, can't heal any more or left the game, or walked out of range (range
        /// plus <see cref="BreachConfig.HealLeash"/>) is cancelled and refunded. One that reaches its end tick finishes
        /// unless someone is inside the space it fills (then it is cancelled and refunded) or there is nothing left to do
        /// (it just ends).
        /// </summary>
        public void Step(int tick, IReadOnlyList<HealerStatus> players, Func<int, SculptBar> barOf)
        {
            if (_byPlayer.Count == 0) return;
            _scratch.Clear();
            foreach (int id in _byPlayer.Keys) _scratch.Add(id);
            _scratch.Sort(); // deterministic order
            foreach (int id in _scratch)
            {
                HealJob j = _byPlayer[id];
                int idx = IndexOf(players, id);
                SculptBar bar = idx >= 0 ? barOf(id) : null;
                if (idx < 0 || !players[idx].Alive || !players[idx].InSculptMode) { Cancel(id, HealResult.Stopped, bar, tick); continue; }
                if (DistanceTo(j.Tile, j.Cell, players[idx].Eye) > Config.HealRange + Config.HealLeash) { Cancel(id, HealResult.LeftRange, bar, tick); continue; }
                if (tick < j.EndTick) continue;
                if (Occupied(j.Tile, j.Cell, j.Kind, players)) { Cancel(id, HealResult.PlayerInside, bar, tick); continue; }
                _byPlayer.Remove(id);
                _healerOfTile.Remove(j.Tile);
                Finish(j, id);
                Events.Add(new HealEvent { Type = HealEventType.Completed, Player = id, Tile = j.Tile, Cell = j.Cell, Kind = j.Kind, Tick = tick, EndTick = j.EndTick });
            }
        }

        private void Finish(in HealJob j, int by)
        {
            if (!Walls.HasCells(j.Tile))
            {
                if (j.Kind == HealKind.Reinforce) Walls.Set(j.Tile, TileState.Reinforced, Config.ReinforcedHealth, TileCause.Reinforce, by);
                else Walls.Set(j.Tile, TileState.Patched, Config.PatchedHealth, TileCause.Heal, by);
                return;
            }
            Walls.HealPlan(j.Tile, j.Cell, j.Kind, _plan);
            Walls.ApplyHeal(_plan, j.Kind, by);
        }

        private static int IndexOf(IReadOnlyList<HealerStatus> players, int id)
        {
            for (int i = 0; i < players.Count; i++) if (players[i].Id == id) return i;
            return -1;
        }

        // ------------------------------------------------------------------ client copy

        /// <summary>A client copying the server's events (no rules, no bar: the server already decided).</summary>
        public void ApplyRemote(in HealEvent e)
        {
            switch (e.Type)
            {
                case HealEventType.Began:
                    if (_byPlayer.TryGetValue(e.Player, out HealJob old)) _healerOfTile.Remove(old.Tile);
                    _byPlayer[e.Player] = new HealJob { Player = e.Player, Tile = e.Tile, Cell = e.Cell, Kind = e.Kind, StartTick = e.Tick, EndTick = e.EndTick, Cost = CostOf(e.Kind, Config) };
                    _healerOfTile[e.Tile] = e.Player;
                    break;
                case HealEventType.Completed:
                case HealEventType.Cancelled:
                    if (_byPlayer.TryGetValue(e.Player, out HealJob j) && j.Tile == e.Tile)
                    {
                        _byPlayer.Remove(e.Player);
                        _healerOfTile.Remove(e.Tile);
                    }
                    break;
            }
        }

        /// <summary>Forget everything (a client rejoining).</summary>
        public void Clear()
        {
            _byPlayer.Clear();
            _healerOfTile.Clear();
            Events.Clear();
        }
    }
}
