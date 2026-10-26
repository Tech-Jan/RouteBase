using RouteBaseWeb.Services.Photos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// Real temp-dir photo staging (no host needed). PI-03: per-file sizes are recorded in the manifest so the
/// upload guard can sum staged bytes without touching the FS.
/// </summary>
public class PhotoImportStagingTests
{
    private static PhotoImportStaging NewStaging() =>
        new(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);

    private static IFormFile FormFile(string fileName, int size)
    {
        var bytes = new byte[size];
        return new FormFile(new MemoryStream(bytes), 0, size, "files", fileName);
    }

    [Fact]
    public async Task AppendFiles_RecordsSizes_LoadSumsThem()
    {
        var staging = NewStaging();
        var userId = "pi03-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(
                userId, batchId,
                new[] { FormFile("a.jpg", 3), FormFile("b.jpg", 5) },
                CancellationToken.None);

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.Equal(8, batch!.Files.Sum(f => f.SizeBytes));
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }

    [Fact]
    public async Task TryMarkCommitted_FirstCallTrue_SecondCallFalse()
    {
        var staging = NewStaging();
        var userId = "pi04-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
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
    public async Task TryMarkCommitted_MissingBatch_False()
    {
        var staging = NewStaging();
        var userId = "pi04-user-" + Guid.NewGuid().ToString("N");
        var missing = Guid.NewGuid().ToString("N");   // valid format, never created
        Assert.False(await staging.TryMarkCommittedAsync(userId, missing));
    }

    [Fact]
    public async Task AppendFiles_AfterCommit_Throws()
    {
        var staging = NewStaging();
        var userId = "pi05-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staging.AppendFilesAsync(
                userId, batchId, new[] { FormFile("late.jpg", 4) }, CancellationToken.None));
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
        var userId = "pi08-user-" + Guid.NewGuid().ToString("N");
        var batchId = staging.BeginBatch(userId, previewMaxEdge: 1024);
        try
        {
            await staging.AppendFilesAsync(
                userId, batchId, new[] { FormFile("a.jpg", 3) }, CancellationToken.None);
            Assert.True(await staging.TryMarkCommittedAsync(userId, batchId));

            var batchDir = Path.Combine(Path.GetTempPath(), "routebase-photos", userId, batchId);
            Assert.False(File.Exists(Path.Combine(batchDir, "manifest.json.tmp")));

            var batch = await staging.LoadAsync(userId, batchId);
            Assert.NotNull(batch);
            Assert.True(batch!.Committed);
        }
        finally
        {
            staging.Delete(userId, batchId);
        }
    }
}
