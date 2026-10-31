using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Install step: import tray.json / seed defaults and write the manifest MindAttic Vault renders, then exit.
        if (args.Contains(MindAttic.Vault.Settings.Manifest.SettingsManifestCommand.Switch, StringComparer.OrdinalIgnoreCase))
        {
            var primary = Desktop.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            TraySettings.Load(primary);
            return;
        }

        using var single = new Mutex(true, @"Local\MindAttic.Capture.Tray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
