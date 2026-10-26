using System.Globalization;
using RouteBaseWeb.Services.Matching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Characterization proof that <see cref="SqliteCompat"/> reproduces SQLite's julianday() and
/// round(x,5) bit-for-bit (TE-14). Runs against a real in-memory <see cref="SqliteConnection"/>
/// (same e_sqlite3 build the app bundles) — no .db file — so the pure helpers are validated
/// against the actual engine they must mirror, not a re-derivation of its algorithm.
/// </summary>
public class SqliteCompatTests
{
    private static SqliteConnection OpenMemory()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        return c;
    }

    private static double SqlScalar(SqliteConnection c, string sql, params (string name, object val)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Whole-second epoch seconds for a "yyyy-MM-dd HH:mm:ss" UTC timestamp — matches how
    // DecodedTrack derives TimeEpochS (GetDateTime − DateTime.UnixEpoch).
    private static double Epoch(string ts)
    {
        var dt = DateTime.SpecifyKind(
            DateTime.ParseExact(ts, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
        return (dt - DateTime.UnixEpoch).TotalSeconds;
    }

    private static bool BitEq(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    // ------------------------------------------------------------------
    // julianday()
    // ------------------------------------------------------------------

    [Fact]
    public void JulianDay_MatchesSqliteJulianday_OnFixtureTimestamps()
    {
        string[] fixtures =
        {
            "2010-01-01 00:00:00", "2016-02-29 12:00:00", "2020-02-29 23:59:59",
            "2021-06-15 10:00:00", "2021-06-15 10:30:15", "2024-01-01 00:00:00",
            "2025-07-05 08:15:42", "2028-11-30 06:07:08", "2030-12-31 23:59:59",
            "2012-07-01 00:00:01", "2019-03-31 01:00:00", "2023-10-29 02:30:00",
        };

        using var c = OpenMemory();
        foreach (var ts in fixtures)
        {
            double sql = SqlScalar(c, "SELECT julianday($t)", ("$t", ts));
            double replica = SqliteCompat.JulianDay(Epoch(ts));
            Assert.True(BitEq(sql, replica),
                $"julianday('{ts}'): sqlite={sql:R} replica={replica:R}");
        }
    }

    [Fact]
    public void EffortTime_ReproducesCastSensitiveFraction()
    {
        // A 1-second span: the julianday round-trip yields ~0.99999 (fraction > 0.999), so
        // CAST(effort_time AS INT) = 0 — NOT the 1 an exact-seconds engine would report. Pins
        // that SqliteCompat reproduces the fingerprint-relevant CAST behavior (S9.5).
        const string tMin = "2021-06-15 10:00:00";
        const string tMax = "2021-06-15 10:00:01";

        using var c = OpenMemory();
        double sql = SqlScalar(c,
            "SELECT (julianday($max) - julianday($min)) * 86400.0",
            ("$max", tMax), ("$min", tMin));
        double replica = SqliteCompat.EffortTimeSeconds(Epoch(tMin), Epoch(tMax));

        Assert.True(BitEq(sql, replica), $"effort_time: sqlite={sql:R} replica={replica:R}");
        Assert.Equal((long)sql, (long)replica);
        Assert.Equal(0L, (long)replica);                       // CAST truncates below the integer
        Assert.NotEqual((long)(Epoch(tMax) - Epoch(tMin)), (long)replica); // exact-seconds would be 1
    }

    // ------------------------------------------------------------------
    // round(x, 5)
    // ------------------------------------------------------------------

    [Fact]
    public void Round5_MatchesSqliteRound_OnAdversarialAndSampledValues()
    {
        // Everything except an exact-decimal binary midpoint round-trips identically to SQLite.
        double[] vals =
        {
            0.00317, 0.00025, 0.0, -0.00317,          // live-representative offsets
            0.000005, 0.000015, 0.000025, 0.000135,   // 5th-decimal rounding boundaries
            0.123455, 0.123465, 0.5000005,            // straddle-the-boundary shapes
            1.0 / 3.0, 0.0002, 0.00019999, 0.00020001 // band-boundary neighborhoods
        };

        using var c = OpenMemory();
        foreach (var v in vals)
        {
            double sql = SqlScalar(c, "SELECT round($x, 5)", ("$x", v));
            double replica = SqliteCompat.Round5(v);
            Assert.True(BitEq(sql, replica), $"round({v:R},5): sqlite={sql:R} replica={replica:R}");
        }
    }

    [Fact]
    public void Round5_ExactMidpoint_DivergesHalfToEven_DocumentedAndUnreachable()
    {
        // 0.015625 = 1/64 is exactly representable and an exact decimal midpoint at 5 places.
        // C# "F5" rounds half-to-even → 0.01562; SQLite round() rounds half-away → 0.01563.
        // This is the ONLY divergence class and cannot arise from a coordinate sum, so it never
        // affects parity — pinned here so the fact stays visible if either engine changes.
        using var c = OpenMemory();
        double sql = SqlScalar(c, "SELECT round($x, 5)", ("$x", 0.015625));

        Assert.Equal(0.01562, SqliteCompat.Round5(0.015625), 12);
        Assert.Equal(0.01563, sql, 12);
        Assert.False(BitEq(sql, SqliteCompat.Round5(0.015625)));
    }

    [Fact]
    public void Round5_NonFinite_PassThrough()
    {
        Assert.True(double.IsNaN(SqliteCompat.Round5(double.NaN)));
        Assert.True(double.IsPositiveInfinity(SqliteCompat.Round5(double.PositiveInfinity)));
        Assert.True(double.IsNegativeInfinity(SqliteCompat.Round5(double.NegativeInfinity)));
    }
}
