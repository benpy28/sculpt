using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// Ben 2026-11-28: "you should be fully frozen besides looking around and switching what you hold in your inventory
    /// for the 5 second countdown". Every match countdown (1v1 and battle royale).
    /// </summary>
    public class CountdownTests
    {
        private static SculptNetHarness Arena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        });

        /// <summary>Everything at once: run, sprint, jump, crouch, fire, aim, reload, punch, pick up, drop, door, heal.</summary>
        private static SculptTickInput Mash(int t)
        {
            var i = new SculptTickInput();
            i.Move = new MoveInput
            {
                MoveZ = 1, MoveX = (t / 30) % 2 == 0 ? 1 : -1, Yaw = Math.IEEERemainder(t * 0.05, 2 * Math.PI), SprintHeld = true,
                JumpPressed = t % 7 == 0, JumpHeld = true, CrouchPressed = t % 11 == 0, CrouchHeld = t % 22 < 11,
                FireHeld = true, FirePressed = t % 3 == 0, WantsToShoot = true, AimPressed = t % 5 == 0,
            };
            i.Pitch = (float)(20 * Math.Sin(t * 0.03));
            i.Hand.Reload = t % 13 == 0;
            i.Hand.Punch = t % 17 == 0;
            i.Hand.Drop = t % 19 == 0;
            if (t % 23 == 0) { i.Hand.Heal = true; i.Hand.HealTile = 0; }
            if (t % 29 == 0) { i.Hand.Door = true; i.Hand.DoorTile = 0; i.Hand.DoorOpen = true; }
            if (t % 31 == 0) { i.Hand.Pickup = true; i.Hand.PickId = 1; }
            return i;
        }

        [Test]
        public void InTheCountdown_YouCanOnlyLookAndChangeSlots_AndPredictionStaysExactAt100msPing()
        {
            var h = Arena();
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 240 * SimTime.Per60;
            h.Server.MatchConfig.DurationTicks = 600 * SimTime.Per60;
            var a = h.AddClient(SculptNetHarness.FeetIn(10, 5), team: 1, up: SculptNetHarness.Lag(50), down: SculptNetHarness.Lag(50));
            var b = h.AddClient(SculptNetHarness.FeetIn(10, 12), team: 2, seed: 2);
            h.Server.SpawnItem(Item.Grenade, 2, SculptNetHarness.FeetIn(10, 6) + new Vec3d(0, 0.5, 0));
            var shots = new List<CombatEvent>();
            int swapAt = -1, keyAt = -1;
            a.Script = (p, t) =>
            {
                SculptTickInput i = Mash(t);
                if (h.Server.Match.Phase == MatchPhase.Countdown && keyAt < 0) keyAt = t + 120;
                if (t == keyAt) i.Hand.WeaponKey = 3; // the AR
                if (keyAt > 0 && t == keyAt + 60) { i.Hand.Swap = true; i.Hand.SwapA = 0; i.Hand.SwapB = 1; swapAt = t; }
                return i;
            };
            h.RunSeconds(0.5); // the countdown starts (two players) and its respawn reaches the client
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            Vec3d pad = a.ServerSide.State.Position;
            int corrections = a.Client.Corrections;
            Bag bag0 = a.ServerSide.State.Hand.Bag;
            int items0 = h.Server.Items.Count;
            for (int k = 0; k < 300; k++)
            {
                h.RunMs(SculptNetHarness.TickMs);
                foreach (CombatEvent e in h.Server.CombatEvents) if (e.Attacker == a.ServerSide.Id) shots.Add(e);
                if (h.Server.Match.Phase != MatchPhase.Countdown) break;
                MotorState s = a.ServerSide.State;
                Assert.That(s.Position.X, Is.EqualTo(pad.X), "no walking");
                Assert.That(s.Position.Z, Is.EqualTo(pad.Z), "no walking");
                Assert.That(s.Position.Y, Is.EqualTo(pad.Y).Within(1e-9), "no jumping");
                Assert.That(s.Velocity.HorizontalLength, Is.Zero);
                Assert.That(s.Crouched, Is.False, "no crouch or slide");
                Assert.That(s.Mode, Is.EqualTo(MoveMode.Ground));
            }
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown), "still counting down");
            Assert.That(shots, Is.Empty, "no shots, punches or throws");
            Assert.That(h.Server.Items.Count, Is.EqualTo(items0), "nothing picked up or dropped");
            Assert.That(a.Client.Corrections, Is.EqualTo(corrections), "prediction exact through the countdown");
            Assert.That(a.ServerSide.LastCmd.Move.Yaw, Is.Not.EqualTo(0), "looking reaches the server");
            Assert.That(swapAt, Is.GreaterThan(0));
            Bag bag = a.ServerSide.State.Hand.Bag;
            Assert.That(bag[0], Is.EqualTo(bag0[1]), "swapped in the inventory screen");
            Assert.That(bag[1], Is.EqualTo(bag0[0]));
            Assert.That(bag[a.ServerSide.State.Hand.Slot], Is.EqualTo(bag0[2]), "the slot key drew the AR (the hand follows it)");

            // The countdown ends: you move at once, and the edge is predicted exactly too.
            int c2 = a.Client.Corrections;
            h.RunSeconds(2.2);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That((a.ServerSide.State.Position - pad).HorizontalLength, Is.GreaterThan(1), "moving once it is GO");
            Assert.That(a.Client.Corrections, Is.EqualTo(c2), "no correction where the countdown ends");
        }
    }
}
