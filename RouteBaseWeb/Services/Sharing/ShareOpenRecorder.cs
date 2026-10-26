using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Sharing;

/// <inheritdoc cref="IShareOpenRecorder" />
public sealed class ShareOpenRecorder(
    RouteBaseDbContext db,
    ILogger<ShareOpenRecorder> logger,
    TimeProvider clock) : IShareOpenRecorder
{
    public async Task RecordOpenAsync(int shareLinkId, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            // ExecuteUpdateAsync, not load-mutate-save: this is ONE `UPDATE … SET open_count = open_count + 1`
            // statement, so two simultaneous opens both land. The read-modify-write form would have them read
            // the same value and write the same value — an undercount that grows with exactly the traffic the
            // number exists to reveal. It also skips the change tracker, so a live-share page open cannot
            // collide with whatever else the request-scoped context is holding.
            await db.ShareLinks
                .Where(s => s.ShareLinkId == shareLinkId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.OpenCount, x => x.OpenCount + 1)
                          .SetProperty(x => x.LastOpenedAt, now),
                    ct);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose (logged, not raised): the page is already authorized and rendered by the
            // time this runs. A locked SQLite file must not turn a working share link into a 500.
            logger.LogWarning(ex, "Could not record a share-link open for {ShareLinkId}", shareLinkId);
        }
    }
}
