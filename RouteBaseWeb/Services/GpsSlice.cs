using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services;

/// <summary>
/// Lightweight GPS projection used by effort aggregation and performance data.
/// Contains only the columns that <see cref="SegmentEfforts"/>'s performance-point build reads.
/// </summary>
internal sealed class GpsSlice
{
    public int       GpsId         { get; init; }
    public int       TrackId       { get; init; }
    public DateTime? DateTimeUtc   { get; init; }
    public double?   Distance      { get; init; }
    public double?   Speed         { get; init; }
    public double?   Altitude      { get; init; }
    public double?   HeartRate     { get; init; }
    public double?   Cadence       { get; init; }
    public double?   Power         { get; init; }
    public double?   GradeSmooth   { get; init; }
    // Lat/Lng only needed for BuildPerformancePoints Haversine fallback.
    public double?   GpsLat        { get; init; }
    public double?   GpsLong       { get; init; }

    /// <summary>
    /// Slices ordinals [lo..hi] (inclusive) of a decoded track into effort points — the single
    /// DecodedTrack → GpsSlice funnel. Per-point NaN (or a whole-NULL channel) maps to null,
    /// matching the old EF projection's NULL semantics. Empty when hi &lt; lo (no points in range).
    /// <paramref name="trackId"/> comes from the caller: a decode is shared content and carries no id.
    /// </summary>
    public static List<GpsSlice> FromDecoded(DecodedTrack track, TrackId trackId, int lo, int hi)
    {
        if (hi < lo) return [];
        var result = new List<GpsSlice>(hi - lo + 1);
        for (int i = lo; i <= hi; i++)
        {
            double tSec = track.TimeEpochS[i];
            double la = track.Lat[i], ln = track.Lng[i];
            result.Add(new GpsSlice
            {
                GpsId       = i,
                TrackId     = trackId.Value,
                DateTimeUtc = double.IsNaN(tSec) ? null : DateTime.UnixEpoch.AddSeconds(tSec),
                Distance    = Channel(track.Distance, i),
                Speed       = Channel(track.Speed, i),
                Altitude    = Channel(track.Altitude, i),
                HeartRate   = Channel(track.HeartRate, i),
                Cadence     = Channel(track.Cadence, i),
                Power       = Channel(track.Power, i),
                GradeSmooth = Channel(track.GradeSmooth, i),
                GpsLat      = double.IsNaN(la) ? null : la,
                GpsLong     = double.IsNaN(ln) ? null : ln,
            });
        }
        return result;
    }

    private static double? Channel(TrackChannel channel, int i)
    {
        if (channel.IsNull) return null;
        double v = channel[i];   // one dequantize, not two (indexer multiplies per read)
        return double.IsNaN(v) ? null : v;
    }
}
