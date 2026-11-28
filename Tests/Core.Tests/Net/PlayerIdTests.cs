using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// Player ids are 1..65535 (they ride as 16 bits in snapshots, world, combat, tile and item messages). Before, ids
    /// only counted up, so after 65,535 joins on one server run they collided in snapshots (Id &amp; 0xFFFF) or the server
    /// threw (checked ushort in world events). Now ids wrap, skip ids in use, and skip ids that left recently.
    /// </summary>
    public class PlayerIdTests
    {
        private static NetServer Server(NetConfig cfg = null)
        {
            var g = new SubGrid(4, 4, 4, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return new NetServer(g, new MovementConfig(), cfg ?? new NetConfig(), (c, d, r) => { });
        }

        private static readonly Vec3d Spawn = new Vec3d(5, 0, 5);

        [Test]
        public void Ids_WrapAt65535_ToOne_AndSkipIdsInUse()
        {
            NetServer s = Server();
            s.AddPlayer(10, 1, Spawn, 0);                       // id 1, stays
            s.NextPlayerId = 65534;
            Assert.That(s.AddPlayer(11, 1, Spawn, 0).Id, Is.EqualTo(65534));
            Assert.That(s.AddPlayer(12, 1, Spawn, 0).Id, Is.EqualTo(65535));
            Assert.That(s.AddPlayer(13, 1, Spawn, 0).Id, Is.EqualTo(2), "1 is in use: skipped");
            Assert.That(s.AddPlayer(14, 1, Spawn, 0).Id, Is.EqualTo(3));
        }

        [Test]
        public void AnIdThatLeft_IsNotReused_UntilItHasBeenQuietLongEnough()
        {
            var cfg = new NetConfig { PlayerIdQuietTicks = 50 };
            NetServer s = Server(cfg);
            s.AddPlayer(10, 1, Spawn, 0);              // 1
            s.AddPlayer(11, 1, Spawn, 0);              // 2
            s.RemovePlayer(10);
            s.NextPlayerId = 1;
            Assert.That(s.AddPlayer(12, 1, Spawn, 0).Id, Is.EqualTo(3), "1 left just now, 2 is in use");
            s.RemovePlayer(12);
            for (int i = 0; i < 50; i++) s.Step();
            s.NextPlayerId = 1;
            Assert.That(s.AddPlayer(13, 1, Spawn, 0).Id, Is.EqualTo(1), "quiet for 50 ticks: free again");
            Assert.That(s.AddPlayer(14, 1, Spawn, 0).Id, Is.EqualTo(3), "3 too");
        }

        [Test]
        public void WhenEveryFreeIdLeftRecently_TheNextFreeOneIsUsed_AndAFullServerRefuses()
        {
            var cfg = new NetConfig { MaxPlayerId = 4, PlayerIdQuietTicks = 1 << 30 };
            NetServer s = Server(cfg);
            for (int c = 0; c < 4; c++) s.AddPlayer(c, 1, Spawn, 0);
            Assert.Throws<System.InvalidOperationException>(() => s.AddPlayer(99, 1, Spawn, 0));
            s.RemovePlayer(2); // id 3
            Assert.That(s.AddPlayer(5, 1, Spawn, 0).Id, Is.EqualTo(3), "nothing else free: the recent one beats refusing a join");
        }

        [Test]
        public void ManyJoinsAndLeaves_PastTheWrap_NeverCollide_AndEveryIdFitsTheWire()
        {
            var cfg = new NetConfig { PlayerIdQuietTicks = 3 };
            NetServer s = Server(cfg);
            s.NextPlayerId = 65500;
            var rng = new System.Random(3);
            var conns = new List<int>();
            int nextConn = 0;
            for (int i = 0; i < 400; i++)
            {
                if (conns.Count < 20 || rng.Next(2) == 0) { s.AddPlayer(nextConn, 1 + (nextConn & 1), Spawn, 0); conns.Add(nextConn++); }
                else { int k = rng.Next(conns.Count); s.RemovePlayer(conns[k]); conns.RemoveAt(k); }
                var seen = new HashSet<int>();
                foreach (ServerPlayer p in s.Players)
                {
                    Assert.That(p.Id, Is.InRange(1, 65535));
                    Assert.That(seen.Add(p.Id), Is.True, $"id {p.Id} twice");
                }
                s.Step();
            }
        }

        [Test]
        public void ClientsSeeEachOther_AcrossTheWrap()
        {
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            });
            h.Server.NextPlayerId = 65535;
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            Assert.That(a.ServerSide.Id, Is.EqualTo(65535));
            Assert.That(b.ServerSide.Id, Is.EqualTo(1));
            h.RunSeconds(1.0);
            Assert.That(a.Client.TryGetRemote(1, out _), Is.True);
            Assert.That(b.Client.TryGetRemote(65535, out _), Is.True);
        }
    }
}
