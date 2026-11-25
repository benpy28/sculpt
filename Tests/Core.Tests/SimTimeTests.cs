using System;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class SimTimeTests
    {
        [Test]
        public void TickRateIs120() => Assert.That(SimTime.TicksPerSecond, Is.EqualTo(120));

        [Test]
        public void Per60IsTwoTicks() => Assert.That(SimTime.Per60, Is.EqualTo(2));

        [TestCase(0.0, 0)]
        [TestCase(0.2, 24)]
        [TestCase(0.35, 42)]
        [TestCase(1.5, 180)]
        [TestCase(20.0, 2400)]
        public void SecondsToTicks(double seconds, int ticks) =>
            Assert.That(SimTime.SecondsToTicks(seconds), Is.EqualTo(ticks));

        [Test]
        public void NegativeSecondsThrow() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => SimTime.SecondsToTicks(-0.1));
    }
}
