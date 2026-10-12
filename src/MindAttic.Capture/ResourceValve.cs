using System.Diagnostics;
using System.Globalization;
using System.Text;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Tape;

namespace MindAttic.Capture;

/// <summary>One poll of the resources the valve watches. Null means "unknown on this platform".</summary>
internal sealed record ValveSample(
    TimeSpan Elapsed, double? FreeDiskGB, double? AvailableMemoryMB, double? CpuPercent, double? AppMemoryMB,
    double? EncoderMemoryMB, double? EncoderLagSeconds, double TapeSeconds, long FramesWritten, long FramesDropped, double? DiskWriteMBps);

/// <summary>
/// The shut-off valve: polls disk, memory, CPU and the tape encoder while capture runs, logs a status line
/// each minute, appends every sample to {session}.stats.csv, and closes (stops capture) when a
/// <see cref="CaptureLimits"/> limit is crossed.
/// </summary>
internal sealed class ResourceValve
{
    private static readonly TimeSpan StatusEvery = TimeSpan.FromMinutes(1);
    private readonly CaptureLimits _limits;
    private readonly ICaptureLog _log;
    private readonly Func<CaptureTape?> _tape;
    private readonly string _volumePath;
    private readonly string _csvPath;
    private readonly SystemStats _stats = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _self = Process.GetCurrentProcess();
    private double? _lastFreeGB;
    private TimeSpan _lastSampleAt, _lastStatusAt;
    private bool _csvFailed;

    public ResourceValve(CaptureLimits limits, ICaptureLog log, Func<CaptureTape?> tape, string volumePath, string csvPath)
    {
        _limits = limits;
        _log = log;
        _tape = tape;
        _volumePath = volumePath;
        _csvPath = csvPath;
    }

    public double? MinFreeDiskGB { get; private set; }
    public double? MinAvailableMemoryMB { get; private set; }
    public double? MaxEncoderMemoryMB { get; private set; }
    public double? MaxCpuPercent { get; private set; }

    /// <summary>Polls until cancelled or a limit trips; returns the reason it closed, or null.</summary>
    public async Task<string?> RunAsync(CancellationToken ct)
    {
        _stats.CpuPercent(); // prime the CPU delta
        var limits = DescribeLimits();
        _log.Info($"Capture limits: {limits}. Stats go to {_csvPath}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_limits.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            ValveSample sample;
            try
            {
                sample = Sample();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Reading resource stats failed; trying again next poll.", ex);
                continue;
            }
            Track(sample);
            WriteCsv(sample);
            if (sample.Elapsed - _lastStatusAt >= StatusEvery)
            {
                _lastStatusAt = sample.Elapsed;
                _log.Info(Status(sample));
            }
            if (Check(sample, _limits) is { } reason)
            {
                _log.Warn($"Capture valve closed: {reason}. Stopping capture and exporting what was recorded ({Status(sample)}).");
                return reason;
            }
        }
        return null;
    }

    /// <summary>The first limit <paramref name="s"/> crosses, as a sentence, or null.</summary>
    internal static string? Check(ValveSample s, CaptureLimits l)
    {
        // A duration cap the user asked for applies even with the resource limits switched off; a limit of 0 is off.
        if (l.Enabled)
        {
            if (l.MinFreeDiskGB is > 0 and var disk && s.FreeDiskGB is { } free && free < disk)
                return $"only {free:0.#} GB free on the output drive (limit {disk:0.#} GB)";
            if (l.MinFreeMemoryMB is > 0 and var mem && s.AvailableMemoryMB is { } avail && avail < mem)
                return $"only {avail:0} MB of memory available (limit {mem:0} MB)";
            if (l.MaxEncoderMemoryMB is > 0 and var enc && s.EncoderMemoryMB is { } used && used > enc)
                return $"the encoder is using {used:0} MB, frames are backing up inside it (limit {enc:0} MB)";
            if (l.MaxEncoderLagSeconds is > 0 and var lagLimit && s.EncoderLagSeconds is { } lag && lag > lagLimit)
                return $"the encoder is {lag:0}s behind real time (limit {lagLimit:0}s)";
        }
        if (l.MaxDuration is { } max && max > TimeSpan.Zero && s.Elapsed >= max)
            return $"the maximum duration of {DurationText.Seconds(max.TotalSeconds)}s was reached";
        return null;
    }

    private ValveSample Sample()
    {
        var now = _clock.Elapsed;
        var tape = _tape();
        var free = SystemStats.FreeDiskGB(_volumePath);
        double? writeRate = null;
        if (free is { } f && _lastFreeGB is { } lastFree && now > _lastSampleAt)
            writeRate = Math.Max(0, (lastFree - f) * 1024 / (now - _lastSampleAt).TotalSeconds);
        _lastFreeGB = free;
        _lastSampleAt = now;
        double? lag = null;
        if (tape is { IsRunning: true, FixedTimestep: false })
        {
            try
            {
                lag = tape.CurrentLag();
            }
            catch (IOException)
            {
            }
        }
        return new ValveSample(now, free, SystemStats.AvailableMemoryMB(), _stats.CpuPercent(), SystemStats.PrivateMemoryMB(_self),
            tape is { IsRunning: true } ? SystemStats.PrivateMemoryMB(tape.EncoderProcess) : null, lag,
            tape?.Now ?? 0, tape?.FramesWritten ?? 0, tape?.FramesDropped ?? 0, writeRate);
    }

    private void Track(ValveSample s)
    {
        if (s.FreeDiskGB is { } d) MinFreeDiskGB = MinFreeDiskGB is { } m ? Math.Min(m, d) : d;
        if (s.AvailableMemoryMB is { } a) MinAvailableMemoryMB = MinAvailableMemoryMB is { } m2 ? Math.Min(m2, a) : a;
        if (s.EncoderMemoryMB is { } e) MaxEncoderMemoryMB = MaxEncoderMemoryMB is { } m3 ? Math.Max(m3, e) : e;
        if (s.CpuPercent is { } c) MaxCpuPercent = MaxCpuPercent is { } m4 ? Math.Max(m4, c) : c;
    }

    private void WriteCsv(ValveSample s)
    {
        if (_csvFailed) return;
        try
        {
            var header = !File.Exists(_csvPath);
            using var w = new StreamWriter(new FileStream(_csvPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            if (header)
                w.WriteLine("time,elapsedSeconds,freeDiskGB,availableMemoryMB,cpuPercent,appMemoryMB,encoderMemoryMB,encoderLagSeconds,tapeSeconds,framesWritten,framesDropped,diskWriteMBps");
            w.WriteLine(string.Join(",",
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                N(s.Elapsed.TotalSeconds), N(s.FreeDiskGB), N(s.AvailableMemoryMB), N(s.CpuPercent), N(s.AppMemoryMB),
                N(s.EncoderMemoryMB), N(s.EncoderLagSeconds), N(s.TapeSeconds),
                s.FramesWritten.ToString(CultureInfo.InvariantCulture), s.FramesDropped.ToString(CultureInfo.InvariantCulture), N(s.DiskWriteMBps)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _csvFailed = true;
            _log.Warn($"Could not write capture stats to {_csvPath}; stats stay in the log.", ex);
        }
    }

    private static string N(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

    internal static string Status(ValveSample s)
    {
        var parts = new List<string> { $"captured {(int)s.Elapsed.TotalHours}:{s.Elapsed:mm\\:ss}" };
        if (s.FreeDiskGB is { } d) parts.Add($"disk {d:N0} GB free");
        if (s.AvailableMemoryMB is { } m) parts.Add($"RAM {m / 1024:0.0} GB free");
        if (s.CpuPercent is { } c) parts.Add($"CPU {c:0}%");
        if (s.EncoderMemoryMB is { } e) parts.Add($"encoder {e:0} MB");
        if (s.EncoderLagSeconds is { } l) parts.Add($"{l:0.0}s behind");
        if (s.DiskWriteMBps is { } w) parts.Add($"drive writing {w:0.#} MB/s");
        if (s.FramesDropped > 0) parts.Add($"{s.FramesDropped} frames dropped");
        return string.Join(", ", parts);
    }

    private string DescribeLimits()
    {
        var parts = new List<string>();
        if (!_limits.Enabled) parts.Add("resource limits off (stats only)");
        else
        {
            if (_limits.MinFreeDiskGB is > 0 and var d) parts.Add($"disk >= {d:0.#} GB free");
            if (_limits.MinFreeMemoryMB is > 0 and var m) parts.Add($"RAM >= {m:0} MB free");
            if (_limits.MaxEncoderMemoryMB is > 0 and var e) parts.Add($"encoder <= {e:0} MB");
            if (_limits.MaxEncoderLagSeconds is > 0 and var l) parts.Add($"lag <= {l:0}s");
        }
        if (_limits.MaxDuration is { } t && t > TimeSpan.Zero) parts.Add($"duration <= {t}");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }
}
