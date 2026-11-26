using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.AntiCheat
{
    /// <summary>
    /// Anti-cheat groundwork (2026-11-26): what a client sends is checked, what the server sends is limited to what a
    /// player could see or hear soon (interest management), the server's own results are watched for impossible ones,
    /// and suspicious events go in a log that replays carry and re-runs reproduce. Legit play, ping and packet loss must
    /// never be flagged or lose anything.
    /// </summary>
    public class AntiCheatTests
    {
        private static BotNav _nav;
        private static BotNav Nav() => _nav ??= DuelMap.BuildNav();

        private static NetServer DuelServer(Action<NetConfig> tweak = null) => DuelServer(out _, tweak);

        private static NetServer DuelServer(out BreachMapInfo info, Action<NetConfig> tweak = null)
        {
            var nc = new NetConfig();
            tweak?.Invoke(nc);
            return new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out info)), new MovementConfig(), nc, (c, d, r) => { });
        }

        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        /// <summary>A bot that does whatever its function says (inputs only, like every bot).</summary>
        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        private static InputCmd Cmd(NetServer s, double yaw = 0) => new InputCmd { Tick = s.Tick, ViewTick = s.Tick, Move = new MoveInput { Yaw = yaw } };

        private static double YawTo(Vec3d from, Vec3d to) => Math.Atan2(to.X - from.X, to.Z - from.Z);

        // ================================================================ inputs

        [Test]
        public void DevRespawn_IsRefused_AndFlagged_UnlessTheServerAllowsIt()
        {
            foreach (bool allow in new[] { false, true })
            {
                var h = NetHarness.Flat(c => c.AllowDevRespawn = allow);
                var p = h.AddClient(NetHarness.Lag(30), NetHarness.Lag(30));
                p.Script = Scripts.StraightNorth;
                h.RunSeconds(1.5);
                double away = (p.ServerSide.State.Position - h.Spawn).HorizontalLength;
                Assert.That(away, Is.GreaterThan(3));
                int at = p.Client.Tick + 5;
                p.RespawnScript = t => t == at;
                p.Script = t => t >= at ? default : Scripts.StraightNorth(t);
                h.RunSeconds(1);
                double after = (p.ServerSide.State.Position - h.Spawn).HorizontalLength;
                if (allow)
                {
                    Assert.That(after, Is.LessThan(0.01), "a dev server respawns you");
                    Assert.That(h.Server.Flags.Count(FlagKind.DevRespawnRefused), Is.Zero);
                }
                else
                {
                    Assert.That(after, Is.GreaterThan(3), "a modified client can't teleport home at full health");
                    Assert.That(h.Server.Flags.Count(FlagKind.DevRespawnRefused), Is.GreaterThan(0));
                }
            }
        }

        [Test]
        public void TheMatchCountdown_StillRespawnsEveryone_WithDevRespawnOff()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            Assert.That(s.Config.AllowDevRespawn, Is.False);
            s.MatchConfig.Enabled = true;
            s.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            var walk = new Brain { F = (sv, me) => { var c = Cmd(sv); c.Move.MoveZ = 1; return c; } };
            ServerPlayer a = s.AddBot(walk, 1, info.TeamSpawns[0][0], 0);
            s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, info.TeamSpawns[1][0], 0);
            for (int i = 0; i < 20 * SimTime.Per60; i++) s.Step();
            Assert.That(s.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            // The countdown put both at their spawns (a forced respawn), then the walker set off again.
            Assert.That((a.State.Position - info.TeamSpawns[0][0]).HorizontalLength, Is.LessThan(2.5));
        }

        [Test]
        public void UnwrappedAngles_ClockCheats_AndJunk_AreFlagged_AndChangeNothing()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.3);
            var w = new ByteWriter(256);
            w.Clear();
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = h.Server.Tick, Cmds = new List<InputCmd> { new InputCmd { Tick = h.Server.Tick + 3, ViewTick = h.Server.Tick, Move = new MoveInput { Yaw = 7.5 } } } });
            h.Server.Receive(p.ConnectionId, w.Buffer, 0, w.Length);
            Assert.That(h.Server.Flags.Count(FlagKind.BadView), Is.EqualTo(1), "a yaw of 7.5 rad was never wrapped by an honest client");

            w.Clear();
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = h.Server.Tick, Cmds = new List<InputCmd> { new InputCmd { Tick = h.Server.Tick + 500, ViewTick = h.Server.Tick } } });
            h.Server.Receive(p.ConnectionId, w.Buffer, 0, w.Length);
            Assert.That(h.Server.Flags.Count(FlagKind.ClockAhead), Is.EqualTo(1));

            h.Server.Receive(p.ConnectionId, new byte[] { (byte)MsgType.Inputs, 1, 2 }, 0, 3);
            Assert.That(h.Server.Flags.Count(FlagKind.Malformed), Is.EqualTo(1));
            h.RunSeconds(0.3);
            Assert.That(p.Client.Corrections, Is.Zero, "nothing the junk said reached the simulation");
        }

        // ================================================================ integrity

        [Test]
        public void ImpossibleServerResults_AreFlagged_SpeedFireRateAndMagazine()
        {
            var nc = new NetConfig();
            var s = new NetServer(OpenField(), new MovementConfig(), nc, (c, d, r) => { });
            var fire = new Brain { F = (sv, me) => { var c = Cmd(sv); c.Move.FireHeld = true; return c; } };
            ServerPlayer p = s.AddBot(fire, 1, new Vec3d(20, 0, 20), 0);
            for (int i = 0; i < SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(s.Flags.CountCategory(FlagCategory.Integrity), Is.Zero, "a pump fired by the rules is never flagged");

            // A rules bug lets the pump fire every tick (its cycle keeps being reset).
            for (int i = 0; i < 10; i++) { p.State.Weapon.SetNext(Weapon.Pump, 0); p.State.Weapon.SetUsed(Weapon.Pump, 0); s.Step(); }
            Assert.That(s.Flags.Count(FlagKind.FireTooFast), Is.GreaterThan(0));

            p.State.Weapon.SetUsed(Weapon.Ar, 200);
            s.Step();
            Assert.That(s.Flags.Count(FlagKind.MagOverflow), Is.GreaterThan(0));

            p.State.Velocity = new Vec3d(80, 0, 0);
            s.Step();
            Assert.That(s.Flags.Count(FlagKind.SpeedOverCap), Is.GreaterThan(0));
            TestContext.WriteLine(s.Flags.Report());
        }

        [Test]
        public void ABotMatch_RaisesNoIntegrityInterestOrAimFlags()
        {
            // Two hard duel bots fighting for 90 s: every rule result is possible, every hit was on someone the shooter
            // was being sent, and no bot aim looks like an aimbot.
            NetServer s = DuelServer(out BreachMapInfo info);
            foreach (GroundItem gi in info.ItemSpawns) s.SpawnItem(gi.Kind, gi.Count, gi.Position);
            s.MatchConfig.Enabled = true;
            s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 2), 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
            s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 9), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            for (int t = 0; t < 90 * SimTime.TicksPerSecond; t++) s.Step();
            TestContext.WriteLine(s.Flags.Report());
            foreach (Flag f in s.Flags.Flags) TestContext.WriteLine(f.ToString());
            int hits = 0;
            foreach (PlayerFlagStats st in s.Flags.Players.Values) hits += st.Hits;
            Assert.That(hits, Is.GreaterThan(20), "they really fought");
            Assert.That(s.Flags.CountCategory(FlagCategory.Integrity), Is.Zero);
            Assert.That(s.Flags.CountCategory(FlagCategory.Interest), Is.Zero, "no hit on a player the shooter wasn't sent");
            Assert.That(s.Flags.CountCategory(FlagCategory.Aim), Is.Zero);
        }

        // ================================================================ lag compensation

        /// <summary>Shooter and strafing target 10 m apart on a flat field; the shooter aims at the target (by server truth) and holds fire.</summary>
        private static (NetHarness h, NetHarness.Peer shooter, NetHarness.Peer target) Duel(LinkConditions up, LinkConditions down)
        {
            var h = NetHarness.Flat();
            var shooter = h.AddClient(up, down, seed: 3);
            var target = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20), seed: 4, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 10));
            target.Script = t => new MoveInput { MoveX = (t / 60) % 2 == 0 ? 1 : -1, Yaw = Math.PI };
            shooter.Script = t =>
            {
                Vec3d from = shooter.ServerSide.State.Position, to = target.ServerSide.State.Position;
                return new MoveInput { Yaw = YawTo(from, to), FireHeld = true, WantsToShoot = true };
            };
            return (h, shooter, target);
        }

        [Test]
        public void HonestShooting_OverJitterAndLoss_IsNeverFlagged()
        {
            var (h, shooter, _) = Duel(NetHarness.Lag(60, 15, 5), NetHarness.Lag(60, 15, 5));
            h.RunSeconds(12);
            TestContext.WriteLine(h.Server.Flags.Report());
            Assert.That(h.Server.Flags.StatsOf(shooter.ServerSide.Id).Hits, Is.GreaterThan(5), "the shots land");
            Assert.That(h.Server.Flags.Count(FlagKind.ViewBehindAck) + h.Server.Flags.Count(FlagKind.ViewLagJump), Is.Zero);
            Assert.That(h.Server.Flags.Count(FlagKind.HitUnseen), Is.Zero);
        }

        [Test]
        public void Backtracking_ClaimingAnOlderViewOnShots_IsFlagged()
        {
            var (h, shooter, _) = Duel(NetHarness.Lag(30, 5, 0), NetHarness.Lag(30, 5, 0));
            var list = new List<InputCmd>();
            var w = new ByteWriter(1024);
            shooter.Tamper = data =>
            {
                if (Protocol.PeekType(data, 0, data.Length) != MsgType.Inputs) return data;
                var r = new ByteReader(data, 0, data.Length);
                InputsMsg m = Protocol.ReadInputs(ref r, list);
                for (int i = 0; i < list.Count; i++)
                {
                    InputCmd c = list[i];
                    if (c.Move.FireHeld) { c.ViewTick -= 30; list[i] = c; } // "I was seeing them 250 ms ago" on every shot
                }
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = m.AckSnapshotTick, Cmds = list });
                return w.ToArray();
            };
            // A few seconds honest first, so the server knows this player's usual view lag.
            shooter.Script = t => default;
            h.RunSeconds(2);
            var (_, _, target) = (h, shooter, h.Peers[1]);
            shooter.Script = t => new MoveInput { Yaw = YawTo(shooter.ServerSide.State.Position, target.ServerSide.State.Position), FireHeld = true, WantsToShoot = true };
            h.RunSeconds(6);
            TestContext.WriteLine(h.Server.Flags.Report());
            Assert.That(h.Server.Flags.Count(FlagKind.ViewBehindAck), Is.GreaterThan(0));
        }

        // ================================================================ interest management

        [Test]
        public void HiddenQuietEnemies_AreNotSent_HeardSeenCloseOrPingedOnesAre()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            bool shoot = false;
            ServerPlayer a = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 1, info.TeamSpawns[0][0], 0);
            ServerPlayer b = s.AddBot(new Brain { F = (sv, me) => { var c = Cmd(sv); c.Move.FireHeld = shoot; return c; } }, 2, info.TeamSpawns[1][0], 0);
            ServerPlayer mate = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 1, info.TeamSpawns[1][1], 0);
            for (int i = 0; i < 60; i++) s.Step();
            Assert.That(s.IsSentTo(a.Id, b.Id), Is.False, "standing still behind the divide: not sent");
            Assert.That(s.IsSentTo(b.Id, a.Id), Is.False);
            Assert.That(s.IsSentTo(a.Id, mate.Id), Is.True, "teammates always are");
            shoot = true;
            for (int i = 0; i < 30; i++) s.Step();
            Assert.That(s.IsSentTo(a.Id, b.Id), Is.True, "a shot is heard across the map (its tracer goes to everyone anyway)");
            shoot = false;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(s.IsSentTo(a.Id, b.Id), Is.False, "and a second after the last shot they are gone again");
        }

        [Test]
        public void InTheOpen_EnemiesAreSent_AndTheClientDrawsThem_WithNoBaseMisses()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(NetHarness.Lag(40, 10, 5), NetHarness.Lag(40, 10, 5));
            var b = h.AddClient(NetHarness.Lag(40, 10, 5), NetHarness.Lag(40, 10, 5), seed: 2, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 12));
            h.RunSeconds(1);
            Assert.That(a.Client.TryGetRemote(b.ServerSide.Id, out _), Is.True);
            Assert.That(b.Client.TryGetRemote(a.ServerSide.Id, out _), Is.True);
            Assert.That(a.Client.SnapshotBaseMisses + b.Client.SnapshotBaseMisses, Is.Zero);
        }

        [Test]
        public void ClientsOnTheDuelMap_DontReceiveAHiddenEnemy_ThenDoOnceTheyMakeNoise()
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0]);
            var a = h.AddClient(NetHarness.Lag(30), NetHarness.Lag(30));
            var b = h.AddClient(NetHarness.Lag(30), NetHarness.Lag(30), seed: 2, team: 2, spawn: info.TeamSpawns[1][0]);
            h.RunSeconds(1);
            Assert.That(a.Client.TryGetRemote(b.ServerSide.Id, out _), Is.False, "a wallhack has nothing to draw");
            b.Script = t => new MoveInput { FireHeld = true };
            h.RunSeconds(0.3);
            Assert.That(a.Client.TryGetRemote(b.ServerSide.Id, out _), Is.True);
            b.Script = t => default;
            h.RunSeconds(2);
            Assert.That(a.Client.TryGetRemote(b.ServerSide.Id, out _), Is.False);
            Assert.That(a.Client.SnapshotBaseMisses, Is.Zero, "leaving players out never breaks the delta chain");
        }

        /// <summary>Crouch-walks a wobbly circle (quiet: heard only within ~9 m), so sight decides who is sent.</summary>
        private sealed class Prowler : IBotBrain
        {
            private readonly int _k;
            public Prowler(int k) { _k = k; }
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me)
            {
                int t = s.Tick;
                var c = new InputCmd { Tick = t, ViewTick = t };
                c.Move.MoveZ = 1;
                c.Move.MoveX = Math.Sin(t * 0.004 + _k);
                c.Move.CrouchHeld = (_k % 3) != 0;
                c.Move.Yaw = Math.IEEERemainder(t * 0.006 * (1 + _k % 4) + _k, 2 * Math.PI);
                return InputCmd.Quantize(c);
            }
        }

        /// <summary>
        /// The fairness rule: nobody pops in. Twelve quiet players wander the duel map's buildings; whenever one can
        /// really see another (a ray from the eye to their head, chest or knees), that player must already have been in
        /// the viewer's snapshots 15 ticks earlier (125 ms: a 100 ms trip to the client plus the 25 ms it draws others
        /// behind). Counted over 20 s; it must never fail.
        /// </summary>
        [Test]
        public void NobodyPopsIn_SeenPlayersWereSentWellBeforeTheyCameIntoView()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            var bots = new List<ServerPlayer>();
            var rng = new Random(3);
            var box = new Vec3d(0.4, 0, 0.4);
            while (bots.Count < 12)
            {
                // Scattered over the middle of the map, on the ground or a ground floor, inside buildings and out.
                var at = new Vec3d(18 + rng.NextDouble() * 60, 0.02, 18 + rng.NextDouble() * 60);
                if (s.Grid.BoxOverlapsSolid(new Aabb(at - box, at + box + new Vec3d(0, 1.8, 0)))) continue;
                int i = bots.Count;
                bots.Add(s.AddBot(new Prowler(i), 1 + i % 2, at, 0));
            }
            const int lead = 15;
            var sent = new Dictionary<long, int[]>(); // pair -> ring of ticks it was sent
            int seenChecks = 0, failures = 0, firstFail = -1;
            int n = 20 * SimTime.TicksPerSecond;
            for (int t = 0; t < n; t++)
            {
                s.Step();
                foreach (ServerPlayer v in bots)
                    foreach (ServerPlayer o in bots)
                    {
                        if (v == o || v.Team == o.Team) continue;
                        long key = ((long)v.Id << 32) | (uint)o.Id;
                        if (!sent.TryGetValue(key, out int[] ring)) sent[key] = ring = new int[64];
                        if (s.IsSentTo(v.Id, o.Id)) ring[s.Tick & 63] = s.Tick;
                        if (t < lead + 5 || t % 2 != 0) continue;
                        Vec3d d = o.State.Position - v.State.Position;
                        if (d.LengthSquared > 70 * 70) continue;
                        Vec3d eye = v.State.Position + new Vec3d(0, PlayerMovement.EyeOf(v.State, s.Movement), 0);
                        if (!DuelBot.CanSee(s.Grid, eye, o.State.Position, PlayerMovement.HeightOf(o.State, s.Movement))) continue;
                        seenChecks++;
                        int back = s.Tick - lead;
                        if (ring[back & 63] != back)
                        {
                            failures++;
                            if (firstFail < 0) firstFail = s.Tick;
                        }
                    }
            }
            TestContext.WriteLine($"{seenChecks} in-sight checks, {failures} not sent {lead} ticks before (first at tick {firstFail}); {s.InterestTests} pair tests");
            Assert.That(seenChecks, Is.GreaterThan(500), "they did see each other a lot");
            Assert.That(failures, Is.Zero);
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>
        /// The sight grid is only ever asked about lines between players' eyes, chests and knees, which are at least a
        /// player's half width (0.35 m) from any wall. From such points it must never block a line a bullet ray gets
        /// through, except one that grazes an edge within half a cell (it errs the other way only there, and the 0.3 s
        /// look-ahead and the six points per player cover that; see the pop-in test).
        /// </summary>
        [Test]
        public void TheSightGrid_NeverBlocksALineARayGetsThrough_ExceptGrazingAnEdge()
        {
            ITerrain t = DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _));
            var g = new Sculpt.Core.Audio.SoundGrid(t);
            Assert.That(g.Supported);
            g.Build();
            var rng = new Random(7);
            var clearance = new Vec3d(0.3, 0.3, 0.3);
            int clear = 0, blockedByGrid = 0, grazing = 0;
            for (int i = 0; i < 60000; i++)
            {
                var a = new Vec3d(rng.NextDouble() * 96, 0.3 + rng.NextDouble() * 10, rng.NextDouble() * 96);
                var b = a + new Vec3d(rng.NextDouble() * 40 - 20, rng.NextDouble() * 4 - 2, rng.NextDouble() * 40 - 20);
                if (t.BoxOverlapsSolid(new Aabb(a - clearance, a + clearance)) || t.BoxOverlapsSolid(new Aabb(b - clearance, b + clearance))) continue;
                Vec3d d = b - a;
                double len = d.Length;
                TerrainHit hit = t.Raycast(a, d / len, len);
                if (hit.Hit && hit.Distance < len - 0.05) continue;
                clear++;
                if (g.LineOpen(a, b)) continue;
                blockedByGrid++;
                // Grazing: the same ray moved up to 0.35 m sideways (any of eight directions round it) is blocked.
                bool graze = false;
                for (double m = 0.05; m <= 0.35 + 1e-9 && !graze; m += 0.05)
                {
                    Vec3d side = new Vec3d(-d.Z, 0, d.X).Normalized * m, up = Cross(d / len, side.Normalized) * m;
                    Vec3d q1 = (side + up) * 0.7071, q2 = (side - up) * 0.7071;
                    foreach (Vec3d off in new[] { side, -side, up, -up, q1, -q1, q2, -q2 })
                    {
                        TerrainHit h2 = t.Raycast(a + off, d / len, len);
                        if (h2.Hit && h2.Distance < len - 0.05) graze = true;
                    }
                }
                if (graze) grazing++;
                else TestContext.WriteLine($"  not grazing: {a} -> {b}");
            }
            TestContext.WriteLine($"{clear} clear rays, {blockedByGrid} blocked by the grid, {grazing} of those grazing within 0.35 m of something");
            Assert.That(clear, Is.GreaterThan(5000));
            Assert.That(blockedByGrid, Is.EqualTo(grazing), "the grid only ever disagrees on rays that graze an edge");
            Assert.That(blockedByGrid, Is.LessThan(clear / 100));
        }

        // ================================================================ flag log and replays

        [Test]
        public void FlagLog_RateLimitsPerKind_CountsEverything_AndRoundTrips()
        {
            var log = new FlagLog { RateEveryTicks = 120 };
            for (int t = 0; t < 600; t++) log.Add(1000 + t, 3, FlagKind.AimSnap, t, 4);
            log.Add(1000, 5, FlagKind.Teleport, 250);
            Assert.That(log.StatsOf(3).Count(FlagKind.AimSnap), Is.EqualTo(600));
            Assert.That(log.Flags.FindAll(f => f.Kind == FlagKind.AimSnap).Count, Is.EqualTo(5));
            Assert.That(log.Suppressed, Is.EqualTo(595));
            var w = new ByteWriter(64);
            foreach (Flag f in log.Flags)
            {
                w.Clear();
                FlagLog.Write(w, f);
                var r = new ByteReader(w.Buffer, 0, w.Length);
                Assert.That(FlagLog.Read(ref r).Equals(f), Is.True);
            }
            Assert.That(FlagLog.FromSimulation(FlagKind.AimSnap) && !FlagLog.FromSimulation(FlagKind.Malformed));
            Assert.That(log.Report(), Does.Contain("AimSnap x600"));
        }

        /// <summary>Faces away from a still target, then turns 40 degrees onto it in one tick and fires, and holds dead still: an aimbot's snap.</summary>
        private static Brain Snapper(Func<ServerPlayer> target, int every)
        {
            return new Brain
            {
                F = (s, me) =>
                {
                    int phase = s.Tick % every;
                    double onTarget = YawTo(me.State.Position, target().State.Position);
                    var c = Cmd(s, phase < every - 30 ? onTarget + 40 * Math.PI / 180 : onTarget);
                    c.Pitch = 4;
                    c.Move.FirePressed = phase == every - 30;
                    c.Move.FireHeld = c.Move.FirePressed;
                    return c;
                },
            };
        }

        private static NetServer SnapServer()
        {
            var s = new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            return s;
        }

        [Test]
        public void AnAimSnap_IsFlagged_RecordedInTheReplay_AndTheReRunReproducesIt()
        {
            NetServer s = SnapServer();
            var ms = new MemoryStream();
            MatchStream stream = s.StartStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "Field", Mode = "test", Build = "tests" }, stream);
            ServerPlayer target = null;
            s.AddBot(Snapper(() => target, 240), 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            writer.Finish();
            TestContext.WriteLine(s.Flags.Report());
            int snaps = s.Flags.Count(FlagKind.AimSnap);
            Assert.That(snaps, Is.GreaterThanOrEqualTo(3), "each flick-and-freeze onto the target is flagged");

            ReplayFile f = ReplayFile.Read(ms.ToArray());
            Assert.That(f.Flags.FindAll(x => x.Kind == FlagKind.AimSnap).Count, Is.EqualTo(snaps));
            ReplayCheck check = ReplayVerifier.Run(f, SnapServer);
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Passed, check.ToString());
            Assert.That(check.FlagsChecked, Is.EqualTo(snaps));
        }

        [Test]
        public void AFlickByHandThatKeepsMoving_IsNotASnap()
        {
            NetServer s = SnapServer();
            ServerPlayer target = null;
            // Same flick, but the view keeps drifting 1 degree a tick afterwards, like a hand settling.
            int every = 240;
            s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    int phase = sv.Tick % every;
                    double on = YawTo(me.State.Position, target.State.Position);
                    double drift = phase >= every - 30 ? (phase - (every - 30)) * Math.PI / 180 : 0;
                    var c = Cmd(sv, phase < every - 30 ? on + 40 * Math.PI / 180 : on + drift);
                    c.Pitch = 4;
                    c.Move.FirePressed = phase == every - 30;
                    c.Move.FireHeld = c.Move.FirePressed;
                    return c;
                },
            }, 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            Assert.That(s.Flags.StatsOf(1).Hits, Is.GreaterThan(0));
            Assert.That(s.Flags.Count(FlagKind.AimSnap), Is.Zero);
        }
    }
}
