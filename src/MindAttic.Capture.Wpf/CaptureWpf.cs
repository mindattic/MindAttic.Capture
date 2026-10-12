using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Wpf;

/// <summary>
/// One-line capture for WPF apps. Call <see cref="Init"/> in <c>App.OnStartup</c>:
/// <code>CaptureWpf.Init(this, e.Args);</code>
/// With <c>-capture video</c> the main window is recorded at its native size from the moment it is
/// shown until the app exits; with <c>-capture screen 5</c> a PNG is taken every 5 seconds.
/// Without flags the engine still runs, so <c>Capture.Raise(...)</c> rules from capture.json work.
/// </summary>
public static class CaptureWpf
{
    /// <summary>
    /// Starts capture for <paramref name="app"/>'s main window once it is on screen and stops it
    /// (exporting the session) when the app exits. Never throws: setup problems are logged and the
    /// app keeps running without capture.
    /// </summary>
    public static WpfCapture Init(Application app, string[] args, Action<CaptureOptions>? configure = null, DesktopHostOptions? hostOptions = null)
    {
        var handle = new WpfCapture();
        CaptureOptions options;
        try
        {
            options = WindowsCaptureOptions.Create(args, o =>
            {
                o.AppName = CaptureNaming.RepoName(AppContext.BaseDirectory) ?? AppNameOf(app);
                configure?.Invoke(o);
            });
        }
        catch (CaptureException ex)
        {
            new TraceCaptureLog().Log(CaptureLogLevel.Error, $"Capture disabled: {ex.Message}");
            return handle;
        }
        handle.Options = options;

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        var waited = TimeSpan.Zero;
        timer.Tick += async (_, _) =>
        {
            waited += timer.Interval;
            var window = app.MainWindow;
            var hwnd = window is { IsVisible: true } ? new WindowInteropHelper(window).Handle : 0;
            if (hwnd == 0)
            {
                if (waited > TimeSpan.FromMinutes(2))
                {
                    timer.Stop();
                    options.Log.Log(CaptureLogLevel.Warning, "No main window appeared within 2 minutes; capture not started.");
                }
                return;
            }
            timer.Stop();
            await handle.StartAsync(new DesktopCaptureHost(CaptureTarget.Window(hwnd), hostOptions), options);
        };
        timer.Start();
        app.Exit += (_, _) =>
        {
            timer.Stop();
            handle.StopBlocking();
        };
        return handle;
    }

    private static string AppNameOf(Application app) =>
        app.GetType().Assembly.GetName().Name is { Length: > 0 } name && name != "App" ? name : System.Diagnostics.Process.GetCurrentProcess().ProcessName;
}

/// <summary>The capture started by <see cref="CaptureWpf.Init"/>.</summary>
public sealed class WpfCapture
{
    private readonly object _gate = new();
    private WindowsCaptureSession? _session;
    private Task? _start;
    private bool _stopping;
    private readonly TaskCompletionSource<ICapture> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Effective options (null if setup failed).</summary>
    public CaptureOptions? Options { get; internal set; }

    /// <summary>The engine once the main window is up (also <see cref="Capture.Current"/>).</summary>
    public ICapture? Engine => _session?.Engine;

    /// <summary>Completes when capture is running.</summary>
    public Task<ICapture> Ready => _ready.Task;

    internal Task StartAsync(ICaptureHost host, CaptureOptions options) => _start = StartCoreAsync(host, options);

    private async Task StartCoreAsync(ICaptureHost host, CaptureOptions options)
    {
        try
        {
            var session = await DesktopCapture.StartAsync(host, options).ConfigureAwait(false);
            bool stopping;
            lock (_gate)
            {
                stopping = _stopping;
                if (!stopping) _session = session;
            }
            if (stopping)
            {
                // The app exited while capture was starting: shut down what just started so ffmpeg isn't orphaned.
                await session.DisposeAsync().ConfigureAwait(false);
                _ready.TrySetCanceled();
                return;
            }
            _ready.TrySetResult(session.Engine);
        }
        catch (Exception ex)
        {
            options.Log.Log(CaptureLogLevel.Error, $"Capture could not start: {ex.Message}", ex);
            _ready.TrySetException(ex);
        }
    }

    /// <summary>Stops capture on the UI thread during Application.Exit (the engine never needs the UI thread to finish).</summary>
    internal void StopBlocking()
    {
        WindowsCaptureSession? session;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            session = _session;
            _session = null;
        }
        // Still starting: StartCoreAsync sees _stopping and disposes the session itself, so wait for that.
        var shutdown = session is not null ? Task.Run(() => session.DisposeAsync().AsTask()) : _start;
        if (shutdown is null) return;
        try
        {
            shutdown.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Options?.Log.Log(CaptureLogLevel.Error, "Capture did not shut down cleanly.", ex);
        }
    }
}
