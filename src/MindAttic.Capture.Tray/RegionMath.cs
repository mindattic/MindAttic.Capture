using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>Pure geometry for the area selector, in physical pixels.</summary>
internal static class RegionMath
{
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
