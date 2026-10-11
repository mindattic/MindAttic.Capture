using System.Globalization;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Agent;

/// <summary>Parsed agent command line: a command, positional values, --options, and anything after "--".</summary>
internal sealed class AgentArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private set; } = "help";
    public List<string> Positional { get; } = [];
    public List<string> Passthrough { get; } = [];

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "hq", "lossless", "gdi", "no-wgc", "separate-audio", "verbose", "no-hotkeys", "help",
    };

    public static AgentArgs Parse(IReadOnlyList<string> args)
    {
        var result = new AgentArgs();
        var i = 0;
        if (args.Count > 0 && !args[0].StartsWith('-')) result.Command = args[i++].ToLowerInvariant();
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                result.Passthrough.AddRange(args.Skip(i + 1));
                break;
            }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (!Flags.Contains(name) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                result._options[name] = value;
            }
            else
            {
                result.Positional.Add(a);
            }
        }
        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string Require(string name) => Get(name) is { Length: > 0 } v ? v
        : throw new CaptureException(CaptureError.InvalidArgument, $"--{name} needs a value.");

    public int? GetInt(string name) => Get(name) is { } v
        ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
          : throw new CaptureException(CaptureError.InvalidArgument, $"--{name} must be a whole number, got \"{v}\".")
        : null;

    public TimeSpan? GetDuration(string name) => Get(name) is { } v ? DurationText.Parse(v) : null;

    /// <summary>The capture target from --monitor / --region / --window / --hwnd / --process (default: primary monitor).</summary>
    public CaptureTarget Target()
    {
        if (Get("region") is { } region)
        {
            var parts = region.Split(',', 'x', ' ').Where(p => p.Length > 0)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                    : throw new CaptureException(CaptureError.InvalidArgument, $"--region must be x,y,w,h; got \"{region}\"."))
                .ToArray();
            if (parts.Length != 4) throw new CaptureException(CaptureError.InvalidArgument, $"--region must be x,y,w,h; got \"{region}\".");
            return CaptureTarget.Region(parts[0], parts[1], parts[2], parts[3]);
        }
        if (Get("window") is { } title) return CaptureTarget.WindowTitle(title);
        if (Get("hwnd") is { } hwnd)
        {
            var text = hwnd.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hwnd[2..] : hwnd;
            var style = hwnd.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer;
            return long.TryParse(text, style, CultureInfo.InvariantCulture, out var h) ? CaptureTarget.Window((nint)h)
                : throw new CaptureException(CaptureError.InvalidArgument, $"--hwnd must be a window handle; got \"{hwnd}\".");
        }
        if (Get("process") is { } process) return CaptureTarget.Process(process);
        return CaptureTarget.Monitor(GetInt("monitor") ?? 0);
    }

    public DesktopHostOptions HostOptions() => new()
    {
        ForceGdiGrab = Has("gdi"),
        PreferWgcForWindows = !Has("no-wgc"),
    };

    public ImageFormat ImageFormat() => (Get("format") ?? "png").ToLowerInvariant() switch
    {
        "png" => global::MindAttic.Capture.ImageFormat.Png,
        "jpg" or "jpeg" => global::MindAttic.Capture.ImageFormat.Jpg,
        "webp" => global::MindAttic.Capture.ImageFormat.WebP,
        "bmp" => global::MindAttic.Capture.ImageFormat.Bmp,
        "raw" or "rawbgra" => global::MindAttic.Capture.ImageFormat.RawBgra,
        var f => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown --format \"{f}\" (png, jpg, webp, bmp, raw)."),
    };

    public CapturePreset Preset() => (Get("preset") ?? "trailer").ToLowerInvariant() switch
    {
        "trailer" => CapturePreset.Trailer,
        "lossless" => CapturePreset.Lossless,
        "devlog" => CapturePreset.Devlog,
        "bugreport" or "bug" => CapturePreset.BugReport,
        "session" => CapturePreset.Session,
        var p => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown --preset \"{p}\" (trailer, lossless, devlog, bugreport)."),
    };

    public AudioMode Audio() => (Get("audio") ?? (Has("audio") ? "app" : "none")).ToLowerInvariant() switch
    {
        "none" or "off" => AudioMode.None,
        "app" or "system" or "" => AudioMode.App,
        "mic" or "app+mic" or "both" => AudioMode.AppAndMic,
        var a => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown --audio \"{a}\" (app, mic, none)."),
    };

    public OutputFormats? Outputs()
    {
        if (Get("outputs") is not { } text) return null;
        var result = OutputFormats.None;
        foreach (var part in text.Split(',', '+'))
        {
            result |= part.Trim().ToLowerInvariant() switch
            {
                "mkv" => OutputFormats.Mkv,
                "mp4" => OutputFormats.Mp4,
                "webm" => OutputFormats.WebM,
                "gif" => OutputFormats.Gif,
                "webp" => OutputFormats.WebP,
                var o => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown output \"{o}\" (mkv, mp4, webm, gif, webp)."),
            };
        }
        return result;
    }

    /// <summary>Shared options: --out, --app, --config, --fps, --hq/--lossless, --audio, --ffmpeg.</summary>
    public void ApplyCommon(CaptureOptions o)
    {
        if (Get("config") is { } config) o.ApplyConfigFile(config);
        if (Get("out") is { } outRoot) o.OutputRoot = outRoot;
        if (Get("app") is { } app) o.AppName = app;
        if (GetInt("fps") is { } fps) o.Fps = fps;
        if (Get("ffmpeg") is { } ffmpeg) o.FfmpegPath = ffmpeg;
        if (Has("hq")) o.TapeQuality = TapeQuality.HighQuality;
        if (Has("lossless")) o.TapeQuality = TapeQuality.Lossless;
        if (Has("audio")) o.Audio = Audio();
    }
}
