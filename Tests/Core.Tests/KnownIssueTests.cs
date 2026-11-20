using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Bugs found by the hardening suite, written as the behaviour the spec asks for. While a bug is open its test
    /// is marked Ignore (the reason says why) to keep the suite green. When a fix lands, delete the Ignore line.
    /// ISSUE-1..4 were terrain v1 sculpt bugs (fixed in M2, then superseded by terrain v2 pieces).
    /// </summary>
    [Category("KnownIssue")]
    public class KnownIssueTests
    {
        /// <summary>
        /// ISSUE-5 (join feel, fixed in M3.5): on joining at about 90 to 120 ms one way (180 to 240 ms ping) the first
        /// lead guess was 4 to 7 ticks short, under the snap threshold, so the clock slewed for 2.5 to 4 s with every
        /// command arriving late. Now the client snaps whenever the server reports late inputs.
        /// Expectation: within 1.5 s of joining, inputs arrive in time.
        /// </summary>
        [TestCase(90.0)]
        [TestCase(100.0)]
        [TestCase(110.0)]
        [TestCase(120.0)]
        public void JoiningAtMidLatency_InputsArriveInTimeWithinOneAndAHalfSeconds(double oneWayMs)
        {
            var h = Net.NetHarness.Flat();
            var p = h.AddClient(Net.NetHarness.Lag(oneWayMs), Net.NetHarness.Lag(oneWayMs));
            p.Script = Net.Scripts.StraightNorth;
            h.RunSeconds(1.5);
            int missed = p.ServerSide.MissedInputs;
            h.RunSeconds(1.0);
            Assert.That(p.ServerSide.MissedInputs - missed, Is.Zero, "inputs still late 1.5 s after joining");
        }

        /// <summary>
        /// ISSUE-6 (stale shoves on throw): a throw never plans shoves, but its resolution applies whatever the last
        /// shove plan left in the shared scratch list. Any earlier Evaluate (another player's ghost, a rejected confirm,
        /// a different windup) that planned to lift or shove someone gets replayed when the throw lands: that player
        /// teleports to the old planned spot (can be in the air, pulled back, or a fresh position that is now inside rock).
        /// Found by the moving-player piece fuzz (seeds 33, 314220, 523678: players "shoved" down 0.09 to 0.43 m by throws).
        /// Expectation: a throw moves nobody except via its rider kick.
        /// </summary>
        [Test]
        public void ThrowNeverReplaysSomeoneElsesShovePlan()
        {
            var w = new Pieces.SculptWorld();
            w.Tiles.Set(7, 0, 6, Tile.Solid);                 // the cover tile that gets thrown
            var thrower = w.AddPlayer(1, 1, 5, 0, 6);
            var bystander = w.AddPlayer(2, 2, 10, 0, 12);      // far from the throw
            var ghoster = w.AddPlayer(3, 2, 8, 0, 12);        // only previews a raise under the bystander
            Vec3d start = bystander.Position;

            SculptCheck t = w.Sculpt.TryBegin(thrower, w.Throw(thrower, new Vec3d(8.4, 0.6, 7.8), new Vec3d(0, 0.3, -1)));
            Assert.That(t.Result, Is.EqualTo(SculptResult.Valid), "setup: the throw starts");

            // The ghost: a raise of the ground the bystander stands on would lift them a level. Evaluate only, never confirmed.
            SculptCheck ghost = w.Sculpt.Evaluate(ghoster, w.Raise(ghoster, Pieces.SculptWorld.FeetIn(10, 0, 12)));
            Assert.That(ghost.Result, Is.EqualTo(SculptResult.Valid), "setup: the previewed raise would carry the bystander");

            w.Step(t.WindupTicks + 1);
            Assert.That(w.Sculpt.Thrown.Count + (w.Sculpt.Events.Exists(e => e.Type == SculptEventType.ThrowImpact) ? 1 : 0), Is.GreaterThan(0), "setup: the piece was thrown");
            Assert.That(w.Sculpt.Events.Exists(e => e.Type == SculptEventType.PlayerShoved && e.PlayerId == bystander.Id), Is.False, "no shove event for a bystander");
            Assert.That(bystander.Position, Is.EqualTo(start), "the bystander did not move");
        }

        /// <summary>
        /// ISSUE-7 (box test not monotone at contact): a box resting on the surface (feet at the contact epsilon, where
        /// movement's bisection leaves them) can test as overlapping while a taller box with the same feet, which
        /// contains it, does not. Cause: the separating-axis test compares centre +- half extent against the polytope
        /// with an epsilon, so at the epsilon the answer flips on 1e-14 rounding that depends on the box height.
        /// Effect in play: crouching while standing still can register as "inside rock" (seen by the moving-player
        /// piece fuzz, seeds 942594, 1047323, 1885155 and more), which makes Depenetrate pop the player up 5 cm and makes
        /// sculpt and movement checks treat a resting player as blocked.
        /// Expectation: if a box overlaps, every box containing it overlaps.
        /// </summary>
        [Test]
        public void ShorterBoxAtTheSameFeetNeverOverlapsWhenTheTallerOneDoesNot()
        {
            SubGrid g = MapGen.Generate(1047323, out _, 32);
            var feet = new Vec3d(23.726735578766785, -0.12673699298034644, 20.798992102116515); // where movement left a player
            // Scan the contact zone around the spot (the exact old resting height depended on the old rounding).
            int clearTall = 0;
            for (int k = -200; k <= 200; k++)
            {
                double y = feet.Y + k * 1e-9;
                Aabb Box(double h) => new Aabb(new Vec3d(feet.X - 0.35, y, feet.Z - 0.35), new Vec3d(feet.X + 0.35, y + h, feet.Z + 0.35));
                if (g.BoxOverlapsSolid(Box(1.8))) continue;
                clearTall++;
                Assert.That(g.BoxOverlapsSolid(Box(1.1)), Is.False, $"crouching must be clear where standing is (k {k})");
            }
            Assert.That(clearTall, Is.GreaterThan(0), "setup: the scan reaches clear heights");
        }

        /// <summary>ISSUE-7, as a property over random resting spots on generated arenas.</summary>
        [Test]
        public void BoxOverlapIsMonotoneForRestingBoxes()
        {
            int violations = 0, samples = 0;
            for (int seed = 1; seed <= 4; seed++)
            {
                SubGrid g = MapGen.Generate(seed * 7919, out _, 32);
                var r = new Fuzz.Rng((ulong)seed);
                double size = g.SizeX * SubGrid.SubSize;
                for (int i = 0; i < 400; i++)
                {
                    var p = new PlayerBody(1, 1, new Vec3d(r.Range(2, size - 2), 0, r.Range(2, size - 2)));
                    if (!Fuzz.Inspect.DropToGround(g, p, -4, 9)) continue;
                    samples++;
                    Vec3d f = p.Position;
                    bool tall = g.BoxOverlapsSolid(new Aabb(new Vec3d(f.X - 0.35, f.Y, f.Z - 0.35), new Vec3d(f.X + 0.35, f.Y + 1.8, f.Z + 0.35)));
                    bool shortBox = g.BoxOverlapsSolid(new Aabb(new Vec3d(f.X - 0.35, f.Y, f.Z - 0.35), new Vec3d(f.X + 0.35, f.Y + 1.1, f.Z + 0.35)));
                    if (shortBox && !tall) violations++;
                }
            }
            Assert.That(samples, Is.GreaterThan(1000));
            Assert.That(violations, Is.Zero, $"{violations} of {samples} resting spots: crouch box overlaps, standing box does not");
        }

        /// <summary>
        /// ISSUE-8 (own sculpt shoves not predicted): when your own predicted sculpt lands in your view, the client moves
        /// the rock but not you. The server shoves or lifts you by the sculpt rule; your prediction instead relies on
        /// movement's Depenetrate, which only ever goes straight up (at most 1.3 m). So pulling a wall into yourself pops
        /// you on top of it in your view, then the server's correction drags you back a round trip later, and a lift that
        /// needs more than 1.3 m (raising a piece you stand on at a chamfer) leaves you inside rock until the correction
        /// (seen in the networked sculpt fuzz, seed 47531: 9 ticks inside, 1.5 m deep). The same happens for one tick when
        /// someone else's sculpt reaches you before your prediction is corrected.
        /// Expectation: your own sculpt moves you in your view exactly as the server does (no pop, no correction).
        /// </summary>
        [Test]

        public void PullingAWallIntoYourselfShovesYouBackInYourViewWithoutACorrection()
        {
            var h = Net.SculptNetHarness.Flat();
            // Box max x = 8.35: 5 cm from the wall's west face at x 8.4.
            var p = h.AddClient(Net.SculptNetHarness.FeetIn(8.0 / Net.SculptNetHarness.B - 0.5, 5.5), 1, Net.SculptNetHarness.Lag(50), Net.SculptNetHarness.Lag(50));
            int confirmAt = -1, corrBefore = -1;
            double maxY = double.NegativeInfinity;
            p.Script = (peer, t) =>
            {
                var i = new Net.SculptTickInput { Move = new MoveInput() };
                if (peer.Sculpt.LocalBody == null) return i;
                if (confirmAt >= 0 && t > confirmAt) maxY = System.Math.Max(maxY, peer.Client.Predicted.Position.Y);
                if (confirmAt < 0 && peer.Client.Predicted.Grounded && peer.Client.Predicted.Hand.Mode == HandMode.Weapon && t > 1150) i.Hand.SculptToggle = true;
                if (confirmAt < 0 && peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    i.Confirm = true;
                    i.Action = Net.SculptNetHarness.PushWall(peer, Dir.West); // pull it toward you
                    confirmAt = t;
                    corrBefore = peer.Client.Corrections;
                }
                return i;
            };
            h.RunSeconds(5);
            Assert.That(confirmAt, Is.GreaterThan(0), "setup: confirmed");
            Assert.That(h.ServerSculpt.Pieces.Count, Is.GreaterThan(0), "setup: the server pulled the wall");
            Assert.That(p.ServerSide.Body.Position.X, Is.LessThan(7.65 - 0.5), "setup: the server shoved the player back");
            Assert.That(maxY, Is.LessThan(0.3), "your view never pops up onto the wall");
            Assert.That(p.Client.Corrections - corrBefore, Is.Zero, "no correction: you predicted the shove");
        }

        /// <summary>
        /// ISSUE-9 (phantom sculpt): if the server never gets a sculpt confirm (every packet carrying it lost, or it arrives
        /// later than the 8-tick carry window), it never answers: no Began, no Rejected. The client's prediction of that
        /// sculpt stays in its world until SpecTimeoutTicks (240 ticks = 4 s) after it landed. For those 4 s you see rock
        /// that is not there for anyone else, and if the phantom raise is under you, your prediction sits inside it
        /// (networked sculpt fuzz, seed 134640: 78+ ticks inside a phantom raise, 1.5 m deep, 120 corrections).
        /// Expectation: a confirm the server never saw is rolled back about a round trip after it should have begun.
        /// </summary>
        [Test]

        public void ALostConfirmIsRolledBackWithinARoundTrip()
        {
            var h = Net.SculptNetHarness.Flat();
            var p = h.AddClient(Net.SculptNetHarness.FeetIn(3, 10), 1, Net.SculptNetHarness.Lag(50), Net.SculptNetHarness.Lag(50));
            int confirmAt = -1;
            p.Script = (peer, t) =>
            {
                var i = new Net.SculptTickInput { Move = new MoveInput() };
                if (peer.Sculpt.LocalBody == null) return i;
                if (confirmAt < 0 && peer.Client.Predicted.Grounded && peer.Client.Predicted.Hand.Mode == HandMode.Weapon && t > 1150) i.Hand.SculptToggle = true;
                if (confirmAt < 0 && peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    i.Confirm = true;
                    i.Action = Net.SculptNetHarness.RaiseFloor(peer, 4.5 * Net.SculptNetHarness.B, 10.5 * Net.SculptNetHarness.B);
                    confirmAt = t;
                    peer.Up.Conditions.LossPercent = 100; // every packet that carries the confirm is lost
                }
                return i;
            };
            for (int k = 0; k < 400 && confirmAt < 0; k++) h.RunMs(10);
            Assert.That(confirmAt, Is.GreaterThan(0), "setup: confirmed");
            h.RunMs(400);                       // past the windup, and longer than the 8-command redundancy window
            p.Up.Conditions.LossPercent = 0;
            Assert.That(p.SculptEvents.Exists(e => e.Type == Sculpt.Core.Net.ClientSculptEventType.LocalLanded), Is.True, "setup: the prediction landed in the client view; events: " + string.Join(",", p.SculptEvents.ConvertAll(e => e.Type + ":" + e.Result)));
            Assert.That(h.ServerSculpt.Pieces.Count, Is.Zero, "setup: the server never saw it");

            h.RunMs(600);                       // a round trip and change after the link recovers
            Assert.That(p.Sculpt.Local.Count, Is.Zero, "the unanswered prediction is dropped");
            Assert.That(p.Grid.Hash(), Is.EqualTo(h.ServerGrid.Hash()), "and the phantom rock is gone");
        }

        /// <summary>
        /// ISSUE-10 (client crash): a sculpt confirm whose target sub-cell does not fit the wire's 16-bit cell throws
        /// OverflowException inside NetClient.PredictTick (ByteWriter.Cell uses checked casts), killing the client's tick.
        /// It happens in play: a dead player's body is parked at y = -10000 but their hand stays in sculpt mode, so aiming
        /// from that eye targets sub-cell y = -33328 (weapons fuzz, seeds 6162 and 24615).
        /// Expectation: no input can make PredictTick throw; an impossible target is simply refused (and a dead player
        /// can't confirm sculpts at all).
        /// </summary>
        [Test]
        [Ignore("ISSUE-10: a far-out-of-range sculpt target (e.g. aimed while dead) throws OverflowException in PredictTick")]
        public void AnOutOfRangeSculptTargetNeverCrashesTheClient()
        {
            var h = Net.SculptNetHarness.Flat();
            var p = h.AddClient(Net.SculptNetHarness.FeetIn(3, 10), 1, Net.SculptNetHarness.Lag(30), Net.SculptNetHarness.Lag(30));
            bool sent = false;
            p.Script = (peer, t) =>
            {
                var i = new Net.SculptTickInput();
                if (peer.Sculpt.LocalBody == null) return i;
                if (!sent && peer.Client.Predicted.Grounded && peer.Client.Predicted.Hand.Mode == HandMode.Weapon && t > 1150) i.Hand.SculptToggle = true;
                if (!sent && peer.Client.Predicted.Hand.CanConfirmSculpt)
                {
                    i.Confirm = true;
                    i.Action = SculptAction.Raise(new Int3(-1, -33328, -1), new Vec3d(0, -9998.4, 0)); // what aiming from the parked body gives
                    sent = true;
                }
                return i;
            };
            Assert.DoesNotThrow(() => h.RunSeconds(3));
            Assert.That(sent, Is.True, "setup: the confirm was made");
        }

        /// <summary>
        /// ISSUE-11 (draw-time bypass, design call): tapping sculpt mode then a weapon key swaps faster than the weapon key
        /// alone. From the AR, pressing 1 fires the pump after its 24-tick draw (0.4 s); pressing Q then 1 next tick fires it
        /// after 13 ticks, because a weapon key in sculpt mode leaves straight to that gun for its 12-tick exit time instead
        /// of its draw. Every swap gets about twice as fast with a Q tap, so draw times stop mattering for anyone who learns it.
        /// Expectation (suggested): leaving sculpt mode to a gun other than the one you came from pays that gun's draw time;
        /// the cheaper exit time is only for going back to the gun you were holding.
        /// </summary>
        [Test]
        [Ignore("ISSUE-11: Q then a weapon key swaps about twice as fast as the weapon key (bypasses draw time)")]
        public void TappingSculptModeNeverMakesASwapFaster()
        {
            int direct = FirstPumpShotAfterSwapFromAr(viaSculpt: false);
            int viaQ = FirstPumpShotAfterSwapFromAr(viaSculpt: true);
            Assert.That(direct, Is.GreaterThan(0));
            Assert.That(viaQ, Is.GreaterThanOrEqualTo(direct), $"direct swap {direct} ticks, Q-tap swap {viaQ} ticks");
        }

        private static int FirstPumpShotAfterSwapFromAr(bool viaSculpt)
        {
            var mc = new MovementConfig();
            var nc = new Sculpt.Core.Net.NetConfig();
            var g = new SubGrid(4, 4, 4, -16);
            g.FillBaseGround();
            var spawn = new Vec3d(10, 0, 10);
            var s = MotorState.At(spawn);
            var shots = new System.Collections.Generic.List<WeaponShot>();
            int tick = 1000;
            void Step(MoveInput m, HandInput hand)
            {
                tick++;
                Sculpt.Core.Net.NetSim.Simulate(g, mc, nc, ref s, Sculpt.Core.Net.InputCmd.Quantize(new Sculpt.Core.Net.InputCmd { Tick = tick, Move = m, Hand = hand }), spawn, null, shots);
            }
            for (int i = 0; i < 30; i++) Step(default, default);
            Step(default, new HandInput { WeaponKey = (byte)(Weapon.Ar + 1) });
            for (int i = 0; i < 40; i++) Step(default, default);
            int start = tick + 1;
            if (viaSculpt) Step(default, new HandInput { SculptToggle = true });
            Step(default, new HandInput { WeaponKey = (byte)(Weapon.Pump + 1) });
            shots.Clear();
            for (int i = 0; i < 60; i++)
            {
                Step(new MoveInput { FireHeld = true, FirePressed = i == 0, WantsToShoot = true }, default);
                if (shots.Count > 0) return tick - start;
            }
            return -1;
        }
    }
}
