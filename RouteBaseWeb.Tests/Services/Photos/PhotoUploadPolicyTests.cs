using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// PI-01/03/09 safe-upload guard: content-sniff allowlist (spoofed extensions rejected), per-batch
/// file-count + total-bytes caps, and degenerate/reserved filename reject. Pure — no I/O, headers supplied.
/// </summary>
public class PhotoUploadPolicyTests
{
    private static readonly PhotoUploadLimits Limits = new()
    {
        MaxFileBytes = 1_000,
        MaxBatchFiles = 500,
        MaxBatchTotalBytes = 2L * 1024 * 1024 * 1024,
    };
    private static PhotoUploadPolicy Policy() => new(Limits);

    private static byte[] JpegHeader() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static byte[] PngHeader() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static byte[] WebpHeader() => new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0, 0, 0 };

    private static UploadCandidate Jpeg(string name, long size = 100) => new(name, size, JpegHeader());

    [Fact]
    public void Evaluate_SpoofedExtension_Rejected()
    {
        // "MZ…" (Windows exe) named evil.jpg — content sniff overrules the extension.
        var evil = new UploadCandidate("evil.jpg", 100, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var verdict = Policy().Evaluate(new[] { evil }, alreadyStagedCount: 0, alreadyStagedBytes: 0);

        Assert.False(verdict.Ok);
        Assert.Contains("evil.jpg", verdict.Files!, StringComparer.Ordinal);
        Assert.Contains("JPEG, PNG, and WebP", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RealJpegPngWebpHeaders_Allowed()
    {
        var candidates = new[]
        {
            new UploadCandidate("a.jpg", 100, JpegHeader()),
            new UploadCandidate("b.png", 100, PngHeader()),
            new UploadCandidate("c.webp", 100, WebpHeader()),
        };
        var verdict = Policy().Evaluate(candidates, 0, 0);
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void Evaluate_BatchFileCountCap_Rejects()
    {
        var verdict = Policy().Evaluate(new[] { Jpeg("x.jpg") }, alreadyStagedCount: 500, alreadyStagedBytes: 0);

        Assert.False(verdict.Ok);
        Assert.Contains("file limit", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BatchTotalBytesCap_Rejects()
    {
        var verdict = Policy().Evaluate(
            new[] { Jpeg("x.jpg", size: 2) }, alreadyStagedCount: 0,
            alreadyStagedBytes: Limits.MaxBatchTotalBytes - 1);

        Assert.False(verdict.Ok);
        Assert.Contains("GB batch limit", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_OversizeFile_Rejected()
    {
        var verdict = Policy().Evaluate(new[] { Jpeg("huge.jpg", size: Limits.MaxFileBytes + 1) }, 0, 0);

        Assert.False(verdict.Ok);
        Assert.Contains("huge.jpg", verdict.Files!, StringComparer.Ordinal);
        Assert.Contains("or smaller", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_DotDotName_Rejected()
    {
        var verdict = Policy().Evaluate(new[] { new UploadCandidate("..", 100, JpegHeader()) }, 0, 0);
        Assert.False(verdict.Ok);
        Assert.Contains("Invalid file name", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ManifestName_Rejected()
    {
        var verdict = Policy().Evaluate(new[] { new UploadCandidate("manifest.json", 100, JpegHeader()) }, 0, 0);
        Assert.False(verdict.Ok);
        Assert.Contains("Invalid file name", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_Empty_Rejected()
    {
        var verdict = Policy().Evaluate(Array.Empty<UploadCandidate>(), 0, 0);
        Assert.False(verdict.Ok);
    }
}
