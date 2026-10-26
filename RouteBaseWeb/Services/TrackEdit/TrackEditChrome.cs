namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>Which piece of the editor's Details chrome to render. The two pieces sit at two different places in
/// the page (header-right / above the map hero), so the partial renders one at a time — same shape as the
/// _CdnLibs partial.</summary>
public enum TrackEditChromePart { EnterButton, Toolbar }

/// <summary>Model for <c>Pages/Activities/_TrackEditChrome.cshtml</c>. The editor slice owns its markup; Details
/// only names the seam (TE-02 — the slice's markup had pushed the page 96 lines over the .cshtml cap).
///
/// <paramref name="Resume"/> (F-1) is the ride's open edit session, server-rendered — null when it has none,
/// which is what makes a view-only visit cost zero extra HTTP requests. It is also the ONE fact the header's
/// edit button renders from: a checked-out ride offers Resume + Discard, a free one offers Edit
/// (EditorSurfaces). Checked-out is a state of the ride, not a banner floating above it.</summary>
public sealed record TrackEditChrome(
    TrackEditChromePart Part, bool IsOwner, int PointCount, EditResumeHint? Resume = null);
