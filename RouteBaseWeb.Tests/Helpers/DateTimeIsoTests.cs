using System.Globalization;
using RouteBaseWeb.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// SQLite round-trips UTC-stored DateTimes as Kind=Unspecified, so "O" emits offset-less ISO
/// that JS parses as local time. ToUtcIso stamps Utc so the "O" output ends in Z.
/// </summary>
public class DateTimeIsoTests
{
    [Fact]
    public void ToUtcIso_UnspecifiedKind_EndsWithZ()
    {
        var dt = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Unspecified);

        var iso = dt.ToUtcIso();

        Assert.EndsWith("Z", iso, StringComparison.Ordinal);
        // Round-trips to the same instant (10:00 UTC), not shifted by a local offset.
        var parsed = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void ToUtcIso_UtcKind_Unchanged()
    {
        var dt = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);

        var iso = dt.ToUtcIso();

        Assert.EndsWith("Z", iso, StringComparison.Ordinal);
        var parsed = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero), parsed);
    }
}
