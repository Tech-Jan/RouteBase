using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>Sensor-channel aggregates over the present samples of a track (whole-NULL channel → nulls).
/// Used for created-activity summaries (§4.5) and the synchronous save/derive sensor recompute (§5.3).</summary>
public sealed record SensorStats(
    double? HrAvg, double? HrMax, double? CadAvg, double? CadMax, double? PowAvg, double? PowMax);

/// <summary>
/// Pure conversions between a replay's <see cref="DecodedTrack"/> and the point/summary shapes the save
/// pipeline needs (A2 §4.5, §14.1). No I/O, no DI. The <see cref="DecodedTrack"/> → import-point direction
/// is the inverse of <see cref="DecodedTrackExtensions.FromExternalPoints"/> (round-trip is bit-exact:
/// dequantize then re-quantize lands on the same codec grid value).
/// </summary>
public static class TrackEditConverters
{
    // Codec channel order (= fixture keys / presence-bit order). THE order everywhere (TrackBlobCodec).
    private static readonly string[] ChannelNames =
    {
        "time", "lat", "lng", "altitude", "distance", "speed",
        "heartRate", "gradeSmooth", "cadence", "power", "pressure",
    };

    /// <summary>Channel-per-field inverse of <see cref="DecodedTrackExtensions.FromExternalPoints"/>: NaN
    /// lat/lng pass through as NaN doubles (re-quantize → NullQ), NaN optionals → null, NaN time → null
    /// Timestamp. Feeds the split-off/copy import persist path (own-list, cold path — a couple allocs/pt).</summary>
    public static List<ExternalGpsPoint> ToExternalPoints(DecodedTrack t)
    {
        var list = new List<ExternalGpsPoint>(t.Count);
        for (int i = 0; i < t.Count; i++)
        {
            double time = t.TimeEpochS[i];
            list.Add(new ExternalGpsPoint
            {
                Lat = t.Lat[i],   // NaN passes through → NullQ on re-quantize (bit-exact round trip)
                Lng = t.Lng[i],
                Altitude = Optional(t.Altitude, i),
                Distance = Optional(t.Distance, i),
                Speed = Optional(t.Speed, i),
                HeartRate = Optional(t.HeartRate, i),
                GradeSmooth = Optional(t.GradeSmooth, i),
                Cadence = Optional(t.Cadence, i),
                Power = Optional(t.Power, i),
                Pressure = Optional(t.Pressure, i),
                TimestampUtc = double.IsNaN(time) ? null : DateTime.UnixEpoch.AddSeconds(time),
            });
        }
        return list;
    }

    /// <summary>Avg/max of hr/cadence/power over present samples; a whole-NULL channel yields nulls.</summary>
    public static SensorStats ComputeSensorStats(DecodedTrack t)
    {
        var (hrAvg, hrMax) = AvgMax(t.HeartRate, t.Count);
        var (cadAvg, cadMax) = AvgMax(t.Cadence, t.Count);
        var (powAvg, powMax) = AvgMax(t.Power, t.Count);
        // Named construction (H1): six adjacent double?s — a silent transposition surface otherwise.
        return new SensorStats(
            HrAvg: hrAvg, HrMax: hrMax, CadAvg: cadAvg, CadMax: cadMax, PowAvg: powAvg, PowMax: powMax);
    }

    /// <summary>Serialize a track as absolute quantized channels on the codec grid (§14.1 fixture semantics):
    /// per channel <c>q_abs = RawQ[i] + QBias</c>, <c>NullQ → null</c>, a whole-NULL channel → key absent.
    /// Single pass per channel, no per-point objects.</summary>
    public static IReadOnlyDictionary<string, long?[]> ToAbsoluteQuantizedChannels(DecodedTrack t)
    {
        TrackChannel[] channels =
        {
            t.TimeEpochS, t.Lat, t.Lng, t.Altitude, t.Distance, t.Speed,
            t.HeartRate, t.GradeSmooth, t.Cadence, t.Power, t.Pressure,
        };
        var result = new Dictionary<string, long?[]>(ChannelNames.Length, StringComparer.Ordinal);
        for (int c = 0; c < channels.Length; c++)
        {
            int[]? raw = channels[c].RawQ;
            if (raw is null) continue;   // whole-NULL channel → key absent
            long bias = channels[c].QBias;
            var col = new long?[t.Count];
            for (int i = 0; i < t.Count; i++)
                col[i] = raw[i] == TrackChannel.NullQ ? null : raw[i] + bias;
            result[ChannelNames[c]] = col;
        }
        return result;
    }

    private static double? Optional(TrackChannel channel, int i)
    {
        if (channel.IsNull) return null;
        double v = channel[i];
        return double.IsNaN(v) ? null : v;
    }

    private static (double? avg, double? max) AvgMax(TrackChannel channel, int count)
    {
        if (channel.IsNull) return (null, null);
        double sum = 0, max = double.NegativeInfinity;
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            double v = channel[i];
            if (double.IsNaN(v)) continue;
            sum += v;
            if (v > max) max = v;
            n++;
        }
        return n == 0 ? (null, null) : (sum / n, max);
    }
}
