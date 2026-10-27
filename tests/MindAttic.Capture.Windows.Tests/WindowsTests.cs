using System.IO;
using System.Diagnostics;
using MindAttic.Capture.Agent;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Windows;
using MindAttic.Capture.Windows.Audio;
using MindAttic.Capture.Windows.Native;
using MindAttic.Capture.Windows.Video;
using MindAttic.Capture.Windows.Wgc;
using NAudio.Wave;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class BrowserLocatorTests
{
    [Test]
    public void Capture_always_drives_the_browser_deployed_in_C_Apps() =>
        Assert.That(BrowserLocator.DeployedPath, Is.EqualTo(@"C:\Apps\MindAttic.Browser\MindAttic.Browser.exe"));

    [Test]
    public void A_missing_deployment_fails_loudly_with_how_to_deploy()
    {
        var ex = Assert.Throws<CaptureException>(() => BrowserLocator.Require(@"Z:\nowhere\MindAttic.Browser.exe"));
        Assert.That(ex!.Message, Does.Contain("run.ps1"));
        Assert.That(BrowserLocator.Require(Environment.ProcessPath!), Is.EqualTo(Environment.ProcessPath));
    }
}

public class DesktopInputTests
{
    private static readonly PixelRect[] TwoOutputs = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];

    [Test]
    public void Uses_ddagrab_relative_to_the_containing_output_then_gdigrab()
    {
        var list = DesktopInputs.Candidates(new PixelRect(2000, 100, 800, 600), TwoOutputs, 60, true, false);
        Assert.That(list.Select(c => c.Name), Is.EqualTo(new[] { "ddagrab", "gdigrab" }));
        Assert.That(list[0].Args[3], Is.EqualTo("ddagrab=output_idx=1:framerate=60:draw_mouse=1:offset_x=80:offset_y=100:video_size=800x600,hwdownload,format=bgra"));
        Assert.That(string.Join(" ", list[1].Args), Does.Contain("-offset_x 2000 -offset_y 100 -video_size 800x600 -i desktop"));
    }

    [Test]
    public void Spanning_rect_or_forced_gdi_skips_ddagrab()
    {
        Assert.That(DesktopInputs.Candidates(new PixelRect(1800, 0, 400, 400), TwoOutputs, 30, false, false).Select(c => c.Name), Is.EqualTo(new[] { "gdigrab" }));
        Assert.That(DesktopInputs.Candidates(new PixelRect(0, 0, 100, 100), TwoOutputs, 30, false, true).Select(c => c.Name), Is.EqualTo(new[] { "gdigrab" }));
    }
}

public class TargetResolverTests
{
    private static readonly IReadOnlyList<MonitorInfo> Layout = Desktop.Order(
    [
        (new PixelRect(1920, 0, 1920, 1080), false, "B"),
        (new PixelRect(-1280, 0, 1280, 1024), false, "C"),
        (new PixelRect(0, 0, 1920, 1080), true, "A"),
    ]);

    [Test]
    public void Primary_is_zero_then_left_to_right() =>
        Assert.That(Layout.Select(m => m.DeviceName), Is.EqualTo(new[] { "A", "C", "B" }));

    [Test]
    public void Regions_must_be_fully_on_monitors()
    {
        Assert.That(TargetResolver.ValidateRegion(new PixelRect(1800, 10, 300, 300), Layout), Is.EqualTo(new PixelRect(1800, 10, 300, 300)));
        Assert.Throws<CaptureException>(() => TargetResolver.ValidateRegion(new PixelRect(-100, 1000, 300, 300), Layout));
        Assert.Throws<CaptureException>(() => TargetResolver.ValidateRegion(new PixelRect(0, 0, 0, 10), Layout));
    }

    [Test]
    public void Missing_monitor_fails_loudly() =>
        Assert.That(Assert.Throws<CaptureException>(() => TargetResolver.ResolveMonitor(5, Layout))!.Error, Is.EqualTo(CaptureError.TargetNotFound));

    [Test]
    public void Video_rect_is_clipped_to_the_desktop_with_even_size() =>
        Assert.That(TargetResolver.ClipToDesktop(new PixelRect(3800, -50, 301, 200), Layout), Is.EqualTo(new PixelRect(3800, 0, 40, 150)));
}

public class AppWindowsTests
{
    private static WindowInfo Window(nint hwnd, int pid, int width = 800, int height = 600) =>
        new(hwnd, "Title", pid, "proc", new PixelRect(0, 0, width, height));

    [Test]
    public void Finds_the_nearest_ancestor_that_owns_a_window()
    {
        // 1 (self, the CLI) -> 2 (no window) -> 3 (owns the host window)
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 0 };
        var windows = new[] { Window(0x30, 3) };
        Assert.That(AppWindows.FindHostWindow(1, parents, windows), Is.EqualTo((nint)0x30));
    }

    [Test]
    public void Stops_at_the_first_ancestor_with_a_window_rather_than_a_further_one()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 4, [4] = 0 };
        var windows = new[] { Window(0x20, 2), Window(0x40, 4) };
        Assert.That(AppWindows.FindHostWindow(1, parents, windows), Is.EqualTo((nint)0x20));
    }

    [Test]
    public void An_ancestor_with_several_windows_yields_the_largest()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 0 };
        var windows = new[] { Window(0x1, 2, 100, 100), Window(0x2, 2, 1000, 1000) };
        Assert.That(AppWindows.FindHostWindow(1, parents, windows), Is.EqualTo((nint)0x2));
    }

    [Test]
    public void No_ancestor_owning_a_window_fails_loudly()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 0 };
        var ex = Assert.Throws<CaptureException>(() => AppWindows.FindHostWindow(1, parents, []));
        Assert.That(ex!.Error, Is.EqualTo(CaptureError.TargetNotFound));
    }

    [Test]
    public void A_cycle_in_the_process_tree_does_not_loop_forever() =>
        Assert.Throws<CaptureException>(() => AppWindows.FindHostWindow(1, new Dictionary<int, int> { [1] = 2, [2] = 1 }, []));

    [Test]
    public void Finds_the_window_of_the_process_running_APP_EXE()
    {
        var windows = new[] { Window(0x1, 11), Window(0x2, 22) };
        Func<int, string?> modules = pid => pid switch { 11 => @"C:\Apps\Other\Other.exe", 22 => @"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.Desktop.exe", _ => null };
        Assert.That(AppWindows.FindWindowForExePath(@"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.Desktop.exe", windows, modules), Is.EqualTo((nint)0x2));
    }

    [Test]
    public void No_window_owned_by_APP_EXE_yields_null()
    {
        var windows = new[] { Window(0x1, 11) };
        Assert.That(AppWindows.FindWindowForExePath(@"C:\Apps\MindAttic.Orchestrator\MindAttic.Orchestrator.Desktop.exe", windows, _ => @"C:\Apps\Other\Other.exe"), Is.Null);
    }
}

public class ClipboardWriterTests
{
    [Test]
    public void Builds_a_bottom_up_32bpp_header_and_flips_rows()
    {
        // 2x2 BGRA, top-down: row0 then row1.
        var row0 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var row1 = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };
        var image = new CapturedImage(2, 2, row0.Concat(row1).ToArray());

        var dib = ClipboardWriter.BuildDibBytes(image);

        Assert.That(dib.Length, Is.EqualTo(40 + 16));
        Assert.That(BitConverter.ToInt32(dib, 4), Is.EqualTo(2));   // biWidth
        Assert.That(BitConverter.ToInt32(dib, 8), Is.EqualTo(2));   // biHeight: positive => CF_DIB rows are bottom-up
        Assert.That(BitConverter.ToInt16(dib, 14), Is.EqualTo(32)); // biBitCount
        Assert.That(BitConverter.ToUInt32(dib, 16), Is.EqualTo(0u)); // biCompression = BI_RGB
        // Bottom-up: the DIB's first stored row is the image's last (bottom) row.
        Assert.That(dib.Skip(40).Take(8), Is.EqualTo(row1));
        Assert.That(dib.Skip(48).Take(8), Is.EqualTo(row0));
    }
}

public class HotkeyAndArgsTests
{
    [TestCase("F9", 0u, 0x78u)]
    [TestCase("Ctrl+Shift+S", 6u, (uint)'S')]
    [TestCase("alt+PrintScreen", 1u, 0x2Cu)]
    [TestCase("Win+F12", 8u, 0x7Bu)]
    public void Parses_hotkeys(string text, uint mods, uint vk)
    {
        Assert.That(GlobalHotkeys.TryParse(text, out var m, out var v), Is.True);
        Assert.That((m, v), Is.EqualTo((mods, vk)));
    }

    [TestCase("Hyper+F1")]
    [TestCase("F25")]
    [TestCase("")]
    public void Rejects_bad_hotkeys(string text) => Assert.That(GlobalHotkeys.TryParse(text, out _, out _), Is.False);

    [Test]
    public void Agent_args_parse_targets_and_passthrough()
    {
        var a = AgentArgs.Parse(["run", "--hq", "--app", "Game", "--", "Game.exe", "-demo", "-capture"]);
        Assert.Multiple(() =>
        {
            Assert.That(a.Command, Is.EqualTo("run"));
            Assert.That(a.Has("hq"), Is.True);
            Assert.That(a.Get("app"), Is.EqualTo("Game"));
            Assert.That(a.Passthrough, Is.EqualTo(new[] { "Game.exe", "-demo", "-capture" }));
        });
        Assert.That(AgentArgs.Parse(["shot", "--region", "10,20,300,200"]).Target(), Is.EqualTo(CaptureTarget.Region(10, 20, 300, 200)));
        Assert.That(AgentArgs.Parse(["shot", "--hwnd", "0x1A"]).Target(), Is.EqualTo(CaptureTarget.Window(26)));
        Assert.Throws<CaptureException>(() => AgentArgs.Parse(["shot", "--region", "1,2,3"]).Target());
    }

    [TestCase("http://localhost:5173", true)]
    [TestCase("https://127.0.0.1", true)]
    [TestCase("https://evil.example", false)]
    [TestCase("http://localhost.evil.example", false)]
    [TestCase("null", false)]
    public void Only_local_origins_are_allowed_by_default(string origin, bool allowed) =>
        Assert.That(ApiServer.IsAllowedOrigin(origin, []), Is.EqualTo(allowed));

    [Test]
    public void Extra_origins_can_be_allowed() =>
        Assert.That(ApiServer.IsAllowedOrigin("null", ["null"]), Is.True);

    [Test]
    public void Maps_wasapi_formats()
    {
        Assert.That(WasapiTrack.MapFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)), Is.EqualTo(new AudioFormat(48000, 2, "f32le")));
        Assert.That(WasapiTrack.MapFormat(new WaveFormat(44100, 16, 1)), Is.EqualTo(new AudioFormat(44100, 1, "s16le")));
        Assert.That(WasapiTrack.MapFormat(new WaveFormatExtensible(48000, 32, 8)).FfmpegFormat, Is.EqualTo("f32le"));
    }
}

/// <summary>Real-desktop tests; skipped without an interactive session or ffmpeg.</summary>
[NonParallelizable]
public class DesktopIntegrationTests
{
    private string _root = "";

    [OneTimeSetUp]
    public void Require()
    {
        if (!Environment.UserInteractive || Desktop.GetMonitors().Count == 0) Assert.Ignore("Needs an interactive desktop.");
        if (FfmpegLocator.TryFind("ffmpeg") is null) Assert.Ignore("Needs ffmpeg.");
    }

    [SetUp]
    public void Setup() => _root = Directory.CreateTempSubdirectory("capture-win-").FullName;

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private CaptureOptions Options(Action<CaptureOptions>? c = null)
    {
        var o = new CaptureOptions { OutputRoot = _root, AppName = "WinTest", Fps = 30 };
        c?.Invoke(o);
        return o;
    }

    private static string Probe(string file, string entries)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.Find("ffprobe"), $"-v error -count_packets -show_entries {entries} -of default=nw=1:nk=1 \"{file}\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim().Replace("\r", "");
        p.WaitForExit();
        return text;
    }

    [Test]
    public async Task Region_screenshot_has_exact_size()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(CaptureTarget.Region(10, 20, 333, 222)), Options());
        var r = await engine.ScreenshotAsync("region");
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(Probe(r.PrimaryFile!, "stream=width,height"), Is.EqualTo("333\n222"));
    }

    [Test]
    public async Task Region_recording_is_lossless_with_expected_size_and_length()
    {
        await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(CaptureTarget.Region(0, 0, 640, 360)), Options());
        var r = await engine.ClipAsync(TimeSpan.FromSeconds(2), "clip");
        Assert.That(r.Succeeded, Is.True, r.Error);
        Assert.That(Probe(r.Files[0], "stream=codec_name,width,height"), Is.EqualTo("ffv1\n640\n360"));
        Assert.That(double.Parse(Probe(r.Files[0], "format=duration"), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(2.0).Within(0.15));
        Assert.That(Probe(r.Files[1], "stream=codec_name"), Is.EqualTo("h264"));
    }

    [Test]
    public async Task Window_session_via_wgc_exports_on_close()
    {
        if (!WgcWindowSource.IsSupported) Assert.Ignore("Windows Graphics Capture unavailable.");
        using var app = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = true })!;
        try
        {
            var hwnd = await WaitForWindowAsync("Character Map");
            string folder, session;
            await using (var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(CaptureTarget.Window(hwnd)), Options(o => o.SessionRecording = true)))
            {
                folder = engine.SessionFolder;
                session = engine.SessionId;
                Assert.That(engine.Diagnostics.VideoSource, Does.StartWith("wgc"));
                var still = await engine.ScreenshotAsync("window");
                Assert.That(still.Succeeded, Is.True, still.Error);
                await Task.Delay(2500);
            }
            var mkv = Path.Combine(folder, session + "_WinTest_Video.mkv");
            Assert.That(File.Exists(mkv), Is.True);
            Assert.That(double.Parse(Probe(mkv, "format=duration"), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(2.5).Within(1.0));
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("charmap")) { p.Kill(); p.Dispose(); }
        }
    }

    private static async Task<nint> WaitForWindowAsync(string title)
    {
        for (var i = 0; i < 40; i++)
        {
            if (Desktop.GetWindows().FirstOrDefault(w => w.Title.Contains(title)) is { } w) return w.Hwnd;
            await Task.Delay(250);
        }
        Assert.Ignore($"Could not open a \"{title}\" window.");
        return 0;
    }
}
