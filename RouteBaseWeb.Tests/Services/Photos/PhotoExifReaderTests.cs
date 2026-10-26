using RouteBaseWeb.Services.Photos;
using SkiaSharp;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// Verifies PhotoExifReader against the three real §11 sample classes (Pixel, WhatsApp, Signal), located via
/// <see cref="PhotoFixtures"/>. The sample binaries are opt-in (not committed — see PhotoFixtures), so each
/// real-sample test skips when the directory is absent and runs for real once fixtures are provided. The
/// no-EXIF negative path is pinned unconditionally with a synthetic JPEG, and an anti-vacuity guard makes a
/// misconfigured fixtures directory fail loudly instead of silently no-op'ing every sample test.
/// </summary>
public class PhotoExifReaderTests
{
    private static byte[] SyntheticJpegNoExif()
    {
        using var bmp = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.SlateGray);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static PhotoExif? ReadIfPresent(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return new PhotoExifReader().Read(fs);
    }

    /// <summary>Always runs — exercises Read() end-to-end on a real (synthetic, EXIF-free) JPEG so the reader
    /// never sits entirely untested when the sample binaries are absent.</summary>
    [Fact]
    public void NoExifJpeg_ReadsAsAllNull_NeverThrows()
    {
        using var ms = new MemoryStream(SyntheticJpegNoExif());
        var exif = new PhotoExifReader().Read(ms);

        Assert.False(exif.HasTime);
        Assert.False(exif.HasGps);
        Assert.Null(exif.DateTimeOriginalLocal);
        Assert.Null(exif.UtcOffset);
        Assert.Null(exif.CameraModel);
    }

    /// <summary>Anti-vacuity guard: a configured fixtures directory MUST contain every named sample, so a
    /// stale/renamed path fails here instead of silently turning every guarded test into a no-op (the exact
    /// regression this replaced). Skips only when no fixtures are opted in at all.</summary>
    [Fact]
    public void FixturesDir_WhenPresent_ContainsEveryNamedSample()
    {
        if (!PhotoFixtures.Available) return; // fixtures not opted in — guarded tests skip by design
        foreach (var f in PhotoFixtures.AllFiles)
            Assert.True(File.Exists(PhotoFixtures.PathOf(f)),
                $"Fixtures dir '{PhotoFixtures.Dir}' is present but missing sample '{f}'.");
    }

    [Fact]
    public void Pixel_HasTimeGpsAndOffset()
    {
        var exif = ReadIfPresent(PhotoFixtures.PathOf(PhotoFixtures.PixelFile));
        if (exif is null) return; // sample absent — skip
        Assert.True(exif.HasTime);
        Assert.True(exif.HasGps);
        Assert.NotNull(exif.UtcOffset);
        Assert.NotNull(exif.CameraModel);
        // Sub-second precision is folded into the capture instant so burst frames don't dedup-collapse.
        Assert.NotEqual(0, exif.DateTimeOriginalLocal!.Value.Millisecond);
    }

    [Fact]
    public void WhatsApp_HasTime_ButNoGps()
    {
        var exif = ReadIfPresent(PhotoFixtures.PathOf(PhotoFixtures.WhatsAppFile));
        if (exif is null) return;
        Assert.True(exif.HasTime);
        Assert.False(exif.HasGps);
    }

    [Fact]
    public void Signal_IsFullyStripped()
    {
        var exif = ReadIfPresent(PhotoFixtures.PathOf(PhotoFixtures.SignalFile));
        if (exif is null) return;
        Assert.False(exif.HasTime);
        Assert.False(exif.HasGps);
    }

    // SVC-02: the `hh\:mm` format has no sign token, so a '-' left on the string failed the parse and every
    // negative EXIF offset (all of the Americas) returned null. These pin both signs and the magnitude.
    [Theory]
    [InlineData("-05:00", -5, 0)]
    [InlineData("-05:30", -5, -30)]
    [InlineData("+02:00", 2, 0)]
    [InlineData("+09:30", 9, 30)]
    public void ParseOffset_SignedOffset_ParsesMagnitudeAndSign(string raw, int expectHours, int expectMinutes)
    {
        var ts = PhotoExifReader.ParseOffset(raw);
        Assert.NotNull(ts);
        Assert.Equal(TimeSpan.FromHours(expectHours) + TimeSpan.FromMinutes(expectMinutes), ts!.Value);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("z")]
    public void ParseOffset_Zulu_IsZero(string raw) => Assert.Equal(TimeSpan.Zero, PhotoExifReader.ParseOffset(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseOffset_AbsentOrMalformed_IsNull(string? raw) => Assert.Null(PhotoExifReader.ParseOffset(raw));
}
