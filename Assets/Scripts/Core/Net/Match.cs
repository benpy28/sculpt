using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Friend demo mode (Ben 2026-11-25): most kills in 5 minutes wins. Server-authoritative; clients only draw it.
    /// Waiting (fewer than two real players: free play) -> Countdown (map and players reset, nobody can be hurt) ->
    /// Playing (kills count) -> Ended (results, nobody can be hurt) -> Countdown again while two players are still in.
    /// </summary>
    public enum MatchPhase : byte
    {
        /// <summary>No match: offline, or waiting for an opponent. Free play, kills don't count.</summary>
        Off = 0,
        Waiting = 1,
        Countdown = 2,
        Playing = 3,
        Ended = 4,
    }

    public sealed class MatchConfig
    {
        /// <summary>Server switch. Off: no match logic at all (offline practice, the breach test map).</summary>
        public bool Enabled;
        public int MinPlayers = 2;
        public int CountdownTicks = (3 * 60) * SimTime.Per60;
        public int DurationTicks = (5 * 60 * 60) * SimTime.Per60;
        public int ResultsTicks = (10 * 60) * SimTime.Per60;
    }

    public struct MatchScore
    {
        public int PlayerId;
        public int Team;
        public int Kills;
        public int Deaths;
    }

    /// <summary>What every client knows about the match.</summary>
    public sealed class MatchState
    {
        public MatchPhase Phase;
        /// <summary>Server tick the current phase ends (Countdown, Playing, Ended); 0 otherwise.</summary>
        public int PhaseEndTick;
        /// <summary>Server tick the current phase began (Countdown starts are when the map was reset).</summary>
        public int PhaseStartTick;
        /// <summary>Bumped on every change, so views can tell something happened.</summary>
        public int Version;
        public readonly List<MatchScore> Scores = new List<MatchScore>();

        public bool Protects => Phase == MatchPhase.Countdown || Phase == MatchPhase.Ended;
        public bool CountsKills => Phase == MatchPhase.Playing;

        /// <summary>The leader's id, or 0 on a tie (or no scores).</summary>
        public int Leader()
        {
            int best = int.MinValue, id = 0;
            bool tie = false;
            foreach (MatchScore s in Scores)
            {
                if (s.Kills > best) { best = s.Kills; id = s.PlayerId; tie = false; }
                else if (s.Kills == best) tie = true;
            }
            return tie ? 0 : id;
        }

        public MatchScore ScoreOf(int playerId)
        {
            foreach (MatchScore s in Scores) if (s.PlayerId == playerId) return s;
            return new MatchScore { PlayerId = playerId };
        }

        public void CopyFrom(MatchState o)
        {
            Phase = o.Phase;
            PhaseEndTick = o.PhaseEndTick;
            PhaseStartTick = o.PhaseStartTick;
            Version++;
            Scores.Clear();
            Scores.AddRange(o.Scores);
        }
    }

    /// <summary>MsgType.Match, reliable: the whole match state (tiny), sent on every change and to joiners.</summary>
    public static class MatchProtocol
    {
        public const int MaxScores = 64;

        public static void Write(ByteWriter w, MatchState m, int tick)
        {
            int n = System.Math.Min(MaxScores, m.Scores.Count);
            w.U8((byte)MsgType.Match);
            w.I32(tick);
            w.U8((byte)m.Phase);
            w.I32(m.PhaseStartTick);
            w.I32(m.PhaseEndTick);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                MatchScore s = m.Scores[i];
                w.I32(s.PlayerId);
                w.U8((byte)s.Team);
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, s.Kills)));
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, s.Deaths)));
            }
        }

        public static void Read(ref ByteReader r, MatchState into)
        {
            if (r.U8() != (byte)MsgType.Match) throw new NetFormatException("Unexpected message type.");
            r.I32(); // tick sent (unused)
            byte phase = r.U8();
            if (phase > (byte)MatchPhase.Ended) throw new NetFormatException("Bad match phase.");
            into.Phase = (MatchPhase)phase;
            into.PhaseStartTick = r.I32();
            into.PhaseEndTick = r.I32();
            int n = r.U8();
            if (n > MaxScores) throw new NetFormatException("Too many scores.");
            into.Scores.Clear();
            for (int i = 0; i < n; i++)
                into.Scores.Add(new MatchScore { PlayerId = r.I32(), Team = r.U8(), Kills = r.U16(), Deaths = r.U16() });
            into.Version++;
        }
    }
}
