using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    public class HandRulesTests
    {
        private static readonly HandConfig Cfg = new HandConfig();
        private const int P = SimTime.Per60; // the approved hand timings are in sixtieths of a second

        private static HandState Run(HandState h, int from, int to, Func<int, HandInput> input)
        {
            for (int t = from; t <= to; t++) HandRules.Step(ref h, t, input(t), Cfg);
            return h;
        }

        /// <summary>Enters sculpt mode on tick 0 and runs through tick 10/60 s.</summary>
        private static HandState InSculpt(HandState h) => Run(h, 0, 10 * P, t => new HandInput { SculptToggle = t == 0 });

        [Test]
        public void EnteringSculptModeTakesSixTicks()
        {
            var h = HandState.Initial;
            HandRules.Step(ref h, 100, new HandInput { SculptToggle = true }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptEnter));
            Assert.That(h.InSculptMode, Is.True, "aiming and the ghost work at once");
            for (int t = 101; t < 100 + 6 * P; t++)
            {
                HandRules.Step(ref h, t, default, Cfg);
                Assert.That(h.CanConfirmSculpt, Is.False, $"no confirm on tick {t}");
            }
            HandRules.Step(ref h, 100 + 6 * P, default, Cfg);
            Assert.That(h.CanConfirmSculpt, Is.True, "confirms from 0.1 s");
        }

        [TestCase(Weapon.Pistol, 6)]
        [TestCase(Weapon.Smg, 9)]
        [TestCase(Weapon.Ar, 10)]
        [TestCase(Weapon.Pump, 12)]
        public void LeavingSculptModePaysTheWeaponsExitTime(Weapon w, int exit60)
        {
            int exit = exit60 * P;
            int leave = 10 * P + 1;
            var h = HandState.Initial;
            h.Held = w;
            h = InSculpt(h);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Sculpt));
            HandRules.Step(ref h, leave, new HandInput { SculptToggle = true }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptExit));
            h = Run(h, leave + 1, leave + exit - 1, _ => default);
            Assert.That(h.CanFire, Is.False);
            HandRules.Step(ref h, leave + exit, default, Cfg);
            Assert.That(h.CanFire, Is.True, $"ready {exit60}/60 s after leaving");
        }

        [Test]
        public void GunLockKeepsYouInSculptModeUntilTheActionLandsThenPaysTheExit()
        {
            int leave = 10 * P + 1, unlock = 30 * P;
            var h = HandState.Initial;
            h.Held = Weapon.Pump;
            h = InSculpt(h);
            HandRules.LockGun(ref h, unlock);
            HandRules.Step(ref h, leave, new HandInput { SculptToggle = true }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Sculpt), "locked: still sculpting");
            h = Run(h, leave + 1, unlock - 1, _ => default);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Sculpt));
            HandRules.Step(ref h, unlock, default, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptExit), "leaves as soon as the lock ends");
            Assert.That(h.ReadyTick, Is.EqualTo(unlock + 12 * P), "then the pump's exit time");
        }

        [Test]
        public void WeaponKeyInSculptModeLeavesToThatWeapon()
        {
            int leave = 10 * P + 1;
            var h = InSculpt(HandState.Initial);
            HandRules.Step(ref h, leave, new HandInput { WeaponKey = 4 }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptExit));
            Assert.That(h.Held, Is.EqualTo(Weapon.Pistol));
            Assert.That(h.ReadyTick, Is.EqualTo(leave + 12 * P), "a different gun pays its full draw (ISSUE-11)");
        }

        [Test]
        public void WeaponKeyForTheGunYouCameFromUsesTheExitTime()
        {
            int leave = 10 * P + 1;
            var h = InSculpt(HandState.Initial); // pump out
            HandRules.Step(ref h, leave, new HandInput { WeaponKey = 1 }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptExit));
            Assert.That(h.ReadyTick, Is.EqualTo(leave + 12 * P), "pump exit time");
        }

        [Test]
        public void EnteringSculptModeMidDrawStillOwesTheDraw()
        {
            int leave = 10 * P;
            var h = HandState.Initial;
            HandRules.Step(ref h, 0, new HandInput { WeaponKey = 3 }, Cfg); // AR draw, 0.35 s
            HandRules.Step(ref h, 1, new HandInput { SculptToggle = true }, Cfg);
            h = Run(h, 2, leave - 1, _ => default);
            HandRules.Step(ref h, leave, new HandInput { SculptToggle = true }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.SculptExit));
            Assert.That(h.ReadyTick, Is.EqualTo(leave + 21 * P), "the AR was never out, so leaving draws it");
        }

        [Test]
        public void SwappingWeaponsPaysTheDrawTime()
        {
            var h = HandState.Initial;
            HandRules.Step(ref h, 5, new HandInput { WeaponKey = 3 }, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(h.ReadyTick, Is.EqualTo(5 + 21 * P));
            HandRules.Step(ref h, 6, new HandInput { WeaponKey = 3 }, Cfg);
            Assert.That(h.ReadyTick, Is.EqualTo(5 + 21 * P), "pressing the held weapon again changes nothing");
        }

        [Test]
        public void DisarmBlocksEverythingThenRedrawsTheHeldWeapon()
        {
            int at = 10 * P + 1, end = at + 20 * P;
            var h = HandState.Initial;
            h.Held = Weapon.Smg;
            h = InSculpt(h);
            HandRules.Disarm(ref h, at, 20 * P);
            Assert.That(h.CanConfirmSculpt, Is.False);
            h = Run(h, at, end - 1, t => new HandInput { SculptToggle = t == at + 4 * P, WeaponKey = (byte)(t == at + 5 * P ? 1 : 0) });
            Assert.That(h.Mode, Is.EqualTo(HandMode.Disarmed), "presses do nothing while disarmed");
            HandRules.Step(ref h, end, default, Cfg);
            Assert.That(h.Mode, Is.EqualTo(HandMode.Drawing));
            Assert.That(h.Held, Is.EqualTo(Weapon.Smg));
            Assert.That(h.ReadyTick, Is.EqualTo(end + 18 * P));
        }
    }

    public class WorldCodecTests
    {
        [Test]
        public void SculptConfirmSurvivesTheWireExactly()
        {
            var rng = new Random(9);
            var w = new ByteWriter();
            var into = new List<InputCmd>();
            for (int i = 0; i < 500; i++)
            {
                var type = (SculptActionType)rng.Next(4);
                var raw = new InputCmd
                {
                    Tick = 100 + i,
                    ViewTick = 100 + i - 3,
                    Move = new MoveInput { Yaw = rng.NextDouble() },
                    Hand = new HandInput { SculptToggle = rng.Next(2) == 0, WeaponKey = (byte)rng.Next(5) },
                    HasSculpt = rng.Next(3) != 0,
                    SculptSeq = (byte)rng.Next(1, 256),
                    Sculpt = new SculptAction
                    {
                        Type = type,
                        Target = new Int3(rng.Next(-500, 500), rng.Next(-40, 40), rng.Next(-500, 500)),
                        Direction = (Dir)rng.Next(6),
                        Reinforce = rng.Next(2) == 0,
                        AimPoint = new Vec3d(rng.NextDouble() * 100, rng.NextDouble() * 5, rng.NextDouble() * 100),
                        LookDir = new Vec3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5),
                    },
                };
                InputCmd q = InputCmd.Quantize(raw);
                Assert.That(InputCmd.Quantize(q), Is.EqualTo(q));
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = 1, Cmds = new List<InputCmd> { q } });
                var r = new ByteReader(w.Buffer, 0, w.Length);
                Protocol.ReadInputs(ref r, into);
                Assert.That(into[0], Is.EqualTo(q), "the server validates exactly what the client predicted");
            }
        }

        [Test]
        public void WorldMessageRoundTrips()
        {
            var rng = new Random(3);
            var cells = new List<Int3>();
            for (int x = 10; x < 22; x++)
                for (int z = 5; z < 13; z++)
                    for (int y = -4; y < 0; y++)
                        if (rng.Next(3) != 0) cells.Add(new Int3(x, y, z));
            var ops = new List<PieceOp>
            {
                new PieceOp { Type = PieceOpType.Carve, Id = 77, Cells = PieceTable.SortedCopy(cells) },
                new PieceOp { Type = PieceOpType.Translate, Id = 77, Delta = new Int3(0, 4, 0), FillVacated = true },
                new PieceOp { Type = PieceOpType.Stamp, Id = 77, Team = 2, TintTick = 1234, ReinforceTeam = 2, ReinforceUntilTick = 5000 },
                new PieceOp { Type = PieceOpType.Remove, Id = 77 },
            };
            var events = new List<WorldEvent>
            {
                new WorldEvent { Type = WorldEventType.Began, PlayerId = 3, Team = 2, Seq = 9, Piece = 77, Action = SculptActionType.Throw, StartTick = 1000, WindupTicks = 36, Cost = 2500, LocksGun = true, Look = new Vec3d(0.5f, 0.1f, -0.8f), Warned = new[] { 1, 4 } },
                new WorldEvent { Type = WorldEventType.Rejected, PlayerId = 3, Seq = 10, Result = SculptResult.NotInSculptMode },
                new WorldEvent { Type = WorldEventType.Thrown, PlayerId = 3, Team = 2, Piece = 77, ProjectileId = 5, AtTick = 1036, LaunchTick = 1036, SubCells = 256, X = 123456, Y = -5, Z = 9999999, VX = 1, VY = 2, VZ = 3, HX = 4, HY = 5, HZ = 6 },
                new WorldEvent { Type = WorldEventType.ThrowHit, PlayerId = 1, ProjectileId = 5, Value = 70, Disarm = 20, Point = new Vec3d(1, 2, 3) },
            };
            var wr = new ByteWriter();
            WorldProtocol.Write(wr, new WorldMsg { Tick = 1036, Ops = ops, Events = events });
            var r = new ByteReader(wr.Buffer, 0, wr.Length);
            var ops2 = new List<PieceOp>();
            var ev2 = new List<WorldEvent>();
            WorldMsg m = WorldProtocol.Read(ref r, ops2, ev2);
            Assert.That(m.Tick, Is.EqualTo(1036));
            Assert.That(ops2[0].Cells, Is.EqualTo(ops[0].Cells), "carve cells survive the bitmask");
            Assert.That(ops2[1].Delta, Is.EqualTo(ops[1].Delta));
            Assert.That(ops2[1].FillVacated, Is.True);
            Assert.That(ops2[2].ReinforceUntilTick, Is.EqualTo(5000));
            Assert.That(ops2[3].Type, Is.EqualTo(PieceOpType.Remove));
            Assert.That(ev2[0].Warned, Is.EqualTo(new[] { 1, 4 }));
            Assert.That(ev2[0].Look, Is.EqualTo(events[0].Look));
            Assert.That(ev2[0].WindupTicks, Is.EqualTo(36));
            Assert.That(ev2[1].Result, Is.EqualTo(SculptResult.NotInSculptMode));
            Assert.That(ev2[2].ToProjectile().Z, Is.EqualTo(9999999));
            Assert.That(ev2[3].Disarm, Is.EqualTo(20));
            Assert.That(r.Remaining, Is.EqualTo(0));
        }

        [Test]
        public void JournalRollbackRestoresGridAndPiecesExactly()
        {
            var h = SculptNetHarness.Flat();
            SubGrid g = h.ServerGrid;
            var table = h.ServerSculpt.Pieces;
            ulong before = g.Hash();
            var natural = new List<Int3>();
            PieceFinder.FindNatural(g, new Int3(41, -2, 37), h.SculptConfig, natural);
            PieceInfo existing = table.Carve(natural);
            ulong carved = g.Hash();
            int ops = table.Ops.Count;

            table.BeginJournal();
            table.Translate(existing.Id, new Int3(0, 4, 0), true);
            table.Stamp(existing.Id, 1, 50, 1, 900);
            var wall = new List<Int3>();
            PieceFinder.FindNatural(g, new Int3(29, 1, 21), h.SculptConfig, wall);
            PieceInfo temp = table.Carve(wall);
            table.Translate(temp.Id, new Int3(4, 0, 0), false);
            table.Remove(existing.Id);
            Assert.That(g.Hash(), Is.Not.EqualTo(carved));
            table.Rollback();

            Assert.That(g.Hash(), Is.EqualTo(carved));
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.Get(existing.Id).Cells, Is.EqualTo(PieceTable.SortedCopy(natural)));
            Assert.That(table.Get(existing.Id).Team, Is.EqualTo(0));
            Assert.That(table.Ops.Count, Is.EqualTo(ops));
            Assert.That(before, Is.Not.EqualTo(carved), "carving changes ids");
        }
    }

    public class SculptNetTests
    {
        private const double B = SubGrid.TileSize;
        private static readonly Int3 WallDest = new Int3(33, 1, 22); // a sub-cell the wall fills after a push east

        private static (SculptNetHarness h, SculptNetHarness.Peer p, int[] seen) PushScenario(double oneWayMs, int confirmAt = 150)
        {
            var h = SculptNetHarness.Flat();
            var p = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(oneWayMs), SculptNetHarness.Lag(oneWayMs));
            var seen = new int[] { -1, -1 }; // [0] first client tick whose script saw the wall moved, [1] confirm client tick
            p.Script = (peer, t) =>
            {
                if (seen[0] < 0 && peer.Grid.Get(WallDest) != SubId.Empty) seen[0] = t;
                var i = new SculptTickInput { Move = new MoveInput() };
                if (peer.Sculpt.LocalBody == null) return i;
                if (seen[1] < 0 && peer.Client.Predicted.Grounded && peer.Client.Predicted.Hand.Mode == HandMode.Weapon && t > 1000 + 150) i.Hand.SculptToggle = true;
                if (seen[1] < 0 && peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    i.Confirm = true;
                    i.Action = SculptNetHarness.PushWall(peer);
                    seen[1] = t;
                }
                return i;
            };
            return (h, p, seen);
        }

        [TestCase(0)]
        [TestCase(50)]
        [TestCase(120)]
        public void ScrollSpamDuringYourOwnWindupsIsQueuedNeverRed(double oneWayMs)
        {
            // Play-test: wheel notches during your own windup came back red (Busy, then "Locked" every tick once the 3-tick
            // buffer ran out, and every refusal twice: once locally, once from the server).
            var h = SculptNetHarness.Flat();
            var p = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(oneWayMs), SculptNetHarness.Lag(oneWayMs));
            int start = -1, confirms = 0;
            p.Script = (peer, t) =>
            {
                var i = new SculptTickInput { Move = new MoveInput() };
                if (peer.Sculpt.LocalBody == null) return i;
                if (start < 0 && peer.Client.Predicted.Grounded && peer.Client.Predicted.Hand.Mode == HandMode.Weapon && t > 1000 + 150) i.Hand.SculptToggle = true;
                if (peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    if (start < 0) start = t;
                    if (t - start < 12 * SimTime.Per60 && (t - start) % (3 * SimTime.Per60) == 0 && SculptNetHarness.Aim(peer, new Vec3d(8.5 * SubGrid.TileSize, 0, 6.6), out Int3 sub, out Vec3d aim))
                    {
                        i.Confirm = true;
                        i.Action = SculptAction.Raise(sub, aim);
                        confirms++;
                    }
                }
                return i;
            };
            h.RunSeconds(4);
            Assert.That(confirms, Is.GreaterThanOrEqualTo(4)); // four notches in 0.2 s on a 1-level wall: stays under the +4 cap
            Assert.That(p.SculptEvents.Where(e => e.Type == ClientSculptEventType.Rejected).Select(e => e.Result), Is.Empty,
                "no red: " + string.Join(", ", p.SculptEvents.Select(e => $"{e.Type}/{e.Action}/{e.Result}" + (e.Cells != null && e.Cells.Length > 0 ? $" y{e.Cells.Min(c => c.Y)}..{e.Cells.Max(c => c.Y)} n{e.Cells.Length}" : ""))));
            Assert.That(p.SculptEvents.Count(e => e.Type == ClientSculptEventType.LocalLanded), Is.GreaterThanOrEqualTo(2), "queued raises chain");
            h.AssertConverged();
        }

        [TestCase(0)]
        [TestCase(50)]
        [TestCase(120)]
        public void YourPushLandsInYourViewOnTheServersTickWithoutWaitingForTheRoundTrip(double oneWayMs)
        {
            var (h, p, seen) = PushScenario(oneWayMs);
            h.RunSeconds(4);
            Assert.That(seen[1], Is.GreaterThan(0), "confirmed");
            Assert.That(p.SculptEvents.Any(e => e.Type == ClientSculptEventType.Rejected), Is.False, "nothing rejected");
            int windup = h.SculptConfig.ScaledWindup(96);
            // The script for tick T + windup + 1 is the first to see it: it landed in BeforeMove(T + windup).
            Assert.That(windup, Is.EqualTo(9 * SimTime.Per60), "0.15 s");
            Assert.That(seen[0] - 1 - seen[1], Is.EqualTo(windup), "lands exactly one windup after the confirm, at any ping");
            Assert.That(p.SculptEvents.Count(e => e.Type == ClientSculptEventType.LocalLanded), Is.EqualTo(1));
            Assert.That(p.Sculpt.Mispredictions, Is.EqualTo(0));
            Assert.That(p.Sculpt.ServerRejections, Is.EqualTo(0));
            h.AssertConverged();
            Assert.That(h.ServerGrid.Get(WallDest), Is.Not.EqualTo(SubId.Empty), "the server moved it too");
        }

        [Test]
        public void TheBarIsChargedAtOnceAndMatchesTheServer()
        {
            var (h, p, seen) = PushScenario(60);
            int confirmTick = -1;
            var inner = p.Script;
            int barAfter = -1;
            p.Script = (peer, t) =>
            {
                var i = inner(peer, t);
                if (i.Confirm) confirmTick = t;
                if (confirmTick > 0 && t == confirmTick + 1) barAfter = peer.Sculpt.DisplayBar;
                return i;
            };
            h.RunSeconds(1.5);
            Assert.That(barAfter, Is.LessThan(SculptConfig.BarMax), "spent on the confirm tick, before the server answers");
            h.RunSeconds(0.5);
            Assert.That(p.Sculpt.DisplayBar, Is.EqualTo(h.ServerSculpt.BarOf(p.ServerSide.Id).Value).Within(60), "agrees with the server (regen aside)");
        }

        [Test]
        public void ConfirmBeforeSculptModeIsReadyIsRefusedForFree()
        {
            var h = SculptNetHarness.Flat();
            var p = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(40), SculptNetHarness.Lag(40));
            int toggle = -1;
            p.Script = (peer, t) =>
            {
                var i = new SculptTickInput();
                if (peer.Sculpt.LocalBody == null || !peer.Client.Predicted.Grounded || t < 80) return i;
                if (toggle < 0) { toggle = t; i.Hand.SculptToggle = true; }
                else if (t == toggle + 3) { i.Confirm = true; i.Action = SculptNetHarness.PushWall(peer); }
                return i;
            };
            h.RunSeconds(3);
            Assert.That(p.SculptEvents.Any(e => e.Type == ClientSculptEventType.Rejected && e.Result == SculptResult.NotInSculptMode), Is.True);
            Assert.That(p.SculptEvents.Any(e => e.Type == ClientSculptEventType.LocalBegan), Is.False);
            Assert.That(h.ServerSculpt.BarOf(p.ServerSide.Id).Value, Is.EqualTo(SculptConfig.BarMax));
            Assert.That(h.ServerGrid.Get(WallDest), Is.EqualTo(SubId.Empty));
            h.AssertConverged();
        }

        [Test]
        public void OthersSeeTheTellAndTheResult()
        {
            var (h, a, seen) = PushScenario(40);
            var b = h.AddClient(SculptNetHarness.FeetIn(12, 6), 2, SculptNetHarness.Lag(70), SculptNetHarness.Lag(70), seed: 2);
            h.RunSeconds(4);
            var began = b.SculptEvents.Where(e => e.Type == ClientSculptEventType.Began).ToList();
            Assert.That(began.Count, Is.EqualTo(1), "the other player sees the windup start");
            Assert.That(began[0].Action, Is.EqualTo(SculptActionType.Move));
            Assert.That(began[0].Direction, Is.EqualTo(Dir.East), "the tell shows the direction");
            Assert.That(began[0].Team, Is.EqualTo(1));
            Assert.That(began[0].Cells.Length, Is.GreaterThan(0));
            Assert.That(b.SculptEvents.Any(e => e.Type == ClientSculptEventType.Completed && !e.Mine), Is.True);
            Assert.That(b.Sculpt.Windups.Count, Is.EqualTo(0));
            h.AssertConverged();
        }

        [Test]
        public void StandingOnAPieceBeingSculptedWarnsYou()
        {
            var h = SculptNetHarness.Flat();
            var a = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(30), SculptNetHarness.Lag(30));
            var b = h.AddClient(SculptNetHarness.FeetIn(5, 8), 2, SculptNetHarness.Lag(30), SculptNetHarness.Lag(30), seed: 3);
            bool done = false;
            a.Script = (peer, t) =>
            {
                var i = new SculptTickInput();
                if (peer.Sculpt.LocalBody == null || t < 80 || done) return i;
                if (peer.Client.Predicted.Hand.Mode == HandMode.Weapon) i.Hand.SculptToggle = true;
                else if (peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    i.Confirm = true;
                    i.Action = SculptNetHarness.RaiseFloor(peer, 5.5 * B, 8.5 * B);
                    done = true;
                }
                return i;
            };
            h.RunSeconds(3);
            Assert.That(b.SculptEvents.Any(e => e.Type == ClientSculptEventType.Warned && e.PlayerId == a.ServerSide.Id), Is.True);
            Assert.That(b.Client.Predicted.Position.Y, Is.EqualTo(B).Within(0.05), "carried up, and the client agrees");
            h.AssertConverged();
        }

        [Test]
        public void TwoPlayersGrabbingTheSamePieceConvergeAndTheLoserIsRefused()
        {
            var h = SculptNetHarness.Flat();
            var a = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(20), SculptNetHarness.Lag(20));
            var b = h.AddClient(SculptNetHarness.FeetIn(5, 7.5), 2, SculptNetHarness.Lag(90), SculptNetHarness.Lag(90), seed: 4);
            foreach (var peer in new[] { a, b })
            {
                bool done = false;
                peer.Script = (pp, t) =>
                {
                    var i = new SculptTickInput();
                    if (pp.Sculpt.LocalBody == null || t < 90 || done) return i;
                    if (pp.Client.Predicted.Hand.Mode == HandMode.Weapon) i.Hand.SculptToggle = true;
                    else if (pp.Client.Predicted.Hand.CanConfirmSculpt && h.Server.Tick >= 1120)
                    {
                        i.Confirm = true;
                        i.Action = SculptNetHarness.PushWall(pp);
                        done = true;
                    }
                    return i;
                };
            }
            h.RunSeconds(5);
            int landed = h.ServerSculpt.Pieces.All.Count(pc => pc.Cells.Any(c => c.X >= 32));
            Assert.That(landed, Is.EqualTo(1), "pushed exactly once");
            int refused = a.SculptEvents.Count(e => e.Type == ClientSculptEventType.Rejected) + b.SculptEvents.Count(e => e.Type == ClientSculptEventType.Rejected);
            Assert.That(refused, Is.GreaterThanOrEqualTo(1), "the later one is refused (or its second push hits the moved wall)");
            h.AssertConverged();
        }

        [Test]
        public void AttackingAnEnemyPieceLocksTheGunUntilItLands()
        {
            var h = SculptNetHarness.Flat();
            var a = h.AddClient(SculptNetHarness.FeetIn(4, 5.5), 1, SculptNetHarness.Lag(30), SculptNetHarness.Lag(30)); // clear of the wall once it is pushed 1.5 m west
            var b = h.AddClient(SculptNetHarness.FeetIn(11, 5.5), 2, SculptNetHarness.Lag(30), SculptNetHarness.Lag(30), seed: 5);
            int stage = 0, attackTick = -1, lockSeen = -1;
            // Team 2 pushes the wall west (it becomes theirs); then team 1 pushes it east and tries to leave sculpt mode.
            b.Script = (pp, t) =>
            {
                var i = new SculptTickInput();
                if (pp.Sculpt.LocalBody == null || t < 80 || stage > 0) return i;
                if (pp.Client.Predicted.Hand.Mode == HandMode.Weapon) i.Hand.SculptToggle = true;
                else if (pp.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    Assert.That(SculptNetHarness.Aim(pp, new Vec3d(9.6, 0.6, 6.6), out Int3 sub, out Vec3d aim), Is.True);
                    i.Confirm = true;
                    i.Action = SculptAction.Move(sub, Dir.West, aim);
                    stage = 1;
                }
                return i;
            };
            a.Script = (pp, t) =>
            {
                var i = new SculptTickInput();
                if (pp.Sculpt.LocalBody == null || stage < 1 || h.Server.Tick < 1200) return i;
                var hand = pp.Client.Predicted.Hand;
                if (stage == 1 && hand.Mode == HandMode.Weapon) i.Hand.SculptToggle = true;
                else if (stage == 1 && hand.CanConfirmSculpt)
                {
                    Assert.That(SculptNetHarness.Aim(pp, new Vec3d(6.9, 0.6, 6.6), out Int3 sub, out Vec3d aim), Is.True);
                    i.Confirm = true;
                    i.Action = SculptAction.Move(sub, Dir.East, aim);
                    attackTick = t;
                    stage = 2;
                }
                else if (stage == 2 && t == attackTick + 2) i.Hand.SculptToggle = true; // try to leave at once
                else if (stage == 2 && t > attackTick + 2 && lockSeen < 0 && hand.Mode == HandMode.SculptExit) lockSeen = t;
                return i;
            };
            h.RunSeconds(6);
            Assert.That(attackTick, Is.GreaterThan(0));
            Assert.That(a.SculptEvents.Any(e => e.Type == ClientSculptEventType.Rejected), Is.False);
            Assert.That(lockSeen - 1 - attackTick, Is.EqualTo(9 * SimTime.Per60), "stays in sculpt mode until the windup ends, then exits");
            Assert.That(a.Client.Corrections, Is.EqualTo(0), "the server agreed with the predicted hand");
            h.AssertConverged();
        }

        [Test]
        public void ThrowFliesTheSameOnTheClientAndHitsTheTarget()
        {
            var h = SculptNetHarness.Flat();
            var a = h.AddClient(SculptNetHarness.FeetIn(5, 5.5), 1, SculptNetHarness.Lag(40), SculptNetHarness.Lag(40));
            var b = h.AddClient(SculptNetHarness.FeetIn(11, 6.5), 2, SculptNetHarness.Lag(60), SculptNetHarness.Lag(60), seed: 6);
            bool done = false;
            a.Script = (pp, t) =>
            {
                var i = new SculptTickInput();
                if (pp.Sculpt.LocalBody == null || t < 90 || done) return i;
                if (pp.Client.Predicted.Hand.Mode == HandMode.Weapon) i.Hand.SculptToggle = true;
                else if (pp.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    Assert.That(SculptNetHarness.Aim(pp, new Vec3d(8.4, 0.6, 7.2), out Int3 sub, out Vec3d aim), Is.True);
                    Vec3d target = SculptNetHarness.FeetIn(11, 6.5) + new Vec3d(0, 0.9, 0);
                    i.Confirm = true;
                    i.Action = SculptAction.Throw(sub, aim, (target - pp.Sculpt.LocalBody.Eye).Normalized);
                    done = true;
                }
                return i;
            };
            h.RunSeconds(4);
            Assert.That(a.SculptEvents.Any(e => e.Type == ClientSculptEventType.ThrowLaunched && e.Mine), Is.True, "launch predicted");
            var hit = b.SculptEvents.Where(e => e.Type == ClientSculptEventType.ThrowHit && e.PlayerId == b.ServerSide.Id).ToList();
            Assert.That(hit.Count, Is.EqualTo(1), "the victim's client learns about the hit");
            Assert.That(hit[0].Value, Is.GreaterThan(0), "direct or splash damage");
            Assert.That(a.SculptEvents.Any(e => e.Type == ClientSculptEventType.ThrowHit && e.Mine), Is.True, "the thrower gets a hit marker");
            Assert.That(a.Sculpt.Flying.Count + b.Sculpt.Flying.Count, Is.EqualTo(0));
            h.AssertConverged();
        }

        [Test]
        public void LateJoinerGetsTheSculptedWorld()
        {
            var (h, a, seen) = PushScenario(30);
            h.RunSeconds(3);
            var late = h.AddClient(SculptNetHarness.FeetIn(20, 20), 2, SculptNetHarness.Lag(50), SculptNetHarness.Lag(50), seed: 7);
            h.RunSeconds(1);
            Assert.That(late.Grid.Get(WallDest), Is.Not.EqualTo(SubId.Empty));
            h.AssertConverged();
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void SculptBrawlUnderBadNetworkConverges(int seed)
        {
            var h = SculptNetHarness.Flat();
            var rng = new Random(seed);
            var peers = new List<SculptNetHarness.Peer>();
            for (int k = 0; k < 3; k++)
            {
                var link = SculptNetHarness.Lag(20 + rng.Next(80), rng.Next(15), rng.Next(10));
                peers.Add(h.AddClient(SculptNetHarness.FeetIn(6 + k * 3, 4 + k), 1 + k % 2, link, link, frameMs: 1000.0 / (60 + rng.Next(150)), seed: seed * 10 + k));
            }
            int stopAt = 1000 + SimTime.TicksPerSecond * 12;
            foreach (var peer in peers)
            {
                var r = new Random(rng.Next());
                peer.Script = (pp, t) =>
                {
                    var i = new SculptTickInput { Move = new MoveInput { MoveX = r.Next(3) - 1, MoveZ = r.Next(3) - 1, Yaw = r.NextDouble() * 6 } };
                    if (pp.Sculpt.LocalBody == null || t < 80) return i;
                    if (t > stopAt) { i.Move = default; return i; }
                    var hand = pp.Client.Predicted.Hand;
                    if (hand.Mode == HandMode.Weapon && r.Next(10) == 0) i.Hand.SculptToggle = true;
                    if (hand.InSculptMode && r.Next(4) == 0)
                    {
                        Vec3d eye = pp.Sculpt.LocalBody.Eye;
                        Vec3d point = eye + new Vec3d(r.NextDouble() * 8 - 4, -1.6 + r.NextDouble() * 2, r.NextDouble() * 8 - 4);
                        if (SculptNetHarness.Aim(pp, point, out Int3 sub, out Vec3d aim))
                        {
                            var type = (SculptActionType)r.Next(4);
                            i.Confirm = true;
                            i.Action = type == SculptActionType.Move ? SculptAction.Move(sub, DirUtil.Horizontal[r.Next(4)], aim, r.Next(5) == 0)
                                : type == SculptActionType.Raise ? SculptAction.Raise(sub, aim, r.Next(5) == 0)
                                : type == SculptActionType.Sink ? SculptAction.Sink(sub, aim)
                                : SculptAction.Throw(sub, aim, new Vec3d(r.NextDouble() - 0.5, 0.1, r.NextDouble() - 0.5));
                        }
                    }
                    return i;
                };
            }
            h.RunSeconds(16);
            Assert.That(h.ServerSculpt.Pieces.Count, Is.GreaterThan(3), "plenty of sculpting happened");
            int began = peers.Sum(pp => pp.SculptEvents.Count(e => e.Type == ClientSculptEventType.LocalBegan));
            Assert.That(began, Is.GreaterThan(10));
            h.AssertConverged();
            foreach (var pp in peers) Assert.That(h.ServerGrid.BoxOverlapsSolid(pp.ServerSide.Body.Bounds), Is.False, "nobody trapped");
        }
    }
}
