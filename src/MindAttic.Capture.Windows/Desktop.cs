using System.Runtime.InteropServices;
using MindAttic.Capture.Windows.Native;

namespace MindAttic.Capture.Windows;

public sealed record MonitorInfo(int Index, PixelRect Bounds, bool IsPrimary, string DeviceName);

public sealed record WindowInfo(nint Hwnd, string Title, int ProcessId, string ProcessName, PixelRect Bounds);

/// <summary>Monitor and window enumeration, in physical pixels.</summary>
public static class Desktop
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        using var _ = DpiScope.PerMonitor();
        var raw = new List<(PixelRect Bounds, bool Primary, string Device)>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint h, nint _, ref NativeMethods.RECT _, nint _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfoW(h, ref info))
                raw.Add((info.rcMonitor.ToPixelRect(), (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, info.szDevice));
            return true;
        }, 0);
        return Order(raw);
    }

    internal static IReadOnlyList<MonitorInfo> Order(IEnumerable<(PixelRect Bounds, bool Primary, string Device)> raw) =>
        raw.OrderByDescending(m => m.Primary)
           .ThenBy(m => m.Bounds.X)
           .ThenBy(m => m.Bounds.Y)
           .Select((m, i) => new MonitorInfo(i, m.Bounds, m.Primary, m.Device))
           .ToList();

    /// <summary>Visible, titled, non-minimized top-level windows in z-order.</summary>
    public static IReadOnlyList<WindowInfo> GetWindows()
    {
        using var _ = DpiScope.PerMonitor();
        var result = new List<WindowInfo>();
        var names = new Dictionary<int, string>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            var title = NativeMethods.GetWindowTitle(hwnd);
            if (title.Length == 0) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!names.TryGetValue((int)pid, out var name))
                names[(int)pid] = name = ProcessName((int)pid);
            result.Add(new WindowInfo(hwnd, title, (int)pid, name, NativeMethods.GetVisibleWindowBounds(hwnd)));
            return true;
        }, 0);
        return result;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }
}
