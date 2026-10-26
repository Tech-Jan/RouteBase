using System;
using System.Threading.Tasks;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Unit tests for the variant A (GeoTimeZone) resolver. Fully offline — the package bundles the
/// tz shape data; offsets come from the host tz database (present on Linux/.NET images).
/// </summary>
public class GeoTimeZoneActivityResolverTests
{
    private readonly GeoTimeZoneActivityResolver _sut =
        new(NullLogger<GeoTimeZoneActivityResolver>.Instance);

    // Innsbruck, Austria — Europe/Vienna.
    private const double InnsbruckLat = 47.2692;
    private const double InnsbruckLng = 11.4041;

    [Fact]
    public async Task ResolveOffset_InnsbruckSummer_ReturnsPlus2h()
    {
        var offset = await _sut.ResolveOffsetAsync(
            InnsbruckLat, InnsbruckLng, new DateTime(2023, 7, 15, 10, 0, 0, DateTimeKind.Utc), "u");

        Assert.Equal(TimeSpan.FromHours(2), offset); // CEST
    }

    [Fact]
    public async Task ResolveOffset_InnsbruckWinter_ReturnsPlus1h()
    {
        var offset = await _sut.ResolveOffsetAsync(
            InnsbruckLat, InnsbruckLng, new DateTime(2023, 1, 15, 10, 0, 0, DateTimeKind.Utc), "u");

        Assert.Equal(TimeSpan.FromHours(1), offset); // CET
    }

    [Fact]
    public async Task ResolveOffset_NullCoordinates_ReturnsNull()
    {
        Assert.Null(await _sut.ResolveOffsetAsync(null, 11.0, DateTime.UtcNow, "u"));
        Assert.Null(await _sut.ResolveOffsetAsync(47.0, null, DateTime.UtcNow, "u"));
    }
}
