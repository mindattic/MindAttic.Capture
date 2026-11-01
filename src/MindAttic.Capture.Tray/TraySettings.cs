using System.Text.Json;
using System.Text.Json.Serialization;
using MindAttic.Capture.Windows;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Capture.Tray;

/// <summary>A named area to record again without redrawing it, e.g. "YouTube Short".</summary>
internal sealed class SavedRegion
{
    [Setting("Name", Order = 1, Help = "Shown in Saved Regions.")]
    public string Name { get; set; } = "";

    [Setting("Left (px)", Order = 2, Help = "Physical pixels from the left of the virtual desktop.")]
    public int X { get; set; }

    [Setting("Top (px)", Order = 3)]
    public int Y { get; set; }

    [Setting("Width (px)", Order = 4)]
    [SettingRange(2, 16384)]
    public int Width { get; set; } = 1280;

    [Setting("Height (px)", Order = 5)]
    [SettingRange(2, 16384)]
    public int Height { get; set; } = 720;

    [Setting("Aspect ratio", Order = 6, Help = "Ratio the area keeps when resized, e.g. 9:16, 16:9 or Free.")]
    public string Ratio { get; set; } = AspectRatio.Free.Name;

    [Setting("Video files", Order = 7)]
    public VideoFormat Video { get; set; } = VideoFormat.LosslessAndMp4;

    [Setting("Screenshot format", Order = 8)]
    public ImageFormat Image { get; set; } = ImageFormat.Png;

    [JsonIgnore]
    public PixelRect Rect => new(X, Y, Width, Height);

    public AreaChoice ToChoice() => new(Rect, AspectRatio.Find(Ratio), Video, Image);

    public static SavedRegion From(string name, AreaChoice c) => new()
    {
        Name = name, X = c.Region.X, Y = c.Region.Y, Width = c.Region.Width, Height = c.Region.Height,
        Ratio = c.Ratio.Name, Video = c.Video, Image = c.Image,
    };
}

/// <summary>
/// The tray recorder's settings: a per-machine MindAttic Vault settings app in
/// <c>%LOCALAPPDATA%\MindAttic\Capture\settings.json</c>, edited in MindAttic.Vault.Desktop (tray Settings...).
/// The tray applies saved edits live. Imported once from the older <c>tray.json</c> beside it.
/// </summary>
[SettingsApp(AppId, "MindAttic.Capture", Local = true, Icon = "video",
    Description = "Tray recorder: hotkeys, where captures go, default formats and saved regions.")]
internal sealed class TraySettings
{
    public const string AppId = "Capture";

    /// <summary>The pre-Vault settings file, imported once.</summary>
    public static string LegacyPath => Path.Combine(VaultPaths.LocalApp(AppId), "tray.json");

    // Enums as names keep the file readable and match the manifest's choice values.
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions LegacyJson = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Starts recording the armed target, or stops the recording in progress.</summary>
    [Setting("Start / stop recording", Group = "Hotkeys", Order = 1,
        Help = "Ctrl, Alt and/or Shift plus a key, e.g. Ctrl+Alt+Shift+R. Empty turns the hotkey off.")]
    public string RecordHotkey { get; set; } = "Ctrl+Alt+Shift+R";

    /// <summary>Takes a screenshot of the armed target.</summary>
    [Setting("Screenshot", Group = "Hotkeys", Order = 2, Help = "Takes a screenshot of the armed target, e.g. Ctrl+Alt+Shift+S.")]
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+Shift+S";

    /// <summary>Where captures go; empty uses capture.json's outputRoot or the engine default (R:\MindAttic\Capture).</summary>
    [Setting("Save captures to", Group = "Output", Order = 1,
        Help = "Folder for recordings and screenshots. Empty uses capture.json's outputRoot, or R:\\MindAttic\\Capture.")]
    [PathPicker(PathKind.Folder)]
    public string? OutputRoot { get; set; }

    /// <summary>Files written for screen and app recordings and for a fresh area selection.</summary>
    [Setting("Video files", Group = "Output", Order = 2, Help = "What screen, app and new-area recordings write.")]
    public VideoFormat DefaultVideo { get; set; } = VideoFormat.LosslessAndMp4;

    [Setting("Screenshot format", Group = "Output", Order = 3)]
    public ImageFormat DefaultImage { get; set; } = ImageFormat.Png;

    [Setting("Start with Windows", Group = "Startup", Order = 1, Help = "Start the tray recorder when you sign in.")]
    public bool StartWithWindows { get; set; }

    [Setting("Saved regions", Group = "Regions", Order = 1, Help = "Areas to record again without redrawing them.")]
    [SettingCollection(ItemName = "region", ItemLabel = nameof(SavedRegion.Name))]
    public List<SavedRegion> Regions { get; set; } = [];

    /// <summary>The Vault store (camelCase, enum names) in the app's per-machine folder.</summary>
    public static JsonSettingsStore<TraySettings> CreateStore(string? directory = null) =>
        new(directory ?? VaultPaths.LocalApp(AppId), JsonSettingsStore<TraySettings>.DefaultFileName, StoreJson);

    /// <summary>
    /// The settings. First run: today's <c>tray.json</c> (and the registry's start-with-Windows) are imported;
    /// with neither, a "YouTube Short" frame on <paramref name="primaryMonitor"/>. Writes the manifest Vault renders.
    /// </summary>
    public static TraySettings Load(PixelRect primaryMonitor, JsonSettingsStore<TraySettings>? store = null,
        string? legacyPath = null, Func<bool>? startWithWindows = null)
    {
        store ??= CreateStore();
        if (!store.Exists())
        {
            var seeded = ReadLegacy(legacyPath ?? LegacyPath) ?? Defaults(primaryMonitor);
            seeded.StartWithWindows = (startWithWindows ?? StartupRegistration.IsEnabled)();
            store.Save(seeded);
        }
        return store.LoadOrSeed();
    }

    private static TraySettings? ReadLegacy(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), LegacyJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>First run: a "YouTube Short" 9:16 frame, 80% of the primary monitor's height, centred on it.</summary>
    public static TraySettings Defaults(PixelRect primary)
    {
        var height = (int)(primary.Height * 0.8) & ~1;
        var width = (int)(height * 9.0 / 16) & ~1;
        var shortFrame = new PixelRect(primary.X + (primary.Width - width) / 2, primary.Y + (primary.Height - height) / 2, width, height);
        return new TraySettings
        {
            Regions = [SavedRegion.From("YouTube Short", new AreaChoice(shortFrame, AspectRatio.Find("9:16"), VideoFormat.LosslessAndMp4, ImageFormat.Png))],
        };
    }

    /// <summary>Adds <paramref name="region"/>, replacing one with the same name (ignoring case).</summary>
    public void Upsert(SavedRegion region)
    {
        var i = Regions.FindIndex(r => string.Equals(r.Name, region.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Regions[i] = region;
        else Regions.Add(region);
    }

    public void Remove(string name) => Regions.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The output folder a recording uses: this setting when set, else whatever capture.json chose.</summary>
    public void ApplyTo(CaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(OutputRoot)) options.OutputRoot = OutputRoot;
    }

    /// <summary>A fresh area selection's formats.</summary>
    public AreaChoice NewArea() => new(default, AspectRatio.Free, DefaultVideo, DefaultImage);

    /// <summary>The hotkeys the tray registers, by action.</summary>
    public IReadOnlyList<(TrayHotkey Action, string Keys)> Hotkeys() =>
        [(TrayHotkey.Record, RecordHotkey), (TrayHotkey.Screenshot, ScreenshotHotkey)];
}
