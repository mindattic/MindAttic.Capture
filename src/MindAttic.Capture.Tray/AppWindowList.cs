using System.Runtime.InteropServices;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>The windows offered by Record App / Screenshot App: other processes' visible windows, with their icons.</summary>
internal static class AppWindowList
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICONSM = -34;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static IReadOnlyList<WindowInfo> Get()
    {
        var self = Environment.ProcessId;
        return Desktop.GetWindows().Where(w => w.ProcessId != self && w.Bounds.Width > 1 && w.Bounds.Height > 1).ToList();
    }

    public static string MenuText(WindowInfo w)
    {
        var title = w.Title.Length > 60 ? w.Title[..57] + "..." : w.Title;
        // '&' would turn the next letter into a menu mnemonic.
        return $"{title.Replace("&", "&&")}  ({w.ProcessName})";
    }

    /// <summary>The window's small icon, or null; a hung window can't stall the menu (100 ms timeout).</summary>
    public static Image? Icon(nint hwnd)
    {
        nint handle = 0;
        if (SendMessageTimeout(hwnd, WM_GETICON, ICON_SMALL2, 0, SMTO_ABORTIFHUNG, 100, out var result) != 0) handle = result;
        if (handle == 0) handle = GetClassLongPtr(hwnd, GCLP_HICONSM);
        if (handle == 0) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return icon.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint hwnd, int index);
}
