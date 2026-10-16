using System.Diagnostics;
using System.Globalization;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

[NonParallelizable]
public class ExporterTests
{
    private string _root = "";
    private string? _ffmpeg;
    private readonly ICaptureLog _log = new TraceCaptureLog();
    private readonly ICaptureJson _json = new SystemTextCaptureJson();

    [OneTimeSetUp]
    public void FindFfmpeg()
    {
        _ffmpeg = FfmpegLocator.TryFind("ffmpeg");
        if (_ffmpeg is null) Assert.Ignore("ffmpeg is not installed; Exporter tests need it.");
    }

    [SetUp]
    public void Setup() => _root = Directory.CreateTempSubdirectory("capture-exporter-").FullName;

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static double Probe(string file, string entry)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.Find("ffprobe"), $"-v error -show_entries {entry} -of default=nw=1:nk=1 \"{file}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim().Split('\n')[0];
        p.WaitForExit();
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    private Exporter NewExporter(string h264 = "libx264") => new(_ffmpeg!, () => Task.FromResult(new EncoderSet(h264, h264)), _log);

    /// <summary>Records a short real lossless tape, so Exporter has real segments to cut from.</summary>
    private async Task<(string TapeDir, TapeState State, IReadOnlyList<TapeSegment> Segments)> RecordTapeAsync(double seconds)
    {
        var dir = Path.Combine(_root, "tape");
        var state = new TapeState
        {
            AppName = "Test", SessionId = "s", SessionDir = _root, SessionStem = Path.Combine(_root, "s"),
            FfmpegPath = _ffmpeg!, CreatedAt = DateTimeOffset.Now, Fps = 30, Quality = TapeQuality.Lossless, Encoder = "ffv1",
        };
        var source = new SyntheticSource(160, 90, 30, realTime: true);
        var tape = await CaptureTape.StartAsync(dir, state, source, [], _log, _json, default);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await tape.StopAsync();
        return (dir, tape.State, tape.Segments());
    }

    [Test]
    public async Task Each_requested_format_and_a_thumbnail_are_produced()
    {
        var (tapeDir, state, segments) = await RecordTapeAsync(2.5);
        var stem = Path.Combine(_root, "out", "clip");
        var spec = new PresetSpec(OutputFormats.Mkv | OutputFormats.Mp4 | OutputFormats.WebM | OutputFormats.Gif | OutputFormats.WebP, 20, null, null);
        var job = new ExportJob(tapeDir, state, segments, [(0.0, segments[^1].End)], stem, spec, [], false);

        var output = await NewExporter().ExportAsync(job, default);

        Assert.That(output.Files, Is.EquivalentTo(new[] { stem + ".mkv", stem + ".mp4", stem + ".webm", stem + ".gif", stem + ".webp" }));
        foreach (var f in output.Files) Assert.That(File.Exists(f), Is.True, f);
        Assert.That(output.Thumbnail, Is.EqualTo(stem + ".thumb.jpg"));
        Assert.That(File.Exists(output.Thumbnail!), Is.True);
    }

    [Test]
    public async Task A_failing_format_is_skipped_without_losing_the_master_or_other_formats()
    {
        var (tapeDir, state, segments) = await RecordTapeAsync(1.5);
        var stem = Path.Combine(_root, "out", "clip");
        // Fps=0 makes Mp4's "-vf fps=0,..." filter invalid, so ffmpeg rejects it; Gif's own filter hardcodes
        // its own fps and ignores spec.Fps, so it proves a failing format doesn't take the others down with it.
        var spec = new PresetSpec(OutputFormats.Mkv | OutputFormats.Mp4 | OutputFormats.Gif, 20, null, 0);
        var job = new ExportJob(tapeDir, state, segments, [(0.0, segments[^1].End)], stem, spec, [], false);

        var output = await NewExporter().ExportAsync(job, default);

        Assert.That(output.Files, Does.Contain(stem + ".mkv"));
        Assert.That(output.Files, Does.Contain(stem + ".gif"));
        Assert.That(output.Files, Has.None.EqualTo(stem + ".mp4"));
        Assert.That(File.Exists(stem + ".mkv"), Is.True);
        Assert.That(File.Exists(stem + ".gif"), Is.True);
    }

    [Test]
    public async Task Multi_range_jobs_join_parts_and_report_matching_duration_and_markers()
    {
        var (tapeDir, state, segments) = await RecordTapeAsync(4.0);
        var stem = Path.Combine(_root, "out", "clip");
        var spec = new PresetSpec(OutputFormats.Mkv, 16, null, null);
        var markers = new List<MarkerRecord>
        {
            new() { Name = "start", TapeSeconds = 0.4 },
            new() { Name = "mid", TapeSeconds = 2.4 },
        };
        var ranges = new List<(double, double)> { (0.0, 1.0), (2.0, 3.0) };
        var job = new ExportJob(tapeDir, state, segments, ranges, stem, spec, markers, false);

        var output = await NewExporter().ExportAsync(job, default);

        Assert.That(output.Files, Is.EquivalentTo(new[] { stem + ".mkv" }));
        Assert.That(Probe(output.Files[0], "format=duration"), Is.EqualTo(2.0).Within(0.3));
        Assert.That(output.DurationSeconds, Is.EqualTo(2.0).Within(0.3));
        Assert.That(output.Markers.Select(m => m.Name), Is.EqualTo(new[] { "start", "mid" }));
        Assert.That(output.Markers[0].OffsetSeconds, Is.EqualTo(0.4).Within(0.3));
        Assert.That(output.Markers[1].OffsetSeconds, Is.EqualTo(1.4).Within(0.3));
    }
}
