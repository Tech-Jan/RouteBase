using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Pure, I/O-free photo→activity matcher (Phase-1 §3/§3a). <b>Time containment is the only auto-assign
/// signal</b>; GPS is used solely to find, annotate, and position candidates — never to auto-decide.
/// The caller (PhotoImportOrchestrator) resolves the photo's capture UTC, materializes candidate
/// activities + track samples, and does all I/O; this class is unit-testable without mocks.
/// </summary>
public static class PhotoMatchService
{
    /// <summary>Max distance (m) from a photo's EXIF point to any track point for that activity to be
    /// offered as a GPS-only candidate when time matching fails (§3a). Generous by design — it only
    /// builds a pick-list the user confirms, never an auto-assign.</summary>
    public const double GpsCandidateRadiusMeters = 250;

    /// <summary>Decide the outcome for one photo against the supplied candidate activities.</summary>
    public static PhotoMatchResult Match(
        DateTime? photoUtc, double? photoLat, double? photoLng,
        IReadOnlyList<PhotoMatchActivity> candidates)
    {
        bool hasGps = photoLat.HasValue && photoLng.HasValue;

        // 1) Time containment — the only signal that can auto-assign.
        if (photoUtc is { } utc)
        {
            var timeHits = candidates.Where(c => utc >= c.StartUtc && utc <= c.EndUtc).ToList();

            if (timeHits.Count == 1)
            {
                var only = timeHits[0];
                double? pos = only.Track is { Count: > 0 }
                    ? ResolveAlongTrackPositionM(utc, photoLat, photoLng, only.Track)
                    : null;
                return new PhotoMatchResult.AutoMatch(only.TrackId, pos);
            }

            if (timeHits.Count > 1)
                return new PhotoMatchResult.Ambiguous(
                    timeHits.Select(c => ToCandidate(c, photoLat, photoLng)).ToList());
        }

        // 2) No single time match. If the photo has GPS, offer activities whose track passes near it.
        if (hasGps)
        {
            var near = candidates
                .Select(c => (c, dist: NearestDistanceM(photoLat!.Value, photoLng!.Value, c.Track)))
                .Where(x => x.dist is { } d && d <= GpsCandidateRadiusMeters)
                .OrderBy(x => x.dist)
                .Select(x => new PhotoMatchCandidate(x.c.TrackId, x.c.Name, x.c.StartUtc, x.c.TzOffsetHours, x.dist))
                .ToList();

            if (near.Count > 0)
                return new PhotoMatchResult.GpsCandidates(near);
        }

        // 3) Nothing usable → manual assign.
        return new PhotoMatchResult.Unmatched();
    }

    /// <summary>Along-track distance (m) for the map marker: the nearest track point when the photo has
    /// EXIF GPS, else linear interpolation by capture timestamp (WhatsApp case). Null if the track is empty
    /// or there's neither GPS nor time.</summary>
    public static double? ResolveAlongTrackPositionM(
        DateTime? photoUtc, double? photoLat, double? photoLng, IReadOnlyList<TrackSample> track)
    {
        if (track.Count == 0) return null;

        if (photoLat.HasValue && photoLng.HasValue)
        {
            int idx = photoUtc is { } utc
                ? NearestIndexNearTime(photoLat.Value, photoLng.Value, ToEpochS(utc), track)
                : NearestIndex(photoLat.Value, photoLng.Value, track);
            return idx >= 0 ? track[idx].DistanceM : null;
        }

        return photoUtc is { } utc2 ? InterpolateByTime(utc2, track)?.DistanceM : null;
    }

    /// <summary>Points recorded within this many seconds of the photo's capture time are treated as the
    /// "plausible pass" for spatial nearest-point search — disambiguates out-and-back/loop routes where the
    /// literal nearest point can belong to a different lap. Generous vs. typical GPS/EXIF timestamp skew.</summary>
    private const long NearestPointTimeWindowS = 600; // ±10 min

    // Nearest-by-space restricted to points within ±NearestPointTimeWindowS of `epochS`; falls back to the
    // unrestricted full-track search when the window is empty (e.g. sparse/irregular sampling) — same
    // behavior as before for tracks where time-windowing can't help.
    private static int NearestIndexNearTime(double lat, double lng, long epochS, IReadOnlyList<TrackSample> track)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < track.Count; i++)
        {
            if (Math.Abs(track[i].TimeEpochS - epochS) > NearestPointTimeWindowS) continue;
            double d = GeoUtils.HaversineDistance(lat, lng, track[i].Lat, track[i].Lng);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best >= 0 ? best : NearestIndex(lat, lng, track);
    }

    /// <summary>Interpolated marker lat/lng at the photo's capture time — the no-GPS/WhatsApp placement
    /// path (§4). Null when there's no capture time or the track is empty.</summary>
    public static (double Lat, double Lng)? InterpolateLatLngAtTime(
        DateTime? photoUtc, IReadOnlyList<TrackSample> track)
    {
        if (photoUtc is not { } utc) return null;
        return InterpolateByTime(utc, track) is { } s ? (s.Lat, s.Lng) : null;
    }

    /// <summary>Resolve a photo's single stored map location + its provenance, once at write time:
    /// EXIF GPS wins, else timestamp-interpolation along the track, else none. Manual placement is set
    /// directly by the caller (source "manual") and isn't produced here.</summary>
    public static (double? Lat, double? Lng, string Source) ResolveLocation(
        bool hasExifGps, double? exifLat, double? exifLng, DateTime? photoUtc, IReadOnlyList<TrackSample> track)
    {
        if (hasExifGps && exifLat is { } la && exifLng is { } lo)
            return (la, lo, PhotoLocationSources.Exif);
        if (InterpolateLatLngAtTime(photoUtc, track) is { } ll)
            return (ll.Lat, ll.Lng, PhotoLocationSources.Interpolated);
        return (null, null, PhotoLocationSources.None);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PhotoMatchCandidate ToCandidate(PhotoMatchActivity a, double? lat, double? lng)
    {
        double? dist = lat.HasValue && lng.HasValue ? NearestDistanceM(lat.Value, lng.Value, a.Track) : null;
        return new PhotoMatchCandidate(a.TrackId, a.Name, a.StartUtc, a.TzOffsetHours, dist);
    }

    private static double? NearestDistanceM(double lat, double lng, IReadOnlyList<TrackSample>? track)
    {
        if (track is null || track.Count == 0) return null;
        int idx = NearestIndex(lat, lng, track);
        return idx >= 0 ? GeoUtils.HaversineDistance(lat, lng, track[idx].Lat, track[idx].Lng) : null;
    }

    private static int NearestIndex(double lat, double lng, IReadOnlyList<TrackSample> track)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < track.Count; i++)
        {
            double d = GeoUtils.HaversineDistance(lat, lng, track[i].Lat, track[i].Lng);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // Linear interpolation of a TrackSample at instant `utc`. Track is ordered by time ascending; clamps
    // to the endpoints outside the recorded window.
    private static TrackSample? InterpolateByTime(DateTime utc, IReadOnlyList<TrackSample> track)
    {
        if (track.Count == 0) return null;
        long t = ToEpochS(utc);

        if (t <= track[0].TimeEpochS) return track[0];
        if (t >= track[^1].TimeEpochS) return track[^1];

        for (int i = 1; i < track.Count; i++)
        {
            var a = track[i - 1];
            var b = track[i];
            if (t >= a.TimeEpochS && t <= b.TimeEpochS)
            {
                long span = b.TimeEpochS - a.TimeEpochS;
                double f = span <= 0 ? 0 : (double)(t - a.TimeEpochS) / span;
                return new TrackSample(
                    t,
                    a.Lat + (b.Lat - a.Lat) * f,
                    a.Lng + (b.Lng - a.Lng) * f,
                    a.DistanceM + (b.DistanceM - a.DistanceM) * f);
            }
        }
        return track[^1];
    }

    private static long ToEpochS(DateTime utc) =>
        (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
}
