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

        private static Scripted BrainOf(List<Scripted> brains, NetServer s, ServerPlayer p)
        {
            Assert.That(s.Bots.TryGetValue(p.Id, out IBotBrain brain));
            return (Scripted)brain;
        }
    }
}
