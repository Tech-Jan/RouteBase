using RouteBaseWeb.Services.Uploads;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Safe-upload guard for the photo staging/append seam — photo caps (<see cref="PhotoUploadLimits"/>)
/// + the JPEG/PNG/WebP content-sniff allowlist over the shared <see cref="UploadPolicyCore"/> behavior
/// (CR5-02, superseding the Photos-plan "mirrored, not shared" decision). Defense-in-depth: arbitrary
/// bytes never reach MetadataExtractor / the SkiaSharp native decoder.
/// </summary>
public sealed class PhotoUploadPolicy
{
    /// <summary>Bytes the caller must peek from each file for content-sniffing.</summary>
    public const int HeaderBytes = 16;

    private readonly PhotoUploadLimits _limits;

    public PhotoUploadPolicy(PhotoUploadLimits limits)
    {
        _limits = limits;
    }

    /// <param name="candidates">Files in this append request.</param>
    /// <param name="alreadyStagedCount">Files already staged in this batch (for the cumulative count cap).</param>
    /// <param name="alreadyStagedBytes">Bytes already staged in this batch (for the cumulative byte cap).</param>
    public UploadPolicyVerdict Evaluate(
        IReadOnlyList<UploadCandidate> candidates, int alreadyStagedCount, long alreadyStagedBytes) =>
        UploadPolicyCore.Evaluate(
            candidates, alreadyStagedCount, alreadyStagedBytes, _limits,
            static h => PhotoFileSniffer.IsSupportedImage(h),
            $"Each photo must be {_limits.MaxFileBytes / (1024 * 1024)} MB or smaller.",
            "Only JPEG, PNG, and WebP images are accepted.");
}

/// <summary>
/// Pure magic-byte detection for uploaded photos — content-sniffing (not extension trust) so a spoofed
/// <c>evil.exe</c> renamed <c>.jpg</c> is rejected before it reaches any native parser. Stateless +
/// span-based, unit-testable without I/O.
/// </summary>
public static class PhotoFileSniffer
{
    // JPEG: FF D8 FF · PNG: 89 50 4E 47 0D 0A 1A 0A · WebP: "RIFF"????"WEBP"
    private static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsSupportedImage(ReadOnlySpan<byte> h) =>
        (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        || (h.Length >= 8 && h.Slice(0, 8).SequenceEqual(PngSig))
        || (h.Length >= 12 && h.Slice(0, 4).SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8));
}
