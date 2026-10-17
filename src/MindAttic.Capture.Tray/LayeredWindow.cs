using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Tray;

/// <summary>
/// A borderless topmost window drawn with per-pixel alpha (UpdateLayeredWindow), in physical pixels.
/// Subclasses paint into <see cref="Render"/>'s Graphics; pixels with alpha 0 are click-through.
/// </summary>
internal abstract class LayeredWindow : Form
{
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    private const int WM_DPICHANGED = 0x02E0;
    private const int ULW_ALPHA = 2;
    private const byte AC_SRC_ALPHA = 1;

    private PixelRect _bounds;
    private nint _memDc, _dib, _oldBitmap;
    private Bitmap? _surface;

    protected LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
    }

    protected virtual int ExtraExStyle => 0;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | ExtraExStyle;
            return cp;
        }
    }

    /// <summary>Where the window sits, in physical screen pixels.</summary>
    protected PixelRect ScreenBounds => _bounds;

    /// <summary>Moves/resizes the window to <paramref name="bounds"/> (physical pixels) and repaints it.</summary>
    protected void Place(PixelRect bounds)
    {
        if (bounds != _bounds || _surface is null)
        {
            _bounds = bounds;
            Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            CreateSurface(bounds.Width, bounds.Height);
        }
        Render();
    }

    /// <summary>Draws the window's pixels (client coordinates; 0,0 is <see cref="ScreenBounds"/>' top-left).</summary>
    protected abstract void Draw(Graphics g);

    protected void Render()
    {
        if (_surface is null || !IsHandleCreated) return;
        using (var g = Graphics.FromImage(_surface)) Draw(g);
        GdiFlush();
        var screenDc = GetDC(0);
        try
        {
            var dst = new POINT(_bounds.X, _bounds.Y);
            var size = new SIZE(_bounds.Width, _bounds.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void WndProc(ref Message m)
    {
        // The window is placed in physical pixels; WinForms' default DPI rescale would move it.
        if (m.Msg == WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    private void CreateSurface(int width, int height)
    {
        FreeSurface();
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        var screenDc = GetDC(0);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(0, screenDc);
        _dib = CreateDIBSection(_memDc, ref info, 0, out var bits, 0, 0);
        _oldBitmap = SelectObject(_memDc, _dib);
        // GDI+ draws straight into the DIB; premultiplied alpha is what UpdateLayeredWindow expects.
        _surface = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    private void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != 0)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteObject(_dib);
            DeleteDC(_memDc);
            _memDc = _dib = _oldBitmap = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        FreeSurface();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private record struct POINT(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private record struct SIZE(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT dst, ref SIZE size, nint hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
}
