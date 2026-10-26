using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Gpx;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

// Serialized against all other collections: this test stages real files under the shared temp root
// (routebase-import), which TrackImportStagingTests' CleanupOrphans test wipes wholesale — running them
// concurrently would delete our staged files mid-import.
[CollectionDefinition("TrackImportRealStagingSerial", DisableParallelization = true)]
public class TrackImportRealStagingSerialCollection { }

/// <summary>
/// inval-T2 (GPX path): <see cref="TrackFileImportOrchestrator.ImportBatchParallelAsync"/> fires
/// <c>onActivityImported</c> exactly once per successfully persisted track with its fresh TrackId, and
/// never on a pre-skipped (duplicate) or failed (parse/persist) file. Uses the real temp-dir staging
/// (files must exist on disk for the persister to open) with hand-rolled parser/import/timezone fakes.
/// </summary>
[Collection("TrackImportRealStagingSerial")]
public class TrackFileImportOrchestratorTests
{
    private static IFormFile FormFile(string fileName, string content = "x")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", fileName);
    }

    [Fact]
    public async Task ImportBatchParallelAsync_FiresCallbackOnlyForImportedTracks()
    {
        var userId = "gpx-orch-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);

        try
        {
            await staging.AppendFilesAsync(userId, batchId,
                new[] { FormFile("fresh.gpx"), FormFile("dup.gpx"), FormFile("bad.gpx") }, default);

            var parser = new FakeParser();
            parser.ByName["fresh.gpx"] = (ok: true, extId: 3);   // imports → TrackId 3
            parser.ByName["dup.gpx"]   = (ok: true, extId: 4);   // parses, but already exists → skipped
            parser.ByName["bad.gpx"]   = (ok: false, extId: 0);  // parse fails

            var import = new FakeImportService();
            import.Existing.Add(4);                              // makes dup.gpx a pre-skip

            var orchestrator = new TrackFileImportOrchestrator(
                parser, import, staging, new FakeTimezoneResolver(),
                NullLogger<TrackFileImportOrchestrator>.Instance);

            var importedIds = new List<int>();
            var (imported, skipped, failed) = await orchestrator.ImportBatchParallelAsync(
                userId, batchId,
                onFileStarted:   _ => { },
                onFileCompleted: (_, _, _) => { },
                onActivityImported: id => { lock (importedIds) importedIds.Add(id); },
                default);

            Assert.Equal((1, 1, 1), (imported, skipped, failed)); // 1 imported, 1 dup-skipped, 1 parse-failed
            Assert.Equal(new[] { 3 }, importedIds.ToArray());     // callback fired once, only for the fresh track
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task ImportBatchParallelAsync_ChainedFileSplits_CountsEachActivity()
    {
        // OBS-2: a single staged file that parses to N activities (chained FIT) must import as N — the
        // batch count and the per-activity match callback both reflect every piece, not just the first.
        var userId = "gpx-orch-split-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);

        try
        {
            await staging.AppendFilesAsync(userId, batchId,
                new[] { FormFile("chained.fit"), FormFile("solo.gpx") }, default);

            var parser = new FakeParser();
            parser.SplitInto["chained.fit"] = new long[] { 10, 11, 12 }; // one file → three activities
            parser.ByName["solo.gpx"] = (ok: true, extId: 20);

            var orchestrator = new TrackFileImportOrchestrator(
                parser, new FakeImportService(), staging, new FakeTimezoneResolver(),
                NullLogger<TrackFileImportOrchestrator>.Instance);

            var importedIds = new List<int>();
            var completions = new List<(string name, string status)>();
            var (imported, skipped, failed) = await orchestrator.ImportBatchParallelAsync(
                userId, batchId,
                onFileStarted:   _ => { },
                onFileCompleted: (name, status, _) => { lock (completions) completions.Add((name, status)); },
                onActivityImported: id => { lock (importedIds) importedIds.Add(id); },
                default);

            Assert.Equal((4, 0, 0), (imported, skipped, failed));           // 3 split pieces + 1 solo
            Assert.Equal(new[] { 10, 11, 12, 20 }, importedIds.OrderBy(x => x).ToArray());

            // Split pieces surface as distinct progress lines: base + " (2)" + " (3)".
            var chainedNames = completions.Where(c => c.name.StartsWith("chained.fit", StringComparison.Ordinal))
                .Select(c => c.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "chained.fit", "chained.fit (2)", "chained.fit (3)" }, chainedNames);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task ImportBatch_OnFileStartedThrows_FileCountedFailed_MethodReturns()
    {
        var userId = "gpx-orch-ie07-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);

        try
        {
            await staging.AppendFilesAsync(userId, batchId,
                new[] { FormFile("throw.gpx"), FormFile("ok.gpx") }, default);

            var parser = new FakeParser();
            parser.ByName["throw.gpx"] = (ok: true, extId: 1);
            parser.ByName["ok.gpx"]    = (ok: true, extId: 2);

            var orchestrator = new TrackFileImportOrchestrator(
                parser, new FakeImportService(), staging, new FakeTimezoneResolver(),
                NullLogger<TrackFileImportOrchestrator>.Instance);

            // A throwing progress callback must be caught (counted failed), not wedge the bounded channel.
            var run = orchestrator.ImportBatchParallelAsync(
                userId, batchId,
                onFileStarted:   name => { if (name == "throw.gpx") throw new InvalidOperationException("boom"); },
                onFileCompleted: (_, _, _) => { },
                onActivityImported: _ => { },
                default);

            var (_, _, failed) = await run.WaitAsync(TimeSpan.FromSeconds(30)); // no hang
            Assert.True(failed >= 1);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── CR5-03 pipeline pins: skeleton semantics shared with StravaImportOrchestrator ──

    [Fact]
    public async Task ImportBatch_EachFileGetsExactlyOneCompletion_WithFinalStatus()
    {
        var userId = "gpx-orch-seq-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);

        try
        {
            await staging.AppendFilesAsync(userId, batchId,
                new[] { FormFile("fresh.gpx"), FormFile("dup.gpx"), FormFile("bad.gpx") }, default);

            var parser = new FakeParser();
            parser.ByName["fresh.gpx"] = (ok: true, extId: 3);
            parser.ByName["dup.gpx"]   = (ok: true, extId: 4);
            parser.ByName["bad.gpx"]   = (ok: false, extId: 0);

            var import = new FakeImportService();
            import.Existing.Add(4);

            var orchestrator = new TrackFileImportOrchestrator(
                parser, import, staging, new FakeTimezoneResolver(),
                NullLogger<TrackFileImportOrchestrator>.Instance);

            var completions = new List<(string name, string status)>();
            await orchestrator.ImportBatchParallelAsync(
                userId, batchId,
                onFileStarted:   _ => { },
                onFileCompleted: (name, status, _) => { lock (completions) completions.Add((name, status)); },
                onActivityImported: _ => { },
                default);

            Assert.Equal(3, completions.Count); // exactly one completion per file
            var byName = completions.ToDictionary(c => c.name, c => c.status, StringComparer.Ordinal);
            Assert.Equal("imported", byName["fresh.gpx"]);
            Assert.Equal("skipped",  byName["dup.gpx"]);
            Assert.Equal("failed",   byName["bad.gpx"]);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task ImportBatch_PreCanceledToken_ThrowsOperationCanceled()
    {
        var userId = "gpx-orch-cancel-" + Guid.NewGuid().ToString("N");
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);

        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("ok.gpx") }, default);

            var parser = new FakeParser();
            parser.ByName["ok.gpx"] = (ok: true, extId: 1);

            var orchestrator = new TrackFileImportOrchestrator(
                parser, new FakeImportService(), staging, new FakeTimezoneResolver(),
                NullLogger<TrackFileImportOrchestrator>.Instance);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                orchestrator.ImportBatchParallelAsync(
                    userId, batchId,
                    onFileStarted:   _ => { },
                    onFileCompleted: (_, _, _) => { },
                    onActivityImported: _ => { },
                    cts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakeParser : ITrackFileParser
    {
        public Dictionary<string, (bool ok, long extId)> ByName { get; } = new(StringComparer.Ordinal);

        // Files that split into N activities (chained-FIT analogue): fileName → the external ids of
        // the pieces. Takes precedence over ByName; every piece parses successfully.
        public Dictionary<string, long[]> SplitInto { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false)
        {
            if (SplitInto.TryGetValue(fileName, out var ids))
                return ids.Select((id, i) => Ok(id, i == 0 ? fileName : $"{fileName} ({i + 1})")).ToList();

            if (!ByName.TryGetValue(fileName, out var v) || !v.ok)
                return [new GpxParseResult { Success = false, ErrorMessage = "parse failed" }];

            return [Ok(v.extId, fileName)];
        }

        private static GpxParseResult Ok(long extId, string name) => new()
        {
            Success = true,
            Summary = new ExternalActivitySummary
            {
                ExternalId = extId,
                ProviderId = "gpx",
                Source     = "gpx",
                Name       = name,
            },
            GpsPoints = [],
        };
    }

    private sealed class FakeImportService : IActivityImportService
    {
        public HashSet<long> Existing { get; } = new();

        /// <summary>External id → the lifecycle it already sits in (default: a plain released duplicate).</summary>
        public Dictionary<long, string> ExistingLifecycle { get; } = new();

        public Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId) =>
            Task.FromResult(Existing.Contains(externalId)
                ? new ExistingActivity(
                    (int)externalId, $"Ride {externalId}",
                    ExistingLifecycle.GetValueOrDefault(externalId, LifecycleStates.Released), null)
                : null);

        public Task<Activity?> ImportFromSummaryAsync(
            ExternalActivitySummary summary, List<ExternalGpsPoint> gpsPoints, string userId)
            => Task.FromResult<Activity?>(new Activity { TrackId = new TrackId((int)summary.ExternalId) });

        public Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points) => throw new NotSupportedException();
        public double ComputeTotalDescent(List<ExternalGpsPoint> points) => throw new NotSupportedException();
        public Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points) => throw new NotSupportedException();
        public Task UpsertActivityBboxAsync(TrackId trackId) => throw new NotSupportedException();
    }

    private sealed class FakeTimezoneResolver : IActivityTimezoneResolver
    {
        public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId)
            => Task.FromResult<TimeSpan?>(null);
    }
}
