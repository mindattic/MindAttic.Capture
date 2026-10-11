using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Agent;

/// <summary>
/// Localhost capture API for browser pages, scripts and apps that can't reference the .NET packages.
/// Binds 127.0.0.1 only. A request is accepted when its Origin is on the allowlist (localhost pages by
/// default) or, for non-browser clients without an Origin, when it carries the per-run token from
/// <see cref="InfoFile"/>. Requests can raise events, markers and captures — never run commands.
/// </summary>
internal sealed class ApiServer : IAsyncDisposable
{
    public const int DefaultPort = 47800;
    public static readonly string InfoFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Capture", "agent.json");

    private readonly HttpListener _listener;
    private readonly ICapture _capture;
    private readonly ICaptureLog _log;
    private readonly string _token;
    private readonly IReadOnlyList<string> _extraOrigins;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private ApiServer(HttpListener listener, ICapture capture, string token, IReadOnlyList<string> extraOrigins, ICaptureLog log, string url)
    {
        _listener = listener;
        _capture = capture;
        _token = token;
        _extraOrigins = extraOrigins;
        _log = log;
        Url = url;
    }

    public string Url { get; }
    public string Token => _token;

    public static Task<ApiServer> StartAsync(ICapture capture, int port, IReadOnlyList<string> extraOrigins, ICaptureLog log)
    {
        var url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(url);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new CaptureException(CaptureError.InvalidArgument, $"Cannot listen on {url}: {ex.Message} (is another agent running? use --port).", ex);
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(InfoFile)!);
        File.WriteAllText(InfoFile, JsonSerializer.Serialize(new { url, token, pid = Environment.ProcessId }));
        var server = new ApiServer(listener, capture, token, extraOrigins, log, url);
        server._loop = Task.Run(server.AcceptLoopAsync);
        return Task.FromResult(server);
    }

    internal static bool IsAllowedOrigin(string origin, IReadOnlyList<string> extra)
    {
        if (extra.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && (uri.Host is "localhost" or "127.0.0.1" or "[::1]");
    }

    private bool Authorized(HttpListenerRequest request, out string? allowOrigin)
    {
        allowOrigin = null;
        var origin = request.Headers["Origin"];
        if (origin is not null)
        {
            if (!IsAllowedOrigin(origin, _extraOrigins)) return false;
            allowOrigin = origin;
            return true;
        }
        var token = request.Headers["X-Capture-Token"] ?? request.QueryString["token"];
        return token is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_token));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (!Authorized(req, out var allowOrigin))
            {
                res.StatusCode = 403;
                res.Close();
                return;
            }
            if (allowOrigin is not null)
            {
                res.Headers["Access-Control-Allow-Origin"] = allowOrigin;
                res.Headers["Vary"] = "Origin";
            }
            if (req.HttpMethod == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Capture-Token";
                res.StatusCode = 204;
                res.Close();
                return;
            }
            if (req.IsWebSocketRequest && req.Url?.AbsolutePath == "/ws")
            {
                var ws = await ctx.AcceptWebSocketAsync(null);
                await WebSocketLoopAsync(ws.WebSocket);
                return;
            }
            JsonElement body = default;
            if (req.HttpMethod == "POST" && req.HasEntityBody)
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                if (text.Length > 0) body = JsonDocument.Parse(text).RootElement.Clone();
            }
            var op = (req.Url?.AbsolutePath ?? "/").Trim('/');
            var reply = req.HttpMethod == "GET" && op == "status" ? Status() : await DispatchAsync(op, body);
            await WriteJsonAsync(res, 200, reply);
        }
        catch (Exception ex) when (ex is JsonException or CaptureException or ArgumentException or FormatException)
        {
            await WriteJsonAsync(res, 400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.Error("API request failed.", ex);
            try { await WriteJsonAsync(res, 500, new { error = "internal error" }); }
            catch (Exception inner) when (inner is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task WebSocketLoopAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);

            object reply;
            string? id = null;
            try
            {
                var msg = JsonDocument.Parse(sb.ToString()).RootElement;
                id = msg.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                var op = msg.GetProperty("op").GetString() ?? "";
                reply = op == "status" ? Status() : await DispatchAsync(op, msg);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or CaptureException or FormatException)
            {
                reply = new { error = ex.Message };
            }
            var json = JsonSerializer.Serialize(new { id, reply });
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stop.Token);
        }
    }

    private object Status() => new
    {
        session = _capture.SessionId, folder = _capture.SessionFolder, recording = _capture.IsRecording, enabled = _capture.Enabled,
    };

    private async Task<object> DispatchAsync(string op, JsonElement body)
    {
        string? Str(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        CaptureData? Data()
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            var data = new CaptureData();
            foreach (var p in d.EnumerateObject())
            {
                data[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => p.Value.GetString(),
                    _ => p.Value.ToString(),
                };
            }
            return data;
        }
        string Need(string name) => Str(name) ?? throw new ArgumentException($"\"{name}\" is required.");

        switch (op)
        {
            case "raise":
                _capture.Raise(Need("event"), Data());
                return new { ok = true };
            case "marker":
                _capture.Marker(Need("name"), Data());
                return new { ok = true };
            case "screenshot":
                return Result(await _capture.ScreenshotAsync(Str("tag") ?? "screenshot", new ScreenshotOptions { Data = Data() }));
            case "record/start":
                await _capture.StartRecordingAsync(Str("tag") ?? "recording", new RecordingOptions { Data = Data() });
                return new { ok = _capture.IsRecording };
            case "record/stop":
                return Result(await _capture.StopRecordingAsync());
            case "record/pause":
                _capture.PauseRecording();
                return new { ok = true };
            case "record/resume":
                _capture.ResumeRecording();
                return new { ok = true };
            case "clip":
                return Result(await _capture.ClipAsync(TimeSpan.FromSeconds(Num("seconds") ?? 10), Str("tag") ?? "clip", new RecordingOptions { Data = Data() }));
            case "replay":
                return Result(await _capture.SaveReplayAsync(TimeSpan.FromSeconds(Num("before") ?? 15), TimeSpan.FromSeconds(Num("after") ?? 0),
                    Str("tag") ?? "replay", new RecordingOptions { Data = Data() }));
            default:
                throw new ArgumentException($"Unknown operation \"{op}\".");
        }
    }

    private static object Result(CaptureResult r) => new
    {
        ok = r.Succeeded, kind = r.Kind.ToString().ToLowerInvariant(), tag = r.Tag, files = r.Files, error = r.Error,
        durationSeconds = r.Duration.TotalSeconds,
    };

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        if (_loop is not null) await _loop;
        try { File.Delete(InfoFile); }
        catch (IOException) { }
    }
}

/// <summary>`capture raise event k=v ...` — posts to a running `capture serve` using the token file.</summary>
internal static class ApiClient
{
    public static async Task<int> RaiseAsync(AgentArgs args)
    {
        var name = args.Positional.FirstOrDefault() ?? throw new CaptureException(CaptureError.InvalidArgument, "raise needs an event name.");
        if (!File.Exists(ApiServer.InfoFile))
            throw new CaptureException(CaptureError.TargetNotFound, "No `capture serve` is running.");
        using var info = JsonDocument.Parse(await File.ReadAllTextAsync(ApiServer.InfoFile));
        var url = info.RootElement.GetProperty("url").GetString()!;
        var token = info.RootElement.GetProperty("token").GetString()!;
        var data = new Dictionary<string, object>();
        foreach (var kv in args.Positional.Skip(1))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) throw new CaptureException(CaptureError.InvalidArgument, $"Event data must be key=value; got \"{kv}\".");
            var value = kv[(eq + 1)..];
            data[kv[..eq]] = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : value;
        }
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "raise")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { @event = name, data }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Capture-Token", token);
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) return 0;
        Console.Error.WriteLine($"error: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (int)CaptureError.CaptureFailed;
    }
}
