using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MindAttic.Capture.Agent;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Triggers;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class ApiServerTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<ApiServer> StartServerAsync(FakeCapture capture, IReadOnlyList<string>? extraOrigins = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ApiServer.StartAsync(capture, GetFreePort(), extraOrigins ?? [], new TraceCaptureLog());
            }
            catch (CaptureException) when (attempt < 4)
            {
                // Another process grabbed the port between GetFreePort() and Start(); try again with a new one.
            }
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token = null, string? origin = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Add("X-Capture-Token", token);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    // --- Auth ---

    [Test]
    public async Task Missing_token_is_rejected()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Wrong_token_is_rejected()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status", token: "not-the-token");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Correct_token_is_accepted()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status", token: server.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Token_from_the_info_file_is_accepted()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var info = JsonDocument.Parse(await File.ReadAllTextAsync(ApiServer.InfoFile));
        var url = info.RootElement.GetProperty("url").GetString();
        var token = info.RootElement.GetProperty("token").GetString();
        Assert.That(url, Is.EqualTo(server.Url));

        using var client = new HttpClient();
        using var response = await SendAsync(client, HttpMethod.Post, url + "marker", token: token, body: new { name = "checkpoint" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.Marked?.Name, Is.EqualTo("checkpoint"));
    }

    // --- CORS ---

    [Test]
    public async Task Allowed_origin_is_echoed_back_in_cors_headers()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status", origin: "http://localhost:5173");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single(), Is.EqualTo("http://localhost:5173"));
        Assert.That(response.Headers.GetValues("Vary").Single(), Is.EqualTo("Origin"));
    }

    [Test]
    public async Task Disallowed_origin_is_rejected_even_with_a_valid_token()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "status");
        request.Headers.Add("X-Capture-Token", server.Token);
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Extra_origin_is_allowed_when_configured()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture, ["https://app.example.com"]);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status", origin: "https://app.example.com");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Options_preflight_returns_204_with_cors_headers()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Options, "raise", origin: "http://localhost:5173");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(response.Headers.GetValues("Access-Control-Allow-Methods").Single(), Is.EqualTo("GET, POST, OPTIONS"));
        Assert.That(response.Headers.GetValues("Access-Control-Allow-Headers").Single(), Is.EqualTo("Content-Type, X-Capture-Token"));
    }

    // --- Ops reaching the fake ---

    [Test]
    public async Task Raise_reaches_the_capture()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "raise", token: server.Token, body: new { @event = "critical-hit", data = new { damage = 42 } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.Raised?.Name, Is.EqualTo("critical-hit"));
        Assert.That(capture.Raised?.Data?["damage"], Is.EqualTo(42.0));
    }

    [Test]
    public async Task Raise_keeps_a_null_data_value_null()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "raise", token: server.Token, body: new { @event = "hit", data = new { target = (string?)null } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.Raised?.Data?.ContainsKey("target"), Is.True);
        Assert.That(capture.Raised?.Data?["target"], Is.Null);
    }

    [Test]
    public async Task Marker_reaches_the_capture()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "marker", token: server.Token, body: new { name = "level-start" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.Marked?.Name, Is.EqualTo("level-start"));
    }

    [Test]
    public async Task Screenshot_reaches_the_capture_and_returns_the_result_shape()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "screenshot", token: server.Token, body: new { tag = "boss-fight" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ScreenshotCall?.Tag, Is.EqualTo("boss-fight"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(json.RootElement.GetProperty("kind").GetString(), Is.EqualTo("screenshot"));
        Assert.That(json.RootElement.GetProperty("tag").GetString(), Is.EqualTo(capture.NextResult.Tag));
        Assert.That(json.RootElement.GetProperty("files")[0].GetString(), Is.EqualTo(capture.NextResult.Files[0]));
    }

    [Test]
    public async Task Record_start_reaches_the_capture_and_reports_recording()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "record/start", token: server.Token, body: new { tag = "run1" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.StartRecordingCall?.Tag, Is.EqualTo("run1"));
        Assert.That(capture.IsRecording, Is.True);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Record_stop_reaches_the_capture()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "record/stop", token: server.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.StopRecordingCalled, Is.True);
    }

    [Test]
    public async Task Record_pause_and_resume_reach_the_capture()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var pause = await SendAsync(client, HttpMethod.Post, "record/pause", token: server.Token);
        Assert.That(pause.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.PauseCalled, Is.True);
        using var resume = await SendAsync(client, HttpMethod.Post, "record/resume", token: server.Token);
        Assert.That(resume.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ResumeCalled, Is.True);
    }

    [Test]
    public async Task Clip_reaches_the_capture_with_the_requested_duration()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "clip", token: server.Token, body: new { seconds = 20, tag = "clutch" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ClipCall?.Duration, Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(capture.ClipCall?.Tag, Is.EqualTo("clutch"));
    }

    [Test]
    public async Task Replay_reaches_the_capture_with_the_requested_window()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "replay", token: server.Token, body: new { before = 20, after = 5, tag = "oops" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ReplayCall?.Before, Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(capture.ReplayCall?.After, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(capture.ReplayCall?.Tag, Is.EqualTo("oops"));
    }

    [Test]
    public async Task Unknown_op_returns_an_error_status()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Post, "format-disk", token: server.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("error").GetString(), Does.Contain("format-disk"));
    }

    // --- Body limits ---

    [Test]
    public async Task Oversized_body_is_rejected_by_ReadBodyAsync()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "marker") { Content = new ChunkedContent(new byte[(1 << 20) + 1024]) };
        request.Headers.Add("X-Capture-Token", server.Token);
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(capture.Marked, Is.Null);
    }

    [Test]
    public async Task Oversized_body_is_rejected_by_content_length()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        var payload = new string('a', (1 << 20) + 1024);
        using var response = await SendAsync(client, HttpMethod.Post, "marker", token: server.Token, body: new { name = payload });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(capture.Marked, Is.Null);
    }

    // --- seconds clamping ---

    [TestCase(double.NaN, 0.5, 600, 0.5)]
    [TestCase(-5, 0.5, 600, 0.5)]
    [TestCase(999999, 0.5, 600, 600)]
    [TestCase(20, 0.5, 600, 20)]
    public void Clamp_handles_NaN_and_out_of_range_values(double value, double min, double max, double expected)
    {
        var method = typeof(ApiServer).GetMethod("Clamp", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (double)method.Invoke(null, [value, min, max])!;
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task Clip_seconds_out_of_range_are_clamped_through_the_api()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var tooLarge = await SendAsync(client, HttpMethod.Post, "clip", token: server.Token, body: new { seconds = 999999 });
        Assert.That(tooLarge.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ClipCall?.Duration, Is.EqualTo(TimeSpan.FromSeconds(600)));

        using var tooSmall = await SendAsync(client, HttpMethod.Post, "clip", token: server.Token, body: new { seconds = 0 });
        Assert.That(tooSmall.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ClipCall?.Duration, Is.EqualTo(TimeSpan.FromSeconds(0.5)));

        using var omitted = await SendAsync(client, HttpMethod.Post, "clip", token: server.Token);
        Assert.That(omitted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(capture.ClipCall?.Duration, Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    // --- /status ---

    [Test]
    public async Task Status_reports_session_state()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var response = await SendAsync(client, HttpMethod.Get, "status", token: server.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.That(root.GetProperty("session").GetString(), Is.EqualTo(capture.SessionId));
        Assert.That(root.GetProperty("folder").GetString(), Is.EqualTo(capture.SessionFolder));
        Assert.That(root.GetProperty("recording").GetBoolean(), Is.EqualTo(capture.IsRecording));
        Assert.That(root.GetProperty("tapeRunning").GetBoolean(), Is.EqualTo(capture.Diagnostics.TapeRunning));
        Assert.That(root.GetProperty("enabled").GetBoolean(), Is.EqualTo(capture.Enabled));
    }

    // --- /ws ---

    [Test]
    public async Task WebSocket_dispatches_ops_and_replies_with_the_request_id()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Capture-Token", server.Token);
        await ws.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "ws"), CancellationToken.None);

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"id":"1","op":"raise","event":"ping"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.That(reply.RootElement.GetProperty("id").GetString(), Is.EqualTo("1"));
        Assert.That(reply.RootElement.GetProperty("reply").GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(capture.Raised?.Name, Is.EqualTo("ping"));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Test]
    public async Task WebSocket_reports_status()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Capture-Token", server.Token);
        await ws.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "ws"), CancellationToken.None);

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"id":"s1","op":"status"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.That(reply.RootElement.GetProperty("reply").GetProperty("session").GetString(), Is.EqualTo(capture.SessionId));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Test]
    public async Task WebSocket_keeps_the_connection_open_after_an_unknown_op()
    {
        var capture = new FakeCapture();
        await using var server = await StartServerAsync(capture);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Capture-Token", server.Token);
        await ws.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "ws"), CancellationToken.None);

        var buffer = new byte[4096];

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"id":"bad","op":"format-disk"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var badResult = await ws.ReceiveAsync(buffer, CancellationToken.None);
        using (var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, badResult.Count)))
        {
            Assert.That(reply.RootElement.GetProperty("reply").GetProperty("error").GetString(), Does.Contain("format-disk"));
        }
        Assert.That(ws.State, Is.EqualTo(WebSocketState.Open));

        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"id":"ok","op":"marker","name":"still-alive"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var okResult = await ws.ReceiveAsync(buffer, CancellationToken.None);
        using (var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, okResult.Count)))
        {
            Assert.That(reply.RootElement.GetProperty("reply").GetProperty("ok").GetBoolean(), Is.True);
        }
        Assert.That(capture.Marked?.Name, Is.EqualTo("still-alive"));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Test]
    public void WebSocket_without_auth_is_rejected()
    {
        Assert.That(async () =>
        {
            var capture = new FakeCapture();
            await using var server = await StartServerAsync(capture);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "ws"), CancellationToken.None);
        }, Throws.InstanceOf<WebSocketException>());
    }

    private sealed class ChunkedContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FakeCapture : ICapture
    {
        public bool Enabled { get; set; } = true;
        public string SessionId => "session-1";
        public string SessionFolder => @"C:\captures\session-1";
        public bool IsRecording { get; private set; }
        public CaptureDiagnostics Diagnostics { get; } = new();
        public ITriggerRegistry Triggers => NullCapture.Instance.Triggers;
        public event Action<CaptureResult>? Captured { add { } remove { } }
        public event Action<double>? DiskWarning { add { } remove { } }

        public CaptureResult NextResult { get; set; } = new()
        {
            Kind = CaptureKind.Screenshot, Tag = "shot", Files = ["C:\\captures\\shot.png"], Timestamp = DateTimeOffset.Now,
        };

        public (string Name, CaptureData? Data)? Raised { get; private set; }
        public (string Name, CaptureData? Data)? Marked { get; private set; }
        public (string Tag, ScreenshotOptions? Options)? ScreenshotCall { get; private set; }
        public (string Tag, RecordingOptions? Options)? StartRecordingCall { get; private set; }
        public bool StopRecordingCalled { get; private set; }
        public bool PauseCalled { get; private set; }
        public bool ResumeCalled { get; private set; }
        public (TimeSpan Duration, string Tag, RecordingOptions? Options)? ClipCall { get; private set; }
        public (TimeSpan Before, TimeSpan After, string Tag, RecordingOptions? Options)? ReplayCall { get; private set; }

        public Task<CaptureResult> ScreenshotAsync(string tag, ScreenshotOptions? options = null)
        {
            ScreenshotCall = (tag, options);
            return Task.FromResult(NextResult);
        }

        public Task StartRecordingAsync(string tag, RecordingOptions? options = null)
        {
            StartRecordingCall = (tag, options);
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<CaptureResult> StopRecordingAsync()
        {
            StopRecordingCalled = true;
            IsRecording = false;
            return Task.FromResult(NextResult);
        }

        public void PauseRecording() => PauseCalled = true;

        public void ResumeRecording() => ResumeCalled = true;

        public Task<CaptureResult> ClipAsync(TimeSpan duration, string tag, RecordingOptions? options = null)
        {
            ClipCall = (duration, tag, options);
            return Task.FromResult(NextResult);
        }

        public Task<CaptureResult> SaveReplayAsync(TimeSpan before, TimeSpan after, string tag, RecordingOptions? options = null)
        {
            ReplayCall = (before, after, tag, options);
            return Task.FromResult(NextResult);
        }

        public void Marker(string name, CaptureData? data = null) => Marked = (name, data);

        public void Raise(string eventName, CaptureData? data = null) => Raised = (eventName, data);

        public Task<CaptureResult?> ExecuteCommandAsync(string command) => Task.FromResult<CaptureResult?>(null);

        public ValueTask DisposeAsync() => default;
    }
}
