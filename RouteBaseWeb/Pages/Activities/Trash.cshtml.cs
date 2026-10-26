using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Pages.Activities;

/// <summary>
/// The trash, as a card feed: the dashboard's <c>_ActivityCard</c> (minimap + stats), wrapped by this slice in
/// a <c>.trash-card</c> that adds the grey, the badge and the restore/purge actions. The shared partial knows
/// nothing about the trash.
///
/// Cards are server-rendered; the actions stay AJAX against /api/trash (trash.ts).
/// </summary>
[Authorize]
public class TrashModel : PageModel
{
    private readonly TrashQueryService _trash;

    public TrashModel(TrashQueryService trash) => _trash = trash;

    /// <summary>The trashed rides themselves — one card each, newest thrown away first.</summary>
    public List<Activity> Activities { get; private set; } = new();

    /// <summary>Per-card trash chrome (why it's here, what Restore does, whether a merge hangs off it).</summary>
    public Dictionary<int, TrashedActivity> Trashed { get; private set; } = new();

    public TrashGridBootstrap GridBootstrap { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Two reads of the same small set: the entities (the card partial takes Activity) and the trash summary
        // (which adds the merge-product name — a join no Activity column carries).
        // AsNoTracking: the page only renders these. The service's IQueryable is shared with write paths, so the
        // read-only choice belongs here — a change-tracker snapshot per rendered card buys nothing.
        Activities = await _trash.TrashedActivities().AsNoTracking().ToListAsync(ct);
        var trashed = await _trash.ListAsync(ct);

        Trashed = trashed.ToDictionary(t => t.TrackId);
        GridBootstrap = new TrashGridBootstrap
        {
            Rows = trashed,
            TypeOptions = OwnerLabels.DistinctSorted(trashed.Select(t => t.ActivityType)),
        };
    }
}

/// <summary>
/// Bootstrap for the trash's filter/sort strip. The rows ARE the trash summary — the grid only needs the
/// filterable columns (date/name/type/distance/elev/reason); the stats live on the cards, rendered server-side.
/// TS mirrors are generated (TrashGridBootstrap/TrashedActivity in src/js/generated/contracts.ts, island schemas D27).
/// </summary>
public class TrashGridBootstrap
{
    public IReadOnlyList<TrashedActivity> Rows { get; set; } = new List<TrashedActivity>();
    public List<string> TypeOptions { get; set; } = new();
}
