using RouteBaseWeb.Models.Sharing;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// The share path's ONLY race query surface.
///
/// Purpose-built for the same reason <see cref="IShareSegmentReader"/> is: identity-widening cannot express
/// what a race share needs. <see cref="Services.Races.RaceReplayService"/> reads through
/// <see cref="Services.Races.IRaceAccess"/>, which re-authorizes each real participant against the CURRENT
/// user's friend graph — so running it under <c>WidenAsync</c> would evaluate that graph as the OWNER and
/// hand an anonymous stranger every friend's ride, name and full GPS. That is the exact shape of the leak
/// <c>WidenAsync</c>'s own remarks warn about ("the read runs as the OWNER").
///
/// So this runs as <c>share:anon</c>, outside <c>WidenAsync</c>, with the grant passed in as DATA — a
/// mismatched owner/race pair is unconstructible at the call site. It injects neither
/// <c>IVisibilityService</c> nor <c>ICurrentUserService</c>, and <c>ShareRaceReaderIsolationTests</c>
/// fails the build if either is added.
/// </summary>
public interface IShareRaceReader
{
    /// <summary>
    /// The redacted replay payload, or null when the grant no longer resolves to a race this owner owns
    /// over a course they own — and null for every other reason too (no GPS, wrong kind, trashed course),
    /// so a caller cannot distinguish them.
    /// </summary>
    Task<ShareRaceReplayPayload?> GetReplayAsync(ShareGrant grant, CancellationToken ct = default);

    /// <summary>
    /// Whether this grant serves anything at all, plus the title — WITHOUT building the field.
    ///
    /// The page needs both (a miss must be a 404 page, not a rendered shell whose content then fails to
    /// load), and it would otherwise learn them by projecting the whole payload that the client is about to
    /// fetch anyway — paying every real rider's DTW projection twice per open. Runs the SAME gates as
    /// <see cref="GetReplayAsync"/>, so the two cannot disagree about what is servable.
    /// </summary>
    Task<ShareRaceHeader?> GetHeaderAsync(ShareGrant grant, CancellationToken ct = default);
}

/// <summary>What the share PAGE needs before it renders: that there is something to render, and its title.</summary>
public sealed record ShareRaceHeader(int RaceId, string? RaceName);
