using System.Diagnostics;
using MindAttic.Capture.Hosting;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MindAttic.Capture.Windows.Audio;

/// <summary>
/// One WASAPI track (system loopback or the default microphone) written as raw PCM. WASAPI loopback
/// delivers nothing while the system is silent, so gaps are filled with zeros to keep the track in
/// sync with the wall clock (and therefore with the video).
/// </summary>
public sealed class WasapiTrack : IAudioTrackCapture
{
    private readonly WasapiCapture _capture;
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Stream? _sink;
    private long _written;
    private bool _failed;

    private WasapiTrack(string name, WasapiCapture capture)
    {
        Name = name;
        _capture = capture;
        Format = MapFormat(capture.WaveFormat);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => _stopped.TrySetResult(e.Exception is null);
    }

    /// <summary>Whole-system output (what you hear).</summary>
    public static WasapiTrack Loopback() => new("app", new WasapiLoopbackCapture());

    /// <summary>The default microphone.</summary>
    public static WasapiTrack Microphone()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        return new WasapiTrack("mic", new WasapiCapture(device));
    }

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public AudioFormat Format { get; }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    internal static AudioFormat MapFormat(WaveFormat f)
    {
        var isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (f is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        var format = (isFloat, f.BitsPerSample) switch
        {
            (true, 32) => "f32le",
            (true, 64) => "f64le",
            (false, 16) => "s16le",
            (false, 24) => "s24le",
            (false, 32) => "s32le",
            _ => throw new CaptureException(CaptureError.CaptureFailed, $"Unsupported audio format {f}."),
        };
        return new AudioFormat(f.SampleRate, f.Channels, format);
    }

    /// <inheritdoc/>
    public Task StartAsync(Stream sink, CancellationToken ct)
    {
        _sink = sink;
        _clock.Start();
        _capture.StartRecording();
        return Task.CompletedTask;
    }

    private long ExpectedBytes()
    {
        var wf = _capture.WaveFormat;
        var bytes = (long)(_clock.Elapsed.TotalSeconds * wf.AverageBytesPerSecond);
        return bytes - bytes % wf.BlockAlign;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_sink is null || _failed) return;
            try
            {
                PadTo(ExpectedBytes() - e.BytesRecorded);
                _sink.Write(e.Buffer, 0, e.BytesRecorded);
                _written += e.BytesRecorded;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _failed = true;
            }
        }
    }

    /// <summary>Writes silence up to <paramref name="target"/> bytes when the gap exceeds 100 ms.</summary>
    private void PadTo(long target)
    {
        var wf = _capture.WaveFormat;
        var gap = target - _written;
        if (gap <= wf.AverageBytesPerSecond / 10) return;
        gap -= gap % wf.BlockAlign;
        var zeros = new byte[Math.Min(gap, 1 << 16)];
        while (gap > 0)
        {
            var n = (int)Math.Min(gap, zeros.Length);
            _sink!.Write(zeros, 0, n);
            _written += n;
            gap -= n;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (_capture.CaptureState == CaptureState.Capturing) _capture.StopRecording();
        await Task.WhenAny(_stopped.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        lock (_gate)
        {
            if (_sink is not null && !_failed)
            {
                try { PadTo(ExpectedBytes()); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            _sink = null;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _capture.Dispose();
        return default;
    }
}
