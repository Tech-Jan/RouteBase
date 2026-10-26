using System;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Unit tests for the GI-01 summary-mutation helper — pure math, no file I/O or DI.
/// </summary>
public class TrackImportLocalTimeTests
{
    [Fact]
    public void ApplyOffset_SetsLocalAndTimezoneColumns()
    {
        var utc = new DateTime(2023, 7, 15, 10, 0, 0, DateTimeKind.Utc);
        var s = new ExternalActivitySummary { StartDateUtc = utc, StartDateLocal = utc };

        TrackImportLocalTime.ApplyLocalTime(s, TimeSpan.FromHours(2));

        Assert.Equal(utc.AddHours(2), s.StartDateLocal);
        Assert.Equal(2f, s.TimezoneOffset);
    }

    [Fact]
    public void ApplyOffset_NegativeOffset_SubtractsFromUtc()
    {
        var utc = new DateTime(2023, 1, 15, 3, 0, 0, DateTimeKind.Utc);
        var s = new ExternalActivitySummary { StartDateUtc = utc, StartDateLocal = utc };

        TrackImportLocalTime.ApplyLocalTime(s, TimeSpan.FromHours(-5));

        Assert.Equal(utc.AddHours(-5), s.StartDateLocal);
        Assert.Equal(-5f, s.TimezoneOffset);
    }
}
