using System.Buffers.Binary;
using System.IO.Compression;

namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// Zero-dependency decoder for AWS terrarium DEM tiles: a 256×256 8-bit RGB PNG whose pixels encode
/// elevation as (R*256 + G + B/256) − 32768 metres. Decodes a whole tile once (invariant 7 — never
/// per point) into a row-major float[65536]. The only PNG-decode code in the repo; the IHDR guard
/// fails loudly if AWS ever re-encodes the source (decision-#4 tripwire).
/// </summary>
public static class TerrariumTileDecoder
{
    private const int TileDim = 256;
    private const int Bpp = 3;                            // RGB, 8-bit — 3 bytes/pixel
    private const int Stride = TileDim * Bpp;             // 768 bytes/row
    private const int RawSize = TileDim * (1 + Stride);   // 196 864 = (filter byte + row) × 256

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decode a terrarium tile PNG into 65 536 row-major elevations (metres).</summary>
    public static float[] DecodeElevations(byte[] png)
    {
        VerifySignature(png);
        var (ihdr, idat) = WalkChunks(png);
        GuardIhdr(ihdr);
        var raw = Inflate(idat);
        var pixels = Unfilter(raw);
        return ToElevations(pixels);
    }

    private static void VerifySignature(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG: signature mismatch.");
    }

    // Skip CRC validation — ZLibStream verifies the IDAT adler32 on inflate, so a corrupt tile still
    // fails loudly. Concatenates all IDAT chunks (fixture has 2), stops at IEND, ignores ancillary.
    private static (byte[] Ihdr, byte[] Idat) WalkChunks(byte[] png)
    {
        byte[]? ihdr = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos, 4));
            if (len < 0 || (long)pos + 12 + len > png.Length)
                throw new InvalidDataException("Truncated or corrupt PNG chunk.");

            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int dataStart = pos + 8;
            if (type == "IHDR") ihdr = png[dataStart..(dataStart + len)];
            else if (type == "IDAT") idat.Write(png, dataStart, len);
            else if (type == "IEND") break;

            pos = dataStart + len + 4; // skip data + 4-byte CRC
        }

        if (ihdr is null) throw new InvalidDataException("PNG has no IHDR chunk.");
        if (idat.Length == 0) throw new InvalidDataException("PNG has no IDAT data.");
        return (ihdr, idat.ToArray());
    }

    private static void GuardIhdr(byte[] ihdr)
    {
        if (ihdr.Length != 13) throw new InvalidDataException("Malformed IHDR chunk.");
        int w = BinaryPrimitives.ReadInt32BigEndian(ihdr);
        int h = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4));
        int bit = ihdr[8], ct = ihdr[9], comp = ihdr[10], filt = ihdr[11], i = ihdr[12];
        if (w != 256 || h != 256 || bit != 8 || ct != 2 || comp != 0 || filt != 0 || i != 0)
            throw new InvalidDataException(
                $"Unexpected terrarium tile format: {w}x{h} bit={bit} color={ct} interlace={i}");
    }

    // Bounded inflate: reads exactly RawSize bytes and rejects anything more WITHOUT decompressing it —
    // a crafted tile can't balloon a growing buffer before a post-hoc size check (bomb class). EL-02.
    private static byte[] Inflate(byte[] idat)
    {
        using var input = new MemoryStream(idat);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var raw = new byte[RawSize];
        int filled = zlib.ReadAtLeast(raw, RawSize, throwOnEndOfStream: false);
        if (filled < RawSize)
            throw new InvalidDataException($"Inflated size {filled} != expected {RawSize}.");
        if (zlib.ReadByte() != -1)
            throw new InvalidDataException($"Inflated size exceeds expected {RawSize}.");
        return raw;
    }

    // Reverse the per-scanline PNG filters (RFC 2083) into a flat RGB pixel buffer.
    private static byte[] Unfilter(byte[] raw)
    {
        var pixels = new byte[TileDim * Stride];
        for (int row = 0; row < TileDim; row++)
        {
            int src = row * (1 + Stride);
            int filter = raw[src];
            int dst = row * Stride;
            for (int col = 0; col < Stride; col++)
            {
                int x = raw[src + 1 + col];
                int a = col >= Bpp ? pixels[dst + col - Bpp] : 0;                         // left
                int b = row > 0 ? pixels[dst - Stride + col] : 0;                         // up
                int c = row > 0 && col >= Bpp ? pixels[dst - Stride + col - Bpp] : 0;     // up-left
                int val = filter switch
                {
                    0 => x,
                    1 => x + a,
                    2 => x + b,
                    3 => x + ((a + b) >> 1),           // Average — floor((a+b)/2)
                    4 => x + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unsupported PNG filter type {filter}.")
                };
                pixels[dst + col] = (byte)val;
            }
        }
        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static float[] ToElevations(byte[] pixels)
    {
        var elevations = new float[TileDim * TileDim];
        for (int i = 0; i < elevations.Length; i++)
        {
            int p = i * Bpp;
            elevations[i] = (pixels[p] * 256f + pixels[p + 1] + pixels[p + 2] / 256f) - 32768f;
        }
        return elevations;
    }
}
