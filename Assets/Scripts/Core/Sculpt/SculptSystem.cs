using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Authoritative sculpt simulation: validation, windups and tile locks, the sculpt bar,
    /// reinforcement, player shove/carry and block gravity. Fixed 60 Hz ticks, integer grid state.
    /// Usage per tick: TryBegin() for each confirmed command, then Step().
    /// </summary>
    public sealed class SculptSystem
    {
        public readonly VoxelGrid Grid;
        public readonly SculptConfig Config;

        /// <summary>Current tick. Step() advances it.</summary>
        public int Tick { get; private set; }

        /// <summary>Events produced since the last ClearEvents(). Game uses these for audio, effects and warnings.</summary>
        public readonly List<SculptEvent> Events = new List<SculptEvent>();

        private readonly List<PlayerBody> _players = new List<PlayerBody>();
        private readonly Dictionary<int, SculptBar> _bars = new Dictionary<int, SculptBar>();
        private readonly Dictionary<int, PendingSculpt> _pending = new Dictionary<int, PendingSculpt>();
        private readonly Dictionary<Int3, int> _locks = new Dictionary<Int3, int>();
        private readonly HashSet<long> _gravityColumns = new HashSet<long>();
        private readonly Dictionary<Int3, int> _fallTimers = new Dictionary<Int3, int>();

        // Scratch lists, reused to avoid per-tick allocation.
        private readonly List<PendingSculpt> _due = new List<PendingSculpt>();
        private readonly List<long> _columnScratch = new List<long>();
        private readonly List<KeyValuePair<PlayerBody, Vec3d>> _shoves = new List<KeyValuePair<PlayerBody, Vec3d>>();
        private int _resolvingPlayer;

        public SculptSystem(VoxelGrid grid, SculptConfig config, int startTick = 0)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Tick = startTick;
        }

        // ---------------- Players ----------------

        public IReadOnlyList<PlayerBody> Players => _players;

        public void AddPlayer(PlayerBody p)
        {
            if (_bars.ContainsKey(p.Id)) throw new ArgumentException("Duplicate player id " + p.Id);
            _players.Add(p);
            _bars[p.Id] = new SculptBar();
        }

        public void RemovePlayer(int id)
        {
            if (_pending.TryGetValue(id, out var pend)) ReleaseLocks(pend);
            _pending.Remove(id);
            _bars.Remove(id);
            _players.RemoveAll(p => p.Id == id);
        }

        public SculptBar BarOf(int playerId) => _bars[playerId];

        public PendingSculpt PendingOf(int playerId) => _pending.TryGetValue(playerId, out var p) ? p : null;

        public bool IsLocked(Int3 cell, out int ownerId) => _locks.TryGetValue(cell, out ownerId);

        public void ClearEvents() => Events.Clear();

        // ---------------- Validation ----------------

        /// <summary>Full check of a command as it stands right now (drives the ghost colour). Changes nothing.</summary>
        public SculptCheck Evaluate(PlayerBody p, SculptAction a) => EvaluateInternal(p, a, false);

        /// <summary>Validates and, if valid, charges the bar, locks the cells and starts the windup.</summary>
        public SculptCheck TryBegin(PlayerBody p, SculptAction a)
        {
            SculptCheck c = EvaluateInternal(p, a, false);
            if (!c.IsValid) return c;

            _bars[p.Id].Spend(c.Cost, Tick);
            var cells = c.Source == c.Destination ? new[] { c.Source } : new[] { c.Source, c.Destination };
            var pend = new PendingSculpt
            {
                PlayerId = p.Id,
                Action = a,
                Check = c,
                StartTick = Tick,
                CompleteTick = Tick + c.WindupTicks,
                LockedCells = cells,
            };
            foreach (var cell in cells) _locks[cell] = p.Id;
            _pending[p.Id] = pend;

            Events.Add(new SculptEvent
            {
                Type = SculptEventType.Began,
                Tick = Tick,
                PlayerId = p.Id,
                Action = a,
                Check = c,
                Cell = c.Source,
                WarnedPlayers = PlayersTouching(c.Source, p.Id),
            });
            return c;
        }

        private bool IsSculptable(Int3 cell)
        {
            if (!Grid.InBounds(cell)) return false;
            if (cell.Y < Config.MinCellY || cell.Y > Config.MaxCellY) return false;
            return Grid.Get(cell).Kind == BlockKind.Solid;
        }

        private SculptCheck EvaluateInternal(PlayerBody p, SculptAction a, bool resolving)
        {
            var c = new SculptCheck { Type = a.Type, Source = a.Target, Destination = a.Target };

            if (!resolving && _pending.ContainsKey(p.Id)) return Fail(c, SculptResult.Busy);
            if (resolving && _pending.TryGetValue(p.Id, out var own) && own.Disturbed) return Fail(c, SculptResult.Disturbed);
            if (!Grid.InBounds(a.Target) || Grid.Get(a.Target).IsEmpty) return Fail(c, SculptResult.NoTarget);

            switch (a.Type)
            {
                case SculptActionType.Move:
                {
                    if (!a.Direction.IsHorizontal()) return Fail(c, SculptResult.BadDirection);
                    if (!IsSculptable(a.Target)) return Fail(c, SculptResult.NotSculptable);
                    Int3 dst = a.Target + a.Direction.ToInt3();
                    c.Destination = dst;
                    if (!Grid.InBounds(dst) || !Grid.Get(dst).IsEmpty) return Fail(c, SculptResult.DestinationBlocked);
                    break;
                }
                case SculptActionType.Raise:
                {
                    Int3 top = Grid.TopOfStack(a.Target);
                    c.Source = top;
                    if (Grid.Get(top).Kind == BlockKind.Anchor && top.Y >= Config.MinCellY) return Fail(c, SculptResult.NotSculptable);
                    Int3 dst = top + Int3.Up;
                    c.Destination = dst;
                    if (dst.Y > Config.MaxCellY || dst.Y < Config.MinCellY || !Grid.InBounds(dst)) return Fail(c, SculptResult.OutOfHeightRange);
                    break;
                }
                case SculptActionType.Sink:
                {
                    Int3 top = Grid.TopOfStack(a.Target);
                    c.Source = top;
                    c.Destination = top;
                    if (top.Y < Config.MinCellY) return Fail(c, SculptResult.OutOfHeightRange);
                    if (!IsSculptable(top)) return Fail(c, SculptResult.NotSculptable);
                    break;
                }
                case SculptActionType.Shape:
                {
                    if (!a.Direction.IsHorizontal()) return Fail(c, SculptResult.BadDirection);
                    if (!IsSculptable(a.Target)) return Fail(c, SculptResult.NotSculptable);
                    if (!Grid.Get(a.Target + Int3.Up).IsEmpty) return Fail(c, SculptResult.NotColumnTop);
                    break;
                }
                default:
                    return Fail(c, SculptResult.NoTarget);
            }

            // Locks: another player's windup holds the cell.
            if (LockedByOther(c.Source, p.Id) || LockedByOther(c.Destination, p.Id)) return Fail(c, SculptResult.Locked);

            if (!resolving)
            {
                // Range and line of sight: from the server's view of the eye.
                Vec3d eye = p.Eye;
                Aabb targetBox = VoxelGrid.CellBounds(a.Target);
                if ((targetBox.ClosestPoint(eye) - eye).Length > Config.RangeMeters + 1e-9) return Fail(c, SculptResult.OutOfRange);
                if (!targetBox.Contains(a.AimPoint, Config.AimTolerance)) return Fail(c, SculptResult.NoLineOfSight);
                Vec3d toAim = a.AimPoint - eye;
                VoxelHit hit = VoxelGeometry.Raycast(Grid, eye, toAim, toAim.Length + 0.05);
                if (!hit.Hit || hit.Cell != a.Target) return Fail(c, SculptResult.NoLineOfSight);
            }

            // Cost and windup.
            Block affected = Grid.Get(c.Source);
            c.EnemyReinforced = affected.IsEnemyReinforced(p.Team, Tick);
            int baseCost = Config.CostFor(a.Type);
            c.Cost = baseCost * (c.EnemyReinforced ? Config.EnemyReinforcedCostMultiplier : 1) + (a.Reinforce ? Config.CostReinforce : 0);
            c.WindupTicks = a.Reinforce ? Config.ReinforcedCasterWindupTicks : Config.WindupTicks;
            if (c.EnemyReinforced) c.WindupTicks = Math.Max(c.WindupTicks, Config.EnemyReinforcedWindupTicks);

            if (!resolving && !_bars[p.Id].CanAfford(c.Cost)) return Fail(c, SculptResult.NotEnoughBar);

            if (!PlanShoves(a, c)) return Fail(c, SculptResult.PlayerBlocked);

            c.Result = SculptResult.Valid;
            return c;
        }

        private static SculptCheck Fail(SculptCheck c, SculptResult r)
        {
            c.Result = r;
            return c;
        }

        private bool LockedByOther(Int3 cell, int playerId) => _locks.TryGetValue(cell, out int owner) && owner != playerId;

        // ---------------- Shove / carry ----------------

        /// <summary>The block that will occupy a cell after the action, and any cells it reshapes.</summary>
        private VoxelGeometry.Override[] ResultOverrides(SculptAction a, SculptCheck c, out Int3 grownCell, out Block grownBlock, out Int3 extraCell, out Block extraBlock, out bool hasExtra)
        {
            hasExtra = false;
            extraCell = default;
            extraBlock = default;
            switch (a.Type)
            {
                case SculptActionType.Move:
                {
                    grownCell = c.Destination;
                    grownBlock = Grid.Get(c.Source);
                    if (!Grid.Get(c.Destination + Int3.Up).IsEmpty) grownBlock.MakeFlat();
                    Int3 below = c.Destination + Int3.Down;
                    Block belowB = Grid.Get(below);
                    if (belowB.IsRamp)
                    {
                        belowB.MakeFlat();
                        hasExtra = true;
                        extraCell = below;
                        extraBlock = belowB;
                        return new[] { new VoxelGeometry.Override(c.Source, Block.Empty), new VoxelGeometry.Override(grownCell, grownBlock), new VoxelGeometry.Override(below, belowB) };
                    }
                    return new[] { new VoxelGeometry.Override(c.Source, Block.Empty), new VoxelGeometry.Override(grownCell, grownBlock) };
                }
                case SculptActionType.Raise:
                {
                    Block top = Grid.Get(c.Source);
                    grownCell = c.Destination;
                    grownBlock = Block.Solid;
                    if (top.IsRamp)
                    {
                        grownBlock = Block.Ramp(top.RampLow);
                        top.MakeFlat();
                        hasExtra = true;
                        extraCell = c.Source;
                        extraBlock = top;
                        return new[] { new VoxelGeometry.Override(grownCell, grownBlock), new VoxelGeometry.Override(c.Source, top) };
                    }
                    return new[] { new VoxelGeometry.Override(grownCell, grownBlock) };
                }
                case SculptActionType.Shape:
                {
                    grownCell = c.Source;
                    grownBlock = Grid.Get(c.Source);
                    if (grownBlock.IsRamp && grownBlock.RampLow == a.Direction) grownBlock.MakeFlat();
                    else
                    {
                        grownBlock.IsRamp = true;
                        grownBlock.RampLow = a.Direction;
                    }
                    return new[] { new VoxelGeometry.Override(grownCell, grownBlock) };
                }
                default: // Sink only removes volume.
                    grownCell = c.Source;
                    grownBlock = Block.Empty;
                    return new[] { new VoxelGeometry.Override(c.Source, Block.Empty) };
            }
        }

        /// <summary>
        /// Works out where every affected player ends up. Fills _shoves. Returns false if anyone
        /// would end up inside geometry (the action is then invalid).
        /// </summary>
        private bool PlanShoves(SculptAction a, SculptCheck c)
        {
            _shoves.Clear();
            if (a.Type == SculptActionType.Sink) return true;

            var overrides = ResultOverrides(a, c, out Int3 grownCell, out Block grownBlock, out Int3 extraCell, out Block extraBlock, out bool hasExtra);
            Aabb cellBox = VoxelGrid.CellBounds(grownCell);

            foreach (var p in _players)
            {
                Aabb box = p.Bounds;
                bool hitsGrown = VoxelGeometry.BoxOverlapsBlock(grownBlock, grownCell, box);
                bool hitsExtra = hasExtra && VoxelGeometry.BoxOverlapsBlock(extraBlock, extraCell, box);
                if (!hitsGrown && !hitsExtra) continue;

                Vec3d pos = p.Position;
                if (a.Type == SculptActionType.Move && hitsGrown)
                {
                    // Minimal horizontal shove that clears the destination cell, never more than one cell.
                    switch (a.Direction)
                    {
                        case Dir.East: pos = new Vec3d(pos.X + (cellBox.Max.X - box.Min.X), pos.Y, pos.Z); break;
                        case Dir.West: pos = new Vec3d(pos.X + (cellBox.Min.X - box.Max.X), pos.Y, pos.Z); break;
                        case Dir.North: pos = new Vec3d(pos.X, pos.Y, pos.Z + (cellBox.Max.Z - box.Min.Z)); break;
                        case Dir.South: pos = new Vec3d(pos.X, pos.Y, pos.Z + (cellBox.Min.Z - box.Max.Z)); break;
                    }
                }
                else
                {
                    // Lift: raise the feet onto the new surface under the box.
                    double top = box.Min.Y;
                    if (hitsGrown) top = Math.Max(top, SurfaceUnderBox(grownBlock, grownCell, box));
                    if (hitsExtra) top = Math.Max(top, SurfaceUnderBox(extraBlock, extraCell, box));
                    pos = new Vec3d(pos.X, top, pos.Z);
                }

                if (VoxelGeometry.BoxOverlapsSolid(Grid, p.BoundsAt(pos), overrides)) return false;
                _shoves.Add(new KeyValuePair<PlayerBody, Vec3d>(p, pos));
            }
            return true;
        }

        /// <summary>Highest point of a block's surface under the footprint of a box.</summary>
        private static double SurfaceUnderBox(Block b, Int3 cell, Aabb box)
        {
            Aabb c = VoxelGrid.CellBounds(cell);
            if (!b.IsRamp) return c.Max.Y;
            double hx = 0, hz = 0;
            // Sample at the box corner nearest the ramp's high side.
            switch (b.RampLow)
            {
                case Dir.East: hx = box.Min.X; hz = c.Center.Z; break;
                case Dir.West: hx = box.Max.X; hz = c.Center.Z; break;
                case Dir.North: hx = c.Center.X; hz = box.Min.Z; break;
                case Dir.South: hx = c.Center.X; hz = box.Max.Z; break;
            }
            return VoxelGeometry.SurfaceHeight(b, cell, hx, hz);
        }

        private int[] PlayersTouching(Int3 cell, int exceptId)
        {
            Aabb zone = VoxelGrid.CellBounds(cell).Expand(Config.TouchMarginMeters);
            var ids = new List<int>();
            foreach (var p in _players)
                if (p.Id != exceptId && p.Bounds.Overlaps(zone)) ids.Add(p.Id);
            ids.Sort();
            return ids.ToArray();
        }

        // ---------------- Tick ----------------

        /// <summary>Advances one tick: resolves due windups, runs block gravity, regenerates bars.</summary>
        public void Step()
        {
            Tick++;

            _due.Clear();
            foreach (var pend in _pending.Values)
                if (pend.CompleteTick <= Tick) _due.Add(pend);
            _due.Sort((x, y) => x.CompleteTick != y.CompleteTick ? x.CompleteTick.CompareTo(y.CompleteTick) : x.PlayerId.CompareTo(y.PlayerId));
            foreach (var pend in _due) Resolve(pend);

            StepGravity();

            foreach (var p in _players) _bars[p.Id].Tick(Tick, Config);
        }

        private PlayerBody FindPlayer(int id)
        {
            foreach (var p in _players) if (p.Id == id) return p;
            return null;
        }

        private void Resolve(PendingSculpt pend)
        {
            PlayerBody p = FindPlayer(pend.PlayerId);
            SculptCheck c = p != null ? EvaluateInternal(p, pend.Action, true) : Fail(pend.Check, SculptResult.NoTarget);
            ReleaseLocks(pend);
            _pending.Remove(pend.PlayerId);

            if (!c.IsValid)
            {
                if (p != null) _bars[p.Id].Refund(pend.Check.Cost);
                Events.Add(new SculptEvent { Type = SculptEventType.Cancelled, Tick = Tick, PlayerId = pend.PlayerId, Action = pend.Action, Check = c, Result = c.Result, Cell = c.Source });
                return;
            }

            // _shoves was filled by EvaluateInternal for this exact state.
            var shoves = new List<KeyValuePair<PlayerBody, Vec3d>>(_shoves);
            _resolvingPlayer = p.Id;
            Apply(p, pend.Action, c);
            _resolvingPlayer = 0;

            foreach (var s in shoves)
            {
                s.Key.Position = s.Value;
                Events.Add(new SculptEvent { Type = SculptEventType.PlayerShoved, Tick = Tick, PlayerId = s.Key.Id, Action = pend.Action, Check = c, Cell = c.Destination });
            }
            Events.Add(new SculptEvent { Type = SculptEventType.Completed, Tick = Tick, PlayerId = p.Id, Action = pend.Action, Check = c, Cell = c.Destination });
        }

        private void Stamp(ref Block b, PlayerBody p, SculptAction a, bool enemyChange)
        {
            if (enemyChange) b.ClearReinforcement();
            if (a.Reinforce)
            {
                b.ReinforceTeam = (byte)p.Team;
                b.ReinforceUntilTick = Tick + Config.ReinforceDurationTicks;
            }
            b.TintTeam = (byte)p.Team;
            b.TintTick = Tick;
        }

        private void Apply(PlayerBody p, SculptAction a, SculptCheck c)
        {
            switch (a.Type)
            {
                case SculptActionType.Move:
                {
                    Block b = Grid.Get(c.Source);
                    Stamp(ref b, p, a, c.EnemyReinforced);
                    SetCell(c.Source, Block.Empty);
                    SetCell(c.Destination, b);
                    NormalizeRamps(c.Destination);
                    break;
                }
                case SculptActionType.Raise:
                {
                    Block top = Grid.Get(c.Source);
                    Block nb = Block.Solid;
                    if (top.IsRamp)
                    {
                        nb = Block.Ramp(top.RampLow);
                        top.MakeFlat();
                    }
                    if (c.EnemyReinforced) top.ClearReinforcement();
                    SetCell(c.Source, top);
                    Stamp(ref nb, p, a, false);
                    SetCell(c.Destination, nb);
                    break;
                }
                case SculptActionType.Sink:
                {
                    SetCell(c.Source, Block.Empty);
                    Int3 below = c.Source + Int3.Down;
                    if (a.Reinforce && IsSculptable(below))
                    {
                        Block bb = Grid.Get(below);
                        Stamp(ref bb, p, a, bb.IsEnemyReinforced(p.Team, Tick));
                        SetCell(below, bb);
                    }
                    break;
                }
                case SculptActionType.Shape:
                {
                    Block b = Grid.Get(c.Source);
                    if (b.IsRamp && b.RampLow == a.Direction) b.MakeFlat();
                    else
                    {
                        b.IsRamp = true;
                        b.RampLow = a.Direction;
                    }
                    Stamp(ref b, p, a, c.EnemyReinforced);
                    SetCell(c.Source, b);
                    break;
                }
            }
        }

        /// <summary>A ramp only exists as a column's top block: anything sitting on a ramp flattens it.</summary>
        private void NormalizeRamps(Int3 cell)
        {
            Int3 below = cell + Int3.Down;
            Block bb = Grid.Get(below);
            if (Grid.InBounds(below) && bb.IsRamp && !Grid.Get(cell).IsEmpty)
            {
                bb.MakeFlat();
                SetCell(below, bb);
            }
            Block b = Grid.Get(cell);
            if (b.IsRamp && !Grid.Get(cell + Int3.Up).IsEmpty)
            {
                b.MakeFlat();
                SetCell(cell, b);
            }
        }

        /// <summary>All sculpt-driven grid writes go through here so locks can detect disturbance.</summary>
        private void SetCell(Int3 cell, Block b)
        {
            if (_locks.TryGetValue(cell, out int owner) && owner != _resolvingPlayer && _pending.TryGetValue(owner, out var pend))
                pend.Disturbed = true;
            Grid.Set(cell, b);
            _gravityColumns.Add(ColumnKey(cell.X, cell.Z));
        }

        private void ReleaseLocks(PendingSculpt pend)
        {
            foreach (var cell in pend.LockedCells)
                if (_locks.TryGetValue(cell, out int owner) && owner == pend.PlayerId) _locks.Remove(cell);
        }

        // ---------------- Gravity ----------------

        /// <summary>Call after editing the grid directly (map setup, net deltas) so gravity re-checks those columns.</summary>
        public void NotifyCellChanged(Int3 cell) => _gravityColumns.Add(ColumnKey(cell.X, cell.Z));

        private static long ColumnKey(int x, int z) => ((long)x << 32) | (uint)z;
        private static int ColumnX(long k) => (int)(k >> 32);
        private static int ColumnZ(long k) => (int)(uint)k;

        public bool HasFallingBlocks => _gravityColumns.Count > 0;

        private void StepGravity()
        {
            if (_gravityColumns.Count == 0) return;
            _columnScratch.Clear();
            _columnScratch.AddRange(_gravityColumns);
            _columnScratch.Sort();
            foreach (long key in _columnScratch)
            {
                if (!StepColumn(ColumnX(key), ColumnZ(key)))
                {
                    _gravityColumns.Remove(key);
                    ClearColumnTimers(ColumnX(key), ColumnZ(key));
                }
            }
        }

        /// <summary>Steps one column. Returns true while anything in it is still falling.</summary>
        private bool StepColumn(int x, int z)
        {
            bool falling = false;
            int y = Grid.MinY;
            int maxY = Grid.MaxYExclusive;
            while (y < maxY)
            {
                if (Grid.Get(x, y, z).IsEmpty) { y++; continue; }
                int bottom = y;
                bool hasAnchor = false;
                while (y < maxY && !Grid.Get(x, y, z).IsEmpty)
                {
                    if (Grid.Get(x, y, z).Kind == BlockKind.Anchor) hasAnchor = true;
                    y++;
                }
                int top = y - 1;
                var bottomCell = new Int3(x, bottom, z);

                if (bottom == Grid.MinY || hasAnchor)
                {
                    _fallTimers.Remove(bottomCell);
                    continue;
                }

                falling = true;
                if (!_fallTimers.TryGetValue(bottomCell, out int t))
                {
                    _fallTimers[bottomCell] = 0;
                    continue;
                }
                if (t < Config.BlockFallIntervalTicks) t++;
                if (t < Config.BlockFallIntervalTicks)
                {
                    _fallTimers[bottomCell] = t;
                    continue;
                }

                Int3 landing = new Int3(x, bottom - 1, z);
                if (AnyPlayerOverlaps(Grid.Get(bottomCell), landing))
                {
                    // Never crush: wait above the player until the cell is clear.
                    _fallTimers[bottomCell] = t;
                    continue;
                }

                for (int yy = bottom; yy <= top; yy++) SetCell(new Int3(x, yy - 1, z), Grid.Get(x, yy, z));
                SetCell(new Int3(x, top, z), Block.Empty);
                _fallTimers.Remove(bottomCell);
                _fallTimers[landing] = 0;
                NormalizeRamps(landing);
                Events.Add(new SculptEvent { Type = SculptEventType.BlockFell, Tick = Tick, Cell = landing });
            }
            return falling;
        }

        private bool AnyPlayerOverlaps(Block b, Int3 cell)
        {
            foreach (var p in _players)
                if (VoxelGeometry.BoxOverlapsBlock(b, cell, p.Bounds)) return true;
            return false;
        }

        private void ClearColumnTimers(int x, int z)
        {
            if (_fallTimers.Count == 0) return;
            var remove = new List<Int3>();
            foreach (var k in _fallTimers.Keys) if (k.X == x && k.Z == z) remove.Add(k);
            foreach (var k in remove) _fallTimers.Remove(k);
        }
    }
}
