using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Terrain v2 hardening: seeded sculpt fights on generated arenas, checked every tick against rules that do not
    /// depend on how pieces are cut (so they survive tuning): nobody is ever inside terrain, solid volume only changes
    /// by raise, sink and throw (falls and pushes conserve it), bedrock and anchors never change, the piece table always
    /// matches the grid, locks are exactly the running windups, Evaluate is pure, shoves and lifts stay within one tile,
    /// throws only hurt enemies within the damage and disarm caps, windups finish on time, and after settling every
    /// piece obeys the one-third support rule. Optionally the players really move (bots through PlayerMovement,
    /// with friction and throw kicks), the way the server ticks them.
    /// </summary>
    [Category("Fuzz")]
    public class PieceInvariantFuzzTests
    {
        private static IEnumerable<int> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 6;
            for (int i = 0; i < n; i++) yield return 31 + i * 104729;
        }

        internal sealed class Scenario
        {
            public readonly int Seed;
            public readonly Rng R;
            public readonly SubGrid Grid;
            public readonly SculptConfig Config = new SculptConfig();
            public readonly MovementConfig MoveConfig = new MovementConfig();
            public readonly SculptSystem Sculpt;
            public readonly List<PlayerBody> Players = new List<PlayerBody>();

            public bool Move;              // drive players with PlayerMovement
            /// <summary>Penetration tolerated (metres). Zero: ISSUE-7 (crouch at rest inside rock) is fixed.</summary>
            public const double Slack = 0;
            public bool InfiniteBar;
            public double ActionChance = 0.15;
            public double AimAtPlayersChance = 0.5;

            private SubGrid _mirror;       // last tick's grid, kept current from the change log
            private long _solid;           // solid sub-cells in the grid
            private readonly Dictionary<int, MotorState> _motors = new Dictionary<int, MotorState>();
            private readonly Dictionary<int, InputBot> _bots = new Dictionary<int, InputBot>();
            private readonly Dictionary<int, MotorChecker> _checkers = new Dictionary<int, MotorChecker>();
            private readonly Dictionary<int, (int tick, int windup)> _open = new Dictionary<int, (int, int)>();
            private readonly List<Int3> _changes = new List<Int3>();
            private readonly List<MoveEvent> _moveEvents = new List<MoveEvent>();
            public ulong Trace = 1469598103934665603UL;

            public int RaiseHoleFills;
            public int Begun, Completed, Cancelled, Throws, ThrowHits, Falls, Shoves, Drags, Raises, Sinks, Moves, MoveTicks;
            public readonly Dictionary<SculptResult, int> Rejected = new Dictionary<SculptResult, int>();

            private string Ctx(string what) => $"seed {Seed}, tick {Sculpt.Tick}: {what}";

            public Scenario(int seed, int players, bool move, double crouchChance = 0.3)
            {
                Seed = seed;
                R = new Rng((ulong)seed * 2654435761UL + 7);
                Move = move;
                Grid = MapGen.Generate(seed, out _, 32);
                Grid.ClearChanges();
                Sculpt = new SculptSystem(Grid, Config);
                _mirror = Grid.Clone();
                _solid = CountSolid(Grid);

                double size = Grid.SizeX * SubGrid.SubSize;
                for (int id = 1; Players.Count < players && id < players * 20; id++)
                {
                    var p = new PlayerBody(Players.Count + 1, Players.Count % 2 + 1, new Vec3d(size * 0.5 + R.Range(-7, 7), 0, size * 0.5 + R.Range(-7, 7)));
                    if (!move && R.Chance(crouchChance)) { p.Height = 1.1; p.EyeHeight = 0.95; }
                    if (!Inspect.DropToGround(Grid, p, -4, 9)) continue;
                    if (!move && R.Chance(0.2)) p.Position += new Vec3d(0, R.Range(0, 1.3), 0);
                    if (Grid.BoxOverlapsSolid(p.Bounds)) continue;
                    Sculpt.AddPlayer(p);
                    Players.Add(p);
                    if (move)
                    {
                        var m = MotorState.At(p.Position);
                        PlayerMovement.WriteBody(m, MoveConfig, p);
                        _motors[p.Id] = m;
                        _bots[p.Id] = new InputBot(new Rng((ulong)seed * 31 + (ulong)p.Id));
                        _checkers[p.Id] = new MotorChecker(MoveConfig) { ContactSlack = Slack };
                    }
                }
                Assert.That(Players.Count, Is.EqualTo(players), "setup placed every player");
            }

            public static long CountSolid(SubGrid g)
            {
                long n = 0;
                for (int y = g.MinY; y < g.MaxYExclusive; y++)
                    for (int z = 0; z < g.SizeZ; z++)
                        for (int x = 0; x < g.SizeX; x++)
                            if (g.Get(x, y, z) != SubId.Empty) n++;
                return n;
            }

            public void Run(int ticks)
            {
                for (int i = 0; i < ticks; i++) RunTick();
            }

            public bool CheckMirrorEveryTick;
            private SubGrid _mirrorAtTickStart;

            public void RunTick()
            {
                Sculpt.ClearEvents();
                if (CheckMirrorEveryTick) { AssertMirror("start of tick"); AssertTableMatchesGrid(); }

                if (Move)
                    foreach (var p in Players)
                    {
                        MotorState m = _motors[p.Id];
                        m.Position = p.Position;           // shoves moved the body
                        m.Velocity += p.Kick;              // friction and throw kicks, as NetServer applies them
                        p.Kick = Vec3d.Zero;
                        MotorState prev = m;
                        _moveEvents.Clear();
                        MoveInput mi = _bots[p.Id].Next(m.Tick);
                        PlayerMovement.Step(Grid, MoveConfig, ref m, mi, _moveEvents);
                        if (MotorChecker.InsideWithSlack(Grid, MoveConfig, m, Slack))
                            Assert.Fail(Ctx($"movement put player {p.Id} inside terrain. Before: pos {Inspect.Fmt(prev.Position)} vel {Inspect.Fmt(prev.Velocity)} mode {prev.Mode} crouched {prev.Crouched} sprint {prev.Sprinting} tick {prev.Tick} inside-before {MotorChecker.Inside(Grid, MoveConfig, prev)}; " +
                                $"input x {mi.MoveX:R} z {mi.MoveZ:R} yaw {mi.Yaw:R} sprint {mi.SprintHeld} crouch {mi.CrouchHeld}/{mi.CrouchPressed} jump {mi.JumpPressed}; after pos {Inspect.Fmt(m.Position)} mode {m.Mode} crouched {m.Crouched}"));
                        PlayerMovement.WriteBody(m, MoveConfig, p);
                        _motors[p.Id] = m;
                        MoveTicks++;
                        _checkers[p.Id].Check(Grid, prev, m, _moveEvents, Ctx($"movement of player {p.Id}"), worldStatic: false);
                        Trace = Inspect.Mix(Trace, MotorChecker.Hash(m));
                    }
                else
                    foreach (var p in Players) p.Kick = Vec3d.Zero;

                foreach (var p in Players)
                    if (R.Chance(ActionChance)) TryRandomAction(p);

                var before = new Dictionary<int, Vec3d>();
                foreach (var p in Players) before[p.Id] = p.Position;
                int stepFrom = Sculpt.Events.Count;
                int opsBefore = Sculpt.Pieces.Ops.Count;
                if (CheckMirrorEveryTick) _mirrorAtTickStart = _mirror.Clone();
                int ticksBefore = Sculpt.Tick;

                Sculpt.Step();
                Assert.That(Sculpt.Tick, Is.EqualTo(ticksBefore + 1));

                // ---- Events ----
                long gainMax = 0, lossMax = 0, exactLoss = 0;
                var shoved = new HashSet<int>();
                var shoveWhy = new Dictionary<int, string>();
                var projectilesPerOwner = new Dictionary<int, int>();
                for (int i = stepFrom; i < Sculpt.Events.Count; i++)
                {
                    SculptEvent e = Sculpt.Events[i];
                    Assert.That(e.Tick, Is.EqualTo(Sculpt.Tick), Ctx("event tick"));
                    Trace = Inspect.Mix(Trace, ((ulong)e.Type << 32) | ((ulong)(uint)e.PlayerId << 16) | e.Piece);
                    switch (e.Type)
                    {
                        case SculptEventType.Began:
                            // A buffered confirm retried inside Step.
                            CheckBegan(e);
                            break;
                        case SculptEventType.Completed:
                        case SculptEventType.Cancelled:
                        {
                            Assert.That(_open.TryGetValue(e.PlayerId, out var o), Is.True, Ctx($"{e.Type} without Began (player {e.PlayerId})"));
                            Assert.That(e.Tick, Is.EqualTo(o.tick + o.windup), Ctx($"{e.Type} exactly at the end of the windup"));
                            _open.Remove(e.PlayerId);
                            if (e.Type == SculptEventType.Cancelled) { Cancelled++; Assert.That(e.Result, Is.Not.EqualTo(SculptResult.Valid)); break; }
                            Completed++;
                            int n = e.Check.Size;
                            int level = Columns(e.Check.Cells) * SubGrid.TileSubs; // every column moves one level
                            switch (e.Action.Type)
                            {
                                case SculptActionType.Raise:
                                {
                                    // Exact gain from the rule: the piece's new cells, plus the swept volume it leaves
                                    // below them filled with rock, counted where they were empty. Usually one level per
                                    // column; more when the piece has gaps inside it (they are left behind as rock).
                                    Raises++;
                                    var newSet = new HashSet<Int3>();
                                    foreach (var x in e.Check.Cells) newSet.Add(x + e.Check.Delta);
                                    var grown = new HashSet<Int3>();
                                    foreach (var x in newSet) if (_mirror.Get(x) == SubId.Empty) grown.Add(x);
                                    foreach (var x in e.Check.Cells)
                                        for (int k = 0; k < SubGrid.TileSubs; k++)
                                            if (!newSet.Contains(x + Int3.Up * k) && _mirror.Get(x + Int3.Up * k) == SubId.Empty) grown.Add(x + Int3.Up * k);
                                    gainMax += grown.Count;
                                    if (grown.Count > level) RaiseHoleFills += grown.Count - level;
                                    break;
                                }
                                case SculptActionType.Sink:
                                {
                                    // It sinks into rock (or air): the swept volume it leaves above its new cells empties.
                                    Sinks++;
                                    var newSet = new HashSet<Int3>();
                                    foreach (var x in e.Check.Cells) newSet.Add(x + e.Check.Delta);
                                    var emptied = new HashSet<Int3>();
                                    foreach (var x in e.Check.Cells)
                                        for (int k = 0; k < SubGrid.TileSubs; k++)
                                            if (!newSet.Contains(x + Int3.Down * k) && _mirror.Get(x + Int3.Down * k) != SubId.Empty) emptied.Add(x + Int3.Down * k);
                                    lossMax += Math.Max(level, emptied.Count);
                                    break;
                                }
                                case SculptActionType.Throw: exactLoss += n; break;                  // torn out of the world
                                case SculptActionType.Move: Moves++; break;                          // conserves volume
                            }
                            break;
                        }
                        case SculptEventType.Thrown:
                            Throws++;
                            break;
                        case SculptEventType.ThrowHit:
                        {
                            ThrowHits++;
                            PlayerBody victim = Players.Find(q => q.Id == e.PlayerId);
                            PlayerBody thrower = null;
                            Assert.That(victim, Is.Not.Null);
                            Assert.That(e.Value, Is.InRange(0, (int)Math.Round(Config.ThrowDamageCap)), Ctx("throw damage within the cap"));
                            Assert.That(e.Disarm, Is.InRange(0, Config.DisarmMaxTicks), Ctx("disarm within the cap"));
                            // The ThrowImpact event just before names the thrower.
                            for (int k = i - 1; k >= stepFrom; k--)
                                if (Sculpt.Events[k].Type == SculptEventType.ThrowImpact && Sculpt.Events[k].Piece == e.Piece) { thrower = Players.Find(q => q.Id == Sculpt.Events[k].PlayerId); break; }
                            Assert.That(thrower, Is.Not.Null, Ctx("hit has an impact"));
                            Assert.That(victim.Team, Is.Not.EqualTo(thrower.Team), Ctx("no friendly fire, never the thrower"));
                            break;
                        }
                        case SculptEventType.PieceFell: Falls++; break;
                        case SculptEventType.PlayerShoved:
                            Shoves++;
                            shoved.Add(e.PlayerId);
                            shoveWhy[e.PlayerId] = (shoveWhy.TryGetValue(e.PlayerId, out string w0) ? w0 + " + " : "") + $"{e.Action.Type} dir {e.Action.Direction} by player {e.Check.PieceId} piece size {e.Check.Size} delta {e.Check.Delta}";
                            break;
                        case SculptEventType.PlayerDragged: Drags++; break;
                    }
                }
                foreach (var t in Sculpt.Thrown)
                {
                    projectilesPerOwner[t.Owner] = projectilesPerOwner.TryGetValue(t.Owner, out int c) ? c + 1 : 1;
                    Assert.That(projectilesPerOwner[t.Owner], Is.LessThanOrEqualTo(1), Ctx("one throw in flight per player"));
                }

                // ---- Players ----
                foreach (var p in Players)
                {
                    bool inside = Grid.BoxOverlapsSolid(p.Bounds);
                    Assert.That(inside, Is.False, Ctx($"player {p.Id} (h {p.Height}) inside terrain at {p.Position}"));
                    Vec3d d = p.Position - before[p.Id];
                    if (!shoved.Contains(p.Id))
                    {
                        Assert.That(d.LengthSquared, Is.EqualTo(0.0), Ctx($"player {p.Id} moved by the sculpt step without a shove event"));
                        continue;
                    }
                    Assert.That(d.HorizontalLength, Is.LessThanOrEqualTo(Config.MaxShove + 1e-6), Ctx($"player {p.Id} shoved {d.HorizontalLength:0.###} m, more than one tile"));
                    Assert.That(d.Y, Is.GreaterThanOrEqualTo(-1e-6), Ctx($"player {p.Id} (h {p.Height}) pushed down by {d.Y:R} m from {Inspect.Fmt(before[p.Id])} (horizontal {d.HorizontalLength:0.###}); {shoveWhy[p.Id]}"));
                    Assert.That(d.Y, Is.LessThanOrEqualTo(Config.MaxLift + 1e-6), Ctx($"player {p.Id} lifted {d.Y:0.###} m, more than one level"));
                    Trace = Inspect.MixDouble(Trace, p.Position.X + p.Position.Y * 3 + p.Position.Z * 7);
                }

                // ---- Bars ----
                foreach (var p in Players)
                    Assert.That(Sculpt.BarOf(p.Id).Value, Is.InRange(0, SculptConfig.BarMax), Ctx("bar range"));

                // ---- Grid: volume, frozen cells (from the change log against last tick's mirror) ----
                _changes.Clear();
                Grid.DrainChanges(_changes);
                long delta = 0;
                foreach (var c in _changes)
                {
                    ushort was = _mirror.Get(c), now = Grid.Get(c);
                    if (was == now) continue;
                    Assert.That(was != SubId.Bedrock && was != SubId.Anchor, Is.True, Ctx($"frozen sub-cell {c} ({was}) changed to {now}"));
                    Assert.That(now != SubId.Bedrock && now != SubId.Anchor, Is.True, Ctx($"sub-cell {c} became bedrock or anchor"));
                    Assert.That(c.Y, Is.InRange(Config.MinSubY - SubGrid.TileSubs, Config.MaxSubY + SubGrid.TileSubs), Ctx($"write outside the sculpt band at {c}"));
                    if (was == SubId.Empty) delta++;
                    if (now == SubId.Empty) delta--;
                    _mirror.Set(c, now);
                }
                _solid += delta;
                if (delta < -lossMax - exactLoss || delta > gainMax - exactLoss) DumpOps(opsBefore);
                Assert.That(delta, Is.InRange(-lossMax - exactLoss, gainMax - exactLoss),
                    Ctx($"solid volume changed by {delta} with raise gain <= {gainMax}, sink loss <= {lossMax}, throw loss {exactLoss}"));
                if (gainMax == 0 && lossMax == 0) Assert.That(delta, Is.EqualTo(-exactLoss), Ctx("volume conserved by pushes and falls"));

                // ---- Locks ----
                var expected = new Dictionary<Int3, int>();
                foreach (var p in Players)
                {
                    PendingSculpt pend = Sculpt.PendingOf(p.Id);
                    if (pend == null) continue;
                    Assert.That(pend.CompleteTick, Is.GreaterThan(Sculpt.Tick), Ctx("overdue windup"));
                    foreach (var c in pend.LockedCells)
                    {
                        Assert.That(Sculpt.IsLocked(c, out int owner) && owner == p.Id, Is.True, Ctx($"windup cell {c} of {p.Id} not locked by it"));
                        expected[c] = p.Id;
                    }
                }

                if (Sculpt.Tick % 60 == 0) AssertTableMatchesGrid();
            }

            private void AssertMirror(string where)
            {
                if (Grid.SameCells(_mirror)) return;
                for (int y = Grid.MinY; y < Grid.MaxYExclusive; y++)
                    for (int z = 0; z < Grid.SizeZ; z++)
                        for (int x = 0; x < Grid.SizeX; x++)
                            if (Grid.Get(x, y, z) != _mirror.Get(x, y, z))
                                Assert.Fail(Ctx($"{where}: grid write missing from the change log at ({x}, {y}, {z}): grid {Grid.Get(x, y, z)}, mirror {_mirror.Get(x, y, z)}"));
            }

            private void DumpOps(int from)
            {
                // Replays this tick's ops on last tick's grid one at a time and reports what each did to the volume.
                if (_mirrorAtTickStart == null) Assert.Fail(Ctx("volume mismatch (rerun with SCULPT_MIRROR=1 for a per-op breakdown)"));
                SubGrid g = _mirrorAtTickStart.Clone();
                var table = new PieceTable(g);
                table.RebuildFromGrid();
                var log = new List<string>();
                long solid = CountSolid(g);
                for (int i = from; i < Sculpt.Pieces.Ops.Count; i++)
                {
                    PieceOp op = Sculpt.Pieces.Ops[i];
                    int size = op.Cells?.Length ?? table.Get(op.Id)?.Cells.Count ?? 0;
                    try { table.Apply(op); } catch (Exception ex) { log.Add($"op {i} {op.Type} id {op.Id}: replay threw {ex.GetType().Name}"); continue; }
                    long now = CountSolid(g);
                    log.Add($"op {i} {op.Type} id {op.Id} delta {op.Delta} fill {op.FillVacated} size {size}: volume {now - solid:+0;-0;0}");
                    solid = now;
                }
                Assert.Fail(Ctx($"volume mismatch: change-log delta vs true count {CountSolid(Grid) - CountSolid(_mirrorAtTickStart)}; ops this tick: " + string.Join(" | ", log)));
            }

            private static int Columns(Int3[] cells)
            {
                var cols = new HashSet<long>();
                foreach (var c in cells) cols.Add(((long)c.X << 32) | (uint)c.Z);
                return cols.Count;
            }

            private void CheckBegan(in SculptEvent e)
            {
                Begun++;
                SculptCheck c = e.Check;
                Assert.That(_open.ContainsKey(e.PlayerId), Is.False, Ctx("two windups for one player"));
                Assert.That(Array.IndexOf(e.WarnedPlayers ?? Array.Empty<int>(), e.PlayerId), Is.EqualTo(-1), Ctx("sculptor warned about own sculpt"));
                Assert.That(c.Size, Is.GreaterThan(0), Ctx("a piece has cells"));
                if (e.Action.Type == SculptActionType.Throw)
                {
                    Assert.That(c.WindupTicks, Is.EqualTo(Config.ThrowWindup(c.Size)), Ctx("throw windup by size"));
                    Assert.That(c.Cost, Is.EqualTo(Config.ThrowCost(c.Size)), Ctx("throw cost by size"));
                }
                else
                {
                    int baseCost = Config.CostFor(e.Action.Type);
                    int lo = (baseCost * Config.MinCostScalePercent + 50) / 100, hi = (baseCost * Config.MaxCostScalePercent + 50) / 100;
                    int mult = c.EnemyReinforced ? Config.EnemyReinforcedCostMultiplier : 1;
                    int extra = e.Action.Reinforce ? Config.CostReinforce : 0;
                    Assert.That(c.Cost, Is.InRange(lo * mult + extra, hi * mult + extra), Ctx($"cost {c.Cost} for size {c.Size}"));
                    int minW = Config.WindupTicks;
                    if (e.Action.Reinforce) minW = Math.Max(minW, Config.ReinforcedCasterWindupTicks);
                    if (c.EnemyReinforced) minW = Math.Max(minW, Config.EnemyReinforcedWindupTicks);
                    int maxW = Math.Max(minW, Config.MaxWindupTicks);
                    Assert.That(c.WindupTicks, Is.InRange(minW, maxW), Ctx($"windup {c.WindupTicks} for size {c.Size}"));
                }
                _open[e.PlayerId] = (e.Tick, c.WindupTicks);
            }

            private static readonly SculptActionType[] Kinds = { SculptActionType.Move, SculptActionType.Move, SculptActionType.Move, SculptActionType.Raise, SculptActionType.Raise, SculptActionType.Sink, SculptActionType.Sink, SculptActionType.Throw };

            private void TryRandomAction(PlayerBody p)
            {
                Vec3d look = new Vec3d(R.Range(-1, 1), R.Range(-0.9, 0.15), R.Range(-1, 1));
                if (Players.Count > 1 && R.Chance(AimAtPlayersChance))
                {
                    PlayerBody q = Players[R.Int(0, Players.Count)];
                    look = q.Position + new Vec3d(R.Range(-1.4, 1.4), R.Range(-0.8, 0.2), R.Range(-1.4, 1.4)) - p.Eye;
                }
                TerrainHit h = Grid.Raycast(p.Eye, look, Config.RangeMeters + 0.5);
                if (!h.Hit) return;
                if (InfiniteBar) Sculpt.BarOf(p.Id).SetValue(SculptConfig.BarMax);

                SculptActionType kind = R.Pick(Kinds);
                bool reinforce = kind != SculptActionType.Throw && R.Chance(0.2);
                Vec3d throwLook = new Vec3d(R.Range(-1, 1), R.Range(-0.2, 0.4), R.Range(-1, 1));
                if (Players.Count > 1 && R.Chance(0.6))
                {
                    PlayerBody q = Players[R.Int(0, Players.Count)];
                    throwLook = q.Eye - p.Eye;
                }
                var a = new SculptAction { Type = kind, Target = h.Sub, Direction = R.Pick(DirUtil.Horizontal), AimPoint = h.Point, Reinforce = reinforce, LookDir = throwLook };

                // Evaluate is pure.
                _changes.Clear();
                Grid.DrainChanges(_changes);
                Assert.That(_changes.Count, Is.EqualTo(0), Ctx("grid writes outside Step/TryBegin"));
                int ops = Sculpt.Pieces.Ops.Count, bar = Sculpt.BarOf(p.Id).Value, events = Sculpt.Events.Count;
                SculptCheck ev = Sculpt.Evaluate(p, a);
                Grid.DrainChanges(_changes);
                Assert.That(_changes.Count, Is.EqualTo(0), Ctx("Evaluate wrote the grid"));
                Assert.That(Sculpt.Pieces.Ops.Count, Is.EqualTo(ops), Ctx("Evaluate logged an op"));
                Assert.That(Sculpt.BarOf(p.Id).Value, Is.EqualTo(bar), Ctx("Evaluate spent bar"));
                Assert.That(Sculpt.Events.Count, Is.EqualTo(events), Ctx("Evaluate raised an event"));

                SculptCheck c = Sculpt.TryBegin(p, a);
                Assert.That(c.Result, Is.EqualTo(ev.Result), Ctx("TryBegin agrees with Evaluate"));
                Trace = Inspect.Mix(Trace, (ulong)c.Result);
                if (!c.IsValid)
                {
                    Rejected[c.Result] = Rejected.TryGetValue(c.Result, out int k) ? k + 1 : 1;
                    Assert.That(Sculpt.BarOf(p.Id).Value, Is.EqualTo(bar), Ctx("a rejected sculpt costs nothing"));
                    Assert.That(Sculpt.Events.Count, Is.EqualTo(events), Ctx("a rejected sculpt raises no event"));
                    // A rejected confirm may only carve nothing: the grid is untouched.
                    Grid.DrainChanges(_changes);
                    Assert.That(_changes.Count, Is.EqualTo(0), Ctx("a rejected sculpt changed the grid"));
                    return;
                }
                Assert.That(Sculpt.BarOf(p.Id).Value, Is.EqualTo(bar - c.Cost), Ctx("charged on confirm"));
                Assert.That(Sculpt.Events.Count, Is.EqualTo(events + 1));
                CheckBegan(Sculpt.Events[events]);
                // Carving natural rock into a piece changes ids only (volume unchanged): fold it into the mirror now.
                Grid.DrainChanges(_changes);
                foreach (var x in _changes)
                {
                    ushort was = _mirror.Get(x), now = Grid.Get(x);
                    Assert.That(was == SubId.Natural && SubId.IsPiece(now), Is.True, Ctx($"confirm changed {x} from {was} to {now} (only carving is allowed)"));
                    _mirror.Set(x, now);
                }
                Grid.ClearChanges();
                _mirror.ClearChanges();
            }

            /// <summary>Every piece's cells hold its id, and every piece id in the grid belongs to its piece.</summary>
            public void AssertTableMatchesGrid()
            {
                var counts = new Dictionary<ushort, int>();
                for (int y = Config.MinSubY - SubGrid.TileSubs; y <= Config.MaxSubY + SubGrid.TileSubs; y++)
                    for (int z = 0; z < Grid.SizeZ; z++)
                        for (int x = 0; x < Grid.SizeX; x++)
                        {
                            ushort id = Grid.Get(x, y, z);
                            if (!SubId.IsPiece(id)) continue;
                            counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
                        }
                foreach (var pc in Sculpt.Pieces.All)
                {
                    Assert.That(counts.TryGetValue(pc.Id, out int n) ? n : 0, Is.EqualTo(pc.Cells.Count), Ctx($"piece {pc.Id} cell count vs grid"));
                    foreach (var c in pc.Cells) Assert.That(Grid.Get(c), Is.EqualTo(pc.Id), Ctx($"piece {pc.Id} cell {c}"));
                    counts.Remove(pc.Id);
                }
                Assert.That(counts.Count, Is.Zero, Ctx($"grid holds ids of {counts.Count} pieces the table does not know"));
            }

            /// <summary>Remove players, let everything settle, then check the support rule and the volume total.</summary>
            public void SettleAndCheck()
            {
                foreach (var p in Players) Sculpt.RemovePlayer(p.Id);
                Players.Clear();
                for (int i = 0; i < 3000 && (Sculpt.HasFallingPieces || Sculpt.Thrown.Count > 0); i++) RunTick();
                Assert.That(Sculpt.HasFallingPieces, Is.False, $"seed {Seed}: never settled");
                Assert.That(CountSolid(Grid), Is.EqualTo(_solid), $"seed {Seed}: tracked volume drifted from the grid");
                Assert.That(Grid.SameCells(_mirror), Is.True, $"seed {Seed}: change log missed writes");
                AssertTableMatchesGrid();

                foreach (var pc in Sculpt.Pieces.All)
                {
                    var set = new HashSet<Int3>(pc.Cells);
                    bool resting = false;
                    foreach (var c in pc.Cells)
                        if (!set.Contains(c + Int3.Down) && Grid.Get(c + Int3.Down) != SubId.Empty) resting = true;
                    Assert.That(resting, Is.True, $"seed {Seed}: piece {pc.Id} floats with nothing under it");
                    Assert.That(Sculpt.IsSupported(pc.Cells) || !CanDrop(pc), Is.True, $"seed {Seed}: piece {pc.Id} breaks the one-third rule but stopped falling");
                }
            }

            private bool CanDrop(PieceInfo pc)
            {
                var set = new HashSet<Int3>(pc.Cells);
                foreach (var c in pc.Cells)
                {
                    Int3 b = c + Int3.Down;
                    if (!set.Contains(b) && Grid.Get(b) != SubId.Empty) return false;
                }
                return true;
            }

            public string Summary()
            {
                string rej = string.Join(" ", new List<string>(Rejected.Keys.Count > 0 ? MapRej() : new List<string>()));
                return $"seed {Seed}: begun {Begun}, completed {Completed} (moves {Moves}, raises {Raises}, sinks {Sinks}), cancelled {Cancelled}, throws {Throws}, hits {ThrowHits}, falls {Falls}, shoves {Shoves}, drags {Drags}; rejected {rej}";
            }

            private List<string> MapRej()
            {
                var l = new List<string>();
                foreach (var kv in Rejected) l.Add($"{kv.Key}={kv.Value}");
                return l;
            }
        }

        [TestCaseSource(nameof(Seeds))]
        public void StaticPlayers_InvariantsHoldEveryTick(int seed)
        {
            var s = new Scenario(seed, 6, move: false);
            s.Run(900);
            s.SettleAndCheck();
            TestContext.Out.WriteLine(s.Summary());
        }

        [TestCaseSource(nameof(Seeds))]
        public void Brawl_InvariantsHoldEveryTick(int seed)
        {
            var s = new Scenario(seed + 1, 8, move: false, crouchChance: 0.4) { InfiniteBar = true, ActionChance = 0.4, AimAtPlayersChance = 0.8, CheckMirrorEveryTick = Environment.GetEnvironmentVariable("SCULPT_MIRROR") == "1" };
            s.Run(900);
            s.SettleAndCheck();
            TestContext.Out.WriteLine(s.Summary());
        }

        [TestCaseSource(nameof(Seeds))]
        public void MovingPlayers_InvariantsHoldEveryTick(int seed)
        {
            var s = new Scenario(seed + 2, 6, move: true) { InfiniteBar = true, ActionChance = 0.3, AimAtPlayersChance = 0.7 };
            s.Run(900);
            s.SettleAndCheck();
            TestContext.Out.WriteLine(s.Summary());
        }

        [TestCaseSource(nameof(Seeds))]
        public void MovingPlayers_SameSeedReplaysIdentically(int seed)
        {
            var a = new Scenario(seed + 3, 4, move: true) { InfiniteBar = true, ActionChance = 0.3 };
            var b = new Scenario(seed + 3, 4, move: true) { InfiniteBar = true, ActionChance = 0.3 };
            a.Run(500);
            b.Run(500);
            Assert.That(b.Trace, Is.EqualTo(a.Trace), "trace diverged");
            Assert.That(b.Grid.Hash(), Is.EqualTo(a.Grid.Hash()), "grid diverged");
        }

        [Test]
        public void FuzzReachesEveryMechanic()
        {
            int throws = 0, hits = 0, falls = 0, shoves = 0, drags = 0, raises = 0, sinks = 0, moves = 0, cancelled = 0;
            var rej = new HashSet<SculptResult>();
            foreach (int seed in Seeds())
            {
                var s = new Scenario(seed + 1, 8, move: false, crouchChance: 0.4) { InfiniteBar = true, ActionChance = 0.4, AimAtPlayersChance = 0.8 };
                s.Run(900 * SimTime.Per60);
                throws += s.Throws; hits += s.ThrowHits; falls += s.Falls; shoves += s.Shoves; drags += s.Drags;
                raises += s.Raises; sinks += s.Sinks; moves += s.Moves; cancelled += s.Cancelled;
                foreach (var k in s.Rejected.Keys) rej.Add(k);
            }
            TestContext.Out.WriteLine($"throws {throws}, hits {hits}, falls {falls}, shoves {shoves}, drags {drags}, raises {raises}, sinks {sinks}, moves {moves}, cancelled {cancelled}; rejections {string.Join(",", rej)}");
            Assert.That(throws, Is.GreaterThan(0), "throws");
            Assert.That(hits, Is.GreaterThan(0), "throw hits");
            Assert.That(falls, Is.GreaterThan(0), "falls");
            Assert.That(shoves, Is.GreaterThan(0), "shoves");
            Assert.That(drags, Is.GreaterThan(0), "rider drags");
            Assert.That(Math.Min(raises, Math.Min(sinks, moves)), Is.GreaterThan(0), "every action type completed");
            Assert.That(rej, Does.Contain(SculptResult.Locked));
            Assert.That(rej, Does.Contain(SculptResult.PlayerBlocked));
            Assert.That(rej, Does.Contain(SculptResult.DestinationBlocked));
        }
    }
}
