using RouteBaseWeb.Services.Uploads;

namespace RouteBaseWeb.Services;

/// <summary>Tunable caps for the activity-file upload guard (bind from config section "TrackUpload").</summary>
public sealed record TrackUploadLimits : IUploadLimits
{
    /// <summary>Reject any single file larger than this. Default 50 MB.</summary>
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Reject a batch whose cumulative staged file count would exceed this. Default 2000.</summary>
    public int MaxBatchFiles { get; init; } = 2000;

    /// <summary>Reject a batch whose cumulative staged bytes would exceed this. Default 5 GB (tracks allow
    /// 2000 files and full multi-year GPX archives are legitimately larger than photo batches).</summary>
    public long MaxBatchTotalBytes { get; init; } = 5L * 1024 * 1024 * 1024;
}

/// <summary>One file offered to the guard: its name, byte length, and sniffed header.</summary>
public sealed record UploadCandidate(string FileName, long SizeBytes, byte[] Header);

/// <summary>Guard outcome — allowed, or rejected with a user-facing reason and the offending files.</summary>
public sealed record UploadPolicyVerdict(bool Ok, string? Error = null, IReadOnlyList<string>? Files = null)
{
    public static readonly UploadPolicyVerdict Allowed = new(true);
    public static UploadPolicyVerdict Reject(string error, IEnumerable<string>? files = null) =>
        new(false, error, files?.ToList());
}

/// <summary>
/// Safe-upload guard for the activity-file staging/append seam — track caps + the GPX/TCX/FIT
/// content-sniff allowlist over the shared <see cref="UploadPolicyCore"/> behavior (CR5-02: order of
/// checks, cumulative caps, reserved-name reject). We parse, never execute, track files.
/// </summary>
public sealed class TrackUploadPolicy
{
    /// <summary>
    /// Bytes the caller must peek from each file for content-sniffing. Must stay in sync with
    /// <see cref="TrackFileParserRouter"/>'s own sniff window — this gate runs upstream of the
    /// router at upload time, over the same <see cref="TrackFileSniffer.Detect"/> call.
    /// </summary>
    public const int HeaderBytes = 4096;

    private readonly TrackUploadLimits _limits;

    public TrackUploadPolicy(TrackUploadLimits limits)
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
            static h => TrackFileSniffer.Detect(h) != TrackFileFormat.Unknown,
            $"Some files exceed the {_limits.MaxFileBytes / (1024 * 1024)} MB per-file limit.",
            "Only GPX, TCX, and FIT activity files are accepted.");
}
