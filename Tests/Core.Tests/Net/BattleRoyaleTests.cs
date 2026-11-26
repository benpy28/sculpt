using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>The battle royale on the server: dealing teams and pads, the loadout, chests, the circle, places, bots, re-runs.</summary>
    public class BattleRoyaleTests
    {
        private static BotNav _nav;
        private static BotNav Nav() => _nav ??= BrMap.BuildNav(1.0);

        /// <summary>A player that stands still (its pad is where the test wants it).</summary>
        private sealed class Idle : IBotBrain
        {
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me) => new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        private static NetServer NewServer(BrConfig cfg, out BreachMapInfo info, out BrMapInfo br, NetServer.SendFn send = null)
        {
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out info, out br);
            var s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), send ?? ((c, d, r) => { }));
            s.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(s, info, br);
            return s;
        }

        private static BrConfig Quick(BrMode mode, Func<int, IBotBrain> bots = null) => new BrConfig
        {
            Mode = mode,
            MakeBot = bots ?? (i => new Idle()),
            FillWithoutHumans = true,
            CountdownTicks = 60 * SimTime.Per60,
            ResultsTicks = 120 * SimTime.Per60,
        };

        private static void StepUntil(NetServer s, Func<bool> done, int maxTicks)
        {
            for (int i = 0; i < maxTicks && !done(); i++) s.Step();
            Assert.That(done(), $"not reached in {maxTicks} ticks (phase {s.Match.Phase})");
        }

        // ------------------------------------------------------------------ rarity

        [Test]
        public void Rarity_ChangesDamageOnly_LegendaryIsTwentyPercentOverUncommon_AndReloadsFaster()
        {
            var wc = new WeaponsConfig();
            Assert.That(wc.ScaleDamage(100, Rarity.None), Is.EqualTo(100), "no rarity: the normal numbers (the 1v1)");
            Assert.That(wc.ScaleDamage(100, Rarity.Uncommon), Is.EqualTo(100));
            Assert.That(wc.ScaleDamage(100, Rarity.Legendary), Is.EqualTo(120));
            int last = 0;
            for (Rarity r = Rarity.Common; r <= Rarity.Legendary; r++)
            {
                int d = wc.ScaleDamage(1000, r);
                Assert.That(d, Is.GreaterThan(last), "each tier hits harder than the one below");
                last = d;
                Assert.That(wc.ScaleReload(120, r), Is.EqualTo(r == Rarity.Legendary ? 114 : 120), "only legendary reloads faster (5%)");
            }
        }

        [Test]
        public void ALegendaryGun_ReloadsFivePercentFaster_InTheRealRules()
        {
            var mc = new MovementConfig();
            int Reload(Rarity r)
            {
                MotorState s = MotorState.At(Vec3d.Zero);
                s.Mode = MoveMode.Ground;
                s.Hand.Bag = new Bag { S0 = Item.Ar, R0 = r };
                s.Hand.Slot = 0;
                s.Weapon.SetUsed(Weapon.Ar, 5);
                int t = 100;
                WeaponRules.Reload(ref s, t, mc.Weapons.Stats[(int)Weapon.Ar], true, mc.Weapons);
                int n = 0;
                while (s.Weapon.Reloading && n < 2000) { t++; n++; WeaponRules.Reload(ref s, t, mc.Weapons.Stats[(int)Weapon.Ar], false, mc.Weapons); }
                return n;
            }
            int plain = Reload(Rarity.Uncommon), leg = Reload(Rarity.Legendary);
            Assert.That(plain, Is.GreaterThan(100));
            Assert.That(leg, Is.EqualTo((int)Math.Round(plain * 0.95)).Within(1));
        }

        // ------------------------------------------------------------------ item deltas

        [Test]
        public void ItemDeltas_RebuildTheSameGround_AsTheFullList()
        {
            var server = new GroundItems();
            var client = new GroundItems();
            var sent = new Dictionary<int, GroundItem>();
            var up = new List<GroundItem>();
            var gone = new List<int>();
            var seen = new HashSet<int>();
            var rng = new Random(3);
            for (int round = 0; round < 200; round++)
            {
                int op = rng.Next(3);
                if (op == 0 || server.Count == 0)
                    server.Add(new GroundItem { Kind = (Item)rng.Next(1, 9), Count = 1 + rng.Next(3), Rarity = (Rarity)rng.Next(6), Position = new Vec3d(rng.Next(100), 0, rng.Next(100)) });
                else if (op == 1) server.Remove(server[rng.Next(server.Count)].Id);
                else server.SetCount(server[rng.Next(server.Count)].Id, 1 + rng.Next(4));
                ItemsProtocol.Diff(sent, server.All, up, gone, seen);
                var w = new ByteWriter(256);
                ItemsProtocol.Write(w, new ItemsMsg { Tick = round, Items = up, Removed = gone });
                byte[] data = w.ToArray();
                var r = new ByteReader(data, 0, data.Length);
                var into = new List<GroundItem>();
                var rem = new List<int>();
                ItemsMsg m = ItemsProtocol.Read(ref r, into, rem);
                Assert.That(m.Full, Is.False);
                ItemsProtocol.Apply(client, m);
                Assert.That(client.Count, Is.EqualTo(server.Count), $"round {round}");
                for (int i = 0; i < server.Count; i++)
                {
                    Assert.That(client.TryGet(server[i].Id, out GroundItem c), Is.True);
                    Assert.That(c.Count, Is.EqualTo(server[i].Count));
                    Assert.That(c.Rarity, Is.EqualTo(server[i].Rarity));
                    Assert.That(c.Kind, Is.EqualTo(server[i].Kind));
                }
            }
        }

        [Test]
        public void MatchMessage_CarriesTheCircleAndPlaces()
        {
            var m = new MatchState { Phase = MatchPhase.Playing, Mode = BrMode.Duos, TeamSize = 2, AliveTeams = 7, AlivePlayers = 13, WinnerTeam = 0 };
            m.Scores.Add(new MatchScore { PlayerId = 4, Team = 9, Kills = 3, Place = 2 });
            m.Storm = new StormState { Phase = 2, PhaseCount = 6, FromX = 10, FromZ = 20, FromRadius = 70, ToX = 15, ToZ = 25, ToRadius = 40, ShrinkStart = 5000, ShrinkEnd = 6000, Dps = 4 };
            var w = new ByteWriter(128);
            MatchProtocol.Write(w, m, 77);
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var b = new MatchState();
            MatchProtocol.Read(ref r, b);
            Assert.That(b.Mode, Is.EqualTo(BrMode.Duos));
            Assert.That(b.AlivePlayers, Is.EqualTo(13));
            Assert.That(b.Scores[0].Place, Is.EqualTo(2));
            Assert.That(b.Storm.RadiusAt(5500), Is.EqualTo(55).Within(1e-9));
            Assert.That(b.Storm.CentreXAt(7000), Is.EqualTo(15));
            Assert.That(b.Storm.Dps, Is.EqualTo(4));
            data[data.Length - 1 - 1 - 8 - 24 + 8] = 0xFF; // a NaN-ish byte inside the floats
            for (int i = 0; i < data.Length; i++)
            {
                byte[] bad = (byte[])data.Clone();
                bad[i] ^= 0x5A;
                try { var rr = new ByteReader(bad, 0, bad.Length); MatchProtocol.Read(ref rr, new MatchState()); }
                catch (NetFormatException) { }
            }
        }

        // ------------------------------------------------------------------ dealing out

        [TestCase(BrMode.Solos, 1, 32)]
        [TestCase(BrMode.Duos, 2, 16)]
        [TestCase(BrMode.Squads, 4, 8)]
        public void AFullLobby_IsDealtTeamsAndSpawns_AndEveryoneGetsTheLoadout(BrMode mode, int size, int teams)
        {
            NetServer s = NewServer(Quick(mode), out _, out BrMapInfo br);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            Assert.That(s.Players.Count, Is.EqualTo(32), "bots fill the lobby to 32");
            var byTeam = new Dictionary<int, List<ServerPlayer>>();
            foreach (ServerPlayer p in s.Players)
            {
                if (!byTeam.TryGetValue(p.Team, out var l)) byTeam[p.Team] = l = new List<ServerPlayer>();
                l.Add(p);
                Assert.That(p.State.Dead, Is.False);
                Bag b = p.State.Hand.Bag;
                Assert.That(b.S0, Is.EqualTo(Item.Pump));
                Assert.That(b.R0, Is.EqualTo(Rarity.Common));
                Assert.That(b.S1, Is.EqualTo(Item.Ar), "the locker gun (stub: the AR)");
                Assert.That(b.R1, Is.EqualTo(Rarity.Common));
                Assert.That(b.S2, Is.EqualTo(Item.None));
                Assert.That(b.S4, Is.EqualTo(Item.None));
                Assert.That(s.InBrMatch(p.Id));
            }
            Assert.That(byTeam.Count, Is.EqualTo(teams));
            foreach (var kv in byTeam)
            {
                Assert.That(kv.Value.Count, Is.EqualTo(size));
                // Teammates spawn together: within a few metres. Different teams: far apart.
                foreach (ServerPlayer a in kv.Value)
                    foreach (ServerPlayer b in kv.Value)
                        Assert.That((a.Spawn - b.Spawn).HorizontalLength, Is.LessThan(8));
            }
            var spawns = new List<Vec3d>();
            foreach (var kv in byTeam) spawns.Add(kv.Value[0].Spawn);
            double nearest = double.MaxValue;
            for (int i = 0; i < spawns.Count; i++)
                for (int j = i + 1; j < spawns.Count; j++) nearest = Math.Min(nearest, (spawns[i] - spawns[j]).HorizontalLength);
            Assert.That(nearest, Is.GreaterThan(12), "no team spawns on top of another");
            Assert.That(s.Match.Storm.Active);
            Assert.That(s.Match.AliveTeams, Is.EqualTo(teams));
        }

        [Test]
        public void TheDeal_ChangesEveryMatch_ButIsTheSameForTheSameSeed()
        {
            string Deal(int seed, int matches)
            {
                BrConfig cfg = Quick(BrMode.Solos);
                cfg.Seed = seed;
                NetServer s = NewServer(cfg, out _, out _);
                var sb = new System.Text.StringBuilder();
                for (int m = 0; m < matches; m++)
                {
                    StepUntil(s, () => s.Match.Phase == MatchPhase.Countdown, 40 * SimTime.TicksPerSecond);
                    s.Step();
                    foreach (ServerPlayer p in s.Players) sb.Append(p.Id).Append(':').Append(Math.Round(p.Spawn.X)).Append(',').Append(Math.Round(p.Spawn.Z)).Append(' ');
                    sb.Append('|');
                    // End this match fast: everyone dies.
                    StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
                    foreach (ServerPlayer p in s.Players) { p.State.Damage = 149; }
                    s.Br.Dps[0] = 50;
                    s.Br.WaitSeconds[0] = 0;
                    StepUntil(s, () => s.Match.Phase != MatchPhase.Playing, 200 * SimTime.TicksPerSecond);
                    s.Br.Dps[0] = 1;
                    s.Br.WaitSeconds[0] = 60;
                }
                return sb.ToString();
            }
            string a = Deal(7, 2), b = Deal(7, 2), c = Deal(8, 1);
            Assert.That(b, Is.EqualTo(a));
            string[] parts = a.Split('|');
            Assert.That(parts[1], Is.Not.EqualTo(parts[0]), "the next match deals again");
            Assert.That(c, Is.Not.EqualTo(parts[0] + "|"), "another seed deals differently");
        }

        // ------------------------------------------------------------------ chests

        [Test]
        public void AChest_OpensIntoAGunOfSomeRarity_AndAUtility()
        {
            NetServer s = NewServer(Quick(BrMode.Solos), out _, out BrMapInfo br);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            int chests = 0;
            for (int i = 0; i < s.Items.Count; i++) if (s.Items[i].Kind == Item.Chest) chests++;
            Assert.That(chests, Is.EqualTo(br.Chests.Count), "every chest is on the map at the start");

            // Walk a player to the pad loot spot and open it: put them next to it and press interact.
            ServerPlayer p = s.Players[0];
            GroundItem chest = default;
            double best = double.MaxValue;
            for (int i = 0; i < s.Items.Count; i++)
            {
                GroundItem g = s.Items[i];
                if (g.Kind != Item.Chest) continue;
                double d = (g.Position - p.State.Position).Length;
                if (d < best) { best = d; chest = g; }
            }
            double yaw = NetServer.ChestYaw(chest);
            Vec3d front = chest.Position + new Vec3d(Math.Sin(yaw), 0.05, Math.Cos(yaw)) * 1.5;
            // A player that presses interact on it, through the real command path.
            s.AddBot(new Opener(chest.Id), 99, front, 0);
            s.Step();
            s.Step();
            Assert.That(s.Items.TryGet(chest.Id, out _), Is.False, "the chest is gone");
            int guns = 0, utils = 0;
            for (int i = 0; i < s.Items.Count; i++)
            {
                GroundItem g = s.Items[i];
                if ((g.Position - chest.Position).HorizontalLength > 2.5 || g.Kind == Item.Chest) continue;
                if (Items.IsGun(g.Kind)) { guns++; Assert.That(g.Rarity, Is.InRange(Rarity.Common, Rarity.Legendary)); }
                if (Items.Throws(g.Kind)) utils++;
            }
            Assert.That(guns, Is.EqualTo(1));
            Assert.That(utils, Is.EqualTo(1));
        }

        private sealed class Opener : IBotBrain
        {
            private readonly int _id;
            public Opener(int id) { _id = id; }
            public bool CountsAsPlayer => false;
            public InputCmd Next(NetServer server, ServerPlayer me)
            {
                var c = new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
                c.Hand.Pickup = true;
                c.Hand.PickId = _id;
                return c;
            }
        }

        [Test]
        public void ChestRarities_FollowTheOdds()
        {
            // Roll every chest on many matches' seeds: about 5% legendary, 34% common.
            var counts = new int[6];
            int total = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                BrConfig cfg = Quick(BrMode.Solos);
                cfg.Seed = seed;
                NetServer s = NewServer(cfg, out _, out _);
                StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
                var ids = new List<int>();
                for (int i = 0; i < s.Items.Count; i++) if (s.Items[i].Kind == Item.Chest) ids.Add(s.Items[i].Id);
                var opener = s.AddBot(new Opener(0), 99, Vec3d.Zero, 0);
                foreach (int id in ids)
                {
                    s.Items.TryGet(id, out GroundItem ch);
                    var o = new Opener(id);
                    // Put the opener in front of each chest, one per tick.
                    s.RemovePlayer(opener.ConnectionId);
                    opener = s.AddBot(o, 99, ch.Position + new Vec3d(Math.Sin(NetServer.ChestYaw(ch)), 0.05, Math.Cos(NetServer.ChestYaw(ch))) * 1.5, 0);
                    s.Step();
                    s.Step();
                }
                for (int i = 0; i < s.Items.Count; i++)
                    if (Items.IsGun(s.Items[i].Kind) && s.Items[i].Rarity != Rarity.None) { counts[(int)s.Items[i].Rarity]++; total++; }
            }
            TestContext.WriteLine($"{total} guns: common {counts[1]}, uncommon {counts[2]}, rare {counts[3]}, epic {counts[4]}, legendary {counts[5]}");
            Assert.That(total, Is.GreaterThan(400));
            Assert.That(counts[1] / (double)total, Is.EqualTo(0.34).Within(0.07));
            Assert.That(counts[5] / (double)total, Is.EqualTo(0.05).Within(0.03));
            for (int r = 1; r <= 5; r++) Assert.That(counts[r], Is.GreaterThan(0));
        }

        // ------------------------------------------------------------------ the circle, deaths, places

        [Test]
        public void TheCircle_ShrinksInPhases_AndHurtsOnlyOutside()
        {
            BrConfig cfg = Quick(BrMode.Solos);
            NetServer s = NewServer(cfg, out _, out BrMapInfo br);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            StormState st = s.Match.Storm;
            Assert.That(st.Phase, Is.EqualTo(0));
            // The 614 m map: 400 m to 250 m after 75 s (was 200 to 120 after 60 s on the 307 m one).
            Assert.That(st.FromRadius, Is.EqualTo(cfg.StartRadius));
            Assert.That(cfg.StartRadius, Is.GreaterThanOrEqualTo(br.PlayHalf * Math.Sqrt(2)), "the first circle covers the whole play area");
            Assert.That(st.ToRadius, Is.EqualTo(cfg.Radius[0]));
            double off = Math.Sqrt(Math.Pow(st.ToX - st.FromX, 2) + Math.Pow(st.ToZ - st.FromZ, 2));
            Assert.That(off + st.ToRadius, Is.LessThanOrEqualTo(st.FromRadius), "the next circle lies inside this one");
            Assert.That(st.ShrinkStart - s.Tick, Is.EqualTo((int)(cfg.WaitSeconds[0] * SimTime.TicksPerSecond)).Within(2));
            // Nobody is outside the first circle: nobody is hurt.
            StepUntil(s, () => s.Tick > st.ShrinkStart, (int)(cfg.WaitSeconds[0] + 10) * SimTime.TicksPerSecond);
            foreach (ServerPlayer p in s.Players) Assert.That(p.State.Damage, Is.Zero);
            // Run the phases through: each circle sits inside the last.
            int phases = 1;
            StormState last = st;
            for (int i = 0; i < 600 * SimTime.TicksPerSecond && s.Match.Phase == MatchPhase.Playing; i++)
            {
                s.Step();
                StormState now = s.Match.Storm;
                if (now.Phase != last.Phase)
                {
                    phases++;
                    Assert.That(now.FromRadius, Is.EqualTo(last.ToRadius));
                    Assert.That(now.FromX, Is.EqualTo(last.ToX));
                    double o = Math.Sqrt(Math.Pow(now.ToX - now.FromX, 2) + Math.Pow(now.ToZ - now.FromZ, 2));
                    Assert.That(o + now.ToRadius, Is.LessThanOrEqualTo(now.FromRadius + 1e-6));
                    last = now;
                }
            }
            Assert.That(s.Match.Phase, Is.EqualTo(MatchPhase.Ended), "the last circle closes to nothing: someone wins (or the last go down together)");
            Assert.That(phases, Is.GreaterThanOrEqualTo(3), "players standing still on their pads are caught by the third circle");
            // Places: every team has one, the winner first, nobody respawned.
            var places = new List<int>();
            foreach (MatchScore sc in s.Match.Scores) { places.Add(sc.Place); Assert.That(sc.Place, Is.InRange(1, 32)); }
            Assert.That(places, Does.Contain(1));
            if (s.Match.WinnerTeam != 0)
            {
                int winners = 0;
                foreach (MatchScore sc in s.Match.Scores) if (sc.Place == 1) { winners++; Assert.That(sc.Team, Is.EqualTo(s.Match.WinnerTeam)); }
                Assert.That(winners, Is.EqualTo(1));
            }
        }

        [Test]
        public void TheDead_DropEverything_StayOut_AndLateJoinersSitOut()
        {
            NetServer s = NewServer(Quick(BrMode.Solos), out _, out _);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            ServerPlayer p = s.Players[3];
            Vec3d where = p.State.Position;
            // Fall damage kills through the real rules: lift them high, drop.
            p.State.Damage = 149;
            p.State.Position = where + new Vec3d(0, 40, 0);
            p.Body.Position = p.State.Position;
            StepUntil(s, () => p.State.Dead, 10 * SimTime.TicksPerSecond);
            int near = 0;
            for (int i = 0; i < s.Items.Count; i++)
                if ((s.Items[i].Position - p.State.Position).HorizontalLength < 2 && Items.IsGun(s.Items[i].Kind))
                {
                    near++;
                    Assert.That(s.Items[i].Rarity, Is.EqualTo(Rarity.Common));
                }
            Assert.That(near, Is.EqualTo(2), "the pump and the locker gun fall where they died");
            for (int i = 0; i < 10 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(p.State.Dead, Is.True, "one life per match");
            Assert.That(s.Match.AlivePlayers, Is.EqualTo(31));

            // A late joiner (another player) sits it out.
            ServerPlayer late = s.AddBot(new Idle(), 1, where, 0);
            s.Step();
            Assert.That(late.State.Dead, Is.True);
            Assert.That(s.InBrMatch(late.Id), Is.False);
        }

        [Test]
        public void AHumanJoiningAFullLobby_TakesABotsPlace()
        {
            BrConfig cfg = Quick(BrMode.Solos);
            cfg.FillWithoutHumans = false;
            NetServer s = NewServer(cfg, out _, out _);
            s.Step();
            Assert.That(s.Players.Count, Is.Zero, "no bots for an empty server");
            ServerPlayer human = s.AddPlayer(5, 1, new Vec3d(150, 1, 150), 0);
            s.Step();
            Assert.That(s.Players.Count, Is.EqualTo(32));
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            Assert.That(s.InBrMatch(human.Id));
            // The last human leaves mid-match: the bots don't play on alone; the match ends and they go.
            s.RemovePlayer(5);
            StepUntil(s, () => s.Players.Count == 0, 10);
        }

        // ------------------------------------------------------------------ the locker gun

        [Test]
        public void TheLockerPick_IsTheSecondGun_OnlyLockerGunsAreTaken_AndNoneMeansTheDefault()
        {
            NetServer s = NewServer(Quick(BrMode.Solos), out _, out _);
            ServerPlayer smg = s.AddPlayer(1, 1, new Vec3d(150, 1, 150), 0);
            ServerPlayer pistol = s.AddPlayer(2, 1, new Vec3d(152, 1, 150), 0);
            ServerPlayer back = s.AddPlayer(3, 1, new Vec3d(154, 1, 150), 0);
            Assert.That(s.SetLocker(smg, Item.Smg));
            Assert.That(s.SetLocker(pistol, Item.Pistol));
            Assert.That(s.SetLocker(back, Item.Pistol));
            Assert.That(s.SetLocker(back, Item.None), "None goes back to the default");
            foreach (Item no in new[] { Item.Pump, Item.Grenade, Item.C4, Item.Launcher, Item.Chest })
            {
                Assert.That(s.SetLocker(smg, no), Is.False, $"{no} is not a locker gun");
                Assert.That(smg.Locker, Is.EqualTo(Item.Smg), "a refused pick changes nothing");
            }
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            Assert.That(smg.State.Hand.Bag.S1, Is.EqualTo(Item.Smg));
            Assert.That(pistol.State.Hand.Bag.S1, Is.EqualTo(Item.Pistol));
            Assert.That(back.State.Hand.Bag.S1, Is.EqualTo(Item.Ar), "the default locker gun");
            foreach (ServerPlayer p in new[] { smg, pistol, back })
            {
                Assert.That(p.State.Hand.Bag.S0, Is.EqualTo(Item.Pump), "everyone keeps the common pump");
                Assert.That(p.State.Hand.Bag.R1, Is.EqualTo(Rarity.Common), "the locker gun is common");
            }
        }

        [Test]
        public void LockerPicks_AreInTheReplay_SoTheReRunDealsTheSameLoadouts()
        {
            BrConfig cfg = Quick(BrMode.Duos);
            NetServer s = NewServer(cfg, out _, out _);
            var ms = new MemoryStream();
            MatchStream stream = s.StartStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "BattleRoyale", Mode = "Duos", Build = "tests", StartedUtcTicks = 1 }, stream);
            ServerPlayer me = s.AddPlayer(1, 1, new Vec3d(150, 1, 150), 0);
            Assert.That(s.SetLocker(me, Item.Smg));
            s.Step();
            // Picks between ticks after others joined (a later pick replaces an earlier one), all before the deal.
            ServerPlayer bot = null;
            foreach (ServerPlayer p in s.Players) if (p != me) { bot = p; break; }
            Assert.That(bot, Is.Not.Null, "bots filled the lobby after the first tick");
            Assert.That(s.SetLocker(bot, Item.Ar));
            Assert.That(s.SetLocker(bot, Item.Pistol));
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            for (int i = 0; i < 3 * SimTime.TicksPerSecond; i++) s.Step();
            writer.Finish();
            Assert.That(me.State.Hand.Bag.S1, Is.EqualTo(Item.Smg));
            Assert.That(bot.State.Hand.Bag.S1, Is.EqualTo(Item.Pistol));

            ReplayFile f = ReplayFile.Read(ms.ToArray());
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                BrConfig again = Quick(BrMode.Duos);
                again.MakeBot = null;
                return NewServer(again, out _, out _);
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.HashesChecked, Is.GreaterThan(10));
            Assert.That(check.Passed, check.ToString());
        }

        // ------------------------------------------------------------------ the whole thing, with bots

        private static BrConfig Fast(BrMode mode, int seed) => new BrConfig
        {
            Mode = mode,
            Seed = seed,
            MakeBot = i => new DuelBot(Nav(), DuelBotConfig.For((BotLevel)(i % 3)), 100 + i * 7 + seed),
            FillWithoutHumans = true,
            CountdownTicks = 60 * SimTime.Per60,
            ResultsTicks = 120 * SimTime.Per60,
            WaitSeconds = new double[] { 20, 15, 10, 8, 6, 5 },
            ShrinkSeconds = new double[] { 20, 15, 12, 10, 8, 8 },
            Dps = new[] { 2, 4, 6, 10, 15, 25 },
        };

        [TestCase(BrMode.Solos, 3)]
        [TestCase(BrMode.Squads, 4)]
        public void ThirtyTwoBots_PlayAWholeMatch_ToAWinner_AndTheReplayReRunsExactly(BrMode mode, int seed)
        {
            BrConfig cfg = Fast(mode, seed);
            NetServer s = NewServer(cfg, out _, out _);
            var ms = new MemoryStream();
            MatchStream stream = s.StartStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "BattleRoyale", Mode = mode.ToString(), Build = "tests", StartedUtcTicks = 1 }, stream);
            var clock = Stopwatch.StartNew();
            int opened = 0, picked = 0, kills = 0, ticks = 0;
            var sw = new Stopwatch();
            double worst = 0;
            var playing = new List<double>();
            while (ticks < 260 * SimTime.TicksPerSecond)
            {
                sw.Restart();
                s.Step();
                sw.Stop();
                if (s.Match.Phase == MatchPhase.Playing) { worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds); playing.Add(sw.Elapsed.TotalMilliseconds); }
                ticks++;
                foreach (var a in s.ItemActions)
                {
                    if (a.Value == PickupResult.Opened) opened++;
                    else if (a.Value == PickupResult.Ok || a.Value == PickupResult.Swapped) picked++;
                }
                if (s.Match.Phase == MatchPhase.Ended) break;
            }
            writer.Finish();
            foreach (ServerPlayer p in s.Players) kills += p.MatchKills;
            playing.Sort();
            double avg = 0;
            foreach (double d in playing) avg += d;
            avg /= Math.Max(1, playing.Count);
            double p99 = playing.Count > 0 ? playing[(int)(playing.Count * 0.99)] : 0;
            TestContext.WriteLine($"server tick while playing: average {avg:0.00} ms, 99% under {p99:0.00} ms, worst {worst:0.00} ms (.NET release, one core)");
            TestContext.WriteLine($"{mode}: ended after {ticks / (double)SimTime.TicksPerSecond:0.0} s game time in {clock.Elapsed.TotalSeconds:0.0} s; " +
                $"{opened} chests opened, {picked} pickups, {kills} kills, winner team {s.Match.WinnerTeam}; worst tick {worst:0.00} ms; replay {ms.Length / 1024} KB");
            Assert.That(s.Match.Phase, Is.EqualTo(MatchPhase.Ended));
            Assert.That(s.Match.WinnerTeam, Is.Not.Zero, "one team is left standing");
            Assert.That(opened, Is.GreaterThan(5), "bots open chests");
            Assert.That(picked, Is.GreaterThan(5), "bots pick up loot");
            Assert.That(kills, Is.GreaterThan(3), "bots fight");

            ReplayFile f = ReplayFile.Read(ms.ToArray());
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                BrConfig again = Fast(mode, seed);
                again.MakeBot = null; // the recording brings its players
                return NewServer(again, out _, out _);
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Passed, check.ToString());
        }
    }
}
