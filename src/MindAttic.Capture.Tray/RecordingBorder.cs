using System.Runtime.InteropServices;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>
/// A thin red frame drawn just outside an area while it records. Clicks pass through it, and it is excluded
/// from screen capture, so it never shows up in the footage even if the area is moved under it.
/// </summary>
internal sealed class RecordingBorder : LayeredWindow
{
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static readonly Color Edge = Color.FromArgb(235, 230, 50, 50);
    private readonly int _thickness;

    public RecordingBorder(PixelRect area)
    {
        _thickness = Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96f));
        Text = "MindAttic Capture - recording";
        Place(new PixelRect(area.X - _thickness, area.Y - _thickness, area.Width + _thickness * 2, area.Height + _thickness * 2));
    }

    protected override int ExtraExStyle => WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void Draw(Graphics g)
    {
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Edge);
        var w = ScreenBounds.Width;
        var h = ScreenBounds.Height;
        var t = _thickness;
        g.FillRectangle(brush, 0, 0, w, t);
        g.FillRectangle(brush, 0, h - t, w, t);
        g.FillRectangle(brush, 0, t, t, h - t * 2);
        g.FillRectangle(brush, w - t, t, t, h - t * 2);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
