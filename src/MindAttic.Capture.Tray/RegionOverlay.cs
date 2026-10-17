using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

internal enum OverlayAction { Cancel, Confirm }

internal sealed record OverlayResult(OverlayAction Action, PixelRect Region);

/// <summary>
/// Record Area's selector: dims every monitor in semi-transparent gray, and the dragged rectangle is a
/// light-blue outlined, fully clear cut-out. Enter confirms, Esc cancels.
/// </summary>
internal sealed class RegionOverlay : LayeredWindow
{
    private static readonly Color Dim = Color.FromArgb(140, 20, 20, 24);
    // Alpha 1, not 0: the cut-out looks clear but still takes the mouse (alpha-0 pixels are click-through).
    private static readonly Color Hole = Color.FromArgb(1, 0, 0, 0);
    internal static readonly Color Accent = Color.FromArgb(255, 110, 190, 255);

    private readonly PixelRect _desktop;
    private readonly TaskCompletionSource<OverlayResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PixelRect? _selection;
    private Point? _drawFrom;

    private RegionOverlay(PixelRect? initial)
    {
        _desktop = VirtualDesktop();
        _selection = initial is { } r ? RegionMath.Clamp(r, _desktop) : null;
        Cursor = Cursors.Cross;
        Text = "MindAttic Capture - select an area";
        Place(_desktop);
    }

    /// <summary>Shows the selector (optionally starting from <paramref name="initial"/>) and completes when the user confirms or cancels.</summary>
    public static Task<OverlayResult> PickAsync(PixelRect? initial = null)
    {
        var overlay = new RegionOverlay(initial);
        overlay.Show();
        overlay.Activate();
        return overlay._done.Task;
    }

    internal static PixelRect VirtualDesktop() =>
        Desktop.GetMonitors().Aggregate(default(PixelRect), (all, m) => all.Union(m.Bounds));

    private Point ToScreen(Point client) => new(client.X + _desktop.X, client.Y + _desktop.Y);

    private Rectangle ToClient(PixelRect r) => new(r.X - _desktop.X, r.Y - _desktop.Y, r.Width, r.Height);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drawFrom = ToScreen(e.Location);
        _selection = null;
        Render();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drawFrom is not { } from) return;
        var to = ToScreen(e.Location);
        _selection = RegionMath.Clamp(RegionMath.FromCorners(from.X, from.Y, to.X, to.Y), _desktop);
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drawFrom is null) return;
        _drawFrom = null;
        if (_selection is { } s && (s.Width < RegionMath.MinSide || s.Height < RegionMath.MinSide)) _selection = null;
        Render();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Finish(OverlayAction.Cancel);
        else if (e.KeyCode == Keys.Enter && _selection is not null) Finish(OverlayAction.Confirm);
    }

    private void Finish(OverlayAction action)
    {
        var region = _selection is { } s ? RegionMath.Even(s) : default;
        _done.TrySetResult(new OverlayResult(_selection is null ? OverlayAction.Cancel : action, region));
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _done.TrySetResult(new OverlayResult(OverlayAction.Cancel, default));
        base.OnFormClosed(e);
    }

    protected override void Draw(Graphics g)
    {
        g.Clear(Dim);
        var scale = DeviceDpi / 96f;
        using var font = new Font("Segoe UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (_selection is not { } sel)
        {
            Label(g, font, "Drag to select an area   Â·   Enter to confirm   Â·   Esc to cancel", PrimaryCenterTop(scale), center: true);
            return;
        }
        var r = ToClient(sel);
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var hole = new SolidBrush(Hole)) g.FillRectangle(hole, r);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.None;
        var border = Math.Max(2, (int)Math.Round(2 * scale));
        using (var pen = new Pen(Accent, border) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(pen, Rectangle.Inflate(r, border, border));
        var even = RegionMath.Even(sel);
        Label(g, font, $"{even.Width} x {even.Height}", new Point(r.X, r.Y - (int)(30 * scale)), center: false);
    }

    private Point PrimaryCenterTop(float scale)
    {
        var primary = Desktop.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Bounds ?? _desktop;
        return new Point(primary.X - _desktop.X + primary.Width / 2, primary.Y - _desktop.Y + (int)(40 * scale));
    }

    /// <summary>White text on a dark pill, kept on screen.</summary>
    private void Label(Graphics g, Font font, string text, Point at, bool center)
    {
        var size = g.MeasureString(text, font);
        var pad = font.Size * 0.5f;
        var x = center ? at.X - size.Width / 2 - pad : at.X;
        var box = new RectangleF(x, at.Y, size.Width + pad * 2, size.Height + pad);
        box.X = Math.Clamp(box.X, 0, Math.Max(0, _desktop.Width - box.Width));
        box.Y = Math.Clamp(box.Y, 0, Math.Max(0, _desktop.Height - box.Height));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Color.FromArgb(220, 15, 15, 18))) g.FillRectangle(back, box);
        g.DrawString(text, font, Brushes.White, box.X + pad, box.Y + pad / 2);
        g.SmoothingMode = SmoothingMode.None;
    }
}
