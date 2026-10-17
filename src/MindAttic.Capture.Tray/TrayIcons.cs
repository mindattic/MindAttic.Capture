using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MindAttic.Capture.Tray;

/// <summary>Tray icons drawn at startup (no image assets): a gray lens when idle, a red dot while recording.</summary>
internal static class TrayIcons
{
    public static Icon Idle { get; } = Draw(Color.FromArgb(230, 230, 230), Color.FromArgb(90, 160, 255));
    public static Icon Recording { get; } = Draw(Color.FromArgb(230, 230, 230), Color.FromArgb(230, 40, 40));
    public static Icon Busy { get; } = Draw(Color.FromArgb(230, 230, 230), Color.FromArgb(240, 170, 30));

    private static Icon Draw(Color ring, Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ring, 3f);
            g.DrawEllipse(pen, 2.5f, 2.5f, 27f, 27f);
            using var brush = new SolidBrush(dot);
            g.FillEllipse(brush, 9f, 9f, 14f, 14f);
        }
        var handle = bmp.GetHicon();
        // Clone so the Icon owns its own handle, then free the one GetHicon made.
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
