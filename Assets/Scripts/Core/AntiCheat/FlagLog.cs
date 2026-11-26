using System;
using System.Collections.Generic;
using System.Text;
using Sculpt.Core.Net;

namespace Sculpt.Core.AntiCheat
{
    /// <summary>
    /// What a flag is about. Grouped by <see cref="FlagCategory"/>; the number is stored in replays, so never reuse one.
    /// </summary>
    public enum FlagKind : byte
    {
        // Input: the client sent something an honest build never sends. The server already corrected it.
        BadView = 1,            // pitch outside +-89 or a yaw that wasn't wrapped (clamped)
        DevRespawnRefused = 2,  // a respawn press on a server that doesn't allow dev respawns (refused)
        DevWarpRefused = 3,     // a dev warp on a server that doesn't allow it (refused)
        DoorNoSight = 4,        // a door interact for a leaf the eye has no line of sight to (refused)

        // Network: timing the server can see. Packet loss and ping changes look a bit like these, so they are weak alone.
        ClockAhead = 10,        // commands further ahead than the server accepts (the packet was ignored)
        Malformed = 11,         // a packet that didn't parse (dropped)
        ViewBehindAck = 12,     // a hit from a view much older than the newest snapshot the client said it had
        ViewLagJump = 13,       // a hit from a view much older than the player's usual view lag (backtracking)

        // Integrity: the server's own rules gave an impossible result. Never the client alone: a rules bug or an exploit.
        SpeedOverCap = 20,
        Teleport = 21,          // moved further in one tick than its speed allows (not a respawn)
        FireTooFast = 22,       // two shots of one gun closer than its fire interval
        MagOverflow = 23,       // more rounds fired than the magazine holds

        // Interest: a hit on someone the shooter wasn't being sent. Either interest management is too tight (pop-in
        // risk, a bug to fix) or a blind shot; should be zero in normal play.
        HitUnseen = 30,

        // Aim statistics: legit players do these sometimes; only a pattern over a match means anything.
        AimSnap = 40,           // a fast turn that stopped dead on the tick of a hit
        FastReaction = 41,      // a first shot hitting someone who came into sight under 90 ms before
        PhantomTrack = 42,      // the aim stayed locked on a hidden enemy's head while the bearing swept (through-wall tracking)
    }

    public enum FlagCategory : byte { Input, Network, Integrity, Interest, Aim }

    /// <summary>One suspicious event: who, when, what, and a number whose meaning depends on the kind (see <see cref="FlagLog.Describe"/>).</summary>
    public struct Flag
    {
        public int Tick;
        public int Player;
        /// <summary>The other player involved (the victim of a hit), or 0.</summary>
        public int Other;
        public FlagKind Kind;
        public int Value;

        public bool Equals(in Flag o) => Tick == o.Tick && Player == o.Player && Other == o.Other && Kind == o.Kind && Value == o.Value;
        public override string ToString() => $"t{Tick} P{Player} {Kind}{(Other != 0 ? " vs P" + Other : "")} {FlagLog.Describe(Kind, Value)}";
    }

    /// <summary>Per player counts for a review: shots, hits, headshot hits, and every flag kind.</summary>
    public sealed class PlayerFlagStats
    {
        public int Shots, Hits, HeadHits;
        public readonly Dictionary<FlagKind, int> Counts = new Dictionary<FlagKind, int>();
        public int Count(FlagKind k) => Counts.TryGetValue(k, out int n) ? n : 0;
        public double HitShare => Shots > 0 ? Hits / (double)Shots : 0;
        public double HeadShare => Hits > 0 ? HeadHits / (double)Hits : 0;
    }

    /// <summary>
    /// The server's log of suspicious events. Plain data, filled by the server's checks, never acted on: it is for
    /// review (a replay jumps to each flag) and later for ranked and cash-cup eligibility. Flags from the simulation
    /// (integrity, interest, aim) come out the same when a replay's inputs are re-run (see ReplayVerifier), so a
    /// recorded log can be proven; input and network flags depend on packets and are only as good as the recording.
    /// </summary>
    public sealed class FlagLog
    {
        public readonly List<Flag> Flags = new List<Flag>();
        public readonly Dictionary<int, PlayerFlagStats> Players = new Dictionary<int, PlayerFlagStats>();
        private readonly Dictionary<long, int> _lastLogged = new Dictionary<long, int>();
        public int RateEveryTicks = SimTime.TicksPerSecond;
        public int MaxFlags = 20000;
        /// <summary>Flags counted but not written to <see cref="Flags"/> (rate limit or full).</summary>
        public int Suppressed { get; private set; }

        /// <summary>Called for every flag that goes in the log (the replay stream records it).</summary>
        public event Action<Flag> Logged;

        public static FlagCategory CategoryOf(FlagKind k) =>
            (byte)k < 10 ? FlagCategory.Input : (byte)k < 20 ? FlagCategory.Network : (byte)k < 30 ? FlagCategory.Integrity :
            (byte)k < 40 ? FlagCategory.Interest : FlagCategory.Aim;

        /// <summary>True for flags worked out from the simulation alone, which a re-run of the inputs reproduces exactly.</summary>
        public static bool FromSimulation(FlagKind k)
        {
            FlagCategory c = CategoryOf(k);
            return c == FlagCategory.Integrity || c == FlagCategory.Interest || c == FlagCategory.Aim;
        }

        public PlayerFlagStats StatsOf(int player)
        {
            if (!Players.TryGetValue(player, out PlayerFlagStats s)) Players[player] = s = new PlayerFlagStats();
            return s;
        }

        public int Count(FlagKind k)
        {
            int n = 0;
            foreach (PlayerFlagStats s in Players.Values) n += s.Count(k);
            return n;
        }

        public int CountCategory(FlagCategory c)
        {
            int n = 0;
            foreach (PlayerFlagStats s in Players.Values)
                foreach (var kv in s.Counts) if (CategoryOf(kv.Key) == c) n += kv.Value;
            return n;
        }

        /// <summary>Counts the flag and logs it unless rate-limited or full. Returns whether it went in the log.</summary>
        public bool Add(int tick, int player, FlagKind kind, int value = 0, int other = 0)
        {
            PlayerFlagStats st = StatsOf(player);
            st.Counts[kind] = st.Count(kind) + 1;
            long key = ((long)player << 8) | (byte)kind;
            if ((_lastLogged.TryGetValue(key, out int last) && tick - last < RateEveryTicks) || Flags.Count >= MaxFlags)
            {
                Suppressed++;
                return false;
            }
            _lastLogged[key] = tick;
            var f = new Flag { Tick = tick, Player = player, Other = other, Kind = kind, Value = value };
            Flags.Add(f);
            Logged?.Invoke(f);
            return true;
        }

        /// <summary>What a flag's number means, in words.</summary>
        public static string Describe(FlagKind k, int v)
        {
            switch (k)
            {
                case FlagKind.BadView: return $"(pitch {v / 10.0:0.#} deg)";
                case FlagKind.DoorNoSight: return $"(door tile {v}, no line of sight)";
                case FlagKind.ClockAhead: return $"({v} ticks ahead)";
                case FlagKind.ViewBehindAck: return $"({v} ticks behind its ack)";
                case FlagKind.ViewLagJump: return $"(view lag {v} ticks over its median)";
                case FlagKind.SpeedOverCap: return $"({v / 100.0:0.0} m/s)";
                case FlagKind.Teleport: return $"({v / 100.0:0.00} m in a tick)";
                case FlagKind.FireTooFast: return $"({v} ticks apart)";
                case FlagKind.MagOverflow: return $"({v} rounds used)";
                case FlagKind.HitUnseen: return $"(last sent {v} ticks before the view)";
                case FlagKind.AimSnap: return $"({v / 10.0:0.#} deg in a flick)";
                case FlagKind.FastReaction: return $"({v} ms after coming into sight)";
                case FlagKind.PhantomTrack: return $"(locked on a hidden enemy through walls while bearing swept {v} deg)";
                default: return v != 0 ? $"({v})" : "";
            }
        }

        /// <summary>A short report per player (for a reviewer, the dev HUD or a test log).</summary>
        public string Report()
        {
            var sb = new StringBuilder();
            var ids = new List<int>(Players.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                PlayerFlagStats s = Players[id];
                sb.Append($"P{id}: {s.Shots} shots, {s.Hits} hits ({s.HitShare:P0}), {s.HeadHits} head ({s.HeadShare:P0} of hits)");
                var kinds = new List<FlagKind>(s.Counts.Keys);
                kinds.Sort();
                foreach (FlagKind k in kinds) sb.Append($", {k} x{s.Counts[k]}");
                sb.Append('\n');
            }
            if (Suppressed > 0) sb.Append($"({Suppressed} more flags counted but not listed)\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- wire (replay records)

        public static void Write(ByteWriter w, in Flag f)
        {
            w.VarU((ulong)Math.Max(0, f.Tick));
            w.VarU((ulong)Math.Max(0, f.Player));
            w.VarU((ulong)Math.Max(0, f.Other));
            w.U8((byte)f.Kind);
            w.VarS(f.Value);
        }

        public static Flag Read(ref ByteReader r) => new Flag
        {
            Tick = r.VarCount(int.MaxValue),
            Player = r.VarCount(int.MaxValue),
            Other = r.VarCount(int.MaxValue),
            Kind = (FlagKind)r.U8(),
            Value = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, r.VarS())),
        };
    }
}
