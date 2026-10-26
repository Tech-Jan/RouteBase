using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// The post-save/revert derive chain (CommitPath §5.3 as amended by §14.4): re-match segments and re-match
/// photos for the tracks a commit touched. Stats/bbox/start-time are recomputed synchronously inside the
/// save/revert transaction (§14.4), so this queue-serialized pass owns only the slow cross-context work.
/// Scoped; the handler creates the impersonating scope, so every seam here is user-scoped. Per-id failures
/// are isolated (one bad track can't strand the rest); a discarded derive just leaves segments/photos stale
/// until the next save/revert (re-runnable, G29).
/// </summary>
public sealed class TrackEditDeriveService
{
    private readonly ISegmentAnalysis _segments;
    private readonly IPhotoCommandService _photos;
    private readonly UserCacheInvalidator _invalidator;
    private readonly ILogger<TrackEditDeriveService> _logger;

    public TrackEditDeriveService(
        ISegmentAnalysis segments, IPhotoCommandService photos,
        UserCacheInvalidator invalidator, ILogger<TrackEditDeriveService> logger)
    {
        _segments = segments;
        _photos = photos;
        _invalidator = invalidator;
        _logger = logger;
    }

    public async Task RunAsync(TrackEditDerivePayload payload, CancellationToken ct)
    {
        string userId = payload.UserId;
        int threshold = await _segments.GetEffectiveMatchThresholdAsync(userId, null); // parity with analyze-all

        // Trashed sources: stop painting heatmaps/efforts.
        foreach (int id in payload.RemoveOnlyIds)
            await SafeAsync(new TrackId(id), "remove", () => _segments.RemoveTrackFromSegmentsAsync(new TrackId(id), userId, ct));

        // Geometry changed: remove stale matches (a crop can shrink the bbox out of a previously-matched
        // segment incremental re-match would never revisit), re-match, re-match photos.
        foreach (int id in payload.FullDeriveIds)
            await SafeAsync(new TrackId(id), "full-derive", async () =>
            {
                await _segments.RemoveTrackFromSegmentsAsync(new TrackId(id), userId, ct);
                await _segments.MatchNewTrackAsync(new TrackId(id), userId, threshold, ct);
                await _photos.RematchTrackPhotosAsync(new TrackId(id), ct);
            });

        // Created / un-trashed tracks kept a correct bbox+stats → re-match only (Seam-2 holds).
        foreach (int id in payload.MatchOnlyIds)
            await SafeAsync(new TrackId(id), "match", () => _segments.MatchNewTrackAsync(new TrackId(id), userId, threshold, ct));

        _invalidator.InvalidateUser(userId);
    }

    // Isolate per-track failures (a bad blob/decode on one track must not strand its siblings); cancellation
    // still propagates so a user cancel aborts the whole op.
    private async Task SafeAsync(TrackId trackId, string phase, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Track-edit derive ({Phase}) failed for track {TrackId}", phase, trackId);
        }
    }
}
