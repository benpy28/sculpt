using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>Real server traffic, cut and bit-flipped, fed to a joined client: it may drop packets, never throw.</summary>
    public class NetFuzzTests
    {
        [Test]
        public void MangledServerPackets_NeverThrowFromTheClient()
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0]);
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            var a = h.AddClient(spawn: info.TeamSpawns[0][0]);
            var b = h.AddClient(spawn: info.TeamSpawns[1][0], team: 2, seed: 2);
            var captured = new List<byte[]>();
            a.OnDown = d => captured.Add((byte[])d.Clone());
            b.OnDown = d => { if (d[0] != (byte)MsgType.Welcome) captured.Add((byte[])d.Clone()); };
            var up = new List<byte[]>();
            a.Tamper = d => { up.Add((byte[])d.Clone()); return d; };
            a.Script = t => { var m = Scripts.Wander(t); m.FireHeld = t % 20 < 10; m.FirePressed = t % 20 == 0; return m; };
            b.Script = t => Scripts.Wander(t + 500);
            h.RunSeconds(1.5);
            for (int i = 0; i < 6; i++) h.Server.Walls.Damage(info.Block.Tiles[i * 7], 60 + i * 20, TileCause.Bullet, 0);
            h.RunSeconds(1.5);

            var types = new HashSet<byte>();
            foreach (var d in captured) types.Add(d[0]);
            TestContext.WriteLine("types: " + string.Join(",", types));

            var rng = new Random(17);
            var victim = new NetClient(h.MakeGrid(), h.Movement, h.Config, (d, rel) => { });
            byte[] welcome = captured.Find(d => d[0] == (byte)MsgType.Welcome);
            victim.Receive(welcome, 0, welcome.Length);
            int fed = 0;
            for (int round = 0; round < 40; round++)
            {
                foreach (byte[] src in captured)
                {
                    if (src[0] == (byte)MsgType.Welcome) continue;
                    byte[] d = (byte[])src.Clone();
                    int kind = rng.Next(4);
                    int len = d.Length;
                    if (kind == 0) len = rng.Next(1, d.Length + 1);
                    else if (kind == 1) { int flips = 1 + rng.Next(3); for (int f = 0; f < flips; f++) { int at = 1 + rng.Next(Math.Max(1, d.Length - 1)); if (at < d.Length) d[at] ^= (byte)(1 << rng.Next(8)); } }
                    else if (kind == 2) { int at = 1 + rng.Next(Math.Max(1, d.Length - 1)); if (at < d.Length) d[at] = (byte)rng.Next(256); }
                    try { victim.Receive(d, 0, len); fed++; }
                    catch (Exception e)
                    {
                        Assert.Fail($"type {src[0]} kind {kind} len {len}/{src.Length}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    }
                }
            }
            Assert.That(fed, Is.GreaterThan(1000));

            // And the server, fed mangled client packets from a connected player.
            int conn = a.ConnectionId;
            foreach (byte[] src in up.ToArray())
                for (int k = 0; k < 20; k++)
                {
                    byte[] d = (byte[])src.Clone();
                    int len = d.Length;
                    int kind = rng.Next(3);
                    if (kind == 0) len = rng.Next(1, d.Length + 1);
                    else { int at = 1 + rng.Next(Math.Max(1, d.Length - 1)); if (at < d.Length) d[at] = (byte)rng.Next(256); }
                    try { h.Server.Receive(conn, d, 0, len); if (k % 5 == 0) h.Server.Step(); }
                    catch (Exception e) { Assert.Fail($"server: type {src[0]} kind {kind} len {len}/{src.Length}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
                }
        }
    }
}
