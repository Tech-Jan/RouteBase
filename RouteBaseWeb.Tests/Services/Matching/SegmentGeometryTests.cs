using RouteBaseWeb.Services.Matching;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Pins SegmentGeometry.Create's zone math (TE-09 / S2.1–S2.4) — seg_GPS_ID-space arithmetic,
/// truncation toward zero, and the &gt;250 vs ≥250 fraction switch. Pure (no DB).
/// </summary>
public class SegmentGeometryTests
{
    private static SegmentGeometry Seg(int[] segIds)
    {
        int n = segIds.Length;
        var lat = new double[n];
        var lng = new double[n];
        for (int i = 0; i < n; i++) { lat[i] = 47.0 + i * 0.001; lng[i] = 11.0; }
        return SegmentGeometry.Create(new SegmentId(1), segIds, lat, lng);
    }

    private static int[] Contiguous(int start, int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = start + i;
        return ids;
    }

    [Fact]
    public void ZoneBounds_ShortSegment_20PctClampedTo40_60()
    {
        // ids 10..17: n=8, min=10, max=17, f=0.20.
        // start = min((long)(10+8*.20)=11, (long)(10+7*.40)=12) = 11
        // end   = max((long)(17-8*.20)=15, (long)(10+7*.60)=14) = 15
        var seg = Seg(Contiguous(10, 8));
        Assert.Equal(11, seg.StartZoneEnd);
        Assert.Equal(15, seg.EndZoneStart);
        Assert.Equal(10, seg.SegStartId);
        Assert.Equal(17, seg.SegEndId);
    }

    [Fact]
    public void ZoneBounds_At250_Uses20PctZones()
    {
        // n=250 is NOT > 250 → 20% fraction.
        // ids 1..250: start = min((long)(1+250*.20)=51, (long)(1+249*.40)=100) = 51
        var seg = Seg(Contiguous(1, 250));
        Assert.Equal(51, seg.StartZoneEnd);
    }

    [Fact]
    public void ZoneBounds_At251_Uses5Pct()
    {
        // n=251 > 250 → 5% fraction.
        // ids 1..251: start = min((long)(1+251*.05)=13, (long)(1+250*.40)=101) = 13
        var seg = Seg(Contiguous(1, 251));
        Assert.Equal(13, seg.StartZoneEnd);
    }

    [Fact]
    public void ZoneBounds_NonContiguousSegIds_IdSpaceArithmetic()
    {
        // Zones use seg_GPS_ID values (100/200/300), NOT ordinals 0..2. n=3, f=0.20.
        // start = min((long)(100+3*.20)=100, (long)(100+200*.40)=180) = 100
        // end   = max((long)(300-3*.20)=299, (long)(100+200*.60)=220) = 299
        var seg = Seg(new[] { 100, 200, 300 });
        Assert.Equal(100, seg.StartZoneEnd);
        Assert.Equal(299, seg.EndZoneStart);
    }

    [Fact]
    public void GapThreshold_ClampedBetween300And1500()
    {
        Assert.Equal(300, Seg(Contiguous(1, 8)).GapThreshold);     // 80 → floor 300
        Assert.Equal(1000, Seg(Contiguous(1, 100)).GapThreshold);  // 1000 in range
        Assert.Equal(1500, Seg(Contiguous(1, 200)).GapThreshold);  // 2000 → cap 1500
    }
}
