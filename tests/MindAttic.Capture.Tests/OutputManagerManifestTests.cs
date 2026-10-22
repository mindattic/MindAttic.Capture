using System.Collections.Concurrent;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

/// <summary>Covers OutputManager's sidecar and session-manifest bookkeeping (naming/root resolution is in OutputManagerTests).</summary>
public class OutputManagerManifestTests
{
    private string _dir = "";
    private readonly ICaptureJson _json = new SystemTextCaptureJson();

    [SetUp]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("capture-manifest-").FullName;

    [TearDown]
    public void Cleanup() => Directory.Delete(_dir, true);

    private OutputManager NewManager() =>
        new(new CaptureOptions { OutputRoot = _dir, AppName = "My App" }, "test-host", _json, new TraceCaptureLog());

    [Test]
    public void WriteSidecar_writes_json_next_to_the_primary_file()
    {
        var m = NewManager();
        var primary = Path.Combine(_dir, "shot.png");
        var sidecar = new CaptureSidecar
        {
            Kind = "screenshot", Tag = "boss", App = "My App", Session = m.SessionId, Timestamp = DateTimeOffset.Now, DurationSeconds = 0,
        };

        m.WriteSidecar(primary, sidecar);

        Assert.That(File.Exists(primary + ".json"), Is.True);
        var loaded = _json.Deserialize<CaptureSidecar>(File.ReadAllText(primary + ".json"))!;
        Assert.That((loaded.Kind, loaded.Tag, loaded.App, loaded.Session), Is.EqualTo(("screenshot", "boss", "My App", m.SessionId)));
    }

    [Test]
    public void AddEvent_AddMarker_AddCapture_accumulate_and_round_trip_through_SaveManifest()
    {
        var m = NewManager();

        m.AddEvent(new ManifestEvent { Name = "critical-hit", At = DateTimeOffset.Now, TapeSeconds = 1.5, Data = new() { ["damage"] = 900.0 } });
        m.AddMarker(new MarkerRecord { Name = "checkpoint", At = DateTimeOffset.Now, TapeSeconds = 2.0 });
        m.AddCapture(new CaptureResult { Kind = CaptureKind.Screenshot, Tag = "boss", Files = ["shot.png"], Timestamp = DateTimeOffset.Now, Duration = TimeSpan.Zero });

        var manifestPath = m.SessionStem + ".session.json";
        Assert.That(File.Exists(manifestPath), Is.True);
        var loaded = _json.Deserialize<SessionManifest>(File.ReadAllText(manifestPath))!;
        Assert.That(loaded.Events.Select(e => e.Name), Is.EqualTo(new[] { "critical-hit" }));
        Assert.That(loaded.Events[0].TapeSeconds, Is.EqualTo(1.5));
        Assert.That(loaded.Markers.Select(mk => mk.Name), Is.EqualTo(new[] { "checkpoint" }));
        Assert.That(loaded.Captures.Select(c => (c.Kind, c.Tag, c.Files[0])), Is.EqualTo(new[] { ("screenshot", "boss", "shot.png") }));
    }

    [Test]
    public void Concurrent_AddCapture_leaves_a_manifest_with_every_capture()
    {
        var m = NewManager();
        const int count = 300;
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            m.AddCapture(new CaptureResult { Kind = CaptureKind.Screenshot, Tag = $"t{i}", Files = [], Timestamp = DateTimeOffset.Now }));

        var loaded = _json.Deserialize<SessionManifest>(File.ReadAllText(m.SessionStem + ".session.json"))!;
        Assert.That(loaded.Captures, Has.Count.EqualTo(count));
    }

    [Test]
    public void MarkersSnapshot_is_a_safe_copy_under_concurrent_AddMarker()
    {
        var m = NewManager();
        const int tasks = 8, perTask = 250;
        var snapshotFailures = new ConcurrentBag<Exception>();

        var writer = Task.Run(() => Parallel.For(0, tasks * perTask, i => m.AddMarker(new MarkerRecord { Name = $"m{i}", TapeSeconds = i })));
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 3000; i++)
            {
                try
                {
                    var snapshot = m.MarkersSnapshot();
                    foreach (var _ in snapshot) { } // enumerating the copy must never throw, even while the writer mutates concurrently
                }
                catch (Exception ex)
                {
                    snapshotFailures.Add(ex);
                }
            }
        });
        Task.WaitAll(writer, reader);

        Assert.That(snapshotFailures, Is.Empty);
        Assert.That(m.MarkersSnapshot(), Has.Count.EqualTo(tasks * perTask));
    }

    [TestCase("My:App*Name?", "My_App_Name_")]
    [TestCase("  spaced  ", "spaced")]
    [TestCase("trailing.dot.", "trailing.dot")]
    [TestCase("a\tb\nc", "a_b_c")]
    public void Sanitize_replaces_invalid_path_characters_with_underscores(string input, string expected) =>
        Assert.That(OutputManager.Sanitize(input), Is.EqualTo(expected));

    [Test]
    public void Sanitize_truncates_to_120_characters() =>
        Assert.That(OutputManager.Sanitize(new string('x', 200)), Has.Length.EqualTo(120));
}
