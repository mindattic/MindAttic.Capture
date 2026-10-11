using System.Diagnostics;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Triggers;

namespace MindAttic.Capture;

/// <summary>
/// The one capture API every host exposes. Calls are thread-safe and never block the calling
/// (UI or game) thread; failures come back in <see cref="CaptureResult.Error"/> rather than as exceptions.
/// When capture is disabled every call logs a warning and returns a skipped result.
/// </summary>
public interface ICapture : IAsyncDisposable
{
    /// <summary>False when the kill switch is off; calls become no-ops.</summary>
    bool Enabled { get; }

    /// <summary>Session id: the start timestamp (2026-10-11T14.29.20.123); names the session video and manifest.</summary>
    string SessionId { get; }

    /// <summary>The folder this app's captures go to, {root}\{RepoName} (created on first capture).</summary>
    string SessionFolder { get; }

    /// <summary>True between StartRecording and StopRecording.</summary>
    bool IsRecording { get; }

    /// <summary>Encoder, frame and overhead statistics.</summary>
    CaptureDiagnostics Diagnostics { get; }

    /// <summary>Code-defined trigger rules (capture.json rules are here too).</summary>
    ITriggerRegistry Triggers { get; }

    /// <summary>Raised after every capture (success or failure). Runs on a background thread.</summary>
    event Action<CaptureResult>? Captured;

    /// <summary>Raised once when the app's capture folder passes <see cref="CaptureOptions.DiskWarnGB"/>.</summary>
    event Action<double>? DiskWarning;

    /// <summary>Takes a screenshot.</summary>
    Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null);

    /// <summary>Starts recording. Footage is cut from the tape, so starting is instant.</summary>
    Task StartRecordingAsync(string tag, RecordingOptions? options = null);

    /// <summary>Stops recording and exports the footage (pauses removed).</summary>
    Task<CaptureResult> StopRecordingAsync();

    /// <summary>Excludes footage from the current recording until <see cref="ResumeRecording"/>.</summary>
    void PauseRecording();

    /// <summary>Resumes a paused recording.</summary>
    void ResumeRecording();

    /// <summary>Records <paramref name="duration"/> starting now; completes when the clip is written.</summary>
    Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null);

    /// <summary>Saves footage from <paramref name="before"/> the call to <paramref name="after"/> it (needs the replay buffer armed for the past part).</summary>
    Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null);

    /// <summary>Drops a timestamped marker; it appears in sidecars and as a chapter in exported video.</summary>
    void Marker(string name, CaptureData? data = null);

    /// <summary>Raises a named event for the trigger rules. Returns immediately.</summary>
    void Raise(string eventName, CaptureData? data = null);

    /// <summary>Runs a hotkey-style command: screenshot, toggle-recording, start-recording, stop-recording, save-replay, clip, marker.</summary>
    Task<CaptureResult?> ExecuteCommandAsync(string command);
}

/// <summary>Live statistics, also written to the session manifest.</summary>
public sealed class CaptureDiagnostics
{
    /// <summary>Host adapter name.</summary>
    public string Host { get; internal set; } = "";
    /// <summary>How the tape grabs frames (e.g. ddagrab, wgc, unity-readback).</summary>
    public string? VideoSource { get; internal set; }
    /// <summary>Tape encoder.</summary>
    public string? TapeEncoder { get; internal set; }
    /// <summary>H.264 encoder used for MP4s.</summary>
    public string? Mp4Encoder { get; internal set; }
    /// <summary>True while the tape runs.</summary>
    public bool TapeRunning { get; internal set; }
    /// <summary>Frames pushed into the tape (push sources).</summary>
    public long FramesWritten { get; internal set; }
    /// <summary>Frames dropped because the encoder fell behind (real-time push sources).</summary>
    public long FramesDropped { get; internal set; }
    /// <summary>Average time to grab a still, in milliseconds.</summary>
    public double AverageStillMs { get; internal set; }
    /// <summary>Slowest still grab, in milliseconds.</summary>
    public double MaxStillMs { get; internal set; }
    /// <summary>Screenshots taken.</summary>
    public int Screenshots { get; internal set; }
    /// <summary>Videos exported.</summary>
    public int Videos { get; internal set; }
    /// <summary>Captures that failed.</summary>
    public int Failures { get; internal set; }
    /// <summary>Events raised.</summary>
    public long EventsRaised { get; internal set; }

    internal Dictionary<string, object?> ToDictionary() => new()
    {
        ["host"] = Host, ["videoSource"] = VideoSource, ["tapeEncoder"] = TapeEncoder, ["mp4Encoder"] = Mp4Encoder,
        ["framesWritten"] = FramesWritten, ["framesDropped"] = FramesDropped,
        ["averageStillMs"] = Math.Round(AverageStillMs, 2), ["maxStillMs"] = Math.Round(MaxStillMs, 2),
        ["screenshots"] = Screenshots, ["videos"] = Videos, ["failures"] = Failures, ["eventsRaised"] = EventsRaised,
    };
}

/// <summary>
/// Static entry points for game/app code. Calls compile away unless MINDATTIC_CAPTURE is defined
/// (the package defines it by default; set MindAtticCaptureStrip=true to strip a build).
/// </summary>
public static class Capture
{
    private static ICapture _current = NullCapture.Instance;

    /// <summary>The active capture engine (a no-op instance until a host initializes one).</summary>
    public static ICapture Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? NullCapture.Instance);
    }

    /// <summary>Raises a named event (e.g. "critical-hit") for the trigger rules.</summary>
    [Conditional("MINDATTIC_CAPTURE")]
    public static void Raise(string eventName, CaptureData? data = null) => Current.Raise(eventName, data);

    /// <summary>Drops a marker.</summary>
    [Conditional("MINDATTIC_CAPTURE")]
    public static void Marker(string name, CaptureData? data = null) => Current.Marker(name, data);

    /// <summary>Takes a screenshot (fire and forget).</summary>
    [Conditional("MINDATTIC_CAPTURE")]
    public static void Screenshot(string tag, ScreenshotOptions? options = null) => _ = Current.ScreenshotAsync(tag, options);
}

/// <summary>Capture that does nothing; used when capture is disabled, stripped or not initialized.</summary>
public sealed class NullCapture : ICapture
{
    /// <summary>Shared instance.</summary>
    public static readonly NullCapture Instance = new();

    private readonly TriggerEngine _triggers = new((_, _) => { }, _ => { }, new TraceCaptureLog());

    /// <inheritdoc/>
    public bool Enabled => false;
    /// <inheritdoc/>
    public string SessionId => "";
    /// <inheritdoc/>
    public string SessionFolder => "";
    /// <inheritdoc/>
    public bool IsRecording => false;
    /// <inheritdoc/>
    public CaptureDiagnostics Diagnostics { get; } = new();
    /// <inheritdoc/>
    public ITriggerRegistry Triggers => _triggers;

    /// <inheritdoc/>
    public event Action<CaptureResult>? Captured { add { } remove { } }
    /// <inheritdoc/>
    public event Action<double>? DiskWarning { add { } remove { } }

    private static Task<CaptureResult> Skip(CaptureKind kind, string tag) =>
        Task.FromResult(CaptureResult.Skip(kind, tag, "Capture is disabled."));

    /// <inheritdoc/>
    public Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null) => Skip(CaptureKind.Screenshot, tag);
    /// <inheritdoc/>
    public Task StartRecordingAsync(string tag, RecordingOptions? options = null) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task<CaptureResult> StopRecordingAsync() => Skip(CaptureKind.Recording, "");
    /// <inheritdoc/>
    public void PauseRecording() { }
    /// <inheritdoc/>
    public void ResumeRecording() { }
    /// <inheritdoc/>
    public Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null) => Skip(CaptureKind.Clip, tag);
    /// <inheritdoc/>
    public Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null) => Skip(CaptureKind.Replay, tag);
    /// <inheritdoc/>
    public void Marker(string name, CaptureData? data = null) { }
    /// <inheritdoc/>
    public void Raise(string eventName, CaptureData? data = null) { }
    /// <inheritdoc/>
    public Task<CaptureResult?> ExecuteCommandAsync(string command) => Task.FromResult<CaptureResult?>(null);
    /// <inheritdoc/>
    public ValueTask DisposeAsync() => default;
}
