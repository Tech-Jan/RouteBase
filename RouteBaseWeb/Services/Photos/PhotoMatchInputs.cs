using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Photos;

/// <summary>One candidate activity the matcher may assign a photo to — just the fields the pure matcher
/// needs. The caller (PhotoImportOrchestrator) materializes these from Activity metadata and, when GPS
/// refinement / positioning is needed, decoded <c>Track</c> samples; the matcher itself does no I/O.</summary>
public sealed record PhotoMatchActivity(
    int TrackId,
    string Name,
    DateTime StartUtc,
    DateTime EndUtc,
    double? TzOffsetHours,
    IReadOnlyList<TrackSample>? Track);

/// <summary>One decoded track point — the subset of <c>DecodedTrack</c> matching uses: epoch seconds,
/// position, cumulative along-track distance (metres).</summary>
public readonly record struct TrackSample(long TimeEpochS, double Lat, double Lng, double DistanceM);

/// <summary>A candidate offered to the user for a manual pick (ambiguous or GPS-only cases). Carries the
/// GPS distance from the photo when known, so the UI can rank/annotate the hyperlinks (§3a).</summary>
public sealed record PhotoMatchCandidate(
    int TrackId,
    string Name,
    DateTime StartUtc,
    double? TzOffsetHours,
    double? GpsDistanceM);

/// <summary>Outcome of matching a photo to activities (§3b). Only <see cref="AutoMatch"/> is persisted
/// immediately; the candidate variants stay in staging until the user picks.</summary>
public abstract record PhotoMatchResult
{
    /// <summary>Exactly one activity's time range contains the photo. <paramref name="TrackPositionM"/> is
    /// the along-track marker distance when track samples were supplied (else null — computed later).</summary>
    public sealed record AutoMatch(int TrackId, double? TrackPositionM) : PhotoMatchResult;

    /// <summary>Multiple time-range candidates — the user picks one (§3a).</summary>
    public sealed record Ambiguous(IReadOnlyList<PhotoMatchCandidate> Candidates) : PhotoMatchResult;

    /// <summary>No time match, but the photo has GPS and some tracks pass near it — the user picks (§3a).</summary>
    public sealed record GpsCandidates(IReadOnlyList<PhotoMatchCandidate> Candidates) : PhotoMatchResult;

    /// <summary>No usable capture time and no near-GPS candidate — manual assign only (Signal case).</summary>
    public sealed record Unmatched : PhotoMatchResult;
}
