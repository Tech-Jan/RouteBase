using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

public class RamerDouglasPeuckerTests
{
    private static SegmentGpsPoint Pt(double lat, double lng) =>
        new() { Lat = lat, Lng = lng };

    private static List<SegmentGpsPoint> Pts(params (double lat, double lng)[] coords) =>
        coords.Select(c => Pt(c.lat, c.lng)).ToList();

    // ---------------------------------------------------------------
    // 1. Edge cases — inputs too small to simplify
    // ---------------------------------------------------------------

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        var result = SegmentGeometry.RamerDouglasPeucker([], 1.0);
        Assert.Empty(result);
    }

    [Fact]
    public void SinglePoint_ReturnsSamePoint()
    {
        var input = Pts((47.0, 11.0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 1.0);
        Assert.Single(result);
        Assert.Equal(47.0, result[0].Lat);
    }

    [Fact]
    public void TwoPoints_ReturnsBothPoints()
    {
        var input = Pts((47.0, 11.0), (48.0, 12.0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 1.0);
        Assert.Equal(2, result.Count);
    }

    // ---------------------------------------------------------------
    // 2. Collinear points — all on the same line, should reduce
    // ---------------------------------------------------------------

    [Fact]
    public void CollinearPoints_ReducesToEndpoints()
    {
        // 5 points on a straight line from (0,0) to (4,4)
        var input = Pts((0, 0), (1, 1), (2, 2), (3, 3), (4, 4));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.001);
        Assert.Equal(2, result.Count);
        Assert.Equal(0.0, result[0].Lat);
        Assert.Equal(4.0, result[1].Lat);
    }

    [Fact]
    public void CollinearPoints_LargeCount_StillReducesToEndpoints()
    {
        // 20 collinear points
        var input = Enumerable.Range(0, 20)
            .Select(i => Pt(i * 0.1, i * 0.1))
            .ToList();
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.001);
        Assert.Equal(2, result.Count);
    }

    // ---------------------------------------------------------------
    // 3. Zigzag / deviation — points that deviate from the line
    // ---------------------------------------------------------------

    [Fact]
    public void SingleDeviation_KeptWithSmallEpsilon()
    {
        // Triangle: start, peak off-line, end
        var input = Pts((0, 0), (1, 5), (2, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.001);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SingleDeviation_RemovedWithLargeEpsilon()
    {
        // Same triangle but epsilon larger than the deviation
        var input = Pts((0, 0), (1, 5), (2, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 10.0);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Zigzag_SmallEpsilon_KeepsAllPoints()
    {
        // Zigzag with significant lateral offsets
        var input = Pts(
            (0, 0), (1, 5), (2, 0), (3, 5), (4, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.001);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Zigzag_LargeEpsilon_ReducesToEndpoints()
    {
        var input = Pts(
            (0, 0), (1, 5), (2, 0), (3, 5), (4, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 100.0);
        Assert.Equal(2, result.Count);
    }

    // ---------------------------------------------------------------
    // 4. Endpoint preservation — first and last always kept
    // ---------------------------------------------------------------

    [Fact]
    public void AlwaysPreservesFirstAndLastPoint()
    {
        var input = Pts(
            (10, 20), (10.001, 20.001), (10.002, 20.002),
            (10.003, 20.003), (30, 40));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.001);

        Assert.Equal(10.0, result[0].Lat);
        Assert.Equal(20.0, result[0].Lng);
        Assert.Equal(30.0, result[^1].Lat);
        Assert.Equal(40.0, result[^1].Lng);
    }

    // ---------------------------------------------------------------
    // 5. Point count invariant — never more points out than in
    // ---------------------------------------------------------------

    [Fact]
    public void OutputNeverExceedsInput()
    {
        var input = Pts(
            (0, 0), (1, 0.5), (2, -0.3), (3, 0.8),
            (4, -0.1), (5, 0.6), (6, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.01);
        Assert.InRange(result.Count, 2, input.Count);
    }

    // ---------------------------------------------------------------
    // 6. Realistic GPS trace — mixed deviations
    // ---------------------------------------------------------------

    [Fact]
    public void RealisticTrace_IntermediateEpsilon_ReducesButKeepsShape()
    {
        // Simulate a GPS trace with some noise and one real turn
        var input = Pts(
            (47.000, 11.000),   // start
            (47.001, 11.001),   // near-collinear
            (47.002, 11.002),   // near-collinear
            (47.003, 11.010),   // real turn (lng jumps)
            (47.004, 11.011),   // near-collinear after turn
            (47.005, 11.012));  // end

        // Epsilon small enough to keep the turn but remove collinear noise
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.0005);

        // Must keep endpoints + the turn point at minimum
        Assert.InRange(result.Count, 3, input.Count);
        Assert.Equal(47.000, result[0].Lat);
        Assert.Equal(47.005, result[^1].Lat);
    }

    // ---------------------------------------------------------------
    // 7. No duplicate adjacent points in output
    // ---------------------------------------------------------------

    [Fact]
    public void NoDuplicateAdjacentPoints()
    {
        var input = Pts(
            (0, 0), (1, 3), (2, 0), (3, 3), (4, 0),
            (5, 3), (6, 0), (7, 3), (8, 0));
        var result = SegmentGeometry.RamerDouglasPeucker(input, 0.5);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.False(
                result[i].Lat == result[i - 1].Lat && result[i].Lng == result[i - 1].Lng,
                $"Duplicate adjacent point at index {i}: ({result[i].Lat}, {result[i].Lng})");
        }
    }

    // ---------------------------------------------------------------
    // 8. StrideDownsample — bounds RDP's input for big segments
    // ---------------------------------------------------------------

    [Fact]
    public void StrideDownsample_UnderCap_ReturnsInputUntouched()
    {
        var input = Enumerable.Range(0, 100).Select(i => Pt(i * 0.001, i * 0.001)).ToList();
        var result = SegmentGeometry.StrideDownsample(input, 5000);
        Assert.Same(input, result); // no allocation when nothing to thin
    }

    [Fact]
    public void StrideDownsample_OverCap_BoundsCountAndKeepsEndpoints()
    {
        var input = Enumerable.Range(0, 200_000).Select(i => Pt(i * 0.00001, i * 0.00002)).ToList();
        var result = SegmentGeometry.StrideDownsample(input, SegmentGeometry.RdpWorkingCap);

        Assert.True(result.Count <= SegmentGeometry.RdpWorkingCap + 1, $"kept {result.Count}");
        Assert.Equal(input[0].Lat, result[0].Lat);
        Assert.Equal(input[0].Lng, result[0].Lng);
        Assert.Equal(input[^1].Lat, result[^1].Lat);
        Assert.Equal(input[^1].Lng, result[^1].Lng);
    }

    [Fact]
    public void StrideDownsample_ExactStrideMultiple_KeepsEndpointWithoutDuplicating()
    {
        // 10 points, cap 5 → stride 2 → indices 0,2,4,6,8 then endpoint 9 appended (9 % 2 != 0).
        var input = Enumerable.Range(0, 10).Select(i => Pt(i, i)).ToList();
        var result = SegmentGeometry.StrideDownsample(input, 5);
        Assert.Equal(input[^1].Lat, result[^1].Lat);
        Assert.True(result.Count <= 6);
    }

    // StrideIndices is the same engine, exposed for callers with parallel channels (race replay).
    // If the two ever diverge, one of them is a second downsampler — which is the bug.

    [Fact]
    public void StrideIndices_MatchesStrideDownsample()
    {
        var input = Enumerable.Range(0, 12_345).Select(i => Pt(i * 0.00001, i * 0.00002)).ToList();
        var thinned = SegmentGeometry.StrideDownsample(input, 2000);
        var kept = SegmentGeometry.StrideIndices(input.Count, 2000);

        Assert.Equal(thinned.Count, kept.Count);
        for (var i = 0; i < kept.Count; i++)
            Assert.Equal(thinned[i].Lat, input[kept[i]].Lat);
    }

    [Fact]
    public void StrideIndices_UnderCap_IsTheIdentity()
    {
        var kept = SegmentGeometry.StrideIndices(100, 2000);
        Assert.Equal(Enumerable.Range(0, 100), kept);
    }
}
