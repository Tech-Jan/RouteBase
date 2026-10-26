using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// JSON body of the client-rendered activities grid data endpoint (<c>POST /api/activities/grid</c>);
/// mirrors TS <c>ActivityGridRequestBody</c> (overview/activities-grid.ts) — edit both in the same
/// commit. POST because the body carries the map-drawn <see cref="Shape"/> restriction (rule-document
/// JSON): at ~30 zones a GET query string exceeds the 8 KB request line. The per-column filter row
/// arrives as a plain key→filter map bound into <see cref="Filters"/> (see
/// <see cref="GridFilterCollectionJsonConverter"/>); values are in display units (e.g. distance in km)
/// and converted to storage units by the column registry. Date keeps flowing through the dedicated
/// <see cref="StartDate"/>/<see cref="EndDate"/> fields, which are shared with the top filter panel
/// and the map.
/// </summary>
public class ActivityGridRequest
{
    /// <summary>Per-column filter-row values (name, distance, ascent, …), keyed by column key.</summary>
    public GridFilterCollection Filters { get; set; } = new();

    /// <summary>Multi-select exact activity types.</summary>
    public List<string> Types { get; set; } = new();

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public string? OwnerUserId { get; set; }

    public string SortColumn { get; set; } = "date";
    public string SortDirection { get; set; } = SortDirections.Desc;

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10000;

    /// <summary>
    /// Optional map-drawn shape filter as a rule-document JSON (zones/gates, spatial only — the grid applies
    /// its own column filters). When present the grid restricts rows AND <c>filteredIds</c> to the tracks the
    /// shape matches, so the table shows shape ∩ columns. Decoded + matched via the same
    /// <see cref="Services.Segments.ShapeSegmentMatcher"/> the map's live preview uses (identical set → no
    /// table/map drift). Sent by the shape-filter panel's "Merge table ↔ shapes" toggle.
    /// </summary>
    public string? Shape { get; set; }
}
