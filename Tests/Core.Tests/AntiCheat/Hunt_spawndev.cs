using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Hunt
{
    /// <summary>
    /// Red-team hunt: SPAWN / RESPAWN / DEV-KEY / MATCH-STATE.
    /// A modified client forges Respawn / Warp / ForcedRespawn bits and spams respawn after death, with the server in a
    /// real hosted configuration (AllowDevRespawn/AllowDevWarp OFF). Every test drives a real <see cref="NetServer"/> with
    /// raw forged input packets, exactly what a hacked client would send.
    ///
    /// Outcome of the hunt: the protocol-33 fix is airtight. These tests PASS on the current server (they are regression
    /// guards, not a failing proof of a hole): the server refuses every forged respawn/warp, never shortens the death
    /// wait, and never exposes a client-settable ForcedRespawn. If a future change reopened the hole, these would fail.
    /// </summary>
    public class Hunt_spawndev
    {
        private static readonly Vec3d Spawn = new Vec3d(20, 0, 20);

        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private static NetServer Server(out ServerPlayer p, System.Action<NetConfig> tweak = null)
        {
            var nc = new NetConfig();
            tweak?.Invoke(nc);
            var s = new NetServer(OpenField(), new MovementConfig(), nc, (c, d, r) => { });
            p = s.AddPlayer(1, 1, Spawn, 0);
            return s;
        }

        /// <summary>Feed the server one forged input command for a near-future tick, as a hacked client would.</summary>
        private static void Forge(NetServer s, int conn, InputCmd c)
        {
            var w = new ByteWriter(256);
            Protocol.Write(w, new InputsMsg { AckSnapshotTick = s.Tick, Cmds = new List<InputCmd> { c } });
            s.Receive(conn, w.Buffer, 0, w.Length);
        }

        // ================================================================ forged dev warp

        [Test]
        public void ForgedWarp_WhileAlive_IsRefused_AndFlagged_UnlessTheServerAllowsIt()
        {
            foreach (bool allow in new[] { false, true })
            {
                var warpTo = new Vec3d(1000, 0, 1000);
                NetServer s = Server(out ServerPlayer p, nc => { nc.AllowDevWarp = allow; nc.DevWarpPoint = warpTo; });
                for (int i = 0; i < 20; i++)
                {
                    // A modified client sets the warp bit (F6 dev key) every tick.
                    Forge(s, 1, new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick - 2, Warp = true });
                    s.Step();
                }
                double fromSpawn = (p.State.Position - Spawn).HorizontalLength;
                double toWarp = (p.State.Position - warpTo).HorizontalLength;
                if (allow)
                {
                    Assert.That(toWarp, Is.LessThan(1.0), "a dev server honours the warp");
                    Assert.That(s.Flags.Count(FlagKind.DevWarpRefused), Is.Zero);
                }
                else
                {
                    Assert.That(fromSpawn, Is.LessThan(1.0), "a hacked client cannot teleport across the map");
                    Assert.That(s.Flags.Count(FlagKind.DevWarpRefused), Is.GreaterThan(0), "the refused warp is logged for review");
                }
            }
        }

        // ================================================================ forged dev respawn (teleport home)

        [Test]
        public void ForgedRespawn_WhileAlive_CannotTeleportHome_OnAHostedServer()
        {
            NetServer s = Server(out ServerPlayer p, nc => nc.AllowDevRespawn = false);
            // Walk away from spawn first.
            for (int i = 0; i < 160; i++)
            {
                Forge(s, 1, new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick - 2, Move = new MoveInput { MoveZ = 1 } });
                s.Step();
            }
            double away = (p.State.Position - Spawn).HorizontalLength;
            Assert.That(away, Is.GreaterThan(3), "we actually moved away");

            // Now a hacked client spams the respawn bit to snap home at full health mid-fight.
            for (int i = 0; i < 20; i++)
            {
                Forge(s, 1, new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick - 2, Respawn = true });
                s.Step();
            }
            Assert.That((p.State.Position - Spawn).HorizontalLength, Is.GreaterThan(3), "the forged respawn did nothing");
            Assert.That(s.Flags.Count(FlagKind.DevRespawnRefused), Is.GreaterThan(0));
        }

        // ================================================================ 3 s death wait cannot be shortened

        [Test]
        public void ForgedRespawn_AfterDeath_DoesNotShortenTheServerSideWait()
        {
            NetServer s = Server(out ServerPlayer p, nc => nc.AllowDevRespawn = false);
            for (int i = 0; i < 5; i++) s.Step();

            // Kill the player the way the server would (full death, with its real respawn delay).
            int delay = s.Movement.Weapons.RespawnDelayTicks;
            WeaponRules.ApplyDamage(ref p.State, 10000, s.Tick, s.Movement.Weapons);
            Assert.That(p.State.Dead, Is.True);
            int respawnTick = p.State.RespawnTick;
            Assert.That(respawnTick, Is.GreaterThan(s.Tick + 1), "a real respawn delay is pending");

            // A hacked client spams respawn to come back early. Step up to just before the server's own respawn tick.
            bool everAliveEarly = false;
            while (s.Tick < respawnTick - 1)
            {
                Forge(s, 1, new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick - 2, Respawn = true });
                s.Step();
                if (!p.State.Dead) { everAliveEarly = true; break; }
            }
            Assert.That(everAliveEarly, Is.False, "the client cannot shorten the 3 s death wait");
            Assert.That(s.Flags.Count(FlagKind.DevRespawnRefused), Is.GreaterThan(0));

            // The server's own timer still brings them back on schedule (not blocked by the refusal).
            for (int i = 0; i < delay; i++) { s.Step(); if (!p.State.Dead) break; }
            Assert.That(p.State.Dead, Is.False, "the legitimate server-side respawn still fires");
        }

        // ================================================================ ForcedRespawn is not on the wire

        [Test]
        public void TheForcedRespawnBit_CannotBeReachedFromTheWire()
        {
            // Send a command with every settable byte flag turned on. ForcedRespawn (server-only, honoured even with dev
            // respawn off) has no wire bit, so it stays false and the player is not warped/respawned.
            NetServer s = Server(out ServerPlayer p, nc => { nc.AllowDevRespawn = false; nc.AllowDevWarp = false; });
            for (int i = 0; i < 160; i++)
            {
                Forge(s, 1, new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick - 2, Move = new MoveInput { MoveZ = 1 } });
                s.Step();
            }
            double away = (p.State.Position - Spawn).HorizontalLength;
            Assert.That(away, Is.GreaterThan(3));

            for (int i = 0; i < 20; i++)
            {
                Forge(s, 1, new InputCmd
                {
                    Tick = s.Tick + 1,
                    ViewTick = s.Tick - 2,
                    Respawn = true,
                    ForcedRespawn = true, // a hacked client sets this struct field, but Write/Read never carry it
                });
                s.Step();
            }
            Assert.That((p.State.Position - Spawn).HorizontalLength, Is.GreaterThan(3),
                "no client bit maps to ForcedRespawn, so a forged forced-respawn is still refused");
            Assert.That(s.Flags.Count(FlagKind.DevRespawnRefused), Is.GreaterThan(0));
        }

        // ================================================================ match invulnerability is server-global

        [Test]
        public void MatchProtection_HoldsDuringCountdownAndResults_ServerSide()
        {
            // The invulnerability window is a property of the server's match phase (Hurt returns no damage while
            // Match.Protects), not of any client value, so a hacked client cannot desync itself out of it or into it.
            var m = new MatchState();
            m.Phase = MatchPhase.Countdown;
            Assert.That(m.Protects, Is.True);
            Assert.That(m.CountsKills, Is.False);
            m.Phase = MatchPhase.Ended;
            Assert.That(m.Protects, Is.True);
            m.Phase = MatchPhase.Playing;
            Assert.That(m.Protects, Is.False);
            Assert.That(m.CountsKills, Is.True);
        }
    }
}
