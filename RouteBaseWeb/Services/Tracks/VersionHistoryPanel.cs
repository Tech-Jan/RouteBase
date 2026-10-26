using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Model for <c>Pages/Activities/_VersionHistoryPanel.cshtml</c>. The content slice owns its markup; Details
/// only names the seam (the _TrackEditChrome pattern).
///
/// <paramref name="Facts"/> is why this exists as a record rather than the bare Activity: the panel used to
/// fetch <c>/versions</c> on EVERY owner page load just to discover it had nothing to show — which is the case
/// for most rides, since most are imported once and never touched. Rendering the two counts here lets the panel
/// answer that without a request (F-1: server-render the fact, delete the page-load fetch).
///
/// It carries FACTS, not the verdict — the "does this ride have a history" rule has a subtlety (a merge/split
/// product has one version, and that version IS an edit) and lives in exactly one place, <c>hasHistory</c> in
/// version-history-model.ts.
/// </summary>
public sealed record VersionHistoryPanel(
    int TrackId, string Lifecycle, bool CheckedOut, TrackHistoryFacts Facts);
