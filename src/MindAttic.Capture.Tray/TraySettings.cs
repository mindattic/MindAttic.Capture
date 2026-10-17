using System.Text.Json;
using System.Text.Json.Serialization;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>A named area to record again without redrawing it, e.g. "YouTube Short".</summary>
internal sealed class SavedRegion
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Ratio { get; set; } = AspectRatio.Free.Name;
    public VideoFormat Video { get; set; } = VideoFormat.LosslessAndMp4;
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

/// <summary>The tray recorder's own settings (tray.json beside the installed agent's data, not the developer capture.json).</summary>
internal sealed class TraySettings
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Capture", "tray.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Starts recording the armed target, or stops the recording in progress.</summary>
    public string RecordHotkey { get; set; } = "Ctrl+Alt+Shift+R";

    /// <summary>Takes a screenshot of the armed target.</summary>
    public string ScreenshotHotkey { get; set; } = "Ctrl+Alt+Shift+S";

    /// <summary>Where captures go; empty uses capture.json's outputRoot or the engine default (R:\MindAttic\Capture).</summary>
    public string? OutputRoot { get; set; }

    /// <summary>Files written for screen and app recordings and for a fresh area selection.</summary>
    public VideoFormat DefaultVideo { get; set; } = VideoFormat.LosslessAndMp4;

    public ImageFormat DefaultImage { get; set; } = ImageFormat.Png;

    public List<SavedRegion> Regions { get; set; } = [];

    /// <summary>Settings from <paramref name="path"/>; a missing file gets first-run defaults, a broken one is set aside as .bad.</summary>
    public static TraySettings Load(string path, PixelRect primaryMonitor)
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), Json) ?? Defaults(primaryMonitor);
            }
            catch (JsonException)
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }
        var defaults = Defaults(primaryMonitor);
        defaults.Save(path);
        return defaults;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
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
}
