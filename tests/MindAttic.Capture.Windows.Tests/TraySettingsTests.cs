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
    public void A_broken_settings_file_is_set_aside_and_defaults_are_used()
    {
        var path = Path.Combine(_dir, "tray.json");
        File.WriteAllText(path, "{ not json");
        var settings = TraySettings.Load(path, Primary);
        Assert.That(settings.Regions.Single().Name, Is.EqualTo("YouTube Short"));
        Assert.That(File.ReadAllText(path + ".bad"), Is.EqualTo("{ not json"));
    }
}
