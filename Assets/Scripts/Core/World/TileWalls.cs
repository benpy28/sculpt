using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>What a wall tile is right now (Ben 2026-11-21).</summary>
    public enum TileState : byte
    {
        /// <summary>Full health, as built.</summary>
        Intact = 0,
        /// <summary>An intact tile below half health: shows cracks. Can be charged through and healed.</summary>
        Damaged = 1,
        /// <summary>Gone: no collision, bullets and players pass. Can be healed.</summary>
        Broken = 2,
        /// <summary>Healed: lower max health, clearly looks patched. Can be charged through.</summary>
        Patched = 3,
        /// <summary>Reinforced: high health.</summary>
        Reinforced = 4,
    }

    /// <summary>What part of a building a tile is (its look, and which rules apply). Step 8 (Ben 2026-11-21).</summary>
    public enum TileKind : byte
    {
        /// <summary>A 1.5 m wall tile.</summary>
        Wall = 0,
        /// <summary>A 1.5 m floor or roof tile (0.3 m thick).</summary>
        Floor = 1,
        /// <summary>A segment of a stair flight (a wedge: collides as part of the ramp, drawn as steps).</summary>
        Stair = 2,
        /// <summary>A segment of a plain ramp (a wedge).</summary>
        Ramp = 3,
        /// <summary>Frame: column pieces, beam strips over the wall lines, and the nodes where they meet.</summary>
        Frame = 4,
    }

    /// <summary>Why a tile changed (for effects and logs).</summary>
    public enum TileCause : byte
    {
        Built = 0,
        Bullet = 1,
        Grenade = 2,
        Punch = 3,
        Charge = 4,
        Heal = 5,
        Reinforce = 6,
        /// <summary>A client copying the server's state.</summary>
        Server = 7,
    }

    /// <summary>
    /// Every destruction, healing and bar number (Ben 2026-11-21: all in one place). The Game layer's Breach Settings
    /// asset writes into this. Health is in whole points.
    /// </summary>
    public sealed class BreachConfig
    {
        /// <summary>Wall tiles are 1.5 m squares; a storey is 3 tiles.</summary>
        public double TileSize = 1.5;
        /// <summary>How thick a wall of tiles is (on the 0.3 m lattice).</summary>
        public double TileThickness = 0.3;

        public int IntactHealth = 100;
        /// <summary>An intact tile below this fraction of its health shows as Damaged.</summary>
        public double DamagedBelowFraction = 0.5;
        public int PatchedHealth = 50;
        public int ReinforcedHealth = 200;

        /// <summary>Tile damage per bullet (per pellet for the pump). Flat, no falloff, so a wall always takes the same
        /// number of hits: AR 8 bullets, SMG 15, pistol 6, pump 2 point-blank shots for an intact tile.</summary>
        public int PumpPelletTileDamage = 6;
        public int SmgTileDamage = 7;
        public int ArTileDamage = 13;
        public int PistolTileDamage = 17;

        /// <summary>Grenade blast damage to every tile in its radius: breaks Intact, Damaged and Patched tiles at once;
        /// a Reinforced tile (200) survives one blast with 50 left.</summary>
        public int GrenadeTileDamage = 150;
        /// <summary>Punch (Ben: half an unreinforced wall, 50) and what it does to a Reinforced tile.</summary>
        public int PunchTileDamage = 50;
        public int PunchReinforcedTileDamage = 25;

        /// <summary>Charge: moving at least this share of top sprint speed into Damaged or Patched tiles breaks them.</summary>
        public double ChargeSpeedFraction = 0.8;
        /// <summary>Share of horizontal speed kept when you charge through (Ben: "keeps most speed").</summary>
        public double ChargeKeep = 0.85;
        /// <summary>You must hit the wall within this many degrees of head-on (a glancing scrape along it never breaks it).</summary>
        public double ChargeMaxAngleDegrees = 60;

        // ---- Healing and the bar (Ben 2026-11-21). Bar values are basis points (10000 = 100%). ----
        /// <summary>A heal takes this long (0.8 s) and the tile comes back Patched.</summary>
        public int HealTicks = 48;
        /// <summary>Reinforcing takes this long (Claude: same as a heal; Ben only set the cost).</summary>
        public int ReinforceTicks = 48;
        public int HealCost = 1000;      // 10%
        public int ReinforceCost = 2500; // 25%
        /// <summary>Sculpt range for healing, metres (eye to the nearest point of the tile).</summary>
        public double HealRange = 6.0;
        /// <summary>A running heal is cancelled (and refunded) once the healer is this much further away than the range.</summary>
        public double HealLeash = 1.5;
        /// <summary>The bar refills by itself at this many basis points per second (200 = 2%/s, empty to full in 50 s).</summary>
        public int BarRefillPerSecond = 200;
        /// <summary>The refill starts this many ticks after a spend (0: at once).</summary>
        public int BarRefillDelayTicks = 0;
        /// <summary>Bar gained per 10 damage dealt to enemy players (100 = 1%).</summary>
        public int BarPer10Damage = 100;

        /// <summary>Writes the bar refill numbers into the sculpt config the bars tick with.</summary>
        public void ApplyBarTo(SculptConfig s)
        {
            s.RegenDelayTicks = Math.Max(0, BarRefillDelayTicks);
            // Full bar over (BarMax / perSecond) seconds; SculptBar spreads it exactly over that many ticks.
            s.RegenFullTicks = BarRefillPerSecond <= 0 ? int.MaxValue / 4
                : Math.Max(1, (int)Math.Round((double)SculptConfig.BarMax * SimTime.TicksPerSecond / BarRefillPerSecond));
        }

        public int TileDamageOf(Weapon w)
        {
            switch (w)
            {
                case Weapon.Pump: return PumpPelletTileDamage;
                case Weapon.Smg: return SmgTileDamage;
                case Weapon.Ar: return ArTileDamage;
                case Weapon.Pistol: return PistolTileDamage;
                default: return 0;
            }
        }

        public int MaxHealthOf(TileState s)
        {
            switch (s)
            {
                case TileState.Intact:
                case TileState.Damaged: return IntactHealth;
                case TileState.Patched: return PatchedHealth;
                case TileState.Reinforced: return ReinforcedHealth;
                default: return 0;
            }
        }
    }

    /// <summary>One wall tile: which brush it is, its state and health. Plain data.</summary>
    public struct WallTile
    {
        public int Brush;
        public TileState State;
        public int Health;
        /// <summary>The horizontal axis the wall faces along (East = the tile is thin in X, North = thin in Z).</summary>
        public Dir Facing;
        /// <summary>True once the tile has ever left its built state (sent to late joiners).</summary>
        public bool Changed;
        public TileKind Kind;
        /// <summary>
        /// A follower (thin frame: column pieces, beam strips, nodes) takes no damage of its own: it is Broken exactly
        /// when every tile it links to is Broken, and comes back whole when any of them comes back. So a thin piece only
        /// ever goes when the big tiles around it have gone, and merges their holes into one large opening: never a slit
        /// (Ben: "you arent shot through a tiny gap"). Null for an ordinary tile.
        /// </summary>
        public int[] Links;
        public bool IsFollower => Links != null;
    }

    /// <summary>One change to one tile, in the order they happened.</summary>
    public struct TileChange
    {
        public int Tick;
        public int Tile;
        public TileState From;
        public TileState To;
        public int Health;
        public TileCause Cause;
        /// <summary>Player who caused it (0 = nobody).</summary>
        public int By;
    }

    /// <summary>
    /// The breakable wall tiles of a <see cref="BrushWorld"/>. Tile state is plain data (tile id, state, health) and
    /// every change goes through <see cref="Set"/>, which also switches the tile's collision. The server owns the truth;
    /// clients copy it from the server's messages through the same function.
    /// </summary>
    public sealed class TileWalls
    {
        public readonly BrushWorld World;
        public readonly BreachConfig Config;
        private readonly List<WallTile> _tiles = new List<WallTile>();
        private readonly Dictionary<int, int> _byBrush = new Dictionary<int, int>();
        /// <summary>Changes since the owner last cleared them (the server sends and clears them every tick).</summary>
        public readonly List<TileChange> Changes = new List<TileChange>();
        /// <summary>Bumped on every change (views poll it).</summary>
        public int Revision { get; private set; }
        /// <summary>The tick stamped on changes (the server sets it each tick).</summary>
        public int Tick;
        /// <summary>Tiles broken by a charge, and the tick (so a replay of that tick charges through the same tile).</summary>
        private readonly Dictionary<int, int> _chargedAt = new Dictionary<int, int>();

        /// <summary>The tick this tile was charged through, or int.MinValue (never, or it has been healed since).</summary>
        public int ChargedAt(int tile) => _chargedAt.TryGetValue(tile, out int t) ? t : int.MinValue;

        internal TileWalls(BrushWorld world, BreachConfig config)
        {
            World = world;
            Config = config;
        }

        public int Count => _tiles.Count;
        public WallTile this[int i] => _tiles[i];
        public Aabb BoundsOf(int tile) => World[_tiles[tile].Brush].Bounds;

        /// <summary>Tile index of a brush, or -1 if it is not a tile.</summary>
        public int TileOfBrush(int brush) => _byBrush.TryGetValue(brush, out int t) ? t : -1;

        /// <summary>The tile a terrain hit landed on, or -1 (<see cref="TerrainHit.Brush"/> is brush index + 1).</summary>
        public int TileOfHit(in TerrainHit hit) => hit.Hit && hit.Brush > 0 ? TileOfBrush(hit.Brush - 1) : -1;

        /// <summary>Adds one box tile with the given bounds, Intact at full health. Floors face Up.</summary>
        public int AddTile(Aabb bounds, Dir facing, TileKind kind = TileKind.Wall) => AddShape(Brush.Box(bounds, BrushKind.Tile), facing, kind, null);

        /// <summary>Adds one wedge tile (a stair or ramp segment) rising toward <paramref name="rise"/>.</summary>
        public int AddWedgeTile(Aabb bounds, Dir rise, TileKind kind) => AddShape(Brush.Wedge(bounds, rise, BrushKind.Tile), Dir.Up, kind, null);

        /// <summary>
        /// Adds a frame follower linked to <paramref name="links"/> (see <see cref="WallTile.Links"/>). With no links it
        /// is an ordinary breakable frame tile (a free-standing column piece has nothing around it to leave a slit in).
        /// </summary>
        public int AddFollower(Aabb bounds, IReadOnlyCollection<int> links)
        {
            int[] l = null;
            if (links != null && links.Count > 0)
            {
                l = new int[links.Count];
                int k = 0;
                foreach (int t in links)
                {
                    if (t < 0 || t >= _tiles.Count) throw new ArgumentException("A follower links to tiles that already exist.", nameof(links));
                    l[k++] = t;
                }
                Array.Sort(l);
            }
            int id = AddShape(Brush.Box(bounds, BrushKind.Tile), Dir.Up, TileKind.Frame, l);
            if (l != null)
                foreach (int t in l)
                {
                    if (!_followers.TryGetValue(t, out var f)) _followers[t] = f = new List<int>(2);
                    if (!f.Contains(id)) f.Add(id);
                }
            return id;
        }

        private int AddShape(Brush b, Dir facing, TileKind kind, int[] links)
        {
            int brush = World.Add(b);
            int id = _tiles.Count;
            _tiles.Add(new WallTile { Brush = brush, State = TileState.Intact, Health = Config.IntactHealth, Facing = facing, Kind = kind, Links = links });
            _byBrush[brush] = id;
            return id;
        }

        private readonly Dictionary<int, List<int>> _followers = new Dictionary<int, List<int>>();
        private static readonly List<int> NoFollowers = new List<int>();

        /// <summary>The followers linked to a tile (empty if none).</summary>
        public IReadOnlyList<int> FollowersOf(int tile) => _followers.TryGetValue(tile, out var f) ? f : NoFollowers;

        public bool IsFollower(int tile) => _tiles[tile].Links != null;

        /// <summary>The brush shape of a tile (a box, or a wedge for stair and ramp segments).</summary>
        public Brush ShapeOf(int tile) => World[_tiles[tile].Brush];

        /// <summary>
        /// The broken followers that come back if <paramref name="tile"/> comes back (directly or through other followers),
        /// so a heal can check nobody is standing in them.
        /// </summary>
        public void RestoredWith(int tile, List<int> into)
        {
            into.Clear();
            Collect(tile, into);
        }

        private void Collect(int tile, List<int> into)
        {
            foreach (int f in FollowersOf(tile))
            {
                if (_tiles[f].State != TileState.Broken || into.Contains(f)) continue;
                into.Add(f);
                Collect(f, into);
            }
        }

        /// <summary>
        /// A wall of <paramref name="across"/> x <paramref name="up"/> tiles with its lower corner at
        /// <paramref name="corner"/>. <paramref name="along"/> is East (the wall runs along +X, thin in Z from corner.Z)
        /// or North (runs along +Z, thin in X from corner.X). Returns the tile ids, row by row from the bottom.
        /// </summary>
        public int[] AddWall(Vec3d corner, Dir along, int across, int up)
        {
            if (along != Dir.East && along != Dir.North) throw new ArgumentException("A wall runs East or North from its corner.", nameof(along));
            double s = Config.TileSize, t = Config.TileThickness;
            var ids = new int[across * up];
            for (int j = 0; j < up; j++)
                for (int i = 0; i < across; i++)
                {
                    Vec3d lo = along == Dir.East
                        ? new Vec3d(corner.X + i * s, corner.Y + j * s, corner.Z)
                        : new Vec3d(corner.X, corner.Y + j * s, corner.Z + i * s);
                    Vec3d hi = along == Dir.East ? lo + new Vec3d(s, s, t) : lo + new Vec3d(t, s, s);
                    ids[j * across + i] = AddTile(new Aabb(lo, hi), along == Dir.East ? Dir.North : Dir.East);
                }
            return ids;
        }

        /// <summary>
        /// THE one function that changes a tile. Clamps health to the state's range (Broken is always 0, every other
        /// state keeps at least 1), switches collision (only Broken is not solid), logs the change. No-op if nothing
        /// differs.
        /// </summary>
        public void Set(int tile, TileState state, int health, TileCause cause, int by = 0)
        {
            WallTile t = _tiles[tile];
            if (state == TileState.Broken) health = 0;
            else health = Math.Max(1, Math.Min(Config.MaxHealthOf(state), health));
            if (t.State == state && t.Health == health) return;
            TileState from = t.State;
            t.State = state;
            t.Health = health;
            if (cause != TileCause.Built) t.Changed = true;
            _tiles[tile] = t;
            if ((from == TileState.Broken) != (state == TileState.Broken)) World.SetSolid(t.Brush, state != TileState.Broken);
            if (state == TileState.Broken && cause == TileCause.Charge) _chargedAt[tile] = Tick;
            else _chargedAt.Remove(tile);
            Changes.Add(new TileChange { Tick = Tick, Tile = tile, From = from, To = state, Health = health, Cause = cause, By = by });
            Revision++;
            if ((from == TileState.Broken) != (state == TileState.Broken)) Propagate(tile, cause, by);
        }

        /// <summary>Brings the followers of a tile in line with their links (Broken exactly when all links are Broken).</summary>
        private void Propagate(int tile, TileCause cause, int by)
        {
            if (!_followers.TryGetValue(tile, out var list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                int f = list[i];
                WallTile ft = _tiles[f];
                bool all = true;
                foreach (int l in ft.Links)
                    if (_tiles[l].State != TileState.Broken) { all = false; break; }
                if (all && ft.State != TileState.Broken) Set(f, TileState.Broken, 0, cause, by);
                else if (!all && ft.State == TileState.Broken) Set(f, TileState.Intact, Config.IntactHealth, cause, by);
            }
        }

        /// <summary>
        /// Takes <paramref name="amount"/> health off a tile. At 0 it breaks. An intact tile below half health becomes
        /// Damaged; Patched and Reinforced keep their state until they break. Broken tiles ignore damage.
        /// Returns true if this broke the tile.
        /// </summary>
        public bool Damage(int tile, int amount, TileCause cause, int by = 0)
        {
            WallTile t = _tiles[tile];
            if (amount <= 0 || t.State == TileState.Broken || t.Links != null) return false; // followers only go with their links
            AfterDamage(t.State, t.Health, amount, Config, out TileState s, out int h);
            Set(tile, s, h, cause, by);
            return s == TileState.Broken;
        }

        /// <summary>The damage rule on its own (pure).</summary>
        public static void AfterDamage(TileState state, int health, int amount, BreachConfig c, out TileState next, out int nextHealth)
        {
            next = state;
            nextHealth = health;
            if (state == TileState.Broken || amount <= 0) return;
            nextHealth = Math.Max(0, health - amount);
            if (nextHealth == 0) { next = TileState.Broken; return; }
            if ((state == TileState.Intact || state == TileState.Damaged) && nextHealth < c.IntactHealth * c.DamagedBelowFraction)
                next = TileState.Damaged;
        }

        /// <summary>
        /// Applies one shot's pellets to the tiles they hit (flat per-weapon tile damage per pellet, summed per tile).
        /// Players and plain brushes are ignored. Returns how many tiles broke.
        /// </summary>
        public int ApplyShot(IReadOnlyList<BulletHit> pellets, Weapon weapon, int by)
        {
            int per = Config.TileDamageOf(weapon);
            if (per <= 0 || pellets == null) return 0;
            int broke = 0;
            for (int i = 0; i < pellets.Count; i++)
            {
                BulletHit h = pellets[i];
                if (h.Kind != HitKind.Terrain || h.Brush <= 0) continue;
                int tile = TileOfBrush(h.Brush - 1);
                if (tile < 0) continue;
                if (Damage(tile, per, TileCause.Bullet, by)) broke++;
            }
            return broke;
        }

        /// <summary>Tiles whose bounds touch <paramref name="box"/> (any state), in no particular order.</summary>
        public void TilesNear(Aabb box, List<int> into)
        {
            into.Clear();
            var brushes = _nearScratch ??= new List<int>();
            World.BrushesNear(box, brushes);
            foreach (int b in brushes)
            {
                int t = TileOfBrush(b);
                if (t >= 0 && World[b].Bounds.Overlaps(box) && !into.Contains(t)) into.Add(t);
            }
        }
        [ThreadStatic] private static List<int> _nearScratch;

        /// <summary>The building world's tiles behind a terrain (unwraps <see cref="CompositeTerrain"/>), or null.</summary>
        public static TileWalls Find(ITerrain terrain)
        {
            switch (terrain)
            {
                case BrushWorld w: return w.Tiles;
                case CompositeTerrain c: return Find(c.B) ?? Find(c.A);
                default: return null;
            }
        }
    }
}
