using System;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.AntiCheat
{
    /// <summary>Bug hunt 2026-11-26 (anti-cheat and server validation): regressions for what was found.</summary>
    public class ServerValidationHuntTests
    {
        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private static NetServer Server() => new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });

        /// <summary>
        /// Interest management must send an enemy whose legs you can see (and shoot) through a low hole. It only tested
        /// lines to the other player's eye and chest, so someone standing behind a wall with a hole near the floor was
        /// left out of your snapshots while a bullet through that hole still hit them: an invisible, hittable player.
        /// </summary>
        [Test]
        public void AnEnemyVisibleOnlyThroughALowHole_IsStillSent()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            w.AddBox(new Vec3d(0, 0.8, 10), new Vec3d(38, 4.5, 10.3), BrushKind.Floor); // a wall with a 0.8 m gap along the floor
            ITerrain t = new CompositeTerrain(g, w);
            var s = new NetServer(t, new MovementConfig(), new NetConfig(), (c, d, r) => { });
            var viewerAt = new Vec3d(19, 0, 4);
            var targetAt = new Vec3d(19, 0, 12);
            ServerPlayer target = s.AddBot(new Brain { F = (sv, me) => new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick, Move = new MoveInput { Yaw = Math.PI } } }, 2, targetAt, Math.PI);
            ServerPlayer viewer = s.AddBot(new Brain { F = (sv, me) => new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick } }, 1, viewerAt, 0);
            target.Viewer = viewer.Viewer = true; // stand-ins for people: interest is worked out for viewers
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++) s.Step();
            Vec3d eye = viewer.State.Position + new Vec3d(0, PlayerMovement.EyeOf(viewer.State, s.Movement), 0);
            Assert.That(DuelBot.CanSee(t, eye, target.State.Position, PlayerMovement.HeightOf(target.State, s.Movement)), Is.True,
                "the viewer can see the target's knees under the wall");
            Vec3d chest = target.State.Position + new Vec3d(0, 1.08, 0);
            Assert.That(t.Raycast(eye, (chest - eye) / (chest - eye).Length, (chest - eye).Length).Hit, Is.True, "but not the chest");
            Assert.That(s.IsSentTo(viewer.Id, target.Id), Is.True, "so the target is in the viewer's snapshots");
            Assert.That(s.IsSentTo(target.Id, viewer.Id), Is.True, "and the other way round");
        }

        /// <summary>
        /// A shot's rarity is the gun's when it was fired. Shots resolve after every player's tick, so a drop (or a BR
        /// death that empties the bag) on the firing tick used to change the shot's damage: a legendary AR's bullet did
        /// normal damage if the same command also dropped it (and a trade kill in the same tick did the same).
        /// </summary>
        [Test]
        public void AShotKeepsTheRarityItWasFiredWith_EvenIfTheGunLeavesTheBagThatTick()
        {
            foreach (bool dropSameTick in new[] { false, true })
            {
                var mc = new MovementConfig();
                var s = new NetServer(OpenField(), mc, new NetConfig(), (c, d, r) => { });
                var at = new Vec3d(16, 0, 10);
                int fireTick = -1;
                ServerPlayer target = s.AddBot(new Brain { F = (sv, me) => new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick, Move = new MoveInput { Yaw = Math.PI } } }, 2, at + new Vec3d(0, 0, 6), Math.PI);
                ServerPlayer shooter = s.AddBot(new Brain
                {
                    F = (sv, me) =>
                    {
                        var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick, Move = new MoveInput { Yaw = 0 } };
                        double dy = target.State.Position.Y + 1.0 - (me.State.Position.Y + 1.62);
                        c.Pitch = (float)(-Math.Atan2(dy, 6) * 180 / Math.PI);
                        if (sv.Tick == fireTick) { c.Move.FirePressed = true; c.Move.FireHeld = true; c.Hand.Drop = dropSameTick; }
                        return c;
                    },
                }, 1, at, 0);
                for (int i = 0; i < 60; i++) s.Step();
                shooter.State.Hand.Bag = new Bag { S0 = Item.Ar, R0 = Rarity.Legendary };
                shooter.State.Hand.Slot = 0;
                shooter.State.Hand.PrevSlot = 1;
                for (int i = 0; i < 60; i++) s.Step();
                fireTick = s.Tick + 1;
                int dmg = -1;
                s.Step();
                foreach (CombatEvent e in s.CombatEvents)
                    if (e.Type == CombatEventType.Hit && e.Attacker == shooter.Id) dmg = e.Damage;
                int plain = mc.Weapons.ScaleDamage((int)Math.Round(mc.Weapons.Of(Weapon.Ar).BodyDamage), Rarity.None);
                Assert.That(dmg, Is.GreaterThan(0), "the shot hit");
                Assert.That(dmg, Is.GreaterThan(plain), $"legendary damage (drop on the same tick: {dropSameTick})");
                if (dropSameTick) Assert.That(shooter.State.Hand.Bag[0], Is.EqualTo(Item.None), "the drop went through");
            }
        }
    }
}
