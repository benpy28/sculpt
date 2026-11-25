using System;
using NUnit.Framework;
using Sculpt.Core.Diagnostics;

namespace Sculpt.Core.Tests.Diagnostics
{
    public class FrameStatsTests
    {
        [Test]
        public void Empty_IsAllZero()
        {
            var s = new FrameStats(8);
            Assert.That(s.Count, Is.Zero);
            Assert.That(s.AverageMs, Is.Zero);
            Assert.That(s.AverageFps, Is.Zero);
            Assert.That(s.MaxMs, Is.Zero);
            Assert.That(s.PercentileMs(0.99), Is.Zero);
            Assert.That(s.LowFps(0.01), Is.Zero);
            Assert.That(s.Recent(0), Is.Zero);
        }

        /// <summary>Worked by hand: 99 frames of 5 ms and one 50 ms hitch.</summary>
        [Test]
        public void OneHitchInAHundred()
        {
            var s = new FrameStats(256);
            for (int i = 0; i < 60; i++) s.Add(5);
            s.Add(50);
            for (int i = 0; i < 39; i++) s.Add(5);

            Assert.That(s.Count, Is.EqualTo(100));
            Assert.That(s.AverageMs, Is.EqualTo(5.45).Within(1e-4), "(99 x 5 + 50) / 100");
            Assert.That(s.AverageFps, Is.EqualTo(1000.0 / 5.45).Within(1e-2), "frames over time, not the mean of 200 and 20");
            Assert.That(s.MinMs, Is.EqualTo(5));
            Assert.That(s.MaxMs, Is.EqualTo(50));
            Assert.That(s.PercentileMs(0.5), Is.EqualTo(5));
            Assert.That(s.PercentileMs(0.99), Is.EqualTo(5), "99 of the 100 frames were 5 ms or faster");
            Assert.That(s.PercentileMs(1.0), Is.EqualTo(50));
            Assert.That(s.LowFps(0.01), Is.EqualTo(20).Within(1e-9), "the slowest 1% is the one 50 ms frame");
            Assert.That(s.CountOver(10), Is.EqualTo(1));
            Assert.That(s.CountOver(4), Is.EqualTo(100));
        }

        /// <summary>Worked by hand: the slowest 1% of 200 frames is two frames, 20 ms and 40 ms: 2 frames in 60 ms.</summary>
        [Test]
        public void OnePercentLow_AveragesTheSlowestFramesByTime()
        {
            var s = new FrameStats(256);
            for (int i = 0; i < 198; i++) s.Add(4);
            s.Add(20);
            s.Add(40);
            Assert.That(s.LowFps(0.01), Is.EqualTo(1000.0 * 2 / 60).Within(1e-6));
            Assert.That(s.LowFps(0.001), Is.EqualTo(25).Within(1e-6), "0.1% of 200 rounds to no frames, so the single worst one counts");
        }

        [Test]
        public void FewFrames_LowStillCountsOne()
        {
            var s = new FrameStats(16);
            s.Add(10);
            s.Add(8);
            s.Add(25);
            Assert.That(s.LowFps(0.01), Is.EqualTo(40).Within(1e-9));
        }

        /// <summary>Worked by hand: capacity 4, frames 1..5: the 1 rolls out, leaving 2, 3, 4, 5.</summary>
        [Test]
        public void RollsOverTheOldest()
        {
            var s = new FrameStats(4);
            for (int i = 1; i <= 5; i++) s.Add(i);
            Assert.That(s.Count, Is.EqualTo(4));
            Assert.That(s.TotalFrames, Is.EqualTo(5));
            Assert.That(s.AverageMs, Is.EqualTo(3.5).Within(1e-6));
            Assert.That(s.MinMs, Is.EqualTo(2));
            Assert.That(s.MaxMs, Is.EqualTo(5));
            Assert.That(s.Recent(0), Is.EqualTo(5));
            Assert.That(s.Recent(3), Is.EqualTo(2));
            Assert.That(s.Recent(4), Is.Zero, "nothing that old is held");
            Assert.That(s.Recent(-1), Is.Zero);
        }

        [Test]
        public void BadValues_AreIgnored()
        {
            var s = new FrameStats(8);
            s.Add(double.NaN);
            s.Add(-1);
            s.Add(double.PositiveInfinity);
            Assert.That(s.Count, Is.Zero);
            s.Add(0);
            s.Add(4);
            Assert.That(s.Count, Is.EqualTo(2));
            Assert.That(s.AverageMs, Is.EqualTo(2));
        }

        [Test]
        public void Clear_Empties()
        {
            var s = new FrameStats(8);
            s.Add(3);
            s.Add(9);
            s.Clear();
            Assert.That(s.Count, Is.Zero);
            Assert.That(s.TotalFrames, Is.Zero);
            Assert.That(s.MaxMs, Is.Zero);
            s.Add(7);
            Assert.That(s.AverageMs, Is.EqualTo(7));
            Assert.That(s.MaxMs, Is.EqualTo(7));
        }

        /// <summary>Seeded: every figure must match a plain sort-and-count over the same newest frames, through many roll-overs.</summary>
        [Test]
        public void MatchesBruteForce_ThroughRollOvers()
        {
            var rng = new Random(20261125);
            for (int round = 0; round < 40; round++)
            {
                int cap = 1 + rng.Next(70);
                var s = new FrameStats(cap);
                var all = new System.Collections.Generic.List<double>();
                int n = rng.Next(1, 400);
                for (int i = 0; i < n; i++)
                {
                    double v = Math.Round(rng.NextDouble() * 30, 3);
                    s.Add(v);
                    all.Add((float)v);
                    if (i % 17 != 0 && i != n - 1) continue;

                    int held = Math.Min(cap, all.Count);
                    var window = all.GetRange(all.Count - held, held);
                    var sorted = new System.Collections.Generic.List<double>(window);
                    sorted.Sort();
                    double sum = 0;
                    foreach (double x in window) sum += x;
                    Assert.That(s.Count, Is.EqualTo(held));
                    Assert.That(s.AverageMs, Is.EqualTo(sum / held).Within(1e-3));
                    Assert.That(s.MaxMs, Is.EqualTo(sorted[held - 1]).Within(1e-6));
                    Assert.That(s.MinMs, Is.EqualTo(sorted[0]).Within(1e-6));
                    foreach (double p in new[] { 0.1, 0.5, 0.9, 0.99 })
                    {
                        int rank = Math.Max(0, Math.Min(held - 1, (int)Math.Ceiling(p * held) - 1));
                        Assert.That(s.PercentileMs(p), Is.EqualTo(sorted[rank]).Within(1e-6), $"p{p} cap {cap} held {held}");
                    }
                    int k = Math.Max(1, Math.Min(held, (int)Math.Round(held * 0.05)));
                    double slow = 0;
                    for (int j = held - k; j < held; j++) slow += sorted[j];
                    if (slow > 0) Assert.That(s.LowFps(0.05), Is.EqualTo(1000.0 * k / slow).Within(1e-3));
                    for (int j = 0; j < held; j++) Assert.That(s.Recent(j), Is.EqualTo(window[held - 1 - j]).Within(1e-6));
                }
            }
        }
    }
}
