using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Seeded movement soak: bots run, sprint, slide, jump, vault and mantle across random terrain with
    /// ramps, steps, pits and walls. Checks per-tick movement invariants, and that prediction-style
    /// rewind and replay reproduces the exact same trajectory.
    /// </summary>
    [Category("Fuzz")]
    public class MovementFuzzTests
    {
        private const double B = VoxelGrid.BlockSize;
        private const int Ticks = 1800; // 30 s per seed

        private static IEnumerable<ulong> Seeds()
        {
            int n = int.TryParse(System.Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 8;
            for (ulong s = 1; s <= (ulong)n; s++) yield return s * 6151;
        }

        private static VoxelGrid Arena(Rng r)
        {
            var g = new VoxelGrid(2, 2, 2, -16);
            g.FillBaseGround();
            TerrainGen.Build(g, r, 2, 29, rampChance: 0.3);
            return g;
        }

        private static MotorState Spawn(VoxelGrid g, MovementConfig c, Rng r)
        {
            for (int i = 0; i < 500; i++)
            {
                var p = new PlayerBody(0, 1, new Vec3d(r.Range(3 * B, 29 * B), 0, r.Range(3 * B, 29 * B)));
                if (!Inspect.DropToGround(g, p)) continue;
                var s = MotorState.At(p.Position);
                if (!MotorChecker.Inside(g, c, s)) return s;
            }
            throw new System.InvalidOperationException("no spawn");
        }

        [TestCaseSource(nameof(Seeds))]
        public void BotsNeverBreakMovementInvariants(ulong seed)
        {
            var r = new Rng(seed);
            VoxelGrid g = Arena(r);
            var c = new MovementConfig();
            var events = new List<MoveEvent>();
            int jumps = 0, slides = 0, vaults = 0, mantles = 0, slideLands = 0, launches = 0;

            for (int bot = 0; bot < 3; bot++)
            {
                MotorState s = Spawn(g, c, r);
                var input = new InputBot(r);
                var check = new MotorChecker(c);
                for (int t = 0; t < Ticks; t++)
                {
                    MoveInput i = input.Next(t);
                    MotorState before = s;
                    events.Clear();
                    PlayerMovement.Step(g, c, ref s, i, events);
                    check.Check(g, before, s, events, $"seed {seed} bot {bot} tick {s.Tick}", worldStatic: true);
                    if (s.Position.Y < -6) Assert.Fail($"seed {seed} bot {bot}: fell out of the world at {s.Position}");
                }
                jumps += check.Jumps; slides += check.Slides; vaults += check.Vaults; mantles += check.Mantles;
                slideLands += check.SlideLands; launches += check.RampLaunches;
            }
            TestContext.Out.WriteLine($"seed {seed}: jumps {jumps}, slides {slides}, slide-lands {slideLands}, vaults {vaults}, mantles {mantles}, ramp launches {launches}");
        }

        /// <summary>Client prediction relies on this: restore an old state, replay the same inputs, land on exactly the same states.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void RewindAndReplayIsBitExact(ulong seed)
        {
            var r = new Rng(seed + 11);
            VoxelGrid g = Arena(r);
            var c = new MovementConfig();
            MotorState s = Spawn(g, c, r);
            var bot = new InputBot(r);

            var inputs = new List<MoveInput>();
            var states = new List<MotorState> { s };
            for (int t = 0; t < 1200; t++)
            {
                MoveInput i = bot.Next(t);
                inputs.Add(i);
                PlayerMovement.Step(g, c, ref s, i);
                states.Add(s);
            }

            for (int k = 0; k < 20; k++)
            {
                int from = r.Int(0, 1100);
                MotorState replay = states[from];
                for (int t = from; t < from + 100; t++)
                {
                    PlayerMovement.Step(g, c, ref replay, inputs[t]);
                    Assert.That(MotorChecker.Hash(replay), Is.EqualTo(MotorChecker.Hash(states[t + 1])), $"seed {seed}: replay from {from} diverged at tick {t + 1}");
                }
            }
        }

        /// <summary>The fuzzer must reach every bit of movement tech, or it proves little.</summary>
        [Test]
        public void BotsExerciseAllMovementTech()
        {
            int jumps = 0, slides = 0, vaults = 0, mantles = 0, slideLands = 0, launches = 0;
            foreach (ulong seed in Seeds())
            {
                var r = new Rng(seed);
                VoxelGrid g = Arena(r);
                var c = new MovementConfig();
                for (int bot = 0; bot < 3; bot++)
                {
                    MotorState s = Spawn(g, c, r);
                    var input = new InputBot(r);
                    var check = new MotorChecker(c);
                    var events = new List<MoveEvent>();
                    for (int t = 0; t < Ticks; t++)
                    {
                        MotorState before = s;
                        events.Clear();
                        PlayerMovement.Step(g, c, ref s, input.Next(t), events);
                        check.Check(g, before, s, events, "", true);
                    }
                    jumps += check.Jumps; slides += check.Slides; vaults += check.Vaults; mantles += check.Mantles;
                    slideLands += check.SlideLands; launches += check.RampLaunches;
                }
            }
            TestContext.Out.WriteLine($"jumps {jumps}, slides {slides}, slide-lands {slideLands}, vaults {vaults}, mantles {mantles}, ramp launches {launches}");
            Assert.That(jumps, Is.GreaterThan(0));
            Assert.That(slides, Is.GreaterThan(0));
            Assert.That(slideLands, Is.GreaterThan(0));
            // The automatic vault is off since vault and mantle merged (Ben 2026-11-25); one-tile ledges are mantled.
            Assert.That(vaults, Is.EqualTo(0));
            Assert.That(mantles, Is.GreaterThan(0));
            Assert.That(launches, Is.GreaterThan(0));
        }
    }
}
