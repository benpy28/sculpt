using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Server-side record of one connected player.</summary>
    public sealed class ServerPlayer
    {
        public int Id;
        public int ConnectionId;
        public int Team;
        public Vec3d Spawn;
        public double SpawnYaw;
        public MotorState State;
        public PlayerBody Body;
        public InputCmd LastCmd;
        public bool LastCmdWasReal;

        // Input buffer: ring indexed by tick.
        internal readonly InputCmd[] Ring;
        internal readonly bool[] Has;
        internal int NewestReceived = int.MinValue;
        internal bool CarryJump, CarryCrouch, CarryRespawn;

        // Lead feedback: worst lead in the window.
        internal readonly int[] LeadByTick;
        internal readonly int[] LeadSlotTick;
        internal int LastLeadTick = int.MinValue;

        // Position history for lag compensation (M5+).
        internal readonly Vec3d[] PosHistory;
        internal readonly int[] PosHistoryTick;

        public int MissedInputs;   // ticks stepped with a repeated command
        public int LateInputs;     // commands that arrived after their tick was simulated
        public int ReceivedPackets;
        public int DroppedPackets; // malformed

        internal ServerPlayer(int ringSize, int leadWindow, int historyTicks)
        {
            Ring = new InputCmd[ringSize];
            Has = new bool[ringSize];
            LeadByTick = new int[leadWindow];
            LeadSlotTick = new int[leadWindow];
            for (int i = 0; i < leadWindow; i++) LeadSlotTick[i] = int.MinValue;
            PosHistory = new Vec3d[historyTicks];
            PosHistoryTick = new int[historyTicks];
            for (int i = 0; i < historyTicks; i++) PosHistoryTick[i] = int.MinValue;
        }
    }

    /// <summary>
    /// Authoritative server simulation of players. Pure Core: the Game layer feeds it packets and sends what it
    /// returns over FishNet (or an in-memory link for offline play and tests).
    /// One tick: consume each player's command for that tick (or repeat the last one), step movement, send snapshots.
    /// </summary>
    public sealed class NetServer
    {
        public delegate void SendFn(int connectionId, byte[] data, bool reliable);

        public readonly ITerrain Grid;
        public readonly MovementConfig Movement;
        public readonly NetConfig Config;
        public int Tick { get; private set; }
        public IReadOnlyList<ServerPlayer> Players => _players;

        /// <summary>Optional: sculpt system whose bodies are kept in sync (shoves move them between ticks).</summary>
        public SculptSystem Sculpt;

        private readonly SendFn _send;
        private readonly List<ServerPlayer> _players = new List<ServerPlayer>();
        private readonly Dictionary<int, ServerPlayer> _byConn = new Dictionary<int, ServerPlayer>();
        private readonly ByteWriter _w = new ByteWriter(1024);
        private readonly List<InputCmd> _cmdScratch = new List<InputCmd>(Protocol.MaxCmdsPerPacket);
        private readonly List<PlayerSnap> _others = new List<PlayerSnap>();
        private int _nextId = 1;
        private readonly int _ringSize;

        public NetServer(ITerrain grid, MovementConfig movement, NetConfig config, SendFn send, int startTick = 1000)
        {
            Grid = grid;
            Movement = movement;
            Config = config;
            _send = send ?? throw new ArgumentNullException(nameof(send));
            Tick = startTick;
            _ringSize = NextPow2(Math.Max(config.MaxCommandLeadTicks + config.InputRedundancy + 4, 16));
        }

        private static int NextPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        public ServerPlayer FindByConnection(int connectionId) => _byConn.TryGetValue(connectionId, out var p) ? p : null;

        public ServerPlayer AddPlayer(int connectionId, int team, Vec3d spawn, double spawnYaw)
        {
            if (_byConn.ContainsKey(connectionId)) throw new InvalidOperationException("Connection already has a player.");
            var p = new ServerPlayer(_ringSize, Config.LeadWindowTicks, Config.ServerHistoryTicks)
            {
                Id = _nextId++,
                ConnectionId = connectionId,
                Team = team,
                Spawn = spawn,
                SpawnYaw = spawnYaw,
                State = MotorState.At(spawn),
            };
            p.LastCmd = new InputCmd { Tick = Tick, Move = new MoveInput { Yaw = InputCmd.QuantizeYaw(spawnYaw) } };
            p.Body = new PlayerBody(p.Id, team, spawn);
            PlayerMovement.WriteBody(p.State, Movement, p.Body);
            _players.Add(p);
            _byConn[connectionId] = p;
            Sculpt?.AddPlayer(p.Body);

            _w.Clear();
            Protocol.Write(_w, new WelcomeMsg
            {
                ProtocolVersion = Protocol.Version,
                PlayerId = p.Id,
                Team = team,
                ServerTick = Tick,
                Spawn = spawn,
                SpawnYaw = spawnYaw,
            });
            _send(connectionId, _w.ToArray(), true);
            return p;
        }

        public void RemovePlayer(int connectionId)
        {
            if (!_byConn.TryGetValue(connectionId, out var p)) return;
            _byConn.Remove(connectionId);
            _players.Remove(p);
            Sculpt?.RemovePlayer(p.Id);
        }

        /// <summary>Handles one packet from a client. Malformed packets are dropped and counted, never thrown.</summary>
        public void Receive(int connectionId, byte[] data, int offset, int count)
        {
            if (!_byConn.TryGetValue(connectionId, out var p)) return;
            try
            {
                var r = new ByteReader(data, offset, count);
                if (Protocol.PeekType(data, offset, count) != MsgType.Inputs) { p.DroppedPackets++; return; }
                Protocol.ReadInputs(ref r, _cmdScratch);
                p.ReceivedPackets++;
                AcceptCommands(p, _cmdScratch);
            }
            catch (NetFormatException)
            {
                p.DroppedPackets++;
            }
        }

        private void AcceptCommands(ServerPlayer p, List<InputCmd> cmds)
        {
            int newest = cmds[cmds.Count - 1].Tick;
            int lead = newest - Tick;
            // Too far ahead: a broken or cheating clock. Ignore the whole packet.
            if (lead > Config.MaxCommandLeadTicks) return;

            RecordLead(p, lead);
            if (newest > p.NewestReceived) p.NewestReceived = newest;

            foreach (InputCmd c in cmds)
            {
                if (c.Tick <= Tick)
                {
                    // Already simulated with a guess. Keep any press so it is not lost, applied on the next tick.
                    int idx = c.Tick & (_ringSize - 1);
                    bool alreadyHad = p.Has[idx] && p.Ring[idx].Tick == c.Tick;
                    if (!alreadyHad && c.Tick > Tick - Config.InputRedundancy)
                    {
                        p.LateInputs++;
                        if (c.Move.JumpPressed) p.CarryJump = true;
                        if (c.Move.CrouchPressed) p.CarryCrouch = true;
                        if (c.Respawn) p.CarryRespawn = true;
                        // Mark so redundant copies are not counted again.
                        p.Ring[idx] = c;
                        p.Has[idx] = true;
                    }
                    continue;
                }
                int i = c.Tick & (_ringSize - 1);
                if (p.Has[i] && p.Ring[i].Tick == c.Tick) continue; // redundant copy
                p.Ring[i] = c;
                p.Has[i] = true;
            }
        }

        private void RecordLead(ServerPlayer p, int lead)
        {
            int slot = Tick % p.LeadByTick.Length;
            if (slot < 0) slot += p.LeadByTick.Length;
            if (p.LeadSlotTick[slot] != Tick)
            {
                p.LeadByTick[slot] = lead;
                p.LeadSlotTick[slot] = Tick;
                p.LastLeadTick = Tick;
            }
            else if (lead < p.LeadByTick[slot]) p.LeadByTick[slot] = lead;
        }

        private int WorstLead(ServerPlayer p)
        {
            // Slots not written within the window hold stale values; only count ticks in the window.
            int worst = int.MaxValue;
            bool any = false;
            int n = p.LeadByTick.Length;
            for (int k = 0; k < n; k++)
            {
                int t = Tick - k;
                int slot = t % n;
                if (slot < 0) slot += n;
                if (p.LeadSlotTick[slot] != t) continue; // no packet arrived during that tick
                int v = p.LeadByTick[slot];
                if (v == Protocol.NoLead) continue;
                any = true;
                if (v < worst) worst = v;
            }
            return any ? worst : Protocol.NoLead;
        }

        /// <summary>Advances the world by one tick and sends snapshots.</summary>
        public void Step(List<MoveEvent> events = null)
        {
            Tick++;
            foreach (ServerPlayer p in _players)
            {
                // Slots for ticks in the lead window must be cleared lazily; a slot is valid only if its tick matches.
                int i = Tick & (_ringSize - 1);
                InputCmd cmd;
                if (p.Has[i] && p.Ring[i].Tick == Tick)
                {
                    cmd = p.Ring[i];
                    p.LastCmdWasReal = true;
                }
                else
                {
                    cmd = p.LastCmd.AsRepeat(Tick);
                    p.LastCmdWasReal = false;
                    p.MissedInputs++;
                }
                if (p.CarryJump) { cmd.Move.JumpPressed = true; p.CarryJump = false; }
                if (p.CarryCrouch) { cmd.Move.CrouchPressed = true; p.CarryCrouch = false; }
                if (p.CarryRespawn) { cmd.Respawn = true; p.CarryRespawn = false; }
                p.LastCmd = cmd;

                // Sculpt shoves move the shared body between ticks; friction drags and throw kicks add velocity.
                p.State.Position = p.Body.Position;
                if (p.Body.Kick.LengthSquared > 0)
                {
                    p.State.Velocity += p.Body.Kick;
                    p.Body.Kick = Vec3d.Zero;
                }
                NetSim.Simulate(Grid, Movement, Config, ref p.State, cmd, p.Spawn, events);
                PlayerMovement.WriteBody(p.State, Movement, p.Body);

                int h = Tick % p.PosHistory.Length;
                p.PosHistory[h] = p.State.Position;
                p.PosHistoryTick[h] = Tick;
            }

            if (Config.SnapshotEveryTicks <= 1 || Tick % Config.SnapshotEveryTicks == 0)
                SendSnapshots();
        }

        private void SendSnapshots()
        {
            foreach (ServerPlayer to in _players)
            {
                _others.Clear();
                foreach (ServerPlayer o in _players)
                {
                    if (o == to) continue;
                    _others.Add(new PlayerSnap
                    {
                        Id = o.Id,
                        Team = o.Team,
                        Position = o.State.Position,
                        Velocity = o.State.Velocity,
                        Yaw = (float)o.LastCmd.Move.Yaw,
                        Pitch = o.LastCmd.Pitch,
                        Mode = o.State.Mode,
                        Crouched = o.State.Crouched,
                    });
                }
                _w.Clear();
                Protocol.Write(_w, new SnapshotMsg
                {
                    ServerTick = Tick,
                    HasSelf = true,
                    SelfInputWasReal = to.LastCmdWasReal,
                    SelfLead = WorstLead(to),
                    Self = to.State,
                    Others = _others,
                });
                _send(to.ConnectionId, _w.ToArray(), false);
            }
        }

        /// <summary>Where a player's feet were at a past tick (lag compensation hook, M5+).</summary>
        public bool TryGetHistoricalPosition(int playerId, int tick, out Vec3d feet)
        {
            foreach (ServerPlayer p in _players)
            {
                if (p.Id != playerId) continue;
                int h = ((tick % p.PosHistory.Length) + p.PosHistory.Length) % p.PosHistory.Length;
                if (p.PosHistoryTick[h] == tick) { feet = p.PosHistory[h]; return true; }
                break;
            }
            feet = default;
            return false;
        }
    }
}
