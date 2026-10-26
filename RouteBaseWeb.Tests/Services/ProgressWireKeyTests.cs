using System.Text.Json;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests (IQ-04): pin the exact JSON wire keys emitted by each progress
/// class's GetStatus, so the switch from anonymous objects to typed snapshot records cannot
/// change the contract the op-toast client relies on. Serialized with web defaults (camelCase),
/// matching ASP.NET Core controller responses (no custom naming policy in Program.cs).
/// </summary>
public class ProgressWireKeyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string[] Keys(object status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status, Web));
        var keys = new List<string>();
        foreach (var p in doc.RootElement.EnumerateObject()) keys.Add(p.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    private static readonly string[] ImportKeys =
    {
        "activityName", "completedAt", "current", "error", "failed", "imported", "isRunning",
        "phase", "queuePosition", "queueTotal", "results", "skipped", "source", "startedAt", "total"
    };

    [Fact]
    public void ActivityImportProgress_GetStatus_WireKeys_Unchanged_Idle()
    {
        var p = new ActivityImportProgress(TimeProvider.System);
        Assert.Equal(ImportKeys, Keys(p.GetStatus("unknown-user")));
    }

    [Fact]
    public void ActivityImportProgress_GetStatus_WireKeys_Unchanged_Populated()
    {
        var p = new ActivityImportProgress(TimeProvider.System);
        Assert.True(p.TryStart("u", 2, "gpx"));
        p.SetQueueInfo("u", 1, 3);
        p.RecordFileStarted("u", "a.gpx");
        p.RecordFileCompleted("u", "a.gpx", "imported", null);

        Assert.Equal(ImportKeys, Keys(p.GetStatus("u")));

        // Nested FileResult wire keys must also stay stable.
        // "error" → "detail" is a DELIBERATE rename (ActivityLifecycle batch 2): the field was never only an
        // error — it is what the UI shows as the badge tooltip, and a skip now uses it to say WHAT the file
        // already is ("in the trash as 'X'") instead of pretending to be a failure. The TS mirror
        // moved in the same commit, which is the rule this pin exists to enforce (contracts change atomically).
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p.GetStatus("u"), Web));
        var first = doc.RootElement.GetProperty("results")[0];
        var nested = new List<string>();
        foreach (var f in first.EnumerateObject()) nested.Add(f.Name);
        nested.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "detail", "fileName", "outcome" }, nested.ToArray());
    }

    private static readonly string[] AnalysisKeys =
    {
        "completedAt", "current", "error", "isRunning", "phase",
        "queuePosition", "queueTotal", "segmentName", "startedAt", "total"
    };

    [Fact]
    public void SegmentAnalysisProgress_GetStatus_WireKeys_Unchanged_Idle()
    {
        var p = new SegmentAnalysisProgress(TimeProvider.System);
        Assert.Equal(AnalysisKeys, Keys(p.GetStatus("unknown-user")));
    }

    [Fact]
    public void SegmentAnalysisProgress_GetStatus_WireKeys_Unchanged_Populated()
    {
        var p = new SegmentAnalysisProgress(TimeProvider.System);
        Assert.True(p.TryStart("u", 5));
        p.SetQueueInfo("u", 1, 2);
        p.UpdateProgress("u", 3, "Hill", "Analyzing...");
        Assert.Equal(AnalysisKeys, Keys(p.GetStatus("u")));
    }

    private static readonly string[] HeatmapKeys =
    {
        "completedAt", "current", "error", "isRunning", "phase",
        "queuePosition", "queueTotal", "segmentId", "startedAt", "total"
    };

    [Fact]
    public void HeatmapProgress_GetStatus_WireKeys_Unchanged_Idle()
    {
        var p = new HeatmapProgress(TimeProvider.System);
        Assert.Equal(HeatmapKeys, Keys(p.GetStatus("unknown-user")));
    }

    [Fact]
    public void HeatmapProgress_GetStatus_WireKeys_Unchanged_Populated()
    {
        var p = new HeatmapProgress(TimeProvider.System);
        Assert.True(p.TryStart("u", new SegmentId(42)));
        p.SetQueueInfo("u", 1, 2);
        p.UpdateProgress("u", "Painting", 3, 12);
        Assert.Equal(HeatmapKeys, Keys(p.GetStatus("u")));
    }
}
