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
        /// <summary>A door leaf slid open (Ben 2026-11-25: doors you open and close): no collision, bullets and players
        /// pass, its health is kept for when it closes. Only door leaves are ever in this state (see <see cref="DoorRules"/>).</summary>
        DoorOpen = 5,
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
        /// <summary>A door leaf (1.5 m wide, 3 m tall, two to a doorway): weak on purpose, an easy way in (Ben 2026-11-25:
        /// "they usually just get broken instantly"). Takes <see cref="BreachConfig.DoorDamageScale"/> times the damage,
        /// breaks whole (no blast holes), and a charge goes through one even when it isn't cracked.</summary>
        Door = 5,
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
        /// <summary>A door opened or closed.</summary>
        Door = 8,
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
        /// <summary>Door leaves take this many times the damage a wall tile would: with walls by cell (AR 50, punch 50 per hit) one hit of anything breaks a leaf.</summary>
        public int DoorDamageScale = 4;
        /// <summary>How far from the eye a door can be opened or shut (metres to the nearest point of a leaf).</summary>
        public double DoorReach = 2.5;

        /// <summary>
        /// Wall damage per bullet (per pellet for the pump), dealt to every cell of the wall within the weapon's hit
        /// radius of where it landed (Ben 2026-11-25: damage by area, not by tile; "a shotgun pump should one shot any
        /// wall and AR should 2 tap"). Flat, no falloff, so a wall always takes the same number of hits on one spot:
        /// pump 1 shot (9 x 14 = 126; 2 on Reinforced), AR 2 (4 on Reinforced, 1 on Patched), SMG 4, pistol 3.
        /// </summary>
        public int PumpPelletTileDamage = 14;
        public int SmgTileDamage = 25;
        public int ArTileDamage = 50;
        public int PistolTileDamage = 34;
        /// <summary>How far round each hit the damage reaches, metres (the cracks you see, and the hole the finishing
        /// hit opens). A pump shot reaches round its whole pattern (at least this, at most <see cref="PumpMaxRadius"/>).</summary>
        /// <remarks>Ben 2026-11-25: "no [hole] way bigger than a body from far away or smaller than a chest from close
        /// up". A pump hole's radius is the pattern's spread clamped to 0.65..0.7 m, so every pump hole is about
        /// 1.2 to 1.7 m^2 (was 0.55..0.9: about 1 m^2 point blank, up to 2.6 m^2 from range). Above 0.72 the tidy
        /// rules could leave no hole at all for a shot centred on a tile.</remarks>
        public double PumpHitRadius = 0.65;
        public double PumpMaxRadius = 0.7;
        public double SmgHitRadius = 0.35;
        public double ArHitRadius = 0.45;
        public double PistolHitRadius = 0.4;
        public double PunchHitRadius = 0.5;
        /// <summary>A hit's area has a rough edge: each spot on a wall reaches up to this much further or shorter (fixed
        /// per spot, the same on every machine).</summary>
        public double HitJitter = 0.06;

        public double HitRadiusOf(Weapon w)
        {
            switch (w)
            {
                case Weapon.Pump: return PumpHitRadius;
                case Weapon.Smg: return SmgHitRadius;
                case Weapon.Ar: return ArHitRadius;
                case Weapon.Pistol: return PistolHitRadius;
                default: return ArHitRadius;
            }
        }

        /// <summary>Grenade blast damage to every tile in its radius: breaks Intact, Damaged and Patched tiles at once;
        /// a Reinforced tile (200) survives one blast with 50 left.</summary>
        public int GrenadeTileDamage = 150;
        /// <summary>C4 is the breaching tool: it breaks every tile in its blast, Reinforced (200) included.</summary>
        public int C4TileDamage = 220;
        /// <summary>A launcher round breaks Intact, Damaged and Patched tiles in its (slightly smaller) blast; a
        /// Reinforced tile keeps 90.</summary>
        public int LauncherTileDamage = 110;

        /// <summary>Blast tile damage of a projectile kind.</summary>
        public int BlastTileDamage(ThrowKind k) => k == ThrowKind.C4 ? C4TileDamage : k == ThrowKind.Launcher ? LauncherTileDamage : GrenadeTileDamage;
        /// <summary>Punch (Ben: half an unreinforced wall, 50): two punches on one spot open it, a Reinforced spot takes four.</summary>
        public int PunchTileDamage = 50;

        /// <summary>Charge: moving at least this share of top sprint speed into Damaged or Patched tiles breaks them.</summary>
        public double ChargeSpeedFraction = 0.8;
        /// <summary>Share of horizontal speed kept when you charge through (Ben: "keeps most speed").</summary>
        public double ChargeKeep = 0.85;
        /// <summary>A charge bursts through when at least this share of the wall in your way has been hit (and none of it
        /// is reinforced). 0.4: one AR bullet at chest height lets a sprinting player through, any hit a sliding one.</summary>
        public double ChargeHitShare = 0.4;
        /// <summary>A charge counts every patched cell as hit (the old rule). Off (balance pass 2026-11-25, Ben): a freshly
        /// healed spot is sound again until something hits it, so a heal really keeps a sprint out.</summary>
        public bool ChargePatchedCounts = false;
        /// <summary>You must hit the wall within this many degrees of head-on (a glancing scrape along it never breaks it).</summary>
        public double ChargeMaxAngleDegrees = 60;

        // ---- Healing and the bar (Ben 2026-11-21). Bar values are basis points (10000 = 100%). ----
        /// <summary>A heal takes this long and what it fills comes back Patched (Ben 2026-11-25: "almost instantly").</summary>
        public double HealSeconds = 0.2;
        /// <summary>Reinforcing takes this long (Claude: unchanged; Ben only set the cost).</summary>
        public double ReinforceSeconds = 0.8;
        public int HealTicks { get => Math.Max(1, SimTime.SecondsToTicks(HealSeconds)); set => HealSeconds = value / (double)SimTime.TicksPerSecond; }
        public int ReinforceTicks { get => Math.Max(1, SimTime.SecondsToTicks(ReinforceSeconds)); set => ReinforceSeconds = value / (double)SimTime.TicksPerSecond; }
        /// <summary>
        /// What a heal covers (Ben 2026-11-25: "either heal a large chunk almost instantly or the full hole if its smaller
        /// than a person roughly"): everything within this radius of where you aim, plus the whole hole you aim into if
        /// it is no bigger than <see cref="SmallHoleArea"/>.
        /// </summary>
        public double HealRadius = 0.95;
        /// <summary>A hole up to this size (m^2) is healed whole: a broken tile (2.25), a pump hole, a punch hole.
        /// Bigger (a grenade's) closes a chunk per heal.</summary>
        public double SmallHoleArea = 2.3;
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
        /// <summary>The 1v1 anti-hiding ping (Breach Settings > Ping).</summary>
        public readonly PingConfig Ping = new PingConfig();

        // ---- Blast holes (Ben 2026-11-25): holes follow the hit, not the tile grid. ----
        /// <summary>Off: damage is per tile and a tile that breaks goes whole (the old rule).</summary>
        public bool BlastHoles = true;
        /// <summary>A grenade's blast damages every cell within this radius (a hole about 4.4 m across).</summary>
        public double GrenadeBlastRadius = 2.2;
        /// <summary>C4's hole disc around the charge (a doorway-sized breach) and a launcher round's (smaller).</summary>
        public double C4BlastRadius = 2.2;
        public double LauncherBlastRadius = 1.7;

        /// <summary>The hole disc radius of a projectile kind's blast.</summary>
        public double BlastHoleRadius(ThrowKind k) => k == ThrowKind.C4 ? C4BlastRadius : k == ThrowKind.Launcher ? LauncherBlastRadius : GrenadeBlastRadius;
        /// <summary>How rough a blast's edge is: each cell's reach changes by up to this much (fixed per spot on a wall).</summary>
        public double BlastJitter = 0.16;
        /// <summary>No hole smaller than this (Ben: about half a body), in square metres.</summary>
        public double MinHoleArea = 0.63;
        /// <summary>A tile with less than this share of its cells left goes entirely.</summary>
        public double BreakBelowFraction = 0.25;

        /// <summary>The most health a cell can have (cell health is stored in a byte).</summary>
        public int MaxCellHealth(bool patched, bool reinforced) =>
            Math.Max(1, Math.Min(255, reinforced ? ReinforcedHealth : patched ? PatchedHealth : IntactHealth));

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
                case TileState.DoorOpen: return Math.Max(IntactHealth, ReinforcedHealth); // kept as it was
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
        /// <summary>Blast holes: cells of this tile that are blown out (see <see cref="Brush.Open"/>). Always 0 when Broken.</summary>
        public ulong Open;
        public bool Holed => Open != 0;
        /// <summary>Celled tiles: standing cells that were healed (cement, lower max health) and reinforced (steel, high
        /// health). Never both; 0 on open cells. Each cell's health lives in <see cref="TileWalls.CellHealth"/>.</summary>
        public ulong Patch, Steel;
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
        /// <summary>The tile's blown-out cells after the change.</summary>
        public ulong Open;
        /// <summary>Celled tiles: healed and reinforced cells after the change.</summary>
        public ulong Patch, Steel;
        /// <summary>Celled tiles: every cell's health after the change (64 bytes, a copy), or null for a plain tile.</summary>
        public byte[] Hp;
    }

    /// <summary>
    /// The breakable wall tiles of a <see cref="BrushWorld"/>. Tile state is plain data (tile id, state, health) and
    /// every change goes through <see cref="Set"/>, which also switches the tile's collision. The server owns the truth;
    /// clients copy it from the server's messages through the same function.
    /// </summary>
    public sealed partial class TileWalls
    {
        public readonly BrushWorld World;
        public readonly BreachConfig Config;
        private readonly CowList<WallTile> _tiles = new CowList<WallTile>();
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

        /// <summary>A copy for <see cref="BrushWorld.ShareCopy"/>: fixed data shared, tile state copied page by page on write.</summary>
        internal TileWalls(BrushWorld world, TileWalls from)
        {
            World = world;
            Config = from.Config;
            from.EnsureEdges(); // once, shared (which tile lies across each edge never changes)
            _edge = from._edge;
            _tiles = from._tiles.ShareCopy();
            _hp = from._hp.ShareCopy();
            _byBrush = from._byBrush;
            _doors = from._doors;
            _doorPartner = from._doorPartner;
            _followers = from._followers;
            Changes.AddRange(from.Changes);
            Revision = from.Revision;
            Tick = from.Tick;
            CopyOnly = from.CopyOnly;
            foreach (var kv in from._chargedAt) _chargedAt[kv.Key] = kv.Value;
            foreach (var kv in from._shutAs) _shutAs[kv.Key] = kv.Value;
            _touched.AddRange(from._touched);
        }

        private readonly List<int> _touched = new List<int>();

        /// <summary>
        /// Every tile play has ever changed (in the order they first changed): the rest are exactly as built. Lets the
        /// replay state hash skip the untouched hundreds of thousands (map v3: it walked all of them, ~8 ms in Mono).
        /// </summary>
        public IReadOnlyList<int> Touched => _touched;

        /// <summary>Notes a tile's first change by play (every write that isn't the build goes through here).</summary>
        internal void Touch(int tile, ref WallTile t)
        {
            if (t.Changed) return;
            t.Changed = true;
            _touched.Add(tile);
        }

        public int Count => _tiles.Count;
        public WallTile this[int i] => _tiles[i];
        /// <summary>Pages of tile state this copy holds alone (see <see cref="BrushWorld.ShareCopy"/>).</summary>
        public int OwnTilePages => _tiles.OwnPages + _hp.OwnPages;
        public Aabb BoundsOf(int tile) => World[_tiles[tile].Brush].Bounds;

        /// <summary>Tile index of a brush, or -1 if it is not a tile.</summary>
        /// <summary>Not there for collision, bullets and sight: broken, or a door slid open.</summary>
        public static bool Gone(TileState s) => s == TileState.Broken || s == TileState.DoorOpen;

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
            if (!b.IsRamp && links == null && (kind == TileKind.Wall || kind == TileKind.Floor))
            {
                // Face axes for the cell grid: walls facing North are thin in Z (face X, Y), East thin in X (Z, Y);
                // floors are thin in Y (X, Z).
                if (kind == TileKind.Floor) World.SetCells(brush, 0, 2);
                else if (facing == Dir.North) World.SetCells(brush, 0, 1);
                else World.SetCells(brush, 2, 1);
            }
            int id = _tiles.Count;
            _tiles.Add(new WallTile { Brush = brush, State = TileState.Intact, Health = Config.IntactHealth, Facing = facing, Kind = kind, Links = links });
            _byBrush[brush] = id;
            if (kind == TileKind.Door) _doors.Add(id);
            GrowCells(id);
            return id;
        }

        private readonly List<int> _doors = new List<int>();
        private readonly Dictionary<int, TileState> _shutAs = new Dictionary<int, TileState>();

        /// <summary>The state an open door leaf takes when it shuts: what it was when it opened (so a patched or
        /// reinforced door stays so), or for a leaf seen only open (a late joiner) the state its health implies.</summary>
        public TileState ShutStateOf(int leaf)
        {
            int h = _tiles[leaf].Health;
            if (!_shutAs.TryGetValue(leaf, out TileState s)) return DoorRules.ShutState(Config, h);
            if (s == TileState.Intact && h < Config.IntactHealth * Config.DamagedBelowFraction) return TileState.Damaged;
            return s;
        }
        private readonly Dictionary<int, int> _doorPartner = new Dictionary<int, int>();

        /// <summary>Every door leaf.</summary>
        public IReadOnlyList<int> Doors => _doors;

        /// <summary>Makes two leaves one door (they open and close together).</summary>
        public void PairDoor(int a, int b) { _doorPartner[a] = b; _doorPartner[b] = a; }

        /// <summary>The other leaf of a double door, or -1.</summary>
        public int DoorPartner(int leaf) => _doorPartner.TryGetValue(leaf, out int p) ? p : -1;

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
        /// THE one function that changes a tile by state. Clamps health to the state's range (Broken is always 0, every
        /// other state keeps at least 1), switches collision (only Broken is not solid), logs the change. No-op if nothing
        /// differs. On a celled tile (walls, floors) it writes every cell alike through <see cref="WriteCells"/>: the
        /// state's material and the health on every standing cell.
        /// </summary>
        /// <param name="open">The blown-out cells. Null keeps them, except that breaking, healing, reinforcing and a
        /// rebuild always make the tile whole again (a Broken tile has no cells left to speak of).</param>
        public void Set(int tile, TileState state, int health, TileCause cause, int by = 0, ulong? open = null)
        {
            WallTile t = _tiles[tile];
            ulong o = open ?? (cause == TileCause.Heal || cause == TileCause.Reinforce || cause == TileCause.Built ? 0ul : t.Open);
            if (HasCells(tile))
            {
                if (state == TileState.Broken) o = Brush.AllCells;
                bool patch = state == TileState.Patched, steel = state == TileState.Reinforced;
                byte hp = (byte)Math.Max(1, Math.Min(Config.MaxCellHealth(patch, steel), health));
                var cells = new byte[Brush.CellCount];
                for (int c = 0; c < Brush.CellCount; c++) cells[c] = hp;
                WriteCells(tile, o, patch ? Brush.AllCells : 0, steel ? Brush.AllCells : 0, cells, cause, by);
                return;
            }
            o = 0;
            if (state == TileState.Broken) health = 0;
            else health = Math.Max(1, Math.Min(Config.MaxHealthOf(state), health));
            if (t.State == state && t.Health == health && t.Open == o) return;
            TileState from = t.State;
            t.State = state;
            t.Health = health;
            t.Open = o;
            if (cause != TileCause.Built && !t.Changed) { t.Changed = true; _touched.Add(tile); }
            _tiles[tile] = t;
            if (state == TileState.DoorOpen && !Gone(from)) _shutAs[tile] = from; // what it is again when it shuts
            else if (state != TileState.DoorOpen) _shutAs.Remove(tile);
            if (Gone(from) != Gone(state)) World.SetSolid(t.Brush, !Gone(state));
            if (state == TileState.Broken && cause == TileCause.Charge) _chargedAt[tile] = Tick;
            else _chargedAt.Remove(tile);
            Changes.Add(new TileChange { Tick = Tick, Tile = tile, From = from, To = state, Health = health, Cause = cause, By = by });
            Revision++;
            _beyond.Clear(); // what lies past a tile grid's edge may have changed (frame follows tiles)
            if ((from == TileState.Broken) != (state == TileState.Broken)) Propagate(tile, cause, by);
            if (Gone(from) != Gone(state))
            {
                ShapeAround(tile); // a celled neighbour's corners can depend on this tile being there (a door too)
                // A plain tile came back (a door shut, a broken door or frame piece healed): a hole beside it that only
                // counted as fair because it opened onto this gap is now an opening of its own, so it must be big
                // enough or fill in, like any opening beside cells that come back.
                if (!Gone(state) && !CopyOnly && !_filling) FillBeside(tile, cause, by);
            }
        }

        /// <summary>Runs the small-hole fill on the celled tiles touching a plain tile that just came back.</summary>
        private void FillBeside(int tile, TileCause cause, int by)
        {
            var near = new List<int>();
            Aabb b = BoundsOf(tile);
            TilesNear(new Aabb(b.Min - new Vec3d(0.05, 0.05, 0.05), b.Max + new Vec3d(0.05, 0.05, 0.05)), near);
            near.Sort();
            foreach (int u in near)
                if (u != tile && HasCells(u) && _tiles[u].State != TileState.Broken && _tiles[u].Open != 0) FillSmallHoles(u, cause, by);
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
        /// Takes <paramref name="amount"/> health off the whole tile (every standing cell of a celled tile). At 0 it
        /// breaks. An intact tile below half health becomes Damaged; Patched and Reinforced keep their state until they
        /// break. Broken tiles ignore damage. Returns true if this broke the tile.
        /// </summary>
        public bool Damage(int tile, int amount, TileCause cause, int by = 0)
        {
            WallTile t = _tiles[tile];
            if (amount <= 0 || t.State == TileState.Broken || t.State == TileState.DoorOpen || t.Links != null) return false; // followers only go with their links
            if (HasCells(tile)) return DamageWholeCells(tile, amount, cause, by);
            if (t.Kind == TileKind.Door) amount *= Math.Max(1, Config.DoorDamageScale);
            AfterDamage(t.State, t.Health, amount, Config, out TileState s, out int h);
            Set(tile, s, h, cause, by);
            return s == TileState.Broken;
        }

        /// <summary>
        /// A hit on a tile at <paramref name="at"/>. Walls and floors take it by area (Ben 2026-11-25): every cell within
        /// <paramref name="radius"/> of the hit (a rough edge), across tile borders, loses <paramref name="amount"/>; cells
        /// that reach 0 open as a hole, never a pixel hole or a sliver (see <see cref="HitArea"/>). With no radius the
        /// AR's is used. Stair, ramp and frame tiles take it whole. Returns true if this opened anything.
        /// </summary>
        public bool Damage(int tile, int amount, Vec3d at, TileCause cause, int by = 0, double radius = -1)
        {
            WallTile t = _tiles[tile];
            if (amount <= 0 || t.State == TileState.Broken || t.State == TileState.DoorOpen || t.Links != null) return false;
            if (!HasCells(tile) || !Config.BlastHoles) return Damage(tile, amount, cause, by);
            return HitArea(tile, at, amount, radius > 0 ? radius : Config.ArHitRadius, cause, by);
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
        /// Applies one shot's pellets to the walls they hit. On walls and floors the pellets on one face count as one hit
        /// at the middle of where they landed, with their damage summed and an area that covers the pattern (so a pump
        /// blast opens one hole around its pattern, not nine small ones). Stair, ramp and frame tiles take the summed
        /// damage whole. Players and plain brushes are ignored. Returns how many hits opened something.
        /// </summary>
        public int ApplyShot(IReadOnlyList<BulletHit> pellets, Weapon weapon, int by)
        {
            int per = Config.TileDamageOf(weapon);
            if (per <= 0 || pellets == null) return 0;
            var order = _shotTiles ??= new List<int>();
            var dmg = _shotDmg ??= new Dictionary<int, int>();
            var at = _shotAt ??= new Dictionary<int, Vec3d>();
            var points = _shotPoints ??= new List<ShotPoint>();
            order.Clear();
            dmg.Clear();
            at.Clear();
            points.Clear();
            for (int i = 0; i < pellets.Count; i++)
            {
                BulletHit h = pellets[i];
                if (h.Kind != HitKind.Terrain || h.Brush <= 0) continue;
                int tile = TileOfBrush(h.Brush - 1);
                if (tile < 0) continue;
                // Key: the tile itself for a plain tile, or (negative) the face plane for a celled one.
                int key = HasCells(tile) && Config.BlastHoles ? -1 - PlaneOf(tile) : tile;
                if (!dmg.ContainsKey(key)) { order.Add(key); dmg[key] = 0; at[key] = Vec3d.Zero; }
                dmg[key] += per;
                at[key] = at[key] + h.Point;
                points.Add(new ShotPoint { Key = key, P = h.Point, Tile = tile });
            }
            int broke = 0;
            foreach (int key in order)
            {
                if (key >= 0) { if (Damage(key, dmg[key], TileCause.Bullet, by)) broke++; continue; }
                int n = dmg[key] / per;
                Vec3d mid = at[key] * (1.0 / n);
                double spread = 0, best = double.MaxValue;
                int start = -1;
                foreach (ShotPoint pt in points)
                {
                    if (pt.Key != key) continue;
                    double d = (pt.P - mid).Length;
                    spread = Math.Max(spread, d);
                    if (d < best - 1e-12 || (Math.Abs(d - best) <= 1e-12 && pt.Tile < start)) { best = d; start = pt.Tile; }
                }
                double r = Config.HitRadiusOf(weapon);
                if (n > 1) r = Math.Max(r, Math.Min(Config.PumpMaxRadius, spread + 0.15));
                if (HitArea(start, mid, dmg[key], r, TileCause.Bullet, by)) broke++;
            }
            return broke;
        }

        private struct ShotPoint { public int Key; public Vec3d P; public int Tile; }
        [ThreadStatic] private static List<ShotPoint> _shotPoints;

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
        [ThreadStatic] private static List<int> _shotTiles;
        [ThreadStatic] private static Dictionary<int, int> _shotDmg;
        [ThreadStatic] private static Dictionary<int, Vec3d> _shotAt;

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
