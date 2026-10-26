using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// PS-04 pins for the columnar wire form: kept-index selection with verbatim per-channel values
/// and whole-null channel collapse.
/// </summary>
public class EffortPointsWireTests
{
    private static List<EffortDataPoint> FivePoints() =>
    [
        new() { Distance = 0,   Time = 0,  Speed = 7.2,  Altitude = 100, Lat = 47.0, Lng = 11.0, CumulativeCostKj = 0.0 },
        new() { Distance = 100, Time = 10, Speed = 10.8, Altitude = 110, Lat = 47.1, Lng = 11.0, CumulativeCostKj = 1.0 },
        new() { Distance = 200, Time = 20, Speed = 14.4, Altitude = 120, Lat = 47.2, Lng = 11.0, CumulativeCostKj = 2.0 },
        new() { Distance = 300, Time = 30, Speed = 18.0, Altitude = 130, Lat = 47.3, Lng = 11.0, CumulativeCostKj = 3.0 },
        new() { Distance = 400, Time = 40, Speed = 21.6, Altitude = 140, Lat = 47.4, Lng = 11.0, CumulativeCostKj = 4.0 },
    ];

    [Fact]
    public void FromPoints_SelectsKeptIndices_VerbatimValues()
    {
        var pts  = FivePoints();
        var wire = EffortPointsWire.FromPoints(pts, [0, 2, 4]);

        Assert.Equal(3, wire.Distance.Length);
        Assert.Equal([0, 200, 400],       wire.Distance);
        Assert.Equal([0, 20, 40],         wire.Time);
        Assert.Equal([7.2, 14.4, 21.6],   wire.Speed);
        Assert.Equal([100d, 120d, 140d],  wire.Altitude);
        Assert.Equal([47.0, 47.2, 47.4],  wire.Lat);
        Assert.Equal([0.0, 2.0, 4.0],     wire.CumulativeCostKj);
    }

    [Fact]
    public void FromPoints_AllIndices_IsFullResolution()
    {
        var pts  = FivePoints();
        var wire = EffortPointsWire.FromPoints(pts, [0, 1, 2, 3, 4]);
        Assert.Equal(5, wire.Distance.Length);
        Assert.Equal([0, 100, 200, 300, 400], wire.Distance);
    }

    [Fact]
    public void FromPoints_WholeNullChannel_CollapsesToNullProperty()
    {
        // No segment geometry to project against → SegmentProgress channel is null; present channels
        // (none of FivePoints sets it) stay arrays.
        var wire = EffortPointsWire.FromPoints(FivePoints(), [0, 2, 4]);
        Assert.Null(wire.SegmentProgress);
        Assert.NotNull(wire.Altitude);
        Assert.NotNull(wire.CumulativeCostKj);
    }

    [Fact]
    public void FromPoints_PartialNullChannel_KeepsArrayWithNulls()
    {
        var pts = new List<EffortDataPoint>
        {
            new() { Distance = 0,   Time = 0,  Speed = 5, SegmentProgress = 0.2 },
            new() { Distance = 100, Time = 10, Speed = 5, SegmentProgress = null },
        };
        var wire = EffortPointsWire.FromPoints(pts, [0, 1]);
        Assert.NotNull(wire.SegmentProgress);
        Assert.Equal([0.2, null], wire.SegmentProgress);
    }

    [Fact]
    public void FromPoints_Empty_ProducesEmptyColumns()
    {
        var wire = EffortPointsWire.FromPoints([], []);
        Assert.Empty(wire.Distance);
        Assert.Null(wire.Altitude);
    }
}
