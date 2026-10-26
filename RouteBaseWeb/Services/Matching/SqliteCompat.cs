using System.Globalization;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// Pure, zero-I/O replicas of the two SQLite scalar functions the frozen SQL post-pipeline
/// (SegmentService.Offsets.cs / SegmentService.Aggregates.cs) relies on for effort math:
/// <c>julianday()</c> and <c>round(x, 5)</c>. The engine must reproduce them bit-for-bit
/// (invariant 2, bug-for-bug) — <c>effort_time</c> is CAST-fingerprinted (664/1,339 live
/// efforts sit ε below an integer) so an "exact seconds" shortcut shifts the parity gate.
///
/// Equivalence is enforced by SqliteCompatTests (in-memory SQLite characterization, no DB file).
/// A faster arithmetic Round5 is a post-Cutover item only, and only with that test kept green.
/// </summary>
public static class SqliteCompat
{
    /// <summary>
    /// Bit-identical to SQLite <c>julianday(datetime_utc)</c> for whole-second inputs
    /// (live-verified 0/34,993 mismatch over the GPS table). SQLite parses the TEXT timestamp
    /// to an int64 count of milliseconds since −4713-11-24 12:00 (iJD) and returns
    /// <c>iJD / 86400000.0</c> — one correctly-rounded division. The constant is iJD at the
    /// unix epoch (2440587.5 × 86400000 ms); every intermediate here is &lt; 2^53 so exactly
    /// representable, leaving that single division as the only rounding — identical bits.
    /// </summary>
    public static double JulianDay(double epochSeconds) =>
        (210866760000000.0 + epochSeconds * 1000.0) / 86400000.0;

    /// <summary>
    /// Exact expression shape of Aggregates.cs:66
    /// <c>(julianday(MAX(datetime_utc)) − julianday(MIN(datetime_utc))) * 86400.0</c>.
    /// Never "simplify" to <c>max − min</c>: the two julianday roundings plus the scale-back
    /// make the result almost never an integer, and 664/1,339 live efforts are CAST-sensitive
    /// (S9.5). <paramref name="minEpochS"/>/<paramref name="maxEpochS"/> are min/max over the
    /// window's non-NaN TimeEpochS (lexicographic TEXT min/max ≡ epoch min/max for the uniform
    /// 19-char timestamps in this DB).
    /// </summary>
    public static double EffortTimeSeconds(double minEpochS, double maxEpochS) =>
        (JulianDay(maxEpochS) - JulianDay(minEpochS)) * 86400.0;

    /// <summary>
    /// Replica of SQLite <c>round(x, 5)</c> via format-parse (C# "F5" round-trip). Must stay
    /// equivalent to SQLite round(x,5) until Cutover; equivalence enforced by SqliteCompatTests.
    /// Exact-decimal-midpoint doubles (e.g. 0.015625 = 1/64) are the only theoretical divergence
    /// class — C# "F5" rounds half-to-even (→ 0.01562) while SQLite rounds half-away (→ 0.01563).
    /// They cannot occur for coordinate sums (|Δlat| + |Δlng|), so the divergence is unreachable
    /// on live data; SqliteCompatTests documents it explicitly.
    ///
    /// NaN/non-finite in → same value out; callers treat NaN as SQL NULL.
    /// </summary>
    public static double Round5(double x)
    {
        if (!double.IsFinite(x)) return x;
        return double.Parse(x.ToString("F5", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
