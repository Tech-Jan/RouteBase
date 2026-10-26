using RouteBaseWeb.Services.Photos;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>Pins the vendored ThumbHash encoder: compact, deterministic, content-sensitive, size-guarded.</summary>
public class ThumbHashTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            buf[i * 4] = r; buf[i * 4 + 1] = g; buf[i * 4 + 2] = b; buf[i * 4 + 3] = 255;
        }
        return buf;
    }

    [Fact]
    public void Encode_IsCompactAndDeterministic()
    {
        var rgba = Solid(24, 24, 200, 100, 50);
        var h1 = ThumbHash.Encode(24, 24, rgba);
        var h2 = ThumbHash.Encode(24, 24, rgba);

        Assert.Equal(h1, h2);
        Assert.InRange(h1.Length, 5, 30); // ~20-25 B typical
    }

    [Fact]
    public void Encode_DiffersByContent()
    {
        var red  = ThumbHash.Encode(16, 16, Solid(16, 16, 255, 0, 0));
        var blue = ThumbHash.Encode(16, 16, Solid(16, 16, 0, 0, 255));
        Assert.NotEqual(red, blue);
    }

    [Fact]
    public void Encode_RejectsOversizedInput()
    {
        Assert.Throws<ArgumentException>(() => ThumbHash.Encode(200, 200, new byte[200 * 200 * 4]));
    }

    [Fact]
    public void Encode_RejectsTooSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() => ThumbHash.Encode(10, 10, new byte[10]));
    }
}
