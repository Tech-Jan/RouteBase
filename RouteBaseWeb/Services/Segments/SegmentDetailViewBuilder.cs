using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments.Rules;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// How to build a <see cref="SegmentDetailView"/> for one caller.
/// </summary>
/// <param name="IncludeMapTilerKey">
/// Off for a share render: the key is serialized into the page, so a share URL would publish it to anyone
/// holding the link. The basemap registry already falls back to keyless styles.
/// </param>
/// <param name="IncludeHeatmap">
/// Off for a share render. This is the load-bearing half of heatmap suppression: with no Heatmap on the
/// view, the config island omits hasHeatmap/heatmapStatus, so heatmap-controls.ts computes status 'none'
/// and its 2s x 300 poll never starts.
/// </param>
/// <param name="SettingsUserId">
/// Whose display preferences to apply, and who to compare against for <c>IsOwner</c>. <c>null</c> means
/// "nobody" — profile defaults, and IsOwner false. A share render passes null so the page is byte-identical
/// whether the visitor is a stranger or the owner previewing their own link.
/// </param>
/// <param name="Surfaces">Given IsOwner, which affordances the render offers.</param>
/// <param name="ApiBase">Where this render's segment data endpoints live.</param>
public sealed record SegmentViewOptions(
    bool IncludeMapTilerKey,
    bool IncludeHeatmap,
    string? SettingsUserId,
    Func<bool, SegmentViewSurfaces> Surfaces,
    string ApiBase = "/api/SegmentsApi");

public interface ISegmentDetailViewBuilder
{
    /// <summary>Builds the view, or null when the segment is not visible to the caller (→ 404).</summary>
    Task<SegmentDetailView?> BuildAsync(SegmentId segmentId, ISegmentViewSource source, SegmentViewOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Builds the segment statistics view-model. Extracted verbatim from <c>StatisticsModel.OnGetAsync</c> so
/// both the canonical page and the share page produce identical data from one code path — the alternative
/// was a second copy of this loading sequence that would drift.
///
/// Takes NO <see cref="ICurrentUserService"/>. That is structural, not stylistic: <c>GetUserId()</c> throws
/// on an anonymous principal, so a builder that could reach for it would be one refactor away from
/// breaking every share render. Identity arrives as <see cref="SegmentViewOptions.SettingsUserId"/>, and
/// the gate arrives as <see cref="ISegmentViewSource"/>.
/// </summary>
public sealed class SegmentDetailViewBuilder(
    IMapTilerService mapTilerService,
    IProfileSettingsService settingsService,
    IHeatmapLifecycle heatmaps) : ISegmentDetailViewBuilder
{
    public async Task<SegmentDetailView?> BuildAsync(
        SegmentId segmentId, ISegmentViewSource source, SegmentViewOptions options, CancellationToken ct = default)
    {
        // Sequential on purpose: these services share the request-scoped DbContext (EF Core forbids
        // concurrent ops). Load the segment first so a 404 skips the settings work.
        var segment = await source.GetSegmentAsync(segmentId, ct);
        if (segment == null) return null;

        var mapTilerKey = options.IncludeMapTilerKey
            ? await mapTilerService.GetApiKeyAsync() ?? string.Empty
            : string.Empty;

        var userId = options.SettingsUserId;

        // Owner only when UserId matches — legacy null-owner segments are view-only for everyone (matches
        // OwnedSegments write semantics, deliberately not Details' IsNullOrEmpty variant, see PD-08).
        // A null SettingsUserId can never match, so a share render is never the owner.
        var isOwner = userId is not null && segment.UserId == userId;

        // No chart/display preferences are loaded here at all any more: they reach the page as #appSettings,
        // delivered once per request by the layouts. ChartDetail was the last hold-out and its copy HAD
        // drifted — the client fell back to "normal" against a real default of "auto".

        // Single-key read (invariant parsing inside the service); a cheap indexed lookup.
        var defaultMatchThreshold = await settingsService.GetSettingAsync<int>(
            SettingCategories.Segments, SettingKeys.DefaultMatchThreshold, userId,
            SegmentAnalysis.DefaultMatchThresholdPercent);

        IReadOnlyList<ShapeRectDto> shapeRects = [];
        if (segment.SegmentKind == SegmentKinds.Shapes && RuleDocument.TryParse(segment.MatchRulesJson) is { } rules)
        {
            shapeRects = rules.ZoneRects().Select(r => new ShapeRectDto(
                MinLat:  Math.Min(r.South, r.North),
                MaxLat:  Math.Max(r.South, r.North),
                MinLong: Math.Min(r.West, r.East),
                MaxLong: Math.Max(r.West, r.East),
                Role:    Rules.ShapeKinds.Zone)).ToList();
        }

        // SS-09: honour client disconnect so an abandoned page load stops the DB work.
        var efforts = await source.GetEffortsAsync(segmentId, ct);
        var totals  = await source.GetTotalsAsync(segmentId, ct);

        // Not merely gated in the markup — never LOADED. A share render must not be able to leak a
        // heatmap's existence through timing or through a config key someone adds later.
        var heatmap = options.IncludeHeatmap ? await heatmaps.GetVisibleAsync(segmentId, ct) : null;

        return new SegmentDetailView
        {
            Segment               = segment,
            IsOwner               = isOwner,
            Surfaces              = options.Surfaces(isOwner),
            DetailedEfforts       = efforts,
            EffortTotals          = totals,
            ShapeRects            = shapeRects,
            Heatmap               = heatmap,
            MapTilerApiKey        = mapTilerKey,
            ApiBase               = options.ApiBase,
            DefaultMatchThreshold = defaultMatchThreshold,
        };
    }
}
