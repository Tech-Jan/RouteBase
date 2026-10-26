namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Decimal places for GPS channels **on the wire**. One place, so every surface agrees and a change is
/// a one-line edit rather than a hunt.
///
/// WHY THIS EXISTS. Channels are stored quantized (<see cref="TrackChannel"/>: "quantized int32 samples,
/// dequantized on read") and then dequantized to double, unit-converted, and serialized as text. The
/// conversions produce IEEE-754 round-trip artifacts — <c>47.952659999999995</c>, <c>476.40000000000003</c>,
/// <c>9.072000000000001</c> — so the wire carried 17 significant digits of which ~7 were meaningful. That is
/// pure cost twice over: longer text to format, and maximum-entropy digits for the compressor to chew on.
/// Measured on a 2134-point ride: -27% JSON bytes, -16% brotli, -13% serialize+compress CPU.
///
/// ROUND AT THE WIRE BOUNDARY ONLY. Round where a DTO is built, never inside a service or before a cache:
/// <c>ActivityGpsService</c> hands out <c>GpsCoordinate</c> lists that are CACHED and shared across
/// requests, so rounding there would both degrade a domain value and mutate shared state. Every current
/// consumer happens to be an API controller, which is exactly why it would go unnoticed.
///
/// The values below are display precision, chosen against what consumes them — not arbitrary.
/// </summary>
public static class GpsPrecision
{
    /// <summary>~11 cm at the equator. Map rendering tops out well above this.</summary>
    public const int LatLng = 6;

    /// <summary>10 cm. Elevation profiles are drawn in metres.</summary>
    public const int Altitude = 1;

    /// <summary>0.01 km/h.</summary>
    public const int Speed = 2;

    /// <summary>
    /// 0.1 m — deliberately finer than the others. The client INTERPOLATES distance deltas
    /// (replay-engine-base.ts, segment-stats-replay-efforts.ts, activity-segments.ts). At 1 s sampling and
    /// 30 km/h a delta is ~8 m, so even 1 m would be safe, but 0.1 m costs almost nothing and removes the
    /// question. Do not coarsen this without re-checking those three call sites.
    /// </summary>
    public const int Distance = 4;

    /// <summary>Both are integer-valued sensor readings; the fractional part is dequantization noise.</summary>
    public const int Rate = 0;

    public static double Coord(double v) => Math.Round(v, LatLng);
    public static double Alt(double v) => Math.Round(v, Altitude);
    public static double Spd(double v) => Math.Round(v, Speed);
    public static double Dist(double v) => Math.Round(v, Distance);
    public static double Sensor(double v) => Math.Round(v, Rate);

    /// <summary>Nullable passthrough for DTOs whose coordinate fields are optional (photo markers).</summary>
    public static double? Coord(double? v) => v is { } d ? Math.Round(d, LatLng) : null;
}
