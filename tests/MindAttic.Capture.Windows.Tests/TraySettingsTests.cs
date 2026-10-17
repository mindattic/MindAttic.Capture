using System.IO;
using MindAttic.Capture.Tray;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class TraySettingsTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1440);
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "capture-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [Test]
    public void First_run_writes_a_YouTube_Short_region_centred_on_the_primary_monitor()
    {
        var path = Path.Combine(_dir, "tray.json");
        var settings = TraySettings.Load(path, Primary);
        Assert.That(File.Exists(path), Is.True);
        var shortFrame = settings.Regions.Single();
        Assert.That(shortFrame.Name, Is.EqualTo("YouTube Short"));
        Assert.That(shortFrame.Ratio, Is.EqualTo("9:16"));
        Assert.That((double)shortFrame.Width / shortFrame.Height, Is.EqualTo(9.0 / 16).Within(0.01));
        Assert.That(Primary.Contains(shortFrame.Rect), Is.True);
        Assert.That(shortFrame.Width % 2 + shortFrame.Height % 2, Is.Zero);
    }

    [Test]
    public void Regions_round_trip_with_their_shape_and_formats()
    {
        var path = Path.Combine(_dir, "tray.json");
        var settings = new TraySettings();
        settings.Upsert(SavedRegion.From("Clip", new AreaChoice(new PixelRect(-100, 20, 640, 360), AspectRatio.Find("16:9"), VideoFormat.Gif, ImageFormat.Jpg)));
        settings.Save(path);
        var loaded = TraySettings.Load(path, Primary).Regions.Single();
        var choice = loaded.ToChoice();
        Assert.That(choice.Region, Is.EqualTo(new PixelRect(-100, 20, 640, 360)));
        Assert.That(choice.Ratio.Name, Is.EqualTo("16:9"));
        Assert.That((choice.Video, choice.Image), Is.EqualTo((VideoFormat.Gif, ImageFormat.Jpg)));
        Assert.That(File.ReadAllText(path), Does.Contain("\"Gif\""), "enums are stored by name so the file stays readable");
    }

    [Test]
    public void Saving_under_an_existing_name_replaces_it_and_delete_ignores_case()
    {
        var settings = new TraySettings();
        settings.Upsert(SavedRegion.From("Short", new AreaChoice(new PixelRect(0, 0, 10, 10), AspectRatio.Free, VideoFormat.Mp4, ImageFormat.Png)));
        settings.Upsert(SavedRegion.From("short", new AreaChoice(new PixelRect(5, 5, 20, 20), AspectRatio.Free, VideoFormat.Mp4, ImageFormat.Png)));
        Assert.That(settings.Regions.Single().Rect, Is.EqualTo(new PixelRect(5, 5, 20, 20)));
        settings.Remove("SHORT");
        Assert.That(settings.Regions, Is.Empty);
    }

    [Test]
    public void Default_hotkeys_parse_and_use_three_modifiers_so_they_do_not_steal_app_shortcuts()
    {
        var settings = new TraySettings();
        foreach (var key in new[] { settings.RecordHotkey, settings.ScreenshotHotkey })
        {
            Assert.That(GlobalHotkeys.TryParse(key, out var mods, out _), Is.True, key);
            Assert.That(mods, Is.EqualTo(1u | 2u | 4u), key);
        }
        Assert.That(settings.RecordHotkey, Is.Not.EqualTo(settings.ScreenshotHotkey));
    }

    [Test]
    public void A_finished_recording_lists_the_lossless_master_and_its_copy()
    {
        var mkv = Path.Combine(_dir, "a_Recording_000.mkv");
        var mp4 = Path.Combine(_dir, "a_Recording_000.mp4");
        File.WriteAllBytes(mkv, new byte[3 << 20]);
        File.WriteAllBytes(mp4, new byte[2048]);
        var (title, text) = CaptureReport.Describe(
            new CaptureResult { Kind = CaptureKind.Recording, Files = [mkv, mp4], Duration = TimeSpan.FromSeconds(75) }, _dir);
        Assert.That(title, Is.EqualTo("Recording (01:15) saved"));
        Assert.That(text, Does.Contain("a_Recording_000.mkv  (3.0 MB)").And.Contain("a_Recording_000.mp4  (2 KB)"));
    }

    [Test]
    public void A_failed_capture_points_at_the_session_log()
    {
        var (title, text) = CaptureReport.Describe(new CaptureResult { Kind = CaptureKind.Recording, Error = "ffmpeg exited 1" }, @"R:\MindAttic\Capture\Desktop");
        Assert.That(title, Is.EqualTo("Capture failed"));
        Assert.That(text, Does.StartWith("ffmpeg exited 1").And.Contain(@"the .log in R:\MindAttic\Capture\Desktop"));
    }

    [TestCase(System.Windows.Forms.Keys.R | System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.Shift, "Ctrl+Alt+Shift+R")]
    [TestCase(System.Windows.Forms.Keys.F9, "F9")]
    [TestCase(System.Windows.Forms.Keys.D5 | System.Windows.Forms.Keys.Control, "Ctrl+5")]
    [TestCase(System.Windows.Forms.Keys.PrintScreen | System.Windows.Forms.Keys.Alt, "Alt+PrintScreen")]
    [TestCase(System.Windows.Forms.Keys.PageDown | System.Windows.Forms.Keys.Shift, "Shift+PageDown")]
    [TestCase(System.Windows.Forms.Keys.F24, "F24")]
    public void A_pressed_key_becomes_hotkey_text_the_parser_reads_back(System.Windows.Forms.Keys keys, string expected)
    {
        var text = HotkeyText.From(keys);
        Assert.That(text, Is.EqualTo(expected));
        Assert.That(GlobalHotkeys.TryParse(text!, out _, out var vk), Is.True);
        Assert.That(vk, Is.EqualTo((uint)(keys & System.Windows.Forms.Keys.KeyCode)));
    }

    [Test]
    public void Modifiers_alone_and_unsupported_keys_are_not_hotkeys()
    {
        Assert.That(HotkeyText.From(System.Windows.Forms.Keys.ControlKey | System.Windows.Forms.Keys.Control), Is.Null);
        Assert.That(HotkeyText.From(System.Windows.Forms.Keys.ShiftKey | System.Windows.Forms.Keys.Shift), Is.Null);
        Assert.That(HotkeyText.From(System.Windows.Forms.Keys.OemPeriod), Is.Null);
    }

    [Test]
    public void Output_folder_and_default_formats_round_trip()
    {
        var path = Path.Combine(_dir, "tray.json");
        var settings = new TraySettings { OutputRoot = @"D:\Captures", DefaultVideo = VideoFormat.Mp4, DefaultImage = ImageFormat.Jpg, RecordHotkey = "F9" };
        settings.Save(path);
        var loaded = TraySettings.Load(path, Primary);
        Assert.That((loaded.OutputRoot, loaded.DefaultVideo, loaded.DefaultImage, loaded.RecordHotkey),
            Is.EqualTo((@"D:\Captures", VideoFormat.Mp4, ImageFormat.Jpg, "F9")));
    }

    [Test]
    public void A_broken_settings_file_is_set_aside_and_defaults_are_used()
    {
        var path = Path.Combine(_dir, "tray.json");
        File.WriteAllText(path, "{ not json");
        var settings = TraySettings.Load(path, Primary);
        Assert.That(settings.Regions.Single().Name, Is.EqualTo("YouTube Short"));
        Assert.That(File.ReadAllText(path + ".bad"), Is.EqualTo("{ not json"));
    }
}
