using System.Reflection;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Triggers;

namespace MindAttic.Capture;

/// <summary>Everything that configures a capture engine. Defaults suit MindAttic development machines.</summary>
public sealed class CaptureOptions
{
    /// <summary>Global kill switch. When false every API call logs a warning and does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where captures go. A missing drive falls back to <see cref="FallbackRoot"/> with a warning.</summary>
    public string OutputRoot { get; set; } = @"R:\MindAttic\Capture";

    /// <summary>Used when <see cref="OutputRoot"/>'s drive is unavailable.</summary>
    public string FallbackRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MindAttic", "Capture");

    /// <summary>Per-app folder under the root: the git repo the app runs from, else the entry assembly name.</summary>
    public string AppName { get; set; } = CaptureNaming.RepoName(AppContext.BaseDirectory) ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "App";

    /// <summary>Build version written to sidecars and the session manifest.</summary>
    public string? AppVersion { get; set; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

    /// <summary>File name template (default "{timestamp}", e.g. 2026-10-11T14.29.20.123). Tokens: {timestamp} {date} {time} {session} {tag} {index} {kind} {app}.</summary>
    public string FileTemplate { get; set; } = "{timestamp}";

    /// <summary>ffmpeg executable: full path, or a name found next to the app or on PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Tape frame rate.</summary>
    public int Fps { get; set; } = 60;

    /// <summary>How the tape is encoded.</summary>
    public TapeQuality TapeQuality { get; set; } = TapeQuality.Lossless;

    /// <summary>Record the whole run (the <c>-capture</c> flag) and export it when the app closes.</summary>
    public bool SessionRecording { get; set; }

    /// <summary>Outputs for the session export.</summary>
    public OutputFormats SessionOutputs { get; set; } = OutputFormats.Mkv | OutputFormats.Mp4;

    /// <summary>Keep a rolling tape so <see cref="ICapture.SaveReplayAsync"/> can save the past.</summary>
    public bool ReplayArmed { get; set; }

    /// <summary>How much past footage the replay buffer keeps.</summary>
    public TimeSpan ReplayLength { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Take a screenshot this often for as long as the engine runs (<c>-capture screen 5</c>).</summary>
    public TimeSpan? ScreenshotInterval { get; set; }

    /// <summary>Audio recorded with the tape.</summary>
    public AudioMode Audio { get; set; } = AudioMode.None;

    /// <summary>Warn (log plus <see cref="ICapture.DiskWarning"/>) when the app's capture folder exceeds this size. Nothing is deleted.</summary>
    public double? DiskWarnGB { get; set; }

    /// <summary>Declarative trigger rules (also loadable from capture.json).</summary>
    public List<TriggerRuleConfig> Rules { get; set; } = [];

    /// <summary>Key name to command ("screenshot", "toggle-recording", "save-replay", "marker").</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commands run after captures; only ever taken from developer config.</summary>
    public List<PostCaptureConfig> PostCapture { get; set; } = [];

    /// <summary>
    /// Path to the capture agent. When set, the session export runs in a detached
    /// <c>capture finalize</c> process so the app can exit immediately.
    /// </summary>
    public string? DetachedFinalizerPath { get; set; }

    /// <summary>Logging sink.</summary>
    public ICaptureLog Log { get; set; } = new TraceCaptureLog();

    /// <summary>JSON implementation (required on netstandard2.1).</summary>
    public ICaptureJson? Json { get; set; } = CaptureJsonDefaults.Create();

    /// <summary>Applies parsed command-line flags (and the environment).</summary>
    public CaptureOptions Apply(CaptureArgs args)
    {
        if (args.KillSwitch) Enabled = false;
        if (args.Video) SessionRecording = true;
        if (args.ScreenInterval is { } interval) ScreenshotInterval = interval;
        if (args.Quality is { } q) TapeQuality = q;
        if (args.ConfigPath is { } path) ApplyConfigFile(path);
        return this;
    }

    /// <summary>Loads capture.json-style settings from <paramref name="path"/>; missing keys keep their current values.</summary>
    public CaptureOptions ApplyConfigFile(string path)
    {
        if (!File.Exists(path))
            throw new CaptureException(CaptureError.InvalidArgument, $"Capture config \"{path}\" does not exist.");
        var json = Json ?? throw new CaptureException(CaptureError.InvalidArgument, "CaptureOptions.Json must be set to read config files on this platform.");
        CaptureConfigFile? file;
        try
        {
            file = json.Deserialize<CaptureConfigFile>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            throw new CaptureException(CaptureError.InvalidArgument, $"Capture config \"{path}\" is not valid JSON: {ex.Message}", ex);
        }
        if (file is not null) Apply(file);
        return this;
    }

    /// <summary>Applies an already-parsed config file.</summary>
    public CaptureOptions Apply(CaptureConfigFile file)
    {
        if (file.Enabled is { } e) Enabled = e;
        if (!string.IsNullOrWhiteSpace(file.OutputRoot)) OutputRoot = file.OutputRoot!;
        if (!string.IsNullOrWhiteSpace(file.FileTemplate)) FileTemplate = file.FileTemplate!;
        if (!string.IsNullOrWhiteSpace(file.AppName)) AppName = file.AppName!;
        if (!string.IsNullOrWhiteSpace(file.Ffmpeg)) FfmpegPath = file.Ffmpeg!;
        if (file.Fps is { } fps) Fps = fps;
        if (!string.IsNullOrWhiteSpace(file.ScreenshotEvery)) ScreenshotInterval = DurationText.Parse(file.ScreenshotEvery!);
        if (file.Video is true) SessionRecording = true;
        if (file.TapeQuality is { } tq) TapeQuality = tq;
        if (file.Audio is { } audio) Audio = audio;
        if (file.DiskWarnGB is { } gb) DiskWarnGB = gb;
        if (file.Replay is { } replay)
        {
            if (replay.Armed is { } armed) ReplayArmed = armed;
            if (replay.Seconds is { } s) ReplayLength = TimeSpan.FromSeconds(s);
        }
        if (file.Rules is { } rules) Rules.AddRange(rules);
        if (file.Hotkeys is { } keys) foreach (var kv in keys) Hotkeys[kv.Key] = kv.Value;
        if (file.PostCapture is { } post) PostCapture.AddRange(post);
        return this;
    }

    internal void Validate()
    {
        if (ScreenshotInterval is { } si && si < TimeSpan.FromMilliseconds(100))
            throw new CaptureException(CaptureError.InvalidArgument, $"ScreenshotInterval {si} is too short (minimum 100ms).");
        if (Fps is < 1 or > 240) throw new CaptureException(CaptureError.InvalidArgument, $"Fps {Fps} is out of range (1-240).");
        if (ReplayLength < TimeSpan.Zero || ReplayLength > TimeSpan.FromHours(1))
            throw new CaptureException(CaptureError.InvalidArgument, $"ReplayLength {ReplayLength} is out of range (0-1h).");
        if (string.IsNullOrWhiteSpace(OutputRoot)) throw new CaptureException(CaptureError.InvalidArgument, "OutputRoot is empty.");
        if (Json is null) throw new CaptureException(CaptureError.InvalidArgument, "CaptureOptions.Json must be set on this platform (netstandard2.1).");
        foreach (var r in Rules) TriggerRule.FromConfig(r);
    }
}

/// <summary>capture.json schema.</summary>
public sealed class CaptureConfigFile
{
    /// <summary>Kill switch.</summary>
    public bool? Enabled { get; set; }
    /// <summary>Output root.</summary>
    public string? OutputRoot { get; set; }
    /// <summary>File name template.</summary>
    public string? FileTemplate { get; set; }
    /// <summary>App folder name.</summary>
    public string? AppName { get; set; }
    /// <summary>ffmpeg path.</summary>
    public string? Ffmpeg { get; set; }
    /// <summary>Tape frame rate.</summary>
    public int? Fps { get; set; }
    /// <summary>Record the whole run as video (same as <c>-capture video</c>).</summary>
    public bool? Video { get; set; }
    /// <summary>Screenshot interval, e.g. "5s" (same as <c>-capture screen 5</c>).</summary>
    public string? ScreenshotEvery { get; set; }
    /// <summary>Tape encoding.</summary>
    public TapeQuality? TapeQuality { get; set; }
    /// <summary>Audio mode.</summary>
    public AudioMode? Audio { get; set; }
    /// <summary>Disk usage warning threshold.</summary>
    public double? DiskWarnGB { get; set; }
    /// <summary>Replay buffer settings.</summary>
    public ReplayConfig? Replay { get; set; }
    /// <summary>Trigger rules.</summary>
    public List<TriggerRuleConfig>? Rules { get; set; }
    /// <summary>Hotkeys.</summary>
    public Dictionary<string, string>? Hotkeys { get; set; }
    /// <summary>Post-capture commands.</summary>
    public List<PostCaptureConfig>? PostCapture { get; set; }
}

/// <summary>Replay buffer settings in capture.json.</summary>
public sealed class ReplayConfig
{
    /// <summary>Keep the rolling buffer running.</summary>
    public bool? Armed { get; set; }
    /// <summary>Buffer length in seconds.</summary>
    public double? Seconds { get; set; }
}

/// <summary>A command run after matching captures. Tokens: {path} {tag} {kind} {session}.</summary>
public sealed class PostCaptureConfig
{
    /// <summary>Capture kind ("screenshot", "replay", ...), tag, or "*" for all.</summary>
    public string On { get; set; } = "*";
    /// <summary>Command line run through cmd.exe.</summary>
    public string Run { get; set; } = "";
}

/// <summary>
/// Parses the <c>-capture</c> flag and the MINDATTIC_CAPTURE environment variable:
/// <list type="bullet">
/// <item><c>-capture</c> or <c>-capture video</c>: record the app's window from start to exit at native resolution.</item>
/// <item><c>-capture screen 5</c>: a PNG every 5 seconds (any duration: 500ms, 10s, 1m) for as long as the app runs.</item>
/// <item><c>-capture video screen 5</c>: both. Add <c>hq</c> for a hardware-HEVC tape instead of lossless.</item>
/// <item><c>-capture-config capture.json</c>: load settings and trigger rules.</item>
/// </list>
/// MINDATTIC_CAPTURE takes the same words ("video screen 5"); "0" is a kill switch.
/// </summary>
public sealed record CaptureArgs
{
    /// <summary>Default screenshot interval when <c>screen</c> has no number.</summary>
    public static readonly TimeSpan DefaultScreenInterval = TimeSpan.FromSeconds(5);

    /// <summary>Record the whole run as video.</summary>
    public bool Video { get; init; }
    /// <summary>Take a screenshot this often for the whole run.</summary>
    public TimeSpan? ScreenInterval { get; init; }
    /// <summary>
    /// False for a bare <c>-capture screen</c> (interval defaulted): an app with its own idea of when to shoot
    /// (a scripted tour, level checkpoints) may take over and clear <see cref="CaptureOptions.ScreenshotInterval"/>.
    /// </summary>
    public bool ScreenIntervalSpecified { get; init; }
    /// <summary>Tape quality (<c>hq</c> or <c>lossless</c>).</summary>
    public TapeQuality? Quality { get; init; }
    /// <summary><c>-capture-config path</c>.</summary>
    public string? ConfigPath { get; init; }
    /// <summary>MINDATTIC_CAPTURE=0 disables capture entirely.</summary>
    public bool KillSwitch { get; init; }
    /// <summary>Set when <c>-capture</c> was followed by a word that is not a capture mode (another meaning of the flag), which was ignored.</summary>
    public string? Ignored { get; init; }
    /// <summary>True when any capture mode was requested.</summary>
    public bool Any => Video || ScreenInterval is not null;

    /// <summary>Parses <paramref name="args"/> (and MINDATTIC_CAPTURE unless <paramref name="environment"/> is given).</summary>
    public static CaptureArgs Parse(IEnumerable<string> args, string? environment = null)
    {
        var result = new CaptureArgs();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var raw = list[i];
            var a = raw.TrimStart('-', '/');
            if (a.Length == raw.Length) continue;
            var eq = a.IndexOf('=');
            var name = (eq < 0 ? a : a.Substring(0, eq)).ToLowerInvariant();
            var value = eq < 0 ? null : a.Substring(eq + 1);
            switch (name)
            {
                case "capture":
                {
                    // Words after -capture (until the next -flag) pick the modes; "-capture=hq" is the same as "-capture hq".
                    var words = new List<string>();
                    if (value is not null) words.AddRange(value.Split([',', ' ', '+'], StringSplitOptions.RemoveEmptyEntries));
                    while (i + 1 < list.Count && IsModeWord(list[i + 1])) words.Add(list[++i]);
                    // "-capture <something else>" (e.g. an app's own "-capture <folder>") is not ours: leave it alone.
                    if (value is null && words.Count == 0 && i + 1 < list.Count && !LooksLikeFlag(list[i + 1]))
                    {
                        result = result with { Ignored = list[i + 1] };
                        break;
                    }
                    result = Apply(result, words);
                    break;
                }
                case "capture-config":
                    if (value is null && i + 1 < list.Count) value = list[++i];
                    result = result with { ConfigPath = value ?? throw new CaptureException(CaptureError.InvalidArgument, "-capture-config needs a path.") };
                    break;
            }
        }
        var env = environment ?? Environment.GetEnvironmentVariable("MINDATTIC_CAPTURE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var text = env!.Trim();
            if (text == "0") result = new CaptureArgs { KillSwitch = true, ConfigPath = result.ConfigPath };
            else if (!result.Any) result = Apply(result, text == "1" ? [] : text.Split([',', ' ', '+'], StringSplitOptions.RemoveEmptyEntries));
        }
        return result;
    }

    private static bool LooksLikeFlag(string word) => word.StartsWith("-", StringComparison.Ordinal) || (word.StartsWith("/", StringComparison.Ordinal) && word.Length > 1 && char.IsLetter(word[1]) && !word.Contains("\\"));

    /// <summary>True when <paramref name="word"/> is consumed by a preceding <c>-capture</c> (so app argument parsers can skip it).</summary>
    public static bool IsModeWord(string word)
    {
        if (word.StartsWith("-", StringComparison.Ordinal) || word.StartsWith("/", StringComparison.Ordinal)) return false;
        if (word.IndexOfAny(['\\', '/', ':']) >= 0) return false;
        var w = word.Trim().ToLowerInvariant();
        return w is "video" or "screen" or "screenshots" or "hq" or "lossless" || DurationText.TryParse(w, out _);
    }

    private static CaptureArgs Apply(CaptureArgs result, IReadOnlyList<string> words)
    {
        var video = false;
        TimeSpan? screen = null;
        var specified = result.ScreenIntervalSpecified;
        var quality = result.Quality;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i].Trim().ToLowerInvariant();
            switch (w)
            {
                case "video" or "1" or "true":
                    video = true;
                    break;
                case "screen" or "screenshots":
                    screen = DefaultScreenInterval;
                    if (i + 1 < words.Count && DurationText.TryParse(words[i + 1], out var interval))
                    {
                        if (interval < TimeSpan.FromMilliseconds(100))
                            throw new CaptureException(CaptureError.InvalidArgument, $"-capture screen interval {words[i + 1]} is too short (minimum 100ms).");
                        screen = interval;
                        specified = true;
                        i++;
                    }
                    break;
                case "hq" or "high" or "highquality":
                    quality = TapeQuality.HighQuality;
                    break;
                case "lossless" or "ffv1":
                    quality = TapeQuality.Lossless;
                    break;
                default:
                    throw new CaptureException(CaptureError.InvalidArgument,
                        $"Unknown -capture option \"{words[i]}\" (use: video, screen [seconds], hq, lossless).");
            }
        }
        // Bare "-capture" (or only a quality word) means video.
        if (!video && screen is null) video = true;
        return result with
        {
            Video = result.Video || video,
            ScreenInterval = screen ?? result.ScreenInterval,
            ScreenIntervalSpecified = specified,
            Quality = quality,
        };
    }
}
