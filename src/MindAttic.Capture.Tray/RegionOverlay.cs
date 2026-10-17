using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>How the selector closed: Confirm is Enter (what it then does is up to whoever opened it).</summary>
internal enum OverlayAction { Cancel, Confirm, Record, Screenshot }

internal sealed record OverlayResult(OverlayAction Action, AreaChoice Choice);

/// <summary>
/// Record Area's selector: dims every monitor in semi-transparent gray, and the dragged rectangle is a
/// light-blue outlined, fully clear cut-out. The upper-left grip moves it, the other grips resize it,
/// arrow keys nudge it (Shift: 10 px). Enter confirms, Esc cancels.
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
    private Grip _grip;
    private Point _gripFrom;
    private PixelRect _gripStart;
    private AspectRatio _ratio;
    private readonly RegionToolbar _toolbar;

    private RegionOverlay(AreaChoice start, IReadOnlyList<SavedRegion> regions, Func<string, AreaChoice, IReadOnlyList<SavedRegion>> save)
    {
        _desktop = VirtualDesktop();
        _selection = start.Region.IsEmpty ? null : RegionMath.Clamp(start.Region, _desktop);
        _ratio = start.Ratio;
        Cursor = Cursors.Cross;
        Text = "MindAttic Capture - select an area";
        _toolbar = new RegionToolbar(start.Ratio, start.Video, start.Image);
        _toolbar.RatioPicked += PickRatio;
        _toolbar.ActionPicked += action =>
        {
            if (action == OverlayAction.Cancel || _selection is not null) Finish(action);
        };
        _toolbar.SetRegions(regions);
        _toolbar.RegionPicked += region =>
        {
            _ratio = AspectRatio.Find(region.Ratio);
            _toolbar.SetRatio(_ratio);
            _toolbar.SetFormats(region.Video, region.Image);
            _selection = RegionMath.Clamp(region.Rect, _desktop);
            Render();
            PlaceToolbar();
        };
        _toolbar.SaveRequested += name =>
        {
            if (_selection is null) return;
            _toolbar.SetRegions(save(name, Choice()), name);
        };
        _toolbar.KeyForwarded += e =>
        {
            OnKeyDown(e);
            if (!IsDisposed) Activate();
        };
        Place(_desktop);
    }

    /// <summary>
    /// Shows the selector starting from <paramref name="start"/> (an empty region starts with nothing selected)
    /// and completes when the user picks an action or cancels. The toolbar lists <paramref name="regions"/>;
    /// <paramref name="save"/> stores the current area under a name and returns the updated list.
    /// </summary>
    public static Task<OverlayResult> PickAsync(AreaChoice start, IReadOnlyList<SavedRegion> regions, Func<string, AreaChoice, IReadOnlyList<SavedRegion>> save)
    {
        var overlay = new RegionOverlay(start, regions, save);
        overlay.Show();
        overlay.Activate();
        overlay.PlaceToolbar();
        return overlay._done.Task;
    }

    private void PickRatio(AspectRatio ratio)
    {
        _ratio = ratio;
        _toolbar.SetRatio(ratio);
        if (_selection is { } s && ratio.Value is { } k) _selection = RegionMath.FitAspect(s, k, _desktop);
        Render();
        PlaceToolbar();
        Activate();
    }

    /// <summary>Shows the toolbar beside the selection on its monitor; hidden while there is nothing selected.</summary>
    private void PlaceToolbar()
    {
        if (_selection is not { } s || _drawFrom is not null || _grip != Grip.None)
        {
            _toolbar.Hide();
            return;
        }
        var center = new PixelRect(s.X + s.Width / 2, s.Y + s.Height / 2, 1, 1);
        var screen = Desktop.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(center))?.Bounds ?? _desktop;
        var size = _toolbar.Visible ? _toolbar.Size : _toolbar.GetPreferredSize(Size.Empty);
        var (x, y) = RegionMath.ToolbarSpot(s, size.Width, size.Height, screen, (int)Math.Round(16 * DpiScale));
        _toolbar.Location = new Point(x, y);
        if (!_toolbar.Visible) _toolbar.Show(this);
    }

    internal static PixelRect VirtualDesktop() =>
        Desktop.GetMonitors().Aggregate(default(PixelRect), (all, m) => all.Union(m.Bounds));

    private float DpiScale => DeviceDpi / 96f;

    private int Reach => (int)Math.Round(8 * DpiScale);

    private Point ToScreen(Point client) => new(client.X + _desktop.X, client.Y + _desktop.Y);

    private Rectangle ToClient(PixelRect r) => new(r.X - _desktop.X, r.Y - _desktop.Y, r.Width, r.Height);

    private Grip GripAt(Point screen) => _selection is { } s ? RegionMath.HitTest(s, screen.X, screen.Y, Reach) : Grip.None;

    private static bool Inside(PixelRect r, Point p) => p.X > r.X && p.X < r.Right && p.Y > r.Y && p.Y < r.Bottom;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var p = ToScreen(e.Location);
        var grip = GripAt(p);
        if (grip != Grip.None && _selection is { } s)
        {
            _grip = grip;
            _gripFrom = p;
            _gripStart = s;
            _toolbar.Hide();
            return;
        }
        // A press inside the cut-out does nothing, so a stray click can't throw the selection away.
        if (_selection is { } current && Inside(current, p)) return;
        _drawFrom = p;
        _selection = null;
        _toolbar.Hide();
        Render();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToScreen(e.Location);
        if (_grip != Grip.None)
        {
            _selection = RegionMath.Drag(_gripStart, _grip, p.X - _gripFrom.X, p.Y - _gripFrom.Y, _desktop, _ratio.Value);
            Render();
            return;
        }
        if (_drawFrom is { } from)
        {
            var drawn = _ratio.Value is { } k
                ? RegionMath.FromCorners(from.X, from.Y, p.X, p.Y, k)
                : RegionMath.FromCorners(from.X, from.Y, p.X, p.Y);
            // A shaped rectangle that runs off the desktop is refitted rather than clipped, so it keeps its shape.
            _selection = _ratio.Value is { } fit && !_desktop.Contains(drawn)
                ? RegionMath.FitAspect(RegionMath.Clamp(drawn, _desktop), fit, _desktop)
                : RegionMath.Clamp(drawn, _desktop);
            Render();
            return;
        }
        Cursor = CursorFor(GripAt(p), _selection is { } s && Inside(s, p));
    }

    private static Cursor CursorFor(Grip grip, bool inside) => grip switch
    {
        Grip.Move => Cursors.SizeAll,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        Grip.BottomRight => Cursors.SizeNWSE,
        _ => inside ? Cursors.Default : Cursors.Cross,
    };

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_grip != Grip.None)
        {
            _grip = Grip.None;
            PlaceToolbar();
            return;
        }
        if (_drawFrom is null) return;
        _drawFrom = null;
        if (_selection is { } s && (s.Width < RegionMath.MinSide || s.Height < RegionMath.MinSide)) _selection = null;
        Render();
        PlaceToolbar();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.Shift ? 10 : 1;
        switch (e.KeyCode)
        {
            case Keys.Escape: Finish(OverlayAction.Cancel); break;
            case Keys.Enter when _selection is not null: Finish(OverlayAction.Confirm); break;
            case Keys.Left: Nudge(-step, 0); break;
            case Keys.Right: Nudge(step, 0); break;
            case Keys.Up: Nudge(0, -step); break;
            case Keys.Down: Nudge(0, step); break;
        }
    }

    private void Nudge(int dx, int dy)
    {
        if (_selection is not { } s) return;
        _selection = RegionMath.Nudge(s, dx, dy, _desktop);
        Render();
        PlaceToolbar();
    }

    private AreaChoice Choice() =>
        new(_selection is { } s ? RegionMath.Even(s) : default, _ratio, _toolbar.Video, _toolbar.Image);

    private void Finish(OverlayAction action)
    {
        _done.TrySetResult(new OverlayResult(_selection is null ? OverlayAction.Cancel : action, Choice()));
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _done.TrySetResult(new OverlayResult(OverlayAction.Cancel, Choice()));
        _toolbar.Close();
        _toolbar.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Draw(Graphics g)
    {
        g.Clear(Dim);
        var scale = DpiScale;
        using var font = new Font("Segoe UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (_selection is not { } sel)
        {
            Label(g, font, "Drag to select an area  |  Enter to confirm  |  Esc to cancel", PrimaryCenterTop(scale), center: true);
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
        DrawGrips(g, r, scale);
        var even = RegionMath.Even(sel);
        var shape = _ratio.Value is null ? "" : $"   {_ratio.Name}";
        Label(g, font, $"{even.Width} x {even.Height}{shape}",new Point(r.X + (int)(24 * scale), r.Y - (int)(34 * scale)), center: false);
    }

    /// <summary>White squares on the corners and edge midpoints; a larger blue move grip with a four-way arrow on the upper-left corner.</summary>
    private static void DrawGrips(Graphics g, Rectangle r, float scale)
    {
        var half = (int)Math.Round(4 * scale);
        using var outline = new Pen(Accent, Math.Max(1, scale));
        foreach (var p in new[]
                 {
                     new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom),
                     new Point(r.Left + r.Width / 2, r.Top), new Point(r.Left + r.Width / 2, r.Bottom),
                     new Point(r.Left, r.Top + r.Height / 2), new Point(r.Right, r.Top + r.Height / 2),
                 })
        {
            var box = new Rectangle(p.X - half, p.Y - half, half * 2, half * 2);
            g.FillRectangle(Brushes.White, box);
            g.DrawRectangle(outline, box);
        }
        var m = (int)Math.Round(10 * scale);
        var move = new Rectangle(r.Left - m, r.Top - m, m * 2, m * 2);
        using (var fill = new SolidBrush(Accent)) g.FillRectangle(fill, move);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var arrow = new Pen(Color.White, Math.Max(1.5f, 1.5f * scale)) { StartCap = LineCap.ArrowAnchor, EndCap = LineCap.ArrowAnchor };
        var inset = 3 * scale;
        g.DrawLine(arrow, move.Left + inset, r.Top, move.Right - inset, r.Top);
        g.DrawLine(arrow, r.Left, move.Top + inset, r.Left, move.Bottom - inset);
        g.SmoothingMode = SmoothingMode.None;
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
