using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// IQ-03: the committed flag that lets AbortBatch distinguish an in-flight import from a
/// still-abortable staged batch. Uses the real temp-dir staging (no host needed).
/// </summary>
// Serial with the other real-staging tests: CleanupOrphans_EmptyUserDir_Removed wipes the shared
// routebase-import root wholesale, which would delete another test's staged batch if run concurrently.
[Collection("TrackImportRealStagingSerial")]
public class TrackImportStagingTests
{
    private static TrackImportStaging NewStaging() =>
        new(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);

    [Fact]
    public async Task TryMarkCommittedAsync_SetsFlagReadBackByLoad()
    {
        var staging = NewStaging();
        var userId = "iq03-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            var before = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(before);
            Assert.False(before!.Committed);

            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var after = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(after);
            Assert.True(after!.Committed);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task TryMarkCommittedAsync_SecondCall_ReturnsFalse()
    {
        var staging = NewStaging();
        var userId = "ie01-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));   // performs the transition
            Assert.False(await staging.TryMarkCommittedAsync(userId, batchId));  // already committed → loser
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task AppendFilesAsync_AfterCommit_Throws()
    {
        var staging = NewStaging();
        var userId = "ie06-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staging.AppendFilesAsync(
                userId, batchId, new[] { FormFile("late.gpx", "LATE") }, default));
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task MarkCommitted_LeavesNoTmpFile_AndManifestParses()
    {
        var staging = NewStaging();
        var userId = "ie02-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var batchDir = Path.Combine(RootDir, userId, batchId);
            Assert.False(File.Exists(Path.Combine(batchDir, "manifest.json.tmp")));

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.True(batch!.Committed);
            Assert.Single(batch.Files);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task TryMarkCommittedAsync_MissingBatch_ReturnsFalse()
    {
        var staging = NewStaging();
        var userId = "iq03-user-" + Guid.NewGuid().ToString("N");
        Assert.False(await staging.TryMarkCommittedAsync(userId, "does-not-exist"));
    }

    // ── GI-04: batchId must be a Guid "N" — reject path-traversal segments ──────

    [Fact]
    public void Delete_TraversalBatchId_DoesNotDeleteUserRoot()
    {
        var staging = NewStaging();
        var userId = "gi04-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            staging.Delete(userId, "..");
            Assert.True(staging.BatchExists(userId, batchId));
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task LoadAsync_TraversalBatchId_ReturnsNull()
    {
        var staging = NewStaging();
        var userId = "gi04-user-" + Guid.NewGuid().ToString("N");
        Assert.Null(await staging.LoadAsync(userId, "../evil"));
    }

    [Fact]
    public void BatchExists_NonGuidBatchId_ReturnsFalse()
    {
        var staging = NewStaging();
        var userId = "gi04-user-" + Guid.NewGuid().ToString("N");
        Assert.False(staging.BatchExists(userId, "../evil"));
    }

    // ── GI-05: suffixed collision name must not overwrite an existing staged file ──

    [Fact]
    public async Task AppendFilesAsync_TripleCollision_KeepsAllThreeFiles()
    {
        var staging = NewStaging();
        var userId = "gi05-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.gpx", "AAA") }, default);
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a_1.gpx", "BBB") }, default);
            await staging.AppendFilesAsync(userId, batchId, new[] { FormFile("a.gpx", "CCC") }, default);

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(3, batch!.Files.Count);

            var stagedNames = batch.Files.Select(f => Path.GetFileName(f.StagedPath)).ToList();
            Assert.Equal(3, stagedNames.Distinct(StringComparer.Ordinal).Count());

            var contents = new List<string>();
            foreach (var f in batch.Files)
                contents.Add(await File.ReadAllTextAsync(f.StagedPath));
            Assert.Equal(new[] { "AAA", "BBB", "CCC" }, contents.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── IE-05: per-file sizes recorded in the manifest so the guard can sum staged bytes ──

    [Fact]
    public async Task AppendFilesAsync_RecordsSizeBytes()
    {
        var staging = NewStaging();
        var userId = "ie05-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            await staging.AppendFilesAsync(userId, batchId,
                new[] { FormFile("a.gpx", "ABC"), FormFile("b.gpx", "DE") }, default);

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(5, batch!.Files.Sum(f => f.SizeBytes)); // 3 + 2
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── GI-11: manifest read-modify-write must survive concurrent chunk appends ──

    [Fact]
    public async Task AppendFilesAsync_TwoParallelChunks_AllFilesInManifest()
    {
        var staging = NewStaging();
        var userId = "gi11-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        try
        {
            var chunk1 = Enumerable.Range(0, 10).Select(i => FormFile($"a{i}.gpx", $"A{i}")).ToArray();
            var chunk2 = Enumerable.Range(0, 10).Select(i => FormFile($"b{i}.gpx", $"B{i}")).ToArray();

            await Task.WhenAll(
                staging.AppendFilesAsync(userId, batchId, chunk1, default),
                staging.AppendFilesAsync(userId, batchId, chunk2, default));

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(20, batch!.Files.Count);
            Assert.Equal(20, batch.Files.Select(f => f.StagedPath).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    // ── GI-08: cleanup decides by batch age, not user-dir age ──────────────────

    private static readonly string RootDir = Path.Combine(Path.GetTempPath(), "routebase-import");

    [Fact]
    public void CleanupOrphans_YoungBatchInOldUserDir_Survives()
    {
        var staging = NewStaging();
        var userId = "gi08-user-" + Guid.NewGuid().ToString("N");
        var oldBatch = staging.BeginBatch(userId, useFilenameAsId: false);
        var youngBatch = staging.BeginBatch(userId, useFilenameAsId: false);
        var userDir = Path.Combine(RootDir, userId);
        try
        {
            var cutoff = DateTime.UtcNow;
            staging.CleanupOrphansCore(cutoff, dir =>
                Path.GetFileName(dir) == oldBatch ? DateTime.MinValue : DateTime.MaxValue);

            Assert.False(Directory.Exists(Path.Combine(userDir, oldBatch)));
            Assert.True(Directory.Exists(Path.Combine(userDir, youngBatch)));
            Assert.True(Directory.Exists(userDir));
        }
        finally
        {
            if (Directory.Exists(userDir)) Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void CleanupOrphans_EmptyUserDir_Removed()
    {
        var staging = NewStaging();
        var userId = "gi08-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, useFilenameAsId: false);
        var userDir = Path.Combine(RootDir, userId);
        staging.Delete(userId, batchId); // leaves an empty user dir behind

        try
        {
            staging.CleanupOrphansCore(DateTime.UtcNow, _ => DateTime.MinValue);
            Assert.False(Directory.Exists(userDir));
        }
        finally
        {
            if (Directory.Exists(userDir)) Directory.Delete(userDir, recursive: true);
        }
    }

    private static IFormFile FormFile(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", fileName);
    }
}
