using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>LAN room codes: the code alphabet, what typing accepts, and the ask/answer datagrams.</summary>
    public class LanRoomTests
    {
        [Test]
        public void TheAnyRoomAsk_RoundTrips_IsNeverARealCode_AndHalfZeroCodesAreJunk()
        {
            byte[] ask = LanRoom.AskAny(0xC0FFEE);
            Assert.That(LanRoom.TryReadAsk(ask, ask.Length, out string code, out uint nonce), Is.True);
            Assert.That(code, Is.EqualTo(LanRoom.AnyCode));
            Assert.That(nonce, Is.EqualTo(0xC0FFEEu));
            Assert.That(LanRoom.IsCode(LanRoom.AnyCode), Is.False, "nobody can type it as a room code");
            for (int i = 0; i < 200; i++) Assert.That(LanRoom.NewCode(new Random(i)), Is.Not.EqualTo(LanRoom.AnyCode));
            // A code with some zeros is not the any code and not a real code: refused.
            byte[] bad = LanRoom.Ask("K7Q2", 5);
            bad[5] = (byte)'0';
            Assert.That(LanRoom.TryReadAsk(bad, bad.Length, out _, out _), Is.False);
        }

        [Test]
        public void NewCodes_UseOnlyTheAlphabet_AndSpreadOut()
        {
            var rng = new Random(5);
            var seen = new HashSet<string>();
            for (int i = 0; i < 2000; i++)
            {
                string c = LanRoom.NewCode(rng);
                Assert.That(c.Length, Is.EqualTo(4));
                foreach (char ch in c) Assert.That(LanRoom.Alphabet.IndexOf(ch), Is.GreaterThanOrEqualTo(0), c);
                Assert.That(LanRoom.Normalize(c), Is.EqualTo(c));
                seen.Add(c);
            }
            Assert.That(seen.Count, Is.GreaterThan(1990), "2000 draws from a million codes almost never repeat");
            foreach (char bad in "01IO") Assert.That(LanRoom.Alphabet.IndexOf(bad), Is.LessThan(0), $"{bad} reads like another character");
        }

        [TestCase("k7q2", "K7Q2")]
        [TestCase(" K7-Q2 ", "K7Q2")]
        [TestCase("k 7 q 2", "K7Q2")]
        [TestCase("ZZZZ", "ZZZZ")]
        public void Typing_IsForgiving(string typed, string code) => Assert.That(LanRoom.Normalize(typed), Is.EqualTo(code));

        [TestCase("192.168.1.20")]
        [TestCase("localhost")]
        [TestCase("K7Q")]
        [TestCase("K7Q22")]
        [TestCase("K0Q2")]
        [TestCase("KIQ2")]
        [TestCase("")]
        [TestCase(null)]
        public void Addresses_AndNearMisses_AreNotCodes(string typed)
        {
            Assert.That(LanRoom.Normalize(typed), Is.Null);
            Assert.That(LanRoom.IsCode(typed), Is.False);
        }

        [Test]
        public void TheAsk_RoundTrips_AndJunkIsIgnored()
        {
            byte[] ask = LanRoom.Ask("k7q2", 0xDEADBEEF);
            Assert.That(ask.Length, Is.EqualTo(LanRoom.AskLength));
            Assert.That(LanRoom.TryReadAsk(ask, ask.Length, out string code, out uint nonce));
            Assert.That(code, Is.EqualTo("K7Q2"));
            Assert.That(nonce, Is.EqualTo(0xDEADBEEF));
            Assert.That(LanRoom.TryReadAsk(ask, ask.Length - 1, out _, out _), Is.False, "short");
            byte[] answer = LanRoom.Answer(new LanRoomReply { Code = "K7Q2", GamePort = 7770 });
            Assert.That(LanRoom.TryReadAsk(answer, answer.Length, out _, out _), Is.False, "an answer is not an ask");
            Assert.Throws<ArgumentException>(() => LanRoom.Ask("192.168.1.2", 1));
        }

        [Test]
        public void TheAnswer_RoundTrips()
        {
            var a = new LanRoomReply
            {
                Code = "ABCD", Nonce = 42, Protocol = Protocol.Version, GamePort = 7770, Map = LanRoomMap.BattleRoyale,
                BrMode = BrMode.Duos, Players = 3, MaxPlayers = 32,
            };
            byte[] data = LanRoom.Answer(a);
            Assert.That(data.Length, Is.EqualTo(LanRoom.AnswerLength));
            Assert.That(LanRoom.TryReadAnswer(data, data.Length, out LanRoomReply b));
            Assert.That(b.Code, Is.EqualTo("ABCD"));
            Assert.That(b.Nonce, Is.EqualTo(42u));
            Assert.That(b.Protocol, Is.EqualTo(Protocol.Version));
            Assert.That(b.GamePort, Is.EqualTo(7770));
            Assert.That(b.Map, Is.EqualTo(LanRoomMap.BattleRoyale));
            Assert.That(b.BrMode, Is.EqualTo(BrMode.Duos));
            Assert.That(b.Players, Is.EqualTo(3));
            Assert.That(b.MaxPlayers, Is.EqualTo(32));
        }

        [Test]
        public void RandomJunk_IsNeverReadAsAnAskOrAnswer_AndNeverThrows()
        {
            var rng = new Random(9);
            byte[] good = LanRoom.Answer(new LanRoomReply { Code = "WXYZ", GamePort = 7770 });
            for (int i = 0; i < 20000; i++)
            {
                byte[] junk = new byte[rng.Next(0, 40)];
                rng.NextBytes(junk);
                if (i % 2 == 0 && junk.Length >= good.Length)
                {
                    // A real answer with one byte of its magic or version flipped.
                    Array.Copy(good, junk, good.Length);
                    int at = rng.Next(0, 5);
                    junk[at] ^= (byte)(1 + rng.Next(255));
                    Array.Resize(ref junk, good.Length);
                }
                bool ask = LanRoom.TryReadAsk(junk, junk.Length, out _, out _);
                bool ans = LanRoom.TryReadAnswer(junk, junk.Length, out _);
                Assert.That(ask, Is.False, "junk read as an ask");
                if (i % 2 == 0 && junk.Length == good.Length) Assert.That(ans, Is.False, "a damaged answer read as good");
            }
        }
    }
}
