using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Windows.Native;

/// <summary>Puts a captured frame on the Windows clipboard as a standard CF_DIB bitmap.</summary>
internal static class ClipboardWriter
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int BitmapInfoHeaderSize = 40;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint hMem);

    /// <summary>
    /// Replaces the clipboard contents with <paramref name="image"/> as a bottom-up 32bpp CF_DIB — the
    /// format every clipboard-aware app (Paint, Word, Slack, Teams, image editors) reads.
    /// </summary>
    public static void SetImage(CapturedImage image)
    {
        var dib = BuildDibBytes(image);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)dib.Length);
        if (hMem == 0) throw new CaptureException(CaptureError.CaptureFailed, "Could not allocate clipboard memory.");
        var committed = false;
        try
        {
            var ptr = GlobalLock(hMem);
            if (ptr == 0) throw new CaptureException(CaptureError.CaptureFailed, "Could not lock clipboard memory.");
            try
            {
                Marshal.Copy(dib, 0, ptr, dib.Length);
            }
            finally
            {
                GlobalUnlock(hMem);
            }
            OpenClipboardWithRetry();
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_DIB, hMem) == 0)
                    throw new CaptureException(CaptureError.CaptureFailed, "SetClipboardData failed.");
                committed = true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (!committed) GlobalFree(hMem);
        }
    }

    /// <summary>A BITMAPINFOHEADER followed by bottom-up 32bpp pixel data, ready to drop behind CF_DIB.</summary>
    internal static byte[] BuildDibBytes(CapturedImage image)
    {
        var rowBytes = image.Width * 4;
        var dib = new byte[BitmapInfoHeaderSize + rowBytes * image.Height];
        var header = new BITMAPINFOHEADER
        {
            biSize = BitmapInfoHeaderSize,
            biWidth = image.Width,
            biHeight = image.Height, // positive: CF_DIB rows are stored bottom-up
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
            biSizeImage = (uint)(rowBytes * image.Height),
        };
        var headerPtr = Marshal.AllocHGlobal(BitmapInfoHeaderSize);
        try
        {
            Marshal.StructureToPtr(header, headerPtr, false);
            Marshal.Copy(headerPtr, dib, 0, BitmapInfoHeaderSize);
        }
        finally
        {
            Marshal.FreeHGlobal(headerPtr);
        }
        // CapturedImage.Bgra is top-down; flip rows into the bottom-up layout CF_DIB expects.
        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Bgra, y * rowBytes, dib, BitmapInfoHeaderSize + (image.Height - 1 - y) * rowBytes, rowBytes);
        return dib;
    }

    private static void OpenClipboardWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(0)) return;
            if (attempt >= 9) throw new CaptureException(CaptureError.CaptureFailed, "Could not open the clipboard (another app is holding it).");
            Thread.Sleep(50);
        }
    }
}
