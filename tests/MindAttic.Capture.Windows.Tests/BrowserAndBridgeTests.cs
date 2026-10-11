using System.IO;
using System.Text.Json;
using MindAttic.Capture.Browser;
using MindAttic.Capture.WebView2;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class BrowserTargetTests
{
    private static BrowserTarget Parse(params string[] args) =>
        BrowserTarget.Parse(args, d => d == @"D:\Web\mindattic.com", f => f is @"D:\Web\mindattic.com\index.htm" or @"D:\Web\page.html");

    [Test]
    public void Folder_is_served_from_a_virtual_host()
    {
        var t = Parse(@"D:\Web\mindattic.com", "-capture", "video", "screen", "5");
        Assert.That(t, Is.EqualTo(new BrowserTarget("https://mindattic.com.local/index.htm", @"D:\Web\mindattic.com", "mindattic.com.local", "mindattic.com")));
    }

    [Test]
    public void Capture_words_are_not_mistaken_for_the_target()
    {
        var t = Parse("-capture", "screen", "5", "https://example.com/x");
        Assert.That((t.Url, t.AppName), Is.EqualTo(("https://example.com/x", "example.com")));
    }

    [Test]
    public void Html_file_maps_its_folder()
    {
        var t = Parse(@"D:\Web\page.html", "--host", "demo.local");
        Assert.That((t.Url, t.SiteFolder, t.VirtualHost), Is.EqualTo(("https://demo.local/page.html", @"D:\Web", "demo.local")));
    }

    [Test]
    public void Site_folder_names_the_captures_and_generic_folders_fall_back_to_the_repo()
    {
        Assert.That(BrowserTarget.SubjectName(@"D:\Projects\MindAttic\MindAttic.Web\mindattic.com", "mindattic.com"), Is.EqualTo("mindattic.com"));
        var repo = Path.Combine(Path.GetTempPath(), "mac-repo-" + Guid.NewGuid().ToString("N"));
        var dist = Path.Combine(repo, "MySite", "dist");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(dist);
        try
        {
            Assert.That(BrowserTarget.SubjectName(dist, "dist"), Is.EqualTo(Path.GetFileName(repo)));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public void No_target_opens_home() => Assert.That(Parse().Url, Is.EqualTo(BrowserTarget.Home));

    [TestCase("mindattic.com", "https://mindattic.com")]
    [TestCase("localhost:5173", "http://localhost:5173")]
    [TestCase("https://a.b/c?d", "https://a.b/c?d")]
    [TestCase("parasite eve", "https://www.bing.com/search?q=parasite%20eve")]
    public void Address_bar_text_becomes_a_url(string text, string url) => Assert.That(BrowserTarget.NormalizeUrl(text), Is.EqualTo(url));
}

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
