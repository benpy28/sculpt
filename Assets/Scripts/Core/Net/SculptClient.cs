using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    public enum ClientSculptEventType : byte
    {
        /// <summary>Your confirm started a predicted windup (instant feedback: crackle, hum).</summary>
        LocalBegan,
        /// <summary>Your predicted action landed in your view (snap, thunk, dust). The server confirms later.</summary>
        LocalLanded,
        /// <summary>Your confirm was refused, locally or by the server, or the server cancelled it. Free; show red.</summary>
        Rejected,
        /// <summary>Another player's windup started (tell: crackle in their team colour, hum, action type).</summary>
        Began,
        /// <summary>A windup ended on the server (Completed). Mine is true for your own (already shown by LocalLanded).</summary>
        Completed,
        /// <summary>Another player's windup was cancelled.</summary>
        Cancelled,
        /// <summary>A falling piece came to rest.</summary>
        PieceLanded,
        ThrowLaunched,
        ThrowImpact,
        /// <summary>PlayerId took Value damage from a thrown piece (Mine: you threw it).</summary>
        ThrowHit,
        /// <summary>You are standing on or touching a piece someone started sculpting, or you are in a throw's lane.</summary>
        Warned,
        /// <summary>You were shoved or lifted by a sculpt.</summary>
        Shoved,
        /// <summary>A piece moved under you (friction kick) or was torn away by a throw.</summary>
        Dragged,
    }

    public struct ClientSculptEvent
    {
        public ClientSculptEventType Type;
        public int PlayerId;
        public int Team;
        public SculptActionType Action;
        public Dir Direction;
        public ushort Piece;
        /// <summary>The piece's sub-cells (where it is now, or where it was thrown from).</summary>
        public Int3[] Cells;
        public Int3 Delta;
        public Vec3d Point;
        public int Value;
        public SculptResult Result;
        public bool Mine;
        /// <summary>The action reinforces the piece, or breaks an enemy's reinforcement (heavier sound and look).</summary>
        public bool Reinforce;
    }

    /// <summary>A windup you started, predicted on this client. Applied to your grid at CompleteTick; the server answers later.</summary>
    public sealed class LocalSculpt
    {
        public byte Seq;
        public SculptAction Action;
        public SculptCheck Check;
        public int BeginTick;
        public int CompleteTick;
        /// <summary>The piece (real id, or a temporary one for natural rock this client carved in prediction); 0 = natural rock.</summary>
        public ushort PieceId;
        public ushort TempId;
        public bool ServerBegan;
        /// <summary>Reached CompleteTick on this client.</summary>
        public bool Completed;
        /// <summary>Currently written into the grid (prediction on top of the server's state).</summary>
        public bool Applied;
        public FlyingPiece Projectile;
    }

    /// <summary>Someone's windup as the server reported it (other players, or yours if your client did not predict it).</summary>
    public sealed class RemoteWindup
    {
        public int PlayerId;
        public int Team;
        public byte Seq;
        public ushort Piece;
        public SculptAction Action;
        public SculptCheck Check;
        public int StartTick;
        public int CompleteTick;
        public bool WarnsMe;
    }

    /// <summary>A thrown piece in flight, flown locally from its launch state (fixed point, same code as the server).</summary>
    public sealed class FlyingPiece
    {
        public ThrownPiece State;
        public int StateTick;
        /// <summary>State one tick earlier, for smooth drawing.</summary>
        public Vec3d PrevPosition;
        public Int3[] Cells;
        public int Owner;
        public int Team;
        /// <summary>Your own throw, predicted before the server confirmed it.</summary>
        public bool Predicted;
        public bool HitTerrain;
        public int HitTick;
    }

    /// <summary>
    /// Client side of networked sculpting, pure Core. Keeps the client's grid equal to the server's (it applies the
    /// server's piece ops in order) plus this client's own predicted actions on top: a confirm shows its windup at once,
    /// and the piece moves in your view on the tick the server will move it, so sculpting never waits for the round trip.
    /// Every prediction is written through a journal; when server ops arrive the predictions are rolled back exactly,
    /// the server's ops applied, and the predictions the server has not answered yet are applied again.
    /// Validation uses a mirror of the server's <see cref="SculptSystem"/> (same code), fed with everyone's windups.
    /// </summary>
    public sealed class SculptClient
    {
        public readonly SubGrid Grid;
        public readonly SculptConfig Config;
        public readonly MovementConfig Movement;
        public readonly SculptSystem Mirror;

        public int LocalId { get; private set; }
        public int LocalTeam { get; private set; }
        public PlayerBody LocalBody { get; private set; }

        public readonly List<ClientSculptEvent> Events = new List<ClientSculptEvent>();
        public IReadOnlyList<LocalSculpt> Local => _local;
        public IReadOnlyDictionary<int, RemoteWindup> Windups => _windups;
        public IReadOnlyList<FlyingPiece> Flying => _flying;

        /// <summary>Bar as the server last reported it, and the tick of that report.</summary>
        public int ServerBar { get; private set; } = SculptConfig.BarMax;
        public int ServerBarTick { get; private set; } = int.MinValue;
        /// <summary>The bar to show: the server's value minus predicted spends it has not seen yet.</summary>
        public int DisplayBar
        {
            get
            {
                int v = ServerBar;
                foreach (var s in _local) if (s.BeginTick > ServerBarTick) v -= s.Check.Cost;
                return Math.Max(0, v);
            }
        }

        /// <summary>Latest tick this client predicted (its clock, slightly ahead of the server's).</summary>
        public int Tick { get; private set; }

        public int Mispredictions { get; private set; }
        public int ServerRejections { get; private set; }

        public const ushort TempIdBase = 0xF000;
        public const int SpecTimeoutTicks = 240;

        /// <summary>
        /// A prediction the server has not started this many ticks after its confirm was lost or came too late: it is
        /// dropped (ISSUE-9). The net client keeps it at about a round trip plus slack.
        /// </summary>
        public int AnswerTimeoutTicks { get; set; } = 30;

        private readonly List<LocalSculpt> _local = new List<LocalSculpt>();
        private readonly Dictionary<int, RemoteWindup> _windups = new Dictionary<int, RemoteWindup>();
        private readonly List<FlyingPiece> _flying = new List<FlyingPiece>();
        private readonly Dictionary<int, PlayerBody> _remoteBodies = new Dictionary<int, PlayerBody>();
        private readonly Dictionary<int, int> _projectileOwner = new Dictionary<int, int>();
        private readonly Dictionary<ushort, Int3[]> _removedShapes = new Dictionary<ushort, Int3[]>();
        private readonly HashSet<int> _seenRemote = new HashSet<int>();
        private readonly List<int> _scratchIds = new List<int>();
        private byte _nextSeq = 1;

        private struct Buffered
        {
            public bool Active;
            public byte Seq;
            public SculptAction Action;
            public int ExpireTick;
            public Int3 FollowDelta;
            public bool Chained;
            public SculptResult LastResult;
        }
        private Buffered _buffer;

        /// <summary>Confirms this client already refused (and showed red for): the server's matching refusal is not shown again.</summary>
        private readonly HashSet<byte> _localRejected = new HashSet<byte>();

        public SculptClient(SubGrid grid, SculptConfig config, MovementConfig movement)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Config = config ?? new SculptConfig();
            Movement = movement ?? new MovementConfig();
            Mirror = new SculptSystem(grid, Config);
            Mirror.Pieces.LogOps = false;
        }

        /// <summary>Joined a server (Welcome).</summary>
        public void Reset(int localId, int team, Vec3d spawn, int tick)
        {
            Mirror.Pieces.Rollback();
            foreach (var kv in _remoteBodies) Mirror.RemovePlayer(kv.Key);
            _remoteBodies.Clear();
            if (LocalBody != null) Mirror.RemovePlayer(LocalBody.Id);
            _local.Clear();
            _windups.Clear();
            _flying.Clear();
            _buffer = default;
            _localRejected.Clear();
            LocalId = localId;
            LocalTeam = team;
            LocalBody = new PlayerBody(localId, team, spawn);
            Mirror.AddPlayer(LocalBody);
            Tick = tick;
            ServerBar = SculptConfig.BarMax;
            ServerBarTick = int.MinValue;
        }

        public byte NextSeq()
        {
            byte s = _nextSeq++;
            if (_nextSeq == 0) _nextSeq = 1;
            _localRejected.Remove(s);
            return s;
        }

        private static ushort TempIdFor(byte seq) => (ushort)(TempIdBase + seq);
        public static bool IsTempId(ushort id) => id >= TempIdBase && id < TempIdBase + 256;

        // ================================================================ Bodies

        public void SetLocalBody(in MotorState s)
        {
            if (LocalBody == null) return;
            PlayerMovement.WriteBody(s, Movement, LocalBody);
            if (s.Dead) LocalBody.Position = NetServer.ParkedBody;
        }

        /// <summary>Positions of the other players as drawn (for shove planning in prediction). Call once per tick.</summary>
        public void SetRemoteBodies(NetClient net)
        {
            _scratchIds.Clear();
            net.GetRemoteIds(_scratchIds);
            _seenRemote.Clear();
            foreach (int id in _scratchIds)
            {
                if (!net.TryGetRemote(id, out RemoteView v)) continue;
                _seenRemote.Add(id);
                if (!_remoteBodies.TryGetValue(id, out var b))
                {
                    b = new PlayerBody(id, v.Team, v.Position);
                    _remoteBodies[id] = b;
                    Mirror.AddPlayer(b);
                }
                b.Team = v.Team;
                b.Position = v.Dead ? NetServer.ParkedBody : v.Position; // the dead are out of the world
                b.Height = v.Crouched ? Movement.CrouchHeight : Movement.StandHeight;
                b.EyeHeight = v.Crouched ? Movement.CrouchEye : Movement.StandEye;
            }
            _scratchIds.Clear();
            foreach (var kv in _remoteBodies) if (!_seenRemote.Contains(kv.Key)) _scratchIds.Add(kv.Key);
            foreach (int id in _scratchIds)
            {
                _remoteBodies.Remove(id);
                Mirror.RemovePlayer(id);
                if (_windups.Remove(id)) Mirror.ClearMirrorPending(id);
            }
        }

        public PlayerBody BodyOf(int id)
        {
            if (LocalBody != null && id == LocalBody.Id) return LocalBody;
            return _remoteBodies.TryGetValue(id, out var b) ? b : null;
        }

        // ================================================================ Evaluation (ghost)

        /// <summary>Full check of an action from the local player's current predicted position, as the server would see it.</summary>
        public SculptCheck Evaluate(in SculptAction a, int tick) => Evaluate(a, tick, false);

        private SculptCheck Evaluate(in SculptAction a, int tick, bool chained)
        {
            if (LocalBody == null) return new SculptCheck { Result = SculptResult.NoTarget, Type = a.Type };
            Mirror.SetTick(tick);
            Mirror.BarOf(LocalId).SetValue(DisplayBar);
            if (a.Type == SculptActionType.Throw && HasOwnProjectile()) return new SculptCheck { Result = SculptResult.Busy, Type = a.Type };
            SculptCheck c = chained ? Mirror.EvaluateChained(LocalBody, a) : Mirror.Evaluate(LocalBody, a);
            Mirror.ClearSupportQueue();
            return c;
        }

        private bool HasOwnProjectile()
        {
            foreach (var f in _flying) if (f.Owner == LocalId && !f.HitTerrain) return true;
            return false;
        }

        // ================================================================ Prediction ticks

        /// <summary>
        /// Start of predicted tick: your windups that end this tick land in your view, a buffered confirm retries, flights
        /// advance. Same order as the server's tick.
        /// </summary>
        public void BeforeMove(int tick)
        {
            Tick = tick;
            Mirror.SetTick(tick);
            for (int i = 0; i < _local.Count; i++)
            {
                LocalSculpt s = _local[i];
                if (s.Completed || s.CompleteTick > tick) continue;
                s.Completed = true;
                Mirror.ClearMirrorPending(LocalId);
                bool moved = false;
                Vec3d to = default, kick = Vec3d.Zero;
                bool planned = s.Action.Type != SculptActionType.Throw && LocalBody != null &&
                               Mirror.PlanLanding(LocalBody, s.Action, out moved, out to, out kick);
                if (Apply(s, tick))
                {
                    if (planned)
                    {
                        // Move yourself exactly as the server's resolution will (ISSUE-8).
                        if (moved) { LocalBody.Position = to; _landingMoved = true; _landingTo = to; }
                        _landingKick += kick;
                    }
                    Events.Add(new ClientSculptEvent
                    {
                        Type = ClientSculptEventType.LocalLanded, PlayerId = LocalId, Team = LocalTeam, Action = s.Action.Type, Direction = s.Action.Direction,
                        Piece = s.PieceId, Cells = CellsNow(s), Delta = s.Check.Delta, Mine = true, Reinforce = s.Action.Reinforce,
                    });
                }
            }
            _deferredLock = int.MinValue;
            if (_buffer.Active)
            {
                if (tick > _buffer.ExpireTick)
                {
                    _buffer.Active = false;
                    RejectLocal(_buffer.Seq, _buffer.Action, _buffer.LastResult == SculptResult.Valid ? SculptResult.Locked : _buffer.LastResult);
                }
                else
                {
                    // Same order and rule as the server: a click queued on your own moving piece follows it once it lands.
                    if (_buffer.FollowDelta != Int3.Zero && Mirror.PendingOf(LocalId) == null)
                    {
                        if (Mirror.TryFollow(ref _buffer.Action, _buffer.FollowDelta)) _buffer.Chained = true;
                        _buffer.FollowDelta = Int3.Zero;
                    }
                    SculptCheck c = Evaluate(_buffer.Action, tick, _buffer.Chained);
                    _buffer.LastResult = c.Result;
                    if (c.IsValid)
                    {
                        _buffer.Active = false;
                        _deferredLock = Begin(_buffer.Seq, _buffer.Action, c, tick);
                    }
                    else if (c.Result != SculptResult.Locked && c.Result != SculptResult.Busy)
                    {
                        _buffer.Active = false;
                        RejectLocal(_buffer.Seq, _buffer.Action, c.Result);
                    }
                }
            }
            AdvanceFlights(tick);
            ExpireStale(tick);
        }

        private int _deferredLock = int.MinValue;
        private bool _landingMoved;
        private Vec3d _landingTo, _landingKick;

        /// <summary>
        /// What your own sculpt landing did to you this tick (shove or lift to a position, friction kick). The net client
        /// applies it to the predicted state before movement, exactly where the server applies the shared body's.
        /// </summary>
        public bool TakeLandingMove(out bool moved, out Vec3d to, out Vec3d kick)
        {
            moved = _landingMoved;
            to = _landingTo;
            kick = _landingKick;
            _landingMoved = false;
            _landingKick = Vec3d.Zero;
            return moved || kick.LengthSquared > 0;
        }

        /// <summary>
        /// After the local player's movement for the tick: handles a confirm in this tick's command. Returns the gun lock
        /// to apply to the predicted hand (int.MinValue for none), exactly as the server does after movement.
        /// </summary>
        public int AfterMove(int tick, in MotorState predicted, in InputCmd cmd)
        {
            SetLocalBody(predicted);
            int lockUntil = _deferredLock;
            _deferredLock = int.MinValue;
            if (!cmd.HasSculpt) return lockUntil;
            if (!predicted.Hand.CanConfirmSculpt)
            {
                RejectLocal(cmd.SculptSeq, cmd.Sculpt, SculptResult.NotInSculptMode);
                return lockUntil;
            }
            SculptCheck c = Evaluate(cmd.Sculpt, tick);
            if (c.IsValid)
            {
                if (_buffer.Active) { _localRejected.Add(_buffer.Seq); _buffer.Active = false; } // replaced: silent
                return Math.Max(lockUntil, Begin(cmd.SculptSeq, cmd.Sculpt, c, tick));
            }
            if ((c.Result == SculptResult.Locked || c.Result == SculptResult.Busy) && Config.ConfirmBufferTicks > 0)
            {
                if (_buffer.Active && _buffer.Seq != cmd.SculptSeq) _localRejected.Add(_buffer.Seq); // the newest click wins, silently
                Mirror.QueueRule(LocalId, cmd.Sculpt, out int expire, out Int3 follow);
                _buffer = new Buffered { Active = true, Seq = cmd.SculptSeq, Action = cmd.Sculpt, ExpireTick = expire, FollowDelta = follow, LastResult = c.Result };
                return lockUntil;
            }
            RejectLocal(cmd.SculptSeq, cmd.Sculpt, c.Result);
            return lockUntil;
        }

        private int Begin(byte seq, SculptAction a, SculptCheck c, int tick)
        {
            a.Target = c.Target; // as the server stores it
            var s = new LocalSculpt
            {
                Seq = seq,
                Action = a,
                Check = c,
                BeginTick = tick,
                CompleteTick = tick + c.WindupTicks,
                PieceId = c.PieceId,
                TempId = TempIdFor(seq),
            };
            _local.Add(s);
            Mirror.MirrorPending(LocalId, a, c, tick, s.CompleteTick);
            Events.Add(new ClientSculptEvent
            {
                Type = ClientSculptEventType.LocalBegan, PlayerId = LocalId, Team = LocalTeam, Action = a.Type, Direction = a.Direction,
                Piece = c.PieceId, Cells = c.Cells, Delta = c.Delta, Value = c.WindupTicks, Mine = true, Reinforce = a.Reinforce || c.EnemyReinforced,
            });
            return c.LocksGun ? NetServer.GunLockUntil(Config, c, tick) : int.MinValue;
        }

        private void RejectLocal(byte seq, in SculptAction a, SculptResult r)
        {
            _localRejected.Add(seq);
            Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Rejected, PlayerId = LocalId, Team = LocalTeam, Action = a.Type, Direction = a.Direction, Result = r, Value = seq, Mine = true });
        }

        private Int3[] CellsNow(LocalSculpt s)
        {
            PieceInfo p = Mirror.Pieces.Get(s.PieceId != 0 ? s.PieceId : s.TempId);
            return p != null ? p.Cells.ToArray() : s.Check.Cells;
        }

        /// <summary>Writes a predicted action into the grid (inside the journal). False if it no longer fits.</summary>
        private bool Apply(LocalSculpt s, int tick)
        {
            PieceTable pieces = Mirror.Pieces;
            if (!pieces.Journaling) pieces.BeginJournal();
            ushort id = s.PieceId;
            PieceInfo info = id != 0 ? pieces.Get(id) : null;
            Int3[] cells;
            bool carve = false;
            if (info != null) cells = info.Cells.ToArray();
            else
            {
                if (id != 0) return false; // the piece is gone (or the prediction it was chained on did not apply)
                cells = s.Check.Cells;
                foreach (var x in cells) if (Grid.Get(x) != SubId.Natural) return false;
                carve = true;
                id = s.TempId;
            }

            if (s.Action.Type != SculptActionType.Throw && !DestinationFree(cells, s.Check.Delta, s.Action.Type)) return false;

            if (carve) pieces.Apply(new PieceOp { Type = PieceOpType.Carve, Id = id, Cells = PieceTable.SortedCopy(cells) });
            info = pieces.Get(id);

            if (s.Action.Type == SculptActionType.Throw)
            {
                Int3[] shape = info.Cells.ToArray();
                pieces.Remove(id);
                if (s.Projectile == null)
                {
                    ThrownPiece t = Mirror.PredictProjectile(LocalBody, shape, s.Action.LookDir, Grid);
                    t.PieceId = id;
                    t.LaunchTick = tick;
                    var f = new FlyingPiece { State = t, StateTick = tick - 1, PrevPosition = t.Position, Cells = shape, Owner = LocalId, Team = LocalTeam, Predicted = true };
                    s.Projectile = f;
                    _flying.Add(f);
                    FlyOne(f, tick); // the server flies it once in its launch tick
                    Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.ThrowLaunched, PlayerId = LocalId, Team = LocalTeam, Cells = shape, Point = t.Position, Mine = true });
                }
            }
            else
            {
                byte rTeam = info.ReinforceTeam;
                int rUntil = info.ReinforceUntilTick;
                if (s.Check.EnemyReinforced) { rTeam = 0; rUntil = 0; }
                if (s.Action.Reinforce) { rTeam = (byte)LocalTeam; rUntil = s.CompleteTick + Config.ReinforceDurationTicks; }
                pieces.Translate(id, s.Check.Delta, s.Action.Type == SculptActionType.Raise);
                pieces.Stamp(id, (byte)LocalTeam, s.CompleteTick, rTeam, rUntil);
            }
            Mirror.ClearSupportQueue();
            s.Applied = true;
            return true;
        }

        private bool DestinationFree(Int3[] cells, Int3 delta, SculptActionType type)
        {
            var set = new HashSet<Int3>(cells);
            int steps = Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z);
            if (steps == 0) return true;
            var unit = new Int3(Math.Sign(delta.X), Math.Sign(delta.Y), Math.Sign(delta.Z));
            foreach (var x in cells)
                for (int k = 1; k <= steps; k++)
                {
                    Int3 d = x + unit * k;
                    if (set.Contains(d)) continue;
                    if (!Grid.InBounds(d)) return false;
                    ushort v = Grid.Get(d);
                    if (v == SubId.Empty) continue;
                    if (type == SculptActionType.Sink && v == SubId.Natural) continue;
                    return false;
                }
            return true;
        }

        private bool Stale(LocalSculpt s, int tick) =>
            tick - s.CompleteTick > SpecTimeoutTicks || (!s.ServerBegan && tick - s.BeginTick > AnswerTimeoutTicks);

        private void ExpireStale(int tick)
        {
            // Flights whose impact report never came (it always should): forget them.
            _flying.RemoveAll(f => f.HitTerrain && tick - f.HitTick > 180);
            // A prediction the server never answered (it should always answer): drop it eventually.
            // A prediction the server never started: its confirm was lost or too late (ISSUE-9). Drop it about a round
            // trip after the server should have begun it.
            bool any = false;
            foreach (var s in _local) if (Stale(s, tick)) { any = true; break; }
            if (!any) return;
            Mirror.Pieces.Rollback();
            foreach (var s in _local.ToArray())
            {
                s.Applied = false;
                if (!Stale(s, tick)) continue;
                DropLocal(s);
                ServerRejections++;
                Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Rejected, PlayerId = LocalId, Team = LocalTeam, Action = s.Action.Type, Result = SculptResult.TooLate, Value = s.Seq, Mine = true });
            }
            Reapply();
        }

        // ================================================================ Flights

        private void AdvanceFlights(int tick)
        {
            foreach (var f in _flying) FlyTo(f, f.Predicted || f.Owner == LocalId ? tick : int.MinValue);
        }

        /// <summary>Flies a projectile forward to the given tick (terrain only). Server-owned ones are flown on demand by
        /// <see cref="FlyTo"/> from the drawing code, at the server time remote players are drawn at.</summary>
        public void FlyTo(FlyingPiece f, int tick)
        {
            int guard = 0;
            while (f.StateTick < tick && !f.HitTerrain && guard++ < 600) FlyOne(f, f.StateTick + 1);
        }

        private void FlyOne(FlyingPiece f, int tick)
        {
            f.PrevPosition = f.State.Position;
            if (Mirror.AdvanceProjectile(f.State, tick, Grid)) { f.HitTerrain = true; f.HitTick = tick; }
            f.StateTick = tick;
            if (f.State.Y * ThrownPiece.Unit < -30) { f.HitTerrain = true; f.HitTick = tick; }
        }

        // ================================================================ Server messages

        public void OnSelfBar(int serverTick, int bar)
        {
            if (serverTick < ServerBarTick) return;
            ServerBarTick = serverTick;
            ServerBar = bar;
        }

        public void OnWorld(in WorldMsg m)
        {
            PieceTable pieces = Mirror.Pieces;
            pieces.Rollback();
            foreach (var s in _local) s.Applied = false;

            _removedShapes.Clear();
            foreach (PieceOp op in m.Ops)
            {
                if (op.Type == PieceOpType.Remove)
                {
                    PieceInfo pi = pieces.Get(op.Id);
                    if (pi != null) _removedShapes[op.Id] = pi.Cells.ToArray();
                }
                try { pieces.Apply(op); }
                catch (KeyNotFoundException) { Mispredictions++; } // never expected: the op log is reliable and ordered
            }
            Mirror.ClearSupportQueue();

            foreach (WorldEvent e in m.Events) Handle(e, m.Join);
            Reapply();
        }

        private void Reapply()
        {
            PieceTable pieces = Mirror.Pieces;
            if (!pieces.Journaling) pieces.BeginJournal();
            foreach (var s in _local)
                if (s.Completed && !s.Applied) Apply(s, s.CompleteTick);
        }

        private LocalSculpt FindLocal(byte seq)
        {
            foreach (var s in _local) if (s.Seq == seq) return s;
            return null;
        }

        private void DropLocal(LocalSculpt s)
        {
            _local.Remove(s);
            if (!s.Completed) Mirror.ClearMirrorPending(LocalId);
            if (s.Projectile != null && s.Projectile.Predicted) _flying.Remove(s.Projectile);
        }

        private void Handle(in WorldEvent e, bool join)
        {
            bool mine = e.PlayerId == LocalId;
            switch (e.Type)
            {
                case WorldEventType.Began:
                {
                    if (mine) _localRejected.Remove(e.Seq);
                    LocalSculpt s = mine && !join ? FindLocal(e.Seq) : null;
                    if (s != null)
                    {
                        s.ServerBegan = true;
                        RenameTemp(s, e.Piece);
                        if (e.StartTick != s.BeginTick) Mispredictions++;
                        break;
                    }
                    PieceInfo pi = Mirror.Pieces.Get(e.Piece);
                    Int3[] cells = pi != null ? pi.Cells.ToArray() : Array.Empty<Int3>();
                    var action = new SculptAction { Type = e.Action, Direction = e.Direction, Reinforce = e.Reinforce, LookDir = e.Look };
                    var check = new SculptCheck
                    {
                        Result = SculptResult.Valid, Type = e.Action, PieceId = e.Piece, Cells = cells, Delta = Config.DeltaFor(e.Action, e.Direction),
                        Cost = e.Cost, WindupTicks = e.WindupTicks, LocksGun = e.LocksGun, EnemyReinforced = e.EnemyReinforced,
                    };
                    bool warns = false;
                    if (e.Warned != null) foreach (int id in e.Warned) if (id == LocalId) warns = true;
                    var w = new RemoteWindup
                    {
                        PlayerId = e.PlayerId, Team = e.Team, Seq = e.Seq, Piece = e.Piece, Action = action, Check = check,
                        StartTick = e.StartTick, CompleteTick = e.StartTick + e.WindupTicks, WarnsMe = warns,
                    };
                    _windups[e.PlayerId] = w;
                    Mirror.MirrorPending(e.PlayerId, action, check, w.StartTick, w.CompleteTick);
                    if (mine) Mispredictions++; // the server started something this client did not predict
                    Events.Add(new ClientSculptEvent
                    {
                        Type = ClientSculptEventType.Began, PlayerId = e.PlayerId, Team = e.Team, Action = e.Action, Direction = e.Direction,
                        Piece = e.Piece, Cells = cells, Delta = check.Delta, Value = e.WindupTicks, Mine = mine, Reinforce = e.Reinforce || e.EnemyReinforced,
                    });
                    if (warns)
                        Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Warned, PlayerId = e.PlayerId, Team = e.Team, Action = e.Action, Direction = e.Direction, Piece = e.Piece, Cells = cells });
                    break;
                }
                case WorldEventType.Rejected:
                {
                    if (!mine) break;
                    LocalSculpt s = FindLocal(e.Seq);
                    if (_buffer.Active && _buffer.Seq == e.Seq) _buffer.Active = false;
                    ServerRejections++;
                    if (s != null) { DropLocal(s); Mispredictions++; }
                    // Already shown red here, or a queued click replaced by a newer one: nothing more to show.
                    bool shown = _localRejected.Remove(e.Seq) && s == null;
                    if (shown || e.Result == SculptResult.Superseded) break;
                    Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Rejected, PlayerId = LocalId, Team = LocalTeam, Action = s?.Action.Type ?? SculptActionType.Move, Result = e.Result, Value = e.Seq, Mine = true });
                    break;
                }
                case WorldEventType.Completed:
                case WorldEventType.Cancelled:
                {
                    bool done = e.Type == WorldEventType.Completed;
                    LocalSculpt s = mine ? FindLocal(e.Seq) : null;
                    bool hadWindup = _windups.TryGetValue(e.PlayerId, out RemoteWindup w) && (!mine || w.Seq == e.Seq);
                    if (hadWindup)
                    {
                        _windups.Remove(e.PlayerId);
                        Mirror.ClearMirrorPending(e.PlayerId);
                    }
                    if (s != null)
                    {
                        // The prediction is answered: the server's ops (applied above) are the truth now.
                        RenameTemp(s, e.Piece);
                        if (s.Projectile != null && s.Projectile.Predicted)
                        {
                            // Keep flying the predicted one until the server's Thrown replaces it (same message, normally).
                        }
                        _local.Remove(s);
                        if (!s.Completed) Mirror.ClearMirrorPending(LocalId);
                        if (!done)
                        {
                            ServerRejections++;
                            Mispredictions++;
                            if (s.Projectile != null && s.Projectile.Predicted) _flying.Remove(s.Projectile);
                            Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Rejected, PlayerId = LocalId, Team = LocalTeam, Action = s.Action.Type, Result = e.Result, Value = e.Seq, Mine = true });
                        }
                        break;
                    }
                    PieceInfo pi = Mirror.Pieces.Get(e.Piece);
                    Events.Add(new ClientSculptEvent
                    {
                        Type = done ? ClientSculptEventType.Completed : ClientSculptEventType.Cancelled,
                        PlayerId = e.PlayerId, Team = w?.Team ?? 0, Action = w?.Action.Type ?? SculptActionType.Move, Direction = w?.Action.Direction ?? Dir.East,
                        Piece = e.Piece, Cells = pi != null ? pi.Cells.ToArray() : (w?.Check.Cells ?? Array.Empty<Int3>()), Delta = w?.Check.Delta ?? Int3.Zero,
                        Result = e.Result, Mine = mine, Reinforce = w != null && (w.Action.Reinforce || w.Check.EnemyReinforced),
                    });
                    break;
                }
                case WorldEventType.Landed:
                {
                    PieceInfo pi = Mirror.Pieces.Get(e.Piece);
                    if (pi != null) Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.PieceLanded, Piece = e.Piece, Cells = pi.Cells.ToArray(), Team = pi.Team });
                    break;
                }
                case WorldEventType.Shoved:
                    if (mine) Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Shoved, PlayerId = e.PlayerId, Piece = e.Piece, Mine = true });
                    break;
                case WorldEventType.Dragged:
                    if (mine) Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.Dragged, PlayerId = e.PlayerId, Piece = e.Piece, Mine = true });
                    break;
                case WorldEventType.Thrown:
                {
                    _projectileOwner[e.ProjectileId] = e.PlayerId;
                    Int3[] shape = _removedShapes.TryGetValue(e.Piece, out var sh) ? sh : null;
                    FlyingPiece f = null;
                    if (mine && !join)
                        foreach (var x in _flying) if (x.Predicted && x.Owner == LocalId) { f = x; break; }
                    if (f == null)
                    {
                        f = new FlyingPiece { Owner = e.PlayerId, Team = e.Team, Cells = shape ?? Array.Empty<Int3>() };
                        _flying.Add(f);
                        if (!mine) Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.ThrowLaunched, PlayerId = e.PlayerId, Team = e.Team, Cells = f.Cells, Point = e.ToProjectile().Position });
                    }
                    else if (shape != null) f.Cells = shape;
                    ThrownPiece t = e.ToProjectile();
                    Vec3d prev = f.State != null ? f.State.Position : t.Position;
                    f.State = t;
                    f.StateTick = e.AtTick;
                    f.PrevPosition = prev;
                    f.Predicted = false;
                    f.HitTerrain = false;
                    if (mine) FlyTo(f, Tick); // keep drawing your own throw on your own (predicted) clock
                    foreach (var s in _local) if (s.Projectile == f) s.Projectile = null;
                    break;
                }
                case WorldEventType.ThrowImpact:
                {
                    _projectileOwner.TryGetValue(e.ProjectileId, out int owner);
                    int ownerId = e.PlayerId != 0 ? e.PlayerId : owner;
                    FlyingPiece hit = null;
                    foreach (var f in _flying) if (!f.Predicted && f.State != null && f.State.Id == e.ProjectileId) { hit = f; break; }
                    if (hit == null && ownerId == LocalId) foreach (var f in _flying) if (f.Predicted && f.Owner == LocalId) { hit = f; break; }
                    Int3[] cells = hit?.Cells ?? Array.Empty<Int3>();
                    if (hit != null) _flying.Remove(hit);
                    Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.ThrowImpact, PlayerId = ownerId, Point = e.Point, Cells = cells, Value = cells.Length, Mine = ownerId == LocalId });
                    break;
                }
                case WorldEventType.ThrowHit:
                {
                    _projectileOwner.TryGetValue(e.ProjectileId, out int owner);
                    Events.Add(new ClientSculptEvent { Type = ClientSculptEventType.ThrowHit, PlayerId = e.PlayerId, Value = e.Value, Point = e.Point, Mine = owner == LocalId && owner != 0, Result = SculptResult.Valid, Piece = e.Piece, Delta = new Int3(e.Disarm, 0, 0) });
                    break;
                }
            }
        }

        /// <summary>A predicted carve's temporary id becomes the server's real id (here and in chained predictions).</summary>
        private void RenameTemp(LocalSculpt s, ushort real)
        {
            if (real == 0) return;
            if (s.PieceId == 0 || IsTempId(s.PieceId)) s.PieceId = real; // the server says which piece it was
            ushort temp = s.TempId;
            foreach (var o in _local)
            {
                if (o.PieceId == temp) o.PieceId = real;
                if (o.Check.PieceId == temp) o.Check.PieceId = real;
            }
        }


        /// <summary>
        /// Reinforcement for drawing (crystalline veins and shimmer): 1 while reinforced, easing to 0 over the last
        /// <paramref name="fadeTicks"/> before it decays, so you can read when it is about to run out. 0 if not reinforced.
        /// </summary>
        public float ReinforceOf(ushort id, int tick, int fadeTicks = 180)
        {
            PieceInfo p = Mirror.Pieces.Get(id);
            if (p == null || !p.IsReinforced(tick)) return 0f;
            float k = (p.ReinforceUntilTick - tick) / (float)Math.Max(1, fadeTicks);
            return k > 1 ? 1f : (k < 0.25f ? 0.25f : k);
        }

        /// <summary>Tint for drawing: the piece's team and 1..0 as it fades.</summary>
        public (int team, float strength) TintOf(ushort id, int tick)
        {
            PieceInfo p = Mirror.Pieces.Get(id);
            if (p == null || p.Team == 0) return (0, 0f);
            float k = 1f - (tick - p.TintTick) / (float)Math.Max(1, Config.TintFadeTicks);
            return (p.Team, k < 0 ? 0f : (k > 1 ? 1f : k));
        }
    }
}
