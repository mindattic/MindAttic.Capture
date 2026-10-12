using System.Globalization;

namespace MindAttic.Capture.Internal;

/// <summary>Parses "1500ms", "5s", "2.5s", "3m", "1h"; a bare number means seconds.</summary>
internal static class DurationText
{
    public static TimeSpan Parse(string text)
    {
        if (TryParse(text, out var value)) return value;
        throw new CaptureException(CaptureError.InvalidArgument, $"\"{text}\" is not a duration (use e.g. 500ms, 5s, 2m, 1h).");
    }

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text!.Trim().ToLowerInvariant();
        (string num, double scale) = t switch
        {
            _ when t.EndsWith("ms") => (t.Substring(0, t.Length - 2), 0.001),
            _ when t.EndsWith("s") => (t.Substring(0, t.Length - 1), 1.0),
            _ when t.EndsWith("m") => (t.Substring(0, t.Length - 1), 60.0),
            _ when t.EndsWith("h") => (t.Substring(0, t.Length - 1), 3600.0),
            _ => (t, 1.0),
        };
        // "nan" and huge numbers parse as doubles but TimeSpan.FromSeconds throws on them.
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || n < 0
            || n * scale >= TimeSpan.MaxValue.TotalSeconds) return false;
        value = TimeSpan.FromSeconds(n * scale);
        return true;
    }

    public static TimeSpan? ParseOptional(string? text) => string.IsNullOrWhiteSpace(text) ? null : Parse(text!);

    public static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
