namespace RouteBaseWeb.Services.Uploads;

/// <summary>The three batch-upload caps every vertical's limits record exposes (CR5-02) — lets
/// <see cref="UploadPolicyCore"/> enforce them without knowing the feature's config-bound type.</summary>
public interface IUploadLimits
{
    long MaxFileBytes { get; }
    int MaxBatchFiles { get; }
    long MaxBatchTotalBytes { get; }
}

/// <summary>
/// Shared safe-upload guard behavior (CR5-02) — the Evaluate body that was twin-maintained in
/// TrackUploadPolicy ↔ PhotoUploadPolicy, identical except the sniffer predicate and two message
/// strings (supplied by the feature). Purpose = DoS/exhaustion resistance + early-reject UX +
/// defense-in-depth (NOT anti-malware). Pure and stateless: the caller supplies sniffed headers +
/// sizes, so it is unit-testable without I/O. Enforces, in order: batch file-count cap, batch
/// total-bytes cap, per-file size cap, degenerate/reserved filename reject (IE-04/IE-02 ↔
/// PI-09/PI-08 guard — also blocks <c>manifest.json</c>/<c>manifest.json.tmp</c> so a staged file
/// can't clobber the atomic manifest write), then the feature's content-sniff allowlist (a spoofed
/// <c>evil.exe</c> renamed to an accepted extension fails there).
/// </summary>
public static class UploadPolicyCore
{
    /// <param name="candidates">Files in this append request.</param>
    /// <param name="alreadyStagedCount">Files already staged in this batch (cumulative count cap).</param>
    /// <param name="alreadyStagedBytes">Bytes already staged in this batch (cumulative byte cap).</param>
    /// <param name="limits">The feature's caps.</param>
    /// <param name="isRecognized">Content-sniff allowlist predicate over the peeked header.</param>
    /// <param name="oversizeError">Feature wording for the per-file size cap reject.</param>
    /// <param name="unrecognizedError">Feature wording for the sniff-allowlist reject.</param>
    public static UploadPolicyVerdict Evaluate(
        IReadOnlyList<UploadCandidate> candidates, int alreadyStagedCount, long alreadyStagedBytes,
        IUploadLimits limits, Func<byte[], bool> isRecognized, string oversizeError, string unrecognizedError)
    {
        if (candidates.Count == 0)
            return UploadPolicyVerdict.Reject("No files provided.");

        if (alreadyStagedCount + candidates.Count > limits.MaxBatchFiles)
            return UploadPolicyVerdict.Reject(
                $"Too many files — this import would exceed the {limits.MaxBatchFiles}-file limit.");

        if (alreadyStagedBytes + candidates.Sum(c => c.SizeBytes) > limits.MaxBatchTotalBytes)
            return UploadPolicyVerdict.Reject(
                $"This import would exceed the {limits.MaxBatchTotalBytes / (1024 * 1024 * 1024)} GB batch limit.");

        var oversize = candidates.Where(c => c.SizeBytes > limits.MaxFileBytes)
                                 .Select(c => c.FileName).ToList();
        if (oversize.Count > 0)
            return UploadPolicyVerdict.Reject(oversizeError, oversize);

        var badName = candidates
            .Where(c =>
            {
                var n = Path.GetFileName(c.FileName);
                return string.IsNullOrEmpty(n) || n is "." or ".." or "manifest.json" or "manifest.json.tmp";
            })
            .Select(c => c.FileName).ToList();
        if (badName.Count > 0)
            return UploadPolicyVerdict.Reject("Invalid file name.", badName); // IE-04/IE-02 ↔ PI-09/PI-08 guard

        var unrecognized = candidates.Where(c => !isRecognized(c.Header))
                                     .Select(c => c.FileName).ToList();
        if (unrecognized.Count > 0)
            return UploadPolicyVerdict.Reject(unrecognizedError, unrecognized);

        return UploadPolicyVerdict.Allowed;
    }
}
