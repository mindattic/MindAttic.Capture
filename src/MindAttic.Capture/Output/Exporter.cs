using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Tape;

namespace MindAttic.Capture.Output;

/// <summary>What to cut from a tape and what to produce.</summary>
internal sealed record ExportJob(
    string TapeDir,
    TapeState State,
    IReadOnlyList<TapeSegment> Segments,
    IReadOnlyList<(double Start, double End)> Ranges,
    string Stem,
    PresetSpec Spec,
    IReadOnlyList<MarkerRecord> Markers,
    bool SeparateAudio);

internal sealed record ExportOutput(IReadOnlyList<string> Files, double DurationSeconds, IReadOnlyList<SidecarMarker> Markers, string? Thumbnail);

/// <summary>Cuts tape ranges into a master .mkv, derives MP4/WebM/GIF/WebP and a thumbnail.</summary>
internal sealed class Exporter(string ffmpeg, Func<Task<EncoderSet>> encoders, ICaptureLog log)
{
    public async Task<ExportOutput> ExportAsync(ExportJob job, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(job.Stem)!;
        Directory.CreateDirectory(dir);
        var work = Path.Combine(job.TapeDir, "export_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        try
        {
            var master = job.Stem + ".mkv";
            var (duration, markers) = await CutAsync(job, work, master, ct).ConfigureAwait(false);

            var files = new List<string>();
            if (job.Spec.Outputs.HasFlag(OutputFormats.Mkv)) files.Add(master);
            if (job.Spec.Outputs.HasFlag(OutputFormats.Mp4))
            {
                var set = await encoders().ConfigureAwait(false);
                var mp4 = job.Stem + ".mp4";
                // Hardware H.264 encoders top out at 4096 pixels wide; go straight to x264 beyond that.
                var outWidth = job.Spec.MaxHeight is { } mh && mh < job.State.Height ? job.State.Width * mh / job.State.Height : job.State.Width;
                var h264 = outWidth > 4096 ? "libx264" : set.H264;
                if (await TryAsync("MP4", () => Mp4Async(master, h264, job.Spec, mp4, ct)).ConfigureAwait(false)) files.Add(mp4);
            }
            if (job.Spec.Outputs.HasFlag(OutputFormats.WebM) &&
                await TryAsync("WebM", () => FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.WebM(master, job.Spec, job.Stem + ".webm"), log, ct)).ConfigureAwait(false))
                files.Add(job.Stem + ".webm");
            if (job.Spec.Outputs.HasFlag(OutputFormats.Gif) &&
                await TryAsync("GIF", () => FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Gif(master, job.Stem + ".gif"), log, ct)).ConfigureAwait(false))
                files.Add(job.Stem + ".gif");
            if (job.Spec.Outputs.HasFlag(OutputFormats.WebP) &&
                await TryAsync("WebP", () => FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.AnimatedWebP(master, job.Stem + ".webp"), log, ct)).ConfigureAwait(false))
                files.Add(job.Stem + ".webp");

            var thumb = job.Stem + ".thumb.jpg";
            var thumbOk = await TryAsync("thumbnail",
                () => FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Thumbnail(master, duration / 2, thumb), log, ct)).ConfigureAwait(false);

            if (!job.Spec.Outputs.HasFlag(OutputFormats.Mkv))
            {
                if (files.Count == 0) files.Add(master);
                else TryDelete(master);
            }
            return new ExportOutput(files, duration, markers, thumbOk ? thumb : null);
        }
        finally
        {
            TryDeleteDir(work);
        }
    }

    private async Task Mp4Async(string master, string h264, PresetSpec spec, string mp4, CancellationToken ct)
    {
        try
        {
            await FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Mp4(master, h264, spec, mp4), log, ct).ConfigureAwait(false);
        }
        catch (CaptureException) when (h264 != "libx264")
        {
            log.Warn($"{h264} failed for the MP4; retrying with libx264.");
            await FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.Mp4(master, "libx264", spec, mp4), log, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryAsync(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (CaptureException ex)
        {
            log.Warn($"Could not produce the {what}; the master is unaffected. {ex.Message}");
            return false;
        }
    }

    /// <summary>Exports each range to a part (video copy + audio + chapters) and joins parts when there are several.</summary>
    private async Task<(double Duration, IReadOnlyList<SidecarMarker> Markers)> CutAsync(ExportJob job, string work, string master, CancellationToken ct)
    {
        var parts = new List<string>();
        var allMarkers = new List<SidecarMarker>();
        double total = 0;
        for (var i = 0; i < job.Ranges.Count; i++)
        {
            var (a, b) = job.Ranges[i];
            // Video is stream-copied, so it can only start on a keyframe. HQ tapes have one per segment: start the
            // range there, or the concat demuxer emits video from the keyframe while audio starts at the inpoint.
            if (job.State.Quality != TapeQuality.Lossless)
                a = job.Segments.LastOrDefault(s => s.Start <= a && s.End > a && File.Exists(s.File))?.Start ?? a;
            var (pieces, coveredStart, coveredEnd) = TapeSegments.Select(job.Segments, a, b);
            if (pieces.Count == 0) continue;
            var duration = coveredEnd - coveredStart;
            var list = Path.Combine(work, $"range{i}.txt");
            File.WriteAllText(list, FfmpegArgs.ConcatList(pieces));
            var rangeMarkers = job.Markers
                .Where(m => m.TapeSeconds is { } t && t >= coveredStart && t < coveredEnd)
                .Select(m => new SidecarMarker { Name = m.Name, OffsetSeconds = total + (m.TapeSeconds!.Value - coveredStart), Data = m.Data })
                .ToList();
            allMarkers.AddRange(rangeMarkers);

            var single = job.Ranges.Count == 1;
            string? chapters = null;
            if (single && rangeMarkers.Count > 0)
            {
                chapters = Path.Combine(work, "chapters.txt");
                File.WriteAllText(chapters, FfmpegArgs.Chapters(rangeMarkers.Select(m => (m.Name, m.OffsetSeconds - total)).ToList(), duration));
            }
            var audio = job.State.Audio.Select(t => (Path.Combine(job.TapeDir, t.File), t)).Where(x => File.Exists(x.Item1)).ToList();
            var output = single ? master : Path.Combine(work, $"part{i}.mkv");
            await FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.ExportRange(list, coveredStart, duration, audio, job.SeparateAudio, chapters, output), log, ct)
                .ConfigureAwait(false);
            parts.Add(output);
            total += duration;
        }
        if (parts.Count == 0)
            throw new CaptureException(CaptureError.CaptureFailed, "No tape footage covers the requested time range.");
        if (job.Ranges.Count > 1)
        {
            var list = Path.Combine(work, "parts.txt");
            File.WriteAllText(list, FfmpegArgs.ConcatList(parts.Select(p => (p, (double?)null, (double?)null)).ToList()));
            string? chapters = null;
            if (allMarkers.Count > 0)
            {
                chapters = Path.Combine(work, "chapters.txt");
                File.WriteAllText(chapters, FfmpegArgs.Chapters(allMarkers.Select(m => (m.Name, m.OffsetSeconds)).ToList(), total));
            }
            await FfmpegProcess.RunAsync(ffmpeg, FfmpegArgs.JoinParts(list, chapters, master), log, ct).ConfigureAwait(false);
        }
        return (total, allMarkers);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
