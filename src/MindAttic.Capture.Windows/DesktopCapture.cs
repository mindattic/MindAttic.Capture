using System.Diagnostics;
using MindAttic.Capture.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MindAttic.Capture.Windows;

/// <summary>One-line setup for Windows apps and tools.</summary>
public static class DesktopCapture
{
    /// <summary>
    /// Builds options from code defaults, capture.json beside the exe and the <c>-capture</c> flags,
    /// wires the detached finalizer and hotkeys, and starts an engine for <paramref name="target"/>.
    /// The result is also registered as <see cref="Capture.Current"/>.
    /// </summary>
    public static async Task<WindowsCaptureSession> StartAsync(CaptureTarget target, string[]? args = null,
        Action<CaptureOptions>? configure = null, DesktopHostOptions? hostOptions = null, CancellationToken ct = default)
    {
        var options = WindowsCaptureOptions.Create(args, configure);
        return await StartAsync(new DesktopCaptureHost(target, hostOptions), options, ct).ConfigureAwait(false);
    }

    /// <summary>Starts an engine for any host with Windows extras (hotkeys, detached finalizer).</summary>
    public static async Task<WindowsCaptureSession> StartAsync(ICaptureHost host, CaptureOptions options, CancellationToken ct = default)
    {
        var engine = await CaptureEngine.StartNewAsync(host, options, ct).ConfigureAwait(false);
        GlobalHotkeys? hotkeys = null;
        if (options.Enabled && options.Hotkeys.Count > 0) hotkeys = new GlobalHotkeys(engine, options.Hotkeys, options.Log);
        return new WindowsCaptureSession(engine, hotkeys);
    }
}

/// <summary>Builds <see cref="CaptureOptions"/> the MindAttic way on Windows.</summary>
public static class WindowsCaptureOptions
{
    /// <summary>Code defaults, then capture.json beside the exe, then command-line flags; finds the agent for detached finalizing.</summary>
    public static CaptureOptions Create(string[]? args = null, Action<CaptureOptions>? configure = null)
    {
        var options = new CaptureOptions();
        configure?.Invoke(options);
        var config = Path.Combine(AppContext.BaseDirectory, "capture.json");
        if (File.Exists(config)) options.ApplyConfigFile(config);
        options.Apply(CaptureArgs.Parse(args ?? Environment.GetCommandLineArgs().Skip(1).ToArray()));
        options.DetachedFinalizerPath ??= AgentLocator.Find();
        return options;
    }
}

/// <summary>An engine plus the Windows-only extras that must be disposed with it.</summary>
public sealed class WindowsCaptureSession(CaptureEngine engine, GlobalHotkeys? hotkeys) : IAsyncDisposable
{
    /// <summary>The engine (also <see cref="Capture.Current"/>).</summary>
    public CaptureEngine Engine { get; } = engine;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        hotkeys?.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Finds the MindAttic capture agent (capture.exe) for detached finalizing and recovery.</summary>
public static class AgentLocator
{
    /// <summary>Product name stamped on the agent exe; anything else called capture.exe is ignored.</summary>
    public const string ProductName = "MindAttic.Capture.Agent";

    /// <summary>Where tools/install.ps1 puts the agent.</summary>
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Capture", "bin");

    /// <summary>MINDATTIC_CAPTURE_AGENT, next to the app, the install folder, then PATH. Null when not installed.</summary>
    public static string? Find()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("MINDATTIC_CAPTURE_AGENT") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "capture.exe"));
        candidates.Add(Path.Combine(InstallDir, "capture.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "capture.exe"));
        return candidates.FirstOrDefault(IsAgent);
    }

    internal static bool IsAgent(string path)
    {
        try
        {
            return File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductName == ProductName;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The MindAttic Browser that capture drives: always the deployed copy in C:\Apps (published by
/// MindAttic.Browser's tools\deploy.ps1), never a dev build or an older install elsewhere.
/// </summary>
public static class BrowserLocator
{
    /// <summary>The deployed browser.</summary>
    public const string DeployedPath = @"C:\Apps\MindAttic.Browser\MindAttic.Browser.exe";

    /// <summary>The deployed browser, or a <see cref="CaptureException"/> saying how to deploy it.</summary>
    public static string Require(string path = DeployedPath) =>
        File.Exists(path)
            ? path
            : throw new CaptureException(CaptureError.TargetNotFound,
                $"MindAttic Browser is not deployed at {path}. Deploy it with MindAttic.Browser\\tools\\deploy.ps1 (or run its launch.bat).");
}

/// <summary>Bridges capture logging to Microsoft.Extensions.Logging.</summary>
public sealed class LoggerCaptureLog(ILogger logger) : ICaptureLog
{
    /// <inheritdoc/>
    public void Log(CaptureLogLevel level, string message, Exception? exception = null) =>
        logger.Log(level switch
        {
            CaptureLogLevel.Debug => LogLevel.Debug,
            CaptureLogLevel.Info => LogLevel.Information,
            CaptureLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error,
        }, exception, "{Message}", message);
}

/// <summary>DI registration.</summary>
public static class CaptureServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ICapture"/> as whatever engine is current (a no-op until a host starts one).</summary>
    public static IServiceCollection AddMindAtticCapture(this IServiceCollection services) =>
        services.AddSingleton<ICapture>(_ => new CurrentCapture());

    /// <summary>Registers a specific started engine.</summary>
    public static IServiceCollection AddMindAtticCapture(this IServiceCollection services, ICapture capture) =>
        services.AddSingleton(capture);

    /// <summary>Forwards to <see cref="Capture.Current"/> at call time, so injection works before and after init.</summary>
    private sealed class CurrentCapture : ICapture
    {
        private static ICapture C => Capture.Current;
        public bool Enabled => C.Enabled;
        public string SessionId => C.SessionId;
        public string SessionFolder => C.SessionFolder;
        public bool IsRecording => C.IsRecording;
        public CaptureDiagnostics Diagnostics => C.Diagnostics;
        public Triggers.ITriggerRegistry Triggers => C.Triggers;
        public event Action<CaptureResult>? Captured { add => C.Captured += value; remove => C.Captured -= value; }
        public event Action<double>? DiskWarning { add => C.DiskWarning += value; remove => C.DiskWarning -= value; }
        public Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null) => C.ScreenshotAsync(tag, options);
        public Task StartRecordingAsync(string tag, RecordingOptions? options = null) => C.StartRecordingAsync(tag, options);
        public Task<CaptureResult> StopRecordingAsync() => C.StopRecordingAsync();
        public void PauseRecording() => C.PauseRecording();
        public void ResumeRecording() => C.ResumeRecording();
        public Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null) => C.ClipAsync(duration, tag, options);
        public Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null) => C.SaveReplayAsync(before, after, tag, options);
        public void Marker(string name, CaptureData? data = null) => C.Marker(name, data);
        public void Raise(string eventName, CaptureData? data = null) => C.Raise(eventName, data);
        public Task<CaptureResult?> ExecuteCommandAsync(string command) => C.ExecuteCommandAsync(command);
        // The engine's lifetime belongs to the host that started it, not the container.
        public ValueTask DisposeAsync() => default;
    }
}
