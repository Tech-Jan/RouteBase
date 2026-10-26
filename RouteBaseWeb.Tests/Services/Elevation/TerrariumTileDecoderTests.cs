using RouteBaseWeb.Services.Elevation;
using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Elevation;

/// <summary>
/// Pins <see cref="TerrariumTileDecoder"/> against the real Jungfrau fixture tile (z13 x4277 y2896):
/// exact decoded elevations (multi-IDAT concat + Sub/Up/Paeth filters exercised) and the IHDR/length
/// guards that trip loudly if AWS ever re-encodes the source.
/// </summary>
public class TerrariumTileDecoderTests
{
    private static byte[] LoadFixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terrarium_13_4277_2896.png"));

    private static float ElevationAt(float[] tile, int x, int y) => tile[y * 256 + x];

    [Fact]
    public void DecodeElevations_FixtureTile_MatchesExactValues()
    {
        var tile = TerrariumTileDecoder.DecodeElevations(LoadFixture());

        Assert.Equal(65536, tile.Length);
        Assert.Equal(2091.796875f, ElevationAt(tile, 0, 0));
        Assert.Equal(3467.80078125f, ElevationAt(tile, 128, 128));
        Assert.Equal(3004.66015625f, ElevationAt(tile, 255, 255));
        Assert.Equal(3999.3203125f, ElevationAt(tile, 48, 187));
    }

    [Fact]
    public void DecodeElevations_RgbaPng_ThrowsOnColorTypeGuard()
    {
        // PngEncoder writes color type 6 (RGBA); the IHDR guard demands color type 2 (RGB).
        var rgba = new byte[256 * 256 * 4];
        var png = PngEncoder.EncodeRgba(rgba, 256, 256);
        Assert.Throws<InvalidDataException>(() => TerrariumTileDecoder.DecodeElevations(png));
    }

    [Fact]
    public void DecodeElevations_TruncatedBytes_Throws()
    {
        var full = LoadFixture();
        var truncated = full[..(full.Length / 2)];
        Assert.Throws<InvalidDataException>(() => TerrariumTileDecoder.DecodeElevations(truncated));
    }

    [Fact]
    public void DecodeElevations_WrongDimensions_ThrowsOnIhdrGuard()
    {
        // 128×128 synthetic PNG — the IHDR guard rejects the dimensions (and color type).
        var png = PngEncoder.EncodeRgba(new byte[128 * 128 * 4], 128, 128);
        Assert.Throws<InvalidDataException>(() => TerrariumTileDecoder.DecodeElevations(png));
    }

    [Fact]
    public void DecodeElevations_OversizedInflate_Throws()
    {
        // EL-02: a valid-looking tile whose IDAT inflates past the expected raw size must be rejected
        // by the bounded reader (bomb class) — not ballooned into memory before a post-hoc check.
        const int rawSize = 256 * (1 + 256 * 3);
        var png = BuildPng(zlibPayloadLength: rawSize + 4096);
        Assert.Throws<InvalidDataException>(() => TerrariumTileDecoder.DecodeElevations(png));
    }

    /// <summary>Minimal PNG: signature + valid 256×256/8-bit/RGB IHDR + one IDAT of compressed zeros
    /// + IEND. CRCs are left zero — the decoder's chunk walker deliberately skips CRC validation.</summary>
    private static byte[] BuildPng(int zlibPayloadLength)
    {
        byte[] idatData;
        using (var buf = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(
                buf, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(new byte[zlibPayloadLength]);
            }
            idatData = buf.ToArray();
        }

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, 256);              // width
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 256);    // height
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // color type RGB
        // compression/filter/interlace stay 0

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idatData);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]); // CRC — unvalidated by the decoder
    }
}
