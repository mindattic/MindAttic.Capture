using System.Globalization;
using MindAttic.Capture.Internal;

namespace MindAttic.Capture.Triggers;

/// <summary>What a trigger rule does when it fires.</summary>
public enum TriggerAction
{
    /// <summary>Take a screenshot.</summary>
    Screenshot,
    /// <summary>Save footage from <c>before</c> the event to <c>after</c> it.</summary>
    Replay,
    /// <summary>Record a fixed-length clip starting at the event.</summary>
    Clip,
    /// <summary>Drop a marker at the event.</summary>
    Marker,
    /// <summary>Start a recording.</summary>
    StartRecording,
    /// <summary>Stop the active recording.</summary>
    StopRecording,
    /// <summary>Start or stop recording.</summary>
    ToggleRecording,
    /// <summary>Run a code callback.</summary>
    Custom,
}

/// <summary>An event raised through <see cref="ICapture.Raise"/>.</summary>
/// <param name="Name">Event name, e.g. "critical-hit".</param>
/// <param name="Data">Optional key/values; rule <c>where</c> filters test these.</param>
/// <param name="Timestamp">Wall-clock time of the raise.</param>
/// <param name="TapeSeconds">Position on the tape when raised (replays anchor here), or null when no tape ran.</param>
public sealed record CaptureEvent(string Name, CaptureData? Data, DateTimeOffset Timestamp, double? TapeSeconds);

/// <summary>A rule as written in capture.json.</summary>
public sealed class TriggerRuleConfig
{
    /// <summary>Event name; "*" matches all and "battle-*" matches a prefix.</summary>
    public string On { get; set; } = "";
    /// <summary>screenshot | replay | clip | marker | start-recording | stop-recording | toggle-recording.</summary>
    public string Action { get; set; } = "screenshot";
    /// <summary>Output tag (defaults to the event name).</summary>
    public string? Tag { get; set; }
    /// <summary>Replay: footage before the event, e.g. "15s".</summary>
    public string? Before { get; set; }
    /// <summary>Replay: footage after the event, e.g. "5s".</summary>
    public string? After { get; set; }
    /// <summary>Clip length, e.g. "10s".</summary>
    public string? Duration { get; set; }
    /// <summary>Data filters: key to expression (">=500", "!=0", "boss", ...).</summary>
    public Dictionary<string, string>? Where { get; set; }
    /// <summary>Ignore matches for this long after firing.</summary>
    public string? Cooldown { get; set; }
    /// <summary>Fire only once matches stop arriving for this long (with the last event's data).</summary>
    public string? Debounce { get; set; }
    /// <summary>Stop firing after this many times in a session.</summary>
    public int? MaxPerSession { get; set; }
    /// <summary>Per-rule enable switch.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Recording preset for replay/clip/recording actions.</summary>
    public CapturePreset? Preset { get; set; }
    /// <summary>Image format for screenshot actions.</summary>
    public ImageFormat? Format { get; set; }
}

/// <summary>A live trigger rule. Fluent setters return the rule for chaining.</summary>
public sealed class TriggerRule
{
    private readonly List<Condition> _where = [];
    internal readonly object Gate = new();
    internal TimeSpan? LastFired;
    internal int FiredCount;
    internal long DebounceVersion;

    internal TriggerRule(string eventPattern, TriggerAction action)
    {
        if (string.IsNullOrWhiteSpace(eventPattern))
            throw new CaptureException(CaptureError.InvalidArgument, "A trigger rule needs an event name.");
        EventPattern = eventPattern.Trim();
        Action = action;
    }

    /// <summary>Event name pattern.</summary>
    public string EventPattern { get; }
    /// <summary>What happens when the rule fires.</summary>
    public TriggerAction Action { get; }
    /// <summary>Output tag; null means use the event name.</summary>
    public string? Tag { get; set; }
    /// <summary>Replay footage before the event.</summary>
    public TimeSpan Before { get; set; }
    /// <summary>Replay footage after the event.</summary>
    public TimeSpan After { get; set; }
    /// <summary>Clip length.</summary>
    public TimeSpan Duration { get; set; }
    /// <summary>Minimum time between firings.</summary>
    public TimeSpan CooldownTime { get; set; }
    /// <summary>Quiet period before firing.</summary>
    public TimeSpan DebounceTime { get; set; }
    /// <summary>Firing limit per session.</summary>
    public int? MaxPerSession { get; set; }
    /// <summary>Per-rule switch; can be flipped at runtime.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Preset for video actions.</summary>
    public CapturePreset? Preset { get; set; }
    /// <summary>Format for screenshot actions.</summary>
    public ImageFormat? Format { get; set; }
    /// <summary>Callback for <see cref="TriggerAction.Custom"/>.</summary>
    public Func<ICapture, CaptureEvent, Task>? Callback { get; set; }
    /// <summary>How many times this rule has fired this session.</summary>
    public int Fired => FiredCount;

    /// <summary>Sets <see cref="CooldownTime"/>.</summary>
    public TriggerRule Cooldown(TimeSpan value) { CooldownTime = value; return this; }
    /// <summary>Sets <see cref="DebounceTime"/>.</summary>
    public TriggerRule Debounce(TimeSpan value) { DebounceTime = value; return this; }
    /// <summary>Sets <see cref="MaxPerSession"/>.</summary>
    public TriggerRule Max(int perSession) { MaxPerSession = perSession; return this; }
    /// <summary>Sets <see cref="Tag"/>.</summary>
    public TriggerRule Tagged(string tag) { Tag = tag; return this; }
    /// <summary>Sets <see cref="Preset"/>.</summary>
    public TriggerRule WithPreset(CapturePreset preset) { Preset = preset; return this; }

    /// <summary>Adds a data filter, e.g. <c>Where("damage", "&gt;=500")</c>.</summary>
    public TriggerRule Where(string key, string expression)
    {
        _where.Add(Condition.Parse(key, expression));
        return this;
    }

    internal bool MatchesName(string name)
    {
        if (EventPattern == "*") return true;
        if (EventPattern.EndsWith("*", StringComparison.Ordinal))
            return name.StartsWith(EventPattern.Substring(0, EventPattern.Length - 1), StringComparison.OrdinalIgnoreCase);
        return string.Equals(EventPattern, name, StringComparison.OrdinalIgnoreCase);
    }

    internal bool MatchesData(CaptureData? data) => _where.All(c => c.Matches(data));

    /// <summary>Builds and validates a rule from config.</summary>
    public static TriggerRule FromConfig(TriggerRuleConfig c)
    {
        var action = (c.Action ?? "").Trim().ToLowerInvariant().Replace("_", "-") switch
        {
            "screenshot" or "shot" => TriggerAction.Screenshot,
            "replay" or "save-replay" => TriggerAction.Replay,
            "clip" => TriggerAction.Clip,
            "marker" => TriggerAction.Marker,
            "start-recording" or "record" => TriggerAction.StartRecording,
            "stop-recording" => TriggerAction.StopRecording,
            "toggle-recording" => TriggerAction.ToggleRecording,
            _ => throw new CaptureException(CaptureError.InvalidArgument, $"Rule on \"{c.On}\": unknown action \"{c.Action}\"."),
        };
        var rule = new TriggerRule(c.On, action)
        {
            Tag = c.Tag,
            Before = DurationText.ParseOptional(c.Before) ?? TimeSpan.Zero,
            After = DurationText.ParseOptional(c.After) ?? TimeSpan.Zero,
            Duration = DurationText.ParseOptional(c.Duration) ?? TimeSpan.FromSeconds(10),
            CooldownTime = DurationText.ParseOptional(c.Cooldown) ?? TimeSpan.Zero,
            DebounceTime = DurationText.ParseOptional(c.Debounce) ?? TimeSpan.Zero,
            MaxPerSession = c.MaxPerSession,
            Enabled = c.Enabled,
            Preset = c.Preset,
            Format = c.Format,
        };
        if (action == TriggerAction.Replay && rule.Before == TimeSpan.Zero && rule.After == TimeSpan.Zero)
            throw new CaptureException(CaptureError.InvalidArgument, $"Replay rule on \"{c.On}\" needs before and/or after.");
        if (c.Where is not null) foreach (var kv in c.Where) rule.Where(kv.Key, kv.Value);
        return rule;
    }

    internal sealed class Condition
    {
        private readonly string _key;
        private readonly string _op;
        private readonly string _operand;

        private Condition(string key, string op, string operand) { _key = key; _op = op; _operand = operand; }

        public static Condition Parse(string key, string expression)
        {
            var e = (expression ?? "").Trim();
            foreach (var op in new[] { ">=", "<=", "!=", "==", ">", "<", "=" })
                if (e.StartsWith(op, StringComparison.Ordinal))
                    return new Condition(key, op == "==" ? "=" : op, e.Substring(op.Length).Trim());
            return new Condition(key, "=", e);
        }

        public bool Matches(CaptureData? data)
        {
            if (data is null || !data.TryGetValue(_key, out var raw) || raw is null) return false;
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(_operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                return _op switch
                {
                    ">=" => a >= b, "<=" => a <= b, ">" => a > b, "<" => a < b,
                    "!=" => a != b, _ => a == b,
                };
            }
            var eq = string.Equals(text, _operand, StringComparison.OrdinalIgnoreCase);
            return _op switch { "=" => eq, "!=" => !eq, _ => false };
        }
    }
}
