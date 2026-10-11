using System.Runtime.InteropServices;
using System.Text;

namespace MindAttic.Capture.Windows.Native;

internal static class NativeMethods
{
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint PW_RENDERFULLCONTENT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    public static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Visible window bounds, excluding the invisible resize border Windows 10+ adds.</summary>
    public static PixelRect GetVisibleWindowBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>()) == 0)
            return rect.ToPixelRect();
        GetWindowRect(hwnd, out rect);
        return rect.ToPixelRect();
    }
}

/// <summary>Makes Win32 calls on this thread return physical pixels regardless of the host app's DPI mode.</summary>
internal readonly struct DpiScope : IDisposable
{
    private readonly nint _previous;

    private DpiScope(nint previous) => _previous = previous;

    public static DpiScope PerMonitor() =>
        new(NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

    public void Dispose()
    {
        if (_previous != 0) NativeMethods.SetThreadDpiAwarenessContext(_previous);
    }
}
