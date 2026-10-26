using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// The single conversion funnel between the decoded per-channel <see cref="DecodedTrack"/> and the
/// point-object representations (<see cref="GpsData"/> POCO, <see cref="ExternalGpsPoint"/> import DTO).
/// Kept next to the codec so every write path (import funnel, finalize compaction) and every read path
/// (ActivityGpsService materialize) shares one NaN⇔null / epoch⇔DateTime mapping instead of hand-rolling it.
/// Values are quantized at build (to the codec scales), so a sample read back from a fresh
/// <see cref="FromGpsDataList"/> equals <c>TrackBlobCodec.RoundTripValue(input)</c>.
/// </summary>
public static class DecodedTrackExtensions
{
    /// <summary>Materialize channels into <see cref="GpsData"/> POCOs: GpsId = the track's ordinal, NaN → null,
    /// epoch seconds → UTC <see cref="DateTime"/>. <paramref name="trackId"/> is supplied by the caller —
    /// content carries no id (see <see cref="DecodedTrack"/>), so this is where the item axis is stamped on.</summary>
    public static List<GpsData> ToGpsDataList(this DecodedTrack t, TrackId trackId)
    {
        var list = new List<GpsData>(t.Count);
        for (int i = 0; i < t.Count; i++)
        {
            double tSec = t.TimeEpochS[i];
            list.Add(new GpsData
            {
                GpsId       = i,
                TrackId     = trackId.Value,
                DateTimeUtc = double.IsNaN(tSec) ? null : DateTime.UnixEpoch.AddSeconds(tSec),
                GpsLat      = Channel(t.Lat, i),
                GpsLong     = Channel(t.Lng, i),
                Altitude    = Channel(t.Altitude, i),
                Distance    = Channel(t.Distance, i),
                Speed       = Channel(t.Speed, i),
                HeartRate   = Channel(t.HeartRate, i),
                GradeSmooth = Channel(t.GradeSmooth, i),
                Cadence     = Channel(t.Cadence, i),
                Power       = Channel(t.Power, i),
                Pressure    = Channel(t.Pressure, i),
            });
        }
        return list;
    }

    /// <summary>Pack <see cref="GpsData"/> POCOs into a <see cref="DecodedTrack"/> for encoding. Ordinals are the
    /// ramp (the codec does not store ids). null → NaN; a whole-null optional channel → default channel.</summary>
    public static DecodedTrack FromGpsDataList(IReadOnlyList<GpsData> points)
    {
        int n = points.Count;
        var lat  = new double[n];
        var lng  = new double[n];
        var time = new double[n];
        var altitude    = new double[n];
        var distance    = new double[n];
        var speed       = new double[n];
        var heartRate   = new double[n];
        var gradeSmooth = new double[n];
        var cadence     = new double[n];
        var power       = new double[n];
        var pressure    = new double[n];

        for (int i = 0; i < n; i++)
        {
            var p = points[i];
            lat[i]  = p.GpsLat ?? double.NaN;
            lng[i]  = p.GpsLong ?? double.NaN;
            time[i] = p.DateTimeUtc.HasValue
                ? (p.DateTimeUtc.Value.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds
                : double.NaN;
            altitude[i]    = p.Altitude ?? double.NaN;
            distance[i]    = p.Distance ?? double.NaN;
            speed[i]       = p.Speed ?? double.NaN;
            heartRate[i]   = p.HeartRate ?? double.NaN;
            gradeSmooth[i] = p.GradeSmooth ?? double.NaN;
            cadence[i]     = p.Cadence ?? double.NaN;
            power[i]       = p.Power ?? double.NaN;
            pressure[i]    = p.Pressure ?? double.NaN;
        }

        return FromDoubleChannels(time, lat, lng, altitude, distance, speed, heartRate, gradeSmooth, cadence, power, pressure);
    }

    /// <summary>Pack import <see cref="ExternalGpsPoint"/>s into a <see cref="DecodedTrack"/> for encoding.
    /// Same conventions as <see cref="FromGpsDataList"/> (ordinal ids, null → NaN, whole-null → default channel).</summary>
    public static DecodedTrack FromExternalPoints(IReadOnlyList<ExternalGpsPoint> points)
    {
        int n = points.Count;
        var lat  = new double[n];
        var lng  = new double[n];
        var time = new double[n];
        var altitude    = new double[n];
        var distance    = new double[n];
        var speed       = new double[n];
        var heartRate   = new double[n];
        var gradeSmooth = new double[n];
        var cadence     = new double[n];
        var power       = new double[n];
        var pressure    = new double[n];

        for (int i = 0; i < n; i++)
        {
            var p = points[i];
            lat[i]  = p.Lat;
            lng[i]  = p.Lng;
            time[i] = p.TimestampUtc.HasValue
                ? (p.TimestampUtc.Value.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds
                : double.NaN;
            altitude[i]    = p.Altitude ?? double.NaN;
            distance[i]    = p.Distance ?? double.NaN;
            speed[i]       = p.Speed ?? double.NaN;
            heartRate[i]   = p.HeartRate ?? double.NaN;
            gradeSmooth[i] = p.GradeSmooth ?? double.NaN;
            cadence[i]     = p.Cadence ?? double.NaN;
            power[i]       = p.Power ?? double.NaN;
            pressure[i]    = p.Pressure ?? double.NaN;
        }

        return FromDoubleChannels(time, lat, lng, altitude, distance, speed, heartRate, gradeSmooth, cadence, power, pressure);
    }

    /// <summary>The single quantization funnel: doubles → int32-backed <see cref="TrackChannel"/>s. Required
    /// channels are always present (even all-NaN / empty). An optional channel that is null or all-NaN → default
    /// (whole-NULL) channel. Per sample: NaN → <see cref="TrackChannel.NullQ"/>, else the quantized value
    /// guarded into int32. Time is rebased on its first present quantized epoch (stored in QBias).</summary>
    public static DecodedTrack FromDoubleChannels(
        double[] time, double[] lat, double[] lng,
        double[]? altitude, double[]? distance, double[]? speed, double[]? heartRate,
        double[]? gradeSmooth, double[]? cadence, double[]? power, double[]? pressure = null)
    {
        int n = time.Length;
        return new DecodedTrack
        {
            Count = n,
            Lat = Required(lat, TrackBlobCodec.LatScale, n),
            Lng = Required(lng, TrackBlobCodec.LngScale, n),
            TimeEpochS = RequiredTime(time, n),
            Altitude = Optional(altitude, TrackBlobCodec.AltitudeScale, n),
            Distance = Optional(distance, TrackBlobCodec.DistanceScale, n),
            Speed = Optional(speed, TrackBlobCodec.SpeedScale, n),
            HeartRate = Optional(heartRate, TrackBlobCodec.HeartRateScale, n),
            GradeSmooth = Optional(gradeSmooth, TrackBlobCodec.GradeSmoothScale, n),
            Cadence = Optional(cadence, TrackBlobCodec.CadenceScale, n),
            Power = Optional(power, TrackBlobCodec.PowerScale, n),
            Pressure = Optional(pressure, TrackBlobCodec.PressureScale, n),
        };
    }

    private static TrackChannel Required(double[] values, double scale, int n)
    {
        var q = new int[n];
        for (int i = 0; i < n; i++)
            q[i] = double.IsNaN(values[i]) ? TrackChannel.NullQ : GuardQuantize(values[i], scale);
        return new TrackChannel(q, scale);
    }

    // Time channel: rebase on the first present quantized epoch so int32 offsets stay small.
    private static TrackChannel RequiredTime(double[] time, int n)
    {
        long qBias = 0;
        for (int i = 0; i < n; i++)
            if (!double.IsNaN(time[i]))
            {
                qBias = TrackBlobCodec.Quantize(time[i], TrackBlobCodec.TimeScale);
                break;
            }

        var q = new int[n];
        for (int i = 0; i < n; i++)
            q[i] = double.IsNaN(time[i])
                ? TrackChannel.NullQ
                : Guarded(TrackBlobCodec.Quantize(time[i], TrackBlobCodec.TimeScale) - qBias);
        return new TrackChannel(q, TrackBlobCodec.TimeScale, qBias);
    }

    private static TrackChannel Optional(double[]? values, double scale, int n)
    {
        if (values is null) return default;
        var q = new int[n];
        bool anyPresent = false;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(values[i])) { q[i] = TrackChannel.NullQ; continue; }
            q[i] = GuardQuantize(values[i], scale);
            anyPresent = true;
        }
        return anyPresent ? new TrackChannel(q, scale) : default;   // whole-NULL optional costs nothing
    }

    // internal: the live-merge fast path (ContentTrackReader) quantizes only its small staging tail through
    // this same funnel, so a merged sample matches a full FromDoubleChannels round-trip.
    internal static int GuardQuantize(double v, double scale) =>
        Guarded(TrackBlobCodec.Quantize(v, scale));

    /// <summary>Quantize a time epoch (seconds) onto the codec grid, rebased against <paramref name="qBias"/>
    /// — the time-channel form of <see cref="GuardQuantize"/>. Same result RequiredTime produces per sample.</summary>
    internal static int GuardQuantizeTime(double epochSeconds, long qBias) =>
        Guarded(TrackBlobCodec.Quantize(epochSeconds, TrackBlobCodec.TimeScale) - qBias);

    // Fold to int32; NullQ (int.MinValue) is reserved, so the low bound is exclusive.
    // Hot path (called per point per channel on every encode): the throw lives in a cold helper so the
    // guard stays small enough for the JIT to inline — same ThrowHelper shape as TrackBlobCodec.Guarded
    // (its interpolated message measurably blocked inlining on the decode side — a disasm-confirmed
    // regression: no throwing guard with an interpolated message inside a per-point loop).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Guarded(long x)
    {
        if (x <= int.MinValue || x > int.MaxValue) ThrowOutOfInt32Range(x);
        return (int)x;
    }

    [DoesNotReturn]
    private static void ThrowOutOfInt32Range(long x) =>
        throw new InvalidDataException($"Quantized value {x} out of int32 range.");

    private static double? Channel(TrackChannel channel, int i)
    {
        if (channel.IsNull) return null;
        double v = channel[i];
        return double.IsNaN(v) ? null : v;
    }
}
