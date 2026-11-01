using System.IO;
using System.Text.Json.Nodes;
using MindAttic.Capture.Tray;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class TraySettingsTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1440);
    private string _dir = "";
    private string Legacy => Path.Combine(_dir, "tray.json");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "capture-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private JsonSettingsStore<TraySettings> Store() => TraySettings.CreateStore(_dir);
    private TraySettings Load(bool startup = false) => TraySettings.Load(Primary, Store(), Legacy, () => startup);

    /// <summary>Writes today's pre-Vault tray.json (PascalCase, enum names) the way the old tray did.</summary>
    private void WriteLegacy(string json) => File.WriteAllText(Legacy, json);

    private static TraySettings RoundTrip(JsonSettingsStore<TraySettings> store, Action<TraySettings> change)
    {
        store.Update(change);
        return store.Load();
    }

    [Test]
    public void Setting_RecordHotkey_ImportsRoundTripsAndIsRegistered()
    {
        WriteLegacy("""{ "RecordHotkey": "Ctrl+F9" }""");
        Assert.That(Load().RecordHotkey, Is.EqualTo("Ctrl+F9"), "today's tray.json value imported");
        var s = RoundTrip(Store(), x => x.RecordHotkey = "Ctrl+Alt+F10");
        Assert.That(s.Hotkeys(), Does.Contain((TrayHotkey.Record, "Ctrl+Alt+F10")), "the tray registers it");
        Assert.That(GlobalHotkeys.TryParse(s.RecordHotkey, out _, out _), Is.True);
    }

    [Test]
    public void Setting_ScreenshotHotkey_ImportsRoundTripsAndIsRegistered()
    {
        WriteLegacy("""{ "ScreenshotHotkey": "Alt+PrintScreen" }""");
        Assert.That(Load().ScreenshotHotkey, Is.EqualTo("Alt+PrintScreen"));
        var s = RoundTrip(Store(), x => x.ScreenshotHotkey = "Shift+F12");
        Assert.That(s.Hotkeys(), Does.Contain((TrayHotkey.Screenshot, "Shift+F12")));
    }

    [Test]
    public void Setting_OutputRoot_ImportsRoundTripsAndIsWhereRecordingsGo()
    {
        WriteLegacy("""{ "OutputRoot": "D:\\Captures" }""");
        Assert.That(Load().OutputRoot, Is.EqualTo(@"D:\Captures"));
        var s = RoundTrip(Store(), x => x.OutputRoot = @"E:\Clips");
        var options = new CaptureOptions { OutputRoot = @"R:\from-capture-json" };
        s.ApplyTo(options);
        Assert.That(options.OutputRoot, Is.EqualTo(@"E:\Clips"));
        var unset = new CaptureOptions { OutputRoot = @"R:\from-capture-json" };
        new TraySettings().ApplyTo(unset);
        Assert.That(unset.OutputRoot, Is.EqualTo(@"R:\from-capture-json"), "empty keeps capture.json's folder");
    }

    [Test]
    public void Setting_DefaultVideo_ImportsRoundTripsAndIsANewAreasFormat()
    {
        WriteLegacy("""{ "DefaultVideo": "Gif" }""");
        Assert.That(Load().DefaultVideo, Is.EqualTo(VideoFormat.Gif));
        var s = RoundTrip(Store(), x => x.DefaultVideo = VideoFormat.Mp4);
        Assert.That(s.NewArea().Video, Is.EqualTo(VideoFormat.Mp4));
        Assert.That(File.ReadAllText(Store().FilePath), Does.Contain("\"Mp4\""), "stored by name, matching the manifest's choices");
    }

    [Test]
    public void Setting_DefaultImage_ImportsRoundTripsAndIsANewAreasFormat()
    {
        WriteLegacy("""{ "DefaultImage": "Jpg" }""");
        Assert.That(Load().DefaultImage, Is.EqualTo(ImageFormat.Jpg));
        var s = RoundTrip(Store(), x => x.DefaultImage = ImageFormat.Png);
        Assert.That(s.NewArea().Image, Is.EqualTo(ImageFormat.Png));
    }

    [Test]
    public void Setting_StartWithWindows_ImportsTodaysRegistryStateAndRoundTrips()
    {
        Assert.That(Load(startup: true).StartWithWindows, Is.True, "the registry's current state is imported");
        var s = RoundTrip(Store(), x => x.StartWithWindows = false);
        Assert.That(s.StartWithWindows, Is.False, "the tray applies this to the registry at start and on save");
    }

    [Test]
    public void Setting_Regions_ImportRoundTripWithEveryFieldAndBecomeChoices()
    {
        WriteLegacy("""{ "Regions": [ { "Name": "Clip", "X": -100, "Y": 20, "Width": 640, "Height": 360, "Ratio": "16:9", "Video": "Gif", "Image": "Jpg" } ] }""");
        var region = Load().Regions.Single();
        Assert.That((region.Name, region.X, region.Y, region.Width, region.Height, region.Ratio, region.Video, region.Image),
            Is.EqualTo(("Clip", -100, 20, 640, 360, "16:9", VideoFormat.Gif, ImageFormat.Jpg)));
        var s = RoundTrip(Store(), x => x.Upsert(SavedRegion.From("Short", new AreaChoice(new PixelRect(1, 2, 300, 400), AspectRatio.Find("9:16"), VideoFormat.Mp4, ImageFormat.Png))));
        var choice = s.Regions.Single(r => r.Name == "Short").ToChoice();
        Assert.That(choice.Region, Is.EqualTo(new PixelRect(1, 2, 300, 400)));
        Assert.That((choice.Ratio.Name, choice.Video, choice.Image), Is.EqualTo(("9:16", VideoFormat.Mp4, ImageFormat.Png)));
    }

    [Test]
    public void EverySettingHasItsOwnTest()
    {
        var tested = GetType().GetMethods().Select(m => m.Name).Where(n => n.StartsWith("Setting_")).ToList();
        var top = SettingsManifestBuilder.Build(typeof(TraySettings)).Fields;
        foreach (var field in top)
            Assert.That(tested.Any(t => t.StartsWith("Setting_" + char.ToUpperInvariant(field.Property[0]) + field.Property[1..] + "_")), Is.True, field.Property);
        // Each saved region's own fields are asserted one by one in Setting_Regions_...
        Assert.That(top.Single(f => f.Property == "regions").Fields!.Select(f => f.Property),
            Is.EqualTo(new[] { "name", "x", "y", "width", "height", "ratio", "video", "image" }));
    }

    [Test]
    public void First_run_without_tray_json_writes_a_YouTube_Short_region_centred_on_the_primary_monitor()
    {
        var settings = Load();
        Assert.That(File.Exists(Store().FilePath), Is.True);
        var shortFrame = settings.Regions.Single();
        Assert.That(shortFrame.Name, Is.EqualTo("YouTube Short"));
        Assert.That(shortFrame.Ratio, Is.EqualTo("9:16"));
        Assert.That((double)shortFrame.Width / shortFrame.Height, Is.EqualTo(9.0 / 16).Within(0.01));
        Assert.That(Primary.Contains(shortFrame.Rect), Is.True);
        Assert.That(shortFrame.Width % 2 + shortFrame.Height % 2, Is.Zero);
    }

    [Test]
    public void The_manifest_is_per_machine_and_its_enum_choices_are_the_stored_names()
    {
        Load();
        var m = SettingsManifestFile.TryRead(Path.Combine(_dir, SettingsManifest.FileName))!;
        Assert.That((m.AppId, m.Scope), Is.EqualTo(("Capture", SettingsManifest.LocalScope)));
        var video = m.Find("MindAttic:Settings:Capture:defaultVideo")!;
        Assert.That(video.Choices!.Select(c => c.Value!.GetValue<string>()), Does.Contain("Mp4"));
        Assert.That(m.Find("MindAttic:Settings:Capture:regions")!.Kind, Is.EqualTo(SettingKind.Collection));
        Assert.That(File.ReadAllText(Store().FilePath), Does.Contain("\"Ctrl+Alt+Shift+R\""), "hotkeys are not escaped");
        Assert.That(JsonNode.Parse(File.ReadAllText(Store().FilePath))!["regions"]![0]!["rect"], Is.Null, "computed Rect isn't stored");
    }

    [Test]
    public void An_existing_Vault_file_is_never_overwritten_by_tray_json()
    {
        Store().Save(new TraySettings { RecordHotkey = "F9" });
        WriteLegacy("""{ "RecordHotkey": "F10" }""");
        Assert.That(Load().RecordHotkey, Is.EqualTo("F9"));
        Assert.That(File.Exists(Legacy), Is.True, "tray.json is left in place as the rollback");
    }

    [Test]
    public void A_broken_legacy_file_falls_back_to_first_run_defaults()
    {
        WriteLegacy("{ not json");
        Assert.That(Load().Regions.Single().Name, Is.EqualTo("YouTube Short"));
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
    public void The_old_settings_window_is_gone()
    {
        var asm = typeof(TraySettings).Assembly;
        Assert.That(asm.GetType("MindAttic.Capture.Tray.SettingsForm"), Is.Null);
        Assert.That(asm.GetType("MindAttic.Capture.Tray.TrayPreferences"), Is.Null);
        Assert.That(asm.GetType("MindAttic.Capture.Tray.TrayApp")!.GetMethod("OpenSettings",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Not.Null);
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
    public void RunPs1_WritesTheSettingsManifestAfterPublishing()
    {
        // LAMP step 7 (M.CAPT-T88): a redeploy refreshes the manifest MindAttic Vault renders, not only the app's next start.
        var root = TestContext.CurrentContext.TestDirectory;
        while (!System.IO.Directory.Exists(System.IO.Path.Combine(root, ".git")) && !System.IO.File.Exists(System.IO.Path.Combine(root, ".git")))
            root = System.IO.Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repo root above the tests");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "run.ps1")), Does.Contain("-ArgumentList '--write-settings-manifest'"));
    }
}
