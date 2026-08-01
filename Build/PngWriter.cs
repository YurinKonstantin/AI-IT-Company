using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Build;

/// <summary>
/// Минимальный PNG-энкодер (RGBA, без внешних зависимостей).
/// </summary>
public static class PngWriter
{
    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA buffer too small.", nameof(rgba));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var fs = File.Create(path);
        // PNG signature
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(fs, "IHDR", ihdr);

        // Raw image with filter byte 0 per scanline
        var raw = new byte[(1 + width * 4) * height];
        for (int y = 0; y < height; y++)
        {
            int dest = y * (1 + width * 4);
            raw[dest] = 0; // None filter
            Buffer.BlockCopy(rgba, y * width * 4, raw, dest + 1, width * 4);
        }

        var compressed = ZlibCompress(raw);
        WriteChunk(fs, "IDAT", compressed);
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        // zlib header (default compression)
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);

        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        // Adler-32 of uncompressed data
        uint adler = Adler32(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, adler);
        ms.Write(checksum);

        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);

        s.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (var c in data)
        {
            a = (a + c) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var x in type) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
