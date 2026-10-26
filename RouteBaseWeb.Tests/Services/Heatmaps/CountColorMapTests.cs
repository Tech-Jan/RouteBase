using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class CountColorMapTests
{
    [Fact]
    public void Zero_IsFullyTransparent()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), CountColorMap.Lookup(0));
    }

    [Fact]
    public void One_IsOpaqueWhiteHot()
    {
        // Last node: (1.00, 1.00, 0.95, 1.00) → (255, 255, 242, 255)
        Assert.Equal(((byte)255, (byte)255, (byte)242, (byte)255), CountColorMap.Lookup(1));
    }

    [Fact]
    public void OutOfRange_IsClamped()
    {
        Assert.Equal(CountColorMap.Lookup(0), CountColorMap.Lookup(-0.5));
        Assert.Equal(CountColorMap.Lookup(1), CountColorMap.Lookup(1.5));
    }

    [Fact]
    public void Midpoint_IsOrange()
    {
        // Node at 0.50: (1.00, 0.45, 0.10, 0.95) → R=255, G≈115, B≈26, A≈242
        var (r, g, b, a) = CountColorMap.Lookup(0.5);
        Assert.Equal(255, r);
        Assert.InRange(g, 110, 120);
        Assert.InRange(b, 22, 30);
        Assert.InRange(a, 238, 246);
    }

    [Fact]
    public void Alpha_IsMonotonicallyNonDecreasing()
    {
        byte prev = 0;
        for (int i = 0; i <= 255; i++)
        {
            var (_, _, _, a) = CountColorMap.Lookup(i / 255.0);
            Assert.True(a >= prev, $"alpha decreased at i={i}: {a} < {prev}");
            prev = a;
        }
    }
}
