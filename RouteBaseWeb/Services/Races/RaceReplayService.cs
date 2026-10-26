using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Races;

/// <summary>Assembles the replay payload: course + checkpoints + the whole field.</summary>
public interface IRaceReplayService
{
    /// <summary>Null when the race isn't visible to the caller, or its course has no GPS.</summary>
    Task<RaceReplayPayload?> GetReplayAsync(RaceId raceId, CancellationToken ct = default);
}

/// <inheritdoc />
public class RaceReplayService : IRaceReplayService
{
    private readonly RouteBaseDbContext _context;
    private readonly IRaceAccess _access;
    private readonly IRaceCourseService _courses;
    private readonly IRaceRealParticipantService _real;
    private readonly IRaceCostCurveService _curves;
    private readonly TrackContentStore _content;

    public RaceReplayService(
        RouteBaseDbContext context, IRaceAccess access, IRaceCourseService courses,
        IRaceRealParticipantService real, IRaceCostCurveService curves, TrackContentStore content)
    {
        _context = context;
        _access = access;
        _courses = courses;
        _real = real;
        _curves = curves;
        _content = content;
    }

    public async Task<RaceReplayPayload?> GetReplayAsync(RaceId raceId, CancellationToken ct = default)
    {
        // The access seam applies visibility AND re-authorizes every real participant, so a revoked
        // friendship drops that rider here without this service knowing anything about it.
        var race = await _access.GetRaceAsync(raceId);
        if (race == null) return null;

        // AUTHZ-P1: the course id is a pointer, not a grant — the same rule the reference effort below
        // already follows. Race visibility alone does not authorize the course's GPS: DuplicateRaceAsync
        // copies CourseTrackId verbatim into a race the CALLER owns, so without this a user could see any
        // activity's full geometry by duplicating a race built over it. Unlike the reference effort there
        // is no safe fallback — a replay without its course is meaningless — so this fails the read.
        if (!await _access.CanViewActivityAsync(race.CourseTrackId)) return null;

        var course = await _courses.GetCourseGridAsync(race.CourseTrackId, ct);
        if (course == null) return null;

        // Fit the field's terrain response from a REAL effort: the race's reference ride if it names one,
        // else the course activity (the old behaviour — a course is usually a ride you actually did).
        // Null → the browser's default curve.
        //
        // The effort's CONTENT sha goes with its id: the curve is a function of the effort's bytes, not of
        // its track number, so editing the reference ride must move the cache key. Resolved here because
        // this is where identity already lives — the same reason RaceCourseService stamps SourceSha on the
        // grid rather than the pure builder doing it.
        //
        // And it is RE-AUTHORIZED on every read, not merely at the moment it was set. The stored track id
        // is a pointer, not a grant — exactly as it is for a real participant, whom this service's own
        // access seam re-checks on every read. Without this, a reference effort that was a friend's ride
        // went on being fitted (and its terrain response shipped to the browser) after the friendship was
        // revoked, and to viewers who could never see that ride at all. It falls back to the course, which
        // is the same thing a race with no reference does.
        var effortTrackId = race.ReferenceTrackId ?? race.CourseTrackId;
        if (effortTrackId != race.CourseTrackId && !await _access.CanViewActivityAsync(effortTrackId))
            effortTrackId = race.CourseTrackId;

        var effortSha = await _content.GetHeadShaAsync(effortTrackId, ct);
        var curve = await _curves.FitFromEffortAsync(effortTrackId, effortSha, course, race.CourseTrackId, ct);

        var checkpoints = race.Checkpoints.OrderBy(c => c.Seq).ToList();

        // Filter by RACE, not by an id list: a 424-participant IN clause is already near SQLite's
        // variable ceiling, and a 1,000-finisher CSV — an ordinary input for this feature — throws.
        // Rows belonging to a de-authorized real rider are simply never looked up.
        // JOIN, not EXISTS: the EXISTS shape scanned every split row probing participants per row;
        // the join drives from IX_RaceParticipant_RaceId into the splits PK (EXPLAIN-verified).
        // ParticipantId is Race_Participant's PK, so the join cannot duplicate split rows.
        var splitsByParticipant = (await _context.RaceSplitTimes
                .AsNoTracking()
                .Join(_context.RaceParticipants.Where(p => p.RaceId == raceId),
                      s => s.ParticipantId, p => p.ParticipantId, (s, p) => s)
                .ToListAsync(ct))
            .ToLookup(s => s.ParticipantId);

        // Hoisted: the seq lookup and the checkpoint filter were rebuilt once PER PARTICIPANT — 424 hash
        // tables over the same 2-10 rows, and a full rescan of every split row per rider (O(P·S)).
        var seqById = checkpoints.ToDictionary(c => c.CheckpointId, c => c.Seq);

        var participants = new List<RaceParticipantDto>(race.Participants.Count);
        foreach (var p in race.Participants)
        {
            var splits = OrderSplits(
                splitsByParticipant[p.ParticipantId].Select(s => (s.CheckpointId, s.ElapsedSeconds)), seqById);

            RaceRealTrackDto? track = null;
            if (p.Kind == RaceParticipantKind.Real && p.TrackId is TrackId trackId)
            {
                // maxPoints omitted → the default cap. Bounded payload is the DEFAULT, never opt-in.
                var projected = await _real.ProjectAsync(
                    trackId, course, checkpoints, p.ParticipantId, ct: ct);
                if (projected == null) continue;   // his ride lost its GPS — he cannot race

                track = new RaceRealTrackDto
                {
                    Lat = projected.Lat,
                    Lng = projected.Lng,
                    ElapsedSeconds = projected.ElapsedSeconds,
                    ChainageMeters = projected.ChainageMeters,
                };
                // A real rider's splits are DERIVED from his GPS, never read from the table — so he
                // ranks against the field without anyone having typed his times in.
                splits = OrderSplits(
                    projected.Splits.Select(s => (s.CheckpointId, s.ElapsedSeconds)), seqById);
            }

            participants.Add(new RaceParticipantDto
            {
                ParticipantId = p.ParticipantId.Value,
                Kind = p.Kind,
                Name = p.DisplayName,
                Bib = p.Bib,
                Category = p.Category,
                Gender = p.Gender,
                Club = p.Club,
                Color = p.Color,
                Emphasised = p.Emphasised == true,
                CalloutOff = p.CalloutOff == true,
                OfficialFinishSeconds = FiniteOrNull(p.OfficialFinishSeconds),
                // Only a value this build still knows about. A row written by an older/newer schema with
                // an unrecognised token must not reach the browser, where the type says it cannot exist —
                // it degrades to the derived rule, which is what a null already means.
                FinishStatus = RaceFinishStatusVocabulary.IsValid(p.FinishStatus) ? p.FinishStatus : null,
                FinishStatusRaw = p.FinishStatusRaw,
                // A non-finite offset would reach the browser's clock arithmetic and NaN the whole field,
                // the same last-gate-before-the-wire as the splits below.
                StartOffsetSeconds = FiniteOrNull(p.StartOffsetSeconds),
                Wave = p.Wave,
                Discipline = p.Discipline,
                Seed = p.Seed,
                Splits = splits,
                Track = track,
            });
        }

        return new RaceReplayPayload
        {
            RaceId = race.RaceId.Value,
            RaceName = race.RaceName,
            Course = new RaceCourseDto
            {
                Lat = course.Lat,
                Lng = course.Lng,
                Elevation = course.Elevation,
                GradePercent = course.GradePercent,
                StepMeters = course.StepMeters,
                TotalMeters = course.TotalMeters,
            },
            Checkpoints = checkpoints.Select(c => new RaceCheckpointDto
            {
                CheckpointId = c.CheckpointId.Value,
                Seq = c.Seq,
                Name = c.CheckpointName,
                ChainageMeters = c.ChainageMeters,
                // Snap to the grid: the model anchors on NODES, and a chainage between two nodes has
                // no arrival time to hit.
                Node = Math.Clamp(
                    (int)Math.Round(c.ChainageMeters / course.StepMeters), 0, course.Count - 1),
                LegName = c.LegName,
                LegColor = c.LegColor,
            }).ToList(),
            Participants = participants,
            CostCurve = curve?.SecondsPerMetre,
            CostCurveMinBin = curve?.MinBin ?? 0,
        };
    }

    /// <summary>
    /// Splits in checkpoint-seq order. A split whose checkpoint was deleted is dropped — the seq
    /// dictionary IS the filter, exactly as the inner Join it replaces was. OrderBy is stable, so ties
    /// keep their source order.
    ///
    /// A NON-FINITE split is dropped too, and this is the last gate before the wire. The import can no
    /// longer create one (RaceCsvParser rejects a non-finite cell), but rows written BEFORE that fix are
    /// still in the table, and one of them is enough to kill this whole response: System.Text.Json cannot
    /// write a NaN, and it throws mid-body — HTTP 200, truncated, "Failed to fetch". Dropping the row here
    /// is what brings an already-broken race back to life without a migration.
    /// </summary>
    private static List<RaceSplitDto> OrderSplits(
        IEnumerable<(CheckpointId CheckpointId, double ElapsedSeconds)> splits, Dictionary<CheckpointId, int> seqById) =>
        splits
            .Where(s => seqById.ContainsKey(s.CheckpointId) && double.IsFinite(s.ElapsedSeconds))
            .OrderBy(s => seqById[s.CheckpointId])
            .Select(s => new RaceSplitDto
            {
                CheckpointId = s.CheckpointId.Value,
                ElapsedSeconds = s.ElapsedSeconds,
            })
            .ToList();

    /// <summary>The published total travels on the payload as well, and it came from the same parser.</summary>
    private static double? FiniteOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;
}
