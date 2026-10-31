using System.IO;
using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

/// <summary>
/// Points MindAttic Vault's roaming and local roots at a temp folder for the whole assembly, so no test writes the
/// real <c>%APPDATA%\MindAttic</c> or <c>%LOCALAPPDATA%\MindAttic</c> (e.g. the agent's agent.json, which a live
/// capture agent owns) (M.CAPT-T84).
/// </summary>
[SetUpFixture]
public sealed class TestVaultRoots
{
    private string _root = "";
    private string? _savedRoaming;
    private string? _savedLocal;

    public static string Local => VaultPaths.LocalRoot;

    [OneTimeSetUp]
    public void Redirect()
    {
        _root = Path.Combine(Path.GetTempPath(), "capture-windows-tests-" + Guid.NewGuid().ToString("N"));
        _savedRoaming = Environment.GetEnvironmentVariable(VaultPaths.RoamingRootEnvVar);
        _savedLocal = Environment.GetEnvironmentVariable(VaultPaths.LocalRootEnvVar);
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Path.Combine(_root, "roaming"));
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Path.Combine(_root, "local"));
    }

    [OneTimeTearDown]
    public void Restore()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, _savedRoaming);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, _savedLocal);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
