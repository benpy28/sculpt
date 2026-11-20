using System;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class SimTimeTests
    {
        [Test]
        public void TickRateIs60() => Assert.That(SimTime.TicksPerSecond, Is.EqualTo(60));

        [TestCase(0.0, 0)]
        [TestCase(0.2, 12)]
        [TestCase(0.35, 21)]
        [TestCase(1.5, 90)]
        [TestCase(20.0, 1200)]
        public void SecondsToTicks(double seconds, int ticks) =>
            Assert.That(SimTime.SecondsToTicks(seconds), Is.EqualTo(ticks));

        [Test]
        public void NegativeSecondsThrow() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => SimTime.SecondsToTicks(-0.1));
    }
}
