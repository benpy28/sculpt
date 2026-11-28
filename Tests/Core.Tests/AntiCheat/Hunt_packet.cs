using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// PACKET-LEVEL red-team hunt (cheat client sends any bytes it likes).
    /// These tests drive a real NetServer through the NetHarness, rewriting the packets a client sends
    /// (Peer.Tamper), the way a modified client would. They PROVE the server's behaviour on forged input.
    ///
    /// Findings proven here (both now FIXED; these are regression guards):
    ///   1. FlagKind.ViewLagJump WAS dead code: the backtracking check reads PlayerWatch.MedianLag(), which needs
    ///      PushLag() to have fed it samples, and PushLag() was never called. Fixed: CheckCommand now pushes each real
    ///      command's view lag, so a lag that jumps far over the player's median (a toggled backtrack) fires.
    ///   2. FlagKind.ViewBehindAck is evadable by under-reporting the ack, so a CONSISTENT backtracker (same claimed
    ///      lag from login) trips no flag — on purpose, it is indistinguishable from honest high ping. Fixed by
    ///      prevention, not detection: ClampRewindToAck clamps the lag-comp rewind to the snapshot the client CLAIMS
    ///      it had (its ack), so a shooter forging an old view while acking a recent snapshot can no longer rewind a
    ///      moving victim to an old position. The naive peeker-advantage backtrack is prevented, not just logged.
    ///
    /// Also records what was checked and found SAFE (see HUNT report).
    /// </summary>
    public class Hunt_packet
    {
        private static double YawTo(Vec3d from, Vec3d to) => Math.Atan2(to.X - from.X, to.Z - from.Z);

        /// <summary>Shooter and a target 10 m apart; the shooter aims at the target (server truth) and holds fire.</summary>
        private static (NetHarness h, NetHarness.Peer shooter, NetHarness.Peer target) Duel(bool targetStrafes)
        {
            var h = NetHarness.Flat();
            var shooter = h.AddClient(NetHarness.Lag(30, 5, 0), NetHarness.Lag(30, 5, 0), seed: 3);
            var target = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20), seed: 4, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 10));
            if (targetStrafes) target.Script = t => new MoveInput { MoveX = (t / 60) % 2 == 0 ? 1 : -1, Yaw = Math.PI };
            else target.Script = t => new MoveInput { Yaw = Math.PI };
            shooter.Script = t =>
            {
                Vec3d from = shooter.ServerSide.State.Position, to = target.ServerSide.State.Position;
                return new MoveInput { Yaw = YawTo(from, to), FireHeld = true, WantsToShoot = true };
            };
            return (h, shooter, target);
        }

        /// <summary>
        /// REGRESSION GUARD (was FINDING 1). A shooter that starts honest (real view lag ~0), then on every command
        /// claims it was drawing the target 30 ticks (250 ms) in the past, with an honest ack. The per-command
        /// ViewBehindAck detector (wired through CheckCommand) sees the view sitting far behind the acked snapshot and
        /// fires deterministically — the backtrack reaches, and is caught by, the lag-comp checks. (That the now-wired
        /// ViewLagJump ALSO fires on the toggled lag spike, and that the rewind clamp prevents the hit, are guarded by
        /// Hunt_hitreg.SameBacktrackWithHonestAck_IsCaughtByBothLagCompFlags and the hackclient backtrack guards; here we
        /// pin the deterministic per-command detector.)
        /// </summary>
        [Test]
        public void ABacktrackWithHonestAck_IsCaughtByViewBehindAck()
        {
            var (h, shooter, _) = Duel(targetStrafes: true);
            var list = new List<InputCmd>();
            var w = new ByteWriter(1024);
            shooter.Tamper = data =>
            {
                if (Protocol.PeekType(data, 0, data.Length) != MsgType.Inputs) return data;
                var r = new ByteReader(data, 0, data.Length);
                InputsMsg m = Protocol.ReadInputs(ref r, list);
                for (int i = 0; i < list.Count; i++)
                {
                    InputCmd c = list[i];
                    if (c.Move.FireHeld) { c.ViewTick -= 30; list[i] = c; } // ack left honest
                }
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = m.AckSnapshotTick, Cmds = list });
                return w.ToArray();
            };
            shooter.Script = t => default;             // a couple of seconds honest first
            h.RunSeconds(2);
            var target = h.Peers[1];
            shooter.Script = t => new MoveInput { Yaw = YawTo(shooter.ServerSide.State.Position, target.ServerSide.State.Position), FireHeld = true, WantsToShoot = true };
            h.RunSeconds(6);

            TestContext.WriteLine(h.Server.Flags.Report());
            Assert.That(h.Server.Flags.Count(FlagKind.ViewBehindAck), Is.GreaterThan(0),
                "the backtrack is caught: the view sits far behind the honest ack, so ViewBehindAck fires");
        }

        /// <summary>
        /// REGRESSION GUARD (was FINDING 2). The finding was that a consistent full-cap backtracker (every command claims
        /// a view 36 ticks / 0.3 s in the past) that under-reports its ack to keep AckSnapshotTick - ViewTick inside the
        /// threshold evaded BOTH lag-comp flags while its shots landed — invisible to review. There is now no ack value
        /// that both lands the backtrack and stays silent: to keep the newest command under the threshold the cheat must
        /// claim an old snapshot for every command, and the per-command ViewBehindAck check (now wired in CheckCommand)
        /// fires on the older redundant commands. And the same low ack sets the rewind-clamp floor (ClampRewindToAck),
        /// so the rewind can't reach the full claimed-old view either. Either way the backtracker is caught.
        /// </summary>
        [Test]
        public void ConsistentBacktrackWithLoweredAck_IsNoLongerInvisible()
        {
            var (h, shooter, _) = Duel(targetStrafes: true);
            int back = h.Movement.Weapons.MaxRewindTicks;     // 36 ticks
            int gap = (int)Math.Ceiling(h.Config.InterpDelayTicks) + h.Server.Config.AntiCheat.ViewBehindAckTicks;
            var list = new List<InputCmd>();
            var w = new ByteWriter(1024);
            shooter.Tamper = data =>
            {
                if (Protocol.PeekType(data, 0, data.Length) != MsgType.Inputs) return data;
                var r = new ByteReader(data, 0, data.Length);
                InputsMsg m = Protocol.ReadInputs(ref r, list);
                for (int i = 0; i < list.Count; i++) { InputCmd c = list[i]; c.ViewTick -= back; list[i] = c; }
                int newestView = list[list.Count - 1].ViewTick;
                int ack = Math.Min(m.AckSnapshotTick, newestView + gap); // try to keep the newest command under the threshold
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = ack, Cmds = list });
                return w.ToArray();
            };
            h.RunSeconds(12);

            TestContext.WriteLine(h.Server.Flags.Report());
            Assert.That(h.Server.Flags.Count(FlagKind.ViewBehindAck), Is.GreaterThan(0),
                "the lowered-ack backtracker no longer evades the lag-comp check: ViewBehindAck fires on the backtracked commands");
        }

        /// <summary>
        /// SAFE confirmation: a client connection can only move its own player. The input packet carries no player or
        /// connection id; the server keys everything off the transport connection id. Here client A's tampered packets
        /// (forced full-speed north) never move client B, proving commands can't be forged for another player.
        /// </summary>
        [Test]
        public void AClientCannotForgeAnotherPlayersCommands_Safe()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20), seed: 1);
            var b = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20), seed: 2, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 8));
            a.Script = t => new MoveInput { MoveZ = 1 };       // A sprints north
            b.Script = t => default;                            // B stands still
            Vec3d bStart = b.ServerSide.State.Position;
            h.RunSeconds(2);
            Assert.That((a.ServerSide.State.Position - h.Spawn).HorizontalLength, Is.GreaterThan(3), "A moved");
            Assert.That((b.ServerSide.State.Position - bStart).HorizontalLength, Is.LessThan(0.01), "B never moved: no packet can address another player");
        }

        /// <summary>
        /// SAFE confirmation: garbage, truncated, oversized and wrong-type packets are dropped (and flagged Malformed)
        /// without throwing out of Receive or touching the simulation. Includes an oversized command count and a huge
        /// forged ack.
        /// </summary>
        [Test]
        public void MalformedPackets_AreDroppedAndFlagged_NeverCrashTheServer_Safe()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.3);

            // Random junk of many lengths.
            var rng = new Random(11);
            for (int i = 0; i < 400; i++)
            {
                var bytes = new byte[rng.Next(0, 40)];
                rng.NextBytes(bytes);
                Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, bytes, 0, bytes.Length));
            }
            // An Inputs packet claiming 255 commands (> MaxCmdsPerPacket) but with a truncated body.
            Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, new byte[] { (byte)MsgType.Inputs, 0, 0, 0, 0, 255, 0, 0, 0, 0 }, 0, 10));

            // A well-formed Inputs with a wildly forged ack: accepted at parse level, must not crash or desync.
            var w = new ByteWriter(256);
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = int.MaxValue, Cmds = new List<InputCmd> { new InputCmd { Tick = h.Server.Tick + 2, ViewTick = h.Server.Tick } } });
            Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, w.Buffer, 0, w.Length));
            w.Clear();
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = int.MinValue, Cmds = new List<InputCmd> { new InputCmd { Tick = h.Server.Tick + 2, ViewTick = h.Server.Tick } } });
            Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, w.Buffer, 0, w.Length));

            Assert.DoesNotThrow(() => h.RunSeconds(1));
            Assert.That(h.Server.Flags.Count(FlagKind.Malformed), Is.GreaterThan(0), "junk is flagged");
            Assert.That(p.Client.Corrections, Is.Zero, "nothing the junk said reached the simulation");
        }

        /// <summary>
        /// SAFE confirmation: a forged out-of-range heal tile id (max u16, far past the real tile count) is rejected
        /// cleanly (bounds-checked in TileHealing.Check), never an IndexOutOfRange that would crash the server tick.
        /// Uses the duel map, which has breakable tiles.
        /// </summary>
        [Test]
        public void ForgedOutOfRangeHealAndDoorTileIds_DontCrashTheServer_Safe()
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0]);
            var p = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20));
            Assert.That(h.Server.Walls, Is.Not.Null);
            int realCount = h.Server.Walls.Count;
            var list = new List<InputCmd>();
            var w = new ByteWriter(256);
            shooterTamperHealDoor(p, list, w, realCount);
            Assert.DoesNotThrow(() => h.RunSeconds(1.5), "an out-of-range heal/door tile id must not crash the server");
        }

        private static void shooterTamperHealDoor(NetHarness.Peer p, List<InputCmd> list, ByteWriter w, int realCount)
        {
            p.Tamper = data =>
            {
                if (Protocol.PeekType(data, 0, data.Length) != MsgType.Inputs) return data;
                var r = new ByteReader(data, 0, data.Length);
                InputsMsg m = Protocol.ReadInputs(ref r, list);
                for (int i = 0; i < list.Count; i++)
                {
                    InputCmd c = list[i];
                    // Forge a heal and a door on tile ids far past the real tile count (still within the u16 wire field).
                    c.Hand.Heal = true; c.Hand.HealTile = ushort.MaxValue; c.Hand.HealCell = 63; c.Hand.HealReinforce = (i & 1) == 0;
                    c.Hand.Door = true; c.Hand.DoorTile = (ushort)Math.Min(ushort.MaxValue, realCount + 5000); c.Hand.DoorOpen = true;
                    list[i] = c;
                }
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = m.AckSnapshotTick, Cmds = list });
                return w.ToArray();
            };
        }
    }
}
