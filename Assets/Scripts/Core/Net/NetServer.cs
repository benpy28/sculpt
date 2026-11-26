using System;
using System.Collections.Generic;
using Sculpt.Core.AntiCheat;

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
        // Latency overlay: when the command for a tick first arrived, on the server's clock (same ring as the commands).
        internal readonly double[] ArriveMs;
        internal readonly int[] ArriveTick;
        internal int NewestReceived = int.MinValue;
        internal bool CarryJump, CarryCrouch, CarryRespawn, CarryWarp, CarryToggle, CarrySculpt, CarryReload, CarryPickup, CarryDrop, CarryPunch, CarryHeal, CarryHealReinforce, CarryDoor, CarryDoorOpen, CarrySwap;
        internal byte CarrySwapA, CarrySwapB;
        internal byte CarryWeapon;
        internal int CarryPickId, CarryHealTile, CarryHealCell, CarryDoorTile;
        internal InputCmd CarrySculptCmd;

        // Sculpt confirm bookkeeping (client sequence numbers echoed in world events).
        internal int PendingSeq = -1, BufferedSeq = -1;
        internal int DeferredLock = int.MinValue;
        internal int LastLateSeq = -1;

        // Lead feedback: worst lead in the window.
        internal readonly int[] LeadByTick;
        internal readonly int[] LeadSlotTick;
        internal int LastLeadTick = int.MinValue;

        // Position history for lag compensation (M5+).
        internal readonly Vec3d[] PosHistory;
        internal readonly int[] PosHistoryTick;
        internal readonly double[] HeightHistory;
        internal readonly bool[] DeadHistory;
        /// <summary>Shots fired this tick (resolved after every player has moved).</summary>
        internal readonly List<WeaponShot> Shots = new List<WeaponShot>();
        /// <summary>Grenade throws and blasts from this tick's simulation.</summary>
        internal readonly List<GrenadeEvent> Nades = new List<GrenadeEvent>();
        internal readonly List<PunchSwing> Punches = new List<PunchSwing>();
        internal bool CarryFire, CarryAim;

        /// <summary>Kills and deaths since joining (dev HUD).</summary>
        public int Kills, Deaths;
        /// <summary>Kills and deaths in the current match (only while it is Playing).</summary>
        public int MatchKills, MatchDeaths;
        /// <summary>Respawn on the next tick (a new match).</summary>
        internal bool ForceRespawn;
        /// <summary>Battle royale: give the start-of-match loadout once this respawn has happened.</summary>
        internal bool BrLoadout;
        /// <summary>Battle royale: the gun picked in the locker (None: the server's default).</summary>
        public Item Locker;
        /// <summary>Auto-sort on pickup (Ben 2026-11-26). Set from the player's Prefs message, between ticks (<see cref="NetServer.SetSlotPrefs"/>).</summary>
        public SlotPrefs Prefs = SlotPrefs.Default;
        /// <summary>The last enemy who hurt this player, and when (kill credit for falls).</summary>
        public int LastHurtBy, LastHurtTick;

        // Delta snapshots: the newest snapshot tick this client acked (from its newest input packet) and what we sent it.
        internal int AckTick = int.MinValue;
        internal SnapshotBaselines Sent;
        /// <summary>Snapshot bytes sent to this client, and how many snapshots were deltas or full (stats and tests).</summary>
        public long SnapshotBytes;
        public int DeltaSnapshotsSent, FullSnapshotsSent;

        internal readonly PlayerWatch Watch = new PlayerWatch();

        public int MissedInputs;   // ticks stepped with a repeated command
        public int LateInputs;     // commands that arrived after their tick was simulated
        public int ReceivedPackets;
        public int DroppedPackets; // malformed

        internal ServerPlayer(int ringSize, int leadWindow, int historyTicks)
        {
            Ring = new InputCmd[ringSize];
            Has = new bool[ringSize];
            ArriveMs = new double[ringSize];
            ArriveTick = new int[ringSize];
            for (int i = 0; i < ringSize; i++) ArriveTick[i] = int.MinValue;
            LeadByTick = new int[leadWindow];
            LeadSlotTick = new int[leadWindow];
            for (int i = 0; i < leadWindow; i++) LeadSlotTick[i] = int.MinValue;
            PosHistory = new Vec3d[historyTicks];
            PosHistoryTick = new int[historyTicks];
            HeightHistory = new double[historyTicks];
            DeadHistory = new bool[historyTicks];
            for (int i = 0; i < historyTicks; i++) PosHistoryTick[i] = int.MinValue;
        }
    }

    /// <summary>
    /// Authoritative server simulation of players. Pure Core: the Game layer feeds it packets and sends what it
    /// returns over FishNet (or an in-memory link for offline play and tests).
    /// One tick: consume each player's command for that tick (or repeat the last one), step movement, send snapshots.
    /// </summary>
    public sealed partial class NetServer
    {
        public delegate void SendFn(int connectionId, byte[] data, bool reliable);

        public readonly ITerrain Grid;
        /// <summary>The breakable wall tiles behind <see cref="Grid"/> (null on maps without buildings). The server owns them.</summary>
        public readonly TileWalls Walls;
        private readonly ServerChargeBreaker _charge = new ServerChargeBreaker();
        public readonly MovementConfig Movement;
        public readonly NetConfig Config;
        public int Tick { get; private set; }
        /// <summary>
        /// Optional monotonic clock in milliseconds, set by whoever hosts the server. Only used to tell each client how
        /// long its command waited here (latency overlay); the simulation never reads it. Null: nothing is reported.
        /// </summary>
        public Func<double> Clock;
        public IReadOnlyList<ServerPlayer> Players => _players;
        /// <summary>Items on the ground. The server owns them; every client gets the whole list when it changes.</summary>
        public readonly GroundItems Items = new GroundItems();
        private int _itemsSentVersion;
        /// <summary>Pickups and drops of the last tick (tests and the host's effects): player id, result.</summary>
        public readonly List<KeyValuePair<int, PickupResult>> ItemActions = new List<KeyValuePair<int, PickupResult>>();

        /// <summary>Optional: sculpt system whose bodies are kept in sync (shoves move them between ticks).</summary>
        public SculptSystem Sculpt
        {
            get => _sculpt;
            set
            {
                _sculpt = value;
                if (value == null) return;
                value.SetTick(Tick); // one clock: sculpt tick T is server tick T
                foreach (var p in _players) value.AddPlayer(p.Body);
                _opsSent = value.Pieces.Ops.Count;
            }
        }
        private SculptSystem _sculpt;
        private int _opsSent;
        private int _eventsSeen;
        private readonly List<WorldEvent> _worldEvents = new List<WorldEvent>();
        private readonly List<PieceOp> _opsScratch = new List<PieceOp>();
        private readonly List<WorldEvent> _leftEvents = new List<WorldEvent>();

        private readonly SendFn _send;
        private readonly List<ServerPlayer> _players = new List<ServerPlayer>();
        private readonly Dictionary<int, ServerPlayer> _byConn = new Dictionary<int, ServerPlayer>();
        private readonly ByteWriter _w = new ByteWriter(1024);
        private readonly List<InputCmd> _cmdScratch = new List<InputCmd>(Protocol.MaxCmdsPerPacket);
        private readonly List<PlayerSnap> _others = new List<PlayerSnap>();
        private readonly SnapshotCodec _snapCodec = new SnapshotCodec();
        private int _nextId = 1;
        private readonly int _ringSize;

        public NetServer(ITerrain grid, MovementConfig movement, NetConfig config, SendFn send, int startTick = 1000)
        {
            Grid = grid;
            Walls = TileWalls.Find(grid);
            InitHealing();
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

        public ServerPlayer AddPlayer(int connectionId, int team, Vec3d spawn, double spawnYaw) =>
            AddPlayer(connectionId, team, spawn, spawnYaw, Replay.JoinKind.Human, true);

        private ServerPlayer AddPlayer(int connectionId, int team, Vec3d spawn, double spawnYaw, Replay.JoinKind kind, bool countsAsPlayer)
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
            Stream?.OnJoin(Tick, p, kind, countsAsPlayer);

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
            Send(connectionId, _w.ToArray(), true);
            _matchDirty = true;
            SendMatchTo(p);
            if (Sculpt != null) SendJoinState(p);
            SendTileJoinState(p);
            SendItems(p.ConnectionId);
            return p;
        }

        public void RemovePlayer(int connectionId)
        {
            RemoveSpectator(connectionId);
            if (!_byConn.TryGetValue(connectionId, out var p)) return;
            Stream?.OnLeave(Tick, p.Id);
            _byConn.Remove(connectionId);
            _players.Remove(p);
            _matchDirty = true;
            Healing?.Cancel(p.Id, HealResult.Stopped, null, Tick);
            _dummies.Remove(p.Id);
            _brains.Remove(p.Id);
            _ownBars.Remove(p.Id);
            ForgetInterest(p.Id);
            if (Sculpt != null)
            {
                if (p.PendingSeq >= 0 || Sculpt.PendingOf(p.Id) != null)
                    _leftEvents.Add(new WorldEvent { Type = WorldEventType.Cancelled, PlayerId = p.Id, Seq = (byte)Math.Max(0, p.PendingSeq), Result = SculptResult.NoTarget, Piece = Sculpt.PendingOf(p.Id)?.Check.PieceId ?? 0 });
                Sculpt.RemovePlayer(p.Id);
            }
        }

        /// <summary>Handles one packet from a client. Malformed packets are dropped and counted, never thrown.</summary>
        public void Receive(int connectionId, byte[] data, int offset, int count)
        {
            if (!_byConn.TryGetValue(connectionId, out var p)) return;
            try
            {
                var r = new ByteReader(data, offset, count);
                MsgType type = Protocol.PeekType(data, offset, count);
                if (type == MsgType.Prefs)
                {
                    PrefsMsg pm = Protocol.ReadPrefs(ref r);
                    SetSlotPrefs(p, pm.Slots);
                    if (pm.Locker != Item.None) SetLocker(p, pm.Locker); // None = no pick (keeps whatever the server set); refuses anything the locker doesn't offer
                    return;
                }
                if (type != MsgType.Inputs) { p.DroppedPackets++; return; }
                InputCmd.Sanitized = 0;
                InputsMsg im = Protocol.ReadInputs(ref r, _cmdScratch);
                p.ReceivedPackets++;
                if (_cmdScratch.Count > 0) CheckPacket(p, im, _cmdScratch, InputCmd.Sanitized);
                // The ack rides with the commands; only the newest packet's counts (an older one arriving late would
                // name an older snapshot, still valid but needlessly far back).
                if (_cmdScratch[_cmdScratch.Count - 1].Tick > p.NewestReceived && im.AckSnapshotTick <= Tick) p.AckTick = im.AckSnapshotTick;
                AcceptCommands(p, _cmdScratch);
            }
            catch (NetFormatException)
            {
                p.DroppedPackets++;
                Flag(p, FlagKind.Malformed);
            }
        }

        private void AcceptCommands(ServerPlayer p, List<InputCmd> cmds)
        {
            int newest = cmds[cmds.Count - 1].Tick;
            int lead = newest - Tick;
            // Too far ahead: a broken or cheating clock. Ignore the whole packet.
            if (lead > Config.MaxCommandLeadTicks) { Flag(p, FlagKind.ClockAhead, lead); return; }

            RecordLead(p, lead);
            if (newest > p.NewestReceived) p.NewestReceived = newest;
            double now = Clock != null ? Clock() : 0;

            foreach (InputCmd c in cmds)
            {
                if (c.Tick <= Tick)
                {
                    // Already simulated with a guess. Keep any press so it is not lost, applied on the next tick.
                    int idx = c.Tick & (_ringSize - 1);
                    bool alreadyHad = p.Has[idx] && p.Ring[idx].Tick == c.Tick;
                    if (!alreadyHad && c.Tick <= Tick - Config.InputRedundancy)
                    {
                        // Too late to apply. A sculpt confirm in it is refused out loud, so the client's prediction of it
                        // is rolled back at once (ISSUE-9). Redundant copies are refused once.
                        if (c.HasSculpt && c.SculptSeq != p.LastLateSeq && Sculpt != null)
                        {
                            p.LastLateSeq = c.SculptSeq;
                            _leftEvents.Add(new WorldEvent { Type = WorldEventType.Rejected, PlayerId = p.Id, Seq = c.SculptSeq, Result = SculptResult.TooLate });
                        }
                        continue;
                    }
                    if (!alreadyHad)
                    {
                        p.LateInputs++;
                        if (c.Move.JumpPressed) p.CarryJump = true;
                        if (c.Move.CrouchPressed) p.CarryCrouch = true;
                        if (c.Move.FirePressed) p.CarryFire = true;
                        if (c.Move.AimPressed) p.CarryAim = true;
                        if (c.Respawn) p.CarryRespawn = true;
                        if (c.Warp) p.CarryWarp = true;
                        if (c.Hand.SculptToggle) p.CarryToggle = true;
                        if (c.Hand.Reload) p.CarryReload = true;
                        if (c.Hand.WeaponKey != 0) p.CarryWeapon = c.Hand.WeaponKey;
                        if (c.Hand.Pickup) { p.CarryPickup = true; p.CarryPickId = c.Hand.PickId; }
                        if (c.Hand.Drop) p.CarryDrop = true;
                        if (c.Hand.Door) { p.CarryDoor = true; p.CarryDoorTile = c.Hand.DoorTile; p.CarryDoorOpen = c.Hand.DoorOpen; }
                        if (c.Hand.Swap) { p.CarrySwap = true; p.CarrySwapA = c.Hand.SwapA; p.CarrySwapB = c.Hand.SwapB; }
                        if (c.Hand.Punch) p.CarryPunch = true;
                        if (c.Hand.Heal) { p.CarryHeal = true; p.CarryHealTile = c.Hand.HealTile; p.CarryHealCell = c.Hand.HealCell; p.CarryHealReinforce = c.Hand.HealReinforce; }
                        if (c.HasSculpt) { p.CarrySculpt = true; p.CarrySculptCmd = c; }
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
                if (Clock != null)
                {
                    p.ArriveMs[i] = now;
                    p.ArriveTick[i] = c.Tick;
                }
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

        /// <summary>
        /// Advances the world by one tick and sends world deltas and snapshots. Order inside tick T (the client predicts
        /// the same order): sculpt resolution, buffered confirms, flights and falls; then each player's hand and movement;
        /// then that player's new sculpt confirm.
        /// </summary>
        public void Step(List<MoveEvent> events = null)
        {
            Tick++;
            Stream?.BeginTick(Tick);
            StepMatch();
            _combat.Clear();
            ItemActions.Clear();
            if (Walls != null) Walls.Tick = Tick;
            _worldEvents.Clear();
            _worldEvents.AddRange(_leftEvents);
            _leftEvents.Clear();
            TickOwnBars();
            if (Sculpt != null)
            {
                Sculpt.Step();
                ConvertSculptEvents(true);
                foreach (ServerPlayer p in _players)
                {
                    if (p.BufferedSeq < 0 || Sculpt.HasBufferedConfirm(p.Id)) continue;
                    _worldEvents.Add(new WorldEvent { Type = WorldEventType.Rejected, PlayerId = p.Id, Seq = (byte)p.BufferedSeq, Result = SculptResult.Locked });
                    p.BufferedSeq = -1;
                }
            }

            FeedBots();
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
                if (p.CarryFire) { cmd.Move.FirePressed = true; p.CarryFire = false; }
                if (p.CarryAim) { cmd.Move.AimPressed = true; p.CarryAim = false; }
                if (p.CarryRespawn) { cmd.Respawn = true; p.CarryRespawn = false; }
                if (p.ForceRespawn) { cmd.Respawn = true; cmd.ForcedRespawn = true; p.ForceRespawn = false; }
                if (p.CarryWarp) { cmd.Warp = true; p.CarryWarp = false; }
                if (p.CarryToggle) { cmd.Hand.SculptToggle = true; p.CarryToggle = false; }
                if (p.CarryReload) { cmd.Hand.Reload = true; p.CarryReload = false; }
                if (p.CarryWeapon != 0) { if (cmd.Hand.WeaponKey == 0) cmd.Hand.WeaponKey = p.CarryWeapon; p.CarryWeapon = 0; }
                if (p.CarryPickup) { if (!cmd.Hand.Pickup) { cmd.Hand.Pickup = true; cmd.Hand.PickId = p.CarryPickId; } p.CarryPickup = false; }
                if (p.CarryDrop) { cmd.Hand.Drop = true; p.CarryDrop = false; }
                if (p.CarryDoor) { if (!cmd.Hand.Door) { cmd.Hand.Door = true; cmd.Hand.DoorTile = p.CarryDoorTile; cmd.Hand.DoorOpen = p.CarryDoorOpen; } p.CarryDoor = false; }
                if (p.CarrySwap) { if (!cmd.Hand.Swap) { cmd.Hand.Swap = true; cmd.Hand.SwapA = p.CarrySwapA; cmd.Hand.SwapB = p.CarrySwapB; } p.CarrySwap = false; }
                if (p.CarryPunch) { cmd.Hand.Punch = true; p.CarryPunch = false; }
                if (p.CarryHeal) { if (!cmd.Hand.Heal) { cmd.Hand.Heal = true; cmd.Hand.HealTile = p.CarryHealTile; cmd.Hand.HealCell = p.CarryHealCell; cmd.Hand.HealReinforce = p.CarryHealReinforce; } p.CarryHeal = false; }
                if (p.CarrySculpt)
                {
                    if (!cmd.HasSculpt) { cmd.HasSculpt = true; cmd.SculptSeq = p.CarrySculptCmd.SculptSeq; cmd.Sculpt = p.CarrySculptCmd.Sculpt; }
                    p.CarrySculpt = false;
                }
                CheckCommand(p, ref cmd);
                p.LastCmd = cmd;
                Stream?.AddInput(p.Id, cmd);

                // Sculpt shoves move the shared body between ticks; friction drags and throw kicks add velocity.
                // A dead player's body is parked out of the world, so it is not theirs to copy back.
                if (!p.State.Dead)
                {
                    p.State.Position = p.Body.Position;
                    if (p.Body.Kick.LengthSquared > 0) p.State.Velocity += p.Body.Kick;
                }
                p.Body.Kick = Vec3d.Zero;
                bool wasDead = p.State.Dead;
                p.Shots.Clear();
                p.Nades.Clear();
                p.Punches.Clear();
                _charge.By = p.Id;
                Before before = TakeBefore(p);
                NetSim.Simulate(Grid, Movement, Config, ref p.State, cmd, p.Spawn, events, p.Shots, p.Nades, p.Punches, Walls != null ? _charge : null);
                CheckIntegrity(p, before);
                if (p.BrLoadout && !p.State.Dead) { GiveBrLoadout(p); p.BrLoadout = false; }
                if (!wasDead && p.State.Dead) OnKilled(p, 0, DamageSource.Fall, false);
                if (!p.State.Dead && (cmd.Hand.Pickup || cmd.Hand.Drop)) HandleItems(p, cmd);
                if (cmd.Hand.Heal) HandleHeal(p, cmd);
                if (cmd.Hand.Door && !p.State.Dead) HandleDoor(p, cmd);
                if (p.DeferredLock != int.MinValue)
                {
                    HandRules.LockGun(ref p.State.Hand, p.DeferredLock);
                    p.DeferredLock = int.MinValue;
                }
                PlayerMovement.WriteBody(p.State, Movement, p.Body);
                if (p.State.Dead) p.Body.Position = ParkedBody;
                if (cmd.HasSculpt && Sculpt != null && !p.State.Dead) HandleConfirm(p, cmd);

                int h = Tick % p.PosHistory.Length;
                p.PosHistory[h] = p.State.Position;
                p.PosHistoryTick[h] = Tick;
                p.HeightHistory[h] = PlayerMovement.HeightOf(p.State, Movement);
                p.DeadHistory[h] = p.State.Dead;
            }

            ResolveShots();
            ResolvePunches();
            ResolveGrenades();
            StepHealing();
            if (Br == null) StepPing(); // no anti-hiding ping in a battle royale (the circle does that job)
            StepInterest();

            if (Sculpt != null)
            {
                ConvertSculptEvents(false);
                Sculpt.ClearEvents();
                _eventsSeen = 0;
                SendWorldDelta();
            }

            StepBrEnd();
            SendCombat();
            SendTiles();
            if (Items.Version != _itemsSentVersion) { _itemsSentVersion = Items.Version; SendItems(-1); }
            SendMatch();
            if (Config.SnapshotEveryTicks <= 1 || Tick % Config.SnapshotEveryTicks == 0)
                SendSnapshots();
            if (Stream != null)
            {
                Stream.EndTick(this);
                StepSpectators();
            }
            BrBetweenTicks(); // bots join and leave between ticks, like people (a re-run adds them back at the same place)
        }

        // ================================================================ Items

        /// <summary>
        /// Sets a player's auto-sort (from their Prefs message, or a bot or test). Call between ticks. Recorded in the
        /// replay stream so a re-run picks up into the same slots.
        /// </summary>
        public void SetSlotPrefs(ServerPlayer p, in SlotPrefs prefs)
        {
            if (p == null || p.Prefs.Equals(prefs)) return;
            p.Prefs = prefs;
            Stream?.OnPrefs(Tick, p.Id, prefs);
        }

        /// <summary>
        /// Pickup and drop, applied right after the player's movement on the tick they were pressed. Not predicted: the
        /// client sees its new bag in the next snapshot (the draw time that follows hides most of the round trip).
        /// </summary>
        private void HandleItems(ServerPlayer p, in InputCmd cmd)
        {
            if (cmd.Hand.Pickup)
            {
                Vec3d eye = p.State.Position + new Vec3d(0, PlayerMovement.EyeOf(p.State, Movement), 0);
                if (Br != null && Items.TryGet(cmd.Hand.PickId, out GroundItem chest) && chest.Kind == Item.Chest)
                {
                    ItemActions.Add(new KeyValuePair<int, PickupResult>(p.Id, OpenChest(p, chest, eye)));
                    return;
                }
                PickupResult r = ItemRules.TryPickup(ref p.State, Items, cmd.Hand.PickId, Grid, eye, p.Id, Tick, Movement, p.Prefs);
                ItemActions.Add(new KeyValuePair<int, PickupResult>(p.Id, r));
            }
            else if (cmd.Hand.Drop && ItemRules.TryDrop(ref p.State, Items, Grid, cmd.Move.Yaw, p.Id, Tick, Movement))
                ItemActions.Add(new KeyValuePair<int, PickupResult>(p.Id, PickupResult.Ok));
        }

        private readonly Dictionary<int, GroundItem> _itemsSent = new Dictionary<int, GroundItem>();
        private readonly List<GroundItem> _itemUpserts = new List<GroundItem>();
        private readonly List<int> _itemRemoved = new List<int>();
        private readonly HashSet<int> _itemSeen = new HashSet<int>();

        /// <summary>
        /// The whole ground list to one connection (a joiner), or to everyone (-1) only what changed since the last
        /// broadcast. Broadcasts also go into the spectator stream, whose key frames carry the whole list.
        /// </summary>
        private void SendItems(int connectionId)
        {
            if (connectionId != -1)
            {
                var full = new ByteWriter(16 + Items.Count * 24);
                ItemsProtocol.Write(full, new ItemsMsg { Tick = Tick, Full = true, Items = new List<GroundItem>(Items.All) });
                Send(connectionId, full.ToArray(), true);
                return;
            }
            ItemsProtocol.Diff(_itemsSent, Items.All, _itemUpserts, _itemRemoved, _itemSeen);
            if (_itemUpserts.Count == 0 && _itemRemoved.Count == 0) return;
            var w = new ByteWriter(24 + _itemUpserts.Count * 24 + _itemRemoved.Count * 4);
            ItemsProtocol.Write(w, new ItemsMsg { Tick = Tick, Items = _itemUpserts, Removed = _itemRemoved });
            Broadcast(w.ToArray());
        }

        /// <summary>Puts an item on the map (map setup, tests). It rests on the surface below the point.</summary>
        public int SpawnItem(Item kind, int count, Vec3d at)
        {
            return Items.Add(new GroundItem { Kind = kind, Count = count, Position = ItemRules.Settle(Grid, at), DroppedTick = int.MinValue / 2 }, Movement.Items.MaxGroundItems);
        }

        // ================================================================ Sculpt

        private void HandleConfirm(ServerPlayer p, in InputCmd cmd)
        {
            if (!p.State.Hand.CanConfirmSculpt)
            {
                _worldEvents.Add(new WorldEvent { Type = WorldEventType.Rejected, PlayerId = p.Id, Seq = cmd.SculptSeq, Result = SculptResult.NotInSculptMode });
                return;
            }
            int old = p.BufferedSeq;
            p.BufferedSeq = cmd.SculptSeq;
            SculptCheck c = Sculpt.TryBegin(p.Body, cmd.Sculpt);
            bool buffered = c.Result == SculptResult.Locked || c.Result == SculptResult.Busy;
            if (!c.IsValid && !buffered)
            {
                p.BufferedSeq = old; // an older buffered confirm (if any) keeps waiting
                _worldEvents.Add(new WorldEvent { Type = WorldEventType.Rejected, PlayerId = p.Id, Seq = cmd.SculptSeq, Result = c.Result });
                return;
            }
            // A newer confirm replaces an older buffered one.
            if (old >= 0 && old != cmd.SculptSeq)
                _worldEvents.Add(new WorldEvent { Type = WorldEventType.Rejected, PlayerId = p.Id, Seq = (byte)old, Result = SculptResult.Superseded });
        }

        /// <summary>Lock end for a windup that keeps the gun away: its completion, plus follow-through for a throw.</summary>
        public static int GunLockUntil(SculptConfig cfg, in SculptCheck c, int startTick) =>
            startTick + c.WindupTicks + (c.Type == SculptActionType.Throw ? cfg.ThrowFollowThroughTicks : 0);

        private ServerPlayer FindById(int id)
        {
            foreach (var p in _players) if (p.Id == id) return p;
            return null;
        }

        /// <summary>Turns new sculpt events into wire events, and applies their effects on players (gun lock, disarm).</summary>
        private void ConvertSculptEvents(bool beforeMove)
        {
            var evs = Sculpt.Events;
            int lastImpact = 0, lastThrower = 0; // hits follow their impact event
            for (; _eventsSeen < evs.Count; _eventsSeen++)
            {
                SculptEvent e = evs[_eventsSeen];
                ServerPlayer p = FindById(e.PlayerId);
                switch (e.Type)
                {
                    case SculptEventType.Began:
                    {
                        if (p == null) break;
                        byte seq = (byte)Math.Max(0, p.BufferedSeq);
                        p.PendingSeq = seq;
                        p.BufferedSeq = -1;
                        if (e.Check.LocksGun)
                        {
                            int until = GunLockUntil(Sculpt.Config, e.Check, e.Tick);
                            if (beforeMove) p.DeferredLock = Math.Max(p.DeferredLock, until);
                            else HandRules.LockGun(ref p.State.Hand, until);
                        }
                        _worldEvents.Add(BeganEvent(e, seq, p.Team));
                        break;
                    }
                    case SculptEventType.Completed:
                    case SculptEventType.Cancelled:
                    {
                        byte seq = (byte)Math.Max(0, p?.PendingSeq ?? 0);
                        if (p != null) p.PendingSeq = -1;
                        _worldEvents.Add(new WorldEvent
                        {
                            Type = e.Type == SculptEventType.Completed ? WorldEventType.Completed : WorldEventType.Cancelled,
                            PlayerId = e.PlayerId, Seq = seq, Piece = e.Type == SculptEventType.Completed ? e.Check.PieceId : e.Piece, Result = e.Result,
                        });
                        break;
                    }
                    case SculptEventType.PieceLanded:
                        _worldEvents.Add(new WorldEvent { Type = WorldEventType.Landed, Piece = e.Piece });
                        break;
                    case SculptEventType.PlayerShoved:
                        _worldEvents.Add(new WorldEvent { Type = WorldEventType.Shoved, PlayerId = e.PlayerId, Piece = e.Piece });
                        break;
                    case SculptEventType.PlayerDragged:
                        _worldEvents.Add(new WorldEvent { Type = WorldEventType.Dragged, PlayerId = e.PlayerId, Piece = e.Piece });
                        break;
                    case SculptEventType.Thrown:
                    {
                        ThrownPiece t = null;
                        foreach (var x in Sculpt.Thrown) if (x.Id == e.Value) { t = x; break; }
                        if (t != null) _worldEvents.Add(WorldEvent.FromProjectile(t, Tick));
                        break;
                    }
                    case SculptEventType.ThrowImpact:
                        lastImpact = e.Value;
                        lastThrower = e.PlayerId;
                        _worldEvents.Add(new WorldEvent { Type = WorldEventType.ThrowImpact, PlayerId = e.PlayerId, Piece = e.Piece, ProjectileId = e.Value, Point = e.Point });
                        break;
                    case SculptEventType.ThrowHit:
                        if (p != null && e.Disarm > 0) HandRules.Disarm(ref p.State.Hand, Tick, e.Disarm);
                        if (p != null) ThrowDamage(p, e.Value, e.Point, lastThrower);
                        _worldEvents.Add(new WorldEvent { Type = WorldEventType.ThrowHit, PlayerId = e.PlayerId, Piece = e.Piece, ProjectileId = lastImpact, Value = e.Value, Disarm = e.Disarm, Point = e.Point });
                        break;
                }
            }
        }

        private static WorldEvent BeganEvent(in SculptEvent e, byte seq, int team) => new WorldEvent
        {
            Type = WorldEventType.Began,
            PlayerId = e.PlayerId,
            Team = team,
            Seq = seq,
            Piece = e.Check.PieceId,
            Action = e.Action.Type,
            Direction = e.Action.Type == SculptActionType.Move ? e.Action.Direction : Dir.East,
            Reinforce = e.Action.Reinforce,
            StartTick = e.Tick,
            WindupTicks = e.Check.WindupTicks,
            Cost = e.Check.Cost,
            LocksGun = e.Check.LocksGun,
            EnemyReinforced = e.Check.EnemyReinforced,
            Look = e.Action.Type == SculptActionType.Throw ? InputCmd.QuantizeVec(e.Action.LookDir) : Vec3d.Zero,
            Warned = e.WarnedPlayers,
        };

        private void SendWorldDelta()
        {
            var ops = Sculpt.Pieces.Ops;
            if (_worldEvents.Count == 0 && _opsSent >= ops.Count) return;
            _opsScratch.Clear();
            for (int k = _opsSent; k < ops.Count; k++) _opsScratch.Add(ops[k]);
            _opsSent = ops.Count;
            _w.Clear();
            WorldProtocol.Write(_w, new WorldMsg { Tick = Tick, Ops = _opsScratch, Events = _worldEvents });
            Broadcast(_w.ToArray());
        }

        /// <summary>Late join: the whole op log, windups in progress, pieces in flight.</summary>
        private void SendJoinState(ServerPlayer to) => Send(to.ConnectionId, JoinStateBytes(), true);

        private byte[] JoinStateBytes()
        {
            var events = new List<WorldEvent>();
            foreach (ServerPlayer o in _players)
            {
                PendingSculpt pend = Sculpt.PendingOf(o.Id);
                if (pend == null) continue;
                var e = new SculptEvent { Type = SculptEventType.Began, Tick = pend.StartTick, PlayerId = o.Id, Action = pend.Action, Check = pend.Check, WarnedPlayers = Array.Empty<int>() };
                events.Add(BeganEvent(e, (byte)Math.Max(0, o.PendingSeq), o.Team));
            }
            foreach (ThrownPiece t in Sculpt.Thrown) events.Add(WorldEvent.FromProjectile(t, Tick));
            var ops = new List<PieceOp>(Sculpt.Pieces.Ops.Count);
            for (int k = 0; k < _opsSent && k < Sculpt.Pieces.Ops.Count; k++) ops.Add(Sculpt.Pieces.Ops[k]);
            var w = new ByteWriter(4096);
            WorldProtocol.Write(w, new WorldMsg { Tick = Tick, Join = true, Ops = ops, Events = events });
            return w.ToArray();
        }

        /// <summary>
        /// Latency overlay: tenths of a millisecond between the arrival of this player's command for the current tick
        /// and now (the snapshot leaving), or <see cref="Protocol.NoHold"/> when unknown. Needs <see cref="Clock"/>.
        /// </summary>
        private int HoldOf(ServerPlayer p)
        {
            if (Clock == null || !p.LastCmdWasReal) return Protocol.NoHold;
            int i = Tick & (_ringSize - 1);
            if (p.ArriveTick[i] != Tick) return Protocol.NoHold; // a carried or repeated command: no arrival time for this tick
            double held = Clock() - p.ArriveMs[i];
            if (!(held >= 0)) return Protocol.NoHold;
            return (int)Math.Min(Protocol.NoHold - 1, Math.Round(held * 10.0));
        }

        private void SendSnapshots()
        {
            // What everyone sees of everyone else is the same for every receiver: build and quantize it once.
            _others.Clear();
            foreach (ServerPlayer o in _players)
            {
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
                    Dead = o.State.Dead,
                    Held = o.State.Hand.HeldItem,
                    Hand = o.State.Hand.Mode,
                });
            }
            _snapCodec.SetEveryone(Tick, _others);
            foreach (ServerPlayer to in _players)
            {
                if (IsBotConnection(to.ConnectionId)) continue; // nothing is ever sent to a bot
                _w.Clear();
                if (to.Sent == null || to.Sent.Size != Config.SnapshotBaselineTicks) to.Sent = new SnapshotBaselines(Math.Max(2, Config.SnapshotBaselineTicks));
                bool delta = _snapCodec.WriteForReceiver(_w, new SnapshotMsg
                {
                    ServerTick = Tick,
                    HasSelf = true,
                    SelfInputWasReal = to.LastCmdWasReal,
                    SelfLead = WorstLead(to),
                    Self = to.State,
                    SelfBar = BarOf(to.Id).Value,
                    SelfHoldTenthMs = HoldOf(to),
                }, to.Id, to.Sent, Config.DeltaSnapshots ? to.AckTick : -1, MaskFor(to));
                if (delta) to.DeltaSnapshotsSent++; else to.FullSnapshotsSent++;
                to.SnapshotBytes += _w.Length;
                Send(to.ConnectionId, _w.ToArray(), false);
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
