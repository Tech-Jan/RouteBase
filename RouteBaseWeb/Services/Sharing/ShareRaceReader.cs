using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Owner-only race reads for the share path. See <see cref="IShareRaceReader"/> for why this exists
/// rather than reusing <see cref="RaceReplayService"/> under a widened identity.
///
/// Three structural properties, all pinned by tests:
///
/// 1. It injects NEITHER <c>IVisibilityService</c> NOR <c>ICurrentUserService</c> — nor
///    <c>IRaceAccess</c>, which holds one. There is no ambient identity here to get wrong; the authority is
///    the <see cref="ShareGrant"/>, passed in as data. Reaching for <c>VisibleActivities</c> to "fix a
///    missing rider" would require a constructor dependency, which fails the build.
///
/// 2. It projects into <see cref="ShareRaceReplayPayload"/>, its own record family — so what a visitor
///    receives is decided in ONE file rather than by remembering to blank fields here. The entrants'
///    identity columns travel (decided 2026-07-24: the start list is the event's own published
///    record); what does not travel is anyone else's GPS, which is rule 3 plus the whole-drop in
///    ProjectFieldAsync.
///
/// 3. Every activity it touches is re-gated at READ time on owner + Released. Mint checks only that the RACE
///    is owned — not its course or its riders' rides — and a race legitimately points at activities the race
///    owner does not own: the pacing reference is set from any merely-VIEWABLE track (RaceCrud
///    .SetReferenceEffortAsync via CanViewActivityAsync), and rows minted before AUTHZ-P1 (2026-07-29, which
///    now owner-checks the course in DuplicateRaceAsync) may still carry a non-owned course. So this gate is
///    the belt-and-braces that stops a non-owned or non-Released ride republishing on a public link,
///    covering both the live reference-effort case and pre-fix legacy rows. Do NOT relax it to VisibleActivities.
///
/// No share-specific cache family, unlike ShareSegmentReader — deliberately. The two caches on this path
/// (the course grid and the cost curve) are keyed by track id + CONTENT sha with no identity in the key, so
/// a share read and an owner read of the same bytes are the same question. The segment reader needed its
/// own namespace because SegmentEfforts consults its cache BEFORE its visibility gate; nothing here does.
/// </summary>
public sealed class ShareRaceReader(
    RouteBaseDbContext db,
    IRaceCourseService courses,
    IRaceRealParticipantService real,
    IRaceCostCurveService curves,
    TrackContentStore content) : IShareRaceReader
{
    /// <summary>
    /// The granted race. Ownership is re-asserted at READ time: MintAsync guarantees owner == race.UserId
    /// at mint, but nothing re-checks it afterwards and Race.UserId is nullable, so an orphaned race must
    /// fall out of every live link.
    ///
    /// NOT VisibleRaces — that predicate answers "what may the current user see", which under
    /// <c>share:anon</c> would admit every PUBLIC race in the database.
    /// </summary>
    private IQueryable<Race> GrantedRace(ShareGrant g) => db.Races.AsNoTracking()
        .Where(r => r.RaceId == new RaceId(g.ResourceId) && r.UserId == g.OwnerUserId);

    /// <summary>
    /// Activities the OWNER may publish through this link: their own, and Released.
    ///
    /// The lifecycle clause is restated explicitly because we deliberately do not go through
    /// VisibleActivities, which carries it — an owner's trashed, parked or awaiting-review ride must not
    /// surface on a public link. This is the line a future contributor must not "fix" into VisibleActivities.
    /// </summary>
    private IQueryable<Activity> PublishableActivities(ShareGrant g) => db.Activities.AsNoTracking()
        .Where(a => a.UserId == g.OwnerUserId && a.Lifecycle == LifecycleStates.Released);

    private Task<bool> IsPublishableAsync(ShareGrant g, TrackId trackId, CancellationToken ct) =>
        PublishableActivities(g).AnyAsync(a => a.TrackId == trackId, ct);

    private static bool WrongKind(ShareGrant g) => g.Kind != ShareKind.Race;

    public async Task<ShareRaceHeader?> GetHeaderAsync(ShareGrant g, CancellationToken ct = default)
    {
        var servable = await ServableAsync(g, ct);
        return servable is null
            ? null
            : new ShareRaceHeader(servable.Value.Race.RaceId.Value, servable.Value.Race.RaceName);
    }

    /// <summary>
    /// Every gate that decides whether this grant serves ANYTHING, in one place — so the page's 404 and the
    /// endpoint's 404 are the same question asked once, not two predicates that can drift apart.
    /// </summary>
    private async Task<(Race Race, RaceCourseGrid Course)?> ServableAsync(ShareGrant g, CancellationToken ct)
    {
        if (WrongKind(g)) return null;

        var race = await GrantedRace(g).FirstOrDefaultAsync(ct);
        if (race == null) return null;

        // THE course gate. A race the owner owns can still be built over someone else's activity (a
        // duplicate), and the course grid IS that activity's geometry — publishing it would republish the
        // ride. Indistinguishable from "no such race" to the caller.
        if (!await IsPublishableAsync(g, race.CourseTrackId, ct)) return null;

        // Cached by track id + content sha, so the page's gate and the client's fetch build it once.
        var course = await courses.GetCourseGridAsync(race.CourseTrackId, ct);
        return course == null ? null : (race, course);
    }

    public async Task<ShareRaceReplayPayload?> GetReplayAsync(ShareGrant g, CancellationToken ct = default)
    {
        var servable = await ServableAsync(g, ct);
        if (servable is null) return null;
        var (race, course) = servable.Value;

        // Same fallback rule as the authenticated read (RaceReplayService), with the reference effort
        // re-gated the share way: a reference ride the owner does not own (or has trashed) falls back to
        // the course rather than being fitted. The curve is a handful of seconds-per-metre numbers, not
        // geometry — but it is still DERIVED from a ride, so it gets the same gate.
        var effortTrackId = race.ReferenceTrackId ?? race.CourseTrackId;
        if (effortTrackId != race.CourseTrackId && !await IsPublishableAsync(g, effortTrackId, ct))
            effortTrackId = race.CourseTrackId;

        var effortSha = await content.GetHeadShaAsync(effortTrackId, ct);
        var curve = await curves.FitFromEffortAsync(effortTrackId, effortSha, course, race.CourseTrackId, ct);

        var checkpoints = await db.RaceCheckpoints.AsNoTracking()
            .Where(c => c.RaceId == race.RaceId)
            .OrderBy(c => c.Seq)
            .ToListAsync(ct);

        var participants = await ProjectFieldAsync(g, race, course, checkpoints, ct);

        return new ShareRaceReplayPayload
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
    /// The field, filtered by whose GEOMETRY it is.
    ///
    /// Simulated entrants ride the shared course grid off their split times and carry no geometry of their
    /// own, so they travel whole — identity columns included: that start list is the event's own published
    /// record (decided 2026-07-24). A REAL rider is different: he ships his own GPS, and that GPS
    /// is his, not the sharer's. So a real rider survives only when his ride is one the owner could publish
    /// on its own; everyone else's is dropped WHOLE — participant, splits and all — rather than shipped
    /// without a track, because a half-present rider is a leak waiting for someone to "restore" the
    /// missing half.
    /// </summary>
    private async Task<List<ShareRaceParticipantDto>> ProjectFieldAsync(
        ShareGrant g, Race race, RaceCourseGrid course,
        List<RaceCheckpoint> checkpoints, CancellationToken ct)
    {
        var rows = await db.RaceParticipants.AsNoTracking()
            .Where(p => p.RaceId == race.RaceId)
            .OrderBy(p => p.ParticipantId)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Filter by RACE, not by an id list: a 1,000-finisher field is past SQLite's variable ceiling.
        // Same shape (and the same EXPLAIN-verified join) as RaceReplayService.
        var splitsByParticipant = (await db.RaceSplitTimes.AsNoTracking()
                .Join(db.RaceParticipants.Where(p => p.RaceId == race.RaceId),
                      s => s.ParticipantId, p => p.ParticipantId, (s, _) => s)
                .ToListAsync(ct))
            .ToLookup(s => s.ParticipantId);

        var seqById = checkpoints.ToDictionary(c => c.CheckpointId, c => c.Seq);

        // Resolved in ONE query rather than per rider: a real field is single digits, but the query count
        // should not scale with it either way.
        var realTrackIds = rows
            .Where(p => p.Kind == RaceParticipantKind.Real && p.TrackId.HasValue)
            .Select(p => p.TrackId!.Value)
            .Distinct()
            .ToList();
        var publishableTracks = new HashSet<TrackId>();
        if (realTrackIds.Count > 0)
            publishableTracks = (await PublishableActivities(g)
                .Where(a => realTrackIds.Contains(a.TrackId))
                .Select(a => a.TrackId)
                .ToListAsync(ct)).ToHashSet();

        var result = new List<ShareRaceParticipantDto>(rows.Count);
        foreach (var p in rows)
        {
            var isReal = p.Kind == RaceParticipantKind.Real;
            if (isReal && (p.TrackId is not TrackId trackId || !publishableTracks.Contains(new TrackId(trackId.Value))))
                continue;   // someone else's ride — not this owner's to publish

            var splits = OrderSplits(
                splitsByParticipant[p.ParticipantId].Select(s => (s.CheckpointId, s.ElapsedSeconds)), seqById);

            RaceRealTrackDto? track = null;
            if (isReal)
            {
                var projected = await real.ProjectAsync(
                    p.TrackId!.Value, course, checkpoints, p.ParticipantId, ct: ct);
                if (projected == null) continue;   // his ride lost its GPS — he cannot race

                track = new RaceRealTrackDto
                {
                    Lat = projected.Lat,
                    Lng = projected.Lng,
                    ElapsedSeconds = projected.ElapsedSeconds,
                    ChainageMeters = projected.ChainageMeters,
                };
                // Derived from his GPS, exactly as on the owner's page — so he ranks against the field
                // without anyone having typed his times in.
                splits = OrderSplits(
                    projected.Splits.Select(s => (s.CheckpointId, s.ElapsedSeconds)), seqById);
            }

            result.Add(new ShareRaceParticipantDto
            {
                ParticipantId = p.ParticipantId.Value,
                Kind = p.Kind,
                Name = p.DisplayName,
                Bib = p.Bib,
                Category = p.Category,
                Gender = p.Gender,
                Club = p.Club,
                Seed = p.Seed,
                Color = p.Color,
                Emphasised = p.Emphasised == true,
                CalloutOff = p.CalloutOff == true,
                OfficialFinishSeconds = FiniteOrNull(p.OfficialFinishSeconds),
                // Same validity gate as RaceReplayService: a token this build does not know must not
                // reach a browser whose type says it cannot exist.
                FinishStatus = RaceFinishStatusVocabulary.IsValid(p.FinishStatus) ? p.FinishStatus : null,
                FinishStatusRaw = p.FinishStatusRaw,
                StartOffsetSeconds = FiniteOrNull(p.StartOffsetSeconds),
                Wave = p.Wave,
                Discipline = p.Discipline,
                Splits = splits,
                Track = track,
            });
        }
        return result;
    }

    /// <summary>
    /// Splits in checkpoint-seq order, dropping any whose checkpoint is gone or whose value is non-finite.
    /// The non-finite guard is the same last-gate-before-the-wire as RaceReplayService's: System.Text.Json
    /// throws MID-BODY on a NaN, which is HTTP 200 plus a truncated body — and on THIS surface the client
    /// has no signed-in fallback to fall back to.
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

    private static double? FiniteOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;
}
