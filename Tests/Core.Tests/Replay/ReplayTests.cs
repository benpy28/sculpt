using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Tests.Replay
{
    /// <summary>
    /// Replays and spectating: the snapshot delta coder, the file (whole and cut short), playback against what the
    /// server really did (positions, health, walls, items, scores, kills) including seeks both ways, the re-run check
    /// that proves a match from its inputs, and live spectators a delay behind.
    /// </summary>
    public class ReplayTests
    {
        // ------------------------------------------------------------------ helpers

        private static BotNav _nav;
        private static BotNav Nav() => _nav ??= DuelMap.BuildNav();

        private sealed class Rig
        {
            public NetServer Server;
            public BreachMapInfo Info;
            public readonly Dictionary<int, Action<byte[], bool>> Links = new Dictionary<int, Action<byte[], bool>>();
            /// <summary>Server state at the end of each tick: player id -> (position, health, dead).</summary>
            public readonly Dictionary<int, Dictionary<int, (Vec3d pos, int health, bool dead)>> History = new Dictionary<int, Dictionary<int, (Vec3d, int, bool)>>();
            public readonly Dictionary<int, TileState[]> Walls = new Dictionary<int, TileState[]>();
            public int WallEvery = 6 * SimTime.TicksPerSecond;

            public void Step(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    Server.Step();
                    Capture();
                }
            }

            /// <summary>Remembers the server as it is now (also before the first tick: key frames show that).</summary>
            public void Capture()
            {
                {
                    var now = new Dictionary<int, (Vec3d, int, bool)>();
                    foreach (ServerPlayer p in Server.Players) now[p.Id] = (p.State.Position, Math.Max(0, p.State.Health(Server.Movement.Weapons)), p.State.Dead);
                    History[Server.Tick] = now;
                    if (Server.Tick % WallEvery == 0) Walls[Server.Tick] = WallStates(Server.Walls);
                }
            }
        }

        private static TileState[] WallStates(TileWalls w)
        {
            var a = new TileState[w.Count];
            for (int i = 0; i < w.Count; i++) a[i] = w[i].State;
            return a;
        }

        private static NetServer NewDuelServer(out BreachMapInfo info, NetServer.SendFn send, Action<MovementConfig> tweak = null)
        {
            SubGrid g = DuelMap.BuildGround();
            var mc = new MovementConfig();
            tweak?.Invoke(mc);
            var s = new NetServer(DuelMap.Terrain(g, DuelMap.BuildBrushes(out info)), mc, new NetConfig(), send);
            s.Sculpt = new SculptSystem(g, new SculptConfig());
            return s;
        }

        private static void SpawnItems(NetServer s, BreachMapInfo info)
        {
            foreach (GroundItem gi in info.ItemSpawns) s.SpawnItem(gi.Kind, gi.Count, gi.Position);
        }

        private static NetClient NewDuelClient(NetClient.SendFn send = null)
        {
            SubGrid g = DuelMap.BuildGround();
            var mc = new MovementConfig();
            return new NetClient(DuelMap.Terrain(g, DuelMap.BuildBrushes(out _)), mc, new NetConfig(), send ?? ((d, r) => { }), new SculptClient(g, new SculptConfig(), mc));
        }

        /// <summary>Two duel bots playing the match on the duel map, recorded from before anyone joined.</summary>
        private static Rig RecordBotMatch(double seconds, out byte[] file, int seedA = 5, int seedB = 4, ReplayConfig cfg = null, Action<NetServer, int> during = null, Action<NetServer> before = null)
        {
            var rig = new Rig();
            rig.Server = NewDuelServer(out rig.Info, (c, d, r) => { if (rig.Links.TryGetValue(c, out var l)) l(d, r); });
            SpawnItems(rig.Server, rig.Info);
            rig.Server.MatchConfig.Enabled = true;
            rig.Server.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
            var ms = new MemoryStream();
            MatchStream stream = rig.Server.StartStream(cfg);
            rig.Capture();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "Duel", Mode = "test", Build = "tests", StartedUtcTicks = 1 }, stream);
            before?.Invoke(rig.Server);
            rig.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), seedA), 1, rig.Info.TeamSpawns[0][0], rig.Info.SpawnYaw[0]);
            rig.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), seedB), 2, rig.Info.TeamSpawns[1][0], rig.Info.SpawnYaw[1]);
            int n = (int)(seconds * SimTime.TicksPerSecond);
            for (int k = 0; k < n; k++)
            {
                during?.Invoke(rig.Server, k);
                rig.Step(1);
            }
            writer.Finish();
            file = ms.ToArray();
            return rig;
        }

        private static Rig _match;
        private static byte[] _matchFile;

        /// <summary>One 75 s recorded bot match shared by the playback tests (recording it takes a few seconds).</summary>
        private static Rig Match(out byte[] file)
        {
            if (_match == null) _match = RecordBotMatch(75, out _matchFile);
            file = _matchFile;
            return _match;
        }

        // ------------------------------------------------------------------ codec

        [Test]
        public void Varints_RoundTrip_AtTheEdges()
        {
            long[] signed = { 0, 1, -1, 63, -64, 64, -65, 8191, -8192, int.MaxValue, int.MinValue, long.MaxValue, long.MinValue };
            ulong[] unsigned = { 0, 1, 127, 128, 16383, 16384, uint.MaxValue, ulong.MaxValue };
            var w = new ByteWriter();
            foreach (long v in signed) w.VarS(v);
            foreach (ulong v in unsigned) w.VarU(v);
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            foreach (long v in signed) Assert.That(r.VarS(), Is.EqualTo(v));
            foreach (ulong v in unsigned) Assert.That(r.VarU(), Is.EqualTo(v));
            Assert.That(r.Remaining, Is.Zero);
            var one = new ByteWriter();
            one.VarS(-1); one.VarS(1); one.VarU(127);
            Assert.That(one.Length, Is.EqualTo(3), "small numbers take one byte each");
        }

        private static SpecPlayer RandomPlayer(Random rng, int id)
        {
            var p = new SpecPlayer
            {
                Id = id,
                Team = 1 + rng.Next(2),
                Bot = rng.Next(2) == 0,
                Position = new Vec3d(rng.NextDouble() * 400 - 200, rng.NextDouble() * 40 - 5, rng.NextDouble() * 400 - 200),
                Velocity = new Vec3d(rng.NextDouble() * 30 - 15, rng.NextDouble() * 30 - 15, rng.NextDouble() * 30 - 15),
                Yaw = (float)(rng.NextDouble() * 2 * Math.PI - Math.PI),
                Pitch = (float)(rng.NextDouble() * 178 - 89),
                Mode = (MoveMode)rng.Next(5),
                Crouched = rng.Next(2) == 0,
                Dead = rng.Next(5) == 0,
                Sprinting = rng.Next(2) == 0,
                Reloading = rng.Next(3) == 0,
                Held = (Item)rng.Next(8),
                Hand = (HandMode)rng.Next(6),
                Slot = (byte)rng.Next(5),
                Health = rng.Next(151),
                Bar = rng.Next(10001),
                Ammo = rng.Next(40),
            };
            for (int k = 0; k < Items.Slots; k++) { p.Bag[k] = (Item)rng.Next(8); p.Bag.SetCount(k, rng.Next(256)); }
            return p;
        }

        private static void AssertClose(in SpecPlayer want, in SpecPlayer got)
        {
            double pe = 0.5 / SpecSnapCodec.PosScale + 1e-9, ve = 0.5 / SpecSnapCodec.VelScale + 1e-9;
            Assert.That(got.Id, Is.EqualTo(want.Id));
            Assert.That(got.Team, Is.EqualTo(want.Team));
            Assert.That(Math.Abs(got.Position.X - want.Position.X), Is.LessThanOrEqualTo(pe));
            Assert.That(Math.Abs(got.Position.Y - want.Position.Y), Is.LessThanOrEqualTo(pe));
            Assert.That(Math.Abs(got.Position.Z - want.Position.Z), Is.LessThanOrEqualTo(pe));
            Assert.That(Math.Abs(got.Velocity.X - want.Velocity.X), Is.LessThanOrEqualTo(ve));
            Assert.That(Math.Abs(got.Velocity.Z - want.Velocity.Z), Is.LessThanOrEqualTo(ve));
            double dy = Math.Abs(Math.IEEERemainder(got.Yaw - want.Yaw, 2 * Math.PI));
            Assert.That(dy, Is.LessThanOrEqualTo(Math.PI / 65536 + 1e-6));
            Assert.That(Math.Abs(got.Pitch - want.Pitch), Is.LessThanOrEqualTo(0.005 + 1e-4));
            Assert.That(got.Mode, Is.EqualTo(want.Mode));
            Assert.That((got.Crouched, got.Dead, got.Sprinting, got.Reloading, got.Bot), Is.EqualTo((want.Crouched, want.Dead, want.Sprinting, want.Reloading, want.Bot)));
            Assert.That((got.Held, got.Hand, got.Slot), Is.EqualTo((want.Held, want.Hand, want.Slot)));
            Assert.That((got.Health, got.Bar, got.Ammo), Is.EqualTo((want.Health, want.Bar, want.Ammo)));
            Assert.That(got.Bag, Is.EqualTo(want.Bag));
        }

        [Test]
        public void SnapshotCoder_SeededRandomRuns_DecodeToTheSameState_KeysRestart_PlayersComeAndGo()
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                var rng = new Random(seed);
                var enc = new SpecSnapCodec();
                var dec = new SpecSnapCodec();
                var players = new List<SpecPlayer>();
                for (int i = 0; i < 1 + rng.Next(12); i++) players.Add(RandomPlayer(rng, 1 + i * 3));
                var got = new List<SpecPlayer>();
                for (int step = 0; step < 200; step++)
                {
                    // Mostly small moves (what deltas are for), now and then a big jump, a respawn or a change of everything.
                    for (int i = 0; i < players.Count; i++)
                    {
                        SpecPlayer p = players[i];
                        int roll = rng.Next(20);
                        if (roll == 0) { int id = p.Id, team = p.Team; p = RandomPlayer(rng, id); p.Team = rng.Next(4) == 0 ? 3 - team : team; }
                        else if (roll < 15)
                        {
                            p.Position += new Vec3d(rng.NextDouble() * 0.2 - 0.1, rng.NextDouble() * 0.05, rng.NextDouble() * 0.2 - 0.1);
                            p.Yaw = (float)Math.IEEERemainder(p.Yaw + rng.NextDouble() * 0.4 - 0.2, 2 * Math.PI);
                        }
                        players[i] = p;
                    }
                    if (rng.Next(15) == 0 && players.Count > 1) players.RemoveAt(rng.Next(players.Count));
                    if (rng.Next(15) == 0) players.Add(RandomPlayer(rng, 100 + step));
                    bool key = step == 0 || rng.Next(40) == 0;
                    byte[] snap = enc.Encode(1000 + step, key, players);
                    var r = new ByteReader(snap, 0, snap.Length);
                    int tick = dec.Decode(ref r, got, out bool gotKey);
                    Assert.That(tick, Is.EqualTo(1000 + step));
                    Assert.That(gotKey, Is.EqualTo(key));
                    Assert.That(r.Remaining, Is.Zero);
                    var want = new List<SpecPlayer>(players);
                    want.Sort((a, b) => a.Id.CompareTo(b.Id));
                    Assert.That(got.Count, Is.EqualTo(want.Count), $"seed {seed} step {step}");
                    for (int i = 0; i < want.Count; i++) AssertClose(want[i], got[i]);
                }
            }
        }

        [Test]
        public void SnapshotCoder_StillPlayersCostTwoBytes_AndADeltaWithoutItsKeyIsRefused()
        {
            var rng = new Random(3);
            var players = new List<SpecPlayer>();
            for (int i = 0; i < 32; i++) players.Add(RandomPlayer(rng, i + 1));
            var enc = new SpecSnapCodec();
            byte[] key = enc.Encode(10, true, players);
            byte[] still = enc.Encode(11, false, players);
            TestContext.WriteLine($"32 players: key {key.Length} bytes, nobody moved {still.Length} bytes");
            Assert.That(still.Length, Is.LessThanOrEqualTo(8 + 32 * 2), "id + empty mask per player");
            Assert.That(key.Length, Is.LessThan(32 * 40));

            var late = new SpecSnapCodec();
            var r = new ByteReader(still, 0, still.Length);
            Assert.Throws<NetFormatException>(() => late.Decode(ref r, new List<SpecPlayer>(), out _), "joined mid-run: needs a key frame");
        }

        [Test]
        public void InputLog_SeededRandomCommands_DecodeToTheSameWireBytes()
        {
            var rng = new Random(11);
            var enc = new InputLogCodec();
            var w = new ByteWriter();
            var sent = new List<(int id, InputCmd c)>();
            var yaw = new double[4];
            for (int t = 0; t < 3000; t++)
            {
                int id = 1 + rng.Next(4);
                yaw[id - 1] += rng.NextDouble() * 0.05 - 0.025;
                var c = new InputCmd { Tick = 5000 + t, ViewTick = 5000 + t - rng.Next(8), ViewFrac = (byte)rng.Next(256), Pitch = (float)(rng.NextDouble() * 60 - 30) };
                c.Move.Yaw = yaw[id - 1];
                c.Move.MoveX = rng.Next(3) - 1;
                c.Move.MoveZ = rng.NextDouble();
                c.Move.SprintHeld = rng.Next(2) == 0;
                c.Move.FireHeld = rng.Next(3) == 0;
                c.Move.JumpPressed = rng.Next(30) == 0;
                if (rng.Next(20) == 0) c.Hand.WeaponKey = (byte)(1 + rng.Next(5));
                if (rng.Next(40) == 0) { c.Hand.Heal = true; c.Hand.HealTile = rng.Next(3000); c.Hand.HealCell = rng.Next(64); c.Hand.HealReinforce = rng.Next(2) == 0; }
                if (rng.Next(50) == 0) { c.Hand.Pickup = true; c.Hand.PickId = rng.Next(100); }
                if (rng.Next(50) == 0) { c.Hand.Door = true; c.Hand.DoorTile = rng.Next(3000); c.Hand.DoorOpen = true; }
                c = InputCmd.Quantize(c);
                enc.Write(w, id, c);
                sent.Add((id, c));
            }
            TestContext.WriteLine($"3000 commands: {w.Length} bytes ({w.Length / 3000.0:0.0} per command, 14+ raw)");
            var dec = new InputLogCodec();
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            foreach (var (id, c) in sent)
            {
                InputCmd got = dec.Read(ref r, c.Tick, out int gotId);
                Assert.That(gotId, Is.EqualTo(id));
                var a = new ByteWriter(); c.Write(a);
                var b = new ByteWriter(); got.Write(b);
                Assert.That(b.ToArray(), Is.EqualTo(a.ToArray()));
                Assert.That(got.ViewTick, Is.EqualTo(c.ViewTick));
            }
            Assert.That(r.Remaining, Is.Zero);
        }

        [Test]
        public void Welcome_FromAnotherBuild_ReadsOnlyItsVersion_AndHelloRoundTrips()
        {
            var w = new ByteWriter();
            w.U8((byte)MsgType.Welcome);
            w.U8((byte)(Protocol.Version + 1));
            w.U8(7); // whatever another build puts next
            var r = new ByteReader(w.ToArray(), 0, w.Length);
            WelcomeMsg m = Protocol.ReadWelcome(ref r);
            Assert.That(m.ProtocolVersion, Is.EqualTo(Protocol.Version + 1));

            byte[] hello = SpecProtocol.Hello(true);
            Assert.That(SpecProtocol.TryReadHello(hello, 0, hello.Length, out byte v, out bool spec));
            Assert.That(v, Is.EqualTo(Protocol.Version));
            Assert.That(spec, Is.True);
            Assert.That(SpecProtocol.TryReadHello(new byte[] { (byte)MsgType.Inputs, 0, 0 }, 0, 3, out _, out _), Is.False);
        }

        // ------------------------------------------------------------------ file and playback

        [Test]
        public void RecordedBotMatch_ReadsBack_WithItsKillsAndAReasonableSize()
        {
            Rig rig = Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            int kills = 0;
            foreach (ServerPlayer p in rig.Server.Players) kills += p.Kills;
            TestContext.WriteLine($"75 s 1v1: {file.Length / 1024.0:0.0} KB, {f.Segments.Count} segments, {f.Markers.Count} kill markers, server kills {kills}");
            Assert.That(f.Complete);
            Assert.That(f.Incompatible, Is.Null);
            Assert.That(f.Header.Map, Is.EqualTo("Duel"));
            Assert.That(f.Segments.Count, Is.EqualTo(8), "75 s in 10 s segments");
            Assert.That(f.FirstTick, Is.EqualTo(1001));
            Assert.That(f.LastTick, Is.EqualTo(rig.Server.Tick));
            Assert.That(kills, Is.GreaterThan(0), "the bots fought (seeds 5/4)");
            Assert.That(f.Markers.Count, Is.EqualTo(kills), "every kill is a marker");
            Assert.That(file.Length, Is.LessThan(1024 * 1024), "a 1v1 minute with inputs stays well under a megabyte");
        }

        [Test]
        public void Playback_ShowsWhatTheServerDid_EveryPlayerEverySnapshot_ThenWallsItemsAndScoresAtTheEnd()
        {
            Rig rig = Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            var player = new ReplayPlayer(f, () => NewDuelClient());
            int checkedSnaps = 0, lastChecked = int.MinValue, kills = 0;
            double pe = 0.5 / SpecSnapCodec.PosScale + 1e-9;
            while (!player.AtEnd)
            {
                player.Advance(1.0 / 144.0);
                NetClient c = player.Client;
                foreach (var kv in c.Combat) if (kv.Value.Type == CombatEventType.Kill) kills++;
                c.Combat.Clear();
                int t = c.LastSnapshotTick;
                if (t == lastChecked) continue;
                lastChecked = t;
                Assert.That(t, Is.LessThanOrEqualTo(player.Time + 1e-9), "never shows the future");
                var want = rig.History[t];
                Assert.That(c.SpectatedIds.Count, Is.EqualTo(want.Count));
                foreach (int id in c.SpectatedIds)
                {
                    Assert.That(c.TryGetSpectated(id, out SpecPlayer sp));
                    var w = want[id];
                    Assert.That(Math.Abs(sp.Position.X - w.pos.X), Is.LessThanOrEqualTo(pe), $"tick {t} player {id}");
                    Assert.That(Math.Abs(sp.Position.Y - w.pos.Y), Is.LessThanOrEqualTo(pe));
                    Assert.That(Math.Abs(sp.Position.Z - w.pos.Z), Is.LessThanOrEqualTo(pe));
                    Assert.That(sp.Health, Is.EqualTo(w.health), $"tick {t} player {id} health");
                    Assert.That(sp.Dead, Is.EqualTo(w.dead));
                }
                checkedSnaps++;
            }
            NetClient end = player.Client;
            Assert.That(checkedSnaps, Is.GreaterThan(75 * 60 - 200), "about every 60 Hz snapshot was seen at 144 fps");
            Assert.That(kills, Is.EqualTo(f.Markers.Count), "each kill is played once at 1x");
            Assert.That(WallStates(end.Walls), Is.EqualTo(WallStates(rig.Server.Walls)), "walls end as the server's");
            Assert.That(end.Items.Count, Is.EqualTo(rig.Server.Items.Count), "items on the ground");
            Assert.That(end.Match.Phase, Is.EqualTo(rig.Server.Match.Phase));
            foreach (MatchScore s in rig.Server.Match.Scores)
                Assert.That(end.Match.Scores.Exists(x => x.PlayerId == s.PlayerId && x.Kills == s.Kills && x.Deaths == s.Deaths), $"score of {s.PlayerId}");
            Assert.That(end.DroppedPackets, Is.Zero);
        }

        [Test]
        public void Seeking_BackForwardAndFar_LandsOnTheServersWalls_AndShowsNoOldEffects()
        {
            Rig rig = Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            int changes = 0;
            var player = new ReplayPlayer(f, () => NewDuelClient()) { FarJumpTicks = 30 * SimTime.TicksPerSecond };
            player.ClientChanged += () => changes++;
            var order = new List<int>(rig.Walls.Keys);
            order.Sort();
            Assert.That(order.Count, Is.GreaterThanOrEqualTo(10));
            // Forward a little, far forward, back to the start, back a little, forward again.
            int[] visit = { order[1], order[2], order[9], order[0], order[8], order[5], order[6], order[order.Count - 1] };
            foreach (int t in visit)
            {
                int before = player.Rebuilds;
                player.Seek(t);
                Assert.That(player.Client.Combat, Is.Empty, "a seek doesn't replay the shots it skipped");
                Assert.That(WallStates(player.Client.Walls), Is.EqualTo(rig.Walls[t]), $"walls at tick {t}");
                player.Client.TryGetSpectated(player.Client.SpectatedIds[0], out SpecPlayer sp);
                int snapTick = player.Client.LastSnapshotTick;
                Assert.That(snapTick, Is.InRange(t - 2, t));
                Assert.That((sp.Position - rig.History[snapTick][sp.Id].pos).Length, Is.LessThan(0.002));
            }
            TestContext.WriteLine($"{player.Seeks} seeks, {player.Rebuilds} rebuilds (incl. the first), {changes} client swaps");
            Assert.That(player.Rebuilds, Is.LessThan(1 + visit.Length), "short forward seeks reuse the client");
            Assert.That(changes, Is.EqualTo(player.Rebuilds - 1));
        }

        [Test]
        public void JumpToKill_LandsAFewSecondsBefore_AndPlaysTheKill()
        {
            Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            Assume.That(f.Markers.Count, Is.GreaterThan(0));
            var player = new ReplayPlayer(f, () => NewDuelClient());
            int k = player.NextKill();
            Assert.That(k, Is.EqualTo(0));
            player.JumpToKill(k);
            Assert.That(player.Time, Is.EqualTo(Math.Max(player.StartTick, f.Markers[0].Tick - 3.0 * SimTime.TicksPerSecond)).Within(1e-6));
            bool seen = false;
            for (int i = 0; i < 144 * 4 && !seen; i++)
            {
                player.Advance(1.0 / 144.0);
                foreach (var kv in player.Client.Combat)
                    if (kv.Value.Type == CombatEventType.Kill && kv.Value.Victim == f.Markers[0].Victim) seen = true;
                player.Client.Combat.Clear();
            }
            Assert.That(seen, "the kill plays within the lead-in");
            if (f.Markers.Count > 1) Assert.That(player.NextKill(), Is.EqualTo(1));
            Assert.That(player.PreviousKill(), Is.EqualTo(0));
        }

        [Test]
        public void FileCutShort_StillPlaysItsWholeSegments_AndFindsTheirKills()
        {
            Match(out byte[] file);
            ReplayFile whole = ReplayFile.Read(file);
            // Chop off the footer and half of the last segment (a crash while recording).
            ReplayFile.SegmentInfo last = whole.Segments[whole.Segments.Count - 1];
            var cut = new byte[last.Offset + 40];
            Array.Copy(file, cut, cut.Length);
            ReplayFile f = ReplayFile.Read(cut);
            Assert.That(f.Complete, Is.False);
            Assert.That(f.Segments.Count, Is.EqualTo(whole.Segments.Count - 1));
            int kills = whole.Markers.FindAll(m => m.Tick <= f.LastTick).Count;
            Assert.That(f.Markers.Count, Is.EqualTo(kills), "kills found by scanning the segments");
            var player = new ReplayPlayer(f, () => NewDuelClient());
            player.Seek(f.LastTick);
            Assert.That(player.AtEnd);
            Assert.That(player.Client.DroppedPackets, Is.Zero);

            Assert.Throws<NetFormatException>(() => ReplayFile.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        [Test]
        public void RecordingNeverChangesTheMatch()
        {
            // Same seeds with and without a stream: identical server state every tick.
            var a = new Rig();
            a.Server = NewDuelServer(out a.Info, (c, d, r) => { });
            var b = new Rig();
            b.Server = NewDuelServer(out b.Info, (c, d, r) => { });
            b.Server.StartStream();
            foreach (Rig x in new[] { a, b })
            {
                SpawnItems(x.Server, x.Info);
                x.Server.MatchConfig.Enabled = true;
                x.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 2), 1, x.Info.TeamSpawns[0][0], x.Info.SpawnYaw[0]);
                x.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 9), 2, x.Info.TeamSpawns[1][0], x.Info.SpawnYaw[1]);
            }
            for (int t = 0; t < 20 * SimTime.TicksPerSecond; t++)
            {
                a.Server.Step();
                b.Server.Step();
                Assert.That(MatchStream.StateHash(b.Server), Is.EqualTo(MatchStream.StateHash(a.Server)), $"tick {a.Server.Tick}");
            }
        }

        [Test]
        public void StateHash_CoversCellHealthItemsScoresBarsAndTheMatch()
        {
            // The re-run check can only prove what the hash covers. Each of these is simulated server state a re-run
            // must reproduce; a difference in any one of them must change the hash.
            NetServer Fresh()
            {
                NetServer s = NewDuelServer(out BreachMapInfo info, (c, d, r) => { });
                SpawnItems(s, info);
                s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Easy), 1), 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
                s.Step();
                return s;
            }
            Assert.That(MatchStream.StateHash(Fresh()), Is.EqualTo(MatchStream.StateHash(Fresh())), "same state, same hash");

            int tile = -1;
            NetServer probe = Fresh();
            for (int i = 0; i < probe.Walls.Count && tile < 0; i++) if (probe.Walls.HasCells(i)) tile = i;
            Assert.That(tile, Is.GreaterThanOrEqualTo(0));
            byte[] Hp(int cell) { var h = new byte[Brush.CellCount]; for (int c = 0; c < h.Length; c++) h[c] = 100; h[cell] = 40; return h; }

            var changes = new Dictionary<string, Action<NetServer>>
            {
                ["the same damage on another cell"] = s => s.Walls.WriteCells(tile, 0, 0, 0, Hp(1), TileCause.Bullet),
                ["a ground item"] = s => s.SpawnItem(Item.Grenade, 1, new Vec3d(40, 0, 40)),
                ["a score"] = s => s.Players[0].MatchKills++,
                ["the kill credit"] = s => { s.Players[0].LastHurtBy = 7; s.Players[0].LastHurtTick = s.Tick; },
                ["a sculpt bar"] = s => s.BarOf(s.Players[0].Id).Spend(10, s.Tick),
                ["the match phase"] = s => s.Match.PhaseEndTick += 5,
                ["the storm"] = s => s.Match.Storm.ToRadius += 1,
            };
            foreach (var kv in changes)
            {
                NetServer a = Fresh(), b = Fresh();
                if (kv.Key.StartsWith("the same damage")) a.Walls.WriteCells(tile, 0, 0, 0, Hp(0), TileCause.Bullet);
                kv.Value(b);
                Assert.That(MatchStream.StateHash(b), Is.Not.EqualTo(MatchStream.StateHash(a)), kv.Key);
            }
        }

        // ------------------------------------------------------------------ re-run check (anti-cheat groundwork)

        [Test]
        public void ReRun_OfTheInputs_MatchesEveryStateHash()
        {
            Rig rig = Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                NetServer s = NewDuelServer(out BreachMapInfo info, (c, d, r) => { });
                SpawnItems(s, info);
                s.MatchConfig.Enabled = true;
                s.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
                return s;
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Passed, check.ToString());
            Assert.That(check.HashesChecked, Is.EqualTo(75 * 4));
            Assert.That(check.TicksRun, Is.EqualTo(rig.Server.Tick - 1000));
        }

        [Test]
        public void ReRun_AcrossAPlayerIdWrap_MatchesEveryStateHash()
        {
            // Ids are 1..65535 and wrap (skipping ids in use or recently left). A server that started near the top: the
            // bots are 65534 and 65535, then one leaves and a new bot joins as 1, then another as 2. The re-run starts
            // its ids where the recording did and must hand out exactly the same ones.
            var ids = new List<int>();
            RecordBotMatch(8, out byte[] file, before: s => s.NextPlayerId = 65534, during: (s, k) =>
            {
                if (k == 3 * SimTime.TicksPerSecond)
                {
                    ServerPlayer gone = s.Players[0];
                    s.RemovePlayer(gone.ConnectionId);
                    s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 8), gone.Team, gone.Spawn, gone.SpawnYaw);
                }
                if (k == 5 * SimTime.TicksPerSecond)
                    s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 9), 2, s.Players[0].Spawn, 0);
                foreach (ServerPlayer p in s.Players) if (!ids.Contains(p.Id)) ids.Add(p.Id);
            });
            Assert.That(ids, Is.EqualTo(new[] { 65534, 65535, 1, 2 }).Or.EqualTo(new[] { 65535, 65534, 1, 2 }));
            ReplayFile f = ReplayFile.Read(file);
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                NetServer s = NewDuelServer(out BreachMapInfo info, (c, d, r) => { });
                SpawnItems(s, info);
                s.MatchConfig.Enabled = true;
                s.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
                return s;
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Passed, check.ToString());
            Assert.That(check.HashesChecked, Is.EqualTo(8 * 4));
        }

        [Test]
        public void ReRun_WithDifferentRules_IsCaught()
        {
            Match(out byte[] file);
            ReplayFile f = ReplayFile.Read(file);
            // A "server" where sprinting is 1% faster: the match cannot come out the same.
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                NetServer s = NewDuelServer(out BreachMapInfo info, (c, d, r) => { }, mc => mc.SprintSpeed *= 1.01);
                SpawnItems(s, info);
                s.MatchConfig.Enabled = true;
                s.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
                return s;
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Problem, Is.Null);
            Assert.That(check.FirstMismatchTick, Is.GreaterThan(0), "sprinting 1% faster shows within the match");
        }

        // ------------------------------------------------------------------ live spectators

        [Test]
        public void LiveSpectator_WatchesTheDelayBehind_NeverSeesNewer_AndALateOneJoinsAtAKeyFrame()
        {
            const int delay = 3 * SimTime.TicksPerSecond;
            var clients = new Dictionary<int, NetClient>();
            var rig = new Rig();
            var links = new Dictionary<int, SimulatedLink>();
            double now = 0;
            rig.Server = NewDuelServer(out rig.Info, (c, d, r) => { if (links.TryGetValue(c, out var l)) l.Send(d, r, now); });
            rig.Server.Dedicated = true; // test the delay mechanism at 3 s; the 30 s listen-server floor is policy, not mechanism
            rig.Server.SpectatorDelayTicks = delay;
            SpawnItems(rig.Server, rig.Info);
            rig.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 5), 1, rig.Info.TeamSpawns[0][0], rig.Info.SpawnYaw[0]);
            rig.Server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 4), 2, rig.Info.TeamSpawns[1][0], rig.Info.SpawnYaw[1]);

            void AddSpectator(int conn)
            {
                links[conn] = new SimulatedLink(new LinkConditions { LatencyMs = 40, JitterMs = 10 }, conn);
                clients[conn] = NewDuelClient();
                rig.Server.AddSpectator(conn);
            }

            AddSpectator(1);
            rig.Capture();
            Assert.That(rig.Server.Stream, Is.Not.Null, "the first spectator starts the stream");
            int lateAt = 17 * SimTime.TicksPerSecond;
            int checks = 0;
            for (int t = 0; t < 30 * SimTime.TicksPerSecond; t++)
            {
                if (t == lateAt) AddSpectator(2);
                rig.Step(1);
                now += 1000.0 / SimTime.TicksPerSecond;
                foreach (var kv in links)
                {
                    NetClient c = clients[kv.Key];
                    kv.Value.Deliver(now, d => c.Receive(d, 0, d.Length));
                    c.UpdateVisual(1.0 / SimTime.TicksPerSecond);
                    if (c.SpectatorFrameTick != int.MinValue)
                        Assert.That(c.SpectatorFrameTick, Is.LessThanOrEqualTo(rig.Server.Tick - delay), "nothing newer than the delay ever arrives");
                    if (!c.SpectatorSynced) continue;
                    int st = c.LastSnapshotTick;
                    foreach (int id in c.SpectatedIds)
                    {
                        c.TryGetSpectated(id, out SpecPlayer sp);
                        Assert.That((sp.Position - rig.History[st][id].pos).Length, Is.LessThan(0.002));
                    }
                    checks++;
                }
            }
            NetClient early = clients[1], late = clients[2];
            Assert.That(early.IsSpectator && late.IsSpectator);
            Assert.That(early.Playing, Is.False);
            Assert.That(early.SpectatorDelayTicks, Is.EqualTo(delay));
            Assert.That(early.SpectatorSynced && late.SpectatorSynced, "both have the whole state");
            // Each link has its own +-10 ms jitter, so on any one tick the two can be a frame apart either way.
            int skew = (int)Math.Ceiling(2 * 10 / 1000.0 * SimTime.TicksPerSecond) + 2;
            Assert.That(late.SpectatorFrameTick, Is.InRange(early.SpectatorFrameTick - skew, early.SpectatorFrameTick + skew), "the late one caught up");
            Assert.That(rig.Server.Tick - early.SpectatorFrameTick, Is.InRange(delay, delay + SimTime.TicksPerSecond / 5), "about the delay behind (plus the link)");
            Assert.That(early.DroppedPackets + late.DroppedPackets, Is.Zero);
            Assert.That(WallStates(late.Walls), Is.EqualTo(WallStates(early.Walls)));
            Assert.That(rig.Server.Stream.Segments.Count, Is.LessThanOrEqualTo(3), "memory: only what a spectator still needs is kept");
            Assert.That(checks, Is.GreaterThan(1000));
            Assert.That(rig.Server.Players.Count, Is.EqualTo(2), "spectators are not players");
            rig.Server.RemovePlayer(1);
            Assert.That(rig.Server.IsSpectator(1), Is.False);
        }

        [Test]
        public void BattleRoyaleSize_32Players_StreamAndFileBudget()
        {
            // 32 scripted players running, turning and jumping around the duel map: what a spectator downloads and what a
            // replay costs per minute, before any interest management.
            var rig = new Rig();
            rig.Server = NewDuelServer(out rig.Info, (c, d, r) => { });
            var ms = new MemoryStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "Duel" }, rig.Server.StartStream(new ReplayConfig { RecordInputs = Environment.GetEnvironmentVariable("NOINPUTS") == null }));
            for (int i = 0; i < 32; i++)
            {
                List<Vec3d> spots = rig.Info.TeamSpawns[i % 2];
                rig.Server.AddBot(new Runner(i), 1 + i % 2, spots[i / 2 % spots.Count] + new Vec3d((i / 2) * 0.9, 0, 0), 0);
            }
            int seconds = 30;
            rig.Step(seconds * SimTime.TicksPerSecond);
            long frameBytes = 0;
            foreach (StreamSegment s in rig.Server.Stream.Segments) foreach (var fr in s.Frames) frameBytes += fr.Value.Length;
            writer.Finish();
            // Segments in memory hold the last few seconds only when spectators trim; with none, all are kept here.
            double specKbps = frameBytes * 8 / 1000.0 / seconds;
            double fileMbPerMin = ms.Length / (1024.0 * 1024.0) / seconds * 60;
            TestContext.WriteLine($"32 players: spectator stream {specKbps:0} kbit/s, replay file {fileMbPerMin:0.00} MB per minute (inputs included)");
            Assert.That(specKbps, Is.LessThan(400), "a spectator of a full lobby needs far less than a player's 1.5 Mbit/s");
            Assert.That(fileMbPerMin, Is.LessThan(3), "a 20-minute battle royale replay stays well under 60 MB");
        }

        /// <summary>Runs in a wobbly circle, jumping now and then (a stand-in player for size tests).</summary>
        private sealed class Runner : IBotBrain
        {
            private readonly int _k;
            public Runner(int k) { _k = k; }
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me)
            {
                int t = server.Tick;
                var c = new InputCmd { Tick = t, ViewTick = t, Pitch = (float)(10 * Math.Sin(t * 0.01 + _k)) };
                c.Move.MoveZ = 1;
                c.Move.SprintHeld = true;
                c.Move.Yaw = Math.IEEERemainder(t * 0.012 + _k, 2 * Math.PI);
                c.Move.JumpPressed = (t + _k * 37) % 240 == 0;
                c.Move.JumpHeld = c.Move.JumpPressed;
                return InputCmd.Quantize(c);
            }
        }
    }
}
