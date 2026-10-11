namespace MindAttic.Capture.Windows;

/// <summary>A rectangle in physical pixels on the virtual desktop.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : default;
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new PixelRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
