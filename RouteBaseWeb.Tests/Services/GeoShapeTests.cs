using RouteBaseWeb.Models.Geometry;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>Tests for the pure geometry value objects used by shape-defined segments.</summary>
public class GeoShapeTests
{
    [Fact]
    public void GeoRect_NormalisesCorners_RegardlessOfOrder()
    {
        var a = new GeoRect(47.0, 47.5, 8.0, 8.5);
        var b = new GeoRect(47.5, 47.0, 8.5, 8.0); // opposite corner order

        Assert.Equal(a.MinLat, b.MinLat);
        Assert.Equal(a.MaxLat, b.MaxLat);
        Assert.Equal(a.MinLong, b.MinLong);
        Assert.Equal(a.MaxLong, b.MaxLong);
    }

    [Theory]
    [InlineData(47.25, 8.25, true)]   // centre
    [InlineData(47.0, 8.0, true)]     // corner (inclusive)
    [InlineData(47.5, 8.5, true)]     // opposite corner (inclusive)
    [InlineData(47.6, 8.25, false)]   // north of box
    [InlineData(47.25, 7.9, false)]   // west of box
    public void GeoRect_Contains_IsInclusiveOfEdges(double lat, double lng, bool expected)
    {
        var rect = new GeoRect(47.0, 47.5, 8.0, 8.5);
        Assert.Equal(expected, rect.Contains(lat, lng));
    }

    [Fact]
    public void GeoRect_Bbox_EqualsItself()
    {
        var rect = new GeoRect(47.0, 47.5, 8.0, 8.5);
        var bbox = rect.Bbox;
        Assert.Equal(rect.MinLat, bbox.MinLat);
        Assert.Equal(rect.MaxLat, bbox.MaxLat);
        Assert.Equal(rect.MinLong, bbox.MinLong);
        Assert.Equal(rect.MaxLong, bbox.MaxLong);
    }

    [Fact]
    public void Bbox_Overlaps_TrueWhenOverlapping()
    {
        var a = new GeoBoundingBox(47.0, 47.5, 8.0, 8.5);
        var b = new GeoBoundingBox(47.4, 47.9, 8.4, 8.9);
        Assert.True(a.Overlaps(b));
        Assert.True(b.Overlaps(a));
    }

    [Fact]
    public void Bbox_Overlaps_TrueWhenEdgesTouch()
    {
        var a = new GeoBoundingBox(47.0, 47.5, 8.0, 8.5);
        var b = new GeoBoundingBox(47.5, 48.0, 8.5, 9.0); // shares the NE corner
        Assert.True(a.Overlaps(b));
    }

    [Fact]
    public void Bbox_Overlaps_FalseWhenDisjoint()
    {
        var a = new GeoBoundingBox(47.0, 47.5, 8.0, 8.5);
        var b = new GeoBoundingBox(48.0, 48.5, 9.0, 9.5);
        Assert.False(a.Overlaps(b));
        Assert.False(b.Overlaps(a));
    }

    // ---------------------------------------------------------------- Liang–Barsky segment clip

    // Rect lng 10..12, lat 0..10 (a thin box a sparse track can straddle in one segment).
    private static readonly GeoRect Slab = new(0, 10, 10, 12);

    [Theory]
    [InlineData(5, 8, 5, 15, true)]    // fly-through: both endpoints outside, edge crosses the box
    [InlineData(5, 11, 5, 11, true)]   // degenerate segment sitting inside
    [InlineData(5, 8, 5, 9, false)]    // both west of the box
    [InlineData(5, 13, 5, 20, false)]  // both east of the box
    [InlineData(5, 11, 5, 20, true)]   // one endpoint inside
    [InlineData(20, 8, 20, 15, false)] // crosses the lng band but north of the lat band
    public void GeoRect_IntersectsSegment_Liang_Barsky(double lat0, double lng0, double lat1, double lng1, bool expected)
    {
        Assert.Equal(expected, Slab.IntersectsSegment(lat0, lng0, lat1, lng1));
    }
}
