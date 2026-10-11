using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Windows.Native;

namespace MindAttic.Capture.Windows.Screenshots;

internal static class GdiScreenshotter
{
    /// <summary>Windows go through PrintWindow so occluded windows still capture; everything else is a screen copy.</summary>
    public static CapturedImage Capture(ResolvedTarget target)
    {
        using var _ = DpiScope.PerMonitor();
        using var bmp = target.Hwnd != 0 && TryPrintWindow(target.Hwnd, target.Bounds) is { } printed ? printed : CopyFromScreen(target.Bounds);
        return ToImage(bmp);
    }

    private static Bitmap CopyFromScreen(PixelRect r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException)
        {
            bmp.Dispose();
            throw new CaptureException(CaptureError.CaptureFailed, $"Screen copy of {r} failed (locked workstation or secure desktop?): {ex.Message}", ex);
        }
    }

    private static Bitmap? TryPrintWindow(nint hwnd, PixelRect visible)
    {
        // PrintWindow renders the full window rect (including the invisible resize border), so crop to the visible bounds.
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return null;
        var full = wr.ToPixelRect();
        if (full.IsEmpty) return null;
        using var whole = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(whole))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT)) return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        var crop = visible.Intersect(full);
        if (crop.IsEmpty) return null;
        return whole.Clone(new Rectangle(crop.X - full.X, crop.Y - full.Y, crop.Width, crop.Height), PixelFormat.Format32bppRgb);
    }

    /// <summary>Tightly packed BGRA; locking as ARGB forces alpha to 255 (the 32bppRgb padding byte is undefined).</summary>
    internal static CapturedImage ToImage(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bmp.Width * 4;
            var pixels = new byte[rowBytes * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
            return new CapturedImage(bmp.Width, bmp.Height, pixels);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
