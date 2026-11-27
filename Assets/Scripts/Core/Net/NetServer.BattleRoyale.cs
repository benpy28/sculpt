using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Battle royale numbers (first playable, Ben 2026-11-26). 32 players every match: bots fill whatever humans don't.
    /// Every number here is a starting value for play-testing.
    /// </summary>
    public sealed class BrConfig
    {
        public BrMode Mode = BrMode.Solos;
        public int TeamSize => Mode == BrMode.Duos ? 2 : Mode == BrMode.Squads ? 4 : 1;
        public int MaxPlayers = 32;
        /// <summary>A match starts once the lobby is full, or once this many players have waited <see cref="LobbyTicks"/>.</summary>
        public int MinPlayers = 1;
        public int LobbyTicks = (10 * 60) * SimTime.Per60;
        public int CountdownTicks = (5 * 60) * SimTime.Per60;
        public int ResultsTicks = (12 * 60) * SimTime.Per60;
        /// <summary>
        /// Makes the brain for a bot that fills the lobby (argument: how many bots this server has made so far). Null: no
        /// bots are added (the replay verifier, whose bots come from the recording).
        /// </summary>
        public Func<int, IBotBrain> MakeBot;
        /// <summary>Fill with bots even when no human is connected (tests, a bot-only showcase).</summary>
        public bool FillWithoutHumans;
        /// <summary>Everything random in a match (teams, regions, the circle, chest loot) comes from this and the match number.</summary>
        public int Seed = 1;

        // ---- The circle. Phase k: wait, then shrink to Radius[k] over Shrink[k]; outside it you take Dps[k] each second.
        // The 614 m map (Ben 2026-11-26): the first circles are bigger and give longer to get in (crossing half the map at
        // a sprint takes about 30 s); the last ones are the same size as before, since the endgame is about the players
        // left, not the map.
        public double StartRadius = 400;   // covers the whole play area (its corners are 384 m from the middle)
        public double[] WaitSeconds = { 75, 55, 40, 30, 25, 20 };
        public double[] ShrinkSeconds = { 60, 45, 35, 25, 20, 20 };
        public double[] Radius = { 250, 150, 80, 35, 12, 0 };
        public int[] Dps = { 1, 2, 4, 6, 8, 10 };
        public int StormHurtEveryTicks = 60 * SimTime.Per60;

        // ---- Loot.
        /// <summary>The gun a player starts with besides the common pump when they haven't picked one in the locker.</summary>
        public Item LockerGun = Item.Ar;
        /// <summary>The guns the locker offers (Ben: a common pump plus one basic gun picked in the locker).</summary>
        public static readonly Item[] LockerChoices = { Item.Smg, Item.Ar, Item.Pistol };
        /// <summary>Chest rarity odds, common to legendary.</summary>
        public int[] RarityWeights = { 34, 30, 20, 11, 5 };
        /// <summary>Chest guns and their odds: pump, SMG, AR, pistol.</summary>
        public Item[] ChestGuns = { Item.Pump, Item.Smg, Item.Ar, Item.Pistol };
        public int[] ChestGunWeights = { 25, 30, 30, 15 };
        /// <summary>Chest utility and its odds: grenades, C4, launcher.</summary>
        public Item[] ChestUtility = { Item.Grenade, Item.C4, Item.Launcher };
        public int[] ChestUtilityWeights = { 60, 25, 15 };
        /// <summary>Every chest also spills a heal (Ben 2026-11-26: heals in chest loot; odds and counts by Claude):
        /// two bandages or one first-aid kit. None in the list = no heal.</summary>
        public Item[] ChestHeals = { Item.Bandage, Item.Medkit };
        public int[] ChestHealWeights = { 65, 35 };
        public int[] ChestHealCounts = { 2, 1 };
        /// <summary>Chests spill their loot this far in front of them.</summary>
        public double ChestSpill = 0.9;
        /// <summary>Ground item cap in a battle royale (chests, spills and everything the dead drop).</summary>
        public int MaxGroundItems = 1500;
    }

    /// <summary>
    /// The battle royale on the server: lobby fill, teams and spawn regions dealt out at random each match, the loadout,
    /// chests, the shrinking circle, eliminations and placements, then results and the next match. One life per match:
    /// the dead drop everything and watch.
    ///
    /// Re-run safe (the replay verifier gets the same states): everything random is hashed from the seed and the match
    /// number, teams are dealt at the start of a tick from the player list, and bots join and leave only between ticks
    /// (after the stream closes the tick), where the verifier re-adds them from the recording.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Battle royale rules, or null for every other mode.</summary>
        public BrConfig Br { get; private set; }
        public BrMapInfo BrMap { get; private set; }
        /// <summary>Matches started since the server began (part of every random roll).</summary>
        public int BrMatchNumber { get; private set; }
        /// <summary>Who is in the current match (alive or dead).</summary>
        private readonly HashSet<int> _brIn = new HashSet<int>();
        /// <summary>Where each team finished (1 = won), only teams that are out.</summary>
        private readonly Dictionary<int, int> _brPlace = new Dictionary<int, int>();
        private readonly HashSet<int> _brAliveTeams = new HashSet<int>();
        private int _brBotsMade;
        private readonly List<ServerPlayer> _brScratch = new List<ServerPlayer>();

        /// <summary>Turns this server into a battle royale on <paramref name="map"/> (call once, before anyone joins).</summary>
        public void EnableBattleRoyale(BrConfig config, BrMapInfo map)
        {
            Br = config ?? throw new ArgumentNullException(nameof(config));
            BrMap = map ?? throw new ArgumentNullException(nameof(map));
            MatchConfig.Enabled = true;
            Match.Mode = config.Mode;
            Match.TeamSize = config.TeamSize;
            Match.Storm = new StormState { Phase = -1, PhaseCount = config.Radius.Length };
            Movement.Items.MaxGroundItems = Math.Max(Movement.Items.MaxGroundItems, config.MaxGroundItems);
            _matchDirty = true;
        }

        /// <summary>Where the battle royale's place in the current match stands for a team (0 while it is still in).</summary>
        public int PlaceOf(int team) => _brPlace.TryGetValue(team, out int p) ? p : 0;

        public bool InBrMatch(int playerId) => _brIn.Contains(playerId);

        // ================================================================ Phases (start of each tick)

        private void StepBr()
        {
            CaptureMatchStart();
            int n = HumanCount();
            switch (Match.Phase)
            {
                case MatchPhase.Off:
                case MatchPhase.Waiting:
                    if (Match.Phase != MatchPhase.Waiting) SetPhase(MatchPhase.Waiting, 0);
                    ReviveLobby();
                    if (n >= Br.MinPlayers && (n >= Br.MaxPlayers || Tick - Match.PhaseStartTick >= Br.LobbyTicks)) BeginBr();
                    break;
                case MatchPhase.Countdown:
                    SitOutLateJoiners();
                    if (n == 0) SetPhase(MatchPhase.Waiting, 0);
                    else if (Tick >= Match.PhaseEndTick)
                    {
                        SetPhase(MatchPhase.Playing, 0);
                        StartStormPhase(0);
                    }
                    break;
                case MatchPhase.Playing:
                    SitOutLateJoiners();
                    if (n == 0) { SetPhase(MatchPhase.Waiting, 0); break; }
                    // The last person left: bots don't play on alone (only where this server makes its own bots, so a
                    // re-run, whose players are all scripted, is never cut short).
                    if (Br.MakeBot != null && !Br.FillWithoutHumans && !AnyHuman()) { SetPhase(MatchPhase.Ended, Tick); break; }
                    StepStorm();
                    break;
                case MatchPhase.Ended:
                    if (Tick >= Match.PhaseEndTick) SetPhase(MatchPhase.Waiting, 0);
                    break;
            }
        }

        /// <summary>A new match: the map as built, teams and regions dealt out, everyone to a pad with the loadout.</summary>
        private void BeginBr()
        {
            BrMatchNumber++;
            ResetMap();
            _brIn.Clear();
            _brPlace.Clear();
            _brAliveTeams.Clear();
            Match.WinnerTeam = 0;
            Match.Storm = new StormState { Phase = -1, PhaseCount = Br.Radius.Length };

            // Who plays: match players by id (the same order on a re-run), at most a full lobby.
            _brScratch.Clear();
            foreach (ServerPlayer p in _players) if (CountsForMatch(p)) _brScratch.Add(p);
            _brScratch.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (_brScratch.Count > Br.MaxPlayers) _brScratch.RemoveRange(Br.MaxPlayers, _brScratch.Count - Br.MaxPlayers);

            // Shuffle players, then deal: consecutive players make a team, teams take spawn slots in shuffled order.
            int count = _brScratch.Count;
            for (int i = count - 1; i > 0; i--)
            {
                int j = (int)(BrRoll(1, i, 0) % (uint)(i + 1));
                (_brScratch[i], _brScratch[j]) = (_brScratch[j], _brScratch[i]);
            }
            int size = Br.TeamSize;
            int slots = BrSlots();
            int[] order = new int[slots];
            for (int i = 0; i < slots; i++) order[i] = i;
            for (int i = slots - 1; i > 0; i--)
            {
                int j = (int)(BrRoll(2, i, 0) % (uint)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (int i = 0; i < count; i++)
            {
                ServerPlayer p = _brScratch[i];
                int team = i / size + 1;
                int slot = order[(team - 1) % slots];
                BrSpawn(slot, i % size, out Vec3d at, out double yaw);
                p.Team = team;
                p.Body.Team = team;
                p.Spawn = at;
                p.SpawnYaw = yaw;
                p.ForceRespawn = true;
                p.BrLoadout = true;
                p.MatchKills = p.MatchDeaths = 0;
                p.LastHurtBy = 0;
                _brIn.Add(p.Id);
                _brAliveTeams.Add(team);
            }
            SetPhase(MatchPhase.Countdown, Tick + Br.CountdownTicks);
        }

        /// <summary>Spawn slots: solos one per pad (32), duos two pads per region (16, the two farthest apart), squads one per region.</summary>
        private int BrSlots() => Br.Mode == BrMode.Squads ? BrMap.Regions.Count : Br.Mode == BrMode.Duos ? BrMap.Regions.Count * 2 : BrMap.Regions.Count * 4;

        private void BrSpawn(int slot, int member, out Vec3d at, out double yaw)
        {
            int regions = BrMap.Regions.Count;
            BrRegion r;
            switch (Br.Mode)
            {
                case BrMode.Squads:
                    r = BrMap.Regions[slot % regions];
                    at = r.SquadSpots[member % 4];
                    yaw = r.SquadYaw;
                    return;
                case BrMode.Duos:
                {
                    r = BrMap.Regions[(slot / 2) % regions];
                    int pad = slot % 2 == 0 ? 0 : 3;
                    // The two of a duo share the pad, a body's width either side of it, across the way it faces.
                    yaw = r.PadYaw[pad];
                    Vec3d side = new Vec3d(Math.Cos(yaw), 0, -Math.Sin(yaw)) * (member == 0 ? -0.6 : 0.6);
                    at = r.Pads[pad] + side;
                    return;
                }
                default:
                    r = BrMap.Regions[(slot / 4) % regions];
                    at = r.Pads[slot % 4];
                    yaw = r.PadYaw[slot % 4];
                    return;
            }
        }

        /// <summary>True for a gun the locker offers (SMG, AR or pistol).</summary>
        public static bool IsLockerChoice(Item gun) => Array.IndexOf(BrConfig.LockerChoices, gun) >= 0;

        /// <summary>
        /// Sets the gun <paramref name="p"/> starts each battle royale match with (their locker pick), from the next deal
        /// on. Call between ticks. Recorded in the replay stream so a re-run deals the same loadout. Anything the locker
        /// doesn't offer is refused (returns false, nothing changes); <see cref="Item.None"/> goes back to the default.
        /// </summary>
        public bool SetLocker(ServerPlayer p, Item gun)
        {
            if (p == null || (gun != Item.None && !IsLockerChoice(gun))) return false;
            if (p.Locker == gun) return true;
            p.Locker = gun;
            Stream?.OnLocker(Tick, p.Id, gun);
            return true;
        }

        /// <summary>
        /// The start-of-match bag: a common pump and the common locker gun, placed by the player's slot preferences like
        /// a pickup (default: pump in 1, the AR in 2). The other three slots are for whatever they find. The pump is out.
        /// </summary>
        private void GiveBrLoadout(ServerPlayer p)
        {
            Item locker = p.Locker != Item.None ? p.Locker : Br.LockerGun;
            var bag = new Bag();
            int pump = Place(ref bag, p.Prefs, Item.Pump);
            int second = global::Sculpt.Core.Items.IsGun(locker) && locker != Item.Pump ? Place(ref bag, p.Prefs, locker) : -1;
            p.State.Hand.Bag = bag;
            p.State.Hand.Slot = (byte)pump;
            p.State.Hand.PrevSlot = (byte)(second >= 0 ? second : (pump + 1) % global::Sculpt.Core.Items.Slots);
            p.State.Weapon = WeaponState.Initial;
        }

        private static int Place(ref Bag bag, in SlotPrefs prefs, Item gun)
        {
            int k = prefs.Place(bag, gun, out int from, out int to);
            if (from >= 0) bag.SwapSlots(from, to);
            bag[k] = gun;
            bag.SetRarity(k, Rarity.Common);
            return k;
        }

        /// <summary>Anyone who joined after the deal sits the match out (dead, watching) until the next one.</summary>
        private void SitOutLateJoiners()
        {
            foreach (ServerPlayer p in _players)
            {
                if (_brIn.Contains(p.Id) || p.State.Dead || !CountsForMatch(p)) continue;
                WeaponRules.Kill(ref p.State, Tick, Weapons);
                p.State.RespawnTick = int.MaxValue;
                p.Body.Position = ParkedBody;
            }
        }

        /// <summary>Between matches the dead come back (at their last spawn) so the lobby is free play.</summary>
        private void ReviveLobby()
        {
            foreach (ServerPlayer p in _players)
                if (p.State.Dead && p.State.RespawnTick == int.MaxValue) p.ForceRespawn = true;
        }

        private bool AnyHuman()
        {
            foreach (ServerPlayer p in _players) if (!IsBotConnection(p.ConnectionId)) return true;
            return false;
        }

        // ================================================================ The circle

        private void StartStormPhase(int k)
        {
            StormState old = Match.Storm;
            double fx, fz, fr;
            if (k == 0 || !old.Active)
            {
                fx = BrMap.Centre.X;
                fz = BrMap.Centre.Z;
                fr = Br.StartRadius;
            }
            else
            {
                fx = old.ToX;
                fz = old.ToZ;
                fr = old.ToRadius;
            }
            double tr = Math.Min(fr, Br.Radius[k]);
            // The next circle lies inside this one, at a random spot, and inside the play area.
            double room = Math.Max(0, (fr - tr) * 0.85);
            double ang = BrRoll(3, k, 0) / 4294967296.0 * 2 * Math.PI;
            double dist = Math.Sqrt(BrRoll(4, k, 0) / 4294967296.0) * room;
            double tx = fx + Math.Cos(ang) * dist, tz = fz + Math.Sin(ang) * dist;
            double lim = Math.Max(0, BrMap.PlayHalf - tr - 4);
            tx = BrMap.Centre.X + Clamp(tx - BrMap.Centre.X, -lim, lim);
            tz = BrMap.Centre.Z + Clamp(tz - BrMap.Centre.Z, -lim, lim);
            int start = Tick + SimTime.SecondsToTicks(Br.WaitSeconds[Math.Min(k, Br.WaitSeconds.Length - 1)]);
            // Floats, so the server's circle is exactly the one every client draws.
            Match.Storm = new StormState
            {
                Phase = k,
                PhaseCount = Br.Radius.Length,
                FromX = (float)fx, FromZ = (float)fz, FromRadius = (float)fr,
                ToX = (float)tx, ToZ = (float)tz, ToRadius = (float)tr,
                ShrinkStart = start,
                ShrinkEnd = start + SimTime.SecondsToTicks(Br.ShrinkSeconds[Math.Min(k, Br.ShrinkSeconds.Length - 1)]),
                Dps = Br.Dps[Math.Min(k, Br.Dps.Length - 1)],
            };
            _matchDirty = true;
        }

        private void StepStorm()
        {
            StormState st = Match.Storm;
            if (!st.Active) return;
            if (Tick >= st.ShrinkEnd && st.Phase + 1 < Br.Radius.Length) { StartStormPhase(st.Phase + 1); st = Match.Storm; }
            if ((Tick - Match.PhaseStartTick) % Br.StormHurtEveryTicks != 0 || Tick == Match.PhaseStartTick) return;
            foreach (ServerPlayer p in _players)
            {
                if (p.State.Dead || !_brIn.Contains(p.Id)) continue;
                if (st.OutsideBy(p.State.Position, Tick) <= 0) continue;
                bool killed = Hurt(null, p, st.Dps);
                _combat.Add(new CombatEvent
                {
                    Type = CombatEventType.Hit, Attacker = 0, Victim = p.Id, Source = DamageSource.Storm, Damage = st.Dps, Pellets = 1,
                    Point = p.State.Position + new Vec3d(0, 1, 0), Killed = killed, HealthAfter = p.State.Health(Weapons),
                });
                if (killed) OnKilled(p, 0, DamageSource.Storm, false);
            }
        }

        /// <summary>True when the circle is closed in on this point right now (the HUD warning, bots).</summary>
        public bool InStorm(Vec3d p) => Match.IsBr && Match.Storm.Active && Match.Storm.OutsideBy(p, Tick) > 0;

        // ================================================================ Deaths, places, the end

        /// <summary>A death in a running battle royale: out for the match, and everything you carried falls where you died.</summary>
        private void OnBrDeath(ServerPlayer victim)
        {
            if (Br == null || Match.Phase != MatchPhase.Playing || !_brIn.Contains(victim.Id)) return;
            victim.State.RespawnTick = int.MaxValue;
            DropBag(victim);
        }

        private void DropBag(ServerPlayer p)
        {
            Bag b = p.State.Hand.Bag;
            Vec3d at = p.State.Position;
            int k = 0;
            for (int slot = 0; slot < global::Sculpt.Core.Items.Slots; slot++)
            {
                Item kind = b[slot];
                if (kind == Item.None || b.CountIn(slot) <= 0) continue;
                double a = k * (2 * Math.PI / global::Sculpt.Core.Items.Slots);
                k++;
                Vec3d spot = at + new Vec3d(Math.Sin(a) * 0.7, 0.6, Math.Cos(a) * 0.7);
                if (Grid.BoxOverlapsSolid(new Aabb(spot - new Vec3d(0.1, 0.1, 0.1), spot + new Vec3d(0.1, 0.1, 0.1)))) spot = at + new Vec3d(0, 0.6, 0);
                var g = new GroundItem
                {
                    Kind = kind, Count = b.CountIn(slot), Rarity = b.RarityOf(slot), Position = ItemRules.Settle(Grid, spot),
                    DroppedBy = p.Id, DroppedTick = Tick,
                };
                if (global::Sculpt.Core.Items.IsGun(kind)) g.AmmoUsed = p.State.Weapon.UsedOf(global::Sculpt.Core.Items.GunOf(kind));
                Items.Add(g, Movement.Items.MaxGroundItems);
            }
            p.State.Hand.Bag = default;
        }

        /// <summary>End of each tick's simulation: teams wiped out this tick get their place; one team left wins.</summary>
        private void StepBrEnd()
        {
            if (Br == null) return;
            if (Match.Phase == MatchPhase.Playing)
            {
                // Teams with someone alive now.
                var alive = _brTeamsNow;
                alive.Clear();
                int alivePlayers = 0;
                foreach (ServerPlayer p in _players)
                {
                    if (!_brIn.Contains(p.Id) || p.State.Dead) continue;
                    alive.Add(p.Team);
                    alivePlayers++;
                }
                var gone = _brGoneNow;
                gone.Clear();
                foreach (int t in _brAliveTeams) if (!alive.Contains(t)) gone.Add(t);
                if (gone.Count > 0)
                {
                    // Teams out on the same tick share the place.
                    int place = alive.Count + 1;
                    foreach (int t in gone) { _brPlace[t] = place; _brAliveTeams.Remove(t); }
                    _matchDirty = true;
                }
                if (alivePlayers != Match.AlivePlayers || alive.Count != Match.AliveTeams) _matchDirty = true;
                Match.AlivePlayers = alivePlayers;
                Match.AliveTeams = alive.Count;
                if (alive.Count <= 1)
                {
                    int winner = 0;
                    foreach (int t in alive) winner = t;
                    if (winner != 0) _brPlace[winner] = 1;
                    Match.WinnerTeam = winner; // 0: the last teams went down together
                    SetPhase(MatchPhase.Ended, Tick + Br.ResultsTicks);
                }
            }
        }

        private readonly HashSet<int> _brTeamsNow = new HashSet<int>();
        private readonly List<int> _brGoneNow = new List<int>();

        /// <summary>After the tick (the stream has closed it): fill the lobby with bots, or make room for humans.</summary>
        private void BrBetweenTicks()
        {
            if (Br?.MakeBot == null) return;
            bool between = Match.Phase == MatchPhase.Waiting || Match.Phase == MatchPhase.Off || Match.Phase == MatchPhase.Ended;
            if (!between) return;
            int humans = 0, total = 0;
            foreach (ServerPlayer p in _players)
            {
                if (!CountsForMatch(p)) continue;
                total++;
                if (!IsBotConnection(p.ConnectionId)) humans++;
            }
            if (humans == 0 && !Br.FillWithoutHumans)
            {
                // Nobody to play with: the bots go too (a hosted server waits empty).
                for (int i = _players.Count - 1; i >= 0; i--)
                    if (IsBotConnection(_players[i].ConnectionId) && _brains.ContainsKey(_players[i].Id)) RemovePlayer(_players[i].ConnectionId);
                return;
            }
            // Too many (a human joined a full lobby): the newest bot makes room.
            for (int i = _players.Count - 1; i >= 0 && total > Br.MaxPlayers; i--)
            {
                ServerPlayer p = _players[i];
                if (!IsBotConnection(p.ConnectionId) || !_brains.ContainsKey(p.Id)) continue;
                RemovePlayer(p.ConnectionId);
                total--;
            }
            while (total < Br.MaxPlayers)
            {
                IBotBrain brain = Br.MakeBot(_brBotsMade++);
                if (brain == null) break;
                BrRegion r = BrMap.Regions[total % BrMap.Regions.Count];
                AddBot(brain, 1, r.SquadSpots[total % 4], r.SquadYaw);
                total++;
            }
        }

        // ================================================================ Chests

        /// <summary>
        /// Opens a chest (interact on it, within reach and sight): it goes, and a random gun of a random rarity plus a
        /// utility item fall out in front of it.
        /// </summary>
        private PickupResult OpenChest(ServerPlayer p, in GroundItem chest, Vec3d eye)
        {
            ItemConfig cfg = Movement.Items;
            if ((chest.Centre - eye).Length > cfg.Reach + cfg.ServerReachSlack) return PickupResult.TooFar;
            if (!ItemRules.Visible(Grid, eye, chest.Centre)) return PickupResult.Blocked;
            Items.Remove(chest.Id);
            double yaw = ChestYaw(chest);
            Vec3d fwd = new Vec3d(Math.Sin(yaw), 0, Math.Cos(yaw)), side = new Vec3d(fwd.Z, 0, -fwd.X);
            Vec3d front = chest.Position + new Vec3d(0, 0.5, 0) + fwd * Br.ChestSpill;
            if (Grid.BoxOverlapsSolid(new Aabb(front - new Vec3d(0.2, 0.2, 0.2), front + new Vec3d(0.2, 0.2, 0.2)))) front = chest.Position + new Vec3d(0, 0.5, 0);

            Item gun = Pick(Br.ChestGuns, Br.ChestGunWeights, BrRoll(5, chest.Id, 0));
            Rarity rar = (Rarity)(1 + PickIndex(Br.RarityWeights, BrRoll(6, chest.Id, 0)));
            Items.Add(new GroundItem { Kind = gun, Count = 1, Rarity = rar, Position = ItemRules.Settle(Grid, front - side * 0.4), DroppedTick = int.MinValue / 2 }, cfg.MaxGroundItems);
            Item util = Pick(Br.ChestUtility, Br.ChestUtilityWeights, BrRoll(7, chest.Id, 0));
            if (util != Item.None)
                Items.Add(new GroundItem { Kind = util, Count = Math.Max(1, cfg.Carry(util)), Position = ItemRules.Settle(Grid, front + side * 0.4), DroppedTick = int.MinValue / 2 }, cfg.MaxGroundItems);
            int hi = Br.ChestHeals == null || Br.ChestHeals.Length == 0 ? -1 : Math.Min(Br.ChestHeals.Length - 1, PickIndex(Br.ChestHealWeights, BrRoll(8, chest.Id, 0)));
            if (hi >= 0)
            {
                Item heal = Br.ChestHeals[hi];
                int n = Br.ChestHealCounts != null && hi < Br.ChestHealCounts.Length ? Br.ChestHealCounts[hi] : 1;
                n = Math.Max(1, Math.Min(n, cfg.Carry(heal)));
                Items.Add(new GroundItem { Kind = heal, Count = n, Position = ItemRules.Settle(Grid, front), DroppedTick = int.MinValue / 2 }, cfg.MaxGroundItems);
            }
            return PickupResult.Opened;
        }

        /// <summary>A chest's facing rides in its ammo byte (chests have no ammo): 0..255 is one turn.</summary>
        public static double ChestYaw(in GroundItem chest) => chest.AmmoUsed / 256.0 * 2 * Math.PI;

        public static int ChestYawByte(double yaw)
        {
            double t = yaw / (2 * Math.PI);
            t -= Math.Floor(t);
            return (int)Math.Round(t * 256) & 255;
        }

        /// <summary>Puts a chest on the map (map setup).</summary>
        public int SpawnChest(Vec3d at, double yaw) =>
            Items.Add(new GroundItem { Kind = Item.Chest, Count = 1, AmmoUsed = ChestYawByte(yaw), Position = ItemRules.Settle(Grid, at + new Vec3d(0, 0.3, 0)), DroppedTick = int.MinValue / 2 }, Movement.Items.MaxGroundItems);

        private static Item Pick(Item[] kinds, int[] weights, uint roll)
        {
            if (kinds == null || kinds.Length == 0) return Item.None;
            return kinds[Math.Min(kinds.Length - 1, PickIndex(weights, roll))];
        }

        private static int PickIndex(int[] weights, uint roll)
        {
            int total = 0;
            foreach (int w in weights) total += Math.Max(0, w);
            if (total <= 0) return 0;
            int r = (int)(roll % (uint)total);
            for (int i = 0; i < weights.Length; i++)
            {
                r -= Math.Max(0, weights[i]);
                if (r < 0) return i;
            }
            return weights.Length - 1;
        }

        /// <summary>One random number for (what, which), fixed by the seed and the match number.</summary>
        private uint BrRoll(int what, int which, int salt) =>
            WeaponRules.Hash((uint)Br.Seed * 0x9E3779B1u + (uint)BrMatchNumber, (uint)what * 0x85EBCA77u + (uint)salt, (uint)which);

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>Puts the battle royale map's chests and pickups on a server.</summary>
    public static class BrSetup
    {
        public static void PlaceItems(NetServer server, BreachMapInfo info, BrMapInfo br)
        {
            foreach (BrChestSpot c in br.Chests) server.SpawnChest(c.Position, c.Yaw);
            foreach (GroundItem g in info.ItemSpawns) server.SpawnItem(g.Kind, g.Count, g.Position);
        }
    }
}
