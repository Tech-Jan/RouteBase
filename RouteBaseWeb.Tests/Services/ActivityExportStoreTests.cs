using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// The export store keys a finished ZIP by (jobId, owner) in the FILENAME rather than in a table. That
/// choice is what makes the download endpoint a pure path computation with no ownership lookup — so the
/// thing worth pinning is that the owner half actually participates: a correct job id under the wrong user
/// must resolve to nothing, exactly like an id that was never issued.
/// </summary>
public class ActivityExportStoreTests : IDisposable
{
    private const string Me = "me";
    private const string Other = "other";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rb-export-store-" + Guid.NewGuid().ToString("N"));

    private ActivityExportStore NewStore() =>
        new(new ActivityExportStorageOptions { Root = _root }, NullLogger<ActivityExportStore>.Instance);

    private string BuiltFile(string content = "zip-bytes")
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CommittedExport_IsReadableByItsOwner()
    {
        var store = NewStore();
        store.Commit(Me, jobId: 7, BuiltFile("hello"));

        using var stream = store.OpenRead(Me, 7);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void AnotherUsersJobId_ResolvesToNothing()
    {
        var store = NewStore();
        store.Commit(Other, jobId: 7, BuiltFile());

        Assert.Null(store.OpenRead(Me, 7));   // right id, wrong owner
        Assert.Null(store.OpenRead(Me, 999)); // never issued — indistinguishable, by design
    }

    /// <summary>Commit MOVES the built file: a copy would leave the export's temp file behind on every run.</summary>
    [Fact]
    public void Commit_ConsumesTheBuiltFile()
    {
        var store = NewStore();
        var built = BuiltFile();

        store.Commit(Me, jobId: 1, built);

        Assert.False(File.Exists(built));
        Assert.NotNull(store.OpenRead(Me, 1));
    }

    [Fact]
    public void NextJobId_IsUnique()
    {
        var store = NewStore();
        var ids = Enumerable.Range(0, 50).Select(_ => store.NextJobId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>The sweep is the only thing that reclaims export ZIPs — the file is their whole record.</summary>
    [Fact]
    public void SweepOlderThan_DeletesOnlyTheExpired()
    {
        var store = NewStore();
        store.Commit(Me, jobId: 1, BuiltFile()); // "old" — backdated below
        store.Commit(Me, jobId: 2, BuiltFile()); // fresh

        var now = DateTimeOffset.UtcNow;
        var oldPath = Directory.EnumerateFiles(Path.Combine(_root, "exports"), "1_*.zip", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(oldPath, now.UtcDateTime - TimeSpan.FromHours(48));

        var deleted = store.SweepOlderThan(TimeSpan.FromHours(24), now);

        Assert.Equal(1, deleted);
        Assert.Null(store.OpenRead(Me, 1));
        Assert.NotNull(store.OpenRead(Me, 2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
