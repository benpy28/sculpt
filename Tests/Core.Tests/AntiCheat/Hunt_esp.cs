using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.AntiCheat
{
    /// <summary>
    /// ESP / wallhack red-team hunt (2026-11-26). Interest management (NetServer.AntiCheat.cs) withholds HIDDEN enemies
    /// from a client's SNAPSHOTS. But the per-tick Combat (NetServer.Combat.cs SendCombat), Tiles (SendTiles) and Items
    /// (SendItems) messages are BROADCAST to everyone with no interest gate. These tests drive a real NetServer the way a
    /// hacked client that reads every packet would, and prove the server hands it the exact position of an enemy whose
    /// snapshot it is being denied.
    /// </summary>
    public class Hunt_esp
    {
        // ---- harness ----------------------------------------------------------------

        /// <summary>Records every byte array the server sends, by connection id (what a wiretapping cheat client sees).</summary>
        private sealed class Capture
        {
            public readonly Dictionary<int, List<byte[]>> ByConn = new Dictionary<int, List<byte[]>>();
            public void Send(int conn, byte[] data, bool reliable)
            {
                if (!ByConn.TryGetValue(conn, out List<byte[]> l)) ByConn[conn] = l = new List<byte[]>();
                l.Add((byte[])data.Clone());
            }
            public void Clear() { foreach (List<byte[]> l in ByConn.Values) l.Clear(); }
            public IEnumerable<byte[]> To(int conn) => ByConn.TryGetValue(conn, out List<byte[]> l) ? l : new List<byte[]>();
        }

        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        private static InputCmd Cmd(NetServer s) => new InputCmd { Tick = s.Tick, ViewTick = s.Tick };
        private static double YawTo(Vec3d from, Vec3d to) => Math.Atan2(to.X - from.X, to.Z - from.Z);
        private static Vec3d Eye(NetServer s, ServerPlayer p) => p.State.Position + new Vec3d(0, PlayerMovement.EyeOf(p.State, s.Movement), 0);

        private static IEnumerable<CombatEvent> CombatTo(Capture cap, int conn)
        {
            var into = new List<CombatEvent>();
            foreach (byte[] pkt in cap.To(conn))
            {
                if (Protocol.PeekType(pkt, 0, pkt.Length) != MsgType.Combat) continue;
                var r = new ByteReader(pkt, 0, pkt.Length);
                CombatProtocol.Read(ref r, into);
                foreach (CombatEvent e in into) yield return e;
            }
        }

        // ---- REGRESSION GUARD 1: a Hit on a hidden enemy no longer leaks their position to an uninvolved client ------

        /// <summary>
        /// Was a finding. Default config, duel map. S (team 2) and V (team 1, the enemy) fight in the open on one side;
        /// O (team 2) is a cheating client parked far across the divide with no line of sight to V. Interest withholds
        /// V's snapshot from O. SendCombat used to broadcast every Hit event (Victim id + impact Point) to O, so O's
        /// wallhack learned V's exact position through the map. Fixed: an uninvolved receiver only gets a Hit event when
        /// the victim is being sent to them (IsSentTo), with HealthAfter masked. N (V's teammate, right beside V, always
        /// sent V) still receives the Hit, so legit third-party hit effects are unharmed.
        /// </summary>
        [Test]
        public void AHitOnAHiddenEnemy_IsNotLeaked_ToAnUninvolvedClient()
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var cap = new Capture();
            var s = new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), new MovementConfig(), new NetConfig(), cap.Send);

            Vec3d sSpawn = info.TeamSpawns[1][0];
            Vec3d vPos = sSpawn + new Vec3d(3, 0, 0);
            ServerPlayer v = null, shooter = null;
            shooter = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    var c = Cmd(sv);
                    c.Move.Yaw = YawTo(Eye(sv, me), v.State.Position + new Vec3d(0, 0.9, 0));
                    c.Move.FireHeld = true;
                    c.Move.WantsToShoot = true;
                    return c;
                }
            }, 2, sSpawn, 0);
            v = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 1, vPos, 0);
            // O: a real connection (id 1), an enemy of V, parked across the divide. It just stands still and reads packets.
            ServerPlayer o = s.AddPlayer(1, 2, info.TeamSpawns[0][0], 0);
            // N: a real connection (id 2), V's teammate, standing right beside V (always sent V): a legit third party.
            ServerPlayer n = s.AddPlayer(2, 1, vPos + new Vec3d(1.0, 0, 0), 0);

            for (int i = 0; i < 30; i++) s.Step();         // settle interest
            Assert.That(s.IsSentTo(o.Id, v.Id), Is.False, "O cannot see or hear V: interest withholds V's snapshot from O");
            Assert.That(s.IsSentTo(n.Id, v.Id), Is.True, "N stands next to V (teammate): V is always sent to N");
            Assert.That((o.State.Position - v.State.Position).Length, Is.GreaterThan(30), "O really is far from V");

            cap.Clear();
            int hitsLeaked = 0, hitsToN = 0;
            for (int i = 0; i < 3 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                // Only a hit delivered while V is actually withheld from O is a leak. A dead player is sent to everyone
                // by design (and respawns elsewhere), so the death-blow Hit reaching O is interest policy, not a leak.
                bool vWithheldFromO = !s.IsSentTo(o.Id, v.Id);
                foreach (CombatEvent e in CombatTo(cap, o.ConnectionId))
                    if (e.Type == CombatEventType.Hit && e.Victim == v.Id && vWithheldFromO) hitsLeaked++;
                foreach (CombatEvent e in CombatTo(cap, n.ConnectionId))
                    if (e.Type == CombatEventType.Hit && e.Victim == v.Id)
                    {
                        hitsToN++;
                        Assert.That(e.HealthAfter, Is.EqualTo(-1), "a third party never learns the victim's health");
                    }
                cap.Clear();
            }

            Assert.That(shooter.Id, Is.Not.EqualTo(0));
            Assert.That(hitsLeaked, Is.Zero,
                "no Hit event about hidden V reaches O, the client it is withholding V's snapshot from");
            Assert.That(hitsToN, Is.GreaterThan(0),
                "N (beside V, sent V) still receives V's Hit events: legit third-party hit effects are unharmed");
        }

        // ---- REGRESSION GUARD 2: a hidden shooter's Shot no longer leaks their exact eye and aim -----------------------

        /// <summary>
        /// Was a finding. A shooting enemy more than ShotNoiseRange (250 m, the BR map is ~700 m) from a client is
        /// withheld by interest, yet SendCombat used to broadcast their Shot event (exact Origin = eye, and Aim) to
        /// everyone. Modelled here by shrinking the interest ranges so a quiet/distant pair on the duel map is withheld;
        /// the mechanism is identical at any scale. Fixed: a Shot/Grenade event only goes to receivers the attacker is
        /// being sent to. Interest force-sends anyone who fired or threw within 1 s and 250 m, so a shot you could hear
        /// still reaches you (asserted with interest off, where the broadcast must still happen).
        /// </summary>
        [Test]
        public void AHiddenEnemysShot_IsNotLeaked_ButIsStillSentWhenAudible()
        {
            DuelMap.BuildBrushes(out BreachMapInfo info);

            // (a) With interest on and shrunk ranges, a far, unheard shooter is withheld: O gets none of their Shots.
            var cap = new Capture();
            var s = new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), new MovementConfig(), new NetConfig(), cap.Send);
            s.Config.AntiCheat.Interest.ShotNoiseRange = 5;
            s.Config.AntiCheat.Interest.MaxSightRange = 5;
            s.Config.AntiCheat.Interest.AlwaysRange = 1;

            ServerPlayer shooter = s.AddBot(new Brain
            {
                F = (sv, me) => { var c = Cmd(sv); c.Move.FireHeld = true; c.Move.WantsToShoot = true; return c; }
            }, 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            ServerPlayer o = s.AddPlayer(1, 1, info.TeamSpawns[0][0], 0);   // enemy cheat client, far across the map

            for (int i = 0; i < 30; i++) s.Step();
            Assert.That(s.IsSentTo(o.Id, shooter.Id), Is.False, "the shooter is hidden and (with these ranges) unheard: withheld");

            cap.Clear();
            int shotsLeaked = 0;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                foreach (CombatEvent e in CombatTo(cap, o.ConnectionId))
                    if (e.Type == CombatEventType.Shot && e.Attacker == shooter.Id) shotsLeaked++;
                Assert.That(s.IsSentTo(o.Id, shooter.Id), Is.False, "still withheld while shooting");
                cap.Clear();
            }
            Assert.That(shotsLeaked, Is.Zero,
                "a hidden shooter's Shot events (exact eye + aim) never reach a client they are withheld from");

            // (b) With interest off, the Shot must still be broadcast to the enemy (no gameplay regression): the gate
            // only ever removes events the receiver could not see or hear.
            var cap2 = new Capture();
            var s2 = new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), new MovementConfig(), new NetConfig(), cap2.Send);
            s2.Config.AntiCheat.Interest.Enabled = false;
            ServerPlayer sh2 = s2.AddBot(new Brain
            {
                F = (sv, me) => { var c = Cmd(sv); c.Move.FireHeld = true; c.Move.WantsToShoot = true; return c; }
            }, 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            ServerPlayer o2 = s2.AddPlayer(1, 1, info.TeamSpawns[0][0], 0);
            for (int i = 0; i < 30; i++) s2.Step();
            cap2.Clear();
            int shotsSeen = 0;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++)
            {
                s2.Step();
                foreach (CombatEvent e in CombatTo(cap2, o2.ConnectionId))
                    if (e.Type == CombatEventType.Shot && e.Attacker == sh2.Id) shotsSeen++;
                cap2.Clear();
            }
            Assert.That(shotsSeen, Is.GreaterThan(0),
                "with interest off, Shot events are still broadcast to the enemy (the gate never blocks audible/visible shots)");
        }
    }
}
