using MindAttic.Capture.Hosting;

namespace MindAttic.Capture;

/// <summary>What produced a <see cref="CaptureResult"/>.</summary>
public enum CaptureKind
{
    /// <summary>A still image.</summary>
    Screenshot,
    /// <summary>A Start/StopRecording span (pauses removed).</summary>
    Recording,
    /// <summary>A fixed-length clip starting at the call.</summary>
    Clip,
    /// <summary>Footage from before (and optionally after) an event.</summary>
    Replay,
    /// <summary>The whole <c>-capture</c> run.</summary>
    Session,
}

/// <summary>Still-image file format.</summary>
public enum ImageFormat
{
    /// <summary>Lossless; keeps alpha when the host captured a transparent background.</summary>
    Png,
    /// <summary>Lossy (encoded through ffmpeg).</summary>
    Jpg,
    /// <summary>Lossy (encoded through ffmpeg).</summary>
    WebP,
    /// <summary>Uncompressed 32-bit.</summary>
    Bmp,
    /// <summary>Tightly packed top-down BGRA bytes plus a <c>.json</c> sidecar with width, height and stride.</summary>
    RawBgra,
}

/// <summary>Quality presets that pick output formats and encoder quality.</summary>
public enum CapturePreset
{
    /// <summary>Whole-run session footage: lossless master plus an H.264 edit copy.</summary>
    Session,
    /// <summary>Lossless master only.</summary>
    Lossless,
    /// <summary>Lossless master plus a high-quality H.264 edit copy, for trailer editing.</summary>
    Trailer,
    /// <summary>Good-quality H.264 MP4, ready to post.</summary>
    Devlog,
    /// <summary>Small 720p30 H.264 MP4 for bug reports.</summary>
    BugReport,
}

/// <summary>How the rolling tape is encoded while recording.</summary>
public enum TapeQuality
{
    /// <summary>FFV1 RGB: bit-exact, large (roughly 1-4 GB per minute at 1080p60).</summary>
    Lossless,
    /// <summary>Hardware HEVC at near-lossless quality: around 0.2 GB per minute.</summary>
    HighQuality,
}

/// <summary>Video files to produce for a recording, clip, replay or session.</summary>
[Flags]
public enum OutputFormats
{
    /// <summary>Nothing beyond what the preset implies.</summary>
    None = 0,
    /// <summary>The tape-quality master (.mkv).</summary>
    Mkv = 1,
    /// <summary>H.264 MP4 that imports into any editor.</summary>
    Mp4 = 2,
    /// <summary>VP9 WebM.</summary>
    WebM = 4,
    /// <summary>Animated GIF (15 fps, 640 px wide).</summary>
    Gif = 8,
    /// <summary>Animated WebP (15 fps, 640 px wide).</summary>
    WebP = 16,
}

/// <summary>Which audio to record alongside video.</summary>
public enum AudioMode
{
    /// <summary>No audio.</summary>
    None,
    /// <summary>App or game audio (system loopback where per-app capture is unavailable).</summary>
    App,
    /// <summary>App audio plus the default microphone.</summary>
    AppAndMic,
}

/// <summary>Key/value data attached to events, markers and captures; it lands in sidecar JSON.</summary>
public sealed class CaptureData : Dictionary<string, object?>
{
    /// <summary>Creates an empty, case-insensitive bag.</summary>
    public CaptureData() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>Copies <paramref name="source"/>.</summary>
    public CaptureData(IDictionary<string, object?> source) : base(source, StringComparer.OrdinalIgnoreCase) { }
}

/// <summary>Options for one screenshot. Options the host cannot honor are ignored with a warning.</summary>
public sealed record ScreenshotOptions
{
    /// <summary>Render at N times the native resolution (hosts with <see cref="HostCapabilities.Supersample"/>).</summary>
    public int Supersample { get; init; } = 1;
    /// <summary>Clear the background to transparent (hosts with <see cref="HostCapabilities.Transparency"/>; PNG only).</summary>
    public bool TransparentBackground { get; init; }
    /// <summary>Hide UI layers (hosts with <see cref="HostCapabilities.ExcludeUi"/>).</summary>
    public bool ExcludeUi { get; init; }
    /// <summary>File format.</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Png;
    /// <summary>JPG/WebP quality 1-100.</summary>
    public int Quality { get; init; } = 92;
    /// <summary>Extra key/values written to the sidecar.</summary>
    public CaptureData? Data { get; init; }
}

/// <summary>Options for a recording, clip or replay.</summary>
public sealed record RecordingOptions
{
    /// <summary>Picks the default outputs and encoder quality.</summary>
    public CapturePreset Preset { get; init; } = CapturePreset.Trailer;
    /// <summary>Overrides the preset's output formats.</summary>
    public OutputFormats? Outputs { get; init; }
    /// <summary>Scale outputs (not the master) to this height, keeping aspect.</summary>
    public int? OutputHeight { get; init; }
    /// <summary>Encoder quality override (CRF/CQ scale; lower is better).</summary>
    public int? Quality { get; init; }
    /// <summary>Keep app and mic audio as separate tracks instead of mixing them.</summary>
    public bool SeparateAudioTracks { get; init; }
    /// <summary>Extra key/values written to the sidecar.</summary>
    public CaptureData? Data { get; init; }
}

/// <summary>The outcome of a capture call. Calls never throw for runtime capture problems; check <see cref="Error"/>.</summary>
public sealed record CaptureResult
{
    /// <summary>What kind of capture this was.</summary>
    public CaptureKind Kind { get; init; }
    /// <summary>The caller's tag.</summary>
    public string Tag { get; init; } = "";
    /// <summary>Files produced, primary file first. Sidecars are not listed.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>When the capture was requested.</summary>
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Length of footage (zero for screenshots).</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>Event name when a trigger rule produced this capture.</summary>
    public string? Trigger { get; init; }
    /// <summary>Set when capture was disabled or not possible; no files were written.</summary>
    public bool Skipped { get; init; }
    /// <summary>Why the capture failed or was skipped.</summary>
    public string? Error { get; init; }

    /// <summary>The first produced file, if any.</summary>
    public string? PrimaryFile => Files.Count > 0 ? Files[0] : null;

    /// <summary>True when at least one file was written.</summary>
    public bool Succeeded => Files.Count > 0 && Error is null;

    internal static CaptureResult Skip(CaptureKind kind, string tag, string reason) =>
        new() { Kind = kind, Tag = tag, Skipped = true, Error = reason, Timestamp = DateTimeOffset.Now };

    internal static CaptureResult Fail(CaptureKind kind, string tag, string error, DateTimeOffset at) =>
        new() { Kind = kind, Tag = tag, Error = error, Timestamp = at };
}

/// <summary>Why a capture operation failed.</summary>
public enum CaptureError
{
    /// <summary>Bad argument or configuration.</summary>
    InvalidArgument = 1,
    /// <summary>The window, process or monitor to capture was not found.</summary>
    TargetNotFound = 2,
    /// <summary>ffmpeg is missing or exited with an error.</summary>
    FfmpegFailed = 3,
    /// <summary>No output folder could be created.</summary>
    OutputUnavailable = 4,
    /// <summary>The host failed to grab pixels.</summary>
    CaptureFailed = 5,
}

/// <summary>Thrown by setup and host code; the <see cref="ICapture"/> API reports runtime failures through <see cref="CaptureResult.Error"/> instead.</summary>
public sealed class CaptureException(CaptureError error, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The failure category; doubles as the agent's exit code.</summary>
    public CaptureError Error { get; } = error;
}
