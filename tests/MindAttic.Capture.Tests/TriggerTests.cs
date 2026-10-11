using MindAttic.Capture.Hosting;
using MindAttic.Capture.Triggers;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

public class TriggerTests
{
    private readonly List<(TriggerRule Rule, CaptureEvent Event)> _fired = [];
    private TimeSpan _now;
    private readonly List<(TimeSpan Delay, TaskCompletionSource Tcs)> _delays = [];

    private TriggerEngine Engine() => new((r, e) => _fired.Add((r, e)), _ => { }, new TraceCaptureLog(),
        clock: () => _now,
        delay: d =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Add((d, tcs));
            return tcs.Task;
        });

    private static CaptureEvent Ev(string name, CaptureData? data = null) => new(name, data, DateTimeOffset.Now, 1.0);

    [SetUp]
    public void Reset()
    {
        _fired.Clear();
        _delays.Clear();
        _now = TimeSpan.Zero;
    }

    [Test]
    public void Matches_exact_wildcard_and_prefix_case_insensitively()
    {
        var engine = Engine();
        engine.On("Critical-Hit").Marker();
        engine.On("battle-*").Marker();
        engine.On("*").Marker();
        engine.Process(Ev("critical-hit"));
        engine.Process(Ev("battle-complete"));
        Assert.That(_fired.Select(f => f.Rule.EventPattern), Is.EqualTo(new[] { "Critical-Hit", "*", "battle-*", "*" }));
    }

    [TestCase(">=500", 500, true)]
    [TestCase(">=500", 499, false)]
    [TestCase(">10", 10, false)]
    [TestCase("<3", 2, true)]
    [TestCase("<=3", 3, true)]
    [TestCase("!=0", 0, false)]
    [TestCase("=7", 7, true)]
    [TestCase("7", 7.0, true)]
    public void Where_compares_numbers(string expression, double value, bool expected)
    {
        var engine = Engine();
        engine.On("hit").Marker().Where("damage", expression);
        engine.Process(Ev("hit", new CaptureData { ["damage"] = value }));
        Assert.That(_fired.Count == 1, Is.EqualTo(expected));
    }

    [Test]
    public void Where_compares_strings_and_fails_when_key_missing()
    {
        var engine = Engine();
        engine.On("boss").Marker().Where("name", "Dragon");
        engine.Process(Ev("boss", new CaptureData { ["NAME"] = "dragon" }));
        engine.Process(Ev("boss", new CaptureData { ["name"] = "slime" }));
        engine.Process(Ev("boss"));
        Assert.That(_fired, Has.Count.EqualTo(1));
    }

    [Test]
    public void Cooldown_suppresses_until_elapsed()
    {
        var engine = Engine();
        engine.On("hit").Marker().Cooldown(TimeSpan.FromSeconds(10));
        engine.Process(Ev("hit"));
        _now = TimeSpan.FromSeconds(5);
        engine.Process(Ev("hit"));
        _now = TimeSpan.FromSeconds(10);
        engine.Process(Ev("hit"));
        Assert.That(_fired, Has.Count.EqualTo(2));
    }

    [Test]
    public void MaxPerSession_caps_firings()
    {
        var engine = Engine();
        var rule = engine.On("hit").Marker().Max(2);
        for (var i = 0; i < 5; i++) engine.Process(Ev("hit"));
        Assert.That(_fired, Has.Count.EqualTo(2));
        Assert.That(rule.Fired, Is.EqualTo(2));
    }

    [Test]
    public async Task Debounce_fires_once_with_the_last_event()
    {
        var engine = Engine();
        engine.On("hit").Marker().Debounce(TimeSpan.FromSeconds(1));
        engine.Process(Ev("hit", new CaptureData { ["n"] = 1 }));
        engine.Process(Ev("hit", new CaptureData { ["n"] = 2 }));
        engine.Process(Ev("hit", new CaptureData { ["n"] = 3 }));
        Assert.That(_fired, Is.Empty);
        foreach (var d in _delays) d.Tcs.SetResult();
        await Task.Delay(100);
        Assert.That(_fired, Has.Count.EqualTo(1));
        Assert.That(_fired[0].Event.Data!["n"], Is.EqualTo(3));
    }

    [Test]
    public void Disabled_rules_do_not_fire_and_can_be_toggled()
    {
        var engine = Engine();
        engine.On("hit").Marker();
        Assert.That(engine.SetEnabled("hit", false), Is.EqualTo(1));
        engine.Process(Ev("hit"));
        engine.SetEnabled("hit", true);
        engine.Process(Ev("hit"));
        Assert.That(_fired, Has.Count.EqualTo(1));
    }

    [Test]
    public void FromConfig_parses_actions_and_durations()
    {
        var rule = TriggerRule.FromConfig(new TriggerRuleConfig
        {
            On = "boss-death", Action = "replay", Before = "15s", After = "5s", Cooldown = "30s", MaxPerSession = 3, Preset = CapturePreset.Trailer,
        });
        Assert.Multiple(() =>
        {
            Assert.That(rule.Action, Is.EqualTo(TriggerAction.Replay));
            Assert.That(rule.Before, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(rule.After, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(rule.CooldownTime, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(rule.MaxPerSession, Is.EqualTo(3));
        });
    }

    [Test]
    public void FromConfig_rejects_bad_rules()
    {
        Assert.Throws<CaptureException>(() => TriggerRule.FromConfig(new TriggerRuleConfig { On = "x", Action = "explode" }));
        Assert.Throws<CaptureException>(() => TriggerRule.FromConfig(new TriggerRuleConfig { On = "x", Action = "replay" }));
        Assert.Throws<CaptureException>(() => TriggerRule.FromConfig(new TriggerRuleConfig { On = "", Action = "marker" }));
        Assert.Throws<CaptureException>(() => TriggerRule.FromConfig(new TriggerRuleConfig { On = "x", Action = "clip", Duration = "soon" }));
    }

    [Test]
    public async Task When_raises_on_each_false_to_true_edge()
    {
        var raised = new List<string>();
        var engine = new TriggerEngine((_, _) => { }, n => { lock (raised) raised.Add(n); }, new TraceCaptureLog());
        var value = false;
        using (engine.When(() => value, "low-hp", TimeSpan.FromMilliseconds(10)))
        {
            await Task.Delay(60);
            value = true;
            await Task.Delay(60);
            value = false;
            await Task.Delay(60);
            value = true;
            await Task.Delay(60);
        }
        lock (raised) Assert.That(raised, Has.Count.EqualTo(2));
    }
}
