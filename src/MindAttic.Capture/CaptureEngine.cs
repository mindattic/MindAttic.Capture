using System.Collections.Concurrent;
using System.Diagnostics;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Imaging;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;
using MindAttic.Capture.Triggers;

namespace MindAttic.Capture;

/// <summary>
/// The capture engine behind every host. Video always comes from one running tape (see
/// <see cref="CaptureTape"/>); recordings, clips, replays and the session are ranges cut from it.
/// </summary>
public sealed class CaptureEngine : ICapture
{
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(30);

    private readonly CaptureOptions _options;
    private readonly ICaptureHost _host;
    private readonly ICaptureLog _log;
    private readonly SessionLogFile _logFile;
    private readonly ICaptureJson _json;
    private readonly OutputManager _output;
    private readonly TriggerEngine _triggers;
    private readonly SemaphoreSlim _tapeGate = new(1, 1);
    private readonly object _recGate = new();
    private readonly ConcurrentDictionary<int, double> _leases = new();
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly ConcurrentQueue<(CaptureEvent Event, DateTimeOffset At)> _events = new();
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _drain = new();
    private readonly Lazy<Task<EncoderSet>> _encoders;
    private readonly ConcurrentDictionary<string, byte> _warnedOnce = new();
    private Task? _eventWorker;
    private Task? _intervalLoop;
    private ResourceValve? _valve;
    private Task? _valveLoop;
    private CaptureTape? _tape;
    private int _tapeCount;
    private int _leaseIds;
    private string? _ffmpeg;
    private int _started, _disposed, _valveStarted;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private volatile bool _closing;
    private double _stillMsTotal;

    private string? _recTag;
    private Task? _recStart;
    private RecordingOptions? _recOptions;
    private DateTimeOffset _recStartedAt;
    private readonly List<(double Start, double? End)> _recRanges = [];
    private int _recLease = -1;
    // The tape a recording's ranges are offsets into. The engine's current tape (_tape) can be replaced
    // mid-recording if its encoder dies; without this, Pause/Resume/Stop would cut the wrong tape's footage.
    private CaptureTape? _recTape;

    /// <summary>Creates an engine; call <see cref="StartAsync"/> (or use <see cref="StartNewAsync"/>).</summary>
    public CaptureEngine(ICaptureHost host, CaptureOptions options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = _logFile = new SessionLogFile(options.Log);
        _json = options.Json ?? throw new CaptureException(CaptureError.InvalidArgument, "CaptureOptions.Json must be set on this platform (netstandard2.1).");
        _output = new OutputManager(options, host.Name, _json, _log);
        _output.DiskWarning += gb => DiskWarning?.Invoke(gb);
        _triggers = new TriggerEngine(OnRuleFired, name => Raise(name), _log);
        _encoders = new Lazy<Task<EncoderSet>>(() => EncoderProbe.ProbeAsync(Ffmpeg, _log));
        Diagnostics.Host = host.Name;
    }

    /// <summary>Creates, starts and registers an engine as <see cref="Capture.Current"/>.</summary>
    public static async Task<CaptureEngine> StartNewAsync(ICaptureHost host, CaptureOptions options, CancellationToken ct = default)
    {
        var engine = new CaptureEngine(host, options);
        await engine.StartAsync(ct).ConfigureAwait(false);
        Capture.Current = engine;
        return engine;
    }

    /// <inheritdoc/>
    public bool Enabled => _options.Enabled && Volatile.Read(ref _disposed) == 0;
    /// <inheritdoc/>
    public string SessionId => _output.SessionId;
    /// <inheritdoc/>
    public string SessionFolder => _output.SessionDir;
    /// <inheritdoc/>
    public bool IsRecording { get { lock (_recGate) return _recTag is not null || _recStart is not null; } }
    /// <inheritdoc/>
    public CaptureDiagnostics Diagnostics { get; } = new();
    /// <inheritdoc/>
    public ITriggerRegistry Triggers => _triggers;
    /// <inheritdoc/>
    public event Action<CaptureResult>? Captured;
    /// <inheritdoc/>
    public event Action<double>? DiskWarning;

    /// <summary>The options this engine runs with.</summary>
    public CaptureOptions Options => _options;

    private string Ffmpeg => _ffmpeg ??= FfmpegLocator.Find(_options.FfmpegPath);

    /// <summary>Validates options, loads rules, and starts the tape when session recording or the replay buffer is on.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            _options.Validate();
            foreach (var rule in _options.Rules) _triggers.Add(TriggerRule.FromConfig(rule));
        }
        catch
        {
            // Nothing started (no event worker, no tape); let a retry on this instance try again.
            Volatile.Write(ref _started, 0);
            throw;
        }
        _eventWorker = Task.Run(ProcessEventsAsync);
        if (!_options.Enabled)
        {
            _log.Warn("Capture is disabled (kill switch); all capture calls are no-ops.");
            return;
        }
        if (_options.SessionRecording || _options.ReplayArmed) _ = Task.Run(RecoverOrphansAsync);
        if (_options.ScreenshotInterval is { } interval)
        {
            _intervalLoop = Task.Run(() => IntervalScreenshotsAsync(interval));
            _log.Info($"Taking a screenshot every {interval.TotalSeconds:0.###}s.");
        }
        if (_options.SessionRecording || _options.ReplayArmed)
        {
            var tape = await EnsureTapeAsync(ct).ConfigureAwait(false);
            if (tape is null && _options.SessionRecording)
                _log.Error("Session recording was requested but the tape could not start; screenshots still work.");
            if (_options.SessionRecording)
            {
                _output.SaveManifest();
                _log.Info($"Recording this session to {_output.SessionDir}");
            }
        }
        if (_tape is not null || _intervalLoop is not null) StartValve();
    }

    // ---------------------------------------------------------------- shut-off valve

    /// <summary>Raised when the shut-off valve stops capture because a resource limit was crossed; the argument says which.</summary>
    public event Action<string>? LimitReached;

    /// <summary>Starts the valve and the session log once, for the first tape or screenshot loop (on-demand tapes included).</summary>
    private void StartValve()
    {
        if (Interlocked.Exchange(ref _valveStarted, 1) != 0) return;
        _logFile.Open(_output.SessionStem + ".log");
        _valve = new ResourceValve(_options.Limits, _log, () => _tape, _output.Root, _output.SessionStem + ".stats.csv");
        _valveLoop = Task.Run(RunValveAsync);
    }

    private async Task RunValveAsync()
    {
        try
        {
            var reason = await _valve!.RunAsync(_shutdown.Token).ConfigureAwait(false);
            if (reason is null) return;
            Diagnostics.StoppedBy = reason;
            try
            {
                LimitReached?.Invoke(reason);
            }
            catch (Exception ex)
            {
                _log.Warn("A LimitReached handler threw.", ex);
            }
            // Stop and export exactly as if the app had closed; the app keeps running, later capture calls are no-ops.
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("The capture valve could not stop capture cleanly.", ex);
        }
    }

    // ---------------------------------------------------------------- screenshots

    /// <inheritdoc/>
    public Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null) =>
        Guard(CaptureKind.Screenshot, tag) ?? Track(Task.Run(() => ScreenshotCoreAsync(tag, options ?? new ScreenshotOptions(), null, null)));

    private async Task<CaptureResult> ScreenshotCoreAsync(string tag, ScreenshotOptions options, string? trigger, CaptureData? eventData)
    {
        var at = DateTimeOffset.Now;
        try
        {
            WarnUnsupported(options);
            var sw = Stopwatch.StartNew();
            var image = await _host.CaptureStillAsync(options, _shutdown.Token).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            lock (Diagnostics)
            {
                Diagnostics.Screenshots++;
                _stillMsTotal += ms;
                Diagnostics.AverageStillMs = _stillMsTotal / Diagnostics.Screenshots;
                Diagnostics.MaxStillMs = Math.Max(Diagnostics.MaxStillMs, ms);
            }
            var file = _output.NextStem(CaptureKind.Screenshot, tag) + ImageWriter.Extension(options.Format);
            await WriteImageAsync(image, options, file).ConfigureAwait(false);
            var result = new CaptureResult
            {
                Kind = CaptureKind.Screenshot, Tag = tag, Files = [file], Timestamp = at, Trigger = trigger,
            };
            _output.WriteSidecar(file, Sidecar(result, Merge(eventData, options.Data), image.Width, image.Height, null, null, [], null, null));
            return Complete(result);
        }
        catch (Exception ex)
        {
            return Complete(Failed(CaptureKind.Screenshot, tag, at, trigger, ex));
        }
    }

    private async Task WriteImageAsync(CapturedImage image, ScreenshotOptions options, string file)
    {
        switch (options.Format)
        {
            case ImageFormat.Png:
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) ImageWriter.WritePng(image, fs);
                break;
            case ImageFormat.Bmp:
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) ImageWriter.WriteBmp(image, fs);
                break;
            case ImageFormat.RawBgra:
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) fs.Write(image.Bgra, 0, image.Bgra.Length);
                File.WriteAllText(file + ".layout.json", _json.Serialize(new Dictionary<string, object>
                {
                    ["width"] = image.Width, ["height"] = image.Height, ["stride"] = image.Width * 4, ["pixelFormat"] = "bgra8",
                }));
                break;
            default:
                await FfmpegProcess.RunAsync(Ffmpeg, FfmpegArgs.StillFromRaw(image.Width, image.Height, options.Format, options.Quality, file),
                    _log, _shutdown.Token, image.Bgra).ConfigureAwait(false);
                break;
        }
    }

    private void WarnUnsupported(ScreenshotOptions o)
    {
        var caps = _host.Capabilities;
        if (o.TransparentBackground && !caps.HasFlag(HostCapabilities.Transparency)) WarnOnce($"The {_host.Name} host cannot capture transparent backgrounds; ignoring.");
        if (o.ExcludeUi && !caps.HasFlag(HostCapabilities.ExcludeUi)) WarnOnce($"The {_host.Name} host cannot exclude UI layers; ignoring.");
        if (o.Supersample > 1 && !caps.HasFlag(HostCapabilities.Supersample)) WarnOnce($"The {_host.Name} host cannot supersample; capturing at native size.");
        if (o.TransparentBackground && o.Format != ImageFormat.Png && o.Format != ImageFormat.RawBgra) WarnOnce("Transparency needs PNG or RawBgra; the background will be opaque.");
    }

    /// <summary>One screenshot per interval until shutdown; a slow grab skips ticks instead of queueing them.</summary>
    private async Task IntervalScreenshotsAsync(TimeSpan interval)
    {
        var clock = Stopwatch.StartNew();
        long tick = 0;
        while (!_drain.IsCancellationRequested)
        {
            tick++;
            var wait = TimeSpan.FromTicks(interval.Ticks * tick) - clock.Elapsed;
            if (wait < TimeSpan.Zero)
            {
                tick = clock.Elapsed.Ticks / interval.Ticks + 1;
                wait = TimeSpan.FromTicks(interval.Ticks * tick) - clock.Elapsed;
            }
            try
            {
                await Task.Delay(wait, _drain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await ScreenshotCoreAsync("screen", new ScreenshotOptions(), null, null).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- video

    /// <inheritdoc/>
    public Task StartRecordingAsync(string tag, RecordingOptions? options = null)
    {
        if (Guard(CaptureKind.Recording, tag) is not null) return Task.CompletedTask;
        lock (_recGate)
        {
            // A start still waiting for the tape counts as recording, so a quick second toggle stops it instead of starting twice.
            if (_recTag is not null || _recStart is not null)
            {
                _log.Warn($"Already recording \"{_recTag ?? "(starting)"}\"; StartRecording(\"{tag}\") ignored.");
                return Task.CompletedTask;
            }
            var start = Track(BeginRecordingAsync(tag, options));
            _recStart = start.IsCompleted ? null : start;
            return start;
        }
    }

    private async Task<bool> BeginRecordingAsync(string tag, RecordingOptions? options)
    {
        try
        {
            var tape = await EnsureTapeAsync(_shutdown.Token).ConfigureAwait(false);
            if (tape is null) return false;
            BeginRecording(tape, tag, options);
            return true;
        }
        finally
        {
            lock (_recGate) _recStart = null;
        }
    }

    private void BeginRecording(CaptureTape tape, string tag, RecordingOptions? options)
    {
        lock (_recGate)
        {
            _recTag = tag;
            _recTape = tape;
            _recOptions = options;
            _recStartedAt = DateTimeOffset.Now;
            _recRanges.Clear();
            var now = tape.Now;
            _recRanges.Add((now, null));
            _recLease = Lease(now);
        }
        _log.Info($"Recording \"{tag}\" started.");
    }

    /// <inheritdoc/>
    public void PauseRecording()
    {
        lock (_recGate)
        {
            if (_recTag is null || _recTape is null || _recRanges.Count == 0 || _recRanges[_recRanges.Count - 1].End is not null) return;
            var last = _recRanges[_recRanges.Count - 1];
            _recRanges[_recRanges.Count - 1] = (last.Start, _recTape.Now);
        }
    }

    /// <inheritdoc/>
    public void ResumeRecording()
    {
        lock (_recGate)
        {
            if (_recTag is null || _recTape is null || _recRanges.Count == 0 || _recRanges[_recRanges.Count - 1].End is null) return;
            _recRanges.Add((_recTape.Now, null));
        }
    }

    /// <inheritdoc/>
    public Task<CaptureResult> StopRecordingAsync() => Track(StopRecordingCoreAsync(null));

    private async Task<CaptureResult> StopRecordingCoreAsync(string? trigger)
    {
        string tag;
        RecordingOptions? options;
        DateTimeOffset startedAt;
        List<(double, double)> ranges;
        int lease;
        CaptureTape? tape;
        Task? starting;
        lock (_recGate) starting = _recStart;
        if (starting is not null) await starting.ConfigureAwait(false);
        lock (_recGate)
        {
            tape = _recTape;
            if (_recTag is null || tape is null) return CaptureResult.Skip(CaptureKind.Recording, "", "Not recording.");
            tag = _recTag;
            options = _recOptions;
            startedAt = _recStartedAt;
            var now = tape.Now;
            ranges = _recRanges.Select(r => (r.Start, r.End ?? now)).Where(r => r.Item2 > r.Item1).ToList();
            lease = _recLease;
            _recTag = null;
            _recTape = null;
            _recRanges.Clear();
            _recLease = -1;
        }
        try
        {
            return await ExportRangesAsync(tape, CaptureKind.Recording, tag, ranges, options, trigger, null, startedAt).ConfigureAwait(false);
        }
        finally
        {
            Release(lease);
        }
    }

    /// <inheritdoc/>
    public Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null) =>
        Guard(CaptureKind.Clip, tag) ?? Track(WindowAsync(CaptureKind.Clip, TimeSpan.Zero, duration, tag, options, null, null, null));

    /// <inheritdoc/>
    public Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null) =>
        Guard(CaptureKind.Replay, tag) ?? Track(WindowAsync(CaptureKind.Replay, before, after, tag, options, null, null, null));

    /// <summary>Footage from anchor - before to anchor + after (anchor = now unless an event supplied its tape time).</summary>
    private async Task<CaptureResult> WindowAsync(CaptureKind kind, TimeSpan before, TimeSpan after, string tag,
        RecordingOptions? options, string? trigger, double? anchor, CaptureData? eventData)
    {
        var at = DateTimeOffset.Now;
        var tape = await EnsureTapeAsync(_shutdown.Token).ConfigureAwait(false);
        if (tape is null) return Complete(CaptureResult.Fail(kind, tag, "The capture tape is not running.", at), count: false);
        var t = anchor ?? tape.Now;
        var start = Math.Max(0, t - before.TotalSeconds);
        var end = t + after.TotalSeconds;
        // Footage before this floor is already swept by retention (or never kept, without ReplayArmed/a covering
        // rule); comparing against the tape's total runtime alone misses that and silently exports less than asked.
        if (kind == CaptureKind.Replay && before > TimeSpan.Zero && start < KeepFrom())
            WarnOnce($"SaveReplay asked for footage starting at {start:0.0}s, but only footage from about {KeepFrom():0.0}s onward is retained; " +
                     "arm ReplayArmed (or add a trigger rule covering this much) to keep more.");
        var lease = Lease(start);
        try
        {
            return await ExportRangesAsync(tape, kind, tag, [(start, end)], options, trigger, eventData, at).ConfigureAwait(false);
        }
        finally
        {
            Release(lease);
        }
    }

    private async Task<CaptureResult> ExportRangesAsync(CaptureTape tape, CaptureKind kind, string tag, List<(double Start, double End)> ranges,
        RecordingOptions? options, string? trigger, CaptureData? eventData, DateTimeOffset at)
    {
        try
        {
            if (ranges.Count == 0) return Complete(CaptureResult.Fail(kind, tag, "No footage in the requested range.", at), count: false);
            var end = ranges.Max(r => r.End);
            var waitFor = TimeSpan.FromSeconds(Math.Max(0, end - tape.Now)) + TimeSpan.FromSeconds(10);
            if (!await tape.WaitForCoverageAsync(end, waitFor, _shutdown.Token).ConfigureAwait(false))
                _log.Warn($"{kind} \"{tag}\": the tape did not reach {end:0.0}s; exporting what exists.");

            var preset = options?.Preset ?? CapturePreset.Trailer;
            var spec = PresetSpec.For(preset, options);
            var stem = _output.NextStem(kind, tag);
            var exporter = new Exporter(Ffmpeg, () => _encoders.Value, _log);
            var markers = _output.MarkersSnapshot();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(ExportTimeout);
            var export = await exporter.ExportAsync(new ExportJob(tape.Dir, tape.State, tape.Segments(), ranges, stem, spec, markers,
                options?.SeparateAudioTracks ?? false), timeout.Token).ConfigureAwait(false);
            Diagnostics.Mp4Encoder = _encoders.IsValueCreated && _encoders.Value.IsCompleted ? _encoders.Value.Result.H264 : Diagnostics.Mp4Encoder;

            var result = new CaptureResult
            {
                Kind = kind, Tag = tag, Files = export.Files, Timestamp = at, Trigger = trigger,
                Duration = TimeSpan.FromSeconds(export.DurationSeconds),
            };
            _output.WriteSidecar(export.Files[0], Sidecar(result, Merge(eventData, options?.Data), tape.State.Width, tape.State.Height,
                ranges.Min(r => r.Start), end, export.Markers, preset.ToString(), tape.State.Encoder));
            lock (Diagnostics) Diagnostics.Videos++;
            return Complete(result);
        }
        catch (Exception ex)
        {
            return Complete(Failed(kind, tag, at, trigger, ex));
        }
    }

    // ---------------------------------------------------------------- markers, events, commands

    /// <inheritdoc/>
    public void Marker(string name, CaptureData? data = null)
    {
        if (!Enabled) { WarnOnce("Capture is disabled; Marker ignored."); return; }
        _output.AddMarker(new MarkerRecord { Name = name, At = DateTimeOffset.Now, TapeSeconds = _tape?.Now, Data = data });
    }

    /// <inheritdoc/>
    public void Raise(string eventName, CaptureData? data = null)
    {
        if (!Enabled) { WarnOnce("Capture is disabled; Raise ignored."); return; }
        if (string.IsNullOrWhiteSpace(eventName)) return;
        var now = DateTimeOffset.Now;
        _events.Enqueue((new CaptureEvent(eventName.Trim(), data, now, _tape?.Now), now));
        _eventSignal.Release();
    }

    private async Task ProcessEventsAsync()
    {
        while (true)
        {
            try
            {
                await _eventSignal.WaitAsync(_drain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_events.IsEmpty) return;
            }
            while (_events.TryDequeue(out var item))
            {
                var e = item.Event;
                lock (Diagnostics) Diagnostics.EventsRaised++;
                _output.AddEvent(new ManifestEvent { Name = e.Name, At = e.Timestamp, TapeSeconds = e.TapeSeconds, Data = e.Data });
                try
                {
                    _triggers.Process(e);
                }
                catch (Exception ex)
                {
                    _log.Error($"Trigger processing for \"{e.Name}\" failed.", ex);
                }
            }
        }
    }

    private void OnRuleFired(TriggerRule rule, CaptureEvent e)
    {
        if (_closing)
        {
            _log.Debug($"Rule {rule.EventPattern} fired after shutdown began; ignored.");
            return;
        }
        var tag = rule.Tag ?? e.Name;
        var recOptions = rule.Preset is { } p ? new RecordingOptions { Preset = p, Data = e.Data } : new RecordingOptions { Data = e.Data };
        _log.Debug($"Rule {rule.EventPattern} -> {rule.Action} ({tag}).");
        switch (rule.Action)
        {
            case TriggerAction.Screenshot:
                Track(Task.Run(() => ScreenshotCoreAsync(tag, new ScreenshotOptions { Format = rule.Format ?? ImageFormat.Png }, e.Name, e.Data)));
                break;
            case TriggerAction.Replay:
                Track(WindowAsync(CaptureKind.Replay, rule.Before, rule.After, tag, recOptions, e.Name, e.TapeSeconds, e.Data));
                break;
            case TriggerAction.Clip:
                Track(WindowAsync(CaptureKind.Clip, TimeSpan.Zero, rule.Duration, tag, recOptions, e.Name, e.TapeSeconds, e.Data));
                break;
            case TriggerAction.Marker:
                Marker(tag, e.Data);
                break;
            case TriggerAction.StartRecording:
                Track(StartRecordingAsync(tag, recOptions));
                break;
            case TriggerAction.StopRecording:
                Track(StopRecordingCoreAsync(e.Name));
                break;
            case TriggerAction.ToggleRecording:
                Track(IsRecording ? StopRecordingCoreAsync(e.Name) : StartRecordingAsync(tag, recOptions));
                break;
            case TriggerAction.Custom when rule.Callback is not null:
                Track(Task.Run(() => rule.Callback(this, e)));
                break;
        }
    }

    /// <inheritdoc/>
    public async Task<CaptureResult?> ExecuteCommandAsync(string command)
    {
        switch ((command ?? "").Trim().ToLowerInvariant())
        {
            case "screenshot":
                return await ScreenshotAsync("hotkey").ConfigureAwait(false);
            case "toggle-recording":
                if (IsRecording) return await StopRecordingAsync().ConfigureAwait(false);
                await StartRecordingAsync("hotkey").ConfigureAwait(false);
                return null;
            case "start-recording":
                await StartRecordingAsync("hotkey").ConfigureAwait(false);
                return null;
            case "stop-recording":
                return await StopRecordingAsync().ConfigureAwait(false);
            case "save-replay":
                return await SaveReplayAsync(_options.ReplayLength, TimeSpan.Zero, "hotkey").ConfigureAwait(false);
            case "clip":
                return await ClipAsync(TimeSpan.FromSeconds(10), "hotkey").ConfigureAwait(false);
            case "marker":
                Marker("hotkey");
                return null;
            default:
                _log.Warn($"Unknown capture command \"{command}\".");
                return null;
        }
    }

    // ---------------------------------------------------------------- tape lifecycle

    private async Task<CaptureTape?> EnsureTapeAsync(CancellationToken ct)
    {
        var tape = _tape;
        if (tape is { IsRunning: true }) return tape;
        try
        {
            await _tapeGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            if (_tape is { IsRunning: true }) return _tape;
            if (_tape is { } dead)
            {
                _log.Error("The capture tape stopped unexpectedly; starting a new one.");
                await dead.StopAsync().ConfigureAwait(false);
                // Its footage is part of the session: export it now rather than leaving it in staging.
                if (dead.State.SessionRecording) await FinalizeSessionTapeAsync(dead, dead.Now, wait: false).ConfigureAwait(false);
            }
            var encoder = "ffv1";
            if (_options.TapeQuality == TapeQuality.HighQuality)
                encoder = (await _encoders.Value.ConfigureAwait(false)).Hevc;
            var number = Interlocked.Increment(ref _tapeCount);
            var dir = Path.Combine(_output.StagingDir, $"tape{number}");
            using var me = Process.GetCurrentProcess();
            var state = new TapeState
            {
                AppName = _options.AppName, AppVersion = _options.AppVersion, SessionId = _output.SessionId,
                SessionDir = _output.SessionDir, SessionStem = _output.SessionStem,
                OutputStem = number == 1 ? _output.VideoStem : $"{_output.VideoStem}_part{number}",
                FfmpegPath = Ffmpeg, CreatedAt = DateTimeOffset.Now, Fps = _options.Fps, Quality = _options.TapeQuality, Encoder = encoder,
                SessionRecording = _options.SessionRecording, SessionOutputs = _options.SessionOutputs,
                OwnerPid = me.Id, OwnerStartTicks = me.StartTime.ToUniversalTime().Ticks,
            };
            var source = _host.CreateVideoSource(_options.Fps);
            var audio = _options.Audio == AudioMode.None ? [] : _host.CreateAudioTracks(_options.Audio);
            var started = await CaptureTape.StartAsync(dir, state, source, audio, _log, _json, ct).ConfigureAwait(false);
            started.SetRetention(KeepFrom);
            _tape = started;
            Diagnostics.VideoSource = state.Source;
            Diagnostics.TapeEncoder = encoder;
            Diagnostics.TapeRunning = true;
            _output.Manifest.TapeQuality = state.Quality.ToString();
            _output.Manifest.TapeSource = state.Source;
            _output.Manifest.Encoder = encoder;
            StartValve();
            return started;
        }
        catch (Exception ex)
        {
            _log.Error("Could not start the capture tape.", ex);
            lock (Diagnostics) Diagnostics.Failures++;
            return null;
        }
        finally
        {
            _tapeGate.Release();
        }
    }

    /// <summary>Retention floor: the replay window and every active lease.</summary>
    private double KeepFrom()
    {
        var tape = _tape;
        if (tape is null) return 0;
        // Keep enough for the longest replay rule too: its lease is only taken when it fires, by which time
        // older segments would already be gone.
        var back = _options.ReplayArmed ? _options.ReplayLength.TotalSeconds : 0;
        foreach (var rule in _triggers.Rules)
            if (rule.Action == TriggerAction.Replay) back = Math.Max(back, rule.Before.TotalSeconds);
        var floor = tape.Now - back - 2;
        foreach (var lease in _leases.Values) floor = Math.Min(floor, lease);
        return floor;
    }

    private int Lease(double from)
    {
        var id = Interlocked.Increment(ref _leaseIds);
        _leases[id] = from;
        return id;
    }

    private void Release(int id)
    {
        if (id >= 0) _leases.TryRemove(id, out _);
    }

    // ---------------------------------------------------------------- results

    private Task<CaptureResult>? Guard(CaptureKind kind, string tag)
    {
        if (Volatile.Read(ref _started) == 0)
            return Task.FromResult(CaptureResult.Skip(kind, tag, "Capture engine not started; call StartAsync first."));
        if (Enabled) return null;
        WarnOnce($"Capture is disabled; {kind} \"{tag}\" ignored.");
        return Task.FromResult(CaptureResult.Skip(kind, tag, "Capture is disabled."));
    }

    private CaptureResult Failed(CaptureKind kind, string tag, DateTimeOffset at, string? trigger, Exception ex)
    {
        _log.Error($"{kind} \"{tag}\" failed: {ex.Message}", ex is CaptureException ? null : ex);
        return CaptureResult.Fail(kind, tag, ex.Message, at) with { Trigger = trigger };
    }

    private CaptureResult Complete(CaptureResult result, bool count = true)
    {
        if (result.Error is not null && !result.Skipped && count) lock (Diagnostics) Diagnostics.Failures++;
        if (result.Succeeded) _log.Info($"{result.Kind} \"{result.Tag}\" -> {result.PrimaryFile}");
        _output.AddCapture(result);
        try
        {
            Captured?.Invoke(result);
        }
        catch (Exception ex)
        {
            _log.Warn("A Captured handler threw.", ex);
        }
        if (result.Succeeded) PostCaptureRunner.Run(_options.PostCapture, result, SessionId, _log);
        return result;
    }

    private CaptureSidecar Sidecar(CaptureResult r, CaptureData? data, int? width, int? height, double? tapeStart, double? tapeEnd,
        IReadOnlyList<SidecarMarker> markers, string? preset, string? encoder) => new()
    {
        Kind = r.Kind.ToString().ToLowerInvariant(), Tag = r.Tag, App = _options.AppName, AppVersion = _options.AppVersion,
        Session = SessionId, Host = _host.Name, Timestamp = r.Timestamp, Trigger = r.Trigger, DurationSeconds = r.Duration.TotalSeconds,
        TapeStart = tapeStart, TapeEnd = tapeEnd, Width = width, Height = height, Preset = preset, Encoder = encoder,
        Files = r.Files.Select(Path.GetFileName).ToList()!, Markers = markers.ToList(), Data = data,
    };

    private static CaptureData? Merge(CaptureData? a, CaptureData? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var merged = new CaptureData(a);
        foreach (var kv in b) merged[kv.Key] = kv.Value;
        return merged;
    }

    private Task<T> Track<T>(Task<T> task)
    {
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
        return task;
    }

    private void Track(Task task)
    {
        _pending[task] = 0;
        _ = task.ContinueWith(t =>
        {
            _pending.TryRemove(t, out _);
            if (t.Exception is { } ex) _log.Error("A triggered capture failed.", ex.GetBaseException());
        }, TaskScheduler.Default);
    }

    private void WarnOnce(string message)
    {
        if (_warnedOnce.TryAdd(message, 0)) _log.Warn(message);
    }

    private async Task RecoverOrphansAsync()
    {
        try
        {
            if (_options.DetachedFinalizerPath is { } agent && File.Exists(agent))
                SessionFinalizer.SpawnDetached(agent, ["recover", _output.Root], _log);
            else
                await SessionFinalizer.RecoverAsync(_output.Root, _json, _log, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Recovering earlier crashed sessions failed.", ex);
        }
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>
    /// Stops capture: finishes the active recording and pending exports, then (in session mode)
    /// hands the session export to the detached finalizer or runs it in-process.
    /// </summary>
    /// <remarks>Every caller (the app, the shut-off valve) awaits the same shutdown, so a second call waits for the export too.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (ReferenceEquals(Capture.Current, this)) Capture.Current = NullCapture.Instance;
        _triggers.Dispose();
        // Events raised just before shutdown still run their rules; drain them before waiting on captures.
        _drain.Cancel();
        if (_eventWorker is not null) await _eventWorker.CompletesWithin(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // The worker can race a concurrent Raise() right at cancellation and exit just before it enqueues;
        // catch anything left over here instead of losing it.
        while (_events.TryDequeue(out var leftover))
        {
            var e = leftover.Event;
            lock (Diagnostics) Diagnostics.EventsRaised++;
            _output.AddEvent(new ManifestEvent { Name = e.Name, At = e.Timestamp, TapeSeconds = e.TapeSeconds, Data = e.Data });
            try
            {
                _triggers.Process(e);
            }
            catch (Exception ex)
            {
                _log.Error($"Trigger processing for \"{e.Name}\" failed.", ex);
            }
        }
        if (_intervalLoop is not null) await _intervalLoop.CompletesWithin(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await _triggers.DebouncesSettled().CompletesWithin(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Captures can start more captures (a rule's recording, a replay's after-window), so wait until none are left.
        var budget = Stopwatch.StartNew();
        var announced = false;
        while (_pending.Keys.ToArray() is { Length: > 0 } pending)
        {
            if (!announced) _log.Info($"Waiting for {pending.Length} capture(s) to finish...");
            announced = true;
            var left = TimeSpan.FromMinutes(2) - budget.Elapsed;
            if (left <= TimeSpan.Zero || !await Task.WhenAll(pending).CompletesWithin(left).ConfigureAwait(false))
            {
                _log.Warn("Some captures did not finish within 2 minutes and were abandoned.");
                break;
            }
        }
        _closing = true;
        if (IsRecording) await StopRecordingCoreAsync(null).ConfigureAwait(false);
        _shutdown.Cancel();

        var tape = _tape;
        if (tape is not null)
        {
            var end = tape.Now;
            await tape.StopAsync().ConfigureAwait(false);
            Diagnostics.FramesWritten = tape.FramesWritten;
            Diagnostics.FramesDropped = tape.FramesDropped;
            Diagnostics.MaxEncoderLagSeconds = tape.MaxLagSeconds;
            Diagnostics.TapeRunning = false;
            if (tape.State.SessionRecording) WriteFinalizeRequest(tape, end);
        }
        if (_valve is { } valve)
        {
            Diagnostics.MinFreeDiskGB = valve.MinFreeDiskGB;
            Diagnostics.MinAvailableMemoryMB = valve.MinAvailableMemoryMB;
            Diagnostics.MaxEncoderMemoryMB = valve.MaxEncoderMemoryMB;
            Diagnostics.MaxCpuPercent = valve.MaxCpuPercent;
            valve.Dispose();
        }
        _output.Manifest.Ended = DateTimeOffset.Now;
        _output.Manifest.Diagnostics = Diagnostics.ToDictionary();
        _output.SaveManifest();

        if (tape is not null)
        {
            if (tape.State.SessionRecording) await ExportSessionTapeAsync(tape, wait: true).ConfigureAwait(false);
            else SessionFinalizer.DeleteTape(tape.Dir);
        }
        _tapeGate.Dispose();
    }

    private void WriteFinalizeRequest(CaptureTape tape, double end)
    {
        var request = new FinalizeRequest { EndSeconds = end, Markers = _output.MarkersSnapshot() };
        File.WriteAllText(Path.Combine(tape.Dir, "finalize.json"), _json.Serialize(request));
    }

    /// <summary>A session tape replaced mid-run (its encoder died): request and start its export.</summary>
    private async Task FinalizeSessionTapeAsync(CaptureTape tape, double end, bool wait)
    {
        try
        {
            WriteFinalizeRequest(tape, end);
            await ExportSessionTapeAsync(tape, wait).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not hand {tape.Dir} to the finalizer; the next launch will recover it.", ex);
        }
    }

    /// <summary>Hands a stopped session tape to the detached finalizer, or exports it in-process.</summary>
    private async Task ExportSessionTapeAsync(CaptureTape tape, bool wait)
    {
        if (_options.DetachedFinalizerPath is { } agent && File.Exists(agent))
        {
            SessionFinalizer.SpawnDetached(agent, ["finalize", tape.Dir], _log);
            _log.Info($"Session export handed to the finalizer; it will appear in {_output.SessionDir}");
            return;
        }
        _log.Info("Exporting the session recording...");
        var export = Task.Run(() => SessionFinalizer.RunAsync(tape.Dir, _json, _log, CancellationToken.None));
        if (wait) await export.ConfigureAwait(false);
        else Track((Task)export);
    }
}
