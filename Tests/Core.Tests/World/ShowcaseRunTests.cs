using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>The F6 movement showcase must finish the course with the real movement code.</summary>
    public class ShowcaseRunTests
    {
        public static bool Verbose;
        public static (ShowcaseRun run, MotorState s, List<MoveEvent> events, List<string> log) Play(MovementConfig mc = null)
        {
            mc ??= new MovementConfig();
            var ground = BreachMap.BuildGround();
            var brushes = BreachMap.BuildBrushes(out BreachMapInfo info);
            ITerrain t = BreachMap.Terrain(ground, brushes);
            var run = new ShowcaseRun(info.Course, mc);
            var s = MotorState.At(info.Course.Start);
            var events = new List<MoveEvent>();
            var log = new List<string>();
            ShowcaseRun.Phase last = run.Current;
            for (int k = 0; k < SimTime.TicksPerSecond * 70 && !run.Finished; k++)
            {
                MoveInput i = run.Next(s);
                int before = events.Count;
                PlayerMovement.Step(t, mc, ref s, i, events);
                string ev = string.Join(",", events.Skip(before).Select(e => e.Type + (e.Type == MoveEventType.SlideStarted ? $"({e.Value})" : "")));
                if (run.Current != last || ev.Length > 0 || (Verbose && run.Ticks % 6 == 0))
                    log.Add($"t{run.Ticks,4} {run.Current,-12} {s.Mode,-6} pos {s.Position} v {s.HorizontalSpeed:0.00} vy {s.Velocity.Y:0.0} {ev}");
                last = run.Current;
            }
            return (run, s, events, log);
        }

        [Test]
        public void ShowcaseFinishesTheCourse()
        {
            Verbose = Environment.GetEnvironmentVariable("SHOWCASE_VERBOSE") == "1";
            var (run, s, events, log) = Play();
            foreach (var l in log) TestContext.Out.WriteLine(l);
            TestContext.Out.WriteLine($"finish line at {run.FinishTicks / (double)SimTime.TicksPerSecond:0.00} s, top speed {run.TopSpeed:0.0}");
            Assert.That(run.Current, Is.EqualTo(ShowcaseRun.Phase.Done), run.FailReason);
            // Ben 2026-11-25: about 25 s, the fastest route, flashy, showing the core movement.
            Assert.That(run.FinishTicks / (double)SimTime.TicksPerSecond, Is.InRange(20.0, 30.0), "about 25 s to the finish line");
            Assert.That(run.TopSpeed, Is.GreaterThan(16), "built real speed on the stairs");
            Assert.That(events.Count(e => e.Type == MoveEventType.WallKick), Is.EqualTo(0), "no wall kicks (removed)");
            Assert.That(events.Count(e => e.Type == MoveEventType.MantleStarted), Is.GreaterThanOrEqualTo(2), "the ledge and the block");
            Assert.That(events.Count(e => e.Type == MoveEventType.MantleCancel), Is.GreaterThanOrEqualTo(1));
            Assert.That(events.Any(e => e.Type == MoveEventType.Braked), Is.True);
            Assert.That(events.Any(e => e.Type == MoveEventType.SlideCarve), Is.True);
            Assert.That(events.Count(e => e.Type == MoveEventType.SlideLandSuccess), Is.GreaterThanOrEqualTo(6));
            Assert.That(events.Count(e => e.Type == MoveEventType.SlideStarted && e.Value == 1), Is.GreaterThanOrEqualTo(3), "slide re-boosts");
        }

        [Test]
        public void ShowcaseIsDeterministic()
        {
            var a = Play();
            var b = Play();
            Assert.That(a.run.Ticks, Is.EqualTo(b.run.Ticks));
            Assert.That(a.s.Position, Is.EqualTo(b.s.Position));
        }
    }
}
