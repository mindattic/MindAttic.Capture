using System.Diagnostics;
using System.Reflection;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>What the hotkeys capture: the last screen, app or area recorded, shot or armed from the selector.</summary>
internal sealed record ArmedTarget(CaptureTarget Target, string App, string Label, VideoFormat Video, ImageFormat Image)
{
    public PixelRect? Area => Target is RegionTarget r ? r.Rect : null;
}

/// <summary>The tray icon, its menu, the hotkeys and the one recording they control.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string DesktopApp = "Desktop";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Recorder _recorder;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly TraySettings _settings;
    private readonly MindAttic.Vault.Settings.JsonSettingsStore<TraySettings> _store = TraySettings.CreateStore();
    private readonly FileSystemWatcher? _settingsWatcher;
    private readonly HotkeyWindow _hotkeys = new();
    private AreaChoice? _lastArea;
    private ArmedTarget _armed;
    private RecordingBorder? _border;
    private string? _lastCapture;
    private string? _balloonOpens;

    public TrayApp()
    {
        _settings = TraySettings.Load(PrimaryMonitor(), _store);
        _armed = ScreenTarget(Desktop.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Index ?? 0);
        _recorder = new Recorder(CreateOptions);
        _recorder.StateChanged += UpdateIcon;
        // The valve has already stopped and exported the recording; stopping here reports it and returns to idle.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _recorder.LimitReached += reason => ui.Post(state =>
        {
            if (_recorder.State == RecorderState.Recording) _ = StopAsync();
        }, null);
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new NotifyIcon { Icon = TrayIcons.Idle, Text = "MindAttic Capture", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += OnIconClick;
        _icon.BalloonTipClicked += OnBalloonClicked;
        _tick.Tick += (_, _) => UpdateIcon();
        _hotkeys.Pressed += OnHotkey;
        RegisterHotkeys();
        if (_settings.StartWithWindows != StartupRegistration.IsEnabled()) StartupRegistration.Set(_settings.StartWithWindows);
        BuildMenu();
        UpdateIcon();

        // Edits saved in MindAttic.Vault.Desktop take effect without restarting the tray.
        try
        {
            _settingsWatcher = new FileSystemWatcher(_store.Directory, _store.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            var debounce = new System.Windows.Forms.Timer { Interval = 300 };
            debounce.Tick += (_, _) => { debounce.Stop(); ApplySavedSettings(); };
            FileSystemEventHandler changed = (_, _) => ui.Post(_ => { debounce.Stop(); debounce.Start(); }, null);
            _settingsWatcher.Changed += changed;
            _settingsWatcher.Created += changed;
            _settingsWatcher.Renamed += (s, e) => changed(s, e);
            _settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            _settingsWatcher = null;
        }
    }

    private CaptureOptions CreateOptions(string appName)
    {
        var options = WindowsCaptureOptions.Create([], o =>
        {
            o.AppName = appName;
            o.AppVersion = null;
        });
        // After capture.json, so the folder picked in Settings wins.
        _settings.ApplyTo(options);
        return options;
    }

    private void RegisterHotkeys()
    {
        if (TryRegisterHotkeys() is { } problem) ShowError("Hotkey not available", problem);
    }

    private string? TryRegisterHotkeys()
    {
        var problems = _settings.Hotkeys().Select(h => _hotkeys.Register(h.Action, h.Keys)).OfType<string>().ToList();
        return problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
    }

    /// <summary>Settings are edited in MindAttic.Vault.Desktop; its saves come back through the file watcher.</summary>
    private void OpenSettings()
    {
        if (!MindAttic.Vault.Launch.VaultSettings.Open(TraySettings.AppId))
            ShowError("MindAttic Vault isn't installed", $"Install MindAttic Vault to change these settings, or edit {_store.FilePath}.");
    }

    /// <summary>Puts a save from Vault into effect; hotkeys another app holds are refused and the old ones kept.</summary>
    private void ApplySavedSettings()
    {
        // Load() answers defaults for a file it can't read or parse (Vault mid-write, a typo in a hand edit). Applying
        // those would blank Regions in memory and the next SaveRegion would persist the loss, so keep the last good
        // settings until the file reads cleanly. (The Vault 7.0.0 package referenced here predates LoadStrict/TryLoad.)
        if (!IsReadableSettingsFile(_store.FilePath)) return;
        var next = _store.Load();
        if (next.RecordHotkey != _settings.RecordHotkey || next.ScreenshotHotkey != _settings.ScreenshotHotkey)
        {
            var (oldRecord, oldShot) = (_settings.RecordHotkey, _settings.ScreenshotHotkey);
            (_settings.RecordHotkey, _settings.ScreenshotHotkey) = (next.RecordHotkey, next.ScreenshotHotkey);
            if (TryRegisterHotkeys() is { } problem)
            {
                (_settings.RecordHotkey, _settings.ScreenshotHotkey) = (oldRecord, oldShot);
                TryRegisterHotkeys();
                ShowError("Hotkey not available", problem + Environment.NewLine + "Keeping the previous hotkeys.");
            }
        }
        _settings.OutputRoot = next.OutputRoot;
        _settings.DefaultVideo = next.DefaultVideo;
        _settings.DefaultImage = next.DefaultImage;
        _settings.Regions = next.Regions;
        if (next.StartWithWindows != StartupRegistration.IsEnabled()) StartupRegistration.Set(next.StartWithWindows);
        _settings.StartWithWindows = next.StartWithWindows;
        UpdateIcon();
    }

    private static bool IsReadableSettingsFile(string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private void OnHotkey(TrayHotkey hotkey)
    {
        if (hotkey == TrayHotkey.Screenshot)
        {
            _ = ScreenshotAsync(_armed);
            return;
        }
        if (_recorder.State == RecorderState.Recording) _ = StopAsync();
        else if (_recorder.State == RecorderState.Idle) _ = RecordAsync(_armed);
    }

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
        var recording = _recorder.State == RecorderState.Recording;

        _menu.Items.Add(new ToolStripMenuItem(recording ? StopText() : $"Record {Ellipsize(_armed.Label)}", null, (_, _) => OnHotkey(TrayHotkey.Record))
        {
            ShortcutKeyDisplayString = _settings.RecordHotkey, Enabled = idle || recording, Font = new Font(_menu.Font, FontStyle.Bold),
        });
        _menu.Items.Add(new ToolStripMenuItem($"Screenshot {Ellipsize(_armed.Label)}", null, (_, _) => OnHotkey(TrayHotkey.Screenshot))
        {
            ShortcutKeyDisplayString = _settings.ScreenshotHotkey,
        });
        _menu.Items.Add(new ToolStripSeparator());

        var windows = AppWindowList.Get();
        _menu.Items.Add(MonitorMenu("Record Screen", m => _ = RecordAsync(ScreenTarget(m.Index)), idle));
        _menu.Items.Add(WindowMenu("Record App", windows, w => _ = RecordAsync(AppTarget(w)), idle));
        _menu.Items.Add(new ToolStripMenuItem("Record Area...", null, (_, _) => _ = PickAreaAsync()) { Enabled = idle });

        var screenshot = new ToolStripMenuItem("Screenshot");
        screenshot.DropDownItems.Add(MonitorMenu("Screen", m => _ = ScreenshotAsync(ScreenTarget(m.Index)), true));
        screenshot.DropDownItems.Add(WindowMenu("App", windows, w => _ = ScreenshotAsync(AppTarget(w)), true));
        screenshot.DropDownItems.Add(new ToolStripMenuItem("Area...", null, (_, _) => _ = PickAreaAsync()));
        _menu.Items.Add(screenshot);
        _menu.Items.Add(RegionsMenu(idle));

        _menu.Items.Add(new ToolStripSeparator());
        var last = _lastCapture is { } f && File.Exists(f) ? f : null;
        _menu.Items.Add(new ToolStripMenuItem("Open Last Capture", null, (_, _) => OpenFile(last!)) { Enabled = last is not null });
        _menu.Items.Add(new ToolStripMenuItem("Show Last Capture in Folder", null, (_, _) => ShowInExplorer(last!)) { Enabled = last is not null });
        _menu.Items.Add(new ToolStripMenuItem("Open Captures Folder", null, (_, _) => OpenCapturesFolder()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _ = ExitAsync()));
    }

    private static string Ellipsize(string text) => (text.Length > 40 ? text[..37] + "..." : text).Replace("&", "&&");

    private ArmedTarget ScreenTarget(int index) =>
        new(CaptureTarget.Monitor(index), DesktopApp, $"Screen {index + 1}", _settings.DefaultVideo, _settings.DefaultImage);

    private ArmedTarget AppTarget(WindowInfo w) =>
        new(CaptureTarget.Window(w.Hwnd), w.ProcessName.Length > 0 ? w.ProcessName : "App", w.Title, _settings.DefaultVideo, _settings.DefaultImage);

    private static ArmedTarget AreaTarget(AreaChoice a, string? name = null)
    {
        var r = a.Region;
        return new(CaptureTarget.Region(r.X, r.Y, r.Width, r.Height), DesktopApp, name ?? $"Area {r.Width}x{r.Height}", a.Video, a.Image);
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

    /// <summary>Saved Regions: each one can be recorded or shot straight away, armed for the hotkeys, adjusted in the selector, or deleted.</summary>
    private ToolStripMenuItem RegionsMenu(bool idle)
    {
        var menu = new ToolStripMenuItem("Saved Regions") { Enabled = _settings.Regions.Count > 0 };
        foreach (var region in _settings.Regions)
        {
            var r = region.Rect;
            var item = new ToolStripMenuItem($"{region.Name.Replace("&", "&&")}  ({r.Width}x{r.Height}, {region.Ratio})");
            item.DropDownItems.Add(new ToolStripMenuItem("Record", null, (_, _) => _ = RecordAsync(Remember(region))) { Enabled = idle });
            item.DropDownItems.Add(new ToolStripMenuItem("Screenshot", null, (_, _) => _ = ScreenshotAsync(Remember(region))));
            item.DropDownItems.Add(new ToolStripMenuItem("Arm for Hotkeys", null, (_, _) => Arm(Remember(region))));
            item.DropDownItems.Add(new ToolStripMenuItem("Adjust...", null, (_, _) => _ = PickAreaAsync(region.ToChoice())) { Enabled = idle });
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(new ToolStripMenuItem("Delete", null, (_, _) => DeleteRegion(region.Name)));
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ArmedTarget Remember(SavedRegion region)
    {
        _lastArea = region.ToChoice();
        return AreaTarget(_lastArea, region.Name);
    }

    private IReadOnlyList<SavedRegion> SaveRegion(string name, AreaChoice choice)
    {
        _settings.Upsert(SavedRegion.From(name, choice));
        _store.Save(_settings);
        return _settings.Regions;
    }

    private void DeleteRegion(string name)
    {
        if (MessageBox.Show($"Delete the saved region \"{name}\"?", "MindAttic Capture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settings.Remove(name);
        _store.Save(_settings);
    }

    private static PixelRect PrimaryMonitor() =>
        Desktop.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Bounds ?? new PixelRect(0, 0, 1920, 1080);

    private string StopText() => $"Stop Recording ({FormatElapsed(_recorder.Elapsed)})";

    internal static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>
    /// Opens the area selector where the last one left off. Enter arms the area for the hotkeys; the toolbar's
    /// Record and Screenshot buttons capture it straight away.
    /// </summary>
    private async Task PickAreaAsync(AreaChoice? start = null)
    {
        var picked = await RegionOverlay.PickAsync(start ?? _lastArea ?? NewArea(), _settings.Regions, SaveRegion);
        if (picked.Action == OverlayAction.Cancel) return;
        _lastArea = picked.Choice;
        var target = AreaTarget(picked.Choice);
        switch (picked.Action)
        {
            case OverlayAction.Confirm:
                Arm(target);
                break;
            case OverlayAction.Record:
                // Let the compositor drop the overlay before the first frame is grabbed.
                await Task.Delay(150);
                await RecordAsync(target);
                break;
            case OverlayAction.Screenshot:
                await Task.Delay(150);
                await ScreenshotAsync(target);
                break;
        }
    }

    private AreaChoice NewArea() => _settings.NewArea();

    private void Arm(ArmedTarget target)
    {
        _armed = target;
        Notify($"Ready: {target.Label}", $"{_settings.RecordHotkey} starts and stops recording it; {_settings.ScreenshotHotkey} takes a screenshot.", ToolTipIcon.Info, 4000, null);
        UpdateIcon();
    }

    private async Task RecordAsync(ArmedTarget target)
    {
        if (_recorder.State != RecorderState.Idle) return;
        _armed = target;
        try
        {
            await _recorder.StartAsync(target.Target, target.App, target.Label, TrayFormats.Recording(target.Video));
        }
        catch (CaptureException ex)
        {
            ShowError("Recording failed to start", ex.Message);
            return;
        }
        if (target.Area is { } area)
        {
            _border = new RecordingBorder(area);
            _border.Show();
        }
    }

    private async Task StopAsync()
    {
        _border?.Close();
        _border = null;
        var result = await _recorder.StopAsync();
        if (result is not null) Report(result);
    }

    private async Task ScreenshotAsync(ArmedTarget target)
    {
        _armed = target;
        try
        {
            Report(await _recorder.ScreenshotAsync(target.Target, target.App, target.Label, TrayFormats.Screenshot(target.Image)));
        }
        catch (CaptureException ex)
        {
            ShowError("Screenshot failed", ex.Message);
        }
    }

    /// <summary>Notifies the finished capture: every file written (lossless master and its MP4 copy sit side by side); clicking shows it.</summary>
    private void Report(CaptureResult result)
    {
        var (title, text) = CaptureReport.Describe(result, _recorder.LastSessionFolder);
        if (result.Succeeded)
        {
            _lastCapture = result.PrimaryFile;
            Notify(title, text, ToolTipIcon.Info, 8000, result.PrimaryFile);
        }
        else Notify(title, text, ToolTipIcon.Error, 15000, _recorder.LastSessionFolder);
    }

    private void ShowError(string title, string message) => Notify(title, message, ToolTipIcon.Error, 10000, null);

    /// <summary>A balloon; <paramref name="clickOpens"/> is the file (shown selected in Explorer) or folder a click opens.</summary>
    private void Notify(string title, string text, ToolTipIcon icon, int ms, string? clickOpens)
    {
        _balloonOpens = clickOpens;
        // Windows cuts balloon text at 255 characters.
        _icon.ShowBalloonTip(ms, title, text.Length > 255 ? text[..252] + "..." : text, icon);
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        if (_balloonOpens is { } path) ShowInExplorer(path);
    }

    private static void ShowInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void UpdateIcon()
    {
        switch (_recorder.State)
        {
            case RecorderState.Recording:
                _icon.Icon = TrayIcons.Recording;
                _icon.Text = Truncate($"Recording {FormatElapsed(_recorder.Elapsed)} - {_recorder.Label} (click or {_settings.RecordHotkey} to stop)");
                _tick.Start();
                break;
            case RecorderState.Starting or RecorderState.Stopping:
                _icon.Icon = TrayIcons.Busy;
                _icon.Text = _recorder.State == RecorderState.Starting ? "MindAttic Capture - starting..." : "MindAttic Capture - saving...";
                _tick.Stop();
                break;
            default:
                _icon.Icon = TrayIcons.Idle;
                _icon.Text = Truncate($"MindAttic Capture - {_settings.RecordHotkey} records {_armed.Label}");
                _tick.Stop();
                break;
        }
    }

    private static string Truncate(string text) => text.Length <= 127 ? text : text[..127];

    private void OpenCapturesFolder()
    {
        var root = CreateOptions(DesktopApp).OutputRoot;
        if (!Directory.Exists(root)) root = new CaptureOptions().FallbackRoot;
        Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        // A recording still starting can't be stopped yet: wait for it, then stop it, so Exit never abandons one.
        for (var waited = 0; waited < 300; waited++)
        {
            if (_recorder.State == RecorderState.Recording) await StopAsync();
            else if (_recorder.State is RecorderState.Starting or RecorderState.Stopping) await Task.Delay(500);
            else break;
        }
        _settingsWatcher?.Dispose();
        _hotkeys.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _tick.Dispose();
        ExitThread();
    }
}
