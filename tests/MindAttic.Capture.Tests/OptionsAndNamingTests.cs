using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

public class CaptureArgsTests
{
    private static CaptureArgs P(params string[] args) => CaptureArgs.Parse(args, environment: "");

    [Test]
    public void Bare_flag_means_video()
    {
        foreach (var flag in new[] { "-capture", "--capture", "/capture" })
        {
            var a = P("-demo", flag);
            Assert.That((a.Video, a.ScreenInterval), Is.EqualTo((true, (TimeSpan?)null)), flag);
        }
    }

    [Test]
    public void Video_word() => Assert.That(P("-capture", "video", "-demo").Video, Is.True);

    [TestCase("5", 5.0)]
    [TestCase("2.5", 2.5)]
    [TestCase("500ms", 0.5)]
    [TestCase("1m", 60.0)]
    public void Screen_with_interval(string interval, double seconds)
    {
        var a = P("-capture", "screen", interval, "-demo");
        Assert.That(a.Video, Is.False);
        Assert.That(a.ScreenInterval, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [Test]
    public void Screen_without_interval_defaults_to_five_seconds_and_says_so()
    {
        var bare = P("-capture", "screen");
        Assert.That((bare.ScreenInterval, bare.ScreenIntervalSpecified), Is.EqualTo(((TimeSpan?)TimeSpan.FromSeconds(5), false)));
        Assert.That(P("-capture", "screen", "5").ScreenIntervalSpecified, Is.True);
    }

    [Test]
    public void Both_modes_and_quality_combine()
    {
        var a = P("-capture", "video", "screen", "10", "hq");
        Assert.That((a.Video, a.ScreenInterval, a.Quality), Is.EqualTo((true, (TimeSpan?)TimeSpan.FromSeconds(10), (TapeQuality?)TapeQuality.HighQuality)));
        var legacy = P("-capture=hq");
        Assert.That((legacy.Video, legacy.Quality), Is.EqualTo((true, (TapeQuality?)TapeQuality.HighQuality)));
    }

    [Test]
    public void Other_args_are_left_alone()
    {
        Assert.That(P("-demo", "level3").Any, Is.False);
        Assert.That(P("capture", "video").Any, Is.False);
    }

    [TestCase(@"D:\shots")]
    [TestCase("shots")]
    [TestCase("C:/out")]
    public void Another_apps_capture_folder_flag_is_left_alone(string folder)
    {
        var a = P("-capture", folder);
        Assert.That((a.Any, a.Ignored), Is.EqualTo((false, folder)));
    }

    [Test]
    public void Bare_flag_before_another_flag_is_still_video() => Assert.That(P("-capture", "-demo").Video, Is.True);

    [Test]
    public void Parses_config_path_in_both_forms()
    {
        Assert.That(P("-capture-config", "a.json").ConfigPath, Is.EqualTo("a.json"));
        Assert.That(P("--capture-config=b.json").ConfigPath, Is.EqualTo("b.json"));
    }

    [Test]
    public void Environment_variable_takes_the_same_words_or_kills()
    {
        Assert.That(CaptureArgs.Parse([], "1").Video, Is.True);
        Assert.That(CaptureArgs.Parse([], "screen 3").ScreenInterval, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.That(CaptureArgs.Parse([], "hq").Quality, Is.EqualTo(TapeQuality.HighQuality));
        var killed = CaptureArgs.Parse(["-capture", "video"], "0");
        Assert.That((killed.KillSwitch, killed.Any), Is.EqualTo((true, false)));
        Assert.That(new CaptureOptions().Apply(killed).Enabled, Is.False);
    }

    [Test]
    public void Options_pick_up_both_modes()
    {
        var o = new CaptureOptions().Apply(P("-capture", "video", "screen", "5"));
        Assert.That((o.SessionRecording, o.ScreenshotInterval), Is.EqualTo((true, (TimeSpan?)TimeSpan.FromSeconds(5))));
    }

    [TestCase("ultra")]
    [TestCase("screen", "50ms")]
    public void Bad_words_fail_loudly(params string[] words) =>
        Assert.Throws<CaptureException>(() => CaptureArgs.Parse(["-capture=" + string.Join(" ", words)], ""));
}

public class DurationTextTests
{
    [TestCase("500ms", 0.5)]
    [TestCase("5s", 5)]
    [TestCase("2.5s", 2.5)]
    [TestCase("3m", 180)]
    [TestCase("1h", 3600)]
    [TestCase("7", 7)]
    public void Parses(string text, double seconds) => Assert.That(DurationText.Parse(text).TotalSeconds, Is.EqualTo(seconds).Within(1e-9));

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("-5s")]
    public void Rejects(string text) => Assert.That(DurationText.TryParse(text, out _), Is.False);
}

public class OutputManagerTests
{
    private string _dir = "";

    [SetUp]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("capture-tests-").FullName;

    [TearDown]
    public void Cleanup() => Directory.Delete(_dir, true);

    private OutputManager Manager(string template = "{date}_{session}_{tag}_{index}", string? root = null) =>
        new(new CaptureOptions { OutputRoot = root ?? _dir, AppName = "My App", FileTemplate = template },
            "test", new SystemTextCaptureJson(), new TraceCaptureLog(), () => new DateTimeOffset(2026, 10, 11, 14, 30, 5, 123, TimeSpan.Zero));

    [Test]
    public void Expands_template_tokens()
    {
        var m = Manager("{app}-{kind}-{date}-{time}-{tag}-{index}-{session}");
        var name = m.Expand("{app}-{kind}-{date}-{time}-{tag}-{index}-{timestamp}", CaptureKind.Replay, "Boss Death!", 7);
        Assert.That(name, Is.EqualTo("My_App-replay-2026-10-11-143005_123-Boss-Death-007-2026-10-11T14.30.05.123"));
    }

    [Test]
    public void Captures_go_straight_into_the_app_folder_named_by_app_and_iso_timestamp()
    {
        var m = Manager(new CaptureOptions().FileTemplate);
        Assert.That(m.SessionDir, Is.EqualTo(Path.Combine(_dir, "My_App")));
        Assert.That(m.SessionId, Is.EqualTo("2026-10-11T14.30.05.123"));
        Assert.That(m.SessionStem, Is.EqualTo(Path.Combine(_dir, "My_App", "My_App_2026-10-11T14.30.05.123")));
        Assert.That(m.NextStem(CaptureKind.Screenshot, "x"), Is.EqualTo(Path.Combine(_dir, "My_App", "My_App_2026-10-11T14.30.05.123")));
        Assert.That(m.NextStem(CaptureKind.Screenshot, "x"), Is.EqualTo(Path.Combine(_dir, "My_App", "My_App_2026-10-11T14.30.05.123_2")));
    }

    [Test]
    public void Iso_timestamp_uses_dots_instead_of_colons() =>
        Assert.That(CaptureNaming.Timestamp(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero)), Is.EqualTo("2026-01-02T03.04.05.006"));

    [Test]
    public void Repo_name_is_the_nearest_folder_with_git()
    {
        var repo = Path.Combine(_dir, "GridGame2026");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var bin = Directory.CreateDirectory(Path.Combine(repo, "src", "App", "bin", "Debug")).FullName;
        Assert.That(CaptureNaming.RepoName(bin), Is.EqualTo("GridGame2026"));
        Assert.That(CaptureNaming.RepoName(Path.GetTempPath()), Is.Null.Or.Not.EqualTo("GridGame2026"));
        Assert.That(new CaptureOptions().OutputRoot, Is.EqualTo(@"R:\MindAttic\Capture"));
    }

    [Test]
    public void Stems_are_unique_and_indexed()
    {
        var m = Manager("{tag}");
        var a = m.NextStem(CaptureKind.Screenshot, "x");
        File.WriteAllText(a + ".png", "");
        var b = m.NextStem(CaptureKind.Screenshot, "x");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [TestCase("critical-hit", "critical-hit")]
    [TestCase("  boss death  ", "boss-death")]
    [TestCase("../../etc/passwd", "etc-passwd")]
    [TestCase("", "capture")]
    [TestCase("日本", "capture")]
    public void Sanitizes_tags(string tag, string expected) => Assert.That(OutputManager.SanitizeTag(tag), Is.EqualTo(expected));

    [Test]
    public void Falls_back_when_the_drive_is_missing()
    {
        var missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").First(d => !Directory.Exists(d));
        var fallback = Path.Combine(_dir, "fallback");
        var root = OutputManager.ResolveRoot(Path.Combine(missing, "Recording"), fallback, new TraceCaptureLog());
        Assert.That(root, Is.EqualTo(fallback));
    }

    [Test]
    public void Fails_loudly_when_no_root_works()
    {
        var missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").First(d => !Directory.Exists(d));
        Assert.Throws<CaptureException>(() => OutputManager.ResolveRoot(missing + "a", missing + "b", new TraceCaptureLog()));
    }
}

public class ResourceValveTests
{
    private static ValveSample S(double? disk = 5000, double? mem = 16000, double? enc = 300, double? lag = 1, double minutes = 1) =>
        new(TimeSpan.FromMinutes(minutes), disk, mem, 20, 400, enc, lag, minutes * 60, 100, 0, 30);

    [Test]
    public void Healthy_sample_keeps_the_valve_open() =>
        Assert.That(ResourceValve.Check(S(), new CaptureLimits()), Is.Null);

    [Test]
    public void Each_default_limit_closes_the_valve()
    {
        var l = new CaptureLimits { MaxDuration = TimeSpan.FromHours(2) };
        Assert.That(ResourceValve.Check(S(disk: 49), l), Does.Contain("GB free"));
        Assert.That(ResourceValve.Check(S(mem: 1000), l), Does.Contain("memory"));
        Assert.That(ResourceValve.Check(S(enc: 5000), l), Does.Contain("backing up"));
        Assert.That(ResourceValve.Check(S(lag: 61), l), Does.Contain("behind"));
        Assert.That(ResourceValve.Check(S(minutes: 120), l), Does.Contain("maximum duration"));
    }

    [Test]
    public void Unknown_readings_and_disabled_limits_never_trip()
    {
        var off = new CaptureLimits { MinFreeDiskGB = null, MinFreeMemoryMB = null, MaxEncoderMemoryMB = null, MaxEncoderLagSeconds = null };
        Assert.That(ResourceValve.Check(S(disk: 1, mem: 1, enc: 99999, lag: 999), off), Is.Null);
        Assert.That(ResourceValve.Check(S(disk: null, mem: null, enc: null, lag: null), new CaptureLimits()), Is.Null);
    }

    [Test]
    public void Video_duration_word_and_config_set_the_limits()
    {
        var a = CaptureArgs.Parse(["-capture", "video", "8h", "hq"], environment: "");
        Assert.That((a.Video, a.MaxDuration, a.Quality), Is.EqualTo((true, (TimeSpan?)TimeSpan.FromHours(8), (TapeQuality?)TapeQuality.HighQuality)));
        Assert.That(new CaptureOptions().Apply(a).Limits.MaxDuration, Is.EqualTo(TimeSpan.FromHours(8)));

        var o = new CaptureOptions().Apply(new CaptureConfigFile
        {
            Limits = new LimitsConfig { MinFreeDiskGB = 0, MaxEncoderMemoryMB = 8192, MaxDuration = "90m", PollEvery = "2s" },
        });
        Assert.That((o.Limits.MinFreeDiskGB, o.Limits.MaxEncoderMemoryMB, o.Limits.MaxDuration, o.Limits.PollInterval),
            Is.EqualTo(((double?)null, (double?)8192, (TimeSpan?)TimeSpan.FromMinutes(90), TimeSpan.FromSeconds(2))));
    }
}

public class FfmpegLocatorTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "mac-shim-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Test]
    public void Chocolatey_shim_resolves_to_the_real_binary()
    {
        var shim = Touch("chocolatey", "bin", "ffmpeg.exe");
        var real = Touch("chocolatey", "lib", "ffmpeg", "tools", "ffmpeg", "bin", "ffmpeg.exe");
        Assert.That(FfmpegLocator.ResolveShim(shim), Is.EqualTo(real));
    }

    [Test]
    public void Scoop_shim_resolves_through_its_shim_file()
    {
        var shim = Touch("scoop", "shims", "ffmpeg.exe");
        var real = Touch("scoop", "apps", "ffmpeg", "current", "bin", "ffmpeg.exe");
        File.WriteAllText(Path.Combine(_root, "scoop", "shims", "ffmpeg.shim"), $"path = \"{real}\"\n");
        Assert.That(FfmpegLocator.ResolveShim(shim), Is.EqualTo(real));
    }

    [Test]
    public void Plain_binary_is_returned_unchanged()
    {
        var exe = Touch("tools", "bin", "ffmpeg.exe");
        Assert.That(FfmpegLocator.ResolveShim(exe), Is.EqualTo(exe));
    }
}

public class FfmpegArgsTests
{
    [Test]
    public void Concat_list_escapes_quotes_and_writes_trim_points()
    {
        var text = FfmpegArgs.ConcatList([(@"C:\a'b\seg1.mkv", 0.25, null), (@"C:\seg2.mkv", null, 0.5)]);
        Assert.That(text, Is.EqualTo("file 'C:\\a'\\''b\\seg1.mkv'\ninpoint 0.25\nfile 'C:\\seg2.mkv'\noutpoint 0.5\n"));
    }

    [Test]
    public void Chapters_run_marker_to_marker_and_escape()
    {
        var text = FfmpegArgs.Chapters([("b=2", 2.0), ("a;1", 1.0), ("late", 9.0)], 5.0);
        Assert.That(text, Is.EqualTo(";FFMETADATA1\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=a\\;1\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=5000\ntitle=b\\=2\n"));
    }

    [Test]
    public void Export_skips_or_delays_audio_to_match_the_range()
    {
        var early = new TapeAudioTrack { Format = "f32le", SampleRate = 48000, Channels = 2, OffsetSeconds = 0.2 };
        var late = new TapeAudioTrack { Format = "s16le", SampleRate = 44100, Channels = 1, OffsetSeconds = 3.5 };
        var args = FfmpegArgs.ExportRange("list.txt", 3.0, 4.0, [("a0.raw", early), ("a1.raw", late)], separateAudio: false, null, "out.mkv");
        var joined = string.Join(" ", args);
        Assert.That(joined, Does.Contain("-ss 2.8 -f f32le -ar 48000 -ac 2 -i a0.raw"));
        Assert.That(joined, Does.Contain("-f s16le -ar 44100 -ac 1 -i a1.raw"));
        Assert.That(joined, Does.Contain("[2:a]adelay=delays=500:all=1[a1]"));
        Assert.That(joined, Does.Contain("amix=inputs=2"));
        Assert.That(joined, Does.EndWith("-c:v copy -t 4 -y out.mkv"));
    }

    [Test]
    public void Tape_is_lossless_ffv1_in_one_second_segments()
    {
        var joined = string.Join(" ", FfmpegArgs.Tape(["-i", "x"], TapeQuality.Lossless, "hevc_nvenc", 60, @"C:\t"));
        Assert.That(joined, Does.Contain("-c:v ffv1 -level 3 -g 1"));
        Assert.That(joined, Does.Contain("-pix_fmt bgr0"));
        Assert.That(joined, Does.Contain("-f segment -segment_time 1"));
        Assert.That(joined, Does.Contain(@"-segment_list C:\t\tape.csv -segment_list_type csv"));
    }

    [TestCase("h264_nvenc", "-cq 20")]
    [TestCase("h264_qsv", "-global_quality 20")]
    [TestCase("h264_amf", "-qp_i 20")]
    [TestCase("libx264", "-crf 20")]
    public void Encoder_args_follow_the_family(string encoder, string expected) =>
        Assert.That(string.Join(" ", FfmpegArgs.VideoEncoder(encoder, 20)), Does.Contain(expected));

    [Test]
    public void Presets_pick_outputs_and_overrides_apply()
    {
        Assert.That(PresetSpec.For(CapturePreset.Trailer).Outputs, Is.EqualTo(OutputFormats.Mkv | OutputFormats.Mp4));
        Assert.That(PresetSpec.For(CapturePreset.BugReport), Is.EqualTo(new PresetSpec(OutputFormats.Mp4, 28, 720, 30)));
        var custom = PresetSpec.For(CapturePreset.Devlog, new RecordingOptions { Outputs = OutputFormats.Gif, Quality = 10, OutputHeight = 480 });
        Assert.That(custom, Is.EqualTo(new PresetSpec(OutputFormats.Gif, 10, 480, null)));
    }
}

public class TapeSegmentTests
{
    [Test]
    public void Parses_finished_lines_and_ignores_partial_ones()
    {
        var segs = TapeSegments.Parse("seg00000000.mkv,0.000000,1.000000\nseg00000001.mkv,1.000000,2.000000\nseg0000000", @"C:\t");
        Assert.That(segs, Has.Count.EqualTo(2));
        Assert.That(segs[1], Is.EqualTo(new TapeSegment(@"C:\t\seg00000001.mkv", 1, 2)));
    }

    [Test]
    public void Selects_covering_segments_with_trim_points()
    {
        var dir = Directory.CreateTempSubdirectory("tape-").FullName;
        try
        {
            var segs = Enumerable.Range(0, 5).Select(i =>
            {
                var f = Path.Combine(dir, $"seg{i}.mkv");
                File.WriteAllText(f, "");
                return new TapeSegment(f, i, i + 1);
            }).ToList();
            File.Delete(segs[0].File);
            var (parts, start, end) = TapeSegments.Select(segs, 0.5, 3.25);
            Assert.That(parts.Select(p => Path.GetFileName(p.File)), Is.EqualTo(new[] { "seg1.mkv", "seg2.mkv", "seg3.mkv" }));
            Assert.That(parts[0].InPoint, Is.Null);
            Assert.That(parts[2].OutPoint, Is.EqualTo(0.25).Within(1e-9));
            Assert.That(start, Is.EqualTo(1));
            Assert.That(end, Is.EqualTo(3.25));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
