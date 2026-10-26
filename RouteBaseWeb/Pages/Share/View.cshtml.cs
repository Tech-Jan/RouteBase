using RouteBaseWeb.Helpers;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Activities;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Share;

/// <summary>
/// The anonymous share view: <c>/s/{shareToken}</c>.
///
/// Authentication happens in ShareLinkAuthenticationHandler, which reads the token from the route and mints
/// a <c>share:anon</c> principal — an identity that owns nothing and can see only public data. This page
/// then widens, through <see cref="IShareLinkAccess.WidenAsync"/>, to exactly the one resource the grant
/// covers and nothing else.
///
/// ONE page serves both kinds. The kind is deliberately absent from the URL — it would be enumerable
/// information — so it is only known after the grant resolves, which means the dispatch has to live here.
/// A kind switch at the route entry is DISPATCH, not the forbidden feature-`if`: everything below
/// it stays inside its own slice, and the two branches share no logic.
///
/// Every failure is <see cref="NotFoundResult"/>: an unknown, expired or revoked token must be
/// indistinguishable from a wrong URL.
/// </summary>
[Authorize(AuthenticationSchemes = ShareLinkDefaults.AuthenticationScheme,
           Policy = ShareLinkDefaults.ReadPolicy)]
public class ShareViewModel(
    IShareLinkAccess shareAccess,
    IActivityDetailViewBuilder activityViewBuilder,
    ISegmentDetailViewBuilder segmentViewBuilder,
    ShareSegmentViewSource segmentSource,
    IShareRaceReader raceReader,
    IShareOpenRecorder openRecorder,
    IRecordingService recording,
    IUserService users) : PageModel
{
    public ActivityDetailView? ActivityView { get; private set; }
    public SegmentDetailView? SegmentView { get; private set; }

    /// <summary>The seed for a live-recording share — set on the live branch, null otherwise. Map-only:
    /// carries no MapTiler key, no other rider, no PII beyond the owner's display name.</summary>
    public ShareLiveSeedDto? LiveSeed { get; private set; }

    /// <summary>The seed for a race share — set on the race branch, null otherwise. Carries no race data:
    /// the replay is fetched from the token-scoped read endpoint, already redacted.</summary>
    public ShareRaceSeedDto? RaceSeed { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ShareResponseHeaders.Apply(Response);

        var grant = await shareAccess.CurrentAsync(ct);
        if (grant is null) return NotFound();

        // Subresources are mounted under the token. Taken from the ROUTE rather than re-serialized from
        // the grant, so the page never has to hold the plaintext token in a field.
        var token = RouteData.Values[ShareLinkDefaults.RouteTokenKey] as string;
        if (!ShareTokens.IsWellFormed(token)) return NotFound();

        var ok = grant.Kind switch
        {
            ShareKind.Activity      => await LoadActivityAsync(grant, token, ct),
            ShareKind.Segment       => await LoadSegmentAsync(grant, token, ct),
            ShareKind.LiveRecording => await LoadLiveAsync(grant, token, ct),
            ShareKind.Race          => await LoadRaceAsync(grant, token, ct),
            _ => false,
        };
        if (!ok) return NotFound();

        // Counted HERE — after the render succeeded, and only on the page. Not in ShareLinkAccess: token
        // resolution runs on every subresource request (the photo list, each of up to 200 image fetches, a
        // live share's poll loop), so counting there would be a write per request and a number nobody could
        // read as "opens". A 404 is not an open either, which is why this sits past the dispatch.
        await openRecorder.RecordOpenAsync(grant.TokenId, ct);
        return Page();
    }

    /// <summary>
    /// The options are what make this render anonymous:
    ///   SettingsUserId: null    → IsOwner is false even when the OWNER opens their own link, so the page
    ///                             is byte-identical for owner and stranger (and previewing the link
    ///                             actually shows what recipients see).
    ///   IncludeOwnerData: false → no edit-session or version-history reads.
    ///   IncludeMapTilerKey:false→ the key never reaches a page anyone with the URL can load.
    ///   Surfaces: ReadOnlyShare → hard-coded, not derived from IsOwner: share-ness is a property of the
    ///                             ROUTE, not the principal.
    /// </summary>
    private async Task<bool> LoadActivityAsync(ShareGrant grant, string token, CancellationToken ct)
    {
        ActivityView = await shareAccess.WidenAsync(
            ShareKind.Activity,
            grant.ResourceId,
            () => activityViewBuilder.BuildAsync(grant.ResourceId, new ActivityViewOptions(
                IncludeOwnerData: false,
                IncludeMapTilerKey: false,
                SettingsUserId: null,
                Surfaces: _ => ActivityViewSurfaces.ReadOnlyShare,
                PhotosApiBase: $"/s/{token}/api/photos")),
            ct);

        if (ActivityView is null) return false;
        ViewData["Title"] = ActivityView.Activity.TrackName ?? "Shared activity";
        return true;
    }

    /// <summary>
    /// Deliberately NOT wrapped in <see cref="IShareLinkAccess.WidenAsync"/>, unlike the activity branch.
    ///
    /// The segment reads are owner-scoped by CONSTRUCTION (ShareSegmentReader takes the grant as data and
    /// has no ambient identity), so there is nothing to widen. Widening would actively break it: every
    /// SegmentEfforts read consults its cache BEFORE its visibility gate, so running as the owner would
    /// land on the owner's own namespace — the one their normal page filled with their FRIENDS' efforts —
    /// and a cache hit would return third-party efforts before any predicate ran. See CacheKeys.ShareSegment*.
    /// </summary>
    private async Task<bool> LoadSegmentAsync(ShareGrant grant, string token, CancellationToken ct)
    {
        segmentSource.Bind(grant);

        SegmentView = await segmentViewBuilder.BuildAsync(new SegmentId(grant.ResourceId), segmentSource, new SegmentViewOptions(
            IncludeMapTilerKey: false,
            IncludeHeatmap: false,
            SettingsUserId: null,
            Surfaces: _ => SegmentViewSurfaces.ReadOnlyShare,
            ApiBase: $"/s/{token}/api/segment"), ct);

        if (SegmentView is null) return false;
        ViewData["Title"] = SegmentView.Segment.SegmentName ?? "Shared segment";
        return true;
    }

    /// <summary>
    /// The live branch: map-only, driven by the client polling <c>/s/{token}/api/recording/live</c>.
    ///
    /// Like the segment branch and unlike the activity branch, it does NOT widen to the owner — there is
    /// nothing to impersonate. It resolves the one RecordingSession for the granted track and seeds only
    /// the map-safe fields; the display name is the sole PII, matching the confirmed "shows the owner's
    /// display name" decision. NotFound if the session row is gone (abandoned + deleted).
    /// </summary>
    private async Task<bool> LoadLiveAsync(ShareGrant grant, string token, CancellationToken ct)
    {
        // Lifecycle-gated (Released-only) + owner-pinned resolve — the share-READ gate the mint side
        // promises (see GetLiveShareSessionAsync). NotFound for a trashed or awaiting-review ride exactly
        // as for a gone session (abandoned+deleted).
        var session = await recording.GetLiveShareSessionAsync(new TrackId(grant.ResourceId), grant.OwnerUserId, ct);
        if (session is null) return false;

        // DisplayName only (never email): the account name is what the LiveView shows friends, and it is
        // the one identity field the owner chose to publish with this link.
        var displayName = (await users.GetUserByIdAsync(session.UserId))?.DisplayName ?? "Rider";

        LiveSeed = new ShareLiveSeedDto(
            ApiBase: $"/s/{token}/api/recording",
            DisplayName: displayName,
            ActivityType: session.ActivityType,
            // ToUtcOffset, not an implicit cast: the DB round-trip strips DateTimeKind, and the implicit
            // conversion would read Unspecified as server-local — skewing the client's elapsed clock (AR-02).
            StartedAt: session.StartedAt.ToUtcOffset(),
            StoppedAt: session.StoppedAt.ToUtcOffset(),
            Status: session.Status,
            PointCount: session.PointCount);

        ViewData["Title"] = "Live recording";
        return true;
    }

    /// <summary>
    /// The race branch: the replay chrome, seeded with nothing but the read root — the payload itself comes
    /// from <c>/s/{token}/api/race/replay</c>, already redacted.
    ///
    /// Like the segment and live branches, and unlike the activity branch, it does NOT widen. Widening
    /// would run <c>RaceAccess</c>'s per-rider re-authorization as the OWNER, whose friend graph would then
    /// admit every friend's ride — name and full GPS — to an anonymous stranger. See IShareRaceReader.
    ///
    /// The reader is consulted HERE too, rather than trusting the grant and letting the fetch 404 later: a
    /// race whose course the owner no longer owns (or has trashed) must be a 404 PAGE, indistinguishable
    /// from a wrong URL, not a rendered shell that fails to load its own content. Through GetHeaderAsync,
    /// not GetReplayAsync — the same gates without projecting a field the client is about to fetch anyway.
    /// </summary>
    private async Task<bool> LoadRaceAsync(ShareGrant grant, string token, CancellationToken ct)
    {
        var header = await raceReader.GetHeaderAsync(grant, ct);
        if (header is null) return false;

        RaceSeed = new ShareRaceSeedDto(
            RaceId: header.RaceId,
            ApiBase: $"/s/{token}/api/race");

        ViewData["Title"] = header.RaceName ?? "Shared race";
        return true;
    }
}
