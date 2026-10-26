using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>Recording <see cref="ISegmentAnalysis"/> double for the derive-chain tests: captures the ordered
/// remove/match calls (+ userId + threshold) and can throw for a designated track or on the threshold read.</summary>
public sealed class FakeSegmentAnalysis : ISegmentAnalysis
{
    public readonly List<string> Calls = new();
    public int Threshold = 40;
    public TrackId? ThrowRemoveForTrack;
    public bool ThrowOnThreshold;

    public Task<int> GetEffectiveMatchThresholdAsync(string userId, int? explicitPercent)
    {
        if (ThrowOnThreshold) throw new InvalidOperationException("threshold boom");
        return Task.FromResult(Threshold);
    }

    public Task<IReadOnlyList<SegmentId>> RemoveTrackFromSegmentsAsync(
        TrackId trackId, string? userId = null, CancellationToken ct = default)
    {
        if (ThrowRemoveForTrack == trackId) throw new InvalidOperationException($"missing track {trackId}");
        Calls.Add($"remove:{trackId}:{userId}");
        return Task.FromResult<IReadOnlyList<SegmentId>>(Array.Empty<SegmentId>());
    }

    public Task MatchNewTrackAsync(TrackId trackId, string? userId = null,
        int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default)
    {
        Calls.Add($"match:{trackId}:{userId}:{matchThresholdPercent}");
        return Task.CompletedTask;
    }

    public Task<bool> CanAnalyzeSegmentAsync(SegmentId segmentId) => throw new NotImplementedException();
    public Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? userId = null,
        int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task MatchNewTracksAsync(IReadOnlyList<int> trackIds, string? userId = null,
        int matchThresholdPercent = SegmentAnalysis.DefaultMatchThresholdPercent, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task ClearAllSegmentMatchesAsync(string? userId = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task ClearOrphanedSegmentMatchesAsync(
        string userId, IReadOnlyList<SegmentId> processedSegmentIds, CancellationToken ct = default) =>
        throw new NotImplementedException();
}

/// <summary>Recording <see cref="IPhotoCommandService"/> double: captures which tracks got a photo re-match.</summary>
public sealed class FakePhotoCommand : IPhotoCommandService
{
    public readonly List<int> Rematched = new();

    public Task<int> RematchTrackPhotosAsync(TrackId trackId, CancellationToken ct = default)
    {
        Rematched.Add(trackId.Value);
        return Task.FromResult(1);
    }

    public Task<PhotoCommandOutcome> PlaceAsync(PhotoId photoId, double lat, double lng, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<PhotoCommandOutcome> UnplaceAsync(PhotoId photoId, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<RestoreResult> RestoreAsync(PhotoId photoId, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<PhotoCommandOutcome> DeleteAsync(PhotoId photoId, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<int> DeleteManyAsync(IReadOnlyCollection<int> photoIds, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
