namespace MindAttic.Capture.Windows;

/// <summary>What to capture. Coordinates are physical pixels on the virtual desktop.</summary>
public abstract record CaptureTarget
{
    /// <summary>Monitor by index: 0 is the primary, the rest are ordered left to right, then top to bottom.</summary>
    public static CaptureTarget Monitor(int index) => new MonitorTarget(index);

    public static CaptureTarget PrimaryMonitor { get; } = new MonitorTarget(0);

    public static CaptureTarget Region(int x, int y, int width, int height) =>
        new RegionTarget(new PixelRect(x, y, width, height));

    public static CaptureTarget Window(nint hwnd) => new WindowHandleTarget(hwnd);

    /// <summary>First visible top-level window whose title contains <paramref name="titleContains"/> (exact match preferred).</summary>
    public static CaptureTarget WindowTitle(string titleContains) => new WindowTitleTarget(titleContains);

    /// <summary>Main window of a running process, by name without ".exe".</summary>
    public static CaptureTarget Process(string processName) => new ProcessTarget(processName);

    /// <summary>Main window of the calling process.</summary>
    public static CaptureTarget CurrentApp { get; } = new ProcessTarget(null);

    /// <summary>Short file-name-safe description used in output names.</summary>
    public abstract string Label { get; }
}

public sealed record MonitorTarget(int Index) : CaptureTarget
{
    public override string Label => $"monitor{Index}";
}

public sealed record RegionTarget(PixelRect Rect) : CaptureTarget
{
    public override string Label => $"region_{Rect.X}_{Rect.Y}_{Rect.Width}x{Rect.Height}";
}

public sealed record WindowHandleTarget(nint Hwnd) : CaptureTarget
{
    public override string Label => $"window_{Hwnd:x}";
}

public sealed record WindowTitleTarget(string TitleContains) : CaptureTarget
{
    public override string Label => $"window_{TitleContains}";
}

public sealed record ProcessTarget(string? ProcessName) : CaptureTarget
{
    public override string Label => ProcessName is null ? "app" : $"window_{ProcessName}";
}
