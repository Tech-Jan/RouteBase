using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Pure coordinate shaping over a decoded track (mirrors the retired ROW_NUMBER SQL bodies).
/// Extracted from <see cref="RouteBaseWeb.Services.ActivityGpsService"/> because that service gates every
/// read on <c>VisibleActivities</c> and is therefore unusable from the share path — which must decode via
/// <see cref="ITrackReader"/> behind its own gate, then shape the result identically. Two copies of the
/// stride math would drift, and a share map that disagrees with the owner's map is a bug nobody would
/// look for here.
///
/// Static and identity-free by design: shaping is not an authorization decision.
/// </summary>
public static class TrackCoordinates
{
    /// <summary>Every point, NULL coord → 0 (the full-resolution single-track shape has no non-NULL filter).</summary>
    public static List<GpsCoordinate> NullToZero(DecodedTrack? track)
    {
        if (track == null) return new List<GpsCoordinate>();
        var result = new List<GpsCoordinate>(track.Count);
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], ln = track.Lng[i];
            result.Add(new GpsCoordinate
            {
                Lat   = double.IsNaN(la) ? 0 : la,
                Lng   = double.IsNaN(ln) ? 0 : ln,
                GpsId = i
            });
        }
        return result;
    }

    /// <summary>Only points with non-NULL lat AND lng (the bulk no-limit shape filtered them in SQL).</summary>
    public static List<GpsCoordinate> Filtered(DecodedTrack track)
    {
        var result = new List<GpsCoordinate>(track.Count);
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], ln = track.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(ln)) continue;
            result.Add(new GpsCoordinate { Lat = la, Lng = ln, GpsId = i });
        }
        return result;
    }

    /// <summary>
    /// Stride over the non-NULL-coord subsequence, replicating the SQL exactly:
    ///   ROW_NUMBER()/COUNT() ran over rows with GPS_lat AND GPS_long NOT NULL (ordered by GPS_ID);
    ///   a row survived when (rn-1) % MAX(1, cnt / maxPoints) == 0, with integer division as in SQLite.
    /// </summary>
    public static List<GpsCoordinate> Downsample(DecodedTrack? track, int maxPoints)
    {
        if (track == null) return new List<GpsCoordinate>();

        // First pass: the non-NULL-coord subsequence's length is the SQL COUNT(*) OVER ().
        int cnt = 0;
        for (int i = 0; i < track.Count; i++)
            if (!double.IsNaN(track.Lat[i]) && !double.IsNaN(track.Lng[i])) cnt++;
        if (cnt == 0) return new List<GpsCoordinate>();

        int stride = Math.Max(1, cnt / maxPoints);
        var result = new List<GpsCoordinate>(cnt / stride + 1);
        int k = 0; // 0-based position within the filtered subsequence (rn - 1)
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], ln = track.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(ln)) continue;
            if (k % stride == 0)
                result.Add(new GpsCoordinate { Lat = la, Lng = ln, GpsId = i });
            k++;
        }
        return result;
    }
}
