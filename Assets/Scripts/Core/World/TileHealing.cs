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
        /// <summary>Heal on a tile that has nothing to heal (Intact, full Patched, Reinforced).</summary>
        NothingToHeal = 2,
        /// <summary>Reinforce on a tile that is already Reinforced at full health.</summary>
        AlreadyReinforced = 3,
        OutOfRange = 4,
        NoLineOfSight = 5,
        NotEnoughBar = 6,
        /// <summary>Someone else is already healing or reinforcing this tile.</summary>
        TileBusy = 7,
        NotInSculptMode = 8,
        // ---- reasons a running heal stopped (cancelled and refunded) ----
        /// <summary>A player was inside the tile's space when it would have finished.</summary>
        PlayerInside = 9,
        /// <summary>The healer walked out of range.</summary>
        LeftRange = 10,
        /// <summary>The healer left sculpt mode, died, or left the game.</summary>
        Stopped = 11,
        /// <summary>The healer started another heal (one tile at a time).</summary>
        Replaced = 12,
    }

    public enum HealEventType : byte
    {
        /// <summary>A heal started (bar spent). EndTick says when it finishes.</summary>
        Began = 0,
        /// <summary>It finished: the tile is now Patched or Reinforced.</summary>
        Completed = 1,
        /// <summary>It stopped before finishing (Result says why); the bar was refunded.</summary>
        Cancelled = 2,
        /// <summary>A heal press the server refused (Result says why). Nothing was spent.</summary>
        Rejected = 3,
    }

    /// <summary>One heal or reinforce in progress.</summary>
    public struct HealJob
    {
        public int Player;
        public int Tile;
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
        /// <summary>In sculpt mode (entering counts) - healing needs it.</summary>
        public bool InSculptMode;
        public Vec3d Eye;
        /// <summary>The player's collision box (anyone inside a tile's space stops it finishing).</summary>
        public Aabb Box;
    }

    /// <summary>
    /// Healing and reinforcing wall tiles (Ben 2026-11-21): the sculpt key now mainly heals. Aim at a Damaged or Broken
    /// tile in range and press: it takes <see cref="BreachConfig.HealTicks"/>, costs bar, and the tile comes back
    /// Patched. The hole stays open until the heal finishes (that window is the point). A heal can't finish while a
    /// player is inside the tile's space: it is cancelled and refunded. Reinforce costs more and makes it Reinforced.
    /// Anyone can heal or reinforce any tile, one tile at a time each, and one healer per tile.
    /// Server-authoritative: the server runs this; clients copy the events (and check presses with the same rules first
    /// so a press that can't work goes red at once, for free). Every tile change still goes through
    /// <see cref="TileWalls.Set"/>.
    /// </summary>
    public sealed class TileHealing
    {
        public readonly TileWalls Walls;
        public BreachConfig Config => Walls.Config;
        private readonly Dictionary<int, HealJob> _byPlayer = new Dictionary<int, HealJob>();
        private readonly Dictionary<int, int> _healerOfTile = new Dictionary<int, int>();
        private readonly List<int> _scratch = new List<int>();
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

        /// <summary>Can this kind of heal be done on a tile in this state (ignoring range, bar and other healers)?</summary>
        public static HealResult StateCheck(in WallTile t, HealKind kind, BreachConfig c)
        {
            if (t.Links != null) return HealResult.NothingToHeal; // frame followers come back with the tiles around them
            if (kind == HealKind.Reinforce)
                return t.State == TileState.Reinforced && t.Health >= c.ReinforcedHealth ? HealResult.AlreadyReinforced : HealResult.Ok;
            if (t.Open != 0 && t.State != TileState.Reinforced) return HealResult.Ok; // a hole blown in it
            switch (t.State)
            {
                case TileState.Damaged:
                case TileState.Broken: return HealResult.Ok;
                case TileState.Patched: return t.Health < c.PatchedHealth ? HealResult.Ok : HealResult.NothingToHeal;
                default: return HealResult.NothingToHeal; // Intact (cracks show only below half), Reinforced (a heal would weaken it)
            }
        }

        public static int CostOf(HealKind kind, BreachConfig c) => kind == HealKind.Reinforce ? c.ReinforceCost : c.HealCost;
        public static int TicksOf(HealKind kind, BreachConfig c) => Math.Max(1, kind == HealKind.Reinforce ? c.ReinforceTicks : c.HealTicks);

        /// <summary>Distance from the eye to the nearest point of the tile.</summary>
        public double DistanceTo(int tile, Vec3d eye) => (Walls.BoundsOf(tile).ClosestPoint(eye) - eye).Length;

        /// <summary>
        /// Can the eye see the tile? True if a ray to its centre, or to any of four points halfway to its edges, reaches
        /// the tile (or passes through it when it is broken) before anything else solid. Other tiles in front block.
        /// </summary>
        public bool CanSee(ITerrain terrain, int tile, Vec3d eye)
        {
            Aabb b = Walls.BoundsOf(tile);
            Vec3d c = (b.Min + b.Max) * 0.5;
            Dir facing = Walls[tile].Facing;
            bool thinX = facing == Dir.East; // the tile is thin in X: its face spans Y and Z
            bool flat = facing == Dir.Up;    // floors, stair and ramp segments: the face spans X and Z
            double h = Config.TileSize * 0.25;
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
                if (Walls.TileOfHit(hit) == tile) return true;
                if (hit.Distance >= len - 0.02) return true;
            }
            return false;
        }

        /// <summary>
        /// Anyone's box inside the tile's space (its exact shape: over a stair segment's slope is not inside it), or inside
        /// any broken frame piece that would come back with it?
        /// </summary>
        public bool Occupied(int tile, IReadOnlyList<HealerStatus> players)
        {
            if (Inside(tile, players)) return true;
            Walls.RestoredWith(tile, _restored);
            foreach (int f in _restored)
                if (Inside(f, players)) return true;
            // Blast holes: thin or small openings next to it fill in when it comes back.
            Walls.CellsFilledByRestoring(tile, _filled);
            foreach (Aabb cell in _filled)
                for (int i = 0; i < players.Count; i++)
                    if (players[i].Alive && cell.Overlaps(players[i].Box)) return true;
            return false;
        }

        private readonly List<int> _restored = new List<int>();
        private readonly List<Aabb> _filled = new List<Aabb>();

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
        public HealResult Check(ITerrain terrain, int player, Vec3d eye, bool inSculptMode, int tile, HealKind kind, int bar, double reachSlack = 0)
        {
            if (!inSculptMode) return HealResult.NotInSculptMode;
            if (tile < 0 || tile >= Walls.Count) return HealResult.NoTile;
            HealResult r = StateCheck(Walls[tile], kind, Config);
            if (r != HealResult.Ok) return r;
            int healer = HealerOf(tile);
            if (healer != 0 && healer != player) return HealResult.TileBusy;
            if (DistanceTo(tile, eye) > Config.HealRange + reachSlack) return HealResult.OutOfRange;
            int refund = _byPlayer.TryGetValue(player, out HealJob mine) ? mine.Cost : 0; // replacing your own heal refunds it first
            if (bar + refund < CostOf(kind, Config)) return HealResult.NotEnoughBar;
            if (terrain != null && !CanSee(terrain, tile, eye)) return HealResult.NoLineOfSight;
            return HealResult.Ok;
        }

        // ------------------------------------------------------------------ server

        /// <summary>
        /// A heal press. Pressing the tile you are already healing (same kind) does nothing. A press on another tile
        /// replaces your heal (the old one is refunded). Logs Began or Rejected.
        /// </summary>
        public HealResult TryBegin(ITerrain terrain, in HealerStatus who, int tile, HealKind kind, SculptBar bar, int tick, double reachSlack = 0)
        {
            if (_byPlayer.TryGetValue(who.Id, out HealJob cur) && cur.Tile == tile && cur.Kind == kind) return HealResult.Ok;
            HealResult r = who.Alive ? Check(terrain, who.Id, who.Eye, who.InSculptMode, tile, kind, bar.Value, reachSlack) : HealResult.Stopped;
            if (r != HealResult.Ok)
            {
                Events.Add(new HealEvent { Type = HealEventType.Rejected, Player = who.Id, Tile = Math.Max(0, tile), Kind = kind, Tick = tick, Result = r });
                return r;
            }
            if (_byPlayer.ContainsKey(who.Id)) Cancel(who.Id, HealResult.Replaced, bar, tick);
            int cost = CostOf(kind, Config);
            bar.Spend(cost, tick);
            var job = new HealJob { Player = who.Id, Tile = tile, Kind = kind, StartTick = tick, EndTick = tick + TicksOf(kind, Config), Cost = cost };
            _byPlayer[who.Id] = job;
            _healerOfTile[tile] = who.Id;
            Events.Add(new HealEvent { Type = HealEventType.Began, Player = who.Id, Tile = tile, Kind = kind, Tick = tick, EndTick = job.EndTick });
            return HealResult.Ok;
        }

        /// <summary>Stops a player's heal and refunds it (no-op if they have none). <paramref name="bar"/> may be null (they left).</summary>
        public void Cancel(int player, HealResult reason, SculptBar bar, int tick)
        {
            if (!_byPlayer.TryGetValue(player, out HealJob j)) return;
            _byPlayer.Remove(player);
            _healerOfTile.Remove(j.Tile);
            bar?.Refund(j.Cost);
            Events.Add(new HealEvent { Type = HealEventType.Cancelled, Player = player, Tile = j.Tile, Kind = j.Kind, Tick = tick, EndTick = j.EndTick, Result = reason });
        }

        /// <summary>
        /// One server tick: a heal whose healer died, left sculpt mode or the game, or walked out of range (range plus
        /// <see cref="BreachConfig.HealLeash"/>) is cancelled and refunded. One that reaches its end tick finishes
        /// (Patched at full patched health, or Reinforced at full) unless someone is inside the tile: then it is
        /// cancelled and refunded.
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
                if (DistanceTo(j.Tile, players[idx].Eye) > Config.HealRange + Config.HealLeash) { Cancel(id, HealResult.LeftRange, bar, tick); continue; }
                if (tick < j.EndTick) continue;
                if (Occupied(j.Tile, players)) { Cancel(id, HealResult.PlayerInside, bar, tick); continue; }
                _byPlayer.Remove(id);
                _healerOfTile.Remove(j.Tile);
                if (j.Kind == HealKind.Reinforce) Walls.Set(j.Tile, TileState.Reinforced, Config.ReinforcedHealth, TileCause.Reinforce, id);
                else Walls.Set(j.Tile, TileState.Patched, Config.PatchedHealth, TileCause.Heal, id);
                Events.Add(new HealEvent { Type = HealEventType.Completed, Player = id, Tile = j.Tile, Kind = j.Kind, Tick = tick, EndTick = j.EndTick });
            }
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
                    _byPlayer[e.Player] = new HealJob { Player = e.Player, Tile = e.Tile, Kind = e.Kind, StartTick = e.Tick, EndTick = e.EndTick, Cost = CostOf(e.Kind, Config) };
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
