using RouteBaseWeb.Services.Photos;
using SkiaSharp;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>Pins the SkiaSharp derivative pipeline with a synthetic JPEG (no fixture): valid thumb+preview
/// JPEGs, max-edge respected, no upscaling, ThumbHash produced, undecodable input rejected.</summary>
public class PhotoDerivativeServiceTests
{
    private static byte[] SyntheticJpeg(int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, h),
                new[] { SKColors.Red, SKColors.Yellow, SKColors.Blue }, null, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] SyntheticPng(int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool IsJpeg(byte[] b) => b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;

    [Fact]
    public void Create_RespectsPreviewMaxEdge_AndEmitsValidJpegs()
    {
        var svc = new PhotoDerivativeService();
        var d = svc.Create(SyntheticJpeg(1200, 800), previewMaxEdge: 1024);

        Assert.Equal(1024, Math.Max(d.Width, d.Height));
        Assert.Equal(683, Math.Min(d.Width, d.Height)); // 800 * 1024/1200
        Assert.True(IsJpeg(d.Preview));
        Assert.True(IsJpeg(d.Thumb));
        Assert.True(d.Thumb.Length < d.Preview.Length);
        Assert.InRange(d.ThumbHash.Length, 5, 30);
    }

    [Theory]
    [InlineData(640)]
    [InlineData(1600)]
    public void Create_HonorsSelectableSizes(int maxEdge)
    {
        var svc = new PhotoDerivativeService();
        var d = svc.Create(SyntheticJpeg(3000, 2000), maxEdge);
        Assert.Equal(maxEdge, Math.Max(d.Width, d.Height));
    }

    [Fact]
    public void Create_DoesNotUpscaleSmallImages()
    {
        var svc = new PhotoDerivativeService();
        var d = svc.Create(SyntheticJpeg(200, 150), previewMaxEdge: 1024);
        Assert.Equal(200, d.Width);
        Assert.Equal(150, d.Height);
    }

    [Fact]
    public void Create_UndecodableBytes_Throws()
    {
        var svc = new PhotoDerivativeService();
        Assert.Throws<InvalidDataException>(() => svc.Create(new byte[] { 1, 2, 3, 4 }, 1024));
    }

    [Fact]
    public void Create_DecodePixelsOverLimit_ThrowsInvalidData()
    {
        // PI-02: 100×100 PNG (Skia PNG can't native-scale) → 10 000 px > cap of 100 → rejected pre-decode.
        var svc = new PhotoDerivativeService();
        var ex = Assert.Throws<InvalidDataException>(
            () => svc.Create(SyntheticPng(100, 100), previewMaxEdge: 1024, maxDecodePixels: 100));
        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_DecodePixelsUnderLimit_Succeeds()
    {
        var svc = new PhotoDerivativeService();
        var d = svc.Create(SyntheticPng(100, 100), previewMaxEdge: 1024, maxDecodePixels: 100_000);
        Assert.Equal(100, Math.Max(d.Width, d.Height));
        Assert.True(IsJpeg(d.Preview));
    }
}
