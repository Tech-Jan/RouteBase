using RouteBaseWeb.Services.Sql;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Sql;

public class AnalysisScopeTests
{
    private static SqlBatch Batch() => new SqlBatch("placeholder");

    // ---- DeleteFilter (the only filter AnalysisScope still emits since Cutover) ----

    [Fact]
    public void DeleteFilter_SegmentIdFirst_ThenTrackId()
    {
        var scope = new AnalysisScope(new SegmentId(9), "me", new List<string>());
        var b = Batch();
        var f = scope.DeleteFilter(b);
        // Ordinal: matching generated SQL text, not culture-collated prose.
        Assert.StartsWith("WHERE segment_id = $segmentId", f, StringComparison.Ordinal);
        Assert.Contains("AND track_id IN (SELECT track_ID FROM Activity WHERE user_id = $userId)", f, StringComparison.Ordinal);
        Assert.Equal(9, (int)b.Params.Single(p => p.ParameterName == "$segmentId").Value!);
    }
}
