using System.Diagnostics;
using System.Reflection;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>The tray icon, its menu and the one recording it controls.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string DesktopApp = "Desktop";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Recorder _recorder;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private AreaChoice? _lastArea;

    public TrayApp()
    {
        _recorder = new Recorder(CreateOptions);
        _recorder.StateChanged += UpdateIcon;
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new NotifyIcon { Icon = TrayIcons.Idle, Text = "MindAttic Capture", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += OnIconClick;
        _tick.Tick += (_, _) => UpdateIcon();
        BuildMenu();
    }

    private static CaptureOptions CreateOptions(string appName) =>
        WindowsCaptureOptions.Create([], o =>
        {
            o.AppName = appName;
            o.AppVersion = null;
        });

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_recorder.State == RecorderState.Recording)
        {
            _ = StopAsync();
            return;
        }
        // NotifyIcon only opens its menu on right-click; reuse its own (non-public) opener for left-click.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var idle = _recorder.State == RecorderState.Idle;

        var windows = AppWindowList.Get();
        _menu.Items.Add(MonitorMenu("Record Screen", m => _ = RecordAsync(CaptureTarget.Monitor(m.Index), DesktopApp, $"Screen {m.Index + 1}"), idle));
        _menu.Items.Add(WindowMenu("Record App", windows, w => _ = RecordAsync(CaptureTarget.Window(w.Hwnd), AppName(w), w.Title), idle));
        _menu.Items.Add(new ToolStripMenuItem("Record Area...", null, (_, _) => _ = PickAreaAsync(record: true)) { Enabled = idle });

        var screenshot = new ToolStripMenuItem("Screenshot");
        screenshot.DropDownItems.Add(MonitorMenu("Screen", m => _ = ScreenshotAsync(CaptureTarget.Monitor(m.Index), DesktopApp, $"Screen {m.Index + 1}"), true));
        screenshot.DropDownItems.Add(WindowMenu("App", windows, w => _ = ScreenshotAsync(CaptureTarget.Window(w.Hwnd), AppName(w), w.Title), true));
        screenshot.DropDownItems.Add(new ToolStripMenuItem("Area...", null, (_, _) => _ = PickAreaAsync(record: false)));
        _menu.Items.Add(screenshot);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(StopText(), null, (_, _) => _ = StopAsync()) { Enabled = _recorder.State == RecorderState.Recording });
        _menu.Items.Add(new ToolStripMenuItem("Open Captures Folder", null, (_, _) => OpenCapturesFolder()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _ = ExitAsync()));
    }

    /// <summary>One item per monitor ("Screen 1 (primary) 2560x1440"); a single monitor still gets a submenu so the shape never changes.</summary>
    private static ToolStripMenuItem MonitorMenu(string text, Action<MonitorInfo> pick, bool enabled)
    {
        var menu = new ToolStripMenuItem(text) { Enabled = enabled };
        foreach (var m in Desktop.GetMonitors())
        {
            var label = $"Screen {m.Index + 1}{(m.IsPrimary ? " (primary)" : "")}  {m.Bounds.Width}x{m.Bounds.Height}";
            menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => pick(m)));
        }
        return menu;
    }

    /// <summary>One item per open app window; recordings follow the window (Windows Graphics Capture) at its own size.</summary>
    private static ToolStripMenuItem WindowMenu(string text, IReadOnlyList<WindowInfo> windows, Action<WindowInfo> pick, bool enabled)
    {
        var menu = new ToolStripMenuItem(text) { Enabled = enabled && windows.Count > 0 };
        foreach (var w in windows)
            menu.DropDownItems.Add(new ToolStripMenuItem(AppWindowList.MenuText(w), AppWindowList.Icon(w.Hwnd), (_, _) => pick(w)));
        return menu;
    }

    private static string AppName(WindowInfo w) => w.ProcessName.Length > 0 ? w.ProcessName : "App";

    private string StopText() => _recorder.State == RecorderState.Recording
        ? $"Stop Recording ({FormatElapsed(_recorder.Elapsed)})"
        : "Stop Recording";

    internal static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Opens the area selector where the last one left off; Enter does what the menu item said, the toolbar buttons override it.</summary>
    private async Task PickAreaAsync(bool record)
    {
        var picked = await RegionOverlay.PickAsync(_lastArea ?? NewArea());
        _lastArea = picked.Choice;
        var action = picked.Action == OverlayAction.Confirm ? (record ? OverlayAction.Record : OverlayAction.Screenshot) : picked.Action;
        if (action == OverlayAction.Cancel) return;
        // Let the compositor drop the overlay before the first frame is grabbed.
        await Task.Delay(150);
        await CaptureAreaAsync(picked.Choice, action == OverlayAction.Record);
    }

    private static AreaChoice NewArea() => new(default, AspectRatio.Free, VideoFormat.LosslessAndMp4, ImageFormat.Png);

    private async Task CaptureAreaAsync(AreaChoice area, bool record)
    {
        var r = area.Region;
        var target = CaptureTarget.Region(r.X, r.Y, r.Width, r.Height);
        var label = $"Area {r.Width}x{r.Height}";
        if (record) await RecordAsync(target, DesktopApp, label, TrayFormats.Recording(area.Video));
        else await ScreenshotAsync(target, DesktopApp, label, TrayFormats.Screenshot(area.Image));
    }

    private async Task RecordAsync(CaptureTarget target, string appName, string label, RecordingOptions? recording = null)
    {
        try
        {
            await _recorder.StartAsync(target, appName, label, recording ?? new RecordingOptions { Preset = CapturePreset.Trailer });
        }
        catch (CaptureException ex)
        {
            ShowError("Recording failed to start", ex.Message);
        }
    }

    private async Task StopAsync()
    {
        var result = await _recorder.StopAsync();
        if (result is not null) Report(result);
    }

    private async Task ScreenshotAsync(CaptureTarget target, string appName, string label, ScreenshotOptions? options = null)
    {
        try
        {
            Report(await _recorder.ScreenshotAsync(target, appName, label, options ?? new ScreenshotOptions()));
        }
        catch (CaptureException ex)
        {
            ShowError("Screenshot failed", ex.Message);
        }
    }

    private void Report(CaptureResult result)
    {
        if (result.Succeeded) _icon.ShowBalloonTip(5000, "Saved", string.Join(Environment.NewLine, result.Files.Select(Path.GetFileName)), ToolTipIcon.Info);
        else ShowError("Capture failed", result.Error ?? "Unknown error.");
    }

    private void ShowError(string title, string message) => _icon.ShowBalloonTip(10000, title, message, ToolTipIcon.Error);

    private void UpdateIcon()
    {
        switch (_recorder.State)
        {
            case RecorderState.Recording:
                _icon.Icon = TrayIcons.Recording;
                _icon.Text = Truncate($"Recording {FormatElapsed(_recorder.Elapsed)} - {_recorder.Label} (click to stop)");
                _tick.Start();
                break;
            case RecorderState.Starting or RecorderState.Stopping:
                _icon.Icon = TrayIcons.Busy;
                _icon.Text = _recorder.State == RecorderState.Starting ? "MindAttic Capture - starting..." : "MindAttic Capture - saving...";
                _tick.Stop();
                break;
            default:
                _icon.Icon = TrayIcons.Idle;
                _icon.Text = "MindAttic Capture";
                _tick.Stop();
                break;
        }
    }

    private static string Truncate(string text) => text.Length <= 127 ? text : text[..127];

    private static void OpenCapturesFolder()
    {
        var root = CreateOptions(DesktopApp).OutputRoot;
        if (!Directory.Exists(root)) root = new CaptureOptions().FallbackRoot;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        await StopAsync();
        _icon.Visible = false;
        _icon.Dispose();
        _tick.Dispose();
        ExitThread();
    }
}
