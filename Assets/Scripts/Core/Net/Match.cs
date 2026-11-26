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
        /// <summary>Battle royale: where this player's team finished (1 = won), 0 while it is still in.</summary>
        public int Place;
    }

    /// <summary>Battle royale team sizes (0 = not a battle royale).</summary>
    public enum BrMode : byte { None = 0, Solos = 1, Duos = 2, Squads = 3 }

    /// <summary>
    /// The battle royale circle as every client knows it: during a phase it waits (showing where the next circle will
    /// be), then shrinks from <see cref="From"/> to <see cref="To"/> between <see cref="ShrinkStart"/> and
    /// <see cref="ShrinkEnd"/>. Outside it you take <see cref="Dps"/> once a second. Clients work out the radius at any
    /// tick from these numbers, so the message only goes out when a phase starts.
    /// </summary>
    public struct StormState
    {
        /// <summary>Phase index, -1 before the first.</summary>
        public int Phase;
        public int PhaseCount;
        public double FromX, FromZ, FromRadius;
        public double ToX, ToZ, ToRadius;
        public int ShrinkStart, ShrinkEnd;
        public int Dps;

        public bool Active => Phase >= 0;

        /// <summary>0 before the shrink, 1 after it.</summary>
        public double Progress(double tick)
        {
            if (tick <= ShrinkStart) return 0;
            if (tick >= ShrinkEnd || ShrinkEnd <= ShrinkStart) return 1;
            return (tick - ShrinkStart) / (ShrinkEnd - ShrinkStart);
        }

        public double RadiusAt(double tick) { double f = Progress(tick); return FromRadius + (ToRadius - FromRadius) * f; }
        public double CentreXAt(double tick) { double f = Progress(tick); return FromX + (ToX - FromX) * f; }
        public double CentreZAt(double tick) { double f = Progress(tick); return FromZ + (ToZ - FromZ) * f; }

        /// <summary>How far outside the circle a point is (negative inside).</summary>
        public double OutsideBy(Vec3d p, double tick)
        {
            double dx = p.X - CentreXAt(tick), dz = p.Z - CentreZAt(tick);
            return System.Math.Sqrt(dx * dx + dz * dz) - RadiusAt(tick);
        }
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
        /// <summary>Battle royale (None for the 1v1).</summary>
        public BrMode Mode;
        public int TeamSize = 1;
        public int AliveTeams, AlivePlayers;
        /// <summary>The team that won the last battle royale (0 = none yet, or nobody).</summary>
        public int WinnerTeam;
        public StormState Storm;

        public bool IsBr => Mode != BrMode.None;

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
            Mode = o.Mode;
            TeamSize = o.TeamSize;
            AliveTeams = o.AliveTeams;
            AlivePlayers = o.AlivePlayers;
            WinnerTeam = o.WinnerTeam;
            Storm = o.Storm;
        }
    }

    /// <summary>MsgType.Match, reliable: the whole match state (tiny), sent on every change and to joiners.</summary>
    public static class MatchProtocol
    {
        private static double Finite(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new NetFormatException("Bad number.");
            return v;
        }

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
                w.U8((byte)System.Math.Max(0, System.Math.Min(255, s.Place)));
            }
            w.U8((byte)m.Mode);
            if (m.Mode == BrMode.None) return;
            w.U8((byte)m.TeamSize);
            w.U8((byte)System.Math.Min(255, m.AliveTeams));
            w.U8((byte)System.Math.Min(255, m.AlivePlayers));
            w.U8((byte)System.Math.Max(0, System.Math.Min(255, m.WinnerTeam)));
            StormState st = m.Storm;
            w.U8((byte)(st.Phase + 1));
            w.U8((byte)st.PhaseCount);
            if (st.Phase < 0) return;
            w.F32((float)st.FromX); w.F32((float)st.FromZ); w.F32((float)st.FromRadius);
            w.F32((float)st.ToX); w.F32((float)st.ToZ); w.F32((float)st.ToRadius);
            w.I32(st.ShrinkStart); w.I32(st.ShrinkEnd);
            w.U8((byte)System.Math.Max(0, System.Math.Min(255, st.Dps)));
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
                into.Scores.Add(new MatchScore { PlayerId = r.I32(), Team = r.U8(), Kills = r.U16(), Deaths = r.U16(), Place = r.U8() });
            byte mode = r.U8();
            if (mode > (byte)BrMode.Squads) throw new NetFormatException("Bad mode.");
            into.Mode = (BrMode)mode;
            into.Storm = new StormState { Phase = -1 };
            into.AliveTeams = into.AlivePlayers = into.WinnerTeam = 0;
            into.TeamSize = 1;
            if (into.Mode != BrMode.None)
            {
                into.TeamSize = r.U8();
                if (into.TeamSize < 1 || into.TeamSize > 4) throw new NetFormatException("Bad team size.");
                into.AliveTeams = r.U8();
                into.AlivePlayers = r.U8();
                into.WinnerTeam = r.U8();
                var st = new StormState { Phase = r.U8() - 1, PhaseCount = r.U8() };
                if (st.Phase >= 0)
                {
                    st.FromX = Finite(r.F32()); st.FromZ = Finite(r.F32()); st.FromRadius = Finite(r.F32());
                    st.ToX = Finite(r.F32()); st.ToZ = Finite(r.F32()); st.ToRadius = Finite(r.F32());
                    st.ShrinkStart = r.I32(); st.ShrinkEnd = r.I32();
                    st.Dps = r.U8();
                    if (st.FromRadius < 0 || st.ToRadius < 0) throw new NetFormatException("Bad circle.");
                }
                into.Storm = st;
            }
            into.Version++;
        }
    }
}
