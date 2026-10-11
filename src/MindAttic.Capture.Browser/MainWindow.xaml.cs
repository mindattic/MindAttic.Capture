using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MindAttic.Capture.WebView2;
using Microsoft.Web.WebView2.Core;

namespace MindAttic.Capture.Browser;

public partial class MainWindow : Window
{
    private readonly BrowserTarget _target;
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal MainWindow(BrowserTarget target)
    {
        _target = target;
        InitializeComponent();
        Address.Text = target.Url;
        Loaded += async (_, _) => await InitializeWebViewAsync();
        _stateTimer.Tick += (_, _) => UpdateCaptureState();
        _stateTimer.Start();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Browser");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            Status.Text = $"WebView2 runtime unavailable: {ex.Message}";
            return;
        }
        var core = Web.CoreWebView2;
        if (_target.SiteFolder is { } folder && _target.VirtualHost is { } host)
        {
            core.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.Allow);
            Status.Text = $"Serving {folder} as https://{host}/";
        }
        await CaptureWebView2.AttachAsync(core);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.SourceChanged += (_, _) =>
        {
            if (!Address.IsKeyboardFocusWithin) Address.Text = core.Source;
        };
        core.HistoryChanged += (_, _) =>
        {
            BackButton.IsEnabled = core.CanGoBack;
            ForwardButton.IsEnabled = core.CanGoForward;
        };
        core.DocumentTitleChanged += (_, _) =>
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "MindAttic Browser" : $"{core.DocumentTitle} — MindAttic Browser";
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Status.Text = $"Could not load {core.Source} ({e.WebErrorStatus}).";
        };
        BackButton.IsEnabled = ForwardButton.IsEnabled = false;
        core.Navigate(_target.Url);
    }

    private void Navigate(string text)
    {
        if (Web.CoreWebView2 is null) return;
        var url = BrowserTarget.NormalizeUrl(text);
        Address.Text = url;
        Web.CoreWebView2.Navigate(url);
        Web.Focus();
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Navigate(Address.Text);
        else if (e.Key == Key.Escape) Address.Text = Web.CoreWebView2?.Source ?? Address.Text;
    }

    private void OnAddressFocused(object sender, KeyboardFocusChangedEventArgs e) => Address.SelectAll();
    private void OnBack(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private void OnForward(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward(); }
    private void OnReload(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();
    private void OnHome(object sender, RoutedEventArgs e) => Navigate(_target.Url);
    private void OnFocusAddress(object sender, RoutedEventArgs e) => Address.Focus();
    private void OnDevTools(object sender, RoutedEventArgs e) => Web.CoreWebView2?.OpenDevToolsWindow();

    private async void OnScreenshot(object sender, RoutedEventArgs e)
    {
        var capture = Capture.Current;
        if (!capture.Enabled)
        {
            Status.Text = "Capture is not running yet.";
            return;
        }
        Status.Text = "Taking screenshot...";
        var r = await capture.ScreenshotAsync("browser");
        Status.Text = r.Succeeded ? $"Saved {r.PrimaryFile}" : $"Screenshot failed: {r.Error}";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var capture = Capture.Current;
        if (!capture.Enabled)
        {
            Status.Text = "Capture is not running yet.";
            return;
        }
        var folder = capture.SessionFolder;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void UpdateCaptureState()
    {
        var options = App.Capture.Options;
        var capture = Capture.Current;
        if (options is null || !capture.Enabled)
        {
            CaptureState.Text = "";
            return;
        }
        var parts = new List<string>();
        if (options.SessionRecording) parts.Add("● REC");
        if (options.ScreenshotInterval is { } interval) parts.Add($"▣ every {interval.TotalSeconds:0.#}s");
        if (capture.IsRecording) parts.Add("● clip");
        CaptureState.Text = string.Join("   ", parts);
        CaptureState.Foreground = options.SessionRecording || capture.IsRecording ? (Brush)FindResource("Rec") : (Brush)FindResource("Muted");
    }
}
