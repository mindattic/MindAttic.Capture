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
    /// </summary>
    public static PixelRect Drag(PixelRect start, Grip grip, int dx, int dy, PixelRect bounds)
    {
        if (grip == Grip.None) return start;
        if (grip == Grip.Move) return Clamp(start with { X = start.X + dx, Y = start.Y + dy }, bounds);
        int l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        if (grip is Grip.Left or Grip.BottomLeft) l = Math.Clamp(l + dx, bounds.X, r - MinSide);
        if (grip is Grip.Right or Grip.TopRight or Grip.BottomRight) r = Math.Clamp(r + dx, l + MinSide, bounds.Right);
        if (grip is Grip.Top or Grip.TopRight) t = Math.Clamp(t + dy, bounds.Y, b - MinSide);
        if (grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) b = Math.Clamp(b + dy, t + MinSide, bounds.Bottom);
        return new PixelRect(l, t, r - l, b - t);
    }

    /// <summary>Arrow-key nudge: slides the rectangle, kept on <paramref name="bounds"/>.</summary>
    public static PixelRect Nudge(PixelRect r, int dx, int dy, PixelRect bounds) => Clamp(r with { X = r.X + dx, Y = r.Y + dy }, bounds);

    /// <summary>Smallest selectable side; anything smaller is treated as a click, not a selection.</summary>
    public const int MinSide = 16;

    /// <summary>The rectangle spanned by two drag points, whichever corner each one is.</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

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
