using System.Diagnostics;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

/// <summary>A host that renders a moving gradient, so engine tests need ffmpeg but no desktop.</summary>
internal sealed class SyntheticHost(int width = 320, int height = 180, bool realTime = true) : ICaptureHost
{
    public string Name => "synthetic";
    public HostCapabilities Capabilities => HostCapabilities.Transparency;
    public SyntheticSource? Source { get; private set; }

    public ValueTask<CapturedImage> CaptureStillAsync(ScreenshotOptions options, CancellationToken ct)
    {
        var pixels = SyntheticSource.Render(width, height, 0);
        if (options.TransparentBackground) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = (byte)(i % 255);
        return new(new CapturedImage(width, height, pixels, options.TransparentBackground));
    }

    public VideoSource CreateVideoSource(int fps) => Source = new SyntheticSource(width, height, fps, realTime);

    public IReadOnlyList<IAudioTrackCapture> CreateAudioTracks(AudioMode mode) => [];
}

internal sealed class SyntheticSource(int width, int height, int fps, bool realTime) : PushVideoSource
{
    private CancellationTokenSource? _cts;
    private Task? _pump;
    public override int Width => width;
    public override int Height => height;
    public override bool RealTime => realTime;
    public override string Description => "synthetic";
    public long Frames;

    public static byte[] Render(int w, int h, long frame)
    {
        var p = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            p[i] = (byte)(x + frame);
            p[i + 1] = (byte)(y * 2);
            p[i + 2] = (byte)(frame * 3);
            p[i + 3] = 255;
        }
        return p;
    }

    public override Task StartAsync(IFrameSink sink, CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pump = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                sink.WriteFrame(Render(width, height, Frames));
                Frames++;
                var due = TimeSpan.FromSeconds(Frames / (double)fps) - sw.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due).ContinueWith(_ => { });
            }
        });
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        _cts?.Cancel();
        if (_pump is not null) await _pump;
    }
}

[NonParallelizable]
public class EngineTests
{
    private string _root = "";
    private string? _ffmpeg;

    [OneTimeSetUp]
    public void FindFfmpeg()
    {
        _ffmpeg = FfmpegLocator.TryFind("ffmpeg");
        if (_ffmpeg is null) Assert.Ignore("ffmpeg is not installed; engine tests need it.");
    }

    [SetUp]
    public void Setup() => _root = Directory.CreateTempSubdirectory("capture-engine-").FullName;

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private CaptureOptions Options(Action<CaptureOptions>? configure = null)
    {
        var o = new CaptureOptions { OutputRoot = _root, AppName = "Test", Fps = 30, FfmpegPath = _ffmpeg! };
        configure?.Invoke(o);
        return o;
    }

    private static double Probe(string file, string entry)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.Find("ffprobe"), $"-v error -count_packets -show_entries {entry} -of default=nw=1:nk=1 \"{file}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim().Split('\n')[0];
        p.WaitForExit();
        return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ProbeText(string file, string entry)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.Find("ffprobe"), $"-v error -count_packets -show_entries {entry} -of default=nw=1:nk=1 \"{file}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }

    [Test]
    public async Task Screenshot_png_has_expected_size_and_alpha()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options());
        var opaque = await engine.ScreenshotAsync("still");
        var clear = await engine.ScreenshotAsync("alpha", new ScreenshotOptions { TransparentBackground = true });
        Assert.That(opaque.Succeeded, Is.True, opaque.Error);
        Assert.That(ProbeText(opaque.PrimaryFile!, "stream=width,height,pix_fmt"), Is.EqualTo("320\n180\nrgb24").IgnoreCase.Or.EqualTo("320\r\n180\r\nrgb24"));
        Assert.That(ProbeText(clear.PrimaryFile!, "stream=pix_fmt"), Is.EqualTo("rgba"));
        Assert.That(File.Exists(opaque.PrimaryFile + ".json"), Is.True);
    }

    [Test]
    public async Task Png_round_trips_pixels_exactly()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options());
        var r = await engine.ScreenshotAsync("exact");
        var raw = Path.Combine(_root, "decoded.raw");
        await FfmpegProcess.RunAsync(_ffmpeg!, ["-v", "error", "-i", r.PrimaryFile!, "-f", "rawvideo", "-pix_fmt", "bgra", "-y", raw], new TraceCaptureLog(), default);
        Assert.That(File.ReadAllBytes(raw), Is.EqualTo(SyntheticSource.Render(320, 180, 0)));
    }

    [TestCase(ImageFormat.Jpg, ".jpg")]
    [TestCase(ImageFormat.WebP, ".webp")]
    [TestCase(ImageFormat.Bmp, ".bmp")]
    [TestCase(ImageFormat.RawBgra, ".raw")]
    public async Task Screenshot_formats(ImageFormat format, string ext)
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options());
        var r = await engine.ScreenshotAsync("f", new ScreenshotOptions { Format = format });
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(Path.GetExtension(r.PrimaryFile), Is.EqualTo(ext));
        Assert.That(new FileInfo(r.PrimaryFile!).Length, Is.GreaterThan(100));
        if (format == ImageFormat.RawBgra) Assert.That(new FileInfo(r.PrimaryFile!).Length, Is.EqualTo(320 * 180 * 4));
    }

    [Test]
    public async Task Clip_has_requested_duration()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options());
        var r = await engine.ClipAsync(TimeSpan.FromSeconds(2), "clip", new RecordingOptions { Preset = CapturePreset.Lossless });
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(ProbeText(r.PrimaryFile!, "stream=codec_name"), Is.EqualTo("ffv1"));
        Assert.That(Probe(r.PrimaryFile!, "format=duration"), Is.EqualTo(2.0).Within(0.15));
    }

    [Test]
    public async Task Replay_covers_before_and_after()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options(o => o.ReplayArmed = true));
        await Task.Delay(4000);
        var r = await engine.SaveReplayAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), "replay");
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(r.Files.Select(Path.GetExtension), Is.EquivalentTo(new[] { ".mkv", ".mp4" }));
        Assert.That(Probe(r.Files[0], "format=duration"), Is.EqualTo(4.0).Within(0.15));
        Assert.That(File.Exists(Path.ChangeExtension(r.Files[0], ".thumb.jpg")), Is.True);
    }

    [Test]
    public async Task Paused_footage_is_left_out_and_markers_become_chapters()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options());
        await engine.StartRecordingAsync("rec", new RecordingOptions { Preset = CapturePreset.Lossless });
        await Task.Delay(1500);
        engine.Marker("first");
        await Task.Delay(500);
        engine.PauseRecording();
        await Task.Delay(1500);
        engine.ResumeRecording();
        await Task.Delay(1000);
        engine.Marker("second");
        await Task.Delay(500);
        var r = await engine.StopRecordingAsync();
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(Probe(r.PrimaryFile!, "format=duration"), Is.EqualTo(3.5).Within(0.3));
        Assert.That(ProbeText(r.PrimaryFile!, "chapter=id").Split('\n'), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task Raised_events_fire_rules()
    {
        var options = Options(o =>
        {
            o.ReplayArmed = true;
            o.Rules.Add(new Triggers.TriggerRuleConfig { On = "critical-hit", Action = "replay", Before = "1s", After = "1s", Where = new() { ["damage"] = ">=500" } });
            o.Rules.Add(new Triggers.TriggerRuleConfig { On = "battle-complete", Action = "screenshot", Tag = "victory" });
        });
        var results = new List<CaptureResult>();
        await using (var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), options))
        {
            engine.Captured += r => { lock (results) results.Add(r); };
            await Task.Delay(1500);
            engine.Raise("critical-hit", new CaptureData { ["damage"] = 900 });
            engine.Raise("critical-hit", new CaptureData { ["damage"] = 10 });
            engine.Raise("battle-complete");
        }
        Assert.That(results.Select(r => (r.Kind, r.Tag, r.Trigger)), Is.EquivalentTo(new[]
        {
            (CaptureKind.Screenshot, "victory", (string?)"battle-complete"),
            (CaptureKind.Replay, "critical-hit", (string?)"critical-hit"),
        }));
        Assert.That(results.All(r => r.Succeeded), Is.True);
    }

    [Test]
    public async Task Session_is_exported_when_the_engine_closes()
    {
        string folder, session;
        await using (var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options(o => o.SessionRecording = true)))
        {
            folder = engine.SessionFolder;
            session = engine.SessionId;
            engine.Marker("boss");
            await Task.Delay(3000);
        }
        Assert.That(folder, Is.EqualTo(Path.Combine(_root, "Test")));
        Assert.That(session, Does.Match(@"^\d{4}-\d\d-\d\dT\d\d\.\d\d\.\d\d\.\d{3}$"));
        var stem = Path.Combine(folder, "Test_" + session);
        var mkv = stem + ".mkv";
        Assert.That(File.Exists(mkv), Is.True);
        Assert.That(File.Exists(stem + ".mp4"), Is.True);
        Assert.That(Probe(mkv, "format=duration"), Is.EqualTo(3.0).Within(0.5));
        Assert.That(File.ReadAllText(stem + ".session.json"), Does.Contain("\"kind\": \"session\""));
        Assert.That(Directory.Exists(Path.Combine(_root, ".staging", "Test_" + session)), Is.False);
    }

    [Test]
    public async Task Fixed_timestep_tape_time_follows_frames_not_the_clock()
    {
        var host = new SyntheticHost(realTime: false);
        await using var engine = await CaptureEngine.StartNewAsync(host, Options());
        var r = await engine.ClipAsync(TimeSpan.FromSeconds(1), "fixed", new RecordingOptions { Preset = CapturePreset.Lossless });
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(Probe(r.PrimaryFile!, "stream=nb_read_packets") , Is.EqualTo(30).Within(1));
    }

    [Test]
    public async Task Screen_interval_takes_pngs_until_closed()
    {
        string folder;
        await using (var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options(o => o.ScreenshotInterval = TimeSpan.FromMilliseconds(400))))
        {
            folder = engine.SessionFolder;
            await Task.Delay(1500);
        }
        var shots = Directory.GetFiles(folder, "*.png");
        Assert.That(shots, Has.Length.InRange(3, 4));
        Assert.That(Directory.GetFiles(folder, "*.mkv"), Is.Empty);
    }

    [Test]
    public async Task Disabled_capture_is_a_logged_no_op()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new SyntheticHost(), Options(o => o.Enabled = false));
        var r = await engine.ScreenshotAsync("x");
        engine.Raise("anything");
        Assert.That(r.Skipped, Is.True);
        Assert.That(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void Static_facade_is_safe_before_init() =>
        Assert.DoesNotThrow(() =>
        {
            Capture.Raise("boot");
            Capture.Marker("boot");
            Capture.Screenshot("boot");
        });
}
