using System.Collections.Concurrent;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Ffmpeg;

/// <summary>The encoders this machine can actually use (hardware first, software fallback).</summary>
internal sealed record EncoderSet(string H264, string Hevc)
{
    public static readonly EncoderSet Software = new("libx264", "libx265");
}

/// <summary>Finds working hardware encoders by test-encoding a few frames with each candidate.</summary>
internal static class EncoderProbe
{
    public static readonly IReadOnlyList<string> H264Candidates = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
    public static readonly IReadOnlyList<string> HevcCandidates = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"];

    private static readonly ConcurrentDictionary<string, Task<EncoderSet>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<EncoderSet> ProbeAsync(string ffmpeg, ICaptureLog log) =>
        Cache.GetOrAdd(ffmpeg, path => ProbeUncachedAsync(path, log));

    private static async Task<EncoderSet> ProbeUncachedAsync(string ffmpeg, ICaptureLog log)
    {
        var h264 = await FirstWorkingAsync(ffmpeg, H264Candidates, log).ConfigureAwait(false);
        var hevc = await FirstWorkingAsync(ffmpeg, HevcCandidates, log).ConfigureAwait(false);
        var set = new EncoderSet(h264, hevc);
        log.Info($"Encoders: H.264 = {set.H264}, HEVC = {set.Hevc}");
        return set;
    }

    internal static async Task<string> FirstWorkingAsync(string ffmpeg, IReadOnlyList<string> candidates, ICaptureLog log)
    {
        foreach (var encoder in candidates)
        {
            try
            {
                await FfmpegProcess.RunAsync(ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.2",
                     "-frames:v", "3", "-c:v", encoder, "-f", "null", "-"],
                    log, CancellationToken.None).ConfigureAwait(false);
                return encoder;
            }
            catch (CaptureException ex)
            {
                log.Debug($"Encoder {encoder} unavailable: {ex.Message.Split('\n').LastOrDefault()?.Trim()}");
            }
        }
        return candidates[candidates.Count - 1];
    }
}
