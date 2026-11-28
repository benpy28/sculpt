using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>Ben's battle royale decisions of 2026-11-28: the lobby, the countdown, the final circle, falls.</summary>
    public class BrRulesTests
    {
        private sealed class Scripted : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> Make;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me) =>
                Make != null ? Make(server, me) : new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        private static NetServer NewServer(BrConfig cfg, out BrMapInfo br, NetConfig nc = null)
        {
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out br);
            var s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), nc ?? new NetConfig(), (c, d, r) => { });
            s.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(s, info, br);
            return s;
        }

        private static BrConfig Quick(BrMode mode, List<Scripted> brains, Func<NetServer, ServerPlayer, InputCmd> make = null) => new BrConfig
        {
            Mode = mode,
            MakeBot = i => { var b = new Scripted { Make = make }; brains.Add(b); return b; },
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
        /// Bots fill the lobby to 32 at once, and a full lobby used to start the match on the spot, so a friend joining a
        /// few seconds later never got in. The lobby now always runs its 10 s from the first player.
        /// </summary>
        [Test]
        public void AFullLobby_StillWaitsItsTenSeconds_AndAFriendJoiningLateIsDealtIn()
        {
            var brains = new List<Scripted>();
            NetServer s = NewServer(Quick(BrMode.Solos, brains), out BrMapInfo br);
            s.Step(); // the bots come in after this tick
            Assert.That(s.Players.Count, Is.EqualTo(32), "full at once");
            int first = s.Tick + 1;
            for (int i = 0; i < 5 * SimTime.TicksPerSecond; i++) s.Step();
            Assert.That(s.Match.Phase, Is.EqualTo(MatchPhase.Waiting), "a full lobby still waits");
            Assert.That(s.Match.PhaseEndTick, Is.EqualTo(first + s.Br.LobbyTicks), "clients are told when it starts");
            ServerPlayer friend = s.AddPlayer(1, 1, br.Centre + new Vec3d(0, 2, 0), 0); // a human, 5 s late
            StepUntil(s, () => s.Match.Phase == MatchPhase.Countdown, 6 * SimTime.TicksPerSecond);
            Assert.That(s.Match.PhaseStartTick, Is.EqualTo(first + s.Br.LobbyTicks), "the whole 10 s");
            Assert.That(s.InBrMatch(friend.Id), Is.True, "the friend is in the match");
            Assert.That(s.Players.Count, Is.EqualTo(32), "a bot made room");
        }

        /// <summary>The battle royale's 5 s countdown freezes everyone on their pad too (bots run flat out at it).</summary>
        [Test]
        public void InTheBrCountdown_NobodyLeavesTheirPad()
        {
            var brains = new List<Scripted>();
            NetServer s = NewServer(Quick(BrMode.Solos, brains, (srv, me) => new InputCmd
            {
                Tick = srv.Tick, ViewTick = srv.Tick,
                Move = new MoveInput { MoveZ = 1, SprintHeld = true, JumpPressed = srv.Tick % 20 == 0, JumpHeld = true, FireHeld = true, FirePressed = true, Yaw = srv.Tick * 0.01 % 3 },
            }), out _);
            StepUntil(s, () => s.Match.Phase == MatchPhase.Countdown, 12 * SimTime.TicksPerSecond);
            s.Step(); // the countdown's respawn
            var pads = new Dictionary<int, Vec3d>();
            foreach (ServerPlayer p in s.Players) pads[p.Id] = p.State.Position;
            int shots = 0;
            while (s.Match.Phase == MatchPhase.Countdown)
            {
                s.Step();
                foreach (CombatEvent e in s.CombatEvents) if (e.Type == CombatEventType.Shot) shots++;
                if (s.Match.Phase != MatchPhase.Countdown) break;
                foreach (ServerPlayer p in s.Players)
                    Assert.That((p.State.Position - pads[p.Id]).HorizontalLength, Is.Zero, $"P{p.Id} stays on its pad");
            }
            Assert.That(shots, Is.Zero);
            for (int i = 0; i < SimTime.TicksPerSecond; i++) s.Step();
            int moved = 0;
            foreach (ServerPlayer p in s.Players) if ((p.State.Position - pads[p.Id]).HorizontalLength > 1) moved++;
            Assert.That(moved, Is.GreaterThan(16), "off they go once it is GO");
        }

        private static Scripted BrainOf(NetServer s, ServerPlayer p)
        {
            Assert.That(s.Bots.TryGetValue(p.Id, out IBotBrain brain));
            return (Scripted)brain;
        }
    }
}
