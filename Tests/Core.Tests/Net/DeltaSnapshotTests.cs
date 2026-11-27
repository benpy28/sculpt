using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// Delta snapshots (protocol 32): a delta must decode to exactly what the full snapshot decodes to, every time,
    /// through loss, reordering, late joins, stalls and junk, and a whole networked session must play out the same with
    /// deltas on and off.
    /// </summary>
    public class DeltaSnapshotTests
    {
        // ---------------------------------------------------------------- codec, by hand

        private static PlayerSnap Snap(int id, double x, double y, double z, float yaw = 0, float pitch = 0, MoveMode mode = MoveMode.Ground) =>
            new PlayerSnap { Id = id, Team = 1 + id % 2, Position = new Vec3d(x, y, z), Velocity = new Vec3d(1, 0, -2), Yaw = yaw, Pitch = pitch, Mode = mode };

        private static SnapshotMsg Msg(int tick, MotorState self, params PlayerSnap[] others) => new SnapshotMsg
        {
            ServerTick = tick, HasSelf = true, SelfInputWasReal = true, SelfLead = 2, Self = self, SelfBar = 5000, SelfHoldTenthMs = Protocol.NoHold,
            Others = new List<PlayerSnap>(others),
        };

        private static byte[] Encode(SnapshotCodec c, in SnapshotMsg m, SnapshotBaselines sent, int baseTick, out bool delta)
        {
            var w = new ByteWriter();
            delta = c.Write(w, m, sent, baseTick);
            return w.ToArray();
        }

        private static SnapshotMsg Decode(SnapshotCodec c, byte[] d, SnapshotBaselines received)
        {
            var r = new ByteReader(d, 0, d.Length);
            Assert.That(c.TryRead(ref r, new List<PlayerSnap>(), received, out SnapshotMsg m), Is.True, "decodes");
            Assert.That(r.Remaining, Is.Zero, "reads every byte");
            return m;
        }

        private static void AssertSame(in SnapshotMsg a, in SnapshotMsg b)
        {
            Assert.That(a.ServerTick, Is.EqualTo(b.ServerTick));
            Assert.That(a.HasSelf, Is.EqualTo(b.HasSelf));
            if (a.HasSelf)
            {
                Assert.That(Protocol.SameMotor(a.Self, b.Self), Is.True, "own motor state bit for bit");
                Assert.That(a.SelfInputWasReal, Is.EqualTo(b.SelfInputWasReal));
                Assert.That(a.SelfLead, Is.EqualTo(b.SelfLead));
                Assert.That(a.SelfBar, Is.EqualTo(b.SelfBar));
                Assert.That(a.SelfHoldTenthMs, Is.EqualTo(b.SelfHoldTenthMs));
            }
            Assert.That(a.Others.Count, Is.EqualTo(b.Others.Count));
            for (int i = 0; i < a.Others.Count; i++) Assert.That(a.Others[i], Is.EqualTo(b.Others[i]), $"player {i}");
        }

        [Test]
        public void AStillPlayerCostsTwoBytes_AndTheDeltaDecodesToTheFullSnapshot()
        {
            var self = MotorState.At(new Vec3d(10, 0, 10));
            var server = new SnapshotBaselines(128);
            var client = new SnapshotBaselines(128);
            var sc = new SnapshotCodec();
            var cc = new SnapshotCodec();
            var still = Snap(7, 40.25, 4.8, 61.5, 1.0f, -5f);
            still.Velocity = default;
            byte[] first = Encode(sc, Msg(100, self, still, Snap(9, 1, 2, 3)), server, -1, out bool d0);
            Assert.That(d0, Is.False, "nothing acked yet: full");
            Decode(cc, first, client);

            self.Position = new Vec3d(10.1, 0, 10);
            var moved = Snap(9, 1.5, 2, 3);
            byte[] second = Encode(sc, Msg(104, self, still, moved), server, 100, out bool d1);
            Assert.That(d1, Is.True);
            byte[] fullSecond = Encode(new SnapshotCodec(), Msg(104, self, still, moved), null, -1, out _);
            Assert.That(second.Length, Is.LessThan(fullSecond.Length / 4), $"delta {second.Length} B vs full {fullSecond.Length} B");
            SnapshotMsg viaDelta = Decode(cc, second, client);
            SnapshotMsg viaFull = Decode(new SnapshotCodec(), fullSecond, null);
            AssertSame(viaDelta, viaFull);

            // The still player alone: an id gap byte and an empty mask.
            byte[] a = Encode(sc, Msg(105, self, still), server, 104, out _);
            byte[] b = Encode(sc, Msg(106, self), server, 104, out _);
            Assert.That(a.Length - b.Length, Is.EqualTo(2));
        }

        [Test]
        public void EveryCarriableHeldItem_RoundTrips_IncludingTheHealItems()
        {
            // Protocol 35 widened the held item to 4 bits (bandage 9, first-aid kit 10).
            var self = MotorState.At(new Vec3d(10, 0, 10));
            for (Item it = Item.None; it <= Items.Last; it++)
            {
                if (it == Item.Chest) continue;
                var p = Snap(4, 1, 2, 3);
                p.Held = it;
                p.Hand = HandMode.Drawing;
                SnapshotMsg m = Decode(new SnapshotCodec(), Encode(new SnapshotCodec(), Msg(50, self, p), null, -1, out _), null);
                Assert.That(m.Others[0].Held, Is.EqualTo(it), it.ToString());
                Assert.That(m.Others[0].Hand, Is.EqualTo(HandMode.Drawing));
            }
        }

        [Test]
        public void QuantizedPoses_AreWithinHalfAStepOfTheTruth()
        {
            var p = new PlayerSnap { Id = 3, Team = 2, Position = new Vec3d(523.123456, 9.6, -12.0001), Velocity = new Vec3d(13.3, -7.77, 0.001), Yaw = -2.9f, Pitch = 71.237f, Mode = MoveMode.Slide, Crouched = true, Held = Item.Ar, Hand = HandMode.Drawing };
            PlayerSnap q = SnapshotCodec.Dequantize(SnapshotCodec.Quantize(p));
            Assert.That((q.Position - p.Position).Length, Is.LessThan(0.5 / 1024 * 1.8));
            Assert.That((q.Velocity - p.Velocity).Length, Is.LessThan(0.5 / 64 * 1.8));
            Assert.That(Math.Abs(Math.IEEERemainder(q.Yaw - p.Yaw, 2 * Math.PI)), Is.LessThan(Math.PI / 65536 + 1e-6));
            Assert.That(q.Pitch, Is.EqualTo(p.Pitch).Within(0.005 + 1e-4));
            Assert.That(q.Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(q.Crouched, Is.True);
            Assert.That(q.Dead, Is.False);
            Assert.That(q.Held, Is.EqualTo(Item.Ar));
            Assert.That(q.Hand, Is.EqualTo(HandMode.Drawing));
            Assert.That(SnapshotCodec.Quantize(q), Is.EqualTo(SnapshotCodec.Quantize(p)), "re-quantizing what was drawn changes nothing");
        }

        [Test]
        public void XorRuns_RoundTripAnyBytes()
        {
            var rng = new Random(11);
            for (int it = 0; it < 3000; it++)
            {
                int curLen = rng.Next(0, 600), baseLen = rng.Next(0, 600);
                var bas = new byte[baseLen];
                rng.NextBytes(bas);
                var cur = new byte[curLen];
                for (int i = 0; i < curLen; i++)
                {
                    int mode = rng.Next(4);
                    cur[i] = mode < 2 && i < baseLen ? bas[i] : mode == 2 ? (byte)0 : (byte)rng.Next(256);
                }
                var w = new ByteWriter();
                SnapshotCodec.WriteXor(w, cur, curLen, bas, baseLen);
                var r = new ByteReader(w.Buffer, 0, w.Length);
                int len = r.VarCount(SnapshotCodec.MaxSelfBytes);
                Assert.That(len, Is.EqualTo(curLen));
                var back = new byte[len];
                SnapshotCodec.ReadXor(ref r, back, len, bas, baseLen);
                Assert.That(back, Is.EqualTo(cur), $"iteration {it}");
                Assert.That(r.Remaining, Is.Zero);
            }
        }

        /// <summary>
        /// Random snapshot streams, random losses, random (but honest) acks: every delta decodes to exactly the full
        /// snapshot of the same tick. Players join, leave, change team, teleport and die.
        /// </summary>
        [Test]
        public void SeededStreams_DeltaAlwaysEqualsFull([Range(1, 12)] int seed)
        {
            var rng = new Random(seed * 7907);
            var server = new SnapshotBaselines(64);
            var client = new SnapshotBaselines(64);
            var sc = new SnapshotCodec();
            var cc = new SnapshotCodec();
            var players = new Dictionary<int, PlayerSnap>();
            var self = MotorState.At(new Vec3d(50, 0, 50));
            int clientNewest = -1, deltas = 0;
            for (int tick = 1; tick < 1500; tick++)
            {
                // Evolve the world.
                if (players.Count < 40 && rng.NextDouble() < 0.03) { int id = rng.Next(1, 300); players[id] = Snap(id, rng.Next(0, 400), 0, rng.Next(0, 400)); }
                if (players.Count > 0 && rng.NextDouble() < 0.01) { foreach (int id in players.Keys) { players.Remove(id); break; } }
                foreach (int id in new List<int>(players.Keys))
                {
                    PlayerSnap p = players[id];
                    if (rng.NextDouble() < 0.6) p.Position += new Vec3d(rng.NextDouble() - 0.5, (rng.NextDouble() - 0.5) * 0.2, rng.NextDouble() - 0.5) * 0.2;
                    if (rng.NextDouble() < 0.3) p.Velocity = new Vec3d(rng.NextDouble() * 20 - 10, rng.NextDouble() * 10 - 5, rng.NextDouble() * 20 - 10);
                    if (rng.NextDouble() < 0.4) p.Yaw += (float)(rng.NextDouble() - 0.5);
                    if (rng.NextDouble() < 0.2) p.Pitch = (float)(rng.NextDouble() * 178 - 89);
                    if (rng.NextDouble() < 0.02) p.Mode = (MoveMode)rng.Next(0, 5);
                    if (rng.NextDouble() < 0.01) p.Dead = !p.Dead;
                    if (rng.NextDouble() < 0.005) p.Team = 3 - p.Team;
                    if (rng.NextDouble() < 0.003) p.Position = new Vec3d(rng.Next(0, 900), 0, rng.Next(0, 900)); // respawn
                    players[id] = p;
                }
                self.Tick = tick;
                self.Position += new Vec3d(0.05, 0, 0.01 * Math.Sin(tick * 0.1));
                self.Velocity = new Vec3d(6, rng.NextDouble(), 1);
                if (rng.NextDouble() < 0.05) self.Weapon.ShotCount++;
                if (rng.NextDouble() < 0.02) self.Nade0 = new GrenadeFlight { Active = true, Kind = ThrowKind.Grenade, Position = self.Position, Velocity = new Vec3d(1, 2, 3), ThrowTick = tick };
                if (rng.NextDouble() < 0.02) self.Nade0 = default; // flights change the motor state's length

                var others = new List<PlayerSnap>(players.Values);
                // Server order is not sorted; the codec sorts.
                for (int i = others.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (others[i], others[j]) = (others[j], others[i]); }
                var m = new SnapshotMsg { ServerTick = tick, HasSelf = rng.NextDouble() > 0.02, Self = self, SelfLead = rng.Next(-3, 5), SelfBar = rng.Next(0, 10001), SelfHoldTenthMs = rng.Next(0, 70000), Others = others };

                int ack = clientNewest >= 0 && rng.NextDouble() < 0.9 ? Math.Max(0, clientNewest - rng.Next(0, 30)) : -1;
                if (ack >= 0 && !client.Has(ack)) ack = clientNewest; // the client only ever acks a tick it kept
                byte[] pkt = Encode(sc, m, server, ack, out bool delta);
                if (delta) deltas++;
                if (rng.NextDouble() < 0.25) continue; // lost
                SnapshotMsg got = Decode(cc, pkt, client);
                clientNewest = tick;
                AssertSame(got, Decode(new SnapshotCodec(), Encode(new SnapshotCodec(), m, null, -1, out _), null));
            }
            Assert.That(deltas, Is.GreaterThan(1000), "most snapshots were deltas");
        }

        [Test]
        public void TheServersOncePerTickPath_WritesTheSameBytesAsThePlainOne()
        {
            var rng = new Random(3);
            var all = new List<PlayerSnap>();
            for (int i = 0; i < 20; i++) all.Add(Snap(rng.Next(1, 200), rng.Next(100), 0, rng.Next(100), (float)rng.NextDouble()));
            var self = MotorState.At(new Vec3d(1, 2, 3));
            var fast = new SnapshotCodec();
            var plain = new SnapshotCodec();
            var sentFast = new SnapshotBaselines(64);
            var sentPlain = new SnapshotBaselines(64);
            for (int tick = 1; tick < 40; tick++)
            {
                for (int i = 0; i < all.Count; i++) { PlayerSnap p = all[i]; p.Position += new Vec3d(0.07, 0, -0.03); all[i] = p; }
                int receiver = all[tick % all.Count].Id;
                var others = all.FindAll(p => p.Id != receiver);
                var m = Msg(tick, self, others.ToArray());
                var a = new ByteWriter();
                var b = new ByteWriter();
                fast.SetEveryone(tick, all);
                fast.WriteForReceiver(a, m, receiver, sentFast, tick - 3);
                plain.Write(b, m, sentPlain, tick - 3);
                Assert.That(a.ToArray(), Is.EqualTo(b.ToArray()), $"tick {tick}");
            }
        }

        [Test]
        public void ADeltaWhoseBaseIsMissing_IsRefusedAndChangesNothing()
        {
            var server = new SnapshotBaselines(64);
            var sc = new SnapshotCodec();
            var self = MotorState.At(new Vec3d(1, 2, 3));
            Encode(sc, Msg(10, self, Snap(2, 1, 1, 1)), server, -1, out _);
            byte[] d = Encode(sc, Msg(12, self, Snap(2, 1, 1, 2)), server, 10, out bool delta);
            Assert.That(delta, Is.True);
            var client = new SnapshotBaselines(64);
            var r = new ByteReader(d, 0, d.Length);
            Assert.That(new SnapshotCodec().TryRead(ref r, new List<PlayerSnap>(), client, out _), Is.False);
            Assert.That(client.Has(12), Is.False);
            var r2 = new ByteReader(d, 0, d.Length);
            Assert.Throws<NetFormatException>(() => Protocol.ReadSnapshot(ref r2, new List<PlayerSnap>()), "the plain reader never guesses a base");
        }

        [Test]
        public void JunkAndCutSnapshots_OnlyEverThrowNetFormatException()
        {
            var rng = new Random(5);
            var server = new SnapshotBaselines(64);
            var client = new SnapshotBaselines(64);
            var sc = new SnapshotCodec();
            var self = MotorState.At(new Vec3d(1, 2, 3));
            byte[] full = Encode(sc, Msg(10, self, Snap(2, 1, 1, 1), Snap(4, 3, 3, 3)), server, -1, out _);
            Decode(new SnapshotCodec(), full, client);
            self.Position = new Vec3d(1.5, 2, 3);
            byte[] delta = Encode(sc, Msg(11, self, Snap(2, 1, 1, 1.5), Snap(5, 3, 3, 3)), server, 10, out _);
            foreach (byte[] good in new[] { full, delta })
            {
                for (int it = 0; it < 4000; it++)
                {
                    byte[] d = (byte[])good.Clone();
                    int len = it % 3 == 0 ? rng.Next(1, d.Length) : d.Length;
                    for (int k = rng.Next(1, 4); k > 0; k--) d[rng.Next(1, len)] = (byte)rng.Next(256);
                    var probe = new SnapshotBaselines(64);
                    Decode(new SnapshotCodec(), full, probe);
                    try
                    {
                        var r = new ByteReader(d, 0, len);
                        new SnapshotCodec().TryRead(ref r, new List<PlayerSnap>(), probe, out _);
                    }
                    catch (NetFormatException) { }
                }
            }
        }

        // ---------------------------------------------------------------- networked

        private sealed class Trace
        {
            public readonly List<string> Lines = new List<string>();
        }

        /// <summary>Everything a client shows each frame: its own predicted state and every remote it draws.</summary>
        private static void Record(NetHarness.Peer p, Trace t)
        {
            var ids = new List<int>();
            p.Client.GetRemoteIds(ids);
            ids.Sort();
            var s = new System.Text.StringBuilder();
            MotorState m = p.Client.Predicted;
            s.Append(p.Client.Tick).Append(' ').Append(m.Position.X.ToString("R")).Append(',').Append(m.Position.Y.ToString("R")).Append(',').Append(m.Position.Z.ToString("R"));
            s.Append(" c").Append(p.Client.Corrections).Append(" s").Append(p.Client.LastSnapshotTick);
            foreach (int id in ids)
            {
                p.Client.TryGetRemote(id, out RemoteView v);
                s.Append(" | ").Append(id).Append(':').Append(v.Position.X.ToString("R")).Append(',').Append(v.Position.Y.ToString("R")).Append(',').Append(v.Position.Z.ToString("R"))
                 .Append(' ').Append(v.Yaw.ToString("R")).Append(' ').Append(v.Pitch.ToString("R")).Append(' ').Append(v.Mode).Append(v.Dead ? " dead" : "");
            }
            t.Lines.Add(s.ToString());
        }

        private static List<Trace> Session(bool deltas, LinkConditions down, out NetHarness h)
        {
            h = NetHarness.M2Map(out Vec3d spawn, c => c.DeltaSnapshots = deltas);
            var traces = new List<Trace>();
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var p = h.AddClient(up: NetHarness.Lag(30, 8, 10), down: down, seed: i + 1, team: 1 + i % 2);
                p.Script = t => Scripts.Wander(t + k * 131);
                var tr = new Trace();
                traces.Add(tr);
                p.OnFrame = peer => Record(peer, tr);
            }
            h.RunSeconds(6);
            return traces;
        }

        /// <summary>
        /// The whole point: four clients over lossy, jittery (reordering) links play out exactly the same, frame for
        /// frame, with delta snapshots on and off: own prediction, corrections and every remote drawn.
        /// </summary>
        [Test]
        public void ASession_IsIdenticalWithDeltasOnAndOff_ThroughLossAndReordering()
        {
            var bad = NetHarness.Lag(40, 15, 20);
            List<Trace> on = Session(true, bad, out NetHarness hOn);
            List<Trace> off = Session(false, bad, out NetHarness hOff);
            for (int i = 0; i < on.Count; i++)
            {
                Assert.That(on[i].Lines.Count, Is.EqualTo(off[i].Lines.Count));
                for (int f = 0; f < on[i].Lines.Count; f++)
                    if (on[i].Lines[f] != off[i].Lines[f]) Assert.Fail($"client {i} frame {f} differs:\n on: {on[i].Lines[f]}\noff: {off[i].Lines[f]}");
            }
            long bytesOn = 0, bytesOff = 0;
            for (int i = 0; i < 4; i++)
            {
                NetHarness.Peer p = hOn.Peers[i];
                Assert.That(p.Client.SnapshotBaseMisses, Is.Zero, "a delta always finds its base");
                Assert.That(p.ServerSide.DeltaSnapshotsSent, Is.GreaterThan(p.ServerSide.FullSnapshotsSent * 20), "nearly all deltas");
                Assert.That(hOff.Peers[i].ServerSide.DeltaSnapshotsSent, Is.Zero);
                bytesOn += p.ServerSide.SnapshotBytes;
                bytesOff += hOff.Peers[i].ServerSide.SnapshotBytes;
            }
            TestContext.WriteLine($"4 players, 6 s, 20% loss: snapshots {bytesOn / 1024} KB with deltas, {bytesOff / 1024} KB without ({100.0 * bytesOn / bytesOff:F0}%)");
            Assert.That(bytesOn, Is.LessThan(bytesOff / 2));
        }

        [Test]
        public void ALateJoiner_StartsFull_ThenGetsDeltas()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient();
            a.Script = Scripts.Wander;
            h.RunSeconds(1);
            var b = h.AddClient(down: NetHarness.Lag(50), seed: 2);
            h.RunSeconds(0.5);
            Assert.That(b.ServerSide.FullSnapshotsSent, Is.GreaterThanOrEqualTo(1), "nothing to delta against at first");
            Assert.That(b.ServerSide.FullSnapshotsSent, Is.LessThanOrEqualTo(SimTime.SecondsToTicks(0.12) + 2), "only until the first ack is back (a round trip)");
            Assert.That(b.ServerSide.DeltaSnapshotsSent, Is.GreaterThan(30));
            Assert.That(b.Client.SnapshotBaseMisses, Is.Zero);
            Assert.That(b.Client.TryGetRemote(a.ServerSide.Id, out RemoteView v), Is.True);
            Assert.That((v.Position - a.ServerSide.State.Position).Length, Is.LessThan(2), "draws the other player");
        }

        [Test]
        public void WhenAcksStopForLongerThanTheWindow_TheServerFallsBackToFull_AndRecovers()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient();
            a.Script = Scripts.Wander;
            h.RunSeconds(1);
            a.Up.Conditions.LossPercent = 100; // no inputs, so no acks, reach the server
            int fullBefore = a.ServerSide.FullSnapshotsSent;
            h.RunSeconds(1.5);
            int fulls = a.ServerSide.FullSnapshotsSent - fullBefore;
            Assert.That(fulls, Is.GreaterThan(SimTime.SecondsToTicks(0.3)), "past the window every snapshot is full");
            Assert.That(a.Client.SnapshotBaseMisses, Is.Zero);
            a.Up.Conditions.LossPercent = 0;
            h.RunSeconds(0.5);
            int deltasBefore = a.ServerSide.DeltaSnapshotsSent;
            h.RunSeconds(0.5);
            Assert.That(a.ServerSide.DeltaSnapshotsSent - deltasBefore, Is.EqualTo(SimTime.SecondsToTicks(0.5)).Within(2), "deltas again");
            Assert.That(a.Client.SnapshotBaseMisses, Is.Zero);
            Assert.That((a.Client.Predicted.Position - a.ServerSide.State.Position).Length, Is.LessThan(1.5), "back in step");
        }

        [Test]
        public void AForgedAck_GetsAFullSnapshot_NeverABrokenDelta()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient();
            h.RunSeconds(0.5);
            int full = a.ServerSide.FullSnapshotsSent;
            // An input packet whose ack names a tick we never sent (in the future, then long ago).
            foreach (int ack in new[] { h.Server.Tick + 50, 3, int.MaxValue })
            {
                var w = new ByteWriter();
                var cmd = new InputCmd { Tick = a.ServerSide.NewestReceived + 1 };
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = ack, Cmds = new List<InputCmd> { cmd } });
                byte[] d = w.ToArray();
                h.Server.Receive(a.ConnectionId, d, 0, d.Length);
                h.Server.Step(h.ServerEvents);
            }
            Assert.That(a.ServerSide.FullSnapshotsSent, Is.GreaterThan(full));
            h.RunSeconds(0.5);
            Assert.That(a.Client.SnapshotBaseMisses, Is.Zero);
        }

        [Test]
        public void ServerAndClientCodecs_DoNotAllocateBaselinesEveryTick()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient();
            h.RunSeconds(0.2);
            SnapshotBaselines first = a.ServerSide.Sent;
            h.RunSeconds(0.5);
            Assert.That(a.ServerSide.Sent, Is.SameAs(first));
        }

        /// <summary>
        /// The number for the battle royale: 32 players running, jumping and turning on the duel map, everything
        /// sent every tick at 120 Hz. Prints the download per client with and without deltas.
        /// </summary>
        [Test]
        public void ThirtyTwoPlayers_DownloadPerClient()
        {
            double perOn = Bandwidth32(true, out int missesOn);
            double perOff = Bandwidth32(false, out _);
            TestContext.WriteLine($"32 players, snapshots per client: {perOn:F3} Mbit/s with deltas, {perOff:F3} Mbit/s full ({100 * perOn / perOff:F0}%)");
            Assert.That(missesOn, Is.Zero);
            Assert.That(perOn, Is.LessThan(perOff * 0.45));
        }

        private static double Bandwidth32(bool deltas, out int misses)
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0], c => c.DeltaSnapshots = deltas);
            for (int i = 0; i < 32; i++)
            {
                int k = i;
                Vec3d s = info.TeamSpawns[i % 2][(i / 2) % info.TeamSpawns[i % 2].Count] + new Vec3d((i / 6) * 1.5 - 4, 0, 0);
                var p = h.AddClient(up: NetHarness.Lag(25, 5, 2), down: NetHarness.Lag(25, 5, 2), seed: i + 1, team: 1 + i % 2, spawn: s);
                p.Script = t => Scripts.Wander(t + k * 97);
                p.FrameMs = 1000.0 / 60; // keep the test quick; the client frame rate doesn't change what the server sends
            }
            h.RunSeconds(1);
            long before = 0;
            foreach (var p in h.Peers) before += p.ServerSide.SnapshotBytes;
            const double secs = 3;
            h.RunSeconds(secs);
            long after = 0;
            misses = 0;
            foreach (var p in h.Peers) { after += p.ServerSide.SnapshotBytes; misses += p.Client.SnapshotBaseMisses; }
            // Payload only; UDP/IP headers (28 B per packet, 120 packets a second = 0.027 Mbit/s) come on top either way.
            return (after - before) * 8.0 / secs / h.Peers.Count / 1e6;
        }
    }
}
