using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Randomised sculpt matches with fixed seeds. Each seed is a reproducible scenario: if one fails,
    /// the message names the seed and tick, and re-running that single case replays it exactly.
    /// </summary>
    [Category("Fuzz")]
    public class SculptFuzzTests
    {
        private const int Ticks = 900; // 15 s of play per seed

        /// <summary>
        /// 8 seeds per test by default so the whole suite stays quick. For a deep soak run set
        /// SCULPT_FUZZ_SEEDS (e.g. 500) before dotnet test.
        /// </summary>
        private static IEnumerable<ulong> Seeds()
        {
            int n = int.TryParse(System.Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 8;
            for (ulong s = 1; s <= (ulong)n; s++) yield return s * 7919;
        }

        [TestCaseSource(nameof(Seeds))]
        public void StandingPlayers_InvariantsHoldEveryTick(ulong seed)
        {
            var f = new SculptFuzzScenario(seed);
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        /// <summary>Same, but players are often crouched (1.1 m box), which reaches into ramp air pockets.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void CrouchingPlayers_InvariantsHoldEveryTick(ulong seed)
        {
            var f = new SculptFuzzScenario(seed + 1, crouchChance: 0.5);
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        /// <summary>A crowded arena: many players, frequent sculpts, so shoves, carries and lock fights happen a lot.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void CrowdedArena_InvariantsHoldEveryTick(ulong seed)
        {
            var f = new SculptFuzzScenario(seed + 2, crouchChance: 0.25, playerCount: 10) { ActionChance = 0.5, TeleportChance = 0.08 };
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        /// <summary>
        /// Players really move (bots through PlayerMovement) while everyone sculpts: sliding into shoves,
        /// mantling onto rising blocks, crouching under falling ones. Sculpt and movement invariants every tick.
        /// </summary>
        [TestCaseSource(nameof(Seeds))]
        public void MovingPlayers_SculptAndMovementInvariantsHold(ulong seed)
        {
            var f = new SculptFuzzScenario(seed + 4, playerCount: 6, driveMovement: true) { ActionChance = 0.35 };
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        /// <summary>A brawl: moving players, infinite bar, and sculpts aimed at the ground around each other.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void MovingPlayers_Brawl_InvariantsHold(ulong seed)
        {
            var f = new SculptFuzzScenario(seed + 6, playerCount: 8, driveMovement: true) { ActionChance = 0.5, AimAtPlayersChance = 0.7, InfiniteBar = true };
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        /// <summary>Static players in a brawl (crouched or not), every sculpt aimed around someone.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void StaticBrawl_InvariantsHold(ulong seed)
        {
            var f = new SculptFuzzScenario(seed + 7, crouchChance: 0.4, playerCount: 8) { ActionChance = 0.5, AimAtPlayersChance = 0.8, InfiniteBar = true, TeleportChance = 0.05 };
            f.Run(Ticks);
            f.SettleAndAssertSupport();
            TestContext.Out.WriteLine(f.Summary());
        }

        [TestCaseSource(nameof(Seeds))]
        public void MovingPlayers_SameSeedReplaysIdentically(ulong seed)
        {
            var a = new SculptFuzzScenario(seed + 5, playerCount: 4, driveMovement: true) { ActionChance = 0.35 };
            var b = new SculptFuzzScenario(seed + 5, playerCount: 4, driveMovement: true) { ActionChance = 0.35 };
            a.Run(600);
            b.Run(600);
            Assert.That(b.Trace, Is.EqualTo(a.Trace), "movement + sculpt trace diverged");
        }

        [TestCaseSource(nameof(Seeds))]
        public void SameSeedReplaysIdentically(ulong seed)
        {
            var a = new SculptFuzzScenario(seed + 3, crouchChance: 0.25, playerCount: 6) { ActionChance = 0.4 };
            var b = new SculptFuzzScenario(seed + 3, crouchChance: 0.25, playerCount: 6) { ActionChance = 0.4 };
            a.Run(600);
            b.Run(600);
            Assert.That(b.Trace, Is.EqualTo(a.Trace), "event/position/bar trace diverged");
            Assert.That(Inspect.HashGrid(b.Grid, b.Grid.MinY, b.Grid.MaxYExclusive - 1),
                Is.EqualTo(Inspect.HashGrid(a.Grid, a.Grid.MinY, a.Grid.MaxYExclusive - 1)), "grid diverged");
        }

        /// <summary>The fuzzer must actually reach every action type, shoves, falls and enemy reinforcement, or it proves nothing.</summary>
        [Test]
        public void FuzzerCoversTheInterestingCases()
        {
            var totals = new Dictionary<string, int>();
            void Add(string k, int v) => totals[k] = (totals.TryGetValue(k, out int o) ? o : 0) + v;

            foreach (ulong seed in Seeds())
            {
                var f = new SculptFuzzScenario(seed + 2, crouchChance: 0.25, playerCount: 10) { ActionChance = 0.5, TeleportChance = 0.08 };
                f.Run(Ticks);
                foreach (var kv in f.Completed) Add(kv.Key.ToString(), kv.Value);
                foreach (var kv in f.Rejected) Add("rej:" + kv.Key, kv.Value);
                Add("shoves", f.Shoves);
                Add("falls", f.Falls);
                Add("cancelled", f.Cancelled);
                Add("enemyReinforced", f.EnemyReinforcedBegun);
            }

            foreach (var kv in totals) TestContext.Out.WriteLine($"{kv.Key}: {kv.Value}");
            foreach (string k in new[] { "Move", "Raise", "Sink", "Shape", "shoves", "falls", "cancelled", "enemyReinforced",
                         "rej:Locked", "rej:DestinationBlocked", "rej:OutOfRange", "rej:NotSculptable", "rej:PlayerBlocked" })
                Assert.That(totals.TryGetValue(k, out int v) ? v : 0, Is.GreaterThan(0), "fuzzer never produced " + k);
        }
    }
}
