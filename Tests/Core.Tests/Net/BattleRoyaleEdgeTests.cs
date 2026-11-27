using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>Battle royale edge cases found in the bug hunt (2026-11-26).</summary>
    public class BattleRoyaleEdgeTests
    {
        /// <summary>A bot whose next command the test sets (stands still otherwise).</summary>
        private sealed class Scripted : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> Make;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me) =>
                Make != null ? Make(server, me) : new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        private static NetServer NewServer(BrConfig cfg, out BrMapInfo br)
        {
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out br);
            var s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            s.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(s, info, br);
            return s;
        }

        private static BrConfig Quick(BrMode mode, List<Scripted> brains) => new BrConfig
        {
            Mode = mode,
            MakeBot = i => { var b = new Scripted(); brains.Add(b); return b; },
            FillWithoutHumans = true,
            CountdownTicks = 60 * SimTime.Per60,
            ResultsTicks = 120 * SimTime.Per60,
        };

        private static void StepUntil(NetServer s, Func<bool> done, int maxTicks)
        {
            for (int i = 0; i < maxTicks && !done(); i++) s.Step();
            Assert.That(done(), $"not reached in {maxTicks} ticks (phase {s.Match.Phase})");
        }

        private static void Place(ServerPlayer p, Vec3d at)
        {
            p.State.Position = at;
            p.State.Velocity = Vec3d.Zero;
            p.Body.Position = at;
        }

        /// <summary>
        /// Two players trade on the same tick (both shots resolve after everyone moved). The one who dies first has
        /// already dropped their bag when their own shot is resolved, so their gun's rarity must be the one it fired with.
        /// </summary>
        [Test]
        public void ATradedShot_HitsWithTheRarityItWasFiredWith_EvenIfTheShooterDiedThatTick()
        {
            int Trade(bool shooterDies)
            {
                var brains = new List<Scripted>();
                NetServer s = NewServer(Quick(BrMode.Solos, brains), out BrMapInfo br);
                StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
                ServerPlayer a = s.Players[0], b = s.Players[1];
                // Open ground in the middle of the plaza, 4 m apart, facing each other.
                Vec3d at = br.Centre + new Vec3d(0, 0, -2);
                Vec3d bt = br.Centre + new Vec3d(0, 0, 2);
                at = ItemRules.Settle(s.Grid, at + new Vec3d(0, 5, 0));
                bt = ItemRules.Settle(s.Grid, bt + new Vec3d(0, 5, 0));
                Assert.That(bt.Y, Is.EqualTo(at.Y).Within(1e-6), "flat");
                Assert.That(s.Grid.Raycast(at + new Vec3d(0, 1.6, 0), new Vec3d(0, 0, 1), 4).Hit, Is.False, "a clear line on the plaza");
                Place(a, at);
                Place(b, bt);
                a.State.Hand.Bag = new Bag { S0 = Item.Ar, R0 = Rarity.Common };
                b.State.Hand.Bag = new Bag { S0 = Item.Ar, R0 = Rarity.Legendary };
                a.State.Hand.Slot = b.State.Hand.Slot = 0;
                bool fire = false;
                InputCmd Cmd(NetServer srv, double yaw) => new InputCmd
                {
                    Tick = srv.Tick, ViewTick = srv.Tick,
                    Move = new MoveInput { Yaw = yaw, FireHeld = fire, FirePressed = fire },
                };
                BrainOf(brains, s, a).Make = (srv, me) => Cmd(srv, 0);
                BrainOf(brains, s, b).Make = (srv, me) => Cmd(srv, Math.PI);
                for (int i = 0; i < SimTime.TicksPerSecond; i++) { s.Step(); Place(a, at); Place(b, bt); }
                Assert.That(a.State.Damage, Is.Zero);
                Assert.That(b.State.Damage, Is.Zero);
                // b is one hit from death when shooterDies: a's shot (resolved first) kills b, then b's shot resolves.
                b.State.Damage = shooterDies ? s.Movement.Weapons.MaxHealth - 1 : 0;
                fire = true;
                s.Step();
                Assert.That(b.State.Dead, Is.EqualTo(shooterDies));
                return a.State.Damage;
            }
            int alive = Trade(false), traded = Trade(true);
            Assert.That(alive, Is.GreaterThan(0), "b's legendary AR hit a");
            Assert.That(traded, Is.EqualTo(alive), "the traded shot hits as hard as the same shot from a player who lived");
        }

        /// <summary>
        /// Squads: once you are out you watch your living teammates (BrDeathCamera). Interest management decided what a
        /// dead player is sent from where their body fell, so the enemy fighting the teammate you watch was never sent:
        /// the camera showed your teammate shooting at nobody.
        /// </summary>
        [Test]
        public void AnOutPlayer_IsSentWhatTheirLivingTeammatesAreSent()
        {
            var brains = new List<Scripted>();
            NetServer s = NewServer(Quick(BrMode.Squads, brains), out BrMapInfo br);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            ServerPlayer x = s.Players[0], y = null, z = null;
            foreach (ServerPlayer p in s.Players)
            {
                if (p == x) continue;
                if (p.Team == x.Team && y == null) y = p;
                if (p.Team != x.Team && z == null) z = p;
            }
            // x falls to its death at its spawn (the real rules); its body stays there.
            x.State.Damage = s.Movement.Weapons.MaxHealth - 1;
            Place(x, x.State.Position + new Vec3d(0, 40, 0));
            StepUntil(s, () => x.State.Dead, 10 * SimTime.TicksPerSecond);
            Assert.That(s.InBrMatch(x.Id) && x.State.Dead, "x is out of the match");
            x.State.Position = x.Spawn; // went down indoors (the entrance hall), out of sight of the plaza
            // y and z face off across the open plaza, 24 m apart, standing still (no noise).
            Vec3d yAt = ItemRules.Settle(s.Grid, br.Centre + new Vec3d(0, 5, -12));
            Vec3d zAt = ItemRules.Settle(s.Grid, br.Centre + new Vec3d(0, 5, 12));
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++) { Place(y, yAt); Place(z, zAt); s.Step(); }
            Assert.That(s.IsSentTo(y.Id, z.Id), Is.True, "y sees z across the plaza");
            Assert.That((x.State.Position - zAt).HorizontalLength, Is.GreaterThan(40), "x went down far away");
            Assert.That(s.IsSentTo(x.Id, z.Id), Is.True, "x, watching y, gets the enemy y is facing");
        }

        /// <summary>
        /// Someone who joins mid-match sits it out, but kept the team number they joined with. Teams are dealt 1..N, so
        /// that number is usually a team in the match: the late joiner counted as its teammate (always sent its players,
        /// drawn as teammates on the minimap, "VICTORY" if it won).
        /// </summary>
        [Test]
        public void ALateJoiner_IsNobodysTeammate()
        {
            var brains = new List<Scripted>();
            NetServer s = NewServer(Quick(BrMode.Squads, brains), out BrMapInfo br);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            ServerPlayer late = s.AddBot(new Scripted(), 1, br.Centre + new Vec3d(0, 1, 0), 0);
            s.Step();
            Assert.That(late.State.Dead, Is.True, "sits the match out");
            foreach (ServerPlayer p in s.Players)
                if (p != late && s.InBrMatch(p.Id)) Assert.That(p.Team, Is.Not.EqualTo(late.Team), "on no team in the match");
        }

        /// <summary>
        /// "Nobody can be hurt" in the countdown, but fall damage is part of movement, so a long drop during the
        /// countdown still killed you. That death was not a battle royale death (the match wasn't playing yet), so the
        /// normal 3 s respawn brought you back mid-match with the 1v1 loadout: pump, SMG, AR, pistol and grenades.
        /// </summary>
        [Test]
        public void DyingInTheCountdown_PutsYouBackOnYourPadWithTheBrLoadout()
        {
            var brains = new List<Scripted>();
            BrConfig cfg = Quick(BrMode.Solos, brains);
            cfg.CountdownTicks = 5 * SimTime.TicksPerSecond;
            NetServer s = NewServer(cfg, out _);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Countdown, 20 * SimTime.TicksPerSecond);
            for (int i = 0; i < 10; i++) s.Step();
            ServerPlayer p = s.Players[5];
            Place(p, p.Spawn + new Vec3d(0, 40, 0));
            StepUntil(s, () => p.State.Dead || s.Match.Phase != MatchPhase.Countdown, 5 * SimTime.TicksPerSecond);
            Assert.That(p.State.Dead, Is.True, "the drop kills (fall damage is movement, not Hurt)");
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 10 * SimTime.TicksPerSecond);
            for (int i = 0; i < 5 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(p.State.Dead, Is.False, "the countdown can't knock you out of the match");
            Bag b = p.State.Hand.Bag;
            Assert.That(b.S0, Is.EqualTo(Item.Pump));
            Assert.That(b.S1, Is.EqualTo(Item.Ar));
            Assert.That(b.S2, Is.EqualTo(Item.None), "the battle royale loadout, not the 1v1 one");
            Assert.That(b.S3, Is.EqualTo(Item.None));
            Assert.That(b.S4, Is.EqualTo(Item.None));
            Assert.That(s.Match.AliveTeams, Is.EqualTo(32));
        }

        /// <summary>Leaving mid-match is going out of it: what you carried falls where you stood, like a death.</summary>
        [Test]
        public void LeavingMidMatch_DropsYourBag()
        {
            var brains = new List<Scripted>();
            NetServer s = NewServer(Quick(BrMode.Duos, brains), out _);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Playing, 20 * SimTime.TicksPerSecond);
            ServerPlayer p = s.Players[7];
            Vec3d at = p.State.Position;
            int Guns()
            {
                int n = 0;
                for (int i = 0; i < s.Items.Count; i++)
                    if (Items.IsGun(s.Items[i].Kind) && (s.Items[i].Position - at).HorizontalLength < 2) n++;
                return n;
            }
            Assert.That(Guns(), Is.Zero);
            s.RemovePlayer(p.ConnectionId);
            s.Step();
            Assert.That(Guns(), Is.EqualTo(2), "the pump and the locker gun stay in the match for the others");
        }

        private static Scripted BrainOf(List<Scripted> brains, NetServer s, ServerPlayer p)
        {
            Assert.That(s.Bots.TryGetValue(p.Id, out IBotBrain brain));
            return (Scripted)brain;
        }
    }
}
