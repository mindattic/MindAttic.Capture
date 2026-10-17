using System.Text.Json.Serialization;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>What a tray recording produces. The tape is always lossless; this picks the files exported from it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VideoFormat>))]
internal enum VideoFormat
{
    /// <summary>Lossless MKV master plus an H.264 MP4 copy next to it.</summary>
    LosslessAndMp4,
    /// <summary>Lossless MKV master only.</summary>
    Lossless,
    /// <summary>H.264 MP4 only, at full size.</summary>
    Mp4,
    /// <summary>Animated GIF (15 fps, 640 px wide).</summary>
    Gif,
}

/// <summary>Everything the area selector decides: where, what shape, and which files to write.</summary>
internal sealed record AreaChoice(PixelRect Region, AspectRatio Ratio, VideoFormat Video, ImageFormat Image);

internal static class TrayFormats
{
    public static IReadOnlyList<VideoFormat> Videos { get; } = Enum.GetValues<VideoFormat>();

    public static IReadOnlyList<ImageFormat> Images { get; } = [ImageFormat.Png, ImageFormat.Jpg];

    public static string Name(VideoFormat f) => f switch
    {
        VideoFormat.LosslessAndMp4 => "Lossless + MP4",
        VideoFormat.Lossless => "Lossless only",
        VideoFormat.Mp4 => "MP4 only",
        VideoFormat.Gif => "GIF",
        _ => f.ToString(),
    };

    public static string Name(ImageFormat f) => f.ToString().ToUpperInvariant();

    /// <summary>
    /// The engine options for <paramref name="f"/>. Everything goes through the Trailer preset (no output scaling),
    /// so MP4-only of a tall 9:16 area keeps its full height rather than being cut to 1080.
    /// </summary>
    public static RecordingOptions Recording(VideoFormat f, CaptureData? data = null) => f switch
    {
        VideoFormat.Lossless => new RecordingOptions { Preset = CapturePreset.Lossless, Data = data },
        VideoFormat.Mp4 => new RecordingOptions { Preset = CapturePreset.Trailer, Outputs = OutputFormats.Mp4, Data = data },
        VideoFormat.Gif => new RecordingOptions { Preset = CapturePreset.Trailer, Outputs = OutputFormats.Gif, Data = data },
        _ => new RecordingOptions { Preset = CapturePreset.Trailer, Data = data },
    };

    public static ScreenshotOptions Screenshot(ImageFormat f, CaptureData? data = null) => new() { Format = f, Data = data };
}
