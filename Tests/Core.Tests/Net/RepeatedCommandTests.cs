using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    public class RepeatedCommandTests
    {
        /// <summary>
        /// While a client's inputs are lost the server repeats its last command (fire still held). The client's view
        /// of others keeps moving one tick per tick, so the repeat's view tick must too: a frozen one rewinds every
        /// shot fired on a guessed tick further into the past (up to the 0.3 s cap) than anything the client drew.
        /// </summary>
        [Test]
        public void ARepeatedCommand_KeepsTheSameViewLag()
        {
            var h = NetHarness.Flat();
            var a = h.AddClient(up: NetHarness.Lag(10), down: NetHarness.Lag(10));
            a.Script = t => new MoveInput { FireHeld = true, WantsToShoot = true };
            h.RunSeconds(1);
            Assert.That(a.ServerSide.LastCmdWasReal, Is.True);
            int lagReal = a.ServerSide.LastCmd.Tick - a.ServerSide.LastCmd.ViewTick;
            a.Up.Conditions.LossPercent = 100;
            h.RunMs(30 * NetHarness.TickMs);
            Assert.That(a.ServerSide.LastCmdWasReal, Is.False, "the server is guessing");
            int lagGuess = a.ServerSide.LastCmd.Tick - a.ServerSide.LastCmd.ViewTick;
            Assert.That(lagGuess, Is.EqualTo(lagReal).Within(1), "a guessed command views the world as far back as the real ones did");
        }

        [Test]
        public void AsRepeat_MovesTheViewOnWithTheTick()
        {
            var c = new InputCmd { Tick = 100, ViewTick = 97, ViewFrac = 40 };
            InputCmd r = c.AsRepeat(104);
            Assert.That(r.Tick, Is.EqualTo(104));
            Assert.That(r.ViewTick, Is.EqualTo(101));
            Assert.That(r.ViewFrac, Is.EqualTo(40));
        }
    }
}
