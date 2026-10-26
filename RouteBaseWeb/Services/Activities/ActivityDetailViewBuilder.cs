using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Activities;

/// <summary>
/// How to build an <see cref="ActivityDetailView"/> for one caller.
/// </summary>
/// <param name="IncludeOwnerData">
/// Load the edit-session resume hint and version-history facts. Off for a share render — that data is
/// owner-only and its absence is what keeps the editor chrome from rendering.
/// </param>
/// <param name="IncludeMapTilerKey">
/// Off for a share render: the key is serialized into the page, so a share URL would publish it to anyone
/// holding the link. The basemap registry already falls back to keyless styles.
/// </param>
/// <param name="SettingsUserId">
/// Whose split distances to apply, and who to compare against for <c>IsOwner</c>. <c>null</c> means
/// "nobody" — defaults, and IsOwner false. A share render passes null so the page is byte-identical
/// whether the visitor is a stranger or the owner previewing their own link.
///
/// DISPLAY preferences are no longer read here: the layouts deliver them once per request as the
/// #appSettings island (<see cref="Models.Settings.SettingsPayload"/>). Splits stay because they are
/// consumed server-side, before the wire.
/// </param>
/// <param name="Surfaces">Given IsOwner, which affordances the render offers.</param>
/// <param name="PhotosApiBase">
/// Where this render's photo READ endpoints live. Defaults to the authenticated path. A share render
/// passes <c>/s/{token}/api/photos</c>, because an <c>&lt;img&gt;</c> request cannot carry a custom header
/// and the slice deliberately issues no share cookie — so the credential has to be in the URL.
/// </param>
/// <param name="AllowOwnerNonReleased">
/// Serve the caller's OWN activity even when its lifecycle is not <c>released</c> — an awaiting-review or
/// trashed ride, which <c>VisibleActivities</c> hides from every other read. On only for
/// <c>/Activities/Details/{id}</c>, so the owner can look at a pending recording before approving it; the
/// render then drops to <see cref="ActivityViewSurfaces.ForOwnerReview"/>.
///
/// Never set this on a share path: the owner-scoped queries behind it read the AMBIENT identity, so an owner
/// opening their own link would stop seeing what a stranger sees.
/// </param>
public sealed record ActivityViewOptions(
    bool IncludeOwnerData,
    bool IncludeMapTilerKey,
    string? SettingsUserId,
    Func<bool, ActivityViewSurfaces> Surfaces,
    string PhotosApiBase = "/api/photos",
    bool AllowOwnerNonReleased = false);

public interface IActivityDetailViewBuilder
{
    /// <summary>Builds the view, or null when the activity is not visible to the caller (→ 404).</summary>
    Task<ActivityDetailView?> BuildAsync(int id, ActivityViewOptions options);
}

/// <summary>
/// Builds the activity detail view-model. Extracted verbatim from <c>DetailsModel.OnGetAsync</c> so both
/// the canonical page and the share page produce identical data from one code path — the alternative was
/// a second copy of this loading sequence that would drift.
///
/// The only behavioural change from the original: the three <c>ICurrentUserService.GetUserId()</c> calls
/// became <see cref="ActivityViewOptions.SettingsUserId"/>. That is what lets a share render, which has no
/// user identity, run the same sequence without throwing.
/// </summary>
public sealed class ActivityDetailViewBuilder(
    IActivityQueryService activityQuery,
    IActivityGpsService activityGps,
    IMapTilerService mapTilerService,
    IProfileSettingsService settingsService,
    EditSessionService editSessions,
    ITrackVersionHistory versionHistory,
    ILogger<ActivityDetailViewBuilder> logger) : IActivityDetailViewBuilder
{
    public async Task<ActivityDetailView?> BuildAsync(int id, ActivityViewOptions options)
    {
        // Sequential on purpose: these services share the request-scoped DbContext (EF Core forbids
        // concurrent ops). Load the activity first so a 404 skips the settings work.
        var activity = await activityQuery.GetActivityMetadataByIdAsync(id);

        // Second look, owner-scoped: the visible read above refuses any non-released ride. Only the details
        // route asks for this, and only the owner's own rows can come back — a stranger still gets null.
        var isReview = false;
        if (activity is null && options.AllowOwnerNonReleased)
        {
            activity = await activityQuery.GetOwnedActivityMetadataByIdAsync(id);
            isReview = activity is not null;
        }

        if (activity is null)
        {
            logger.LogWarning("Activity with ID {ActivityId} not found", id);
            return null;
        }

        var mapTilerKey = options.IncludeMapTilerKey
            ? await mapTilerService.GetApiKeyAsync() ?? string.Empty
            : string.Empty;

        // Owner only when UserId matches — legacy null-owner rows are view-only for everyone (matches
        // OwnedActivities write semantics; a null-owner UI would lie about editability). A null
        // SettingsUserId can never match, so a share render is never the owner.
        var isOwner = options.SettingsUserId is not null && activity.UserId == options.SettingsUserId;

        EditResumeHint? resumeHint = null;
        var historyFacts = new TrackHistoryFacts(0, false);
        if (isOwner && options.IncludeOwnerData)
        {
            resumeHint = await editSessions.GetResumeHintAsync(id);
            historyFacts = await versionHistory.FactsAsync(new TrackId(id));
        }

        // Full resolution — the details page does NOT downsample. A review render reads owner-scoped, for the
        // same reason the metadata did: the visible-gated read returns an empty track for a non-released ride.
        var gpsData = isReview
            ? await activityGps.GetOwnedGpsDataForTrackAsync(new TrackId(id))
            : await activityGps.GetGpsDataForTrackAsync(new TrackId(id));

        if (gpsData.Count == 0)
            logger.LogWarning("No GPS data found for activity {ActivityId}", id);
        else
            LogCoordinateQuality(gpsData);

        var splits = gpsData.Count > 0
            ? await CalculateSplitsAsync(activity, gpsData, options.SettingsUserId)
            : [];

        return new ActivityDetailView
        {
            Activity = activity,
            GpsData = gpsData,
            Splits = splits,
            IsOwner = isOwner,
            // A ride that is not in circulation yet offers no editor, no segment creation and no photo import:
            // the review banner's approve/delete are the only verbs that mean anything on it.
            Surfaces = isReview ? ActivityViewSurfaces.ForOwnerReview : options.Surfaces(isOwner),
            ResumeHint = resumeHint,
            HistoryFacts = historyFacts,
            MapTilerApiKey = mapTilerKey,
            PhotosApiBase = options.PhotosApiBase,
        };
    }

    private void LogCoordinateQuality(List<GpsData> gpsData)
    {
        // One pass (was two LINQ Count scans over the full-res track).
        int nullCoords = 0, zeroCoords = 0;
        foreach (var g in gpsData)
        {
            if (g.GpsLat is null || g.GpsLong is null) nullCoords++;
            else if (g.GpsLat == 0 && g.GpsLong == 0) zeroCoords++;
        }

        if (nullCoords > 0)
            logger.LogWarning("Found {NullCount} GPS points with null coordinates out of {TotalCount}",
                nullCoords, gpsData.Count);
        if (zeroCoords > 0)
            logger.LogWarning("Found {ZeroCount} GPS points with zero coordinates out of {TotalCount}",
                zeroCoords, gpsData.Count);
    }

    private async Task<List<Split>> CalculateSplitsAsync(
        Activity activity, List<GpsData> gpsData, string? userId)
    {
        try
        {
            var activityType = activity.ActivityType?.ToUpperInvariant() ?? "";
            var isFootborne = activityType.Contains("RUN", StringComparison.Ordinal)
                              || activityType.Contains("WALK", StringComparison.Ordinal)
                              || activityType.Contains("HIK", StringComparison.Ordinal);

            var splitDistanceKm = isFootborne
                ? await settingsService.GetSettingAsync<decimal>(
                    SettingCategories.Splits, SettingKeys.SplitDistanceRunWalk, userId, defaultValue: 1.0m)
                : await settingsService.GetSettingAsync<decimal>(
                    SettingCategories.Splits, SettingKeys.SplitDistanceOther, userId, defaultValue: 4.0m);

            var splits = SplitCalculator.CalculateSplits(gpsData, splitDistanceKm);
            logger.LogDebug("Calculated {Count} splits at {Distance} km intervals", splits.Count, splitDistanceKm);
            return splits;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calculating splits");
            return [];
        }
    }
}
