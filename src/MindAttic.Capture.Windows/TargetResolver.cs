using System.Diagnostics;
using MindAttic.Capture.Windows.Native;

namespace MindAttic.Capture.Windows;

/// <param name="Bounds">Physical-pixel rectangle on the virtual desktop.</param>
/// <param name="Hwnd">Non-zero when the target is a window (screenshots use PrintWindow for it).</param>
/// <param name="Label">File-name-safe description.</param>
internal sealed record ResolvedTarget(PixelRect Bounds, nint Hwnd, string Label);

internal static class TargetResolver
{
    public static ResolvedTarget Resolve(CaptureTarget target)
    {
        using var _ = DpiScope.PerMonitor();
        return target switch
        {
            MonitorTarget m => new(ResolveMonitor(m.Index, Desktop.GetMonitors()), 0, m.Label),
            RegionTarget r => new(ValidateRegion(r.Rect, Desktop.GetMonitors()), 0, r.Label),
            WindowHandleTarget w => FromWindow(w.Hwnd, w.Label),
            WindowTitleTarget t => FromWindow(FindByTitle(t.TitleContains), t.Label),
            ProcessTarget { ProcessName: null } p => FromWindow(FindForCurrentProcess(), p.Label),
            ProcessTarget p => FromWindow(FindForProcess(p.ProcessName!), p.Label),
            _ => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown capture target {target}."),
        };
    }

    internal static PixelRect ResolveMonitor(int index, IReadOnlyList<MonitorInfo> monitors)
    {
        if (index < 0 || index >= monitors.Count)
            throw new CaptureException(CaptureError.TargetNotFound,
                $"Monitor {index} does not exist; {monitors.Count} monitor(s) are attached (0..{monitors.Count - 1}).");
        return monitors[index].Bounds;
    }

    /// <summary>A region must be non-empty and lie entirely on attached monitors (no gaps between them).</summary>
    internal static PixelRect ValidateRegion(PixelRect region, IReadOnlyList<MonitorInfo> monitors)
    {
        if (region.IsEmpty)
            throw new CaptureException(CaptureError.InvalidArgument, $"Region {region} has no area.");
        var covered = monitors.Sum(m => m.Bounds.Intersect(region).Area);
        if (covered != region.Area)
            throw new CaptureException(CaptureError.InvalidArgument,
                $"Region {region} is not fully on screen. Monitors: {string.Join("; ", monitors.Select(m => $"#{m.Index} {m.Bounds}"))}.");
        return region;
    }

    /// <summary>Video inputs fail on off-screen pixels, so clip to the monitors' bounding box (even dimensions).</summary>
    internal static PixelRect ClipToDesktop(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        var desktop = monitors.Aggregate(default(PixelRect), (acc, m) => acc.Union(m.Bounds));
        var clipped = rect.Intersect(desktop);
        if (clipped.IsEmpty)
            throw new CaptureException(CaptureError.TargetNotFound, $"{rect} is entirely off screen.");
        return clipped with { Width = clipped.Width & ~1, Height = clipped.Height & ~1 };
    }

    private static ResolvedTarget FromWindow(nint hwnd, string label)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            throw new CaptureException(CaptureError.TargetNotFound, $"Window 0x{hwnd:x} does not exist.");
        if (NativeMethods.IsIconic(hwnd))
            throw new CaptureException(CaptureError.TargetNotFound, $"Window 0x{hwnd:x} is minimized.");
        var bounds = NativeMethods.GetVisibleWindowBounds(hwnd);
        if (bounds.IsEmpty)
            throw new CaptureException(CaptureError.TargetNotFound, $"Window 0x{hwnd:x} has no visible area.");
        return new ResolvedTarget(bounds, hwnd, label);
    }

    private static nint FindByTitle(string titleContains)
    {
        var windows = Desktop.GetWindows();
        var match = windows.FirstOrDefault(w => string.Equals(w.Title, titleContains, StringComparison.OrdinalIgnoreCase))
                    ?? windows.FirstOrDefault(w => w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));
        return match?.Hwnd ?? throw new CaptureException(CaptureError.TargetNotFound,
            $"No visible window title contains \"{titleContains}\".");
    }

    private static nint FindForProcess(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        try
        {
            if (processes.Length == 0)
                throw new CaptureException(CaptureError.TargetNotFound, $"No running process named \"{name}\".");
            foreach (var p in processes)
            {
                var hwnd = WindowFor(p);
                if (hwnd != 0) return hwnd;
            }
            throw new CaptureException(CaptureError.TargetNotFound, $"Process \"{name}\" has no visible window.");
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static nint FindForCurrentProcess()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        var hwnd = WindowFor(p);
        return hwnd != 0 ? hwnd : throw new CaptureException(CaptureError.TargetNotFound,
            "The current process has no visible window (console apps should target a monitor, region or window title).");
    }

    private static nint WindowFor(Process p)
    {
        var main = p.MainWindowHandle;
        if (main != 0 && NativeMethods.IsWindowVisible(main) && !NativeMethods.IsIconic(main)) return main;
        return Desktop.GetWindows().FirstOrDefault(w => w.ProcessId == p.Id)?.Hwnd ?? 0;
    }
}
