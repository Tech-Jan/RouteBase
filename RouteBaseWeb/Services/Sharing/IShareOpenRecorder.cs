using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Records that a share page was opened.
///
/// Its own service rather than a method on <see cref="IShareLinkService"/>: that one is the OWNER-facing
/// management surface (mint / list / revoke) and drags IVisibilityService, IRecordingService and IRaceAccess
/// behind it. None of that belongs on the anonymous page's dependency graph. This has one job, one query and
/// no authorization of its own — it is handed a link id that a grant already resolved.
/// </summary>
public interface IShareOpenRecorder
{
    /// <summary>
    /// Bumps the link's open count and stamps the time. Never throws: an accounting write must not be able
    /// to 500 a page that has already authorized and rendered.
    /// </summary>
    Task RecordOpenAsync(int shareLinkId, CancellationToken ct);
}
