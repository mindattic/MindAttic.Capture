using System.Diagnostics;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

internal enum RecorderState { Idle, Starting, Recording, Stopping }

/// <summary>One recording at a time: an engine is started for the chosen target and disposed when it stops.</summary>
internal sealed class Recorder : IAsyncDisposable
{
    private readonly Func<string, CaptureOptions> _options;
    private readonly Stopwatch _clock = new();
    private CaptureEngine? _engine;
    // The recording the engine exported itself when its shut-off valve stopped it.
    private volatile CaptureResult? _valveExport;

    public Recorder(Func<string, CaptureOptions> options) => _options = options;

    public RecorderState State { get; private set; }
    public CaptureTarget? Target { get; private set; }
    public string Label { get; private set; } = "";
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>Where the latest recording or screenshot went (its session .log is there too).</summary>
    public string? LastSessionFolder { get; private set; }

    public event Action? StateChanged;

    /// <summary>The shut-off valve stopped the recording (a resource limit was crossed); raised on a background thread.</summary>
    public event Action<string>? LimitReached;

    /// <summary>Starts recording <paramref name="target"/>; throws <see cref="CaptureException"/> when capture can't start.</summary>
    public async Task StartAsync(CaptureTarget target, string appName, string label, RecordingOptions recording)
    {
        if (State != RecorderState.Idle) return;
        SetState(RecorderState.Starting);
        try
        {
            _engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(target), _options(appName));
            LastSessionFolder = _engine.SessionFolder;
            _valveExport = null;
            _engine.Captured += r =>
            {
                if (r.Kind == CaptureKind.Recording) _valveExport = r;
            };
            _engine.LimitReached += reason => LimitReached?.Invoke(reason);
            await _engine.StartRecordingAsync(label, recording);
            if (!_engine.IsRecording)
                throw new CaptureException(CaptureError.CaptureFailed, $"Recording did not start; see the .log in {_engine.SessionFolder}.");
        }
        catch
        {
            if (_engine is not null) await _engine.DisposeAsync();
            _engine = null;
            SetState(RecorderState.Idle);
            throw;
        }
        Target = target;
        Label = label;
        _clock.Restart();
        SetState(RecorderState.Recording);
    }

    public async Task<CaptureResult?> StopAsync()
    {
        if (State != RecorderState.Recording || _engine is null) return null;
        SetState(RecorderState.Stopping);
        _clock.Stop();
        CaptureResult result;
        try
        {
            result = await _engine.StopRecordingAsync();
        }
        finally
        {
            await _engine.DisposeAsync();
            _engine = null;
            SetState(RecorderState.Idle);
        }
        // Stopped by the valve: the engine's own shutdown exported the recording, so this stop found nothing to stop.
        return result.Skipped && _valveExport is { } exported ? exported : result;
    }

    /// <summary>A one-off still of <paramref name="target"/>.</summary>
    public async Task<CaptureResult> ScreenshotAsync(CaptureTarget target, string appName, string label, ScreenshotOptions options)
    {
        await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(target), _options(appName));
        LastSessionFolder = engine.SessionFolder;
        return await engine.ScreenshotAsync(label, options);
    }

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (State == RecorderState.Recording) await StopAsync();
    }
}
