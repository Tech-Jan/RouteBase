using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Pages.Races;

public class IndexModel : PageModel
{
    private readonly IRaceCrud _races;
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly IShareLinkService _shareLinks;
    private readonly ICurrentUserService _currentUser;

    public IndexModel(
        IRaceCrud races,
        RouteBaseDbContext context,
        IVisibilityService visibility,
        IShareLinkService shareLinks,
        ICurrentUserService currentUser)
    {
        _races = races;
        _context = context;
        _visibility = visibility;
        _shareLinks = shareLinks;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Pre-selected course, from "Race this ride" on an activity (and therefore from the editor: a
    /// merged multi-day ride is just an activity, so the same link works for it unchanged).
    /// </summary>
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public int? CourseTrackId { get; set; }

    public List<RaceSummaryDto> Races { get; private set; } = [];

    /// <summary>Race ids the VIEWER has EVER shared, mapped to their lifetime open count and whether a link
    /// is live — the cards badge the key, show the value and dim it once sharing ended. Owner-scoping lives
    /// in the service's WHERE, so a friend's race simply never comes back (which is just as well:
    /// RaceSummaryDto carries no owner for this page to filter on).</summary>
    public IReadOnlyDictionary<int, ShareOpens> SharedRaceOpens { get; private set; } = new Dictionary<int, ShareOpens>();

    /// <summary>Bootstrap for the client filter/sort controls grid over the race cards (races-grid.ts).</summary>
    public RacesGridBootstrap GridBootstrap { get; private set; } = new();

    /// <summary>Courses to choose from — your OWN rides only (you may race over a friend's, not define
    /// a course from one). A merged multi-day ride out of the editor is just another entry here.</summary>
    public List<RaceRideOption> CourseCandidates { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Races = await _races.GetRacesAsync();

        // Before the bootstrap, not after: the rows carry IsShared, so the set has to exist first.
        SharedRaceOpens = await _shareLinks.ShareOpensAsync(
            ShareKindNames.Race, _currentUser.GetUserId(), HttpContext.RequestAborted);

        GridBootstrap = new RacesGridBootstrap
        {
            Rows = Races.Select(r => new RacesGridRow
            {
                Id = r.RaceId,
                Name = r.RaceName,
                DateIso = r.RaceDate,   // already the FE-12 wall-clock string (AR-02)
                Visibility = r.Visibility,
                Field = r.ParticipantCount,
                Sections = r.CheckpointCount,
                DistanceMeters = r.RaceDistanceMeters,
                IsShared = SharedRaceOpens.IsSharedNow(r.RaceId),
                WasShared = SharedRaceOpens.WasSharedBefore(r.RaceId),
            }).ToList(),
        };

        // No cap: a Take(n) here silently hid rides, and it hid the undated ones first — a route imported
        // without timestamps sorts last under DESC. The dropdown is filterable, so it can hold them all.
        CourseCandidates = (await _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.Lifecycle == Services.Lifecycle.LifecycleStates.Released && a.TotalDistance > 0)
            .OrderByDescending(a => a.StartDateTimeUtc)
            .AsNoTracking()
            .Select(a => new { a.TrackId, a.TrackName, a.TotalDistance })
            .ToListAsync())
            .Select(a => new RaceRideOption(a.TrackId.Value, a.TrackName, a.TotalDistance)).ToList();
    }
}

/// <summary>Bootstrap for the races controls grid — all visible race rows. Mirrors TS RacesGridBootstrap.</summary>
public class RacesGridBootstrap
{
    public List<RacesGridRow> Rows { get; set; } = new();
}

/// <summary>One row of the races sort/filter controls grid. Mirrors TS RacesGridRow (races-grid-types.ts).</summary>
public class RacesGridRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    /// <summary>Round-trip (ISO 8601) race date, or null for an undated race.</summary>
    public string? DateIso { get; set; }
    [WireValues(typeof(Visibilities))]
    public string Visibility { get; set; } = Visibilities.Default;
    /// <summary>Field size — how many riders (real + simulated).</summary>
    public int Field { get; set; }
    /// <summary>Section count — checkpoints incl. Start + Finish.</summary>
    public int Sections { get; set; }
    /// <summary>Course length in metres, or null if the course row is gone.</summary>
    public double? DistanceMeters { get; set; }

    /// <summary>Whether the VIEWER has an active share link for this race — never the row owner's, so a
    /// friend's race always reads false. Drives the grid's Shared filter, matching the card badge.</summary>
    public bool IsShared { get; set; }

    /// <summary>The viewer shared this race at some point and no link is live now — the grid's "Previously
    /// shared" state. Mutually exclusive with <see cref="IsShared"/>.</summary>
    public bool WasShared { get; set; }
}
