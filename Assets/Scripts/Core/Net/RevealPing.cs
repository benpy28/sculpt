using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Anti-hiding ping for the 1v1 (Ben 2026-11-25): when two enemies have gone a while with no contact (no damage
    /// between them, no clear line between them, not close), the server shows each to the other through walls for a
    /// short time. Only while a match is being played. The Game layer's Breach Settings asset writes into this.
    /// </summary>
    public sealed class PingConfig
    {
        public bool Enabled = true;
        /// <summary>No contact for this long fires the ping, seconds.</summary>
        public double QuietSeconds = 10;
        /// <summary>How long both stay revealed, seconds.</summary>
        public double RevealSeconds = 3;
        /// <summary>Being this close counts as contact (you can hear each other), metres. 0: distance never counts.</summary>
        public double ContactRange = 10;

        public int QuietTicks => System.Math.Max(1, SimTime.SecondsToTicks(QuietSeconds));
        public int RevealTicks => System.Math.Max(1, SimTime.SecondsToTicks(RevealSeconds));
    }
}

namespace Sculpt.Core.Net
{
    /// <summary>One player shown to the receiver through walls until <see cref="EndTick"/> (an end at or before the message tick cancels).</summary>
    public struct RevealEntry
    {
        public int PlayerId;
        public int EndTick;
        /// <summary>Where they were when the ping fired (feet).</summary>
        public Vec3d Position;
    }

    /// <summary>
    /// MsgType.Reveal, reliable, sent only to the players a ping concerns and only when it starts or is cancelled. Nothing
    /// about a hidden enemy goes out at any other time through this message.
    /// </summary>
    public static class RevealProtocol
    {
        public const int MaxEntries = 32;

        public static void Write(ByteWriter w, int tick, List<RevealEntry> entries)
        {
            int n = System.Math.Min(MaxEntries, entries.Count);
            w.U8((byte)MsgType.Reveal);
            w.I32(tick);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                RevealEntry e = entries[i];
                w.I32(e.PlayerId);
                w.I32(e.EndTick);
                w.VecF(e.Position);
            }
        }

        public static int Read(ref ByteReader r, List<RevealEntry> into)
        {
            if (r.U8() != (byte)MsgType.Reveal) throw new NetFormatException("Unexpected message type.");
            int tick = r.I32();
            int n = r.U8();
            if (n > MaxEntries) throw new NetFormatException("Too many reveals.");
            into.Clear();
            for (int i = 0; i < n; i++)
            {
                var e = new RevealEntry { PlayerId = r.I32(), EndTick = r.I32(), Position = r.VecF() };
                if (double.IsNaN(e.Position.X) || double.IsNaN(e.Position.Y) || double.IsNaN(e.Position.Z) ||
                    double.IsInfinity(e.Position.X) || double.IsInfinity(e.Position.Y) || double.IsInfinity(e.Position.Z))
                    throw new NetFormatException("Bad reveal position.");
                into.Add(e);
            }
            return tick;
        }
    }

    /// <summary>The server side of the ping: a contact clock per pair of enemies.</summary>
    public sealed partial class NetServer
    {
        private sealed class PingPair
        {
            public int A, B;          // player ids, A < B
            public int LastContact;   // tick of the last contact (or of the last reveal's end)
            public int RevealEnd;     // tick the current reveal ends; at or before now: none
            public bool Hurt;         // damage between them this tick
        }

        private readonly Dictionary<long, PingPair> _pingPairs = new Dictionary<long, PingPair>();
        private readonly List<RevealEntry> _revealScratch = new List<RevealEntry>(1);
        private static readonly PingConfig DefaultPing = new PingConfig();
        private PingConfig _ping;

        /// <summary>The ping numbers in force: set here, else Breach Settings (when the map has walls), else the defaults.</summary>
        public PingConfig Ping
        {
            get => _ping ?? Walls?.Config.Ping ?? DefaultPing;
            set => _ping = value;
        }

        /// <summary>Pings fired since the server started (tests, dev HUD).</summary>
        public int PingsFired { get; private set; }

        private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private PingPair PairOf(int a, int b)
        {
            long k = PairKey(a, b);
            if (!_pingPairs.TryGetValue(k, out PingPair p))
            {
                p = new PingPair { A = System.Math.Min(a, b), B = System.Math.Max(a, b), LastContact = Tick };
                _pingPairs[k] = p;
            }
            return p;
        }

        /// <summary>True while the server shows <paramref name="targetId"/> to <paramref name="viewerId"/> through walls (the ping). Bots read this.</summary>
        public bool IsRevealed(int viewerId, int targetId)
        {
            return _pingPairs.TryGetValue(PairKey(viewerId, targetId), out PingPair p) && p.RevealEnd > Tick;
        }

        /// <summary>Damage dealt between two enemies is contact (called from <see cref="Hurt"/>).</summary>
        private void NotePingHurt(ServerPlayer attacker, ServerPlayer victim)
        {
            PairOf(attacker.Id, victim.Id).Hurt = true;
        }

        /// <summary>After everything else this tick: update each enemy pair's contact clock and fire or cancel pings.</summary>
        private void StepPing()
        {
            PingConfig cfg = Ping;
            bool live = cfg.Enabled && MatchConfig.Enabled && Match.Phase == MatchPhase.Playing;
            if (!live && _pingPairs.Count == 0) return;
            for (int i = 0; i < _players.Count; i++)
            {
                ServerPlayer a = _players[i];
                if (!CountsForMatch(a)) continue;
                for (int j = i + 1; j < _players.Count; j++)
                {
                    ServerPlayer b = _players[j];
                    if (b.Team == a.Team || !CountsForMatch(b)) continue;
                    StepPair(PairOf(a.Id, b.Id), a, b, cfg, live);
                }
            }
        }

        private void StepPair(PingPair pair, ServerPlayer a, ServerPlayer b, PingConfig cfg, bool live)
        {
            bool hurt = pair.Hurt;
            pair.Hurt = false;
            bool revealing = pair.RevealEnd > Tick;
            if (!live || a.State.Dead || b.State.Dead)
            {
                // Nothing to find (dead, or no match being played): the clock starts again from now.
                pair.LastContact = Tick;
                if (revealing) { pair.RevealEnd = Tick; SendReveal(a, b, Tick); SendReveal(b, a, Tick); }
                return;
            }
            if (revealing) return; // a running reveal plays out; the next quiet spell starts when it ends
            if (hurt || InContact(a, b, cfg)) { pair.LastContact = Tick; return; }
            if (Tick - pair.LastContact < cfg.QuietTicks) return;
            pair.RevealEnd = Tick + cfg.RevealTicks;
            pair.LastContact = pair.RevealEnd;
            PingsFired++;
            SendReveal(a, b, pair.RevealEnd);
            SendReveal(b, a, pair.RevealEnd);
        }

        /// <summary>Close, or a clear line between them (head, chest or knees; terrain only, like bullets).</summary>
        private bool InContact(ServerPlayer a, ServerPlayer b, PingConfig cfg)
        {
            Vec3d d = b.State.Position - a.State.Position;
            if (cfg.ContactRange > 0 && d.LengthSquared <= cfg.ContactRange * cfg.ContactRange) return true;
            Vec3d eyeA = a.State.Position + new Vec3d(0, PlayerMovement.EyeOf(a.State, Movement), 0);
            Vec3d eyeB = b.State.Position + new Vec3d(0, PlayerMovement.EyeOf(b.State, Movement), 0);
            return DuelBot.CanSee(Grid, eyeA, b.State.Position, PlayerMovement.HeightOf(b.State, Movement))
                || DuelBot.CanSee(Grid, eyeB, a.State.Position, PlayerMovement.HeightOf(a.State, Movement));
        }

        /// <summary>Tells <paramref name="to"/> that <paramref name="shown"/> is revealed until <paramref name="endTick"/> (now: cancelled).</summary>
        private void SendReveal(ServerPlayer to, ServerPlayer shown, int endTick)
        {
            if (IsBotConnection(to.ConnectionId)) return;
            _revealScratch.Clear();
            _revealScratch.Add(new RevealEntry { PlayerId = shown.Id, EndTick = endTick, Position = shown.State.Position });
            _w.Clear();
            RevealProtocol.Write(_w, Tick, _revealScratch);
            Send(to.ConnectionId, _w.ToArray(), true);
        }
    }
}
