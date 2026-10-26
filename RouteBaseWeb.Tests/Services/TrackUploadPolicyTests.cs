using System.Text;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Step 3 safe-upload guard: content-sniff allowlist (spoofed extensions rejected), per-file size cap,
/// cumulative per-batch file-count + total-bytes caps (IE-05), and degenerate/reserved filename reject
/// (IE-04). Pure — no I/O, headers supplied directly.
/// </summary>
public class TrackUploadPolicyTests
{
    private static readonly TrackUploadLimits Limits = new()
    {
        MaxFileBytes = 1_000,
        MaxBatchFiles = 5,
        MaxBatchTotalBytes = 2L * 1024 * 1024 * 1024,
    };
    private static TrackUploadPolicy Policy() => new(Limits);

    private static byte[] Header(string content) => Pad(Encoding.UTF8.GetBytes(content));
    private static byte[] Pad(byte[] bytes) => bytes.Length >= 12 ? bytes : bytes.Concat(new byte[12 - bytes.Length]).ToArray();

    private static UploadCandidate Gpx(string name, long size = 100) =>
        new(name, size, Header("<gpx version=\"1.1\"><trk></trk></gpx>"));

    [Fact]
    public void Evaluate_RealGpx_Allowed()
    {
        var verdict = Policy().Evaluate(new[] { Gpx("ride.gpx") }, alreadyStagedCount: 0, alreadyStagedBytes: 0);
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void Evaluate_SpoofedExtension_Rejected()
    {
        // Binary content (MZ / .exe) with a .gpx name — content sniff overrules the extension.
        var evil = new UploadCandidate("evil.gpx", 100, Pad(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));
        var verdict = Policy().Evaluate(new[] { evil }, 0, 0);

        Assert.False(verdict.Ok);
        Assert.Contains("evil.gpx", verdict.Files!, StringComparer.Ordinal);
        Assert.Contains("GPX, TCX, and FIT", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_OversizeFile_Rejected()
    {
        var big = Gpx("huge.gpx", size: Limits.MaxFileBytes + 1);
        var verdict = Policy().Evaluate(new[] { big }, 0, 0);

        Assert.False(verdict.Ok);
        Assert.Contains("huge.gpx", verdict.Files!, StringComparer.Ordinal);
        Assert.Contains("per-file limit", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CountBombAcrossBatch_Rejected()
    {
        // 3 already staged + 3 more = 6 > cap of 5.
        var incoming = new[] { Gpx("a.gpx"), Gpx("b.gpx"), Gpx("c.gpx") };
        var verdict = Policy().Evaluate(incoming, alreadyStagedCount: 3, alreadyStagedBytes: 0);

        Assert.False(verdict.Ok);
        Assert.Contains("file limit", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CountAtExactCap_Allowed()
    {
        var incoming = new[] { Gpx("a.gpx"), Gpx("b.gpx") };
        var verdict = Policy().Evaluate(incoming, alreadyStagedCount: 3, alreadyStagedBytes: 0); // 3 + 2 = 5 == cap
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void Evaluate_Empty_Rejected()
    {
        var verdict = Policy().Evaluate(Array.Empty<UploadCandidate>(), 0, 0);
        Assert.False(verdict.Ok);
    }

    [Fact]
    public void Evaluate_BatchTotalBytesCap_Rejects()
    {
        var verdict = Policy().Evaluate(
            new[] { Gpx("x.gpx", size: 2) }, alreadyStagedCount: 0,
            alreadyStagedBytes: Limits.MaxBatchTotalBytes - 1);

        Assert.False(verdict.Ok);
        Assert.Contains("GB batch limit", verdict.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BatchTotalBytesAtCap_Allowed()
    {
        var verdict = Policy().Evaluate(
            new[] { Gpx("x.gpx", size: 100) }, alreadyStagedCount: 0,
            alreadyStagedBytes: Limits.MaxBatchTotalBytes - 100); // sum == cap, not over
        Assert.True(verdict.Ok);
    }

    [Fact]
    public void Evaluate_DotDotName_Rejected()
    {
        var verdict = Policy().Evaluate(new[] { Gpx("..") }, 0, 0);
        Assert.False(verdict.Ok);
        Assert.Contains("Invalid file name", verdict.Error, StringComparison.Ordinal);
        Assert.Contains("..", verdict.Files!, StringComparer.Ordinal);
    }

    [Fact]
    public void Evaluate_ManifestJsonNames_Rejected()
    {
        var verdict = Policy().Evaluate(
            new[] { Gpx("manifest.json"), Gpx("manifest.json.tmp") }, 0, 0);
        Assert.False(verdict.Ok);
        Assert.Contains("Invalid file name", verdict.Error, StringComparison.Ordinal);
        Assert.Contains("manifest.json", verdict.Files!, StringComparer.Ordinal);
        Assert.Contains("manifest.json.tmp", verdict.Files!, StringComparer.Ordinal);
    }

    [Fact]
    public void Evaluate_NestedPathName_AcceptedAsLeaf()
    {
        // Path.GetFileName strips "dir/" → leaf "a.gpx" is neither reserved nor degenerate.
        var verdict = Policy().Evaluate(new[] { Gpx("dir/a.gpx") }, 0, 0);
        Assert.True(verdict.Ok);
    }
}
