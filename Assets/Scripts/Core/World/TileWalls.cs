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

        /// <summary>Adds one tile with the given bounds, Intact at full health.</summary>
        public int AddTile(Aabb bounds, Dir facing)
        {
            int brush = World.Add(Brush.Box(bounds, BrushKind.Tile));
            int id = _tiles.Count;
            _tiles.Add(new WallTile { Brush = brush, State = TileState.Intact, Health = Config.IntactHealth, Facing = facing });
            _byBrush[brush] = id;
            return id;
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
            Changes.Add(new TileChange { Tick = Tick, Tile = tile, From = from, To = state, Health = health, Cause = cause, By = by });
            Revision++;
        }

        /// <summary>
        /// Takes <paramref name="amount"/> health off a tile. At 0 it breaks. An intact tile below half health becomes
        /// Damaged; Patched and Reinforced keep their state until they break. Broken tiles ignore damage.
        /// Returns true if this broke the tile.
        /// </summary>
        public bool Damage(int tile, int amount, TileCause cause, int by = 0)
        {
            WallTile t = _tiles[tile];
            if (amount <= 0 || t.State == TileState.Broken) return false;
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
