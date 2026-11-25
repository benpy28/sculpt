using System;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Weapons
{
    /// <summary>The friend demo match: most kills in 5 minutes, server-authoritative, reset between matches.</summary>
    public class MatchTests
    {
        private static SculptNetHarness Arena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        });

        private static void Short(NetServer s)
        {
            s.MatchConfig.Enabled = true;
            s.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            s.MatchConfig.DurationTicks = 600 * SimTime.Per60;
            s.MatchConfig.ResultsTicks = 60 * SimTime.Per60;
        }

        private static void AimAt(NetClient c, Vec3d point, out double yaw, out float pitch)
        {
            MotorState s = c.Predicted;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, c.Movement), 0);
            Vec3d d = point - eye;
            yaw = Math.Atan2(d.X, d.Z);
            pitch = (float)(-Math.Asin(d.Y / d.Length) * 180 / Math.PI);
        }

        /// <summary>Pistol out at once; shoots the target's chest every 15 ticks while <paramref name="fire"/> says so.</summary>
        private static Func<SculptNetHarness.Peer, int, SculptTickInput> Shooter(Func<SculptNetHarness.Peer> target, Func<bool> fire)
        {
            int start = int.MinValue;
            return (p, t) =>
            {
                if (start == int.MinValue) start = t;
                var i = new SculptTickInput();
                if (t == start + 1) i.Hand.WeaponKey = (byte)((int)Weapon.Pistol + 1);
                if (p.Client.TryGetRemote(target().ServerSide.Id, out RemoteView v))
                {
                    AimAt(p.Client, v.Position + new Vec3d(0, 1.0, 0), out double yaw, out i.Pitch);
                    i.Move.Yaw = yaw;
                }
                bool f = t > start + 40 * SimTime.Per60 && (t - start) % (15 * SimTime.Per60) == 0 && fire();
                i.Move.FireHeld = i.Move.FirePressed = i.Move.WantsToShoot = f;
                return i;
            };
        }

        [Test]
        public void OnePlayerWaits_TwoStartACountdown_ThenPlaying_ThenResults_ThenANewMatch()
        {
            var h = Arena();
            Short(h.Server);
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            h.RunSeconds(0.5);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Waiting));
            Assert.That(a.Client.Match.Phase, Is.EqualTo(MatchPhase.Waiting), "the client is told");

            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            h.RunSeconds(0.2);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            h.RunSeconds(0.5);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That(b.Client.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            int end = h.Server.Match.PhaseEndTick;
            Assert.That(end - h.Server.Match.PhaseStartTick, Is.EqualTo(600 * SimTime.Per60));
            Assert.That(a.Client.Match.PhaseEndTick, Is.EqualTo(end), "clients count down to the same tick");
            h.RunSeconds(10.2);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Ended));
            h.RunSeconds(1.1);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown).Or.EqualTo(MatchPhase.Playing), "and again");
            Assert.That(h.Server.Match.PhaseStartTick, Is.GreaterThan(end + 60 * SimTime.Per60 - 1), "a new match after the results");

            h.Remove(b);
            h.RunSeconds(0.1);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Waiting), "back to waiting when the opponent leaves");
        }

        [Test]
        public void KillsCountWhilePlaying_NobodyIsHurtInTheCountdown()
        {
            var h = Arena();
            Short(h.Server);
            h.Server.MatchConfig.CountdownTicks = 240 * SimTime.Per60; // long enough to shoot at someone during it
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            a.Script = Shooter(() => b, () => true);
            h.RunSeconds(3.5);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            Assert.That(b.ServerSide.State.Health(h.Server.Movement.Weapons), Is.EqualTo(h.Server.Movement.Weapons.MaxHealth), "protected in the countdown");

            h.RunSeconds(1.5 + 5 * 15 / 60.0 + 0.5); // 15 sixtieths apart
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That(b.ServerSide.MatchDeaths, Is.GreaterThanOrEqualTo(1));
            Assert.That(a.ServerSide.MatchKills, Is.EqualTo(b.ServerSide.MatchDeaths));
            h.RunSeconds(0.2);
            Assert.That(a.Client.Match.ScoreOf(a.ServerSide.Id).Kills, Is.EqualTo(a.ServerSide.MatchKills), "scores reach the clients");
            Assert.That(b.Client.Match.Leader(), Is.EqualTo(a.ServerSide.Id));
        }

        [Test]
        public void ANewMatchPutsTheWallsBackAndRespawnsEveryone()
        {
            var brushes = DuelMap.BuildBrushes(out BreachMapInfo info);
            var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0]);
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            h.Server.MatchConfig.DurationTicks = 120 * SimTime.Per60;
            h.Server.MatchConfig.ResultsTicks = 30 * SimTime.Per60;
            var a = h.AddClient(spawn: info.TeamSpawns[0][0]);
            var b = h.AddClient(spawn: info.TeamSpawns[1][0], team: 2, seed: 2);
            a.Script = t => new MoveInput { MoveZ = 1, SprintHeld = true }; // runs north, away from spawn
            h.RunSeconds(1.0);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            int tile = info.Block.Tiles[0];
            h.Server.Walls.Damage(tile, 1000, TileCause.Bullet, 0);
            Assert.That(h.Server.Walls[tile].State, Is.EqualTo(TileState.Broken));
            h.RunSeconds(0.3);
            Assert.That(a.Client.Walls[tile].State, Is.EqualTo(TileState.Broken));
            Assert.That(a.ServerSide.State.Position.Z, Is.GreaterThan(info.TeamSpawns[0][0].Z + 3));

            a.Script = t => default;
            h.RunSeconds(2.0 + 0.6); // playing ends, results, new countdown
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown).Or.EqualTo(MatchPhase.Playing));
            Assert.That(h.Server.Walls[tile].State, Is.EqualTo(TileState.Intact), "walls back");
            Assert.That(a.Client.Walls[tile].State, Is.EqualTo(TileState.Intact), "on the clients too");
            Assert.That(a.ServerSide.State.Position.Z, Is.EqualTo(info.TeamSpawns[0][0].Z).Within(0.5), "back at spawn");
            Assert.That(a.Client.Predicted.Position.Z, Is.EqualTo(info.TeamSpawns[0][0].Z).Within(0.5), "the client follows");
        }

        [Test]
        public void AFallSoonAfterBeingHurt_CountsAsTheEnemysKill_ALateFallIsJustADeath()
        {
            foreach (bool recent in new[] { true, false })
            {
                var brushes = DuelMap.BuildBrushes(out BreachMapInfo info);
                Aabb hall = info.Block.Buildings[0];
                var roofEdge = new Vec3d(hall.Min.X + 1.0, hall.Max.Y, hall.Min.Z + 2.5); // over a roofed corner, clear of the stairwell and the bridge
                var h = new NetHarness(() => DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out _)), info.TeamSpawns[0][0]);
                h.Server.MatchConfig.Enabled = true;
                h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
                var a = h.AddClient(spawn: info.TeamSpawns[0][0]);
                var b = h.AddClient(spawn: roofEdge, team: 2, seed: 2);
                h.RunSeconds(1.0);
                Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
                // b stands on the roof edge (the countdown respawned it at its spawn: put it back) nearly dead.
                b.ServerSide.State = MotorState.At(roofEdge);
                b.ServerSide.Body.Position = roofEdge;
                b.ServerSide.State.Damage = h.Server.Movement.Weapons.MaxHealth - 2; // a roof fall does a few points
                b.ServerSide.LastHurtBy = a.ServerSide.Id;
                b.ServerSide.LastHurtTick = recent ? h.Server.Tick : h.Server.Tick - NetServer.KillCreditTicks - 60 * SimTime.Per60;
                b.Script = t => new MoveInput { Yaw = -Math.PI / 2, MoveZ = 1 }; // west, off the edge
                h.RunSeconds(2.0);
                Assert.That(b.ServerSide.MatchDeaths, Is.EqualTo(1), $"the fall killed it (at {b.ServerSide.State.Position}, damage {b.ServerSide.State.Damage}, dead {b.ServerSide.State.Dead})");
                Assert.That(a.ServerSide.MatchKills, Is.EqualTo(recent ? 1 : 0), recent ? "credited to the enemy who hurt it" : "too long ago: just a death");
            }
        }

        [Test]
        public void MatchOffByDefault_NothingChangesForOfflinePractice()
        {
            var h = Arena();
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1);
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2);
            h.RunSeconds(1);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Off));
            Assert.That(a.Client.Match.Phase, Is.EqualTo(MatchPhase.Off));
        }
    }
}
