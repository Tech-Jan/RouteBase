using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Pages.Races;

public class ReplayModel : PageModel
{
    private readonly IRaceCrud _races;
    private readonly IRaceAccess _access;
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;

    public ReplayModel(IRaceCrud races, IRaceAccess access, RouteBaseDbContext context, IVisibilityService visibility)
    {
        _races = races;
        _access = access;
        _context = context;
        _visibility = visibility;
    }

    [BindProperty(SupportsGet = true)]
    public int RaceId { get; set; }

    public Race? Race { get; private set; }

    public int SimulatedCount { get; private set; }

    /// <summary>The real riders already in the field — you and whichever friends you added.</summary>
    public List<RaceParticipant> RealRiders { get; private set; } = [];

    /// <summary>
    /// Rides that could join the field: everything you can SEE (your own + friends' released,
    /// non-private rides), minus the ones already racing. View rights, not ownership — racing a friend's
    /// ride is the whole point.
    /// </summary>
    public List<RaceRideOption> RiderCandidates { get; private set; } = [];

    /// <summary>
    /// Rides that could be the race's PACING REFERENCE — the real effort the grade→cost curve is fitted
    /// from, so the synthetic field inherits a human's terrain response instead of a default one.
    ///
    /// Same view rights as a rider (SetReferenceEffortAsync gates on CanViewActivityAsync), but unlike
    /// <see cref="RiderCandidates"/> the rides already IN the field are NOT excluded — a real rider's own
    /// GPS over this course is the most natural reference there is.
    /// </summary>
    public List<RaceRideOption> ReferenceCandidates { get; private set; } = [];

    public bool IsOwner { get; private set; }

    /// <summary>
    /// Whether the current user may EDIT the field/course — owner, or a collaborator the race's edit
    /// policy admits. Gates every edit control; <see cref="IsOwner"/> gates only the owner-exclusive bits
    /// (deleting, changing the edit policy itself). Computed through <see cref="IRaceAccess"/> so the page
    /// and the write seam share one rule.
    /// </summary>
    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // GetRaceAsync applies visibility AND re-authorizes every real participant — a race you may not
        // see is a 404, not a partially-rendered page.
        Race = await _races.GetRaceAsync(new RaceId(RaceId));
        if (Race == null) return NotFound();

        IsOwner = Race.UserId == _visibility.GetCurrentUserId();
        CanEdit = await _access.CanEditRaceAsync(new RaceId(RaceId));
        SimulatedCount = Race.Participants.Count(p => p.Kind == RaceParticipantKind.Simulated);
        RealRiders = Race.Participants
            .Where(p => p.Kind == RaceParticipantKind.Real)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        if (CanEdit)
        {
            // No cap, same reason as the course list on /Races: a Take(n) hides rides, and it hides the
            // undated ones first. The dropdown is filterable instead.
            var visible = (await _visibility.VisibleActivities(_context.Activities)
                .Where(a => a.Lifecycle == LifecycleStates.Released && a.TotalDistance > 0)
                .OrderByDescending(a => a.StartDateTimeUtc)
                .AsNoTracking()
                .Select(a => new { a.TrackId, a.TrackName, a.TotalDistance })
                .ToListAsync())
                .Select(a => new RaceRideOption(a.TrackId.Value, a.TrackName, a.TotalDistance)).ToList();

            // One query, two lists. A ride already racing cannot be added twice — but it makes a fine
            // pacing reference, so only the RIDER list excludes it. Filtered in memory: the id list is as
            // large as the real field, and an IN clause is the thing this slice keeps tripping over.
            var racing = RealRiders
                .Select(p => p.TrackId)
                .OfType<TrackId>()
                .ToHashSet();

            RiderCandidates = visible.Where(a => !racing.Contains(new TrackId(a.TrackId))).ToList();
            ReferenceCandidates = visible;
        }

        return Page();
    }
}
