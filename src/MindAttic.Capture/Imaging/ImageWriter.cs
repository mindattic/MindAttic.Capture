using System.IO.Compression;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Imaging;

/// <summary>Lossless still writers with no dependencies (works under Unity too). JPG/WebP go through ffmpeg.</summary>
internal static class ImageWriter
{
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpg => ".jpg",
        ImageFormat.WebP => ".webp",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.RawBgra => ".raw",
        _ => throw new CaptureException(CaptureError.InvalidArgument, $"Unknown image format {format}."),
    };

    public static void WritePng(CapturedImage image, Stream output)
    {
        var channels = image.HasAlpha ? 4 : 3;
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, 8);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)image.Width);
        WriteBigEndian(ihdr, 4, (uint)image.Height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(image.HasAlpha ? 6 : 2);
        WriteChunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            idat.WriteByte(0x78);
            idat.WriteByte(0xDA);
            uint a = 1, b = 0;
            var rowLength = image.Width * channels;
            var row = new byte[1 + rowLength];
            var prev = new byte[rowLength];
            var cur = new byte[rowLength];
            using (var deflate = new DeflateStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var src = image.Bgra;
                for (var y = 0; y < image.Height; y++)
                {
                    var s = y * image.Width * 4;
                    for (int x = 0, d = 0; x < image.Width; x++, s += 4, d += channels)
                    {
                        cur[d] = src[s + 2];
                        cur[d + 1] = src[s + 1];
                        cur[d + 2] = src[s];
                        if (channels == 4) cur[d + 3] = src[s + 3];
                    }
                    // "Up" filter compresses screen content well and is cheap.
                    row[0] = 2;
                    for (var i = 0; i < rowLength; i++) row[i + 1] = (byte)(cur[i] - prev[i]);
                    deflate.Write(row, 0, row.Length);
                    for (var i = 0; i < row.Length; i++)
                    {
                        a = (a + row[i]) % 65521;
                        b = (b + a) % 65521;
                    }
                    (prev, cur) = (cur, prev);
                }
            }
            var adler = (b << 16) | a;
            var tail = new byte[4];
            WriteBigEndian(tail, 0, adler);
            idat.Write(tail, 0, 4);
            WriteChunk(output, "IDAT", idat.ToArray());
        }
        WriteChunk(output, "IEND", []);
    }

    public static void WriteBmp(CapturedImage image, Stream output)
    {
        var pixelBytes = image.Width * image.Height * 4;
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        WriteLittleEndian(header, 2, (uint)(54 + pixelBytes));
        WriteLittleEndian(header, 10, 54);
        WriteLittleEndian(header, 14, 40);
        WriteLittleEndian(header, 18, (uint)image.Width);
        WriteLittleEndian(header, 22, unchecked((uint)-image.Height));
        header[26] = 1;
        header[28] = 32;
        WriteLittleEndian(header, 34, (uint)pixelBytes);
        output.Write(header, 0, header.Length);
        output.Write(image.Bgra, 0, pixelBytes);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        output.Write(len, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, 4);
        output.Write(data, 0, data.Length);
        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        output.Write(crcBytes, 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteLittleEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
