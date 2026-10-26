using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Sync;
using Xunit;

namespace RouteBaseWeb.Tests;

/// <summary>
/// K: Seq is non-nullable on the wire (TS PointPayload requires it; this pins the C# side matching, not
/// loosening TS to it). Both emit paths (RecordingService.AppendPointsCoreAsync's SignalR broadcast,
/// GetPointsAfterSeqAsync's REST catch-up) always stamp it before a point reaches this DTO — see
/// LivePointDto.cs's doc comment. The throw-branch is the seam's failsafe, not reachable today: it exists
/// so a FUTURE caller that feeds this a parser-produced point (where Seq genuinely can be null) fails loud
/// instead of silently emitting garbage. (Ts dropped 2026-07-22, perf-11 — its timestamp-precision
/// contract is now pinned at RecordingStagingStore.)
/// </summary>
public class LivePointDtoTests
{
    private static ExternalGpsPoint Point(long? seq) => new()
    {
        Lat = 47.0, Lng = 11.0, Seq = seq,
    };

    [Fact]
    public void From_ANormalStagedPoint_CarriesNonNullSeq()
    {
        var dto = LivePointDto.From(Point(3));

        Assert.Equal(3, dto.Seq);
    }

    [Fact]
    public void From_ANullSeq_ThrowsRatherThanSilentlyEmittingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LivePointDto.From(Point(null)));
        Assert.Contains("Seq", ex.Message, StringComparison.Ordinal);
    }
}
