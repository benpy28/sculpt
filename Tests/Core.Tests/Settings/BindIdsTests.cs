using NUnit.Framework;
using Sculpt.Core.Settings;

namespace Sculpt.Core.Tests.Settings
{
    public class BindIdsTests
    {
        [TestCase("Jump", 0)]
        [TestCase("Crouch", 1)]
        [TestCase("Watch: next player", 0)]
        [TestCase("Slot 1 (1)", 0)]
        [TestCase("Weird#name", 12)]
        public void SavedBindKeys_RoundTrip_AndAreTheSameEveryRun(string action, int index)
        {
            string key = BindIds.Key(action, index);
            Assert.That(key, Is.EqualTo(BindIds.Key(action, index)));
            Assert.That(BindIds.TryParse(key, out string a, out int i), Is.True);
            Assert.That(a, Is.EqualTo(action));
            Assert.That(i, Is.EqualTo(index));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("3f2504e0-4f89-11d3-9a0c-0305e82c3301")] // an old random binding id
        [TestCase("sculpt:")]
        [TestCase("sculpt:#1")]
        [TestCase("sculpt:Jump#")]
        [TestCase("sculpt:Jump#x")]
        [TestCase("sculpt:Jump#-1")]
        [TestCase("sculpt:Jump#99999")]
        public void JunkBindKeys_AreRefused(string key)
        {
            Assert.That(BindIds.TryParse(key, out _, out int i), Is.False);
            Assert.That(i, Is.EqualTo(-1));
        }
    }
}
