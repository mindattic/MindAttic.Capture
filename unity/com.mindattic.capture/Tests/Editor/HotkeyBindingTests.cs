using MindAttic.Capture.Unity;
using NUnit.Framework;

namespace MindAttic.Capture.Unity.Tests
{
    public class HotkeyBindingTests
    {
        [TestCase("F9")]
        [TestCase("f9")]
        [TestCase("Ctrl+F9")]
        [TestCase("ctrl+shift+F9")]
        [TestCase("Ctrl+Alt+Shift+F9")]
        [TestCase("5")]
        [TestCase("PrintScreen")]
        public void Parses_valid_bindings(string text) =>
            Assert.That(CaptureRunner.HotkeyBinding.TryParse(text, out _), Is.True);

        [TestCase("")]
        [TestCase("NotAKey")]
        [TestCase("Ctrl+")]
        [TestCase("Bogus+F9")]
        public void Rejects_bad_bindings(string text) =>
            Assert.That(CaptureRunner.HotkeyBinding.TryParse(text, out _), Is.False);
    }
}
