using System.Windows;
using MindAttic.Capture.Wpf;

namespace MindAttic.Capture.Browser;

/// <summary>
/// MindAttic Browser: a WebView2 browser for viewing and capturing any website or local site folder.
/// <code>
/// MindAttic.Browser.exe https://mindattic.com -capture video
/// MindAttic.Browser.exe D:\Projects\MindAttic\MindAttic.Web\mindattic.com -capture screen 5
/// </code>
/// </summary>
public partial class App : Application
{
    internal static WpfCapture Capture { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var target = BrowserTarget.Parse(e.Args);
        Capture = CaptureWpf.Init(this, e.Args, o => o.AppName = target.AppName);
        MainWindow = new MainWindow(target);
        MainWindow.Show();
    }
}
