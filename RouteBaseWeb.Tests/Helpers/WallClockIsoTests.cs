using RouteBaseWeb.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// FE-12: the dashboard/races/segments grids bucket rides by LOCAL calendar day from these strings.
/// The old <c>ToString("o")</c> produced an offset-less string only because SQLite hands DateTimes back
/// as Kind.Unspecified — an accident, not a contract. This pins the wire against that Kind changing.
/// </summary>
public class WallClockIsoTests
{
    [Fact]
    public void EveryKind_ProducesTheSameString_ForTheSameWallClock()
    {
        var wall = new DateTime(2026, 7, 28, 22, 30, 0, DateTimeKind.Unspecified);

        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified).ToWallClockIso();
        var utc = DateTime.SpecifyKind(wall, DateTimeKind.Utc).ToWallClockIso();
        var local = DateTime.SpecifyKind(wall, DateTimeKind.Local).ToWallClockIso();

        Assert.Equal("2026-07-28T22:30:00", unspecified);
        Assert.Equal(unspecified, utc);   // the regression this exists to prevent: `o` would append Z here
        Assert.Equal(unspecified, local); // …and a +02:00 offset here
    }

    [Fact]
    public void NoZoneMarker_IsEmitted_TheBrowserMustReadItAsLocal()
    {
        var iso = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToWallClockIso();

        Assert.DoesNotContain("Z", iso, StringComparison.Ordinal);
        Assert.DoesNotContain("+", iso, StringComparison.Ordinal);
        Assert.Equal("2026-01-02T03:04:05", iso);
    }

    [Fact]
    public void TheStringSortsChronologically_TheGridsSortOnItRaw()
    {
        var earlier = new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Unspecified).ToWallClockIso();
        var later = new DateTime(2026, 7, 28, 17, 0, 0, DateTimeKind.Unspecified).ToWallClockIso();
        var nextDay = new DateTime(2026, 7, 29, 1, 0, 0, DateTimeKind.Unspecified).ToWallClockIso();

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, nextDay) < 0);
    }

    [Fact]
    public void ANullTimestamp_StaysNull_RatherThanBecomingAnEpochString()
    {
        DateTime? missing = null;
        Assert.Null(missing.ToWallClockIso());
    }

    [Fact]
    public void TheFormat_IsCultureInvariant()
    {
        // A German thread culture must not turn the date separators into dots.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2026-07-28T22:30:00", new DateTime(2026, 7, 28, 22, 30, 0, DateTimeKind.Unspecified).ToWallClockIso());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
