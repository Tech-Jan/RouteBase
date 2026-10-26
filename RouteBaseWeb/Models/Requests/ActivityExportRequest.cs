namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// JSON body of <c>POST /api/activities/export</c> — the activities grid's current filtered id set
/// (<c>filteredIds</c>, all pages, already narrowed by the column filters AND the map-drawn shape).
/// POST, not a GET query string: ~1500 ids exceed the 8 KB request line, the same reason
/// <see cref="ActivityGridRequest"/> is a body.
///
/// The ids are a RESTRICTION, not an assertion: this is the scope the user is looking at, so ids they
/// don't own are dropped silently. The GET <c>?trackIds=</c> path names specific rides instead, so there
/// a foreign id stays a recorded <c>NotOwned</c> skip (API-10) — do not merge the two semantics.
/// </summary>
public class ActivityExportRequest
{
    /// <summary>Track ids to export the owned subset of. Empty is a 400 — an empty ZIP is never the intent.</summary>
    public List<int> TrackIds { get; set; } = new();
}
