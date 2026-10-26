using RouteBaseWeb.Api;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins <see cref="ReviewApiController.DownsampleLatLng"/>'s own contract ("strided to &lt;= maxPoints"):
/// with floor-division striding, any count in (max, 2·max) returned the FULL track — up to ~2× the
/// stated minimap budget, multiplied across every pending-review row.
/// </summary>
public class ReviewMinimapDownsampleTests
{
    private static DecodedTrack Track(int count)
    {
        var lat = new int[count];
        var lng = new int[count];
        var time = new int[count];
        for (int i = 0; i < count; i++) { lat[i] = 4700000 + i; lng[i] = 1100000 + i; time[i] = i; }
        return new DecodedTrack
        {
            Count = count,
            Lat = new TrackChannel(lat, 1e-5),
            Lng = new TrackChannel(lng, 1e-5),
            TimeEpochS = new TrackChannel(time, 1.0),
        };
    }

    [Theory]
    [InlineData(399)]  // floor stride = 1 → returned all 399 before the fix
    [InlineData(401)]  // floor stride = 2 → returned 201 before the fix
    [InlineData(200)]
    [InlineData(3)]
    [InlineData(4000)]
    public void Downsample_NeverExceedsBudget(int count)
    {
        var result = ReviewApiController.DownsampleLatLng(Track(count), 200);
        Assert.True(result.Count <= 200, $"{count} points → {result.Count} returned (budget 200)");
        Assert.NotEmpty(result);
    }
}
