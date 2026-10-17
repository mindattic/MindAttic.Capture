using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>What a press on the selection grabs. The upper-left corner moves the whole rectangle; the rest resize it.</summary>
internal enum Grip { None, Move, Top, Right, Bottom, Left, TopRight, BottomRight, BottomLeft }

/// <summary>Pure geometry for the area selector, in physical pixels.</summary>
internal static class RegionMath
{
    /// <summary>Which grip is under (<paramref name="x"/>, <paramref name="y"/>); <paramref name="reach"/> is the grab tolerance in pixels.</summary>
    public static Grip HitTest(PixelRect r, int x, int y, int reach)
    {
        bool Near(int a, int b) => Math.Abs(a - b) <= reach;
        bool Between(int v, int lo, int hi) => v >= lo - reach && v <= hi + reach;
        if (Near(x, r.X) && Near(y, r.Y)) return Grip.Move;
        if (Near(x, r.Right) && Near(y, r.Y)) return Grip.TopRight;
        if (Near(x, r.Right) && Near(y, r.Bottom)) return Grip.BottomRight;
        if (Near(x, r.X) && Near(y, r.Bottom)) return Grip.BottomLeft;
        if (Near(y, r.Y) && Between(x, r.X, r.Right)) return Grip.Top;
        if (Near(y, r.Bottom) && Between(x, r.X, r.Right)) return Grip.Bottom;
        if (Near(x, r.X) && Between(y, r.Y, r.Bottom)) return Grip.Left;
        if (Near(x, r.Right) && Between(y, r.Y, r.Bottom)) return Grip.Right;
        return Grip.None;
    }

    /// <summary>
    /// <paramref name="start"/> after dragging <paramref name="grip"/> by (<paramref name="dx"/>, <paramref name="dy"/>):
    /// Move slides the rectangle (kept on <paramref name="bounds"/>); the others move their edges, keeping the
    /// opposite edges fixed, never shrinking below <see cref="MinSide"/> and never leaving <paramref name="bounds"/>.
    /// With a <paramref name="ratio"/> (width / height) the shape is kept: corners scale from the opposite corner,
    /// edges scale around the middle of the other axis.
    /// </summary>
    public static PixelRect Drag(PixelRect start, Grip grip, int dx, int dy, PixelRect bounds, double? ratio = null)
    {
        if (grip == Grip.None) return start;
        if (ratio is { } k && grip != Grip.Move) return DragLocked(start, grip, dx, dy, bounds, k);
        if (grip == Grip.Move) return Clamp(start with { X = start.X + dx, Y = start.Y + dy }, bounds);
        int l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (grip is Grip.Left or Grip.BottomLeft) l = Math.Clamp(l + dx, bounds.X, r - MinSide);
        if (grip is Grip.Right or Grip.TopRight or Grip.BottomRight) r = Math.Clamp(r + dx, l + MinSide, bounds.Right);
        if (grip is Grip.Top or Grip.TopRight) t = Math.Clamp(t + dy, bounds.Y, b - MinSide);
        if (grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) b = Math.Clamp(b + dy, t + MinSide, bounds.Bottom);
        return new PixelRect(l, t, r - l, b - t);
    }

    private static PixelRect DragLocked(PixelRect start, Grip grip, int dx, int dy, PixelRect bounds, double k)
    {
        var growsRight = grip is Grip.Right or Grip.TopRight or Grip.BottomRight;
        var growsDown = grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight;
        var corner = grip is Grip.TopRight or Grip.BottomRight or Grip.BottomLeft;
        double w = grip switch
        {
            Grip.Left => start.Width - dx,
            Grip.Right => start.Width + dx,
            Grip.Top => (start.Height - dy) * k,
            Grip.Bottom => (start.Height + dy) * k,
            _ => 0,
        };
        if (corner)
        {
            // Follow whichever axis the pointer moved further along, so a vertical pull on a corner works too.
            double byX = start.Width + (growsRight ? dx : -dx);
            double byY = (start.Height + (growsDown ? dy : -dy)) * k;
            w = Math.Abs(byX - start.Width) >= Math.Abs(byY - start.Width) ? byX : byY;
        }
        double cx = start.X + start.Width / 2.0, cy = start.Y + start.Height / 2.0;
        var horizontalEdge = grip is Grip.Top or Grip.Bottom;
        var verticalEdge = grip is Grip.Left or Grip.Right;
        double roomW = horizontalEdge ? 2 * Math.Min(cx - bounds.X, bounds.Right - cx)
            : growsRight ? bounds.Right - start.X : start.Right - bounds.X;
        double roomH = verticalEdge ? 2 * Math.Min(cy - bounds.Y, bounds.Bottom - cy)
            : growsDown ? bounds.Bottom - start.Y : start.Bottom - bounds.Y;
        var minW = Math.Max(MinSide, MinSide * k);
        var maxW = Math.Max(minW, Math.Min(roomW, roomH * k));
        w = Math.Clamp(w, minW, maxW);
        var width = (int)Math.Round(w);
        var height = (int)Math.Round(w / k);
        var x = horizontalEdge ? (int)Math.Round(cx - width / 2.0) : growsRight ? start.X : start.Right - width;
        var y = verticalEdge ? (int)Math.Round(cy - height / 2.0) : growsDown ? start.Y : start.Bottom - height;
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// The rectangle of shape <paramref name="ratio"/> (width / height) closest in size to <paramref name="r"/>: same
    /// area and centre, shrunk to fit <paramref name="bounds"/>, sizes even. 1 is a square, 9/16 a tall frame.
    /// </summary>
    public static PixelRect FitAspect(PixelRect r, double ratio, PixelRect bounds)
    {
        double area = Math.Max(r.Area, (long)MinSide * MinSide);
        var w = Math.Sqrt(area * ratio);
        var h = w / ratio;
        if (w > bounds.Width) (w, h) = (bounds.Width, bounds.Width / ratio);
        if (h > bounds.Height) (w, h) = (bounds.Height * ratio, bounds.Height);
        var width = Math.Max(2, (int)w & ~1);
        var height = Math.Max(2, (int)h & ~1);
        var x = (int)Math.Round(r.X + r.Width / 2.0 - width / 2.0);
        var y = (int)Math.Round(r.Y + r.Height / 2.0 - height / 2.0);
        return Clamp(new PixelRect(x, y, width, height), bounds);
    }

    /// <summary>
    /// Where the toolbar goes: under the selection, else above it, else inside its bottom edge; always on
    /// <paramref name="screen"/> (the monitor the selection is on). <paramref name="gap"/> leaves room for the grips.
    /// </summary>
    public static (int X, int Y) ToolbarSpot(PixelRect sel, int width, int height, PixelRect screen, int gap)
    {
        var x = Math.Clamp(sel.X, screen.X, Math.Max(screen.X, screen.Right - width));
        int y;
        if (sel.Bottom + gap + height <= screen.Bottom) y = sel.Bottom + gap;
        else if (sel.Y - gap - height >= screen.Y) y = sel.Y - gap - height;
        else y = Math.Max(screen.Y, Math.Min(sel.Bottom, screen.Bottom) - gap - height);
        return (x, y);
    }

    /// <summary>Arrow-key nudge: slides the rectangle, kept on <paramref name="bounds"/>.</summary>
    public static PixelRect Nudge(PixelRect r, int dx, int dy, PixelRect bounds) => Clamp(r with { X = r.X + dx, Y = r.Y + dy }, bounds);

    /// <summary>Smallest selectable side; anything smaller is treated as a click, not a selection.</summary>
    public const int MinSide = 16;

    /// <summary>The rectangle spanned by two drag points, whichever corner each one is.</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>
    /// Like <see cref="FromCorners"/> but shaped to <paramref name="ratio"/> (width / height): the drag's longer
    /// axis sets the size and the rectangle grows from the first point toward the pointer.
    /// </summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2, double ratio)
    {
        double dx = x2 - x1, dy = y2 - y1;
        var w = Math.Max(Math.Abs(dx), Math.Abs(dy) * ratio);
        var width = (int)Math.Round(w);
        var height = (int)Math.Round(w / ratio);
        return new PixelRect(dx < 0 ? x1 - width : x1, dy < 0 ? y1 - height : y1, width, height);
    }

    /// <summary>Shrinks <paramref name="r"/> to fit <paramref name="bounds"/>, then slides it inside.</summary>
    public static PixelRect Clamp(PixelRect r, PixelRect bounds)
    {
        var w = Math.Min(r.Width, bounds.Width);
        var h = Math.Min(r.Height, bounds.Height);
        var x = Math.Clamp(r.X, bounds.X, bounds.Right - w);
        var y = Math.Clamp(r.Y, bounds.Y, bounds.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Rounds width and height down to even numbers: H.264/HEVC 4:2:0 encoders reject odd sizes.</summary>
    public static PixelRect Even(PixelRect r) => r with { Width = Math.Max(2, r.Width & ~1), Height = Math.Max(2, r.Height & ~1) };
}
