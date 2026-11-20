using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Exact-number checks of the sculpt economy and timing, every combination, at tick precision.
    /// Layout: sculptor (id 1, team 1) in cell (5,0,5), target block on the ground at (7,0,5).
    /// </summary>
    public class RulesTableTests
    {
        private static readonly Int3 Target = new Int3(7, 0, 5);

        private static SculptAction ActionFor(SculptActionType t, bool reinforce)
        {
            Vec3d side = TestWorld.FaceCenter(Target, Dir.West);
            switch (t)
            {
                case SculptActionType.Move: return SculptAction.Move(Target, Dir.East, side, reinforce);
                case SculptActionType.Raise: return SculptAction.Raise(Target, side, reinforce);
                case SculptActionType.Sink: return SculptAction.Sink(Target, side, reinforce);
                default: return SculptAction.Shape(Target, Dir.East, side, reinforce);
            }
        }

        // type, reinforce, enemy reinforced, expected cost (basis points), expected windup ticks
        [TestCase(SculptActionType.Move, false, false, 500, 12)]
        [TestCase(SculptActionType.Move, true, false, 2000, 21)]
        [TestCase(SculptActionType.Move, false, true, 1000, 24)]
        [TestCase(SculptActionType.Move, true, true, 2500, 24)]
        [TestCase(SculptActionType.Raise, false, false, 1000, 12)]
        [TestCase(SculptActionType.Raise, true, false, 2500, 21)]
        [TestCase(SculptActionType.Raise, false, true, 2000, 24)]
        [TestCase(SculptActionType.Raise, true, true, 3500, 24)]
        [TestCase(SculptActionType.Sink, false, false, 700, 12)]
        [TestCase(SculptActionType.Sink, true, false, 2200, 21)]
        [TestCase(SculptActionType.Sink, false, true, 1400, 24)]
        [TestCase(SculptActionType.Sink, true, true, 2900, 24)]
        [TestCase(SculptActionType.Shape, false, false, 500, 12)]
        [TestCase(SculptActionType.Shape, true, false, 2000, 21)]
        [TestCase(SculptActionType.Shape, false, true, 1000, 24)]
        [TestCase(SculptActionType.Shape, true, true, 2500, 24)]
        public void CostAndWindupForEveryCombination(SculptActionType type, bool reinforce, bool enemyReinforced, int cost, int windup)
        {
            var w = new TestWorld();
            var b = Block.Solid;
            if (enemyReinforced) { b.ReinforceTeam = 2; b.ReinforceUntilTick = 10000; }
            w.Grid.Set(Target, b);
            var p = w.AddPlayer(1, 1, 5, 0, 5);

            SculptCheck c = w.Sculpt.TryBegin(p, ActionFor(type, reinforce));
            Assert.That(c.Result, Is.EqualTo(SculptResult.Valid));
            Assert.That(c.Cost, Is.EqualTo(cost), "cost");
            Assert.That(c.WindupTicks, Is.EqualTo(windup), "windup");
            Assert.That(c.EnemyReinforced, Is.EqualTo(enemyReinforced));
            Assert.That(w.Sculpt.BarOf(1).Value, Is.EqualTo(SculptConfig.BarMax - cost), "charged on confirm");

            w.Step(windup - 1);
            Assert.That(w.Sculpt.Events.Any(e => e.Type == SculptEventType.Completed), Is.False, "not one tick early");
            Assert.That(w.Sculpt.IsLocked(c.Source, out _), Is.True, "locked through the whole windup");
            w.Step(1);
            Assert.That(w.Sculpt.Events.Count(e => e.Type == SculptEventType.Completed && e.Tick == windup), Is.EqualTo(1), "completes exactly on time");
            Assert.That(w.Sculpt.IsLocked(c.Source, out _), Is.False);
            Assert.That(w.Sculpt.IsLocked(c.Destination, out _), Is.False);
        }

        [Test]
        public void ReinforcementExpiresOnTheExactTick()
        {
            var w = new TestWorld();
            var b = Block.Solid;
            b.ReinforceTeam = 2;
            b.ReinforceUntilTick = 50;
            w.Grid.Set(Target, b);
            var p = w.AddPlayer(1, 1, 5, 0, 5);
            var a = ActionFor(SculptActionType.Move, false);

            w.Step(49);
            Assert.That(w.Sculpt.Evaluate(p, a).EnemyReinforced, Is.True, "tick 49: still reinforced");
            Assert.That(w.Sculpt.Evaluate(p, a).Cost, Is.EqualTo(1000));
            w.Step(1);
            Assert.That(w.Sculpt.Evaluate(p, a).EnemyReinforced, Is.False, "tick 50: normal again");
            Assert.That(w.Sculpt.Evaluate(p, a).Cost, Is.EqualTo(500));
            Assert.That(w.Sculpt.Evaluate(p, a).WindupTicks, Is.EqualTo(12));
        }

        [Test]
        public void ReinforcedBlockLastsExactlyTwentySeconds()
        {
            var w = new TestWorld();
            w.Grid.Set(Target, Block.Solid);
            var p = w.AddPlayer(1, 1, 5, 0, 5);
            w.Sculpt.TryBegin(p, ActionFor(SculptActionType.Shape, true));
            w.Step(21);
            Block shaped = w.Grid.Get(Target);
            Assert.That(shaped.IsReinforced(21), Is.True);
            Assert.That(shaped.IsReinforced(21 + 1199), Is.True);
            Assert.That(shaped.IsReinforced(21 + 1200), Is.False, "20 s = 1200 ticks after completion");
        }

        [Test]
        public void TwoPlayersSameTickSameBlock_FirstWinsSecondIsLockedThenFree()
        {
            var w = new TestWorld();
            w.Grid.Set(Target, Block.Solid);
            var p1 = w.AddPlayer(1, 1, 5, 0, 5);
            var p2 = w.AddPlayer(2, 2, 5, 0, 6);
            var pull = SculptAction.Move(Target, Dir.West, TestWorld.FaceCenter(Target, Dir.West));

            Assert.That(w.Sculpt.TryBegin(p1, ActionFor(SculptActionType.Move, false)).IsValid, Is.True);
            Assert.That(w.Sculpt.TryBegin(p2, pull).Result, Is.EqualTo(SculptResult.Locked));
            Assert.That(w.Sculpt.BarOf(2).Value, Is.EqualTo(SculptConfig.BarMax), "loser pays nothing");
            w.Step(12);

            // The block is now at (8,0,5); p2 may target it at once.
            var moved = new Int3(8, 0, 5);
            var pullMoved = SculptAction.Move(moved, Dir.West, TestWorld.FaceCenter(moved, Dir.West));
            Assert.That(w.Sculpt.Evaluate(p2, pullMoved).Result, Is.EqualTo(SculptResult.Valid));
        }

        [Test]
        public void RemovingSculptorMidWindupChangesNothing()
        {
            var w = new TestWorld();
            w.Grid.Set(Target, Block.Solid);
            var p = w.AddPlayer(1, 1, 5, 0, 5);
            w.Sculpt.TryBegin(p, ActionFor(SculptActionType.Move, false));
            w.Step(5);
            w.Sculpt.RemovePlayer(1);
            w.Sculpt.ClearEvents();
            w.Step(20);
            Assert.That(w.At(7, 0, 5).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(w.At(8, 0, 5).IsEmpty, Is.True);
            Assert.That(w.Sculpt.Events.Any(e => e.Type == SculptEventType.Completed), Is.False);
        }

        [Test]
        public void EnemyCannotSculptDuringYourWindupEvenFromTheOtherSide()
        {
            var w = new TestWorld();
            w.Grid.Set(Target, Block.Solid);
            var p1 = w.AddPlayer(1, 1, 5, 0, 5);
            var p2 = w.AddPlayer(2, 2, 9, 0, 5);
            Assert.That(w.Sculpt.TryBegin(p1, ActionFor(SculptActionType.Move, false)).IsValid, Is.True);

            var e = TestWorld.FaceCenter(Target, Dir.East);
            Assert.That(w.Sculpt.Evaluate(p2, SculptAction.Move(Target, Dir.West, e)).Result, Is.EqualTo(SculptResult.Locked));
            Assert.That(w.Sculpt.Evaluate(p2, SculptAction.Sink(Target, e)).Result, Is.EqualTo(SculptResult.Locked));
            Assert.That(w.Sculpt.Evaluate(p2, SculptAction.Raise(Target, e)).Result, Is.EqualTo(SculptResult.Locked));
            Assert.That(w.Sculpt.Evaluate(p2, SculptAction.Shape(Target, Dir.North, e)).Result, Is.EqualTo(SculptResult.Locked));
        }
    }
}
