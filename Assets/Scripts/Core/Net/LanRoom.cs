using System;
using System.Text;

namespace Sculpt.Core.Net
{
    /// <summary>What a LAN host is running, as its room answer says (so the joiner loads the right map).</summary>
    public enum LanRoomMap : byte { Duel = 0, Breach = 1, BattleRoyale = 2 }

    /// <summary>A host's answer to "who has room K7Q2?".</summary>
    public struct LanRoomReply
    {
        public string Code;
        public uint Nonce;
        /// <summary>The host's game protocol (<see cref="Protocol.Version"/>); a different one can't be joined.</summary>
        public byte Protocol;
        public ushort GamePort;
        public LanRoomMap Map;
        public BrMode BrMode;
        public byte Players, MaxPlayers;
    }

    /// <summary>
    /// LAN room codes (Ben 2026-11-26: "are room codes possible or does LAN have to be done with the IP"). A host picks a
    /// short code and answers broadcast questions for it on <see cref="DiscoveryPort"/>; a joiner types the code, asks the
    /// whole local network, and connects to whoever answers. Discovery only: the match itself still runs over the normal
    /// game connection to the host's address and port. Codes over the internet need a relay or matchmaking server (later).
    ///
    /// Packets (UDP, one datagram each, junk is ignored):
    ///   ask    "SCLQ" 1 code[4] nonce:u32
    ///   answer "SCLR" 1 code[4] nonce:u32 protocol:u8 port:u16 map:u8 brMode:u8 players:u8 max:u8
    /// The nonce comes back unchanged, so the joiner can time the round trip (the ping shown) and ignore stale answers.
    /// </summary>
    public static class LanRoom
    {
        /// <summary>Discovery listens one above the game port (the game is on 7770).</summary>
        public const ushort DiscoveryPort = 7771;
        public const int CodeLength = 4;
        /// <summary>Digits 2-9 and letters without I and O: nothing that reads as another character (0/O, 1/I/L kept apart).</summary>
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const byte FormatVersion = 1;
        private static readonly byte[] AskMagic = { (byte)'S', (byte)'C', (byte)'L', (byte)'Q' };
        private static readonly byte[] AnswerMagic = { (byte)'S', (byte)'C', (byte)'L', (byte)'R' };
        public const int AskLength = 4 + 1 + CodeLength + 4;
        public const int AnswerLength = AskLength + 1 + 2 + 1 + 1 + 1 + 1;

        /// <summary>A fresh code from <paramref name="random"/> (32^4, about a million codes).</summary>
        public static string NewCode(Random random)
        {
            var c = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++) c[i] = Alphabet[random.Next(Alphabet.Length)];
            return new string(c);
        }

        /// <summary>
        /// What the player typed as a room code: spaces and dashes dropped, upper case. Null when it isn't a code (an
        /// address, say, or a code with a character the alphabet never uses: O, I, 0 or 1 are refused, not guessed).
        /// </summary>
        public static string Normalize(string typed)
        {
            if (typed == null) return null;
            var sb = new StringBuilder(CodeLength);
            foreach (char ch in typed)
            {
                if (ch == ' ' || ch == '-') continue;
                char u = char.ToUpperInvariant(ch);
                if (Alphabet.IndexOf(u) < 0) return null;
                sb.Append(u);
                if (sb.Length > CodeLength) return null;
            }
            return sb.Length == CodeLength ? sb.ToString() : null;
        }

        public static bool IsCode(string typed) => Normalize(typed) != null;

        public static byte[] Ask(string code, uint nonce)
        {
            string c = Normalize(code) ?? throw new ArgumentException("not a room code", nameof(code));
            var w = new ByteWriter(AskLength);
            Head(w, AskMagic, c, nonce);
            return w.ToArray();
        }

        public static bool TryReadAsk(byte[] data, int length, out string code, out uint nonce)
        {
            code = null;
            nonce = 0;
            if (data == null || length != AskLength || length > data.Length) return false;
            var r = new ByteReader(data, 0, length);
            return ReadHead(ref r, AskMagic, out code, out nonce);
        }

        public static byte[] Answer(in LanRoomReply a)
        {
            string c = Normalize(a.Code) ?? throw new ArgumentException("not a room code", nameof(a));
            var w = new ByteWriter(AnswerLength);
            Head(w, AnswerMagic, c, a.Nonce);
            w.U8(a.Protocol);
            w.U16(a.GamePort);
            w.U8((byte)a.Map);
            w.U8((byte)a.BrMode);
            w.U8(a.Players);
            w.U8(a.MaxPlayers);
            return w.ToArray();
        }

        public static bool TryReadAnswer(byte[] data, int length, out LanRoomReply a)
        {
            a = default;
            if (data == null || length != AnswerLength || length > data.Length) return false;
            var r = new ByteReader(data, 0, length);
            if (!ReadHead(ref r, AnswerMagic, out a.Code, out a.Nonce)) return false;
            a.Protocol = r.U8();
            a.GamePort = r.U16();
            byte map = r.U8(), mode = r.U8();
            if (map > (byte)LanRoomMap.BattleRoyale || mode > (byte)BrMode.Squads) return false;
            a.Map = (LanRoomMap)map;
            a.BrMode = (BrMode)mode;
            a.Players = r.U8();
            a.MaxPlayers = r.U8();
            return a.GamePort != 0;
        }

        private static void Head(ByteWriter w, byte[] magic, string code, uint nonce)
        {
            foreach (byte b in magic) w.U8(b);
            w.U8(FormatVersion);
            foreach (char ch in code) w.U8((byte)ch);
            w.I32(unchecked((int)nonce));
        }

        private static bool ReadHead(ref ByteReader r, byte[] magic, out string code, out uint nonce)
        {
            code = null;
            nonce = 0;
            foreach (byte b in magic) if (r.U8() != b) return false;
            if (r.U8() != FormatVersion) return false;
            var c = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++)
            {
                c[i] = (char)r.U8();
                if (Alphabet.IndexOf(c[i]) < 0) return false;
            }
            code = new string(c);
            nonce = unchecked((uint)r.I32());
            return true;
        }
    }
}
