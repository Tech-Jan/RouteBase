using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class PngEncoderTests
{
    [Fact]
    public void Crc32_MatchesKnownTestVector()
    {
        // Standard CRC-32 check value: crc32("123456789") == 0xCBF43926
        uint crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Encode_StartsWithPngSignatureAndValidIhdr()
    {
        var png = PngEncoder.EncodeRgba(new byte[16 * 8 * 4], 16, 8);

        byte[] expectedSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(expectedSig, png[..8]);

        // First chunk: length 13, type IHDR, width 16, height 8, depth 8, color type 6
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8)));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);
    }

    [Fact]
    public void Encode_RoundTripsPixelsExactly()
    {
        const int w = 64, h = 32;
        var rgba = new byte[w * h * 4];
        new Random(7).NextBytes(rgba);

        var png = PngEncoder.EncodeRgba(rgba, w, h);
        var decoded = DecodeFilter0Png(png, w, h, out bool crcsValid);

        Assert.True(crcsValid, "chunk CRC mismatch");
        Assert.Equal(rgba, decoded);
    }

    [Fact]
    public void Encode_WrongBufferLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeRgba(new byte[10], 16, 16));
    }

    /// <summary>Minimal decoder for our own output: filter-0 scanlines, single zlib stream.</summary>
    private static byte[] DecodeFilter0Png(byte[] png, int width, int height, out bool crcsValid)
    {
        crcsValid = true;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            uint actualCrc = Crc32.Compute(png.AsSpan(pos + 4, 4 + len));
            if (storedCrc != actualCrc)
                crcsValid = false;
            if (type == "IDAT")
                idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scanlines = raw.ToArray();

        int stride = width * 4;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (stride + 1)]); // filter type 0
            Array.Copy(scanlines, y * (stride + 1) + 1, pixels, y * stride, stride);
        }
        return pixels;
    }
}
