using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>What a re-run of a replay's inputs found.</summary>
    public sealed class ReplayCheck
    {
        public int TicksRun;
        public int HashesChecked;
        public int FirstMismatchTick = -1;
        /// <summary>Anti-cheat flags worked out from the simulation (integrity, interest, aim) in the recording, and whether the re-run logged exactly the same ones.</summary>
        public int FlagsChecked;
        public bool FlagsMatch = true;
        /// <summary>Every anti-cheat flag in the recording, both the re-run-verifiable ones and the host-recorded input/network ones. A non-zero count means the match is NOT clean, whatever <see cref="Passed"/> says.</summary>
        public int FlagsLogged;
        /// <summary>The re-run server's flag log (with per-player shot and hit counts), for a reviewer.</summary>
        public AntiCheat.FlagLog ReRunFlags;
        /// <summary>Why the check couldn't run (no inputs, recorded mid-match, a join the re-run couldn't match), or null.</summary>
        public string Problem;

        /// <summary>
        /// The recording is internally CONSISTENT: it re-runs to the same state hashes and its simulation flags reproduce.
        /// This proves the record wasn't tampered with and no simulation-level cheat (speed, teleport, impossible fire,
        /// hit-unseen, aim stats) fired. It does NOT mean "no cheating": a match full of logged flags still passes. Gate
        /// ranked/cup eligibility on <see cref="Clean"/>, never on Passed alone.
        /// </summary>
        public bool Passed => Problem == null && FirstMismatchTick < 0 && HashesChecked > 0 && FlagsMatch;

        /// <summary>
        /// Eligible as a clean match: consistent AND no anti-cheat flag of any kind was logged. On a listen server the
        /// input/network half of the flags is host-written and strippable, so Clean is only trustworthy from a dedicated
        /// server whose flags are attested; a reviewer should still read <see cref="ReRunFlags"/>.
        /// </summary>
        public bool Clean => Passed && FlagsLogged == 0;

        public override string ToString() =>
            Problem != null ? "can't check: " + Problem :
            FirstMismatchTick >= 0 ? $"MISMATCH at tick {FirstMismatchTick} ({HashesChecked} hashes checked, {TicksRun} ticks run)" :
            !FlagsMatch ? $"state matches ({HashesChecked} hashes) but the anti-cheat flags differ from the recording's {FlagsChecked}" :
            $"{(Clean ? "clean" : FlagsLogged + " flag(s) logged — NOT clean")}: {HashesChecked} state hashes over {TicksRun} ticks, {FlagsChecked} simulation flags reproduced";
    }

    /// <summary>
    /// Proves a match: runs a fresh server on the same map with every player's recorded command, tick by tick, and
    /// checks the exact state hash the recording server wrote. The groundwork for reviewing reported matches and cup
    /// games: the server's record can be re-run by anyone with the same build, and any tampering or desync shows.
    /// Needs a stream started before anyone joined (the Game's local and hosted servers do that). The practice dummy
    /// acts on its own outside its commands, so maps with one may not match.
    /// </summary>
    public static class ReplayVerifier
    {
        private struct Join
        {
            public int Tick, Id, Team;
            public JoinKind Kind;
            public bool Counts;
            public Vec3d Spawn;
            public double Yaw;
        }

        /// <summary>A bot whose every command comes from the recording.</summary>
        private sealed class Scripted : IBotBrain
        {
            public bool CountsAsPlayer { get; set; }
            public Dictionary<long, InputCmd> Cmds;
            public InputCmd Next(NetServer server, ServerPlayer me) =>
                Cmds.TryGetValue(Key(server.Tick, me.Id), out InputCmd c) ? c : new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        private static long Key(int tick, int id) => ((long)tick << 20) ^ (uint)id;

        /// <param name="makeServer">A fresh server on the same map with the same setup (items), and no players: the
        /// recording adds them. Its tick must be where the recording started.</param>
        public static ReplayCheck Run(ReplayFile file, Func<NetServer> makeServer)
        {
            var check = new ReplayCheck();
            if (!file.Header.HasInputs) { check.Problem = "the replay has no inputs"; return check; }
            if (file.Incompatible != null) { check.Problem = file.Incompatible; return check; }

            var cmds = new Dictionary<long, InputCmd>();
            var hashes = new SortedDictionary<int, ulong>();
            var joins = new List<Join>();
            var leaves = new List<KeyValuePair<int, int>>();
            var lockers = new List<(int Tick, int Id, Item Gun)>();
            var prefs = new List<(int Tick, int Id, SlotPrefs Prefs)>();
            int? brSeed = null;
            for (int i = 0; i < file.Segments.Count; i++)
            {
                byte[] raw = file.SegmentBytes(i);
                var log = new InputLogCodec();
                foreach (ReplayFile.Record rec in ReplayFile.Records(raw))
                {
                    var r = new ByteReader(raw, rec.Offset, rec.Length);
                    switch (rec.Kind)
                    {
                        case ReplayRecord.Inputs:
                        {
                            int tick = (int)r.VarU();
                            int n = r.VarCount(1 << 16);
                            for (int k = 0; k < n; k++)
                            {
                                InputCmd c = log.Read(ref r, tick, out int id);
                                cmds[Key(tick, id)] = c;
                            }
                            break;
                        }
                        case ReplayRecord.Hash:
                        {
                            int tick = (int)r.VarU();
                            hashes[tick] = unchecked((ulong)r.I64());
                            break;
                        }
                        case ReplayRecord.Join:
                            joins.Add(new Join
                            {
                                Tick = (int)r.VarU(), Id = (int)r.VarU(), Kind = (JoinKind)r.U8(), Counts = r.Bool(),
                                Team = r.U8(), Spawn = r.Vec(), Yaw = r.F64(),
                            });
                            break;
                        case ReplayRecord.Locker:
                            lockers.Add(((int)r.VarU(), (int)r.VarU(), (Item)r.U8()));
                            break;
                        case ReplayRecord.Prefs:
                        {
                            int pt = (int)r.VarU(), pid = (int)r.VarU();
                            var sp = new SlotPrefs();
                            for (int k = 0; k < Items.Slots; k++) sp[k] = (SlotPref)r.U8();
                            prefs.Add((pt, pid, sp));
                            break;
                        }
                        case ReplayRecord.BrSeed:
                            r.VarU();
                            brSeed = r.I32();
                            break;
                        case ReplayRecord.Leave:
                            leaves.Add(new KeyValuePair<int, int>((int)r.VarU(), (int)r.VarU()));
                            break;
                    }
                }
            }

            var recorded = new List<AntiCheat.Flag>();
            foreach (AntiCheat.Flag fl in file.Flags)
            {
                check.FlagsLogged++;
                if (AntiCheat.FlagLog.FromSimulation(fl.Kind)) recorded.Add(fl);
            }

            NetServer s = makeServer();
            check.ReRunFlags = s.Flags;
            int start = file.FirstTick - 1;
            if (s.Tick != start) { check.Problem = $"the fresh server starts at tick {s.Tick}, the recording at {start}"; return check; }
            if (s.Players.Count != 0) { check.Problem = "the fresh server already has players"; return check; }
            if (brSeed.HasValue)
            {
                // The recording server's own seed (the Game picks a new one per run), not whatever the fresh server got.
                if (s.Br == null) { check.Problem = "the recording is a battle royale, the fresh server isn't"; return check; }
                s.Br.Seed = brSeed.Value;
            }
            int ji = 0, li = 0, ki = 0, pi = 0;
            var byId = new Dictionary<int, ServerPlayer>();
            for (int t = file.FirstTick; t <= file.LastTick; t++)
            {
                for (; li < leaves.Count && leaves[li].Key <= s.Tick; li++)
                    if (byId.TryGetValue(leaves[li].Value, out ServerPlayer gone)) { s.RemovePlayer(gone.ConnectionId); byId.Remove(gone.Id); }
                for (; ji < joins.Count && joins[ji].Tick <= s.Tick; ji++)
                {
                    Join j = joins[ji];
                    ServerPlayer p = s.AddBot(new Scripted { Cmds = cmds, CountsAsPlayer = j.Counts }, j.Team, j.Spawn, j.Yaw);
                    p.Viewer = j.Kind == JoinKind.Human; // interest is worked out as it was live
                    if (p.Id != j.Id) { check.Problem = $"player {j.Id} came back as {p.Id}"; return check; }
                    byId[p.Id] = p;
                }
                for (; ki < lockers.Count && lockers[ki].Tick <= s.Tick; ki++)
                    if (byId.TryGetValue(lockers[ki].Id, out ServerPlayer lp)) s.SetLocker(lp, lockers[ki].Gun);
                for (; pi < prefs.Count && prefs[pi].Tick <= s.Tick; pi++)
                    if (byId.TryGetValue(prefs[pi].Id, out ServerPlayer pp)) s.SetSlotPrefs(pp, prefs[pi].Prefs);
                s.Step();
                check.TicksRun++;
                if (hashes.TryGetValue(t, out ulong want))
                {
                    check.HashesChecked++;
                    if (MatchStream.StateHash(s) != want) { check.FirstMismatchTick = t; return check; }
                }
            }
            var again = new List<AntiCheat.Flag>();
            foreach (AntiCheat.Flag fl in s.Flags.Flags)
                if (AntiCheat.FlagLog.FromSimulation(fl.Kind) && fl.Tick >= file.FirstTick && fl.Tick <= file.LastTick) again.Add(fl);
            check.FlagsChecked = recorded.Count;
            check.FlagsMatch = again.Count == recorded.Count;
            for (int i = 0; check.FlagsMatch && i < recorded.Count; i++) check.FlagsMatch = recorded[i].Equals(again[i]);
            return check;
        }
    }
}
