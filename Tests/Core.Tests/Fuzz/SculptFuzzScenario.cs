using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// A random but fully seeded sculpt match: random terrain (steps, pits, ramps, anchor pillars),
    /// a handful of players on two teams standing, crouching or hanging in the air, and random sculpt
    /// commands aimed the way a client would aim them (a ray from the eye). Every tick it checks the
    /// invariants that must hold no matter what players do.
    /// </summary>
    internal sealed class SculptFuzzScenario
    {
        public const double B = VoxelGrid.BlockSize;
        public const int RegionMin = 2, RegionMax = 17; // terrain columns x/z in [RegionMin, RegionMax]

        public readonly ulong Seed;
        public readonly Rng R;
        public readonly VoxelGrid Grid;
        public readonly SculptConfig Config = new SculptConfig();
        public readonly SculptSystem Sculpt;
        public readonly List<PlayerBody> Players = new List<PlayerBody>();

        // Knobs.
        public double CrouchChance;
        public double AirChance = 0.3;
        public double ActionChance = 0.2;
        public double TeleportChance = 0.03;
        public bool CheckShoveDistance = true;
        /// <summary>Chance a sculpt is aimed at the ground around some player rather than anywhere.</summary>
        public double AimAtPlayersChance = 0.0;
        /// <summary>Refill bars before each command, so fights never stall on an empty bar.</summary>
        public bool InfiniteBar;

        // Stats, so a test can prove the run actually exercised things.
        public readonly Dictionary<SculptActionType, int> Completed = new Dictionary<SculptActionType, int>();
        public readonly Dictionary<SculptResult, int> Rejected = new Dictionary<SculptResult, int>();
        public int Begun, Cancelled, Shoves, Falls, ReinforcedBegun, EnemyReinforcedBegun;

        /// <summary>Running hash of everything observable, for determinism checks.</summary>
        public ulong Trace = 14695981039346656037UL;

        private readonly Dictionary<int, (int tick, int windup)> _open = new Dictionary<int, (int, int)>();
        private readonly ulong _frozenHash; // anchors and bedrock
        private readonly List<Int3> _scratchChanges = new List<Int3>();
        private readonly List<Int3> _anchors = new List<Int3>();

        // Real movement instead of teleports (players are driven by bots through PlayerMovement, the way
        // LocalPlayer.Tick does it: take the body position, step the motor, write the body back).
        public readonly bool DriveMovement;
        public readonly MovementConfig MoveConfig = new MovementConfig();
        private readonly Dictionary<int, MotorState> _motors = new Dictionary<int, MotorState>();
        private readonly Dictionary<int, InputBot> _bots = new Dictionary<int, InputBot>();
        private readonly Dictionary<int, MotorChecker> _checkers = new Dictionary<int, MotorChecker>();
        private readonly List<MoveEvent> _moveEvents = new List<MoveEvent>();
        public int MoveTicks;

        public SculptFuzzScenario(ulong seed, double crouchChance = 0.0, int playerCount = -1, bool driveMovement = false)
        {
            Seed = seed;
            R = new Rng(seed);
            CrouchChance = driveMovement ? 0.0 : crouchChance;
            DriveMovement = driveMovement;
            if (driveMovement) { AirChance = 0.0; TeleportChance = 0.0; }
            Grid = new VoxelGrid(2, 2, 2, -16);
            Grid.FillBaseGround();
            BuildTerrain();
            Grid.ClearChanges();
            Sculpt = new SculptSystem(Grid, Config);

            int n = playerCount > 0 ? playerCount : R.Int(2, 6);
            for (int i = 1; i <= n; i++)
            {
                var p = new PlayerBody(i, 1 + (i % 2), Vec3d.Zero);
                if (!Place(p)) throw new InvalidOperationException("Could not place player " + i + " (seed " + seed + ")");
                Sculpt.AddPlayer(p);
                Players.Add(p);
                if (driveMovement)
                {
                    var m = MotorState.At(p.Position);
                    PlayerMovement.WriteBody(m, MoveConfig, p);
                    _motors[p.Id] = m;
                    _bots[p.Id] = new InputBot(new Rng(seed * 31 + (ulong)i));
                    _checkers[p.Id] = new MotorChecker(MoveConfig);
                }
            }
            _frozenHash = FrozenHash();
            _fullFrozenHash = FrozenHash(full: true);
            AssertInvariants("setup");
        }

        private string Ctx(string where) => $"seed {Seed}, tick {Sculpt.Tick}, {where}";

        // ---------------- World ----------------

        private void BuildTerrain() => TerrainGen.Build(Grid, R, RegionMin, RegionMax);

        private bool Place(PlayerBody p)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                bool crouch = R.Chance(CrouchChance);
                p.Height = crouch ? 1.1 : 1.8;
                p.EyeHeight = crouch ? 0.95 : 1.62;
                double lo = (RegionMin + 0.3) * B, hi = (RegionMax + 0.7) * B;
                p.Position = new Vec3d(R.Range(lo, hi), 0, R.Range(lo, hi));
                if (!Inspect.DropToGround(Grid, p)) continue;
                if (R.Chance(AirChance)) p.Position = p.Position + new Vec3d(0, R.Range(0.0, 1.4), 0);
                if (!Inspect.IsInsideTerrain(Grid, p)) return true;
            }
            return false;
        }

        // ---------------- Driving ----------------

        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) RunTick();
        }

        public void RunTick()
        {
            Sculpt.ClearEvents();

            // "Movement": occasionally a player relocates (only to a legal spot).
            foreach (var p in Players)
                if (R.Chance(TeleportChance))
                {
                    Vec3d old = p.Position;
                    double h = p.Height, e = p.EyeHeight;
                    if (!Place(p)) { p.Position = old; p.Height = h; p.EyeHeight = e; }
                }

            if (DriveMovement)
                foreach (var p in Players)
                {
                    MotorState m = _motors[p.Id];
                    m.Position = p.Position; // sculpt shoves moved the shared body
                    MotorState prev = m;
                    _moveEvents.Clear();
                    PlayerMovement.Step(Grid, MoveConfig, ref m, _bots[p.Id].Next(m.Tick), _moveEvents);
                    PlayerMovement.WriteBody(m, MoveConfig, p);
                    _motors[p.Id] = m;
                    MoveTicks++;
                    _checkers[p.Id].Check(Grid, prev, m, _moveEvents, Ctx($"movement of player {p.Id}"), worldStatic: false);
                    Trace = Inspect.Mix(Trace, MotorChecker.Hash(m));
                }

            foreach (var p in Players)
                if (Sculpt.PendingOf(p.Id) == null && R.Chance(ActionChance))
                {
                    if (InfiniteBar) Sculpt.BarOf(p.Id).SetValue(SculptConfig.BarMax);
                    TryRandomAction(p);
                }

            var before = new Dictionary<int, Vec3d>();
            foreach (var p in Players) before[p.Id] = p.Position;
            int countBefore = Inspect.CountNonEmpty(Grid, Config.MinCellY - 1, Config.MaxCellY + 2);

            int stepEventsFrom = Sculpt.Events.Count; // Began events from this tick's commands come first
            Sculpt.Step();

            int raises = 0, sinks = 0;
            var shoved = new HashSet<int>();
            var shoveBy = new Dictionary<int, string>();
            var shoveRamp = new HashSet<int>();
            for (int ei = stepEventsFrom; ei < Sculpt.Events.Count; ei++)
            {
                SculptEvent e = Sculpt.Events[ei];
                Assert.That(e.Tick, Is.EqualTo(Sculpt.Tick), Ctx("event tick"));
                switch (e.Type)
                {
                    case SculptEventType.Completed:
                    case SculptEventType.Cancelled:
                    {
                        Assert.That(_open.TryGetValue(e.PlayerId, out var open), Is.True, Ctx($"{e.Type} without Began for player {e.PlayerId}"));
                        Assert.That(e.Tick, Is.EqualTo(open.tick + open.windup), Ctx($"{e.Type} must land exactly at the end of the windup"));
                        _open.Remove(e.PlayerId);
                        if (e.Type == SculptEventType.Completed)
                        {
                            Completed[e.Action.Type] = Completed.TryGetValue(e.Action.Type, out int c) ? c + 1 : 1;
                            if (e.Action.Type == SculptActionType.Raise) raises++;
                            if (e.Action.Type == SculptActionType.Sink) sinks++;
                        }
                        else
                        {
                            Cancelled++;
                            Assert.That(e.Result, Is.Not.EqualTo(SculptResult.Valid), Ctx("cancel carries a reason"));
                        }
                        break;
                    }
                    case SculptEventType.PlayerShoved:
                        Shoves++;
                        shoved.Add(e.PlayerId);
                        if (e.Action.Type == SculptActionType.Move && Grid.Get(e.Check.Destination).IsRamp) shoveRamp.Add(e.PlayerId);
                        shoveBy[e.PlayerId] = $"{e.Action.Type} target {e.Action.Target} dir {e.Action.Direction}, src {e.Check.Source} dst {e.Check.Destination}, src block now {Grid.Get(e.Check.Source)}, dst block now {Grid.Get(e.Check.Destination)}";
                        break;
                    case SculptEventType.BlockFell:
                        Falls++;
                        Assert.That(Grid.Get(e.Cell).IsEmpty, Is.False, Ctx("BlockFell cell holds the block"));
                        break;
                    case SculptEventType.Began:
                        Assert.Fail(Ctx("Began must only come from TryBegin, never from Step"));
                        break;
                }
                Trace = Inspect.Mix(Trace, (ulong)e.Type);
                Trace = Inspect.Mix(Trace, (ulong)e.PlayerId);
                Trace = Inspect.Mix(Trace, (ulong)(uint)(e.Cell.X * 1000003 + e.Cell.Y * 1009 + e.Cell.Z));
            }

            // Players move only when an event says they were shoved, and never further than the rules allow.
            foreach (var p in Players)
            {
                Vec3d d = p.Position - before[p.Id];
                if (!shoved.Contains(p.Id))
                {
                    Assert.That(d.LengthSquared, Is.EqualTo(0.0), Ctx($"player {p.Id} moved without a shove event"));
                    continue;
                }
                if (CheckShoveDistance)
                {
                    // ISSUE-2 (see KnownIssueTests): a pushed ramp can shove a rider on its slope up to
                    // B + box width. Tolerated here until Ben decides the fix, everything else is held to one cell.
                    double maxShove = shoveRamp.Contains(p.Id) ? B + 2 * p.HalfWidth : B;
                    Assert.That(d.HorizontalLength, Is.LessThanOrEqualTo(maxShove + 1e-5), Ctx($"player {p.Id} (h {p.Height}) shoved more than one cell: from {Fmt(before[p.Id])} by {Fmt(d)}; {shoveBy[p.Id]}"));
                    Assert.That(d.Y, Is.GreaterThanOrEqualTo(-1e-5), Ctx($"player {p.Id} shoved downward by {Fmt(d)}; {shoveBy[p.Id]}"));
                    Assert.That(d.Y, Is.LessThanOrEqualTo(B + 1e-5), Ctx($"player {p.Id} (h {p.Height}) lifted more than one cell: from {Fmt(before[p.Id])} by {Fmt(d)}; {shoveBy[p.Id]}"));
                }
            }

            int countAfter = Inspect.CountNonEmpty(Grid, Config.MinCellY - 1, Config.MaxCellY + 2);
            Assert.That(countAfter - countBefore, Is.EqualTo(raises - sinks), Ctx("blocks are conserved except raise (+1) and sink (-1)"));

            AssertInvariants("after step");

            foreach (var p in Players)
            {
                Trace = Inspect.MixDouble(Trace, p.Position.X);
                Trace = Inspect.MixDouble(Trace, p.Position.Y);
                Trace = Inspect.MixDouble(Trace, p.Position.Z);
                Trace = Inspect.Mix(Trace, (ulong)Sculpt.BarOf(p.Id).Value);
            }
        }

        public static string Fmt(Vec3d v) => $"({v.X:R}, {v.Y:R}, {v.Z:R})";

        private static readonly SculptActionType[] Types = { SculptActionType.Move, SculptActionType.Move, SculptActionType.Raise, SculptActionType.Sink, SculptActionType.Shape };

        private void TryRandomAction(PlayerBody p)
        {
            // Aim like a client: random look direction, ray from the eye, take what it hits.
            double yaw = R.Range(0, 2 * Math.PI);
            double pitch = R.Range(-80, 25) * Math.PI / 180;
            var look = new Vec3d(Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Sin(yaw));
            if (Players.Count > 1 && R.Chance(AimAtPlayersChance))
            {
                // Aim at the ground around someone: the block under them, beside them or in their way.
                PlayerBody q = Players[R.Int(0, Players.Count)];
                Vec3d spot = q.Position + new Vec3d(R.Range(-1.3, 1.3), R.Range(-0.7, 0.3), R.Range(-1.3, 1.3));
                look = spot - p.Eye;
            }
            VoxelHit hit = VoxelGeometry.Raycast(Grid, p.Eye, look, Config.RangeMeters + 1.0);
            if (!hit.Hit) return;

            SculptActionType type = R.Pick(Types);
            Dir dir = R.Pick(DirUtil.Horizontal);
            bool reinforce = R.Chance(0.25);
            var a = new SculptAction { Type = type, Target = hit.Cell, Direction = dir, AimPoint = hit.Point, Reinforce = reinforce };

            // Evaluate is pure and agrees with TryBegin.
            _scratchChanges.Clear();
            Grid.DrainChanges(_scratchChanges);
            ulong h0 = StateHash();
            SculptCheck ev = Sculpt.Evaluate(p, a);
            _scratchChanges.Clear();
            Grid.DrainChanges(_scratchChanges);
            Assert.That(_scratchChanges.Count, Is.EqualTo(0), Ctx("Evaluate must not write the grid"));
            Assert.That(StateHash(), Is.EqualTo(h0), Ctx("Evaluate must not change any state"));

            int barBefore = Sculpt.BarOf(p.Id).Value;
            int eventsBefore = Sculpt.Events.Count;
            SculptCheck c = Sculpt.TryBegin(p, a);
            Assert.That(c.Result, Is.EqualTo(ev.Result), Ctx("TryBegin agrees with Evaluate"));

            Trace = Inspect.Mix(Trace, (ulong)c.Result);
            if (!c.IsValid)
            {
                Rejected[c.Result] = Rejected.TryGetValue(c.Result, out int r) ? r + 1 : 1;
                Assert.That(Sculpt.BarOf(p.Id).Value, Is.EqualTo(barBefore), Ctx("rejected action costs nothing"));
                Assert.That(Sculpt.PendingOf(p.Id), Is.Null, Ctx("rejected action starts no windup"));
                Assert.That(Sculpt.Events.Count, Is.EqualTo(eventsBefore), Ctx("rejected action raises no event"));
                return;
            }

            Begun++;
            if (reinforce) ReinforcedBegun++;
            if (c.EnemyReinforced) EnemyReinforcedBegun++;

            // Cost and windup follow the spec exactly.
            int baseCost = Config.CostFor(type);
            int expectCost = baseCost * (c.EnemyReinforced ? 2 : 1) + (reinforce ? Config.CostReinforce : 0);
            Assert.That(c.Cost, Is.EqualTo(expectCost), Ctx("cost formula"));
            int expectWindup = reinforce ? Config.ReinforcedCasterWindupTicks : Config.WindupTicks;
            if (c.EnemyReinforced) expectWindup = Math.Max(expectWindup, Config.EnemyReinforcedWindupTicks);
            Assert.That(c.WindupTicks, Is.EqualTo(expectWindup), Ctx("windup rule"));
            Assert.That(Sculpt.BarOf(p.Id).Value, Is.EqualTo(barBefore - c.Cost), Ctx("bar charged on confirm"));

            // The locked cells belong to this player now.
            Assert.That(Sculpt.IsLocked(c.Source, out int o1) && o1 == p.Id, Is.True, Ctx("source locked"));
            Assert.That(Sculpt.IsLocked(c.Destination, out int o2) && o2 == p.Id, Is.True, Ctx("destination locked"));

            Assert.That(Sculpt.Events.Count, Is.EqualTo(eventsBefore + 1), Ctx("one Began event"));
            Assert.That(Sculpt.Events[eventsBefore].Type, Is.EqualTo(SculptEventType.Began));
            int[] warned = Sculpt.Events[eventsBefore].WarnedPlayers;
            Assert.That(Array.IndexOf(warned, p.Id), Is.EqualTo(-1), Ctx("the sculptor is not warned about their own sculpt"));

            _open[p.Id] = (Sculpt.Tick, c.WindupTicks);
        }

        private ulong StateHash()
        {
            ulong h = 0;
            foreach (var p in Players)
            {
                h = Inspect.Mix(h, (ulong)Sculpt.BarOf(p.Id).Value);
                h = Inspect.Mix(h, Sculpt.PendingOf(p.Id) == null ? 0UL : 1UL);
                h = Inspect.MixDouble(h, p.Position.X);
                h = Inspect.MixDouble(h, p.Position.Y);
                h = Inspect.MixDouble(h, p.Position.Z);
            }
            h = Inspect.Mix(h, (ulong)Sculpt.Events.Count);
            return h;
        }

        /// <summary>
        /// Anchors and the top bedrock layer: cells nothing may ever change. (Deeper bedrock is checked
        /// once after settling: nothing can reach it without passing through this layer first.)
        /// </summary>
        private ulong FrozenHash(bool full = false)
        {
            if (_anchors.Count == 0)
                for (int y = Config.MinCellY; y <= Config.MaxCellY + 1; y++)
                    for (int z = 0; z < Grid.SizeZ; z++)
                        for (int x = 0; x < Grid.SizeX; x++)
                            if (Grid.Get(x, y, z).Kind == BlockKind.Anchor) _anchors.Add(new Int3(x, y, z));
            ulong h = Inspect.HashGrid(Grid, full ? Grid.MinY : Config.MinCellY - 1, Config.MinCellY - 1);
            foreach (var a in _anchors) h = Inspect.Mix(h, Inspect.Pack(Grid.Get(a)));
            return h;
        }

        private ulong _fullFrozenHash;

        // ---------------- Invariants ----------------

        public void AssertInvariants(string where)
        {
            // 1. Nobody is ever inside geometry.
            foreach (var p in Players)
                Assert.That(Inspect.IsInsideTerrain(Grid, p), Is.False, Ctx($"{where}: player {p.Id} is inside terrain at {p.Position} (h {p.Height})"));

            // 2. Bars stay in range.
            foreach (var p in Players)
            {
                int v = Sculpt.BarOf(p.Id).Value;
                Assert.That(v, Is.InRange(0, SculptConfig.BarMax), Ctx($"{where}: bar of {p.Id}"));
            }

            // 3. Anchors and bedrock never change.
            Assert.That(FrozenHash(), Is.EqualTo(_frozenHash), Ctx($"{where}: anchors or bedrock changed"));

            // 4. Nothing above the sculpt range, ramps only on column tops, no ramp on non-solid.
            var lockedExpected = new Dictionary<Int3, int>();
            foreach (var p in Players)
            {
                PendingSculpt pend = Sculpt.PendingOf(p.Id);
                if (pend == null) continue;
                Assert.That(pend.CompleteTick, Is.GreaterThan(Sculpt.Tick), Ctx($"{where}: overdue windup for {p.Id}"));
                foreach (var cell in pend.LockedCells) lockedExpected[cell] = p.Id;
            }

            int lockCount = 0;
            for (int y = Config.MinCellY - 1; y <= Config.MaxCellY + 2; y++)
                for (int z = 0; z < Grid.SizeZ; z++)
                    for (int x = 0; x < Grid.SizeX; x++)
                    {
                        var c = new Int3(x, y, z);
                        Block b = Grid.Get(c);
                        if (y > Config.MaxCellY)
                            Assert.That(b.IsEmpty, Is.True, Ctx($"{where}: block above the sculpt range at {c}"));
                        if (b.IsRamp)
                        {
                            Assert.That(b.Kind, Is.EqualTo(BlockKind.Solid), Ctx($"{where}: ramp must be solid at {c}"));
                            Assert.That(b.RampLow.IsHorizontal(), Is.True, Ctx($"{where}: ramp low side at {c}"));
                            Assert.That(Grid.Get(c + Int3.Up).IsEmpty, Is.True, Ctx($"{where}: ramp under a block at {c}"));
                        }
                        if (b.ReinforceTeam != 0)
                            Assert.That(b.Kind, Is.EqualTo(BlockKind.Solid), Ctx($"{where}: only solids get reinforced ({c})"));

                        // 5. Locks: exactly the cells of running windups, owned by their sculptor.
                        if (Sculpt.IsLocked(c, out int owner))
                        {
                            lockCount++;
                            Assert.That(lockedExpected.TryGetValue(c, out int exp) && exp == owner, Is.True, Ctx($"{where}: stray lock at {c} owned by {owner}"));
                        }
                    }
            Assert.That(lockCount, Is.EqualTo(lockedExpected.Count), Ctx($"{where}: every windup cell is locked"));
        }

        /// <summary>Stop sculpting, remove the players and let gravity finish. Then every block must be supported.</summary>
        public void SettleAndAssertSupport()
        {
            foreach (var p in Players) Sculpt.RemovePlayer(p.Id);
            Players.Clear();
            for (int i = 0; i < 2000 && Sculpt.HasFallingBlocks; i++) Sculpt.Step();
            Assert.That(Sculpt.HasFallingBlocks, Is.False, $"seed {Seed}: gravity never settled");
            Assert.That(FrozenHash(full: true), Is.EqualTo(_fullFrozenHash), $"seed {Seed}: bedrock or anchors changed");

            for (int y = Grid.MinY + 1; y < Grid.MaxYExclusive; y++)
                for (int z = 0; z < Grid.SizeZ; z++)
                    for (int x = 0; x < Grid.SizeX; x++)
                    {
                        Block b = Grid.Get(x, y, z);
                        if (b.IsEmpty || b.Kind == BlockKind.Anchor) continue;
                        Assert.That(Grid.Get(x, y - 1, z).IsEmpty, Is.False, $"seed {Seed}: unsupported block at ({x}, {y}, {z}) after settling");
                    }
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"seed {Seed}: begun {Begun} (reinforced {ReinforcedBegun}, vs enemy reinforcement {EnemyReinforcedBegun}), cancelled {Cancelled}, shoves {Shoves}, falls {Falls}; completed");
            foreach (var kv in Completed) sb.Append($" {kv.Key}={kv.Value}");
            sb.Append("; rejected");
            foreach (var kv in Rejected) sb.Append($" {kv.Key}={kv.Value}");
            return sb.ToString();
        }
    }
}
