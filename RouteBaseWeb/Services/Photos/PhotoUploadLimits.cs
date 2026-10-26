using RouteBaseWeb.Constants;
using RouteBaseWeb.Services.Uploads;

namespace RouteBaseWeb.Services.Photos;

/// <summary>Tunable caps for the photo upload guard (bind from config section "PhotoUpload").</summary>
public sealed record PhotoUploadLimits : IUploadLimits
{
    /// <summary>Reject any single photo larger than this. Default 50 MB.</summary>
    public long MaxFileBytes { get; init; } = ProfileSettingsDefaults.PhotoMaxFileBytes;

    /// <summary>Reject a batch whose cumulative staged file count would exceed this. Default 500.</summary>
    public int MaxBatchFiles { get; init; } = 500;

    /// <summary>Reject a batch whose cumulative staged bytes would exceed this. Default 2 GB.</summary>
    public long MaxBatchTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}
