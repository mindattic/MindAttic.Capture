using System.Collections.Concurrent;
using System.Diagnostics;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;

namespace MindAttic.Capture.Tape;

/// <summary>
/// A continuously running recorder writing 1-second segments. Recordings, clips, replays and the
/// session are all time ranges cut from it, so they never start or stop an encoder themselves.
/// </summary>
internal sealed class CaptureTape : IAsyncDisposable
{
    private readonly ICaptureLog _log;
    private readonly ICaptureJson _json;
    private readonly FfmpegProcess _ffmpeg;
    private readonly PushVideoSource? _push;
    private readonly FrameSink? _sink;
    private readonly Stopwatch _clock;
    private readonly List<(IAudioTrackCapture Capture, FileStream File)> _audio = [];
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _janitor;
    private Func<double>? _keepFrom;
    private int _stopped;

    private CaptureTape(string dir, TapeState state, FfmpegProcess ffmpeg, PushVideoSource? push, FrameSink? sink, Stopwatch clock, ICaptureLog log, ICaptureJson json)
    {
        Dir = dir;
        State = state;
        _ffmpeg = ffmpeg;
        _push = push;
        _sink = sink;
        _clock = clock;
        _log = log;
        _json = json;
        _janitor = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public string Dir { get; }
    public TapeState State { get; }
    public bool IsRunning => Volatile.Read(ref _stopped) == 0 && !_ffmpeg.HasExited;
    public long FramesWritten => _sink?.Written ?? 0;
    public long FramesDropped => _sink?.Dropped ?? 0;

    /// <summary>Current tape time in seconds.</summary>
    public double Now => _push is { RealTime: false } && _sink is not null
        ? _sink.Written / (double)State.Fps
        : _sink is { FirstFrameTicks: 0 } ? 0 : (_clock.Elapsed.TotalSeconds - (_sink?.FirstFrameOffset ?? 0));

    public static async Task<CaptureTape> StartAsync(string dir, TapeState state, VideoSource source, IReadOnlyList<IAudioTrackCapture> audio,
        ICaptureLog log, ICaptureJson json, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        state.Width = source.Width;
        state.Height = source.Height;
        state.Source = source.Description;
        CaptureTape tape;
        switch (source)
        {
            case NativeVideoSource native:
            {
                CaptureException? last = null;
                FfmpegProcess? ffmpeg = null;
                foreach (var candidate in native.Candidates)
                {
                    try
                    {
                        ffmpeg = await FfmpegProcess.StartAsync(state.FfmpegPath,
                            FfmpegArgs.Tape(candidate.Args, state.Quality, state.Encoder, state.Fps, dir), dataStdin: false, log, ct).ConfigureAwait(false);
                        state.Source = candidate.Name;
                        break;
                    }
                    catch (CaptureException ex)
                    {
                        last = ex;
                        log.Warn($"Video input {candidate.Name} failed; trying the next one. {LastLine(ex.Message)}");
                    }
                }
                if (ffmpeg is null) throw last ?? new CaptureException(CaptureError.CaptureFailed, "No video input candidates.");
                tape = new CaptureTape(dir, state, ffmpeg, null, null, Stopwatch.StartNew(), log, json);
                break;
            }
            case PushVideoSource push:
            {
                state.FixedTimestep = !push.RealTime;
                var ffmpeg = await FfmpegProcess.StartAsync(state.FfmpegPath,
                    FfmpegArgs.Tape(FfmpegArgs.PushInput(push.Width, push.Height, state.Fps, push.RealTime), state.Quality, state.Encoder, state.Fps, dir),
                    dataStdin: true, log, ct).ConfigureAwait(false);
                var clock = Stopwatch.StartNew();
                var sink = new FrameSink(ffmpeg.DataStdin, push.Width * push.Height * 4, blocking: !push.RealTime, clock, log);
                tape = new CaptureTape(dir, state, ffmpeg, push, sink, clock, log, json);
                try
                {
                    await push.StartAsync(sink, ct).ConfigureAwait(false);
                }
                catch
                {
                    await tape.AbandonAsync().ConfigureAwait(false);
                    throw;
                }
                break;
            }
            default:
                throw new CaptureException(CaptureError.InvalidArgument, $"Unsupported video source {source.GetType().Name}.");
        }

        for (var i = 0; i < audio.Count; i++)
        {
            var file = $"audio{i}.raw";
            var fs = new FileStream(Path.Combine(dir, file), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            try
            {
                await audio[i].StartAsync(fs, ct).ConfigureAwait(false);
                tape._audio.Add((audio[i], fs));
                state.Audio.Add(new TapeAudioTrack
                {
                    File = file, Name = audio[i].Name, SampleRate = audio[i].Format.SampleRate,
                    Channels = audio[i].Format.Channels, Format = audio[i].Format.FfmpegFormat, OffsetSeconds = tape.Now,
                });
            }
            catch (Exception ex)
            {
                fs.Dispose();
                log.Warn($"Audio track {audio[i].Name} could not start; recording without it.", ex);
            }
        }
        try
        {
            tape.SaveState();
        }
        catch
        {
            await tape.AbandonAsync().ConfigureAwait(false);
            throw;
        }
        log.Info($"Tape started: {state.Width}x{state.Height}@{state.Fps} {state.Quality} via {state.Source}, {tape._audio.Count} audio track(s).");
        return tape;
    }

    private static string LastLine(string message) =>
        message.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    public void SaveState() => File.WriteAllText(Path.Combine(Dir, "tape.json"), _json.Serialize(State));

    public IReadOnlyList<TapeSegment> Segments() => TapeSegments.Read(Dir);

    /// <summary>Retention: segments ending before keepFrom() are deleted (never while the session is being recorded).</summary>
    public void SetRetention(Func<double> keepFrom) => _keepFrom = keepFrom;

    /// <summary>Waits until finished segments cover tape time <paramref name="t"/> (or the tape stops).</summary>
    public async Task<bool> WaitForCoverageAsync(double t, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var segs = Segments();
            if (segs.Count > 0 && segs[segs.Count - 1].End >= t - 0.001) return true;
            if (!IsRunning) return segs.Count > 0;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Furthest the encoder has fallen behind real time, in seconds.</summary>
    public double MaxLagSeconds { get; private set; }

    /// <summary>The encoder process (for resource polling).</summary>
    public Process EncoderProcess => _ffmpeg.Process;

    /// <summary>True for fixed-timestep tapes, which slow the game down instead of lagging.</summary>
    public bool FixedTimestep => _push is { RealTime: false };

    /// <summary>How far finished segments trail the tape clock right now.</summary>
    public double CurrentLag()
    {
        var segs = Segments();
        var encoded = segs.Count > 0 ? segs[segs.Count - 1].End : 0;
        return Math.Max(0, Now - encoded - FfmpegArgs.SegmentSeconds);
    }

    private void CheckLag()
    {
        if (_push is { RealTime: false }) return; // fixed-timestep tapes slow the game down instead of lagging
        var lag = CurrentLag();
        if (lag > MaxLagSeconds) MaxLagSeconds = lag;
        if (lag >= LagWarnSeconds && !_lagWarned)
        {
            _lagWarned = true;
            _log.Warn($"The tape encoder is {lag:0}s behind real time: the output drive or CPU can't keep up with {State.Quality} " +
                      $"{State.Width}x{State.Height}@{State.Fps}. Frames buffer inside ffmpeg until it catches up, and anything not " +
                      "encoded when the app closes is lost. Use 'hq', a lower resolution, or a faster output drive.");
        }
    }

    private const double LagWarnSeconds = 10;
    private bool _lagWarned;

    /// <summary>Timer callback: an exception escaping it would end the host process, and runs must not overlap.</summary>
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            SweepOnce();
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _stopped) == 0) _log.Warn("Tape housekeeping failed; trying again shortly.", ex);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private int _sweeping;

    private void SweepOnce()
    {
        try
        {
            CheckLag();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (State.SessionRecording || _keepFrom is null) return;
        double keep;
        try
        {
            keep = _keepFrom();
        }
        catch (Exception ex)
        {
            _log.Warn("Retention callback failed.", ex);
            return;
        }
        foreach (var s in Segments())
        {
            if (s.End >= keep - FfmpegArgs.SegmentSeconds) break;
            if (_deleted.Contains(s.File)) continue;
            try
            {
                if (File.Exists(s.File)) File.Delete(s.File);
                _deleted.Add(s.File);
            }
            catch (IOException)
            {
                // In use by an export; retry next sweep.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _janitor.Dispose();
        foreach (var (capture, file) in _audio)
        {
            try
            {
                await capture.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn($"Audio track {capture.Name} did not stop cleanly.", ex);
            }
            await file.FlushAsync().ConfigureAwait(false);
            file.Dispose();
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        if (_push is not null) await _push.StopAsync().ConfigureAwait(false);
        var lag = 0.0;
        try
        {
            lag = CurrentLag();
        }
        catch (IOException)
        {
        }
        // An encoder far behind real time would take as long again to catch up; give it a bounded grace
        // period, then cut. Segments already finished are kept and the session is exported from them.
        var grace = TimeSpan.FromSeconds(30);
        if (lag > grace.TotalSeconds)
            _log.Warn($"The tape encoder is {lag:0}s behind; footage it hasn't encoded within {grace.TotalSeconds:0}s will be dropped.");
        var drained = _sink is null || await _sink.CompleteAsync().CompletesWithin(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var code = await _ffmpeg.StopAsync(drained ? grace : TimeSpan.Zero).ConfigureAwait(false);
        if (code != 0) _log.Warn($"Tape ffmpeg exited with code {code}.{Environment.NewLine}{_ffmpeg.StderrTail}");
        if (_sink is not null && !drained) await _sink.CompleteAsync().CompletesWithin(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await _ffmpeg.DisposeAsync().ConfigureAwait(false);
        SaveState();
        _log.Info($"Tape stopped at {Now:0.0}s ({FramesWritten} pushed, {FramesDropped} dropped).");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>A tape that failed to start: stop ffmpeg, the writer and the janitor without letting cleanup errors hide the original one.</summary>
    private async Task AbandonAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Cleaning up a tape that failed to start also failed.", ex);
        }
    }

    /// <summary>Copies pushed frames into a bounded queue drained by a writer thread into ffmpeg's stdin.</summary>
    private sealed class FrameSink : IFrameSink
    {
        private const int Capacity = 6;
        private readonly Stream _stdin;
        private readonly int _frameBytes;
        private readonly bool _blocking;
        private readonly Stopwatch _clock;
        private readonly ICaptureLog _log;
        private readonly BlockingCollection<byte[]> _queue = new(Capacity);
        private readonly ConcurrentBag<byte[]> _pool = [];
        private readonly Task _writer;
        private long _written, _dropped, _firstFrameTicks;
        private volatile bool _failed;

        public FrameSink(Stream stdin, int frameBytes, bool blocking, Stopwatch clock, ICaptureLog log)
        {
            _stdin = stdin;
            _frameBytes = frameBytes;
            _blocking = blocking;
            _clock = clock;
            _log = log;
            _writer = Task.Factory.StartNew(Drain, TaskCreationOptions.LongRunning);
        }

        public long Written => Interlocked.Read(ref _written);
        public long Dropped => Interlocked.Read(ref _dropped);
        public long FirstFrameTicks => Interlocked.Read(ref _firstFrameTicks);
        public double FirstFrameOffset => FirstFrameTicks / (double)Stopwatch.Frequency;

        public bool WriteFrame(ReadOnlySpan<byte> bgra)
        {
            if (_failed || _queue.IsAddingCompleted) return false;
            if (bgra.Length != _frameBytes)
                throw new ArgumentException($"Frame is {bgra.Length} bytes; the tape expects {_frameBytes}.", nameof(bgra));
            if (!_blocking && _queue.Count >= Capacity)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            if (!_pool.TryTake(out var buffer)) buffer = new byte[_frameBytes];
            bgra.CopyTo(buffer);
            Interlocked.CompareExchange(ref _firstFrameTicks, Math.Max(1, _clock.ElapsedTicks), 0);
            try
            {
                if (_blocking) _queue.Add(buffer);
                else if (!_queue.TryAdd(buffer))
                {
                    Interlocked.Increment(ref _dropped);
                    _pool.Add(buffer);
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return true;
        }

        private void Drain()
        {
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                if (!_failed)
                {
                    try
                    {
                        _stdin.Write(frame, 0, frame.Length);
                        Interlocked.Increment(ref _written);
                    }
                    catch (IOException ex)
                    {
                        _failed = true;
                        _log.Error("ffmpeg stopped accepting frames; the tape has ended.", ex);
                    }
                }
                _pool.Add(frame);
            }
        }

        public async Task CompleteAsync()
        {
            _queue.CompleteAdding();
            await _writer.ConfigureAwait(false);
        }
    }
}
