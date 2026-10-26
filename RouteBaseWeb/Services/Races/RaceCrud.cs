using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The race itself: list, create, delete, and the reference effort. Checkpoints and participants have
/// their own services (<see cref="IRaceCheckpointCrud"/>, <see cref="IRaceParticipantCrud"/>).
///
/// Authorization is NOT here — it is <see cref="IRaceAccess"/>, the slice's single seam.
/// </summary>
public interface IRaceCrud
{
    /// <summary>
    /// Races the caller may see, with their field/checkpoint sizes counted IN SQL. A summary, not an
    /// entity: the counts are the only thing either caller wants, and Including the children to produce
    /// them would load the whole 424-rider field per race.
    /// </summary>
    Task<List<RaceSummaryDto>> GetRacesAsync();

    /// <summary>A race with checkpoints and its authorized field. Null when not visible.</summary>
    Task<Race?> GetRaceAsync(RaceId raceId);

    /// <summary>
    /// Create a race over an OWNED activity. Seeds the two checkpoints every race has: Start (chainage 0)
    /// and Finish (chainage = the COURSE GRID's length). Returns null if the course isn't owned, or has no
    /// geometry to race over.
    /// </summary>
    Task<Race?> CreateRaceAsync(TrackId courseTrackId, string raceName, DateTime? raceDate, string visibility);

    /// <summary>
    /// Deep-copy a race the caller can SEE into a new race they OWN — its checkpoints, its field and the
    /// imported split times, over the SAME course/reference/rider tracks (the activities are shared, never
    /// duplicated). Named "&lt;source&gt; (copy)". Null when the source isn't visible. Un-viewable real
    /// riders are dropped from the copy, not carried, because the read re-authorizes the field.
    /// </summary>
    Task<Race?> DuplicateRaceAsync(RaceId sourceRaceId);

    /// <summary>
    /// Point the race at the real effort its cost curve is fitted from. Null clears it (fall back to the
    /// course). The effort need only be VIEWABLE — fitting a curve from a friend's ride is reading it,
    /// not owning it. See <see cref="Race.ReferenceTrackId"/>.
    /// </summary>
    Task<RaceWriteResult> SetReferenceEffortAsync(RaceId raceId, TrackId? referenceTrackId);

    /// <summary>
    /// Set who may EDIT the race — <c>owner</c> | <c>friends</c> | <c>public</c>. Owner-only: opening a
    /// race up (or closing it) is the owner's call, not a collaborator's. See <see cref="Models.Race.EditPolicy"/>.
    /// </summary>
    Task<RaceWriteResult> SetEditPolicyAsync(RaceId raceId, string policy);

    /// <summary>Delete a race. Checkpoints, participants and split times cascade.</summary>
    Task<RaceWriteResult> DeleteRaceAsync(RaceId raceId);
}

/// <inheritdoc />
public class RaceCrud : IRaceCrud
{
    private readonly RouteBaseDbContext _context;
    private readonly IRaceAccess _access;
    private readonly IRaceCourseService _courses;

    private readonly TimeProvider _clock;

    public RaceCrud(RouteBaseDbContext context, IRaceAccess access, IRaceCourseService courses, TimeProvider clock)
    {
        _clock = clock;
        _context = context;
        _access = access;
        _courses = courses;
    }

    // Counts are projected in SQL, never Included: the list page wants one number per race, and an
    // Include would drag 424 participant rows across the wire to produce it.
    public async Task<List<RaceSummaryDto>> GetRacesAsync()
    {
        // AUTHZ-P2: a race the caller can SEE does not authorize its COURSE's geometry — CourseTrackId is a
        // pointer, not a grant, exactly as the replay treats it (RaceReplayService.GetReplayAsync re-gates
        // the course on CanViewActivityAsync — the AUTHZ-P1 fix). Without a matching gate here, a
        // friends-race built over the owner's OWN private ride leaks that ride's track_id + total distance to
        // any race viewer even though the replay 404s. Gate both course fields so the card and the replay
        // agree: the viewer who cannot see the course gets a course-less card; the owner still gets it.
        //
        // Mirror CanViewActivityAsync EXACTLY — owner in ANY lifecycle OR (released AND visible-to-others) —
        // by OR-ing the two composable seams that already encode those halves (OwnedActivities is
        // owner-only/any-lifecycle; VisibleActivities is released + own/public/friends). Reusing them keeps
        // the friends-membership predicate in ONE place instead of hand-inlining it here.
        var ownedCourses = _access.OwnedActivities();
        var visibleCourses = _access.VisibleActivities();
        return await _access.VisibleRaces()
            .OrderByDescending(r => r.RaceDate)
            .Select(r => new RaceSummaryDto
            {
                RaceId = r.RaceId.Value,
                RaceName = r.RaceName,
                // Client-evaluated (final projection): ToWallClockIso does not translate to SQL.
                RaceDate = r.RaceDate.ToWallClockIso(),
                Visibility = r.Visibility,
                CheckpointCount = r.Checkpoints.Count,
                // SVC-13 (accepted): raw count, NOT re-authorized per rider the way GetRaceAsync's field is
                // (RaceAccess.GetRaceAsync drops a participant whose friendship was revoked since they were
                // added). A one-bit leak — "N people are in this race" — for a race the caller can already
                // see, in exchange for not loading every race's whole field just to count it. Deliberate.
                ParticipantCount = r.Participants.Count,
                // Course id + length only when the VIEWER may see that activity; else null (course-less card).
                // Race has no Course navigation (the FK is HasOne<Activity>() nav-less), so these are pulled
                // with correlated subqueries over the viewable set — one number per race, translated to SQL.
                CourseTrackId = ownedCourses.Any(a => a.TrackId == r.CourseTrackId)
                                || visibleCourses.Any(a => a.TrackId == r.CourseTrackId)
                    ? r.CourseTrackId.Value
                    : (int?)null,
                // The RACE's length (Finish − Start), not the ride's: those are different numbers the moment
                // the endpoints are moved, and Activity.TotalDistance is a different METRIC besides (the
                // source's own scalar, ~0.5% off the geometry every chainage is measured in). Two aggregates
                // over Race_Checkpoint, translated to SQL — no course grid per row.
                //
                // Still gated on course visibility, for the AUTHZ-P2 reason above: with the default endpoints
                // this number IS the private ride's length, so ungating it would re-open exactly the leak
                // CourseTrackId is withheld to close.
                RaceDistanceMeters =
                    ownedCourses.Any(a => a.TrackId == r.CourseTrackId)
                    || visibleCourses.Any(a => a.TrackId == r.CourseTrackId)
                        ? r.Checkpoints.Max(c => (double?)c.ChainageMeters)
                          - r.Checkpoints.Min(c => (double?)c.ChainageMeters)
                        : null,
            })
            .ToListAsync();
    }

    public Task<Race?> GetRaceAsync(RaceId raceId) => _access.GetRaceAsync(raceId);

    public async Task<Race?> CreateRaceAsync(
        TrackId courseTrackId, string raceName, DateTime? raceDate, string visibility)
    {
        // Courses come out of your own editor — you may race over a friend's ride, but you may not
        // define a course from it.
        if (!await _access.OwnedActivities().AnyAsync(a => a.TrackId == courseTrackId)) return null;

        // A course with no geometry is not a course, and a race over one is born unusable rather than
        // merely empty: Start and Finish would BOTH sit at chainage 0, and every invariant in this slice is
        // stated in terms of chainage extremes. IsEndpoint (MinBy || MaxBy) stops recognising Finish — so it
        // becomes deletable; AddCheckpointAsync measures against a "finish" at 0 and refuses EVERY
        // checkpoint; and the replay snaps Finish to node 0, where anchorsFor drops it and the field comes
        // back empty. Refuse it here, while the user can still pick another ride.
        //
        // The GRID is the test, not Activity.TotalDistance: it is null for exactly the unraceable cases (no
        // GPS, or shorter than one grid step), and it is the thing the race actually needs.
        var grid = await _courses.GetCourseGridAsync(courseTrackId);
        if (grid == null) return null;

        var race = new Race
        {
            RaceName = raceName,
            RaceDate = raceDate,
            CourseTrackId = courseTrackId,
            // Race.UserId is non-nullable since J3 — an unauthenticated create is a caller bug.
            UserId = _access.CurrentUserId() ?? throw new InvalidOperationException("race create requires a user"),
            Visibility = visibility,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _context.Races.Add(race);
        await _context.SaveChangesAsync();

        // Every race has these two. The model normalizes between consecutive checkpoints, so without
        // them there is no interval at all and nothing can be simulated.
        //
        // Chainage is metres along the COURSE GRID, always — the one coordinate system the map, the profile,
        // the pacing anchors and the derived splits all position against. Activity.TotalDistance is NOT that
        // number: it is the source's own scalar (Strava's, on an import), and raw haversine geometry runs
        // ~0.5% longer. Seeded from it, the Finish sat 337 m inside the ride on a 70 km course — and the
        // course polyline, drawn leg by leg, simply stopped there. Default endpoints span the whole ride;
        // the user moves them to where the lines actually were.
        var courseLength = grid.TotalMeters;   // > 0: a grid shorter than one step is null, guarded above
        _context.RaceCheckpoints.AddRange(
            new RaceCheckpoint { RaceId = race.RaceId, Seq = 0, CheckpointName = RaceCheckpointDefaults.Start, ChainageMeters = 0 },
            new RaceCheckpoint { RaceId = race.RaceId, Seq = 1, CheckpointName = RaceCheckpointDefaults.Finish, ChainageMeters = courseLength });
        await _context.SaveChangesAsync();

        return await _access.GetRaceAsync(race.RaceId);
    }

    public async Task<Race?> DuplicateRaceAsync(RaceId sourceRaceId)
    {
        // Any race the caller can SEE may be copied — the copy is theirs. GetRaceAsync re-authorizes the
        // field, so un-viewable real riders never ride into someone else's copy.
        var source = await _access.GetRaceAsync(sourceRaceId);
        if (source == null) return null;

        // AUTHZ-P1: but the COURSE must pass the same bar CreateRaceAsync sets — you may race over a
        // friend's ride, not define (or inherit) a course from it. The copy is owned by the caller, so
        // without this check a visible race hands its owner a permanent, self-visible pointer to another
        // user's activity geometry. The read path re-gates too; this stops the row from existing.
        if (!await _access.OwnedActivities().AnyAsync(a => a.TrackId == source.CourseTrackId)) return null;

        // Split times are the imported results, and no read path Includes them — load them explicitly.
        // Scoped by the participant's race (a nav join), not an id list, so a large field can't blow the
        // SQLite 999-parameter cap.
        var splits = await _context.RaceSplitTimes
            .AsNoTracking()
            .Where(s => s.Participant!.RaceId == sourceRaceId)
            .ToListAsync();

        await using var tx = await _context.Database.BeginTransactionAsync();

        var copy = new Race
        {
            RaceName = CopyName(source.RaceName),
            RaceDate = source.RaceDate,
            CourseTrackId = source.CourseTrackId,       // the ride is shared, never duplicated
            ReferenceTrackId = source.ReferenceTrackId,
            UserId = _access.CurrentUserId() ?? throw new InvalidOperationException("race copy requires a user"), // the copy is the caller's, not the source owner's
            Visibility = source.Visibility,
            // AC-01: the copy does NOT inherit the source's edit policy. Edit ⊆ view, and the new owner
            // never chose to open the copy up — inheriting a friends/public policy would hand out edit
            // rights over a race its owner never shared. Owner-edit until they re-open it themselves.
            EditPolicy = RaceEditPolicy.Owner,
            Notes = source.Notes,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _context.Races.Add(copy);
        await _context.SaveChangesAsync();              // assigns copy.RaceId

        // Old id → new row, so each split's TWO FKs (participant AND checkpoint) can be remapped below.
        // Seq/chainage are copied verbatim, so the (RaceId, Seq) unique index holds under the fresh RaceId
        // with no renumbering.
        var checkpointMap = new Dictionary<CheckpointId, RaceCheckpoint>();
        foreach (var c in source.Checkpoints)
        {
            var cc = new RaceCheckpoint
            {
                RaceId = copy.RaceId,
                Seq = c.Seq,
                CheckpointName = c.CheckpointName,
                ChainageMeters = c.ChainageMeters,
                LegName = c.LegName,
                LegColor = c.LegColor,
            };
            checkpointMap[c.CheckpointId] = cc;
            _context.RaceCheckpoints.Add(cc);
        }

        var participantMap = new Dictionary<ParticipantId, RaceParticipant>();
        foreach (var p in source.Participants)
        {
            var pp = new RaceParticipant
            {
                RaceId = copy.RaceId,
                Kind = p.Kind,
                DisplayName = p.DisplayName,
                Bib = p.Bib,
                Category = p.Category,
                Gender = p.Gender,
                Club = p.Club,
                TrackId = p.TrackId,                    // a real rider's ride is shared, not duplicated
                Color = p.Color,
                Emphasised = p.Emphasised,
                CalloutOff = p.CalloutOff,
                Seed = p.Seed,
                OfficialFinishSeconds = p.OfficialFinishSeconds,
            };
            participantMap[p.ParticipantId] = pp;
            _context.RaceParticipants.Add(pp);
        }
        await _context.SaveChangesAsync();              // assigns checkpoint + participant ids

        foreach (var s in splits)
        {
            // A split whose participant was dropped on re-authorization has nowhere to land — skip it.
            // (Real riders carry no imported splits, so in practice every split maps.)
            if (!participantMap.TryGetValue(s.ParticipantId, out var participant)) continue;
            if (!checkpointMap.TryGetValue(s.CheckpointId, out var checkpoint)) continue;
            _context.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = participant.ParticipantId,
                CheckpointId = checkpoint.CheckpointId,
                ElapsedSeconds = s.ElapsedSeconds,
            });
        }
        await _context.SaveChangesAsync();

        await tx.CommitAsync();
        return await _access.GetRaceAsync(copy.RaceId);
    }

    // "<name> (copy)", clamped to the race_name column cap. The results file often carries no name, so a
    // copy of an unnamed race stays grep-ably a copy rather than becoming another blank row.
    private static string CopyName(string? sourceName)
    {
        const string suffix = " (copy)";
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Race" : sourceName;
        var maxBase = 255 - suffix.Length;
        if (baseName.Length > maxBase) baseName = baseName[..maxBase];
        return baseName + suffix;
    }

    public async Task<RaceWriteResult> SetReferenceEffortAsync(RaceId raceId, TrackId? referenceTrackId)
    {
        // The pacing reference is field setup, not ownership — a collaborator may set it (edit-gated).
        var race = await _access.EditableRaceAsync(raceId);
        if (race == null) return RaceWriteResult.RaceNotFound;

        if (referenceTrackId is TrackId trackId && !await _access.CanViewActivityAsync(trackId))
            return RaceWriteResult.ActivityNotVisible;

        race.ReferenceTrackId = referenceTrackId;
        await _context.SaveChangesAsync();
        return RaceWriteResult.Ok;
    }

    public async Task<RaceWriteResult> SetEditPolicyAsync(RaceId raceId, string policy)
    {
        // Validate before the DB round-trip: a bad value is the caller's bug, not a missing race.
        if (!RaceEditPolicy.IsValid(policy)) return RaceWriteResult.InvalidEditPolicy;

        var race = await _access.OwnedRaceAsync(raceId);   // owner-only, deliberately not EditableRaceAsync
        if (race == null) return RaceWriteResult.RaceNotFound;

        race.EditPolicy = policy;
        await _context.SaveChangesAsync();
        return RaceWriteResult.Ok;
    }

    public async Task<RaceWriteResult> DeleteRaceAsync(RaceId raceId)
    {
        var race = await _access.OwnedRaceAsync(raceId);
        if (race == null) return RaceWriteResult.RaceNotFound;

        _context.Races.Remove(race);
        await _context.SaveChangesAsync();
        return RaceWriteResult.Ok;
    }
}
