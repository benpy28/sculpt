using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>What the client draws for another player at the current render time.</summary>
    public struct RemoteView
    {
        public int Id;
        public int Team;
        public Vec3d Position;
        public Vec3d Velocity;
        public double Yaw;     // radians
        public double Pitch;   // degrees
        public MoveMode Mode;
        public bool Crouched;
        public bool Dead;
        public Item Held;
        public HandMode Hand;
        /// <summary>True when the snapshot stream ran dry and this pose is extrapolated or held.</summary>
        public bool Extrapolated;
    }

    /// <summary>
    /// Client side of the netcode, pure Core.
    /// - Runs its own tick clock slightly ahead of the server so each command arrives just before it is needed
    ///   (the server reports how early commands arrive; the clock speeds up or slows down by a few percent).
    /// - Predicts the local player instantly with the exact quantized command it sends.
    /// - On every snapshot, compares the server's state for that tick with its own history; on any difference it
    ///   rewinds to the server state and replays the newer commands. The sim snaps; only the drawn position is smoothed.
    /// - Draws other players interpolated a few ticks in the past.
    /// </summary>
    public sealed class NetClient
    {
        public delegate void SendFn(byte[] data, bool reliable);

        public readonly ITerrain Grid;
        /// <summary>This client's copy of the wall tiles behind <see cref="Grid"/> (null without buildings). Only the
        /// server's Tiles messages change it; its Changes list is for the Game layer to drain (effects).</summary>
        public readonly TileWalls Walls;
        /// <summary>Your own charge breaks, predicted on <see cref="Walls"/> until the server confirms or refuses them.</summary>
        public readonly PredictedChargeBreaker Charges = new PredictedChargeBreaker();
        /// <summary>Your own door presses, shown at once until the server answers.</summary>
        public readonly PredictedDoors Doors = new PredictedDoors();
        private readonly List<Aabb> _doorBodies = new List<Aabb>();
        /// <summary>Everyone's heals as the server reported them (null without tiles). Also runs the press checks.</summary>
        public readonly TileHealing Heals;
        /// <summary>Heal events from the server (everyone's), for the Game layer to drain (sounds, effects, red refusals).</summary>
        public readonly List<HealEvent> HealEvents = new List<HealEvent>();
        private readonly List<HealEvent> _healScratch = new List<HealEvent>();
        private bool _healPending, _healAcked;
        private HealJob _healPress;
        private int _healAckTick, _healRefund;
        /// <summary>Your sculpt bar as the server last reported it, and the tick of that report.</summary>
        public int ServerBar { get; private set; } = SculptConfig.BarMax;
        public int ServerBarTick { get; private set; } = int.MinValue;
        public readonly MovementConfig Movement;
        public readonly NetConfig Config;
        /// <summary>Optional: networked sculpting on a sub-cell grid (prediction, server ops, windups, flights).</summary>
        public readonly SculptClient Sculpt;

        // Latency overlay: how long the server said it held our command for a tick (see SnapshotMsg.SelfHoldTenthMs).
        private readonly int[] _holdTick = new int[128];
        private readonly double[] _holdMs = new double[128];

        /// <summary>
        /// Milliseconds the server held this client's command for <paramref name="tick"/> (arrival to snapshot sent),
        /// or a negative number when it did not say (no server clock, a guessed command, or the snapshot is too old).
        /// </summary>
        public double ServerHoldMs(int tick)
        {
            int i = tick & (_holdTick.Length - 1);
            return _holdTick[i] == tick ? _holdMs[i] : -1;
        }

        public bool Joined { get; private set; }
        public int PlayerId { get; private set; }
        public int Team { get; private set; }
        public Vec3d Spawn { get; private set; }
        public double SpawnYaw { get; private set; }

        /// <summary>Tick of the newest predicted command.</summary>
        public int Tick { get; private set; }
        public MotorState Predicted => _pred;
        public MotorState PreviousPredicted => _prevPred;
        /// <summary>Drawn-position offset left over from corrections; decays to zero.</summary>
        public Vec3d CorrectionOffset { get; private set; }

        // Stats for the HUD and tests.
        public int Corrections { get; private set; }       // visible (moved the player)
        public int SilentResyncs { get; private set; }      // tiny or invisible differences
        public double LastCorrectionMeters { get; private set; }
        public int LastServerLead { get; private set; } = Protocol.NoLead;
        public double Dilation { get; private set; }
        public int ClockSnaps { get; private set; }
        public double RttTicks { get; private set; }
        public int LastSnapshotTick { get; private set; } = int.MinValue;
        /// <summary>The match as the server last told it (phase, timer, scores).</summary>
        public readonly MatchState Match = new MatchState();
        /// <summary>Set when the server can't be joined (a different game build); empty otherwise.</summary>
        public string JoinError { get; private set; } = "";
        /// <summary>Smoothed estimate of the server's tick right now (for timers).</summary>
        public double ServerTimeEstimate => _serverTime;
        public int SnapshotsReceived { get; private set; }
        public int DroppedPackets { get; private set; }
        public bool LastInputWasRealOnServer { get; private set; } = true;

        /// <summary>Shots the local player fired in prediction (each once, never on replays). The Game layer drains it.</summary>
        public readonly List<WeaponShot> LocalShots = new List<WeaponShot>();
        /// <summary>Your own grenade throws and blasts as predicted (each once, never on replays). The Game layer drains it.</summary>
        public readonly List<GrenadeEvent> LocalGrenades = new List<GrenadeEvent>();
        /// <summary>Combat events from the server (others' shots, hits, kills), with their server tick. The Game layer drains it.</summary>
        public readonly List<KeyValuePair<int, CombatEvent>> Combat = new List<KeyValuePair<int, CombatEvent>>();
        private readonly List<CombatEvent> _combatScratch = new List<CombatEvent>();
        private readonly List<TileChange> _tileScratch = new List<TileChange>();
        private readonly List<GroundItem> _itemScratch = new List<GroundItem>();

        /// <summary>This client's copy of the items on the ground (only the server's Items messages change it).</summary>
        public readonly GroundItems Items = new GroundItems();

        public double TickSeconds => 1.0 / SimTime.TicksPerSecond;
        /// <summary>Fraction of a tick elapsed since the last predicted tick, for drawing. 0..1.</summary>
        public double Alpha => Math.Max(0, Math.Min(1, _accum / TickSeconds));

        private readonly SendFn _send;
        private readonly ByteWriter _w = new ByteWriter(512);
        private readonly List<InputCmd> _sendCmds = new List<InputCmd>(Protocol.MaxCmdsPerPacket);
        private readonly List<PlayerSnap> _snapOthers = new List<PlayerSnap>();
        private readonly List<InputCmd> _readScratch = new List<InputCmd>();
        private readonly List<PieceOp> _worldOps = new List<PieceOp>();
        private readonly List<WorldEvent> _worldEvents = new List<WorldEvent>();

        private readonly InputCmd[] _histCmd;
        private readonly MotorState[] _histState;
        private readonly int[] _histLock; // gun lock a sculpt begin put on the hand after that tick's movement
        private readonly int[] _histTick;
        // Own sculpt landings (ISSUE-8): applied to the state right before that tick's movement, also on replays.
        private readonly bool[] _histMoved;
        private readonly Vec3d[] _histMoveTo, _histKick;
        private readonly int _histMask;

        private MotorState _pred, _prevPred;
        private double _accum;
        private int _clockHoldUntilServerTick = int.MinValue;

        private double _serverTime;   // estimate of the newest snapshot tick "now", in ticks
        private bool _serverTimeValid;
        private readonly Dictionary<int, RemoteTrack> _remotes = new Dictionary<int, RemoteTrack>();
        private readonly List<int> _removeScratch = new List<int>();

        public NetClient(ITerrain grid, MovementConfig movement, NetConfig config, SendFn send, SculptClient sculpt = null)
        {
            Grid = grid;
            Walls = TileWalls.Find(grid);
            if (Walls != null) { Heals = new TileHealing(Walls); Walls.CopyOnly = true; }
            Movement = movement;
            Config = config;
            _send = send ?? throw new ArgumentNullException(nameof(send));
            int n = 1;
            while (n < Math.Max(32, config.HistoryTicks)) n <<= 1;
            _histCmd = new InputCmd[n];
            _histState = new MotorState[n];
            _histTick = new int[n];
            _histLock = new int[n];
            _histMoved = new bool[n];
            _histMoveTo = new Vec3d[n];
            _histKick = new Vec3d[n];
            Sculpt = sculpt;
            _histMask = n - 1;
            ClearHistory();
        }

        private void ClearHistory()
        {
            for (int i = 0; i < _histTick.Length; i++) _histTick[i] = int.MinValue;
            for (int i = 0; i < _holdTick.Length; i++) _holdTick[i] = int.MinValue;
        }

        // ================================================================ Receiving

        public void Receive(byte[] data, int offset, int count)
        {
            try
            {
                var r = new ByteReader(data, offset, count);
                switch (Protocol.PeekType(data, offset, count))
                {
                    case MsgType.Welcome: OnWelcome(Protocol.ReadWelcome(ref r)); break;
                    case MsgType.World:
                        if (!Joined || Sculpt == null) return;
                        Sculpt.OnWorld(WorldProtocol.Read(ref r, _worldOps, _worldEvents));
                        break;
                    case MsgType.Combat:
                    {
                        if (!Joined) return;
                        CombatMsg cm = CombatProtocol.Read(ref r, _combatScratch);
                        foreach (CombatEvent e in cm.Events) Combat.Add(new KeyValuePair<int, CombatEvent>(cm.Tick, e));
                        if (Combat.Count > 4096) Combat.RemoveRange(0, 2048); // nobody is draining (tests)
                        break;
                    }
                    case MsgType.Tiles:
                    {
                        if (!Joined || Walls == null) return;
                        TilesMsg tm = TileProtocol.Read(ref r, _tileScratch, _healScratch);
                        foreach (TileChange c in tm.Changes)
                        {
                            if (c.Tile >= Walls.Count) { DroppedPackets++; continue; }
                            Walls.Tick = tm.Tick;
                            TileCause cause = tm.Join ? TileCause.Server : c.Cause;
                            if (c.Hp != null && Walls.HasCells(c.Tile))
                                Walls.WriteCells(c.Tile, c.To == TileState.Broken ? ulong.MaxValue : c.Open, c.Patch, c.Steel, c.Hp, cause, c.By);
                            else Walls.Set(c.Tile, c.To, c.Health, cause, c.By);
                            Doors.OnServerChange(Walls, c.Tile, c.To, c.Health, tm.Tick);
                        }
                        if (Walls.Changes.Count > 4096) Walls.Changes.RemoveRange(0, 2048); // nobody is draining (tests)
                        foreach (HealEvent e in tm.Heals)
                        {
                            if (e.Tile >= Walls.Count) { DroppedPackets++; continue; }
                            OnHealEvent(e);
                        }
                        if (HealEvents.Count > 4096) HealEvents.RemoveRange(0, 2048);
                        break;
                    }
                    case MsgType.Items:
                    {
                        if (!Joined) return;
                        ItemsMsg im = ItemsProtocol.Read(ref r, _itemScratch);
                        Items.ReplaceAll(im.Items);
                        break;
                    }
                    case MsgType.Match:
                        if (!Joined) return;
                        MatchProtocol.Read(ref r, Match);
                        break;
                    case MsgType.Snapshot:
                        if (!Joined) return;
                        OnSnapshot(Protocol.ReadSnapshot(ref r, _snapOthers));
                        break;
                    default: DroppedPackets++; break;
                }
            }
            catch (NetFormatException)
            {
                DroppedPackets++;
            }
        }

        private void OnWelcome(in WelcomeMsg m)
        {
            if (m.ProtocolVersion != Protocol.Version)
            {
                JoinError = m.ProtocolVersion > Protocol.Version ? "The host runs a newer build of the game. Use the same build on both PCs." : "The host runs an older build of the game. Use the same build on both PCs.";
                throw new NetFormatException($"Protocol {m.ProtocolVersion}, expected {Protocol.Version}.");
            }
            Joined = true;
            Match.Phase = MatchPhase.Off;
            Match.Scores.Clear();
            PlayerId = m.PlayerId;
            Team = m.Team;
            Spawn = m.Spawn;
            SpawnYaw = m.SpawnYaw;
            // First guess at the lead; the server's feedback corrects it within a round trip.
            Tick = m.ServerTick + Config.TargetLeadTicks + 6 * SimTime.Per60;
            _pred = MotorState.At(m.Spawn);
            _prevPred = _pred;
            _accum = 0;
            ClearHistory();
            _remotes.Clear();
            _serverTime = m.ServerTick;
            _serverTimeValid = true;
            LastSnapshotTick = int.MinValue;
            Sculpt?.Reset(m.PlayerId, m.Team, m.Spawn, Tick);
            Heals?.Clear();
            _healPending = false;
            ServerBar = SculptConfig.BarMax;
            ServerBarTick = int.MinValue;
        }

        private void OnSnapshot(in SnapshotMsg m)
        {
            if (m.ServerTick <= LastSnapshotTick) return; // late or duplicate
            LastSnapshotTick = m.ServerTick;
            SnapshotsReceived++;

            // Server clock estimate for drawing remote players.
            double err = m.ServerTick - _serverTime;
            if (!_serverTimeValid || Math.Abs(err) > 10) _serverTime = m.ServerTick;
            else _serverTime += err * (err > 0 ? 0.1 : 0.02);
            _serverTimeValid = true;

            // Remote players.
            foreach (var kv in _remotes) kv.Value.SeenThisSnapshot = false;
            foreach (PlayerSnap p in m.Others)
            {
                if (!_remotes.TryGetValue(p.Id, out var track))
                {
                    track = new RemoteTrack();
                    _remotes[p.Id] = track;
                }
                track.Add(m.ServerTick, p);
                track.SeenThisSnapshot = true;
            }
            _removeScratch.Clear();
            foreach (var kv in _remotes) if (!kv.Value.SeenThisSnapshot) _removeScratch.Add(kv.Key);
            foreach (int id in _removeScratch) _remotes.Remove(id);

            if (!m.HasSelf) return;
            LastInputWasRealOnServer = m.SelfInputWasReal;
            if (m.SelfHoldTenthMs != Protocol.NoHold)
            {
                int hi = m.ServerTick & (_holdTick.Length - 1);
                _holdTick[hi] = m.ServerTick;
                _holdMs[hi] = m.SelfHoldTenthMs / 10.0;
            }
            Sculpt?.OnSelfBar(m.ServerTick, m.SelfBar);
            ServerBarTick = m.ServerTick;
            ServerBar = m.SelfBar;
            RttTicks += ((Tick - m.ServerTick - Math.Max(0, LastServerLead == Protocol.NoLead ? 0 : LastServerLead)) - RttTicks) * 0.1;
            // Our confirm for tick T is answered (Began) in the world message of server tick T, which we see about a
            // snapshot round trip after predicting T. Allow 1.5x that plus slack before giving up on it.
            if (Sculpt != null) Sculpt.AnswerTimeoutTicks = Math.Max(20 * SimTime.Per60, (int)Math.Ceiling((Tick - m.ServerTick) * 1.5) + 12 * SimTime.Per60);
            AdjustClock(m.ServerTick, m.SelfLead);
            Reconcile(m.ServerTick, m.Self);
        }

        private void AdjustClock(int serverTick, int lead)
        {
            LastServerLead = lead;
            if (lead == Protocol.NoLead) { Dilation = 0; return; }
            if (serverTick < _clockHoldUntilServerTick) { Dilation = 0; return; }

            int err = lead - Config.TargetLeadTicks;
            // Late inputs (lead below zero) cost a correction on every snapshot, so jump straight to the target
            // however small the error; slewing at a few percent would take seconds (ISSUE-5).
            if (Math.Abs(err) > Config.ClockSnapTicks || lead < 0)
            {
                // Positive err: we are too far ahead, so stall; negative: run extra ticks now.
                _accum -= err * TickSeconds;
                Dilation = 0;
                ClockSnaps++;
                // The server's window still holds leads from before the snap. Commands sent after it reach the server
                // about one round trip after serverTick; wait until those fill the whole window.
                int rtt = Math.Max(0, Tick - serverTick) + Math.Max(0, -err);
                _clockHoldUntilServerTick = serverTick + rtt + Config.LeadWindowTicks + rtt + 4;
                return;
            }
            int deadband = Math.Max(0, Config.ClockDeadbandTicks);
            double d = 0;
            if (err < 0) d = -err * Config.ClockGainPerTick;                    // too close: speed up
            else if (err > deadband) d = -(err - deadband) * Config.ClockGainPerTick; // too early: slow down
            Dilation = Math.Max(-Config.MaxClockDilation, Math.Min(Config.MaxClockDilation, d));
        }

        private void Reconcile(int serverTick, in MotorState server)
        {
            if (Walls != null) Charges.OnServerState(Walls, serverTick, server.ChargeTick);
            if (serverTick > Tick)
            {
                // We are behind the server (start-up or a stall): adopt its state outright.
                _pred = server;
                _prevPred = server;
                Tick = serverTick;
                ClearHistory();
                return;
            }
            int i = serverTick & _histMask;
            if (_histTick[i] != serverTick) return; // too old, or before we started predicting
            if (Protocol.SameMotor(_histState[i], server)) return;

            MotorState before = _pred;
            MotorState s = server;
            _histState[i] = server;
            MotorState prev = server;
            for (int t = serverTick + 1; t <= Tick; t++)
            {
                int k = t & _histMask;
                if (_histTick[k] != t) break;
                prev = s;
                ApplyLanding(ref s, _histMoved[k], _histMoveTo[k], _histKick[k]);
                NetSim.Simulate(Grid, Movement, Config, ref s, _histCmd[k], Spawn, null, null, null, null, Walls != null ? Charges : null);
                if (_histLock[k] != int.MinValue) HandRules.LockGun(ref s.Hand, _histLock[k]);
                _histState[k] = s;
            }
            _pred = s;
            _prevPred = Tick > serverTick ? prev : s;

            Vec3d jump = before.Position - _pred.Position;
            double dist = jump.Length;
            if (dist > 1e-4 || before.Mode != _pred.Mode)
            {
                Corrections++;
                LastCorrectionMeters = dist;
            }
            else SilentResyncs++;

            Vec3d off = CorrectionOffset + jump;
            CorrectionOffset = off.Length > Config.CorrectionSnapDistance ? Vec3d.Zero : off;
        }

        // ================================================================ Ticking

        /// <summary>How many prediction ticks to run this frame (dilated clock, catch-up capped).</summary>
        public int BeginFrame(double dt)
        {
            if (!Joined) return 0;
            _accum += dt * (1.0 + Dilation);
            int n = (int)Math.Floor(_accum / TickSeconds);
            if (n <= 0) return 0;
            // Catch-up is spread over frames; never keep more than a second of debt (e.g. after a long hitch).
            if (n > Config.MaxCatchUpTicksPerFrame) n = Config.MaxCatchUpTicksPerFrame;
            _accum -= n * TickSeconds;
            _accum = Math.Min(_accum, SimTime.TicksPerSecond * TickSeconds);
            return n;
        }

        /// <summary>Decays the visual correction offset. Call once per rendered frame.</summary>
        public void UpdateVisual(double dt)
        {
            if (Config.CorrectionSmoothSeconds <= 0) { CorrectionOffset = Vec3d.Zero; return; }
            double k = Math.Exp(-dt / Config.CorrectionSmoothSeconds);
            Vec3d o = CorrectionOffset * k;
            CorrectionOffset = o.LengthSquared < 1e-10 ? Vec3d.Zero : o;
            if (_serverTimeValid) _serverTime += dt * SimTime.TicksPerSecond;
        }

        /// <summary>Dev only: the next predicted tick carries a warp to the dev warp point (F6 showcase).</summary>
        public bool WarpNext;

        /// <summary>Predicts one tick with this input and sends the newest commands to the server.</summary>
        public void PredictTick(in MoveInput move, float pitchDegrees, bool respawn, List<MoveEvent> events) =>
            PredictTick(move, pitchDegrees, respawn, default, false, default, events);

        /// <summary>
        /// Predicts one tick with movement, hand presses and an optional sculpt confirm, and sends the newest commands.
        /// Returns the sequence number given to the confirm (0 if none).
        /// </summary>
        public byte PredictTick(in MoveInput move, float pitchDegrees, bool respawn, in HandInput hand, bool confirm, in SculptAction sculpt, List<MoveEvent> events)
        {
            if (!Joined) return 0;
            Tick++;
            ExpireHealPress();
            // ISSUE-10: the dead can't sculpt (their body is parked far below the map), and a target the wire can't
            // carry is impossible anyway: drop the confirm instead of letting the send throw.
            if (confirm && (_pred.Dead || !InputCmd.TargetFits(sculpt.Target))) confirm = false;
            byte seq = confirm && Sculpt != null ? Sculpt.NextSeq() : (byte)0;
            double view = RemoteRenderTick;
            int viewTick = (int)Math.Floor(view);
            var cmd = InputCmd.Quantize(new InputCmd
            {
                Tick = Tick,
                Move = move,
                Pitch = pitchDegrees,
                Respawn = respawn,
                Warp = WarpNext,
                ViewTick = viewTick,
                ViewFrac = (byte)Math.Max(0, Math.Min(255, (int)((view - viewTick) * 256))),
                Hand = hand,
                HasSculpt = seq != 0,
                SculptSeq = seq,
                Sculpt = sculpt,
            });

            WarpNext = false;
            if (cmd.Hand.Heal && Heals != null) OnHealPressed(cmd);
            _prevPred = _pred;
            if (Sculpt != null)
            {
                Sculpt.SetRemoteBodies(this);
                Sculpt.BeforeMove(Tick);
            }
            bool moved = false;
            Vec3d moveTo = default, kick = Vec3d.Zero;
            Sculpt?.TakeLandingMove(out moved, out moveTo, out kick);
            ApplyLanding(ref _pred, moved, moveTo, kick);
            if (LocalShots.Count > 1024) LocalShots.Clear(); // nobody is draining (tests)
            if (LocalGrenades.Count > 1024) LocalGrenades.Clear();
            int shotsBefore = LocalShots.Count;
            NetSim.Simulate(Grid, Movement, Config, ref _pred, cmd, Spawn, events, LocalShots, LocalGrenades, null, Walls != null ? Charges : null);
            for (int k = shotsBefore; k < LocalShots.Count; k++)
            {
                WeaponShot sh = LocalShots[k];
                sh.ShooterId = PlayerId;
                LocalShots[k] = sh;
            }
            if (Walls != null)
            {
                // Doors: the server applies a press right after that tick's movement, so do the same here.
                Doors.Expire(Walls, Tick, HealAnswerTimeoutTicks);
                if (cmd.Hand.Door && !_pred.Dead)
                {
                    _doorBodies.Clear();
                    _doorBodies.Add(PlayerMovement.Box(_pred.Position, Movement.HalfWidth, PlayerMovement.HeightOf(_pred, Movement)));
                    Doors.Press(Walls, cmd.Hand.DoorTile, cmd.Hand.DoorOpen, Tick, PlayerId, _doorBodies);
                }
            }
            int lockUntil = Sculpt != null ? Sculpt.AfterMove(Tick, _pred, cmd) : int.MinValue;
            if (lockUntil != int.MinValue) HandRules.LockGun(ref _pred.Hand, lockUntil);

            int i = Tick & _histMask;
            _histTick[i] = Tick;
            _histCmd[i] = cmd;
            _histState[i] = _pred;
            _histLock[i] = lockUntil;
            _histMoved[i] = moved;
            _histMoveTo[i] = moveTo;
            _histKick[i] = kick;

            SendInputs();
            return seq;
        }

        /// <summary>Same as the server: a shove sets the position, a kick adds velocity; the dead are out of the world.</summary>
        private static void ApplyLanding(ref MotorState s, bool moved, Vec3d to, Vec3d kick)
        {
            if (s.Dead) return;
            if (moved) s.Position = to;
            if (kick.LengthSquared > 0) s.Velocity += kick;
        }

        private void SendInputs()
        {
            _sendCmds.Clear();
            int n = Math.Min(Config.InputRedundancy, Protocol.MaxCmdsPerPacket);
            int first = Tick - n + 1;
            for (int t = first; t <= Tick; t++)
            {
                int k = t & _histMask;
                if (_histTick[k] != t) { _sendCmds.Clear(); continue; } // keep only a consecutive run ending at Tick
                _sendCmds.Add(_histCmd[k]);
            }
            if (_sendCmds.Count == 0) return;
            _w.Clear();
            Protocol.Write(_w, new InputsMsg { AckSnapshotTick = LastSnapshotTick, Cmds = _sendCmds });
            _send(_w.ToArray(), false);
        }

        // ================================================================ Healing

        /// <summary>
        /// The bar to show: the server's value minus your heal press it has not seen yet (or has, but whose spend is not
        /// in a snapshot yet), and minus any sculpt spends the sculpt client is still predicting.
        /// </summary>
        public int DisplayBar
        {
            get
            {
                int v = Sculpt != null ? Sculpt.DisplayBar : ServerBar;
                if (_healPending && (!_healAcked || ServerBarTick < _healAckTick)) v += _healRefund - _healPress.Cost;
                return Math.Max(0, Math.Min(SculptConfig.BarMax, v));
            }
        }

        /// <summary>
        /// Your heal as you should see it: the press you just made (predicted, on the same ticks the server will run it)
        /// until the server answers, then the server's. False when you are not healing.
        /// </summary>
        public bool TryGetOwnHeal(out HealJob job, out bool confirmed)
        {
            confirmed = false;
            job = default;
            if (Heals == null) return false;
            if (_healPending && !_healAcked) { job = _healPress; return true; }
            confirmed = Heals.TryGetJob(PlayerId, out job);
            return confirmed;
        }

        /// <summary>Ticks to wait for the server's answer to a heal press before forgetting it.</summary>
        public int HealAnswerTimeoutTicks => Math.Max(30 * SimTime.Per60, (int)Math.Ceiling(RttTicks * 2) + 20 * SimTime.Per60);

        private void OnHealPressed(in InputCmd cmd)
        {
            HealKind kind = cmd.Hand.HealReinforce ? HealKind.Reinforce : HealKind.Heal;
            if (Heals.TryGetJob(PlayerId, out HealJob same) && same.Tile == cmd.Hand.HealTile && same.Kind == kind) return; // the server ignores it too
            _healRefund = Heals.TryGetJob(PlayerId, out HealJob cur) && cur.Tile != cmd.Hand.HealTile ? cur.Cost : 0;
            _healPress = new HealJob
            {
                Player = PlayerId, Tile = cmd.Hand.HealTile, Cell = cmd.Hand.HealCell, Kind = kind, StartTick = cmd.Tick,
                EndTick = cmd.Tick + TileHealing.TicksOf(kind, Heals.Config), Cost = TileHealing.CostOf(kind, Heals.Config),
            };
            _healPending = true;
            _healAcked = false;
        }

        private void OnHealEvent(in HealEvent e)
        {
            Heals.ApplyRemote(e);
            HealEvents.Add(e);
            if (e.Player != PlayerId || !_healPending) return;
            if (e.Tile != _healPress.Tile) return;
            if (e.Type == HealEventType.Began && !_healAcked) { _healAcked = true; _healAckTick = e.Tick; }
            else if (e.Type == HealEventType.Rejected && !_healAcked) _healPending = false;
            else if (e.Type == HealEventType.Completed || e.Type == HealEventType.Cancelled) _healPending = false;
        }

        private void ExpireHealPress()
        {
            if (!_healPending) return;
            if (_healAcked ? ServerBarTick >= _healAckTick : Tick - _healPress.StartTick > HealAnswerTimeoutTicks) _healPending = false;
        }

        // ================================================================ Remote players

        /// <summary>Server tick (fractional) at which remote players are drawn.</summary>
        public double RemoteRenderTick => _serverTime - Config.InterpDelayTicks;

        public int RemoteCount => _remotes.Count;

        public void GetRemoteIds(List<int> into)
        {
            into.Clear();
            foreach (var kv in _remotes) into.Add(kv.Key);
        }

        public bool TryGetRemote(int id, out RemoteView view)
        {
            if (!_remotes.TryGetValue(id, out var t)) { view = default; return false; }
            view = t.Sample(RemoteRenderTick, Config.MaxExtrapolateTicks);
            view.Id = id;
            return true;
        }

        private sealed class RemoteTrack
        {
            private const int N = 32 * SimTime.Per60;
            private const double TeleportSq = 4.0 * 4.0;
            private readonly PlayerSnap[] _snaps = new PlayerSnap[N];
            private readonly int[] _ticks = new int[N];
            private int _count;
            private int _head; // index of newest
            public bool SeenThisSnapshot;

            public void Add(int tick, in PlayerSnap s)
            {
                _head = (_head + 1) % N;
                _snaps[_head] = s;
                _ticks[_head] = tick;
                if (_count < N) _count++;
            }

            public RemoteView Sample(double t, double maxExtrap)
            {
                // Walk newest -> oldest; snapshots are added in increasing tick order.
                int newest = _head;
                if (t >= _ticks[newest])
                {
                    PlayerSnap s = _snaps[newest];
                    double ahead = Math.Min(t - _ticks[newest], maxExtrap);
                    var v = From(s);
                    if (ahead > 0)
                    {
                        Vec3d vel = new Vec3d(s.Velocity.X, 0, s.Velocity.Z); // no vertical extrapolation: never sink into floors
                        v.Position = s.Position + vel * (ahead / SimTime.TicksPerSecond);
                        v.Extrapolated = true;
                    }
                    return v;
                }
                int b = newest;
                for (int k = 1; k < _count; k++)
                {
                    int a = (newest - k + N) % N;
                    if (_ticks[a] <= t)
                    {
                        double span = _ticks[b] - _ticks[a];
                        double f = span > 0 ? (t - _ticks[a]) / span : 1;
                        return Lerp(_snaps[a], _snaps[b], f);
                    }
                    b = a;
                }
                return From(_snaps[b]); // older than anything we have: hold the oldest
            }

            private static RemoteView From(in PlayerSnap s) => new RemoteView
            {
                Team = s.Team,
                Position = s.Position,
                Velocity = s.Velocity,
                Yaw = s.Yaw,
                Pitch = s.Pitch,
                Mode = s.Mode,
                Crouched = s.Crouched,
                Dead = s.Dead,
                Held = s.Held,
                Hand = s.Hand,
            };

            private static RemoteView Lerp(in PlayerSnap a, in PlayerSnap b, double f)
            {
                var v = From(f < 0.5 ? a : b);
                if ((b.Position - a.Position).LengthSquared > TeleportSq) return v; // respawn: never streak across the map
                v.Position = a.Position + (b.Position - a.Position) * f;
                v.Velocity = a.Velocity + (b.Velocity - a.Velocity) * f;
                double dy = Math.IEEERemainder(b.Yaw - a.Yaw, 2 * Math.PI);
                v.Yaw = a.Yaw + dy * f;
                v.Pitch = a.Pitch + (b.Pitch - a.Pitch) * f;
                return v;
            }
        }
    }
}
