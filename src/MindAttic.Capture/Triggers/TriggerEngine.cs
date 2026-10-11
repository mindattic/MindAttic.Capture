using System.Diagnostics;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Triggers;

/// <summary>Registers rules from code; capture.json rules land here too.</summary>
public interface ITriggerRegistry
{
    /// <summary>Starts a rule for <paramref name="eventPattern"/>; pick an action on the builder.</summary>
    TriggerRuleBuilder On(string eventPattern);

    /// <summary>Polls <paramref name="condition"/> and raises <paramref name="eventName"/> each time it turns true.</summary>
    IDisposable When(Func<bool> condition, string eventName, TimeSpan? pollInterval = null);

    /// <summary>All registered rules.</summary>
    IReadOnlyList<TriggerRule> Rules { get; }

    /// <summary>Enables or disables every rule for an event pattern. Returns how many rules changed.</summary>
    int SetEnabled(string eventPattern, bool enabled);

    /// <summary>Removes a rule.</summary>
    bool Remove(TriggerRule rule);
}

/// <summary>Picks the action for a new rule.</summary>
public sealed class TriggerRuleBuilder
{
    private readonly string _pattern;
    private readonly Func<TriggerRule, TriggerRule> _add;

    internal TriggerRuleBuilder(string pattern, Func<TriggerRule, TriggerRule> add) { _pattern = pattern; _add = add; }

    /// <summary>Screenshot when the event fires.</summary>
    public TriggerRule Screenshot(string? tag = null, ImageFormat? format = null) =>
        _add(new TriggerRule(_pattern, TriggerAction.Screenshot) { Tag = tag, Format = format });

    /// <summary>Save <paramref name="before"/> + <paramref name="after"/> of footage around the event.</summary>
    public TriggerRule Replay(TimeSpan before, TimeSpan after = default, string? tag = null) =>
        _add(new TriggerRule(_pattern, TriggerAction.Replay) { Before = before, After = after, Tag = tag });

    /// <summary>Record a clip of <paramref name="duration"/> starting at the event.</summary>
    public TriggerRule Clip(TimeSpan duration, string? tag = null) =>
        _add(new TriggerRule(_pattern, TriggerAction.Clip) { Duration = duration, Tag = tag });

    /// <summary>Drop a marker named after the event.</summary>
    public TriggerRule Marker() => _add(new TriggerRule(_pattern, TriggerAction.Marker));

    /// <summary>Start recording.</summary>
    public TriggerRule StartRecording(string? tag = null) => _add(new TriggerRule(_pattern, TriggerAction.StartRecording) { Tag = tag });

    /// <summary>Stop recording.</summary>
    public TriggerRule StopRecording() => _add(new TriggerRule(_pattern, TriggerAction.StopRecording));

    /// <summary>Start or stop recording.</summary>
    public TriggerRule ToggleRecording(string? tag = null) => _add(new TriggerRule(_pattern, TriggerAction.ToggleRecording) { Tag = tag });

    /// <summary>Run <paramref name="callback"/>.</summary>
    public TriggerRule Do(Func<ICapture, CaptureEvent, Task> callback) =>
        _add(new TriggerRule(_pattern, TriggerAction.Custom) { Callback = callback ?? throw new ArgumentNullException(nameof(callback)) });
}

/// <summary>Matches events to rules and applies cooldown, debounce and per-session limits.</summary>
internal sealed class TriggerEngine : ITriggerRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly List<TriggerRule> _rules = [];
    private readonly List<CancellationTokenSource> _pollers = [];
    private readonly Action<TriggerRule, CaptureEvent> _fire;
    private readonly Action<string> _raise;
    private readonly ICaptureLog _log;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, Task> _delay;

    public TriggerEngine(Action<TriggerRule, CaptureEvent> fire, Action<string> raise, ICaptureLog log,
        Func<TimeSpan>? clock = null, Func<TimeSpan, Task>? delay = null)
    {
        _fire = fire;
        _raise = raise;
        _log = log;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed);
        _delay = delay ?? (t => Task.Delay(t));
    }

    public IReadOnlyList<TriggerRule> Rules
    {
        get { lock (_gate) return _rules.ToList(); }
    }

    public TriggerRuleBuilder On(string eventPattern) => new(eventPattern, Add);

    public TriggerRule Add(TriggerRule rule)
    {
        lock (_gate) _rules.Add(rule);
        return rule;
    }

    public bool Remove(TriggerRule rule)
    {
        lock (_gate) return _rules.Remove(rule);
    }

    public int SetEnabled(string eventPattern, bool enabled)
    {
        var n = 0;
        foreach (var r in Rules.Where(r => string.Equals(r.EventPattern, eventPattern, StringComparison.OrdinalIgnoreCase)))
        {
            r.Enabled = enabled;
            n++;
        }
        return n;
    }

    public IDisposable When(Func<bool> condition, string eventName, TimeSpan? pollInterval = null)
    {
        var cts = new CancellationTokenSource();
        lock (_gate) _pollers.Add(cts);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _ = Task.Run(async () =>
        {
            var was = false;
            while (!cts.IsCancellationRequested)
            {
                bool now;
                try
                {
                    now = condition();
                }
                catch (Exception ex)
                {
                    _log.Warn($"Condition for \"{eventName}\" threw; it will keep being polled.", ex);
                    now = false;
                }
                if (now && !was) _raise(eventName);
                was = now;
                try { await Task.Delay(interval, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
        return new Disposer(() =>
        {
            cts.Cancel();
            lock (_gate) _pollers.Remove(cts);
        });
    }

    /// <summary>Evaluates every rule against <paramref name="e"/>. Returns the number of rules that matched.</summary>
    public int Process(CaptureEvent e)
    {
        var matched = 0;
        foreach (var rule in Rules)
        {
            if (!rule.Enabled || !rule.MatchesName(e.Name) || !rule.MatchesData(e.Data)) continue;
            matched++;
            if (rule.DebounceTime > TimeSpan.Zero)
            {
                var version = Interlocked.Increment(ref rule.DebounceVersion);
                _ = _delay(rule.DebounceTime).ContinueWith(_ =>
                {
                    if (Interlocked.Read(ref rule.DebounceVersion) == version) TryFire(rule, e);
                }, TaskScheduler.Default);
            }
            else
            {
                TryFire(rule, e);
            }
        }
        return matched;
    }

    private void TryFire(TriggerRule rule, CaptureEvent e)
    {
        lock (rule.Gate)
        {
            if (!rule.Enabled) return;
            if (rule.MaxPerSession is { } max && rule.FiredCount >= max)
            {
                _log.Debug($"Rule {rule.EventPattern}/{rule.Action} reached its limit of {max} this session.");
                return;
            }
            var now = _clock();
            if (rule.LastFired is { } last && now - last < rule.CooldownTime) return;
            rule.LastFired = now;
            rule.FiredCount++;
        }
        try
        {
            _fire(rule, e);
        }
        catch (Exception ex)
        {
            _log.Error($"Rule {rule.EventPattern}/{rule.Action} failed to start.", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var p in _pollers) p.Cancel();
            _pollers.Clear();
        }
    }

    private sealed class Disposer(Action action) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) action();
        }
    }
}
