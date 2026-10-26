using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The race slice's SINGLE authorization seam. Every read and every write in the slice goes through
/// here — the CRUD services below it hold domain rules, not permissions.
///
/// Four rules, and they are not symmetrical:
///
///   - **Read** a race: own, public, or a friend's non-private one (<see cref="VisibleRaces"/>).
///   - **Edit** a race's field/course: owner, or a collaborator the race's edit policy admits — and only
///     ever someone who may also READ it (<see cref="EditableRaceAsync"/>). Default policy is owner-only.
///   - **Own** a race (delete, change its edit policy): owner only (<see cref="OwnedRaceAsync"/>).
///   - **Create** a course from an activity: you must OWN it. You may race over a friend's ride, but you
///     may not define a course from it.
///
/// Extracted from RaceCrud when that grew a third and fourth responsibility. The seam is the asset; the
/// god-service was not. Splitting the CRUD without extracting this first would have scattered
/// authorization across three classes, which is exactly how a slice grows a hole.
/// </summary>
public interface IRaceAccess
{
    /// <summary>Races the caller may READ, as a composable query. AsNoTracking — see the implementation.</summary>
    IQueryable<Race> VisibleRaces();

    /// <summary>
    /// A race with checkpoints (seq order) and participants, with real participants the caller may no
    /// longer view REMOVED from the field. Null when the race isn't visible.
    /// </summary>
    Task<Race?> GetRaceAsync(RaceId raceId);

    /// <summary>
    /// The race the caller may OWN — owner-only, children loaded, TRACKED for mutation. Used by the
    /// writes that stay the owner's alone: delete, and setting the edit policy itself.
    /// </summary>
    Task<Race?> OwnedRaceAsync(RaceId raceId);

    /// <summary>
    /// The race the caller may EDIT — owner, or a collaborator the edit policy admits (and who may also
    /// view it). Children loaded, TRACKED for mutation. Null when not editable. This is the write seam for
    /// everything except delete / policy changes.
    /// </summary>
    Task<Race?> EditableRaceAsync(RaceId raceId);

    /// <summary>
    /// The <see cref="EditableRaceAsync"/> predicate without loading the field — for the page/UI gate that
    /// decides whether to render edit controls.
    /// </summary>
    Task<bool> CanEditRaceAsync(RaceId raceId);

    /// <summary>
    /// The CHECKPOINTS of a race the caller may edit (seq order, untracked), or null.
    ///
    /// The read-only half of <see cref="EditableRaceAsync"/>, and it exists for a measured reason: the
    /// endpoints that need only the course's SHAPE — the import preview, the CSV template, the mapping
    /// profiles — were loading and change-tracking the entire field to get it. That is 491 entities on
    /// Ski to Sea, and the preview re-runs on EVERY picker change, so the waste was per keystroke.
    /// </summary>
    Task<List<RaceCheckpoint>?> EditableCheckpointsAsync(RaceId raceId);

    /// <summary>The caller's id. Writers stamp it; readers scope by it.</summary>
    string? CurrentUserId();

    /// <summary>
    /// Does <paramref name="userId"/> OWN this race? Ownership by EXPLICIT identity — the one seam in here
    /// that does not consult the ambient user. The share-mint path needs exactly this: it authorizes on the
    /// id it is about to persist, so authorizing one identity while writing another (a confused deputy) is
    /// impossible. Not a substitute for <see cref="OwnedRaceAsync"/>, which loads the row for mutation.
    /// </summary>
    Task<bool> OwnsRaceAsync(RaceId raceId, string userId, CancellationToken ct = default);

    /// <summary>May the caller VIEW this activity (own or a friend's)? Also enforces the lifecycle gate.</summary>
    Task<bool> CanViewActivityAsync(TrackId trackId);

    /// <summary>
    /// Activities the caller may VIEW, as a composable query — the SQL-bulk form of
    /// <see cref="CanViewActivityAsync"/> (own / friends' non-private / public, released for non-owners).
    /// Used to gate a race's course metadata per viewer in one projection (AUTHZ-P2).
    /// </summary>
    IQueryable<Activity> VisibleActivities();

    /// <summary>Activities the caller OWNS — the only ones a course may be defined from.</summary>
    IQueryable<Activity> OwnedActivities();
}

/// <inheritdoc />
public class RaceAccess : IRaceAccess
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;

    public RaceAccess(RouteBaseDbContext context, IVisibilityService visibility)
    {
        _context = context;
        _visibility = visibility;
    }

    public string? CurrentUserId() => _visibility.GetCurrentUserId();

    public Task<bool> CanViewActivityAsync(TrackId trackId) => _visibility.CanViewActivityAsync(trackId);

    public IQueryable<Activity> VisibleActivities() => _visibility.VisibleActivities(_context.Activities);

    public IQueryable<Activity> OwnedActivities() => _visibility.OwnedActivities(_context.Activities);

    /// <summary>
    /// Mirrors the Segment/Activity visibility rules.
    ///
    /// AsNoTracking is load-bearing, not hygiene: <see cref="GetRaceAsync"/> REASSIGNS race.Participants
    /// to the re-authorized subset. On a tracked entity that reads to EF as "these children were removed
    /// from a required-FK collection" — and the next SaveChangesAsync in the same scope would
    /// cascade-DELETE the friend's rider row. Write paths use OwnedRaceAsync, which stays tracked.
    /// </summary>
    public IQueryable<Race> VisibleRaces()
    {
        var userId = _visibility.GetCurrentUserId();
        return _context.Races.AsNoTracking().Where(r =>
            r.UserId == userId ||
            r.Visibility == Visibilities.Public ||
            (r.Visibility == Visibilities.Friends &&
             _context.Friendships.Any(f => f.Status == FriendshipStatuses.Accepted &&
                 ((f.RequesterId == userId && f.AddresseeId == r.UserId) ||
                  (f.AddresseeId == userId && f.RequesterId == r.UserId)))));
    }

    public async Task<Race?> GetRaceAsync(RaceId raceId)
    {
        var race = await VisibleRaces()
            .Include(r => r.Checkpoints)
            .Include(r => r.Participants)
            .AsSplitQuery()   // sibling collection Includes: split avoids the checkpoints × participants row blow-up
            .FirstOrDefaultAsync(r => r.RaceId == raceId);
        if (race == null) return null;

        race.Checkpoints = race.Checkpoints.OrderBy(c => c.Seq).ToList();

        // Re-authorize every real participant against CURRENT visibility. The stored track_id is a
        // pointer, not a grant: a friendship revoked after the rider was added must drop him from the
        // field. Simulated riders own no activity and are never gated.
        var authorized = new List<RaceParticipant>();
        foreach (var p in race.Participants)
        {
            if (p.Kind == RaceParticipantKind.Real && p.TrackId is TrackId trackId)
            {
                if (!await _visibility.CanViewActivityAsync(trackId)) continue;
            }
            authorized.Add(p);
        }
        race.Participants = authorized;
        return race;
    }

    public Task<bool> OwnsRaceAsync(RaceId raceId, string userId, CancellationToken ct = default) =>
        _context.Races.AsNoTracking().AnyAsync(r => r.RaceId == raceId && r.UserId == userId, ct);

    public async Task<Race?> OwnedRaceAsync(RaceId raceId)
    {
        var userId = _visibility.GetCurrentUserId();
        return await _context.Races
            .Include(r => r.Checkpoints)
            .Include(r => r.Participants)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.RaceId == raceId && r.UserId == userId);
    }

    public async Task<Race?> EditableRaceAsync(RaceId raceId)
    {
        // Load unconditionally (tracked), then apply the edit rule: the collaborator check is async and
        // per-friendship, so it cannot live in the WHERE clause the way OwnedRaceAsync's owner check does.
        var race = await _context.Races
            .Include(r => r.Checkpoints)
            .Include(r => r.Participants)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.RaceId == raceId);
        if (race == null) return null;
        return await MayEditAsync(race.UserId, race.Visibility, race.EditPolicy) ? race : null;
    }

    /// <inheritdoc />
    public async Task<List<RaceCheckpoint>?> EditableCheckpointsAsync(RaceId raceId)
    {
        if (!await CanEditRaceAsync(raceId)) return null;

        // AsNoTracking: these are read, projected and thrown away. A tracked load here would also make
        // any later SaveChangesAsync in the same scope see them, which is the class of accident
        // GetRaceAsync's own AsNoTracking comment describes.
        return await _context.RaceCheckpoints.AsNoTracking()
            .Where(c => c.RaceId == raceId)
            .OrderBy(c => c.Seq)
            .ToListAsync();
    }

    public async Task<bool> CanEditRaceAsync(RaceId raceId)
    {
        var race = await _context.Races.AsNoTracking()
            .Where(r => r.RaceId == raceId)
            .Select(r => new { r.UserId, r.Visibility, r.EditPolicy })
            .FirstOrDefaultAsync();
        return race != null && await MayEditAsync(race.UserId, race.Visibility, race.EditPolicy);
    }

    /// <summary>
    /// The edit rule, shared by <see cref="EditableRaceAsync"/> (loads the row) and
    /// <see cref="CanEditRaceAsync"/> (projects it). Owner always; otherwise the caller must be able to
    /// VIEW the race AND match its edit policy — so edit rights can never exceed view rights.
    /// </summary>
    private async Task<bool> MayEditAsync(string? ownerUserId, string? visibility, string? editPolicy)
    {
        var userId = _visibility.GetCurrentUserId();
        if (ownerUserId == userId) return true;
        // Edit ⊆ view: a 'public' edit policy on a 'friends'/'private' race still admits no stranger.
        if (!await _visibility.CanViewItemAsync(ownerUserId ?? string.Empty, visibility)) return false;
        return editPolicy switch
        {
            RaceEditPolicy.Public  => true,                                           // any viewer (gated above)
            RaceEditPolicy.Friends => await _visibility.CanViewItemAsync(ownerUserId ?? string.Empty, Visibilities.Friends),
            _                      => false,                                          // owner-only + unrecognized
        };
    }
}
