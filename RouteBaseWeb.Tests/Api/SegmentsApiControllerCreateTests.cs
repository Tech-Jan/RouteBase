using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// SA-04 pin: <see cref="SegmentsApiController.CreateSegment"/> still rejects an absurd point count with
/// 400 before touching any dependency — but the ceiling is now a raw-request-memory guard (1M), not the
/// old 5000 geometry cap. RDP's StackOverflow / O(n²) risk is bounded by StrideDownsample instead, so a
/// whole-activity polyline (well under 1M) is accepted. Pure input validation ahead of every injected
/// dependency, so the controller is constructed with null deps for this isolated guard test.
/// </summary>
public class SegmentsApiControllerCreateTests
{
    private static SegmentsApiController NewController() =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static CreateSegmentRequest RequestWithPoints(int count)
    {
        var pts = new List<SegmentGpsPoint>(count);
        for (int i = 0; i < count; i++)
            pts.Add(new SegmentGpsPoint { Lat = 47.0, Lng = 11.0 });
        return new CreateSegmentRequest { SegmentName = "S", GpsPoints = pts };
    }

    [Fact]
    public async Task CreateSegment_AboveHardCeiling_Returns400()
    {
        var result = await NewController().CreateSegment(RequestWithPoints(1_000_001));
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("1000000", bad.Value?.ToString(), StringComparison.Ordinal);
    }
}
