using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MindAttic.Capture.WebView2;

/// <summary>Bridge settings.</summary>
public sealed class WebViewBridgeOptions
{
    /// <summary>
    /// Origins allowed to call the bridge (e.g. "https://app.local"). Empty allows every page, which suits
    /// a capture browser; restrict it in apps that load untrusted sites.
    /// </summary>
    public List<string> AllowedOrigins { get; } = [];

    /// <summary>The capture to drive; defaults to <see cref="Capture.Current"/> at call time.</summary>
    public ICapture? Capture { get; set; }
}

/// <summary>
/// Wires <c>window.mindattic.capture</c> into a WebView2 so page script can raise events and take captures:
/// <code>await CaptureWebView2.AttachAsync(webView.CoreWebView2);</code>
/// Pages can only raise events, drop markers and request captures; they can never run commands.
/// </summary>
public static class CaptureWebView2
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(CaptureWebView2).Assembly.GetManifestResourceStream("mindattic-capture.js")
                           ?? throw new InvalidOperationException("mindattic-capture.js resource missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The JavaScript client (also usable in plain pages with <c>capture serve</c>).</summary>
    public static string ClientScript => Script.Value;

    /// <summary>Injects the client into every document and answers its messages.</summary>
    public static async Task AttachAsync(CoreWebView2 core, WebViewBridgeOptions? options = null)
    {
        options ??= new WebViewBridgeOptions();
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ClientScript);
        core.WebMessageReceived += async (_, e) =>
        {
            JsonElement request;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (!doc.RootElement.TryGetProperty("mindatticCapture", out var r) || r.ValueKind != JsonValueKind.Object) return;
                request = r.Clone();
            }
            catch (JsonException)
            {
                return;
            }
            var id = request.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            object result;
            if (!IsAllowed(e.Source, options.AllowedOrigins))
            {
                result = new { ok = false, error = "origin not allowed" };
            }
            else
            {
                try
                {
                    result = await DispatchAsync(options.Capture ?? Capture.Current, request);
                }
                catch (Exception ex)
                {
                    // This is an async void handler on the UI thread: anything escaping it would crash the app
                    // (e.g. a capture cancelled because the engine shut down mid-request).
                    result = new { ok = false, error = ex.Message };
                }
            }
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { mindatticCaptureReply = new { id, result } }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The page navigated away or the WebView closed before the capture finished.
            }
        };
    }

    internal static bool IsAllowed(string source, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0) return true;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return allowed.Any(a => string.Equals(a.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Executes one bridge request against <paramref name="capture"/>.</summary>
    internal static async Task<object> DispatchAsync(ICapture capture, JsonElement body)
    {
        string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        string Need(string name) => Str(name) is { Length: > 0 } s ? s : throw new ArgumentException($"\"{name}\" is required.");
        CaptureData? Data()
        {
            if (!body.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            var data = new CaptureData();
            foreach (var p in d.EnumerateObject())
            {
                data[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => p.Value.ToString(),
                };
            }
            return data;
        }

        switch (Need("op"))
        {
            case "raise":
                capture.Raise(Need("event"), Data());
                return new { ok = capture.Enabled };
            case "marker":
                capture.Marker(Need("name"), Data());
                return new { ok = capture.Enabled };
            case "screenshot":
                return Result(await capture.ScreenshotAsync(Str("tag") ?? "screenshot", new ScreenshotOptions { Data = Data() }));
            case "clip":
                return Result(await capture.ClipAsync(TimeSpan.FromSeconds(Clamp(Num("seconds") ?? 10, 0.5, 600)), Str("tag") ?? "clip", new RecordingOptions { Data = Data() }));
            case "replay":
                return Result(await capture.SaveReplayAsync(TimeSpan.FromSeconds(Clamp(Num("before") ?? 15, 0, 3600)),
                    TimeSpan.FromSeconds(Clamp(Num("after") ?? 0, 0, 600)), Str("tag") ?? "replay", new RecordingOptions { Data = Data() }));
            case "record/start":
                await capture.StartRecordingAsync(Str("tag") ?? "recording", new RecordingOptions { Data = Data() });
                return new { ok = capture.IsRecording };
            case "record/stop":
                return Result(await capture.StopRecordingAsync());
            case "record/pause":
                capture.PauseRecording();
                return new { ok = true };
            case "record/resume":
                capture.ResumeRecording();
                return new { ok = true };
            case "status":
                return new { ok = true, enabled = capture.Enabled, recording = capture.IsRecording, session = capture.SessionId };
            default:
                throw new ArgumentException($"Unknown operation \"{Str("op")}\".");
        }
    }

    private static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));

    private static object Result(CaptureResult r) => new
    {
        ok = r.Succeeded, kind = r.Kind.ToString().ToLower(CultureInfo.InvariantCulture), tag = r.Tag, files = r.Files, error = r.Error,
        durationSeconds = r.Duration.TotalSeconds,
    };
}
