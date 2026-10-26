using System.Buffers.Binary;
using System.IO.Compression;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Minimal PNG writer: RGBA8, non-interlaced, scanline filter 0, zlib-deflated
/// IDAT. Enough for heatmap tiles without pulling in an image library.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The 8-byte PNG file signature — shared so tile reads can sanity-check stored bytes (HM-12).</summary>
    internal static ReadOnlySpan<byte> SignatureSpan => Signature;

    /// <summary>Encode a row-major RGBA byte buffer (4 bytes per pixel) as PNG.</summary>
    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("Buffer length must be width * height * 4.", nameof(rgba));

        using var ms = new MemoryStream();
        ms.Write(Signature);

        // IHDR: width, height, bit depth 8, color type 6 (RGBA), compression 0, filter 0, interlace 0
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(ms, "IHDR", ihdr);

        WriteChunk(ms, "IDAT", DeflateScanlines(rgba, width, height));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] DeflateScanlines(byte[] rgba, int width, int height)
    {
        // Assemble all scanlines (1 filter byte + row each) up front and deflate in ONE Write: the base
        // Stream.WriteByte allocates a byte[1] per call, so the per-row WriteByte(0) form cost height
        // allocations + 2×height deflate entry calls per tile. Identical input byte sequence, no
        // intermediate flushes → byte-identical PNG output (pinned by PngEncoderTests).
        int stride = width * 4;
        var scanlines = new byte[(1 + stride) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (1 + stride);
            scanlines[o] = 0; // filter type 0 (None)
            Buffer.BlockCopy(rgba, y * stride, scanlines, o + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(scanlines, 0, scanlines.Length);
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        stream.Write(len);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++)
            typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32.Compute(typeBytes);
        crc = Crc32.Update(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}

/// <summary>CRC-32 (ISO 3309 / PNG variant), table-driven.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc ^ 0xFFFFFFFFu;
        foreach (byte b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
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
