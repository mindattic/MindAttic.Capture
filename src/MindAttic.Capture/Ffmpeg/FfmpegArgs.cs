using System.Globalization;
using System.Text;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;

namespace MindAttic.Capture.Ffmpeg;

/// <summary>What a preset turns into.</summary>
/// <param name="Outputs">Files to produce.</param>
/// <param name="Quality">CRF/CQ for derived video (lower is better).</param>
/// <param name="MaxHeight">Scale derived outputs down to this height.</param>
/// <param name="Fps">Resample derived outputs to this rate.</param>
internal sealed record PresetSpec(OutputFormats Outputs, int Quality, int? MaxHeight, int? Fps)
{
    public static PresetSpec For(CapturePreset preset, RecordingOptions? o = null)
    {
        var spec = preset switch
        {
            CapturePreset.Session => new PresetSpec(OutputFormats.Mkv | OutputFormats.Mp4, 16, null, null),
            CapturePreset.Lossless => new PresetSpec(OutputFormats.Mkv, 16, null, null),
            CapturePreset.Trailer => new PresetSpec(OutputFormats.Mkv | OutputFormats.Mp4, 16, null, null),
            CapturePreset.Devlog => new PresetSpec(OutputFormats.Mp4, 20, 1080, null),
            CapturePreset.BugReport => new PresetSpec(OutputFormats.Mp4, 28, 720, 30),
            _ => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown preset {preset}."),
        };
        if (o is null) return spec;
        return spec with
        {
            Outputs = o.Outputs is { } outs && outs != OutputFormats.None ? outs : spec.Outputs,
            Quality = o.Quality ?? spec.Quality,
            MaxHeight = o.OutputHeight ?? spec.MaxHeight,
        };
    }
}

/// <summary>One raw PCM track file on the tape.</summary>
internal sealed class TapeAudioTrack
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Format { get; set; } = "f32le";
    /// <summary>Tape time at which the first sample was captured.</summary>
    public double OffsetSeconds { get; set; }
}

/// <summary>Pure ffmpeg argument builders (no IO) so every command can be unit-tested.</summary>
internal static class FfmpegArgs
{
    public const double SegmentSeconds = 1.0;
    public const string SegmentPattern = "seg%08d.mkv";
    public const string SegmentList = "tape.csv";

    private static readonly string[] Common = ["-hide_banner", "-nostats", "-loglevel", "info"];

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string S(double v) => DurationText.Seconds(v);

    /// <summary>stdin raw BGRA input. Real-time frames use wall-clock timestamps; fixed-timestep frames use frame index.</summary>
    public static List<string> PushInput(int width, int height, int fps, bool realTime)
    {
        List<string> args = ["-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{I(width)}x{I(height)}", "-thread_queue_size", "256"];
        args.AddRange(realTime ? ["-use_wallclock_as_timestamps", "1"] : ["-framerate", I(fps)]);
        args.AddRange(["-i", "pipe:0"]);
        return args;
    }

    /// <summary>Tape codec. Lossless: FFV1 RGB, every frame a keyframe. HQ: hardware HEVC with a keyframe every segment.</summary>
    public static List<string> TapeCodec(TapeQuality quality, string hevcEncoder, int fps) => quality switch
    {
        TapeQuality.Lossless => ["-c:v", "ffv1", "-level", "3", "-g", "1", "-slices", "16", "-slicecrc", "1", "-pix_fmt", "bgr0"],
        _ => [.. VideoEncoder(hevcEncoder, 18), "-pix_fmt", "yuv420p", "-g", I(fps),
              "-force_key_frames", $"expr:gte(t,n_forced*{S(SegmentSeconds)})"],
    };

    /// <summary>Full tape command: input, constant frame rate, codec, 1-second segments listed in tape.csv.</summary>
    public static List<string> Tape(IEnumerable<string> input, TapeQuality quality, string hevcEncoder, int fps, string tapeDir) =>
    [
        .. Common, .. input, "-an", "-fps_mode", "cfr", "-r", I(fps),
        .. TapeCodec(quality, hevcEncoder, fps),
        "-f", "segment", "-segment_time", S(SegmentSeconds), "-segment_format", "matroska", "-reset_timestamps", "1",
        "-segment_list", Path.Combine(tapeDir, SegmentList), "-segment_list_type", "csv",
        "-y", Path.Combine(tapeDir, SegmentPattern),
    ];

    /// <summary>Quality-targeted encoder arguments for any probed encoder.</summary>
    public static List<string> VideoEncoder(string encoder, int quality)
    {
        var q = I(quality);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            return ["-c:v", encoder, "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0"];
        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
            return ["-c:v", encoder, "-global_quality", q, "-preset", "slower"];
        if (encoder.EndsWith("_amf", StringComparison.Ordinal))
            return ["-c:v", encoder, "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q, "-quality", "quality"];
        if (encoder == "libx265") return ["-c:v", "libx265", "-crf", q, "-preset", "medium"];
        return ["-c:v", encoder, "-crf", q, "-preset", "slow"];
    }

    /// <summary>Concat-demuxer list: whole segments, trimmed with inpoint/outpoint at the range edges.</summary>
    public static string ConcatList(IReadOnlyList<(string File, double? InPoint, double? OutPoint)> parts)
    {
        var sb = new StringBuilder();
        foreach (var (file, inPoint, outPoint) in parts)
        {
            sb.Append("file '").Append(file.Replace("'", @"'\''")).Append("'\n");
            if (inPoint is { } i) sb.Append("inpoint ").Append(S(i)).Append('\n');
            if (outPoint is { } o) sb.Append("outpoint ").Append(S(o)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>FFMETADATA chapters: one per marker, running to the next marker (or the end).</summary>
    public static string Chapters(IReadOnlyList<(string Name, double Seconds)> markers, double durationSeconds)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        var ordered = markers.Where(m => m.Seconds >= 0 && m.Seconds < durationSeconds).OrderBy(m => m.Seconds).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = (long)(ordered[i].Seconds * 1000);
            var end = (long)((i + 1 < ordered.Count ? ordered[i + 1].Seconds : durationSeconds) * 1000);
            if (end <= start) end = start + 1;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
              .Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("title=").Append(EscapeMeta(ordered[i].Name)).Append('\n');
        }
        return sb.ToString();
    }

    private static string EscapeMeta(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Range export: video stream-copied from the concat list, each audio track cut to the same window
    /// (delayed if it started after the range), mixed or kept separate, chapters attached.
    /// </summary>
    public static List<string> ExportRange(string concatList, double rangeStart, double duration,
        IReadOnlyList<(string Path, TapeAudioTrack Track)> audio, bool separateAudio, string? chaptersFile, string output)
    {
        List<string> args = [.. Common, "-f", "concat", "-safe", "0", "-i", concatList];
        foreach (var (path, track) in audio)
        {
            var skip = Math.Max(0, rangeStart - track.OffsetSeconds);
            if (skip > 0) args.AddRange(["-ss", S(skip)]);
            args.AddRange(["-f", track.Format, "-ar", I(track.SampleRate), "-ac", I(track.Channels), "-i", path]);
        }
        var chapterInput = 1 + audio.Count;
        if (chaptersFile is not null) args.AddRange(["-f", "ffmetadata", "-i", chaptersFile]);

        args.AddRange(["-map", "0:v"]);
        if (audio.Count > 0)
        {
            var filters = new List<string>();
            var labels = new List<string>();
            for (var i = 0; i < audio.Count; i++)
            {
                var delayMs = (int)Math.Round(Math.Max(0, audio[i].Track.OffsetSeconds - rangeStart) * 1000);
                var label = $"a{I(i)}";
                filters.Add(delayMs > 0 ? $"[{I(i + 1)}:a]adelay=delays={I(delayMs)}:all=1[{label}]" : $"[{I(i + 1)}:a]anull[{label}]");
                labels.Add(label);
            }
            if (separateAudio || audio.Count == 1)
            {
                args.AddRange(["-filter_complex", string.Join(";", filters)]);
                foreach (var l in labels) args.AddRange(["-map", $"[{l}]"]);
            }
            else
            {
                filters.Add($"{string.Concat(labels.Select(l => $"[{l}]"))}amix=inputs={I(labels.Count)}:normalize=0:duration=longest[mix]");
                args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[mix]"]);
            }
            args.AddRange(["-c:a", "flac"]);
        }
        if (chaptersFile is not null) args.AddRange(["-map_chapters", I(chapterInput)]);
        args.AddRange(["-c:v", "copy", "-t", S(duration), "-y", output]);
        return args;
    }

    /// <summary>Joins already-exported range files (pause/resume) without re-encoding.</summary>
    public static List<string> JoinParts(string concatList, string? chaptersFile, string output)
    {
        List<string> args = [.. Common, "-f", "concat", "-safe", "0", "-i", concatList];
        if (chaptersFile is not null) args.AddRange(["-f", "ffmetadata", "-i", chaptersFile]);
        args.AddRange(["-map", "0"]);
        if (chaptersFile is not null) args.AddRange(["-map_chapters", "1"]);
        args.AddRange(["-c", "copy", "-y", output]);
        return args;
    }

    private static string ScaleFps(PresetSpec spec, string? extra = null)
    {
        var parts = new List<string>();
        if (spec.Fps is { } fps) parts.Add($"fps={I(fps)}");
        if (spec.MaxHeight is { } h) parts.Add($"scale=-2:'min({I(h)},ih)':flags=lanczos");
        parts.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2");
        if (extra is not null) parts.Add(extra);
        return string.Join(",", parts);
    }

    /// <summary>H.264 MP4 every editor imports (4:2:0, even dimensions, faststart).</summary>
    public static List<string> Mp4(string master, string h264Encoder, PresetSpec spec, string output) =>
    [
        .. Common, "-i", master, "-map", "0:v", "-map", "0:a?",
        "-vf", ScaleFps(spec, "format=yuv420p"),
        .. VideoEncoder(h264Encoder, spec.Quality),
        "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-movflags", "+faststart", "-y", output,
    ];

    /// <summary>VP9 WebM.</summary>
    public static List<string> WebM(string master, PresetSpec spec, string output) =>
    [
        .. Common, "-i", master, "-map", "0:v", "-map", "0:a?",
        "-vf", ScaleFps(spec, "format=yuv420p"),
        "-c:v", "libvpx-vp9", "-crf", I(Math.Min(63, spec.Quality + 14)), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2",
        "-c:a", "libopus", "-b:a", "160k", "-ac", "2", "-y", output,
    ];

    /// <summary>Palette-optimized animated GIF, 15 fps, at most 640 px wide.</summary>
    public static List<string> Gif(string master, string output) =>
    [
        .. Common, "-i", master, "-an",
        "-filter_complex", "fps=15,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a",
        "-loop", "0", "-y", output,
    ];

    /// <summary>Animated WebP, 15 fps, at most 640 px wide.</summary>
    public static List<string> AnimatedWebP(string master, string output) =>
    [
        .. Common, "-i", master, "-an",
        "-vf", "fps=15,scale='min(640,iw)':-2:flags=lanczos",
        "-c:v", "libwebp_anim", "-lossless", "0", "-quality", "75", "-loop", "0", "-y", output,
    ];

    /// <summary>A JPEG thumbnail from the middle of a video, 480 px wide.</summary>
    public static List<string> Thumbnail(string video, double atSeconds, string output) =>
    [
        .. Common, "-ss", S(Math.Max(0, atSeconds)), "-i", video, "-frames:v", "1",
        "-vf", "scale='min(480,iw)':-2", "-q:v", "3", "-y", output,
    ];

    /// <summary>Encodes one raw BGRA frame from stdin to JPG or WebP.</summary>
    public static List<string> StillFromRaw(int width, int height, ImageFormat format, int quality, string output)
    {
        List<string> args = [.. Common, "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{I(width)}x{I(height)}", "-i", "pipe:0", "-frames:v", "1"];
        var q = Math.Max(1, Math.Min(100, quality));
        if (format == ImageFormat.Jpg)
            args.AddRange(["-q:v", I(Math.Max(1, Math.Min(31, (int)Math.Round(31 - q * 0.3)))), "-pix_fmt", "yuvj444p"]);
        else
            args.AddRange(["-c:v", "libwebp", "-quality", I(q)]);
        args.AddRange(["-y", output]);
        return args;
    }
}
