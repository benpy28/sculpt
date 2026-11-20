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

        public readonly VoxelGrid Grid;
        public readonly MovementConfig Movement;
        public readonly NetConfig Config;

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
        public int SnapshotsReceived { get; private set; }
        public int DroppedPackets { get; private set; }
        public bool LastInputWasRealOnServer { get; private set; } = true;

        public double TickSeconds => 1.0 / SimTime.TicksPerSecond;
        /// <summary>Fraction of a tick elapsed since the last predicted tick, for drawing. 0..1.</summary>
        public double Alpha => Math.Max(0, Math.Min(1, _accum / TickSeconds));

        private readonly SendFn _send;
        private readonly ByteWriter _w = new ByteWriter(512);
        private readonly List<InputCmd> _sendCmds = new List<InputCmd>(Protocol.MaxCmdsPerPacket);
        private readonly List<PlayerSnap> _snapOthers = new List<PlayerSnap>();
        private readonly List<InputCmd> _readScratch = new List<InputCmd>();

        private readonly InputCmd[] _histCmd;
        private readonly MotorState[] _histState;
        private readonly int[] _histTick;
        private readonly int _histMask;

        private MotorState _pred, _prevPred;
        private double _accum;
        private int _clockHoldUntilServerTick = int.MinValue;

        private double _serverTime;   // estimate of the newest snapshot tick "now", in ticks
        private bool _serverTimeValid;
        private readonly Dictionary<int, RemoteTrack> _remotes = new Dictionary<int, RemoteTrack>();
        private readonly List<int> _removeScratch = new List<int>();

        public NetClient(VoxelGrid grid, MovementConfig movement, NetConfig config, SendFn send)
        {
            Grid = grid;
            Movement = movement;
            Config = config;
            _send = send ?? throw new ArgumentNullException(nameof(send));
            int n = 1;
            while (n < Math.Max(32, config.HistoryTicks)) n <<= 1;
            _histCmd = new InputCmd[n];
            _histState = new MotorState[n];
            _histTick = new int[n];
            _histMask = n - 1;
            ClearHistory();
        }

        private void ClearHistory()
        {
            for (int i = 0; i < _histTick.Length; i++) _histTick[i] = int.MinValue;
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
            if (m.ProtocolVersion != Protocol.Version) throw new NetFormatException($"Protocol {m.ProtocolVersion}, expected {Protocol.Version}.");
            Joined = true;
            PlayerId = m.PlayerId;
            Team = m.Team;
            Spawn = m.Spawn;
            SpawnYaw = m.SpawnYaw;
            // First guess at the lead; the server's feedback corrects it within a round trip.
            Tick = m.ServerTick + Config.TargetLeadTicks + 6;
            _pred = MotorState.At(m.Spawn);
            _prevPred = _pred;
            _accum = 0;
            ClearHistory();
            _remotes.Clear();
            _serverTime = m.ServerTick;
            _serverTimeValid = true;
            LastSnapshotTick = int.MinValue;
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
            RttTicks += ((Tick - m.ServerTick - Math.Max(0, LastServerLead == Protocol.NoLead ? 0 : LastServerLead)) - RttTicks) * 0.1;
            AdjustClock(m.ServerTick, m.SelfLead);
            Reconcile(m.ServerTick, m.Self);
        }

        private void AdjustClock(int serverTick, int lead)
        {
            LastServerLead = lead;
            if (lead == Protocol.NoLead) { Dilation = 0; return; }
            if (serverTick < _clockHoldUntilServerTick) { Dilation = 0; return; }

            int err = lead - Config.TargetLeadTicks;
            if (Math.Abs(err) > Config.ClockSnapTicks)
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
            const int deadband = 2;
            double d = 0;
            if (err < 0) d = -err * Config.ClockGainPerTick;                    // too close: speed up
            else if (err > deadband) d = -(err - deadband) * Config.ClockGainPerTick; // too early: slow down
            Dilation = Math.Max(-Config.MaxClockDilation, Math.Min(Config.MaxClockDilation, d));
        }

        private void Reconcile(int serverTick, in MotorState server)
        {
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
                NetSim.Simulate(Grid, Movement, Config, ref s, _histCmd[k], Spawn, null);
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

        /// <summary>Predicts one tick with this input and sends the newest commands to the server.</summary>
        public void PredictTick(in MoveInput move, float pitchDegrees, bool respawn, List<MoveEvent> events)
        {
            if (!Joined) return;
            Tick++;
            double view = RemoteRenderTick;
            int viewTick = (int)Math.Floor(view);
            var cmd = InputCmd.Quantize(new InputCmd
            {
                Tick = Tick,
                Move = move,
                Pitch = pitchDegrees,
                Respawn = respawn,
                ViewTick = viewTick,
                ViewFrac = (byte)Math.Max(0, Math.Min(255, (int)((view - viewTick) * 256))),
            });

            _prevPred = _pred;
            NetSim.Simulate(Grid, Movement, Config, ref _pred, cmd, Spawn, events);

            int i = Tick & _histMask;
            _histTick[i] = Tick;
            _histCmd[i] = cmd;
            _histState[i] = _pred;

            SendInputs();
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
            private const int N = 32;
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
