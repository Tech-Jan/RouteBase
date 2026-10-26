using System.Text.Json;
using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Segments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// CR5-01 C1 pins: the exact op-toast wire shape served by /api/queue/progress — key set AND
/// load-bearing values — for every branch: import running (strava/gpx discriminator, all three
/// detail texts), analysis, heatmap, queued-synthetic, most-recently-finished, and idle.
/// The queue-handler extraction (I5.1) and progress-core composition (I5.2) must keep these
/// byte-identical. Serialized with web defaults (camelCase), matching controller responses.
///
/// D30: the body is the [JsonPolymorphic] ProgressResponse base, so the wire now carries the ADDITIVE
/// `$type` shape discriminator (idle|basic|import) alongside `operationType` — pinned below. The value is
/// serialized through its declared ProgressResponse type (as the controller's ActionResult&lt;ProgressResponse&gt;
/// exposes it), reproducing MVC's base-declared serialization that writes `$type`.
/// </summary>
public class QueueProgressWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubCurrentUser : ICurrentUserService
    {
        public string GetUserId() => "u";
        public string? TryGetUserId() => "u";
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string userId) { }
        public IDisposable BeginUserScope(string userId) => NoopUserScope.Instance;
    }

    // Empty DI container solely to hand out an IServiceScopeFactory the handlers' ctors require;
    // none of the capability members exercised here (GetQueueProgress/SetQueueInfo/QueueDiscriminator)
    // ever create a scope, so a real service graph is unnecessary.
    private static readonly IServiceScopeFactory ScopeFactory =
        new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private sealed record Fixture(
        QueueApiController Controller, OperationQueue Queue, ActivityImportProgress Import,
        SegmentAnalysisProgress Analysis, HeatmapProgress Heatmap);

    /// <summary>Builds the controller against the real handler registry (mirrors Program.cs) so the
    /// op-toast wire shape is produced through each handler's GetQueueProgress/QueueDiscriminator
    /// capability (arch finding 1), not a queue-layer switch over OperationType.</summary>
    private static Fixture Build()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var import = new ActivityImportProgress(TimeProvider.System);
        var analysis = new SegmentAnalysisProgress(TimeProvider.System);
        var heatmap = new HeatmapProgress(TimeProvider.System);

        IOperationHandler[] handlers =
        {
            new ActivityImportHandler(ScopeFactory, import, NullLogger<ActivityImportHandler>.Instance),
            new TrackImportHandler(ScopeFactory, import, NullLogger<TrackImportHandler>.Instance),
            new SegmentAnalysisHandler(ScopeFactory, analysis, NullLogger<SegmentAnalysisHandler>.Instance),
            new SingleSegmentAnalysisHandler(ScopeFactory, analysis, NullLogger<SingleSegmentAnalysisHandler>.Instance),
            new HeatmapGenerationHandler(ScopeFactory, heatmap, NullLogger<HeatmapGenerationHandler>.Instance),
            new RouteBaseWeb.Services.Photos.PhotoImportHandler(ScopeFactory,
                new RouteBaseWeb.Services.Photos.PhotoImportProgress(TimeProvider.System), NullLogger<RouteBaseWeb.Services.Photos.PhotoImportHandler>.Instance),
            new RouteBaseWeb.Services.TrackEdit.TrackEditDeriveHandler(ScopeFactory, analysis,
                NullLogger<RouteBaseWeb.Services.TrackEdit.TrackEditDeriveHandler>.Instance),
        };

        var controller = new QueueApiController(
            queue, handlers, new StubCurrentUser(),
            NullLogger<QueueApiController>.Instance);
        return new Fixture(controller, queue, import, analysis, heatmap);
    }

    private static JsonElement GetProgress(Fixture f)
    {
        // Serialize through the declared ProgressResponse type (D30) — matching MVC's base-declared write for
        // ActionResult<ProgressResponse> — so the polymorphic `$type` discriminator appears exactly as on the wire.
        ProgressResponse? body = f.Controller.GetProgress().Value;
        Assert.NotNull(body);
        return JsonDocument.Parse(JsonSerializer.Serialize(body, Web)).RootElement.Clone();
    }

    private static string[] Keys(JsonElement e)
    {
        var keys = new List<string>();
        foreach (var p in e.EnumerateObject()) keys.Add(p.Name);
        keys.Sort(StringComparer.Ordinal);
        return keys.ToArray();
    }

    // `$type` sorts first (ordinal: '$' 0x24 precedes letters) — the additive D30 shape discriminator.

    /// <summary>NormaliseImport wire keys (superset: per-file counters + results).</summary>
    private static readonly string[] ImportShapeKeys =
    {
        "$type", "completedAt", "completedSummary", "current", "detail", "error", "failed", "imported",
        "isRunning", "operationType", "phase", "queuePosition", "queueTotal", "results",
        "skipped", "startedAt", "total"
    };

    /// <summary>NormaliseAnalysis/NormaliseHeatmap and the queued-synthetic state share this set.</summary>
    private static readonly string[] BasicShapeKeys =
    {
        "$type", "completedAt", "completedSummary", "current", "detail", "error", "isRunning",
        "operationType", "phase", "queuePosition", "queueTotal", "startedAt", "total"
    };

    [Fact]
    public void Idle_MinimalShape()
    {
        var e = GetProgress(Build());

        Assert.Equal(new[] { "$type", "isRunning", "operationType", "phase" }, Keys(e));
        Assert.Equal("idle", e.GetProperty("$type").GetString());
        Assert.Equal("idle", e.GetProperty("operationType").GetString());
        Assert.False(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("", e.GetProperty("phase").GetString());
    }

    [Fact]
    public void ImportRunning_Strava_FullShapeAndValues()
    {
        var f = Build();
        Assert.True(f.Import.TryStart("u", 5, "strava"));
        f.Import.SetQueueInfo("u", 2, 3);
        f.Import.UpdateProgress("u", 2, "Morning Ride", "Importing...", 1, 1, 0);

        var e = GetProgress(f);

        Assert.Equal(ImportShapeKeys, Keys(e));
        Assert.Equal("import", e.GetProperty("$type").GetString());
        Assert.Equal("import", e.GetProperty("operationType").GetString());
        Assert.True(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Importing...", e.GetProperty("phase").GetString());
        Assert.Equal(2, e.GetProperty("current").GetInt32());
        Assert.Equal(5, e.GetProperty("total").GetInt32());
        Assert.Equal("2 / 5 — Morning Ride", e.GetProperty("detail").GetString());
        Assert.Equal("1 imported, 1 skipped, 0 failed", e.GetProperty("completedSummary").GetString());
        Assert.Equal(1, e.GetProperty("imported").GetInt32());
        Assert.Equal(1, e.GetProperty("skipped").GetInt32());
        Assert.Equal(0, e.GetProperty("failed").GetInt32());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("error").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, e.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("completedAt").ValueKind);
        Assert.Equal(2, e.GetProperty("queuePosition").GetInt32());
        Assert.Equal(3, e.GetProperty("queueTotal").GetInt32());
        Assert.Equal(JsonValueKind.Array, e.GetProperty("results").ValueKind);
    }

    [Fact]
    public void ImportRunning_Gpx_TrackImportDiscriminatorAndFilesDetail()
    {
        var f = Build();
        Assert.True(f.Import.TryStart("u", 3, "gpx"));
        f.Import.UpdateProgress("u", 1, "", "Importing...", 1, 0, 0);

        var e = GetProgress(f);

        Assert.Equal("trackImport", e.GetProperty("operationType").GetString());
        Assert.Equal("1 / 3 files", e.GetProperty("detail").GetString());
    }

    [Fact]
    public void ImportRunning_NoTotalYet_SourceSpecificPreparingDetail()
    {
        // Non-GPX preparing detail is provider-neutral (arch finding 3, tranche 1) — no "…from Strava".
        var strava = Build();
        Assert.True(strava.Import.TryStart("u", 0, "strava"));
        Assert.Equal("Fetching activity list...",
            GetProgress(strava).GetProperty("detail").GetString());

        var gpx = Build();
        Assert.True(gpx.Import.TryStart("u", 0, "gpx"));
        Assert.Equal("Preparing files...", GetProgress(gpx).GetProperty("detail").GetString());
    }

    [Fact]
    public void ImportRunning_QueueTotal_IncludesLivePendingCount()
    {
        var f = Build();
        Assert.True(f.Import.TryStart("u", 5, "strava"));
        f.Import.SetQueueInfo("u", 1, 1);
        // Two more items enqueued while running: queueTotal = max(1, 1 + pending 2) = 3.
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
        { Type = OperationQueue.OperationType.SegmentAnalysis, UserId = "u" }));
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
        { Type = OperationQueue.OperationType.SegmentAnalysis, UserId = "u" }));

        var e = GetProgress(f);

        Assert.Equal(1, e.GetProperty("queuePosition").GetInt32());
        Assert.Equal(3, e.GetProperty("queueTotal").GetInt32());
    }

    [Fact]
    public void AnalysisRunning_FullShapeAndValues()
    {
        var f = Build();
        Assert.True(f.Analysis.TryStart("u", 4));
        f.Analysis.SetQueueInfo("u", 1, 2);
        f.Analysis.UpdateProgress("u", 2, "Hill", "Analyzing...");

        var e = GetProgress(f);

        Assert.Equal(BasicShapeKeys, Keys(e));
        Assert.Equal("basic", e.GetProperty("$type").GetString());
        Assert.Equal("analysis", e.GetProperty("operationType").GetString());
        Assert.True(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Analyzing...", e.GetProperty("phase").GetString());
        Assert.Equal(2, e.GetProperty("current").GetInt32());
        Assert.Equal(4, e.GetProperty("total").GetInt32());
        Assert.Equal("2 / 4 — Hill", e.GetProperty("detail").GetString());
        Assert.Equal("4 segments analyzed", e.GetProperty("completedSummary").GetString());
        Assert.Equal(1, e.GetProperty("queuePosition").GetInt32());
        Assert.Equal(2, e.GetProperty("queueTotal").GetInt32());
    }

    [Fact]
    public void AnalysisRunning_EmptySegmentName_CountOnlyDetail()
    {
        var f = Build();
        Assert.True(f.Analysis.TryStart("u", 4));
        f.Analysis.UpdateProgress("u", 2, "", "Analyzing...");

        Assert.Equal("2 / 4 segments", GetProgress(f).GetProperty("detail").GetString());
    }

    [Fact]
    public void HeatmapRunning_FullShapeAndValues()
    {
        var f = Build();
        Assert.True(f.Heatmap.TryStart("u", new SegmentId(42)));
        f.Heatmap.SetQueueInfo("u", 1, 1);
        f.Heatmap.UpdateProgress("u", "Painting", 3, 12);

        var e = GetProgress(f);

        Assert.Equal(BasicShapeKeys, Keys(e));
        Assert.Equal("basic", e.GetProperty("$type").GetString());
        Assert.Equal("heatmap", e.GetProperty("operationType").GetString());
        Assert.True(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Painting", e.GetProperty("phase").GetString());
        Assert.Equal(3, e.GetProperty("current").GetInt32());
        Assert.Equal(12, e.GetProperty("total").GetInt32());
        Assert.Equal("3 / 12", e.GetProperty("detail").GetString());
        Assert.Equal("Heatmap ready", e.GetProperty("completedSummary").GetString());
    }

    [Fact]
    public void HeatmapRunning_NoCounts_EmptyDetail()
    {
        var f = Build();
        Assert.True(f.Heatmap.TryStart("u", new SegmentId(42)));

        Assert.Equal("", GetProgress(f).GetProperty("detail").GetString());
    }

    [Fact]
    public void QueuedNotYetRunning_SyntheticState()
    {
        var f = Build();
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
        { Type = OperationQueue.OperationType.TrackImport, UserId = "u" }));

        var e = GetProgress(f);

        Assert.Equal(BasicShapeKeys, Keys(e));
        Assert.Equal("basic", e.GetProperty("$type").GetString());
        Assert.Equal("trackImport", e.GetProperty("operationType").GetString());
        Assert.True(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Queued...", e.GetProperty("phase").GetString());
        Assert.Equal(0, e.GetProperty("current").GetInt32());
        Assert.Equal(0, e.GetProperty("total").GetInt32());
        Assert.Equal("", e.GetProperty("detail").GetString());
        Assert.Equal("", e.GetProperty("completedSummary").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("completedAt").ValueKind);
        Assert.Equal(1, e.GetProperty("queuePosition").GetInt32());
        Assert.Equal(1, e.GetProperty("queueTotal").GetInt32());
    }

    [Theory]
    [InlineData(OperationQueue.OperationType.TrackImport, "trackImport")]
    [InlineData(OperationQueue.OperationType.ActivityImport, "import")]
    [InlineData(OperationQueue.OperationType.SegmentAnalysis, "analysis")]
    [InlineData(OperationQueue.OperationType.SingleSegmentAnalysis, "analysis")]
    [InlineData(OperationQueue.OperationType.HeatmapGeneration, "heatmap")]
    [InlineData(OperationQueue.OperationType.PhotoImport, "analysis")] // falls to the default arm
    public void QueuedNotYetRunning_DiscriminatorPerType(OperationQueue.OperationType type, string expected)
    {
        var f = Build();
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem { Type = type, UserId = "u" }));

        Assert.Equal(expected, GetProgress(f).GetProperty("operationType").GetString());
    }

    [Fact]
    public void ImportFinished_ReturnedWithCompletedShape()
    {
        var f = Build();
        Assert.True(f.Import.TryStart("u", 5, "strava"));
        f.Import.Complete("u", 2, 1, 0);

        var e = GetProgress(f);

        Assert.Equal(ImportShapeKeys, Keys(e));
        Assert.Equal("import", e.GetProperty("operationType").GetString());
        Assert.False(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Completed", e.GetProperty("phase").GetString());
        Assert.Equal("2 imported, 1 skipped, 0 failed", e.GetProperty("completedSummary").GetString());
        Assert.NotEqual(JsonValueKind.Null, e.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public void AnalysisFinished_ReturnedWithCompletedShape()
    {
        var f = Build();
        Assert.True(f.Analysis.TryStart("u", 4));
        f.Analysis.Complete("u");

        var e = GetProgress(f);

        Assert.Equal("analysis", e.GetProperty("operationType").GetString());
        Assert.False(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Completed", e.GetProperty("phase").GetString());
    }

    [Fact]
    public void HeatmapFinished_ReturnedWithCompletedShape()
    {
        var f = Build();
        Assert.True(f.Heatmap.TryStart("u", new SegmentId(42)));
        f.Heatmap.Complete("u");

        var e = GetProgress(f);

        Assert.Equal("heatmap", e.GetProperty("operationType").GetString());
        Assert.False(e.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Completed", e.GetProperty("phase").GetString());
    }

    [Fact]
    public void FailedImport_CarriesErrorAndFailedPhase()
    {
        var f = Build();
        Assert.True(f.Import.TryStart("u", 5, "strava"));
        f.Import.Fail("u", "Cancelled by user");

        var e = GetProgress(f);

        Assert.Equal("Failed", e.GetProperty("phase").GetString());
        Assert.Equal("Cancelled by user", e.GetProperty("error").GetString());
        Assert.False(e.GetProperty("isRunning").GetBoolean());
    }
}
