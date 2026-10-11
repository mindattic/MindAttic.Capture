namespace MindAttic.Capture.Hosting;

/// <summary>What a host adapter can do beyond plain capture.</summary>
[Flags]
public enum HostCapabilities
{
    /// <summary>Plain capture only.</summary>
    None = 0,
    /// <summary>Screenshots with a transparent background.</summary>
    Transparency = 1,
    /// <summary>Screenshots without UI layers.</summary>
    ExcludeUi = 2,
    /// <summary>Screenshots rendered above native resolution.</summary>
    Supersample = 4,
    /// <summary>Frames are produced on a fixed timestep (frame-perfect, not real time).</summary>
    FixedTimestep = 8,
    /// <summary>Audio of just this app (rather than the whole system mix).</summary>
    AppAudio = 16,
}

/// <summary>
/// The contract every host adapter (desktop, WPF, WebView2, Unity, ...) implements.
/// The core never references a host type; new hosts plug in here.
/// </summary>
public interface ICaptureHost
{
    /// <summary>Short host name for logs and sidecars, e.g. "wpf" or "unity".</summary>
    string Name { get; }

    /// <summary>What this host supports.</summary>
    HostCapabilities Capabilities { get; }

    /// <summary>Grabs one frame. Called off the UI/game thread; the host marshals as needed.</summary>
    ValueTask<CapturedImage> CaptureStillAsync(ScreenshotOptions options, CancellationToken ct);

    /// <summary>Creates the video source the tape records from.</summary>
    VideoSource CreateVideoSource(int fps);

    /// <summary>Creates one capture per audio track (app, then mic). Empty when unsupported.</summary>
    IReadOnlyList<IAudioTrackCapture> CreateAudioTracks(AudioMode mode);
}

/// <summary>A captured frame: top-down 8-bit BGRA, stride = Width * 4.</summary>
public sealed class CapturedImage(int width, int height, byte[] bgra, bool hasAlpha = false)
{
    /// <summary>Width in pixels.</summary>
    public int Width { get; } = width;
    /// <summary>Height in pixels.</summary>
    public int Height { get; } = height;
    /// <summary>Pixel bytes.</summary>
    public byte[] Bgra { get; } = bgra.Length == width * height * 4 ? bgra
        : throw new ArgumentException($"Expected {width * height * 4} bytes for {width}x{height} BGRA, got {bgra.Length}.", nameof(bgra));
    /// <summary>True when the alpha channel is meaningful (transparent background).</summary>
    public bool HasAlpha { get; } = hasAlpha;
}

/// <summary>Where the tape's frames come from.</summary>
public abstract class VideoSource
{
    /// <summary>Frame width the tape is created with.</summary>
    public abstract int Width { get; }
    /// <summary>Frame height the tape is created with.</summary>
    public abstract int Height { get; }
    /// <summary>Human-readable description for diagnostics, e.g. "ddagrab output 0".</summary>
    public abstract string Description { get; }
}

/// <summary>ffmpeg grabs frames itself (e.g. ddagrab, gdigrab). Candidates are tried in order until one starts.</summary>
public sealed class NativeVideoSource(int width, int height, IReadOnlyList<NativeInput> candidates) : VideoSource
{
    /// <inheritdoc/>
    public override int Width { get; } = width;
    /// <inheritdoc/>
    public override int Height { get; } = height;
    /// <summary>ffmpeg input argument sets, most preferred first.</summary>
    public IReadOnlyList<NativeInput> Candidates { get; } = candidates;
    /// <inheritdoc/>
    public override string Description => string.Join(" | ", Candidates.Select(c => c.Name));
}

/// <summary>One way to make ffmpeg grab frames.</summary>
/// <param name="Name">Diagnostic name, e.g. "ddagrab".</param>
/// <param name="Args">ffmpeg input arguments, ending with the <c>-i</c> argument pair.</param>
/// <param name="IsLavfiGraph">True when the input is a lavfi graph (affects nothing but diagnostics).</param>
public sealed record NativeInput(string Name, IReadOnlyList<string> Args, bool IsLavfiGraph = false);

/// <summary>
/// The host pushes BGRA frames (WGC, Unity readback, ...). Real-time sources are timestamped by wall clock
/// and resampled to constant fps; fixed-timestep sources are timestamped by frame index (frame-perfect).
/// </summary>
public abstract class PushVideoSource : VideoSource
{
    /// <summary>True: timestamps from the wall clock. False: frame N is at N / fps (fixed timestep).</summary>
    public abstract bool RealTime { get; }

    /// <summary>Begin delivering frames to <paramref name="sink"/> until <see cref="StopAsync"/>.</summary>
    public abstract Task StartAsync(IFrameSink sink, CancellationToken ct);

    /// <summary>Stop delivering frames.</summary>
    public abstract Task StopAsync();
}

/// <summary>Accepts frames for the tape.</summary>
public interface IFrameSink
{
    /// <summary>
    /// Queues a copy of one Width x Height BGRA frame. Real-time sinks drop the frame (and return false)
    /// when the encoder falls behind; fixed-timestep sinks block instead so no frame is lost.
    /// </summary>
    bool WriteFrame(ReadOnlySpan<byte> bgra);
}

/// <summary>Raw PCM layout of an audio track.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Interleaved channel count.</param>
/// <param name="FfmpegFormat">ffmpeg raw sample format, e.g. "f32le" or "s16le".</param>
public sealed record AudioFormat(int SampleRate, int Channels, string FfmpegFormat);

/// <summary>One audio track (app or mic), written as raw PCM.</summary>
public interface IAudioTrackCapture : IAsyncDisposable
{
    /// <summary>Track name for diagnostics ("app" or "mic").</summary>
    string Name { get; }
    /// <summary>PCM layout written to the sink.</summary>
    AudioFormat Format { get; }
    /// <summary>Start writing raw PCM to <paramref name="sink"/>. Silence must be written as zeros, not skipped.</summary>
    Task StartAsync(Stream sink, CancellationToken ct);
    /// <summary>Stop and flush.</summary>
    Task StopAsync();
}
