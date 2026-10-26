using RouteBaseWeb.Models.Geometry;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// The gate line's segment-crossing test: hit vs miss, half-open parameterisation (a point on
/// the line is counted once), the parallel/endpoint edges, and the direction sign.
/// </summary>
public class GateGeometryTests
{
    // Vertical gate at lng 10 spanning lat 0..10.
    private static readonly GeoLine Gate = new(0, 10, 10, 10);

    [Fact]
    public void Crosses_WhenSegmentPassesThroughTheLine()
    {
        Assert.True(Gate.TryCross(5, 5, 5, 15, out double t, out _));
        Assert.Equal(0.5, t, 3);
    }

    [Fact]
    public void NoCross_WhenBothEndpointsSameSide()
    {
        Assert.False(Gate.TryCross(5, 5, 5, 9, out _, out _));    // both west
        Assert.False(Gate.TryCross(5, 11, 5, 15, out _, out _));  // both east
    }

    [Fact]
    public void NoCross_BeyondGateSpan()
    {
        Assert.False(Gate.TryCross(20, 5, 20, 15, out _, out _)); // crosses lng 10 but north of the gate's lat range
    }

    [Fact]
    public void NoCross_WhenParallel()
    {
        var horizontal = new GeoLine(5, 0, 5, 20);               // along lat 5
        Assert.False(horizontal.TryCross(5, 0, 5, 20, out _, out _));
    }

    [Fact]
    public void Direction_IsOppositeForOppositeCrossings()
    {
        Gate.TryCross(5, 5, 5, 15, out _, out int eastbound);   // west → east
        Gate.TryCross(5, 15, 5, 5, out _, out int westbound);   // east → west
        Assert.True(eastbound != 0 && eastbound == -westbound);
    }

    [Fact]
    public void DirectionAllows_HonoursConstraint()
    {
        var fwd = new GeoLine(0, 10, 10, 10, CrossDirection.Forward);
        Assert.True(fwd.DirectionAllows(1));
        Assert.False(fwd.DirectionAllows(-1));

        var any = new GeoLine(0, 10, 10, 10, CrossDirection.Any);
        Assert.True(any.DirectionAllows(1));
        Assert.True(any.DirectionAllows(-1));
    }

    [Fact]
    public void HalfOpenParam_CountsSharedEndpointOnce()
    {
        // A sample sitting exactly on the line: the segment ending on it (t→1) does NOT cross,
        // the next segment starting on it (t=0) does — so the crossing is counted once.
        Assert.False(Gate.TryCross(5, 5, 5, 10, out _, out _));  // ends on the line, t == 1 → excluded
        Assert.True(Gate.TryCross(5, 10, 5, 15, out double t, out _));
        Assert.Equal(0.0, t, 3);
    }
}
