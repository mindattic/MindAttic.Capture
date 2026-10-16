using System;
using System.IO;
using MindAttic.Capture.Unity;
using NUnit.Framework;

namespace MindAttic.Capture.Unity.Tests
{
    public class UnityAgentLocatorTests
    {
        private const string OverrideVar = "MINDATTIC_CAPTURE_AGENT";
        private string _savedOverride;

        [SetUp]
        public void Setup() => _savedOverride = Environment.GetEnvironmentVariable(OverrideVar);

        [TearDown]
        public void Cleanup() => Environment.SetEnvironmentVariable(OverrideVar, _savedOverride);

        [Test]
        public void Find_uses_the_environment_variable_override_when_it_points_to_a_real_agent()
        {
            var agentExe = Environment.GetEnvironmentVariable("MAC_T9_AGENT_EXE");
            if (string.IsNullOrEmpty(agentExe) || !File.Exists(agentExe))
                Assert.Ignore("MAC_T9_AGENT_EXE must point at a built capture.exe (dotnet build src/MindAttic.Capture.Agent first).");

            Environment.SetEnvironmentVariable(OverrideVar, agentExe);
            Assert.That(UnityAgentLocator.Find(), Is.EqualTo(agentExe));
        }

        [Test]
        public void Find_ignores_an_override_with_the_wrong_product_name()
        {
            var wrongProduct = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (!File.Exists(wrongProduct)) Assert.Ignore("notepad.exe not found for a wrong-product fixture.");

            Environment.SetEnvironmentVariable(OverrideVar, wrongProduct);
            Assert.That(UnityAgentLocator.Find(), Is.Not.EqualTo(wrongProduct));
        }

        [Test]
        public void Find_ignores_a_nonexistent_override_path()
        {
            var bogus = Path.Combine(Path.GetTempPath(), "nonexistent-agent-" + Guid.NewGuid().ToString("N") + ".exe");
            Environment.SetEnvironmentVariable(OverrideVar, bogus);
            Assert.That(UnityAgentLocator.Find(), Is.Not.EqualTo(bogus));
        }
    }
}
