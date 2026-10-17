namespace MindAttic.Capture.Tray;

/// <summary>The words of the notification shown when a capture finishes.</summary>
internal static class CaptureReport
{
    public static (string Title, string Text) Describe(CaptureResult result, string? sessionFolder)
    {
        if (result.Succeeded)
        {
            var kind = result.Kind == CaptureKind.Screenshot ? "Screenshot" : $"Recording ({TrayApp.FormatElapsed(result.Duration)})";
            var names = string.Join(Environment.NewLine, result.Files.Select(f => $"{Path.GetFileName(f)}  ({Size(f)})"));
            return ($"{kind} saved", $"{names}{Environment.NewLine}Click to show it in its folder.");
        }
        var log = sessionFolder is null ? "" : $"{Environment.NewLine}Details: the .log in {sessionFolder} (click to open).";
        return ("Capture failed", $"{result.Error ?? "Unknown error."}{log}");
    }

    private static string Size(string file)
    {
        try
        {
            var bytes = new FileInfo(file).Length;
            return bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
                : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
                : $"{Math.Max(1, bytes / 1024)} KB";
        }
        catch (IOException)
        {
            return "?";
        }
    }
}
