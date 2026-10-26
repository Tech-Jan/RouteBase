using System.Runtime.CompilerServices;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// A real rider, ready to race: HIS OWN track, plus the chainage that lets him be ranked against the
/// field. The two are kept apart on purpose — position comes from his GPS, chainage is only a scalar.
/// </summary>
public sealed record RaceRealParticipantTrack
{
    /// <summary>His own coordinates. The marker rides THESE — a detour must stay visible.</summary>
    public required double[] Lat { get; init; }
    public required double[] Lng { get; init; }
    /// <summary>Seconds from his own start.</summary>
    public required double[] ElapsedSeconds { get; init; }
    /// <summary>Metres along the COURSE (projected). Ranking, gaps, checkpoint crossings — never drawing.</summary>
    public required double[] ChainageMeters { get; init; }
    /// <summary>Derived, not imported: when his chainage crossed each checkpoint.</summary>
    public required IReadOnlyList<RaceSplitTime> Splits { get; init; }
}

public interface IRaceRealParticipantService
{
    /// <summary>
    /// Project a real rider's activity onto a race's course. Null when the track has no GPS.
    /// Callers pre-authorize the track id (ITrackReader contract) — RaceCrud does this on add and read.
    ///
    /// <paramref name="maxPoints"/> is a POINT BUDGET, and 0 (or negative) means
    /// <see cref="RaceRealParticipantService.RealTrackPointCap"/> — NOT full resolution. Same inversion
    /// as the segment endpoints (SegmentsApiController "0 = the 2000-pt cap"): the safe path is the
    /// default, so no caller can accidentally ask a weak server for an unbounded full-res decode.
    /// </summary>
    Task<RaceRealParticipantTrack?> ProjectAsync(
        TrackId trackId, RaceCourseGrid course, IReadOnlyList<RaceCheckpoint> checkpoints,
        ParticipantId participantId, int maxPoints = 0, CancellationToken ct = default);
}

/// <inheritdoc />
public class RaceRealParticipantService : IRaceRealParticipantService
{
    private readonly ITrackReader _tracks;

    public RaceRealParticipantService(ITrackReader tracks) => _tracks = tracks;

    /// <summary>
    /// The default point budget for ONE real rider's geometry — the same 2,000 the segment endpoints use.
    /// Uncapped, a 5 h 1 Hz ride shipped 4 × double[18,000] ≈ 1.2 MB per rider, dwarfing the whole 424-strong
    /// simulated field (which shares one course grid). A wrong turn is still plainly visible at 2,000 points.
    /// </summary>
    internal const int RealTrackPointCap = 2000;

    public async Task<RaceRealParticipantTrack?> ProjectAsync(
        TrackId trackId, RaceCourseGrid course, IReadOnlyList<RaceCheckpoint> checkpoints,
        ParticipantId participantId, int maxPoints = 0, CancellationToken ct = default)
    {
        var track = await _tracks.GetTrackAsync(trackId, ct);
        // A ride with no clock cannot be replayed against a field — and indexing a whole-NULL channel throws.
        if (track == null || track.Count < 2 || track.TimeEpochS.IsNull) return null;

        // A point with no fix (or no timestamp) reads as NaN, and a NaN on the wire kills the ENTIRE replay
        // payload, not just this rider: System.Text.Json cannot write one. Dropped before the budget below,
        // so a run of holes spends none of it.
        var usable = new List<int>(track.Count);
        for (var i = 0; i < track.Count; i++)
        {
            if (double.IsFinite(track.Lat[i]) && double.IsFinite(track.Lng[i])
                && double.IsFinite(track.TimeEpochS[i])) usable.Add(i);
        }
        if (usable.Count < 2) return null;

        // 0 (or negative) means THE CAP, not full resolution — see the interface doc.
        var budget = maxPoints > 0 ? Math.Min(maxPoints, RealTrackPointCap) : RealTrackPointCap;
        // Thinned HERE, before chainage: the budget then bounds the wire AND the DTW alignment behind it,
        // and the four channels are consistent by construction (chainage is derived from the kept points).
        var kept = SegmentGeometry.StrideIndices(usable.Count, budget).Select(i => usable[i]).ToList();

        var n = kept.Count;
        var lat = new double[n];
        var lng = new double[n];
        var elapsed = new double[n];
        var t0 = track.TimeEpochS[kept[0]];
        for (var i = 0; i < n; i++)
        {
            var src = kept[i];
            lat[i] = track.Lat[src];
            lng[i] = track.Lng[src];
            elapsed[i] = track.TimeEpochS[src] - t0;
        }

        var chainage = ProjectChainage(lat, lng, course);
        if (chainage == null) return null;

        return new RaceRealParticipantTrack
        {
            Lat = lat,
            Lng = lng,
            ElapsedSeconds = elapsed,
            ChainageMeters = chainage,
            Splits = DeriveSplits(chainage, elapsed, checkpoints, participantId, course.TotalMeters),
        };
    }

    /// <summary>
    /// Metres along the course for each of the rider's points, via the existing DTW linear-referencing
    /// (<see cref="SegmentChainage"/>) — the same machinery segment efforts use.
    ///
    /// Reused rather than rewritten because the hard cases are already solved there: a shortcut that
    /// passes geographically near a LATER part of the course cannot make progress jump ahead, and
    /// self-crossing loops are disambiguated by the rider's own distance progression.
    ///
    /// Note this is exactly why chainage must NOT drive the marker: SegmentChainage deliberately
    /// re-interpolates genuinely off-course spans between their bounding on-course anchors so they
    /// "meet back up smoothly". A detour survives in the GPS; it does not survive in the chainage.
    /// </summary>
    private static double[]? ProjectChainage(double[] lat, double[] lng, RaceCourseGrid course)
    {
        var reference = ReferenceFor(course);
        if (reference == null) return null;

        var n = lat.Length;
        var slices = new List<GpsSlice>(n);
        var points = new List<EffortDataPoint>(n);
        var cumulative = 0.0;
        for (var i = 0; i < n; i++)
        {
            if (i > 0) cumulative += GeoUtils.HaversineDistance(lat[i - 1], lng[i - 1], lat[i], lng[i]);
            slices.Add(new GpsSlice { GpsLat = lat[i], GpsLong = lng[i] });
            points.Add(new EffortDataPoint { Distance = cumulative, Lat = lat[i], Lng = lng[i] });
        }

        SegmentChainage.AssignSegmentProgress(slices, points, reference);

        var total = course.TotalMeters;
        var chainage = new double[n];
        for (var i = 0; i < n; i++) chainage[i] = (points[i].SegmentProgress ?? 0) * total;
        return chainage;
    }

    /// <summary>
    /// The course's DTW reference, built once and shared by every rider projected against that course.
    ///
    /// It was rebuilt per rider per request: a 4,000-node plane projection + uniform resample, thrown away
    /// immediately. Keyed on the GRID INSTANCE, so it inherits the grid's cache key and lifetime exactly
    /// (RaceCourseService caches the grid for 30 min; when that entry expires the grid is collected and
    /// this entry goes with it). Sharing one reference is sanctioned by construction — SegmentChainage
    /// documents it as immutable and cache-shared across concurrent requests (SE-05), which is what
    /// segment efforts already rely on.
    /// </summary>
    private static readonly ConditionalWeakTable<RaceCourseGrid, ReferenceHolder> References = new();

    /// <summary>A null reference (a course too degenerate to align against) must cache as a null, not a miss.</summary>
    private sealed class ReferenceHolder(SegmentChainage.SegmentReference? reference)
    {
        public SegmentChainage.SegmentReference? Reference { get; } = reference;
    }

    private static SegmentChainage.SegmentReference? ReferenceFor(RaceCourseGrid course) =>
        References.GetValue(course, static grid => new ReferenceHolder(
            SegmentChainage.BuildSegmentReference(
                Enumerable.Range(0, grid.Count)
                    .Select(i => new SegmentGpsPoint { Lat = grid.Lat[i], Lng = grid.Lng[i] })
                    .ToList()))).Reference;

    /// <summary>
    /// When the rider's chainage first reached each checkpoint. DERIVED, never imported — a real rider's
    /// splits come out of his GPS, so he ranks and gaps against the field without his geometry being
    /// touched. Checkpoints he never reached (he abandoned) simply get no row.
    ///
    /// <paramref name="courseMeters"/> is the CEILING every target is clamped to, and it is load-bearing:
    /// chainage here is <c>SegmentProgress × course.TotalMeters</c> with progress clamped to [0,1], so
    /// <c>courseMeters</c> is the largest value any rider can ever hold. A checkpoint even a fraction past
    /// it can never be reached, and every real rider is then badged DNF on his own ride.
    ///
    /// Checkpoints are placed in grid metres and bounded by the grid (RaceCheckpointBounds), so today the
    /// clamp only catches a course TRACK edited after its checkpoints were placed — a trim or a splice moves
    /// the grid out from under them. It used to catch far more: the Finish was seeded from
    /// <c>Activity.TotalDistance</c>, a different metric entirely, and on a Strava import that put the line
    /// 337 m off the geometry rather than the sub-step rounding this clamp was written for.
    ///
    /// Clamping cannot invent a split: a rider who stopped short still holds less than the ceiling, which is
    /// what the abandon test pins.
    /// </summary>
    /// <remarks>
    /// See <see cref="ReachSlackMeters"/> for why an exact <c>chainage &gt;= target</c> is not enough.
    /// </remarks>
    /// <summary>
    /// How far short of a checkpoint a rider's projected chainage may peak and still count as reaching it.
    ///
    /// This is NOT slack for an abandon — it is the projection's own error bar. Chainage is
    /// <c>SegmentProgress × TotalMeters</c> off a DTW-resampled reference, and it is accurate to tens of
    /// metres at the ends, not to the metre: <c>Project_OnCourseRider_ChainageTracksTheCourse</c> asserts
    /// the final value only within ±40 m of the total, on a rider who rode the course exactly.
    ///
    /// The FINISH is where that bites, and it bit in production. RaceCheckpointBounds keeps checkpoints
    /// inside the grid, so the clamp above lands the Finish on the grid's LAST node — and reaching it then
    /// demands <c>SegmentProgress == 1.0</c> exactly. A rider who rode the entire course fails that
    /// floating-point knife edge, gets no finish split, and is therefore derived DNF (statusOf's fallback)
    /// and ranked below every simulated finisher, on his own ride.
    ///
    /// An abandon is orders of magnitude clear of 50 m — the abandon test stops at 60% of the course.
    /// </summary>
    private const double ReachSlackMeters = 50;

    private static List<RaceSplitTime> DeriveSplits(
        double[] chainage, double[] elapsed, IReadOnlyList<RaceCheckpoint> checkpoints,
        ParticipantId participantId, double courseMeters)
    {
        var splits = new List<RaceSplitTime>();
        // Chainage can wobble backwards on a detour; a running max makes "first reached" monotone, so a
        // rider cannot re-cross a checkpoint he has already passed.
        foreach (var checkpoint in checkpoints.OrderBy(c => c.Seq))
        {
            var target = Math.Min(checkpoint.ChainageMeters, courseMeters);
            var reached = double.NaN;
            var peak = double.NegativeInfinity;
            var peakAt = -1;
            for (var i = 0; i < chainage.Length; i++)
            {
                if (chainage[i] <= peak) continue;
                if (chainage[i] >= target)
                {
                    reached = i == 0
                        ? elapsed[0]
                        : Interpolate(chainage[i - 1], elapsed[i - 1], chainage[i], elapsed[i], target);
                    break;
                }
                peak = chainage[i];
                peakAt = i;
            }

            // He never crossed it outright — but the projection's error bar is tens of metres, so "never"
            // is not what a peak inside ReachSlackMeters means. His time AT the peak, never an
            // extrapolation past his own last point: it is the moment he was furthest along, which for the
            // Finish is his finish time.
            if (double.IsNaN(reached) && peakAt >= 0 && peak >= target - ReachSlackMeters)
                reached = elapsed[peakAt];

            if (!double.IsNaN(reached))
            {
                splits.Add(new RaceSplitTime
                {
                    ParticipantId = participantId,
                    CheckpointId = checkpoint.CheckpointId,
                    ElapsedSeconds = Math.Max(0, reached),
                });
            }
        }
        return splits;
    }

    private static double Interpolate(double d0, double t0, double d1, double t1, double d)
    {
        var span = d1 - d0;
        return span <= 0 ? t1 : t0 + (d - d0) / span * (t1 - t0);
    }
}
