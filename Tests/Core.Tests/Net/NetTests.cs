using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    public class NetCodecTests
    {
        [Test]
        public void Quantize_IsIdempotent_AndSurvivesTheWire()
        {
            var rng = new Random(5);
            var w = new ByteWriter();
            var into = new List<InputCmd>();
            for (int i = 0; i < 2000; i++)
            {
                var raw = new InputCmd
                {
                    Tick = 5000 + i,
                    Move = new MoveInput
                    {
                        MoveX = rng.NextDouble() * 2.4 - 1.2,
                        MoveZ = rng.NextDouble() * 2.4 - 1.2,
                        Yaw = (rng.NextDouble() - 0.5) * 40,
                        JumpPressed = rng.Next(2) == 0,
                        JumpHeld = rng.Next(2) == 0,
                        CrouchHeld = rng.Next(2) == 0,
                        CrouchPressed = rng.Next(2) == 0,
                        SprintHeld = rng.Next(2) == 0,
                        WantsToShoot = rng.Next(2) == 0,
                    },
                    Pitch = (float)((rng.NextDouble() - 0.5) * 200),
                    Respawn = rng.Next(5) == 0,
                    ViewTick = 5000 + i - rng.Next(0, 20),
                    ViewFrac = (byte)rng.Next(256),
                };
                if (i == 0) raw.Move.Yaw = Math.PI;      // the float-PI edge
                if (i == 1) raw.Move.Yaw = -Math.PI;
                InputCmd q = InputCmd.Quantize(raw);
                Assert.That(InputCmd.Quantize(q), Is.EqualTo(q), "quantize must be idempotent");
                Assert.That(Math.Abs(q.Move.Yaw), Is.LessThanOrEqualTo(Math.PI));

                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = 1, Cmds = new List<InputCmd> { q } });
                var r = new ByteReader(w.Buffer, 0, w.Length);
                Protocol.ReadInputs(ref r, into);
                Assert.That(into[0], Is.EqualTo(q), "the server must step with exactly what the client predicted with");
            }
        }

        [Test]
        public void MotorState_RoundTripsExactly()
        {
            var h = NetHarness.M2Map(out Vec3d spawn);
            var g = TestMaps.BuildM2(out _, out _);
            var s = MotorState.At(spawn);
            var w = new ByteWriter();
            var c = new MovementConfig();
            for (int t = 0; t < 600; t++)
            {
                PlayerMovement.Step(g, c, ref s, Scripts.Wander(t));
                w.Clear();
                Protocol.WriteMotor(w, s);
                var r = new ByteReader(w.Buffer, 0, w.Length);
                MotorState back = Protocol.ReadMotor(ref r);
                Assert.That(Protocol.SameMotor(s, back), Is.True, $"tick {t}");
                Assert.That(r.Remaining, Is.Zero);
            }
        }

        [Test]
        public void Snapshot_RoundTrips()
        {
            var others = new List<PlayerSnap>
            {
                new PlayerSnap { Id = 3, Team = 2, Position = new Vec3d(1.5, 2.25, -3), Velocity = new Vec3d(0.5, -1, 2), Yaw = 1.25f, Pitch = -10f, Mode = MoveMode.Slide, Crouched = true },
            };
            var self = MotorState.At(new Vec3d(4, 5, 6));
            self.Velocity = new Vec3d(0.1, 0.2, 0.3);
            var w = new ByteWriter();
            Protocol.Write(w, new SnapshotMsg { ServerTick = 777, HasSelf = true, SelfInputWasReal = true, SelfLead = -500, Self = self, Others = others });
            var r = new ByteReader(w.Buffer, 0, w.Length);
            var back = Protocol.ReadSnapshot(ref r, new List<PlayerSnap>());
            Assert.That(back.ServerTick, Is.EqualTo(777));
            Assert.That(back.SelfLead, Is.EqualTo((int)sbyte.MinValue), "clamped to the wire range");
            Assert.That(Protocol.SameMotor(back.Self, self), Is.True);
            Assert.That(back.Others[0].Id, Is.EqualTo(3));
            Assert.That(back.Others[0].Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(back.Others[0].Position.Y, Is.EqualTo(2.25));
        }

        [Test]
        public void GarbagePackets_NeverThrow_AndAreCounted()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.5);
            var rng = new Random(9);
            for (int i = 0; i < 5000; i++)
            {
                var junk = new byte[rng.Next(0, 80)];
                rng.NextBytes(junk);
                if (junk.Length > 0 && i % 2 == 0) junk[0] = (byte)MsgType.Inputs;
                if (junk.Length > 0 && i % 3 == 0) junk[0] = (byte)MsgType.Snapshot;
                Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, junk, 0, junk.Length));
                Assert.DoesNotThrow(() => p.Client.Receive(junk, 0, junk.Length));
            }
            Assert.That(p.ServerSide.DroppedPackets, Is.GreaterThan(0));
            h.RunSeconds(1);
            Assert.That(p.Client.Joined, Is.True);
        }

        [Test]
        public void NonFiniteAngles_DropThePacket()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.5);
            int dropped = p.ServerSide.DroppedPackets;
            var w = new ByteWriter();
            var cmd = new InputCmd { Tick = h.Server.Tick + 1, Move = new MoveInput { MoveZ = 1, Yaw = double.NaN } };
            Protocol.Write(w, new InputsMsg { Cmds = new List<InputCmd> { cmd } });
            byte[] d = w.ToArray();
            h.Server.Receive(p.ConnectionId, d, 0, d.Length);
            Assert.That(p.ServerSide.DroppedPackets, Is.EqualTo(dropped + 1));
        }
    }

    internal static class Scripts
    {
        /// <summary>Busy, deterministic movement: runs, turns, sprints, jumps, slides and crouches.</summary>
        public static MoveInput Wander(int t)
        {
            int phase = (t / 90) % 6;
            var m = new MoveInput
            {
                MoveZ = 1,
                MoveX = phase == 3 ? 0.6 : 0,
                Yaw = Math.Sin(t * 0.011) * 2.5,
                SprintHeld = phase == 1 || phase == 2 || phase == 4,
                JumpHeld = t % 47 < 3,
                JumpPressed = t % 47 == 0,
                CrouchHeld = phase == 2 && t % 90 > 40 && t % 90 < 75,
                CrouchPressed = phase == 2 && t % 90 == 41,
            };
            return m;
        }

        public static MoveInput StraightNorth(int t) => new MoveInput { MoveZ = 1, Yaw = 0 };
        public static MoveInput Idle(int t) => default;
    }

    public class NetSyncTests
    {
        private static int CountAfter(Func<int> counter, NetHarness h, double warmSeconds, double runSeconds)
        {
            h.RunSeconds(warmSeconds);
            int before = counter();
            h.RunSeconds(runSeconds);
            return counter() - before;
        }

        [TestCase(0.0)]
        [TestCase(25.0)]
        [TestCase(50.0)]
        [TestCase(100.0)]
        [TestCase(150.0)]
        public void ClockLead_ConvergesToTarget(double oneWayMs)
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(oneWayMs), NetHarness.Lag(oneWayMs));
            p.Script = Scripts.StraightNorth;
            h.RunSeconds(5);
            int target = h.Config.TargetLeadTicks;
            Assert.That(p.Client.LastServerLead, Is.InRange(target, target + 3), $"lead {p.Client.LastServerLead}");
            int missed = p.ServerSide.MissedInputs;
            h.RunSeconds(3);
            Assert.That(p.ServerSide.MissedInputs - missed, Is.Zero, "no ticks should run without the real command");
        }

        [TestCase(0.0, 144.0)]
        [TestCase(50.0, 144.0)]
        [TestCase(50.0, 60.0)]
        [TestCase(50.0, 37.0)]
        [TestCase(100.0, 240.0)]
        public void Prediction_MatchesServerBitForBit(double oneWayMs, double fps)
        {
            var h = NetHarness.M2Map(out _);
            var p = h.AddClient(NetHarness.Lag(oneWayMs), NetHarness.Lag(oneWayMs), 1000.0 / fps);
            p.Script = Scripts.Wander;
            int c = CountAfter(() => p.Client.Corrections + p.Client.SilentResyncs, h, 3, 15);
            Assert.That(c, Is.Zero, "with a clean link and the real commands, the server must reproduce the prediction exactly");
            Assert.That(p.Client.SnapshotsReceived, Is.GreaterThan(800));
        }

        [Test]
        public void LossAndJitter_RarelyMissInputs_AndConverge()
        {
            var h = NetHarness.M2Map(out _);
            var p = h.AddClient(NetHarness.Lag(50, 10, 5), NetHarness.Lag(50, 10, 5), 1000.0 / 144.0, seed: 3);
            p.Script = Scripts.Wander;
            h.RunSeconds(3);
            int missed0 = p.ServerSide.MissedInputs;
            int ticks0 = h.Server.Tick;
            h.RunSeconds(20);
            double missRate = (p.ServerSide.MissedInputs - missed0) / (double)(h.Server.Tick - ticks0);
            Assert.That(missRate, Is.LessThan(0.01), $"miss rate {missRate:P2}");

            // Stop moving: the client must end exactly where the server says.
            p.Script = Scripts.Idle;
            h.RunSeconds(2);
            Assert.That(p.Client.Predicted.Position, Is.EqualTo(p.ServerSide.State.Position));
        }

        [Test]
        public void M3Target_100msRoundTrip_IsSmooth()
        {
            // The milestone bar: 100 ms round trip, a little jitter and loss, busy movement on the real map.
            var h = NetHarness.M2Map(out _);
            var a = h.AddClient(NetHarness.Lag(50, 5, 2), NetHarness.Lag(50, 5, 2), 1000.0 / 144.0, seed: 21);
            var b = h.AddClient(NetHarness.Lag(50, 5, 2), NetHarness.Lag(50, 5, 2), 1000.0 / 120.0, seed: 22);
            a.Script = Scripts.Wander;
            b.Script = t => Scripts.Wander(t + 400);
            h.RunSeconds(4);
            int ca = a.Client.Corrections, cb = b.Client.Corrections;
            int extrap = 0, samples = 0;
            b.OnFrame = peer =>
            {
                if (!peer.Client.TryGetRemote(a.ServerSide.Id, out RemoteView v)) return;
                samples++;
                if (v.Extrapolated) extrap++;
            };
            h.RunSeconds(30);
            double perSecA = (a.Client.Corrections - ca) / 30.0, perSecB = (b.Client.Corrections - cb) / 30.0;
            Assert.That(perSecA, Is.LessThan(0.2), $"A corrections/s {perSecA:0.00}");
            Assert.That(perSecB, Is.LessThan(0.2), $"B corrections/s {perSecB:0.00}");
            Assert.That(extrap / (double)samples, Is.LessThan(0.02), "remote player drawn from real data almost always");
        }

        [Test]
        public void JumpPresses_AreNotLost_UnderLoss()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(40, 5, 10), NetHarness.Lag(40, 5, 10), seed: 11);
            p.Script = t => new MoveInput { MoveZ = 0, JumpPressed = t % 50 == 0, JumpHeld = t % 50 < 2 };
            h.RunSeconds(2);
            h.ServerEvents.Clear();
            p.Events.Clear();
            h.RunSeconds(15);
            int serverJumps = h.ServerEvents.FindAll(e => e.Type == MoveEventType.Jumped).Count;
            int clientJumps = p.Events.FindAll(e => e.Type == MoveEventType.Jumped).Count;
            Assert.That(clientJumps, Is.GreaterThan(15));
            Assert.That(serverJumps, Is.InRange(clientJumps - 1, clientJumps + 1));
        }

        [Test]
        public void ServerOverride_IsReconciled_AndSmoothed()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(50), NetHarness.Lag(50));
            p.Script = Scripts.Idle;
            h.RunSeconds(2);
            Assert.That(p.Client.Corrections, Is.Zero);

            // Server moves the player (as a sculpt shove would): one cell east.
            Vec3d moved = p.ServerSide.Body.Position + new Vec3d(VoxelGrid.BlockSize, 0, 0);
            p.ServerSide.Body.Position = moved;
            h.RunMs(100 + 120);
            Assert.That(p.Client.Corrections, Is.GreaterThanOrEqualTo(1));
            Assert.That((p.Client.Predicted.Position - moved).Length, Is.LessThan(1e-9));
            Assert.That(p.Client.LastCorrectionMeters, Is.EqualTo(VoxelGrid.BlockSize).Within(1e-6));
            h.RunMs(600);
            Assert.That(p.Client.CorrectionOffset.Length, Is.LessThan(0.01), "visual offset decays");
        }

        [Test]
        public void SpeedHack_CannotOutrunTheServerTickRate()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.Peers.Remove(p); // drive this connection by hand
            var w = new ByteWriter();
            int fakeTick = h.Server.Tick;
            var cmds = new List<InputCmd>();
            Vec3d start = p.ServerSide.State.Position;
            for (int frame = 0; frame < 120; frame++) // 2 s of server time
            {
                // A cheating client sends 3 commands per server tick.
                for (int k = 0; k < 3; k++)
                {
                    cmds.Clear();
                    cmds.Add(new InputCmd { Tick = ++fakeTick, Move = new MoveInput { MoveZ = 1, SprintHeld = true } });
                    w.Clear();
                    Protocol.Write(w, new InputsMsg { Cmds = cmds });
                    byte[] d = w.ToArray();
                    h.Server.Receive(p.ConnectionId, d, 0, d.Length);
                }
                h.Server.Step();
            }
            double dist = (p.ServerSide.State.Position - start).HorizontalLength;
            Assert.That(dist, Is.LessThanOrEqualTo(h.Movement.SprintSpeed * 2.0 + 0.01));
        }

        [Test]
        public void LatePress_IsCarriedToTheNextTick()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.Peers.Remove(p);
            for (int i = 0; i < 30; i++) h.Server.Step(); // land on the ground
            h.ServerEvents.Clear();
            int late = h.Server.Tick - 1;
            var w = new ByteWriter();
            Protocol.Write(w, new InputsMsg { Cmds = new List<InputCmd> { new InputCmd { Tick = late, Move = new MoveInput { JumpPressed = true, JumpHeld = true } } } });
            byte[] d = w.ToArray();
            h.Server.Receive(p.ConnectionId, d, 0, d.Length);
            h.Server.Step(h.ServerEvents);
            Assert.That(h.ServerEvents.Exists(e => e.Type == MoveEventType.Jumped), Is.True);
            Assert.That(p.ServerSide.LateInputs, Is.EqualTo(1));
        }

        [Test]
        public void FarFutureCommands_AreIgnored()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.Peers.Remove(p);
            var w = new ByteWriter();
            Protocol.Write(w, new InputsMsg { Cmds = new List<InputCmd> { new InputCmd { Tick = h.Server.Tick + h.Config.MaxCommandLeadTicks + 1, Move = new MoveInput { MoveZ = 1 } } } });
            byte[] d = w.ToArray();
            h.Server.Receive(p.ConnectionId, d, 0, d.Length);
            for (int i = 0; i < 120; i++) h.Server.Step();
            Assert.That(p.ServerSide.State.Velocity.HorizontalLength, Is.Zero);
        }

        [Test]
        public void RespawnCommand_IsPredicted_AndAgreed()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(30), NetHarness.Lag(30));
            p.Script = Scripts.StraightNorth;
            h.RunSeconds(2);
            int respawnAt = p.Client.Tick + 5;
            p.RespawnScript = t => t == respawnAt;
            p.Script = t => t >= respawnAt ? default : Scripts.StraightNorth(t);
            int corrections = p.Client.Corrections;
            h.RunSeconds(1.5);
            Assert.That(p.Events.Exists(e => e.Type == MoveEventType.Respawned), Is.True);
            Assert.That(p.Client.Predicted.Position.X, Is.EqualTo(h.Spawn.X));
            Assert.That(p.Client.Predicted.Position.Z, Is.EqualTo(h.Spawn.Z));
            Assert.That(p.ServerSide.State.Position, Is.EqualTo(p.Client.Predicted.Position));
            Assert.That(p.Client.Corrections, Is.EqualTo(corrections));
        }

        [Test]
        public void ClientBehindServer_AdoptsServerState()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient(NetHarness.Lag(120), NetHarness.Lag(120));
            p.Script = Scripts.StraightNorth;
            h.RunSeconds(6);
            int target = h.Config.TargetLeadTicks;
            Assert.That(p.Client.LastServerLead, Is.InRange(target, target + 3));
            Assert.That(p.Client.Tick, Is.GreaterThan(h.Server.Tick));
        }
    }

    public class NetRemoteTests
    {
        [Test]
        public void RemotePlayers_AreInterpolatedSmoothly_AndCloseToTheServerPast()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(NetHarness.Lag(40), NetHarness.Lag(40), seed: 1, spawn: TileMap.FeetIn(4, 0, 2));
            var b = h.AddClient(NetHarness.Lag(40), NetHarness.Lag(40), seed: 2, spawn: TileMap.FeetIn(12, 0, 2));
            a.Script = Scripts.StraightNorth;
            b.Script = Scripts.Idle;
            h.RunSeconds(1);

            double lastZ = double.MinValue;
            int samples = 0, extrapolated = 0;
            double worstErr = 0;
            b.OnFrame = peer =>
            {
                if (!peer.Client.TryGetRemote(a.ServerSide.Id, out RemoteView v)) return;
                samples++;
                if (v.Extrapolated) extrapolated++;
                Assert.That(v.Position.Z, Is.GreaterThanOrEqualTo(lastZ - 1e-9), "never jumps backwards");
                lastZ = v.Position.Z;
                double rt = peer.Client.RemoteRenderTick;
                int t0 = (int)Math.Floor(rt);
                if (h.Server.TryGetHistoricalPosition(a.ServerSide.Id, t0, out Vec3d p0) &&
                    h.Server.TryGetHistoricalPosition(a.ServerSide.Id, t0 + 1, out Vec3d p1))
                {
                    Vec3d truth = p0 + (p1 - p0) * (rt - t0);
                    worstErr = Math.Max(worstErr, (truth - v.Position).Length);
                }
            };
            h.RunSeconds(1.5);
            Assert.That(samples, Is.GreaterThan(150));
            Assert.That(extrapolated, Is.Zero);
            Assert.That(worstErr, Is.LessThan(0.12), $"worst error {worstErr:0.000} m");
        }

        [Test]
        public void RemotePlayers_SurviveLoss_WithoutExtrapolatingMuch()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(NetHarness.Lag(40, 8, 5), NetHarness.Lag(40, 8, 5), seed: 5, spawn: TileMap.FeetIn(4, 0, 2));
            var b = h.AddClient(NetHarness.Lag(40, 8, 5), NetHarness.Lag(40, 8, 5), seed: 6, spawn: TileMap.FeetIn(12, 0, 2));
            a.Script = t => new MoveInput { MoveX = Math.Sin(t * 0.05), Yaw = 0 };
            h.RunSeconds(1);
            int samples = 0, extrapolated = 0;
            b.OnFrame = peer =>
            {
                if (!peer.Client.TryGetRemote(a.ServerSide.Id, out RemoteView v)) return;
                samples++;
                if (v.Extrapolated) extrapolated++;
            };
            h.RunSeconds(10);
            Assert.That(extrapolated / (double)samples, Is.LessThan(0.03));
        }

        [Test]
        public void LeavingPlayer_DisappearsFromOthers()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(seed: 1);
            var b = h.AddClient(seed: 2, spawn: TileMap.FeetIn(12, 0, 12));
            h.RunSeconds(0.5);
            Assert.That(b.Client.RemoteCount, Is.EqualTo(1));
            h.Remove(a);
            h.RunSeconds(0.2);
            Assert.That(b.Client.RemoteCount, Is.Zero);
        }

        [Test]
        public void OldSnapshots_AreIgnored()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.5);
            int newest = p.Client.LastSnapshotTick;
            var w = new ByteWriter();
            Protocol.Write(w, new SnapshotMsg { ServerTick = newest - 3, HasSelf = true, Self = MotorState.At(new Vec3d(100, 50, 100)), Others = new List<PlayerSnap>() });
            byte[] d = w.ToArray();
            Vec3d before = p.Client.Predicted.Position;
            p.Client.Receive(d, 0, d.Length);
            Assert.That(p.Client.LastSnapshotTick, Is.EqualTo(newest));
            Assert.That(p.Client.Predicted.Position, Is.EqualTo(before));
        }
    }
}
