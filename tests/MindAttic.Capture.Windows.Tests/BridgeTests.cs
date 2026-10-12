using System.IO;
using System.Text.Json;
using MindAttic.Capture.WebView2;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class WebViewBridgeTests
{
    [TestCase("https://automata.local/panel.html", true)]
    [TestCase("https://evil.example/", false)]
    [TestCase("not a url", false)]
    public void Origin_allowlist(string source, bool allowed) =>
        Assert.That(CaptureWebView2.IsAllowed(source, ["https://automata.local"]), Is.EqualTo(allowed));

    [Test]
    public void Empty_allowlist_allows_any_page() => Assert.That(CaptureWebView2.IsAllowed("https://anything.example/", []), Is.True);

    [Test]
    public async Task Raise_reaches_the_engine_and_unknown_ops_fail()
    {
        var capture = new RecordingCapture();
        using var raise = JsonDocument.Parse("""{"op":"raise","event":"critical-hit","data":{"damage":912,"crit":true}}""");
        await CaptureWebView2.DispatchAsync(capture, raise.RootElement);
        Assert.That(capture.Raised, Is.EqualTo(("critical-hit", 912.0, true)));
        using var bad = JsonDocument.Parse("""{"op":"format-disk"}""");
        Assert.ThrowsAsync<ArgumentException>(() => CaptureWebView2.DispatchAsync(capture, bad.RootElement));
        Assert.That(CaptureWebView2.ClientScript, Does.Contain("window.mindattic.capture"));
    }

    private sealed class RecordingCapture : ICapture
    {
        public (string, object?, object?) Raised;
        public bool Enabled => true;
        public string SessionId => "s";
        public string SessionFolder => "";
        public bool IsRecording => false;
        public CaptureDiagnostics Diagnostics { get; } = new();
        public Triggers.ITriggerRegistry Triggers => NullCapture.Instance.Triggers;
        public event Action<CaptureResult>? Captured { add { } remove { } }
        public event Action<double>? DiskWarning { add { } remove { } }
        public Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null) => throw new NotSupportedException();
        public Task StartRecordingAsync(string tag, RecordingOptions? options = null) => Task.CompletedTask;
        public Task<CaptureResult> StopRecordingAsync() => throw new NotSupportedException();
        public void PauseRecording() { }
        public void ResumeRecording() { }
        public Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null) => throw new NotSupportedException();
        public Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null) => throw new NotSupportedException();
        public void Marker(string name, CaptureData? data = null) { }
        public void Raise(string eventName, CaptureData? data = null) => Raised = (eventName, data?["damage"], data?["crit"]);
        public Task<CaptureResult?> ExecuteCommandAsync(string command) => Task.FromResult<CaptureResult?>(null);
        public ValueTask DisposeAsync() => default;
    }
}
