using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Authoritative terrain v2 sculpt simulation: aim-time pieces, validation, windups and sub-cell locks, the bar,
    /// size scaling, reinforcement, shove/carry/friction drag, the one-third support rule with rigid falling, and the
    /// throw. Fixed 60 Hz ticks, integer grid state; every grid change goes through <see cref="PieceTable"/> ops.
    /// Usage per tick: TryBegin() for each confirmed command, then Step().
    /// </summary>
    public sealed partial class SculptSystem
    {
        public readonly SubGrid Grid;
        public readonly SculptConfig Config;
        public readonly PieceTable Pieces;

        /// <summary>Current tick. Step() advances it.</summary>
        public int Tick { get; private set; }

        /// <summary>Events produced since the last ClearEvents(). Game uses these for audio, effects, warnings and damage.</summary>
        public readonly List<SculptEvent> Events = new List<SculptEvent>();

        private readonly List<PlayerBody> _players = new List<PlayerBody>();
        private readonly Dictionary<int, SculptBar> _bars = new Dictionary<int, SculptBar>();
        private readonly Dictionary<int, PendingSculpt> _pending = new Dictionary<int, PendingSculpt>();
        private readonly Dictionary<Int3, int> _locks = new Dictionary<Int3, int>();
        private readonly Dictionary<int, BufferedConfirm> _buffered = new Dictionary<int, BufferedConfirm>();

        // Support and falling.
        private readonly HashSet<ushort> _checkPieces = new HashSet<ushort>();
        private readonly HashSet<Int3> _checkNatural = new HashSet<Int3>();
        private readonly Dictionary<ushort, int> _falling = new Dictionary<ushort, int>();

        // Scratch.
        private readonly List<PendingSculpt> _due = new List<PendingSculpt>();
        private readonly List<Int3> _cellScratch = new List<Int3>();
        private readonly HashSet<Int3> _setScratch = new HashSet<Int3>();
        private readonly TerrainOverlay _overlay;
        private readonly List<KeyValuePair<PlayerBody, Vec3d>> _shoves = new List<KeyValuePair<PlayerBody, Vec3d>>();
        private readonly List<PlayerBody> _drags = new List<PlayerBody>();
        private int _resolvingPlayer;

        private struct BufferedConfirm
        {
            public SculptAction Action;
            public int ExpireTick;
        }

        public SculptSystem(SubGrid grid, SculptConfig config, int startTick = 0)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Tick = startTick;
            Pieces = new PieceTable(grid) { OnWrite = OnCellWritten };
            _overlay = new TerrainOverlay(grid);
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
            _buffered.Remove(id);
            _bars.Remove(id);
            _players.RemoveAll(p => p.Id == id);
        }

        public SculptBar BarOf(int playerId) => _bars[playerId];

        public PendingSculpt PendingOf(int playerId) => _pending.TryGetValue(playerId, out var p) ? p : null;

        public bool IsLocked(Int3 sub, out int ownerId) => _locks.TryGetValue(sub, out ownerId);

        public bool HasBufferedConfirm(int playerId) => _buffered.ContainsKey(playerId);

        public void ClearEvents() => Events.Clear();

        private PlayerBody FindPlayer(int id)
        {
            foreach (var p in _players) if (p.Id == id) return p;
            return null;
        }

        // ---------------- Validation ----------------

        /// <summary>Full check of a command as it stands right now (drives the ghost). Changes nothing.</summary>
        public SculptCheck Evaluate(PlayerBody p, SculptAction a) => EvaluateInternal(p, a, false);

        /// <summary>
        /// Validates and, if valid, carves the piece (natural rock), charges the bar, locks the sub-cells and starts the
        /// windup. A Locked or Busy rejection is free and the confirm is buffered for ConfirmBufferTicks.
        /// </summary>
        public SculptCheck TryBegin(PlayerBody p, SculptAction a) => TryBeginInternal(p, a, true);

        private SculptCheck TryBeginInternal(PlayerBody p, SculptAction a, bool allowBuffer)
        {
            SculptCheck c = EvaluateInternal(p, a, false);
            if (!c.IsValid)
            {
                if (allowBuffer && (c.Result == SculptResult.Locked || c.Result == SculptResult.Busy) && Config.ConfirmBufferTicks > 0)
                    _buffered[p.Id] = new BufferedConfirm { Action = a, ExpireTick = Tick + Config.ConfirmBufferTicks };
                return c;
            }
            _buffered.Remove(p.Id);

            if (c.PieceId == 0)
            {
                PieceInfo carved = Pieces.Carve(c.Cells);
                c.PieceId = carved.Id;
            }

            _bars[p.Id].Spend(c.Cost, Tick);
            // Lock the piece and everything it will sweep through.
            var lockSet = new HashSet<Int3>(c.Cells);
            int lsteps = Math.Abs(c.Delta.X) + Math.Abs(c.Delta.Y) + Math.Abs(c.Delta.Z);
            Int3 lunit = lsteps == 0 ? Int3.Zero : new Int3(Math.Sign(c.Delta.X), Math.Sign(c.Delta.Y), Math.Sign(c.Delta.Z));
            foreach (var x in c.Cells)
                for (int k = 1; k <= lsteps; k++) lockSet.Add(x + lunit * k);
            var cells = new List<Int3>(lockSet);
            cells.Sort(Int3.CompareYZX);
            var pend = new PendingSculpt
            {
                PlayerId = p.Id,
                Action = a,
                Check = c,
                StartTick = Tick,
                CompleteTick = Tick + c.WindupTicks,
                LockedCells = cells.ToArray(),
            };
            foreach (var cell in pend.LockedCells) _locks[cell] = p.Id;
            _pending[p.Id] = pend;

            Events.Add(new SculptEvent
            {
                Type = SculptEventType.Began,
                Tick = Tick,
                PlayerId = p.Id,
                Action = a,
                Check = c,
                Piece = c.PieceId,
                WarnedPlayers = Warned(p, a, c),
            });
            return c;
        }

        private static SculptCheck Fail(SculptCheck c, SculptResult r)
        {
            c.Result = r;
            return c;
        }

        private bool LockedByOther(Int3 cell, int playerId) => _locks.TryGetValue(cell, out int owner) && owner != playerId;

        private bool HasProjectile(int playerId)
        {
            foreach (var t in _thrown) if (t.Owner == playerId) return true;
            return false;
        }

        private SculptCheck EvaluateInternal(PlayerBody p, SculptAction a, bool resolving)
        {
            var c = new SculptCheck { Type = a.Type };

            if (!resolving && (_pending.ContainsKey(p.Id) || (a.Type == SculptActionType.Throw && HasProjectile(p.Id)))) return Fail(c, SculptResult.Busy);
            if (resolving && _pending.TryGetValue(p.Id, out var own) && own.Disturbed) return Fail(c, SculptResult.Disturbed);

            if (!Grid.InBounds(a.Target)) return Fail(c, SculptResult.NoTarget);
            ushort id = Grid.Get(a.Target);
            if (id == SubId.Empty) return Fail(c, SculptResult.NoTarget);
            if (!SubId.IsSculptable(id)) return Fail(c, SculptResult.NotSculptable);
            if (a.Target.Y < Config.MinSubY || a.Target.Y > Config.MaxSubY) return Fail(c, SculptResult.NotSculptable);

            // The piece: an existing one, or the natural rock this aim would carve.
            PieceInfo info = null;
            if (SubId.IsPiece(id))
            {
                info = Pieces.Get(id);
                if (info == null) return Fail(c, SculptResult.NoTarget);
                c.PieceId = id;
                c.Cells = info.Cells.ToArray();
            }
            else
            {
                PieceFinder.FindNatural(Grid, a.Target, Config, _cellScratch);
                if (_cellScratch.Count == 0) return Fail(c, SculptResult.NoTarget);
                c.Cells = _cellScratch.ToArray();
            }

            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var x in c.Cells) { minY = Math.Min(minY, x.Y); maxY = Math.Max(maxY, x.Y); }
            if (minY < Config.MinSubY || maxY > Config.MaxSubY) return Fail(c, SculptResult.NotSculptable);

            switch (a.Type)
            {
                case SculptActionType.Move:
                    if (!a.Direction.IsHorizontal()) return Fail(c, SculptResult.BadDirection);
                    c.Delta = a.Direction.ToInt3() * SubGrid.TileSubs;
                    break;
                case SculptActionType.Raise:
                    c.Delta = new Int3(0, SubGrid.TileSubs, 0);
                    if (maxY + SubGrid.TileSubs > Config.MaxSubY) return Fail(c, SculptResult.OutOfHeightRange);
                    break;
                case SculptActionType.Sink:
                    c.Delta = new Int3(0, -SubGrid.TileSubs, 0);
                    if (minY - SubGrid.TileSubs < Config.MinSubY) return Fail(c, SculptResult.OutOfHeightRange);
                    break;
                case SculptActionType.Throw:
                    c.Delta = Int3.Zero;
                    break;
                default:
                    return Fail(c, SculptResult.NoTarget);
            }

            // Where the piece goes must be empty (or itself; a sink also pushes into natural rock).
            _setScratch.Clear();
            foreach (var x in c.Cells) _setScratch.Add(x);
            // Everything it sweeps through must be clear (no tunnelling through thin walls).
            Int3 unit = c.Delta == Int3.Zero ? Int3.Zero : new Int3(Math.Sign(c.Delta.X), Math.Sign(c.Delta.Y), Math.Sign(c.Delta.Z));
            int steps = Math.Abs(c.Delta.X) + Math.Abs(c.Delta.Y) + Math.Abs(c.Delta.Z);
            foreach (var x in c.Cells)
                for (int k = 1; k <= steps; k++)
                {
                    Int3 dst = x + unit * k;
                    if (_setScratch.Contains(dst)) continue;
                    if (!Grid.InBounds(dst)) return Fail(c, SculptResult.DestinationBlocked);
                    ushort d = Grid.Get(dst);
                    if (d == SubId.Empty) continue;
                    if (a.Type == SculptActionType.Sink && d == SubId.Natural) continue;
                    return Fail(c, SculptResult.DestinationBlocked);
                }

            // Locks: another player's windup holds any of the sub-cells it touches.
            foreach (var x in c.Cells)
                for (int k = 0; k <= steps; k++)
                    if (LockedByOther(x + unit * k, p.Id)) return Fail(c, SculptResult.Locked);

            if (!resolving)
            {
                // Range and line of sight from the server's view of the eye.
                Vec3d eye = p.Eye;
                Aabb targetBox = SubGrid.SubBounds(a.Target);
                if ((targetBox.ClosestPoint(eye) - eye).Length > Config.RangeMeters + 1e-9) return Fail(c, SculptResult.OutOfRange);
                if (!targetBox.Contains(a.AimPoint, Config.AimTolerance)) return Fail(c, SculptResult.NoLineOfSight);
                Vec3d toAim = a.AimPoint - eye;
                TerrainHit hit = Grid.Raycast(eye, toAim, toAim.Length + 0.05);
                if (!hit.Hit) return Fail(c, SculptResult.NoLineOfSight);
                bool samePiece = info != null ? hit.Id == id : (hit.Id == SubId.Natural && _setScratch.Contains(hit.Sub));
                if (!samePiece) return Fail(c, SculptResult.NoLineOfSight);
            }

            // Cost and windup, scaled by size.
            int n = c.Cells.Length;
            c.EnemyReinforced = info != null && info.IsEnemyReinforced(p.Team, Tick);
            if (a.Type == SculptActionType.Throw)
            {
                if (info != null && info.IsReinforced(Tick)) return Fail(c, SculptResult.CannotThrowReinforced);
                c.Cost = Config.ThrowCost(n);
                c.WindupTicks = Config.ThrowWindup(n);
                c.LocksGun = true;
            }
            else
            {
                int baseCost = Config.ScaledCost(Config.CostFor(a.Type), n);
                c.Cost = baseCost * (c.EnemyReinforced ? Config.EnemyReinforcedCostMultiplier : 1) + (a.Reinforce ? Config.CostReinforce : 0);
                c.WindupTicks = Config.ScaledWindup(n);
                if (a.Reinforce) c.WindupTicks = Math.Max(c.WindupTicks, Config.ReinforcedCasterWindupTicks);
                if (c.EnemyReinforced) c.WindupTicks = Math.Max(c.WindupTicks, Config.EnemyReinforcedWindupTicks);
                c.LocksGun = info != null && info.Team != 0 && info.Team != p.Team;
            }

            if (!resolving && !_bars[p.Id].CanAfford(c.Cost)) return Fail(c, SculptResult.NotEnoughBar);

            if (a.Type != SculptActionType.Throw && !PlanShoves(a, c)) return Fail(c, SculptResult.PlayerBlocked);

            c.Result = SculptResult.Valid;
            return c;
        }

        // ---------------- Shove, carry and friction drag ----------------

        /// <summary>Loads the overlay with the terrain as it will be after the move/raise/sink.</summary>
        private void LoadResultOverlay(SculptActionType type, SculptCheck c)
        {
            _overlay.Clear();
            ushort id = c.PieceId != 0 ? c.PieceId : ushort.MaxValue;
            PieceTable.ForEachSwept(c.Cells, c.Delta, type == SculptActionType.Raise, _setScratch, (x, v) => _overlay.Set(x, v));
            foreach (var x in c.Cells) _overlay.Set(x + c.Delta, id);
        }

        /// <summary>
        /// Works out where every affected player ends up (fills _shoves and _drags). False if anyone would end up inside
        /// geometry. In the path of the piece: lifted (raise/sink: up to one level; push: only a chamfer's worth) or
        /// shoved at most one tile; standing on a piece that moves sideways: a friction kick only.
        /// </summary>
        private bool PlanShoves(SculptAction a, SculptCheck c)
        {
            _shoves.Clear();
            _drags.Clear();
            LoadResultOverlay(a.Type, c);
            Aabb near = CellsBounds(c.Cells, c.Delta).Expand(0.5);

            foreach (var p in _players)
            {
                Aabb box = p.Bounds;
                if (!box.Overlaps(near)) continue;
                if (!_overlay.BoxOverlapsSolid(box))
                {
                    if (a.Type == SculptActionType.Move && StandsOn(p, c.Cells)) _drags.Add(p);
                    continue;
                }

                Vec3d pos = p.Position;
                if (a.Type == SculptActionType.Move)
                {
                    Vec3d dir = a.Direction.ToVec();
                    if (TryLift(p, Config.SmallLift, out pos) || TryShove(p, dir, out pos)) { _shoves.Add(new KeyValuePair<PlayerBody, Vec3d>(p, pos)); continue; }
                    return false;
                }

                if (TryLift(p, Config.MaxLift, out pos)) { _shoves.Add(new KeyValuePair<PlayerBody, Vec3d>(p, pos)); continue; }
                bool placed = false;
                double best = double.PositiveInfinity;
                Vec3d bestPos = pos;
                foreach (Dir d in DirUtil.Horizontal)
                {
                    if (!TryShove(p, d.ToVec(), out Vec3d sp)) continue;
                    double dist = (sp - p.Position).HorizontalLength;
                    if (dist < best - 1e-9) { best = dist; bestPos = sp; placed = true; }
                }
                if (!placed) return false;
                _shoves.Add(new KeyValuePair<PlayerBody, Vec3d>(p, bestPos));
            }
            return true;
        }

        /// <summary>Lowest lift (up to max) that frees the player's box in the result terrain.</summary>
        private bool TryLift(PlayerBody p, double max, out Vec3d pos)
        {
            return TrySlide(p, new Vec3d(0, 1, 0), max, out pos);
        }

        /// <summary>
        /// Shortest horizontal shove (up to one tile) that frees the player, allowing a small lift at the end so a player
        /// pushed against the piece's 0.15 m base fillet stands on it instead of counting as blocked.
        /// </summary>
        private bool TryShove(PlayerBody p, Vec3d dir, out Vec3d pos)
        {
            const double step = 0.05;
            double max = Config.MaxShove;
            double lo = 0;
            for (double d = step; d <= max + 1e-9; d += step)
            {
                double dd = Math.Min(d, max);
                Vec3d at = p.Position + dir * dd;
                if (!_overlay.BoxOverlapsSolid(p.BoundsAt(at)))
                {
                    double hi = dd;
                    for (int k = 0; k < 20; k++)
                    {
                        double mid = (lo + hi) * 0.5;
                        if (_overlay.BoxOverlapsSolid(p.BoundsAt(p.Position + dir * mid))) lo = mid; else hi = mid;
                    }
                    pos = p.Position + dir * hi;
                    return true;
                }
                if (!_overlay.BoxOverlapsSolid(p.BoundsAt(at + new Vec3d(0, Config.SmallLift, 0))))
                {
                    double l0 = 0, l1 = Config.SmallLift;
                    for (int k = 0; k < 20; k++)
                    {
                        double mid = (l0 + l1) * 0.5;
                        if (_overlay.BoxOverlapsSolid(p.BoundsAt(at + new Vec3d(0, mid, 0)))) l0 = mid; else l1 = mid;
                    }
                    pos = at + new Vec3d(0, l1, 0);
                    return true;
                }
                lo = dd;
            }
            pos = p.Position;
            return false;
        }

        private bool TrySlide(PlayerBody p, Vec3d dir, double max, out Vec3d pos)
        {
            const double step = 0.05;
            double lo = 0;
            for (double d = step; d <= max + 1e-9; d += step)
            {
                double dd = Math.Min(d, max);
                if (_overlay.BoxOverlapsSolid(p.BoundsAt(p.Position + dir * dd))) { lo = dd; continue; }
                double hi = dd;
                for (int k = 0; k < 20; k++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (_overlay.BoxOverlapsSolid(p.BoundsAt(p.Position + dir * mid))) lo = mid; else hi = mid;
                }
                pos = p.Position + dir * hi;
                return true;
            }
            pos = p.Position;
            return false;
        }

        /// <summary>Is the player's box resting on top of these sub-cells (voxel tops, within RiderTolerance)?</summary>
        private bool StandsOn(PlayerBody p, Int3[] cells)
        {
            Aabb foot = p.Bounds;
            foot = new Aabb(new Vec3d(foot.Min.X, foot.Min.Y - Config.RiderTolerance, foot.Min.Z), new Vec3d(foot.Max.X, foot.Min.Y + 0.01, foot.Max.Z));
            foreach (var x in cells)
                if (SubGrid.SubBounds(x).Overlaps(foot)) return true;
            return false;
        }

        private static Aabb CellsBounds(Int3[] cells, Int3 delta)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c in cells)
            {
                x0 = Math.Min(x0, Math.Min(c.X, c.X + delta.X)); x1 = Math.Max(x1, Math.Max(c.X, c.X + delta.X));
                y0 = Math.Min(y0, Math.Min(c.Y, c.Y + delta.Y)); y1 = Math.Max(y1, Math.Max(c.Y, c.Y + delta.Y));
                z0 = Math.Min(z0, Math.Min(c.Z, c.Z + delta.Z)); z1 = Math.Max(z1, Math.Max(c.Z, c.Z + delta.Z));
            }
            const double s = SubGrid.SubSize;
            return new Aabb(new Vec3d(x0 * s, y0 * s, z0 * s), new Vec3d((x1 + 1) * s, (y1 + 1) * s, (z1 + 1) * s));
        }

        /// <summary>Everyone (but the sculptor) standing on or touching the piece; for a throw, enemies in its lane too.</summary>
        private int[] Warned(PlayerBody sculptor, SculptAction a, SculptCheck c)
        {
            var ids = new List<int>();
            Aabb zone = CellsBounds(c.Cells, Int3.Zero).Expand(Config.TouchMarginMeters);
            foreach (var p in _players)
            {
                if (p.Id == sculptor.Id) continue;
                if (p.Bounds.Overlaps(zone)) { ids.Add(p.Id); continue; }
                if (a.Type == SculptActionType.Throw && p.Team != sculptor.Team && InThrowLane(sculptor, a, c, p)) ids.Add(p.Id);
            }
            ids.Sort();
            return ids.ToArray();
        }

        // ---------------- Tick ----------------

        /// <summary>Advances one tick: resolves due windups, retries buffered confirms, flies thrown pieces, runs support
        /// and falling, regenerates bars.</summary>
        public void Step()
        {
            Tick++;

            _due.Clear();
            foreach (var pend in _pending.Values)
                if (pend.CompleteTick <= Tick) _due.Add(pend);
            _due.Sort((x, y) => x.CompleteTick != y.CompleteTick ? x.CompleteTick.CompareTo(y.CompleteTick) : x.PlayerId.CompareTo(y.PlayerId));
            foreach (var pend in _due) Resolve(pend);

            if (_buffered.Count > 0)
            {
                var ids = new List<int>(_buffered.Keys);
                ids.Sort();
                foreach (int id in ids)
                {
                    BufferedConfirm b = _buffered[id];
                    _buffered.Remove(id);
                    if (Tick > b.ExpireTick) continue;
                    PlayerBody p = FindPlayer(id);
                    if (p == null) continue;
                    SculptCheck c = TryBeginInternal(p, b.Action, false);
                    if (c.Result == SculptResult.Locked || c.Result == SculptResult.Busy) _buffered[id] = b; // keep waiting until it expires
                }
            }

            StepThrown();
            StepSupport();

            foreach (var p in _players) _bars[p.Id].Tick(Tick, Config);
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
                Events.Add(new SculptEvent { Type = SculptEventType.Cancelled, Tick = Tick, PlayerId = pend.PlayerId, Action = pend.Action, Check = c, Result = c.Result, Piece = pend.Check.PieceId });
                return;
            }

            // _shoves and _drags were filled by EvaluateInternal for this exact state.
            var shoves = new List<KeyValuePair<PlayerBody, Vec3d>>(_shoves);
            var drags = new List<PlayerBody>(_drags);
            _resolvingPlayer = p.Id;
            PieceInfo info = Pieces.Get(c.PieceId);

            if (pend.Action.Type == SculptActionType.Throw)
            {
                Launch(p, pend.Action, c, info);
            }
            else
            {
                Pieces.Translate(c.PieceId, c.Delta, pend.Action.Type == SculptActionType.Raise);
                byte rTeam = info.ReinforceTeam;
                int rUntil = info.ReinforceUntilTick;
                if (c.EnemyReinforced) { rTeam = 0; rUntil = 0; }
                if (pend.Action.Reinforce) { rTeam = (byte)p.Team; rUntil = Tick + Config.ReinforceDurationTicks; }
                Pieces.Stamp(c.PieceId, (byte)p.Team, Tick, rTeam, rUntil);
                _checkPieces.Add(c.PieceId);
            }
            _resolvingPlayer = 0;

            foreach (var s in shoves)
            {
                s.Key.Position = s.Value;
                Events.Add(new SculptEvent { Type = SculptEventType.PlayerShoved, Tick = Tick, PlayerId = s.Key.Id, Action = pend.Action, Check = c, Piece = c.PieceId });
            }
            foreach (var d in drags)
            {
                d.Kick += pend.Action.Direction.ToVec() * Config.RiderDragSpeed;
                Events.Add(new SculptEvent { Type = SculptEventType.PlayerDragged, Tick = Tick, PlayerId = d.Id, Action = pend.Action, Check = c, Piece = c.PieceId });
            }
            Events.Add(new SculptEvent { Type = SculptEventType.Completed, Tick = Tick, PlayerId = p.Id, Action = pend.Action, Check = c, Piece = c.PieceId });
        }

        private void ReleaseLocks(PendingSculpt pend)
        {
            foreach (var cell in pend.LockedCells)
                if (_locks.TryGetValue(cell, out int owner) && owner == pend.PlayerId) _locks.Remove(cell);
        }

        /// <summary>Every grid write: lock disturbance and support bookkeeping.</summary>
        private void OnCellWritten(Int3 c)
        {
            if (_locks.TryGetValue(c, out int owner) && owner != _resolvingPlayer && _pending.TryGetValue(owner, out var pend))
                pend.Disturbed = true;
            ushort here = Grid.Get(c);
            if (SubId.IsPiece(here)) _checkPieces.Add(here);
            Int3 above = c + Int3.Up;
            ushort up = Grid.Get(above);
            if (SubId.IsPiece(up)) _checkPieces.Add(up);
            else if (up == SubId.Natural) _checkNatural.Add(above);
        }

        // ---------------- Support and falling ----------------

        /// <summary>Call after editing the grid directly (map setup, tests) so support re-checks that sub-cell's surroundings.</summary>
        public void NotifyCellChanged(Int3 c) => OnCellWritten(c);

        public bool HasFallingPieces => _falling.Count > 0 || _checkPieces.Count > 0 || _checkNatural.Count > 0;

        public bool IsFalling(ushort id) => _falling.ContainsKey(id);

        /// <summary>At least a third of the underside (sub-cells with nothing of the piece below) rests on something solid.</summary>
        public bool IsSupported(IList<Int3> cells)
        {
            _setScratch.Clear();
            foreach (var x in cells) _setScratch.Add(x);
            int under = 0, held = 0;
            foreach (var x in cells)
            {
                Int3 b = x + Int3.Down;
                if (_setScratch.Contains(b)) continue;
                under++;
                if (Grid.Get(b) != SubId.Empty) held++;
            }
            return held * Config.SupportDenominator >= under * Config.SupportNumerator;
        }

        private void StepSupport()
        {
            // Loose natural rock: small unsupported clumps become pieces and fall.
            if (_checkNatural.Count > 0)
            {
                var seeds = new List<Int3>(_checkNatural);
                _checkNatural.Clear();
                seeds.Sort(Int3.CompareYZX);
                var visited = new HashSet<Int3>();
                foreach (var s in seeds)
                {
                    if (visited.Contains(s) || Grid.Get(s) != SubId.Natural) continue;
                    var comp = new List<Int3>();
                    bool grounded = FloodNatural(s, visited, comp);
                    if (grounded || IsSupported(comp)) continue;
                    PieceInfo pc = Pieces.Carve(comp);
                    _falling[pc.Id] = 0;
                }
            }

            if (_checkPieces.Count > 0)
            {
                var ids = new List<ushort>(_checkPieces);
                _checkPieces.Clear();
                ids.Sort();
                foreach (ushort id in ids)
                {
                    PieceInfo pc = Pieces.Get(id);
                    if (pc == null || _falling.ContainsKey(id)) continue;
                    if (!IsSupported(pc.Cells)) _falling[id] = 0;
                }
            }

            if (_falling.Count == 0) return;

            // Bottom-up, then by id.
            var order = new List<ushort>(_falling.Keys);
            order.Sort((a, b) =>
            {
                int ya = Pieces.Get(a)?.MinY ?? int.MaxValue, yb = Pieces.Get(b)?.MinY ?? int.MaxValue;
                return ya != yb ? ya.CompareTo(yb) : a.CompareTo(b);
            });
            foreach (ushort id in order)
            {
                PieceInfo pc = Pieces.Get(id);
                if (pc == null) { _falling.Remove(id); continue; }
                if (IsSupported(pc.Cells)) { Land(id); continue; }

                int t = _falling[id] + 1;
                if (t < Config.FallTicksPerSubCell) { _falling[id] = t; continue; }

                // Rigid drop by one sub-cell, only into empty space, never into a player.
                bool blocked = false;
                _setScratch.Clear();
                foreach (var x in pc.Cells) _setScratch.Add(x);
                foreach (var x in pc.Cells)
                {
                    Int3 b = x + Int3.Down;
                    if (_setScratch.Contains(b)) continue;
                    if (!Grid.InBounds(b) || Grid.Get(b) != SubId.Empty) { blocked = true; break; }
                }
                if (blocked) { Land(id); continue; }

                var tmp = new SculptCheck { PieceId = id, Cells = pc.Cells.ToArray(), Delta = Int3.Down };
                LoadResultOverlay(SculptActionType.Sink, tmp);
                Aabb near = CellsBounds(tmp.Cells, Int3.Down).Expand(0.35); // chamfers and bridges reach 0.15 m past the sub-cells
                bool hitsPlayer = false;
                foreach (var p in _players)
                    if (p.Bounds.Overlaps(near) && _overlay.BoxOverlapsSolid(p.Bounds) && !Grid.BoxOverlapsSolid(p.Bounds)) { hitsPlayer = true; break; }
                if (hitsPlayer) { _falling[id] = t; continue; } // wait above them

                Pieces.Translate(id, Int3.Down, false);
                _falling[id] = 0;
                Events.Add(new SculptEvent { Type = SculptEventType.PieceFell, Tick = Tick, Piece = id });
            }
        }

        private void Land(ushort id)
        {
            _falling.Remove(id);
            Events.Add(new SculptEvent { Type = SculptEventType.PieceLanded, Tick = Tick, Piece = id });
        }

        /// <summary>Flood fill of natural rock. True if it is "the ground": too big, or touching bedrock or an anchor.</summary>
        private bool FloodNatural(Int3 seed, HashSet<Int3> visited, List<Int3> comp)
        {
            var stack = new Stack<Int3>();
            stack.Push(seed);
            visited.Add(seed);
            bool grounded = false;
            while (stack.Count > 0)
            {
                Int3 c = stack.Pop();
                comp.Add(c);
                if (comp.Count > Config.NaturalGroundedLimit) grounded = true;
                for (int k = 0; k < 6; k++)
                {
                    Int3 n = c + Step6(k);
                    ushort id = Grid.Get(n);
                    if (id == SubId.Bedrock || id == SubId.Anchor) { grounded = true; continue; }
                    if (id != SubId.Natural || visited.Contains(n)) continue;
                    if (grounded) continue; // keep the visited set small once we know
                    visited.Add(n);
                    stack.Push(n);
                }
                if (grounded && comp.Count > Config.NaturalGroundedLimit) break;
            }
            return grounded;
        }

        private static Int3 Step6(int k)
        {
            switch (k)
            {
                case 0: return new Int3(1, 0, 0);
                case 1: return new Int3(-1, 0, 0);
                case 2: return new Int3(0, 1, 0);
                case 3: return new Int3(0, -1, 0);
                case 4: return new Int3(0, 0, 1);
                default: return new Int3(0, 0, -1);
            }
        }
    }
}
