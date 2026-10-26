using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.ErrorLog;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.ErrorLog;

/// <summary>
/// Round-trip, filtering, truncation and retention semantics for <see cref="ErrorLogStore"/> against a real
/// (in-memory) SQLite context, same fixture style as DemTileStoreTests. Table created via the schema manager.
/// </summary>
public class ErrorLogStoreTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ErrorLogStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:errorlog_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        // One connection held open so the shared-cache in-memory DB survives the store's open/close cycles.
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureErrorLogTableAsync();
        _store = new ErrorLogStore(_context, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    private static ErrorLogEntry Entry(
        string level = "Error", string source = "api", string? ts = null, string message = "boom") =>
        new(ts ?? Now(), source, "Cat.Egory", level, message,
            "System.InvalidOperationException", "at Foo()", "GET /api/x", "user-1");

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Write_ThenRead_RoundTripsAllColumns()
    {
        var e = Entry();
        await _store.WriteAsync(e);

        var rows = await _store.GetPageAsync(null, null, 1, 50);

        var read = Assert.Single(rows);
        Assert.Equal(e.TimestampUtc, read.TimestampUtc);
        Assert.Equal("api", read.Source);
        Assert.Equal("Cat.Egory", read.Category);
        Assert.Equal("Error", read.Level);
        Assert.Equal("boom", read.Message);
        Assert.Equal("System.InvalidOperationException", read.ExceptionType);
        Assert.Equal("at Foo()", read.StackTrace);
        Assert.Equal("GET /api/x", read.Path);
        Assert.Equal("user-1", read.UserId);
    }

    [Fact]
    public async Task Write_NullOptionalColumns_ReadBackAsNull()
    {
        await _store.WriteAsync(new ErrorLogEntry(Now(), "background", "Bg", "Critical", "x",
            null, null, null, null));

        var read = Assert.Single(await _store.GetPageAsync(null, null, 1, 50));
        Assert.Null(read.ExceptionType);
        Assert.Null(read.StackTrace);
        Assert.Null(read.Path);
        Assert.Null(read.UserId);
    }

    [Fact]
    public async Task Write_TruncatesMessageAndStackToCaps()
    {
        var longMsg = new string('m', 3000);
        var longStack = new string('s', 9000);
        await _store.WriteAsync(new ErrorLogEntry(Now(), "api", "C", "Error", longMsg,
            "T", longStack, null, null));

        var read = Assert.Single(await _store.GetPageAsync(null, null, 1, 50));
        Assert.Equal(2000, read.Message.Length);
        Assert.Equal(8000, read.StackTrace!.Length);
    }

    [Fact]
    public async Task GetPage_ReturnsNewestFirst()
    {
        await _store.WriteAsync(Entry(message: "first"));
        await _store.WriteAsync(Entry(message: "second"));
        await _store.WriteAsync(Entry(message: "third"));

        var rows = await _store.GetPageAsync(null, null, 1, 50);

        Assert.Equal(new[] { "third", "second", "first" }, rows.Select(r => r.Message), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetPage_Paginates()
    {
        for (int i = 0; i < 5; i++) await _store.WriteAsync(Entry(message: $"m{i}"));

        var page1 = await _store.GetPageAsync(null, null, 1, 2);
        var page2 = await _store.GetPageAsync(null, null, 2, 2);

        Assert.Equal(new[] { "m4", "m3" }, page1.Select(r => r.Message), StringComparer.Ordinal);
        Assert.Equal(new[] { "m2", "m1" }, page2.Select(r => r.Message), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetPage_And_Count_FilterByLevelAndSource()
    {
        await _store.WriteAsync(Entry(level: "Error", source: "api"));
        await _store.WriteAsync(Entry(level: "Critical", source: "api"));
        await _store.WriteAsync(Entry(level: "Error", source: "background"));

        Assert.Equal(2, await _store.CountAsync("Error", null));
        Assert.Equal(2, await _store.CountAsync(null, "api"));
        Assert.Equal(1, await _store.CountAsync("Error", "api"));
        Assert.Equal(3, await _store.CountAsync("all", "all"));

        var errorApi = await _store.GetPageAsync("Error", "api", 1, 50);
        Assert.All(errorApi, r => Assert.True(r.Level == "Error" && r.Source == "api"));
        Assert.Single(errorApi);
    }

    [Fact]
    public async Task Prune_DropsRowsOlderThanMaxAge()
    {
        var old = DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        await _store.WriteAsync(Entry(ts: old, message: "old"));
        await _store.WriteAsync(Entry(message: "fresh"));

        await _store.PruneAsync(TimeSpan.FromDays(30), 5000);

        var read = Assert.Single(await _store.GetPageAsync(null, null, 1, 50));
        Assert.Equal("fresh", read.Message);
    }

    [Fact]
    public async Task Prune_TrimsToRowCap_KeepingNewest()
    {
        for (int i = 0; i < 10; i++) await _store.WriteAsync(Entry(message: $"m{i}"));

        await _store.PruneAsync(TimeSpan.FromDays(3650), 3);

        var rows = await _store.GetPageAsync(null, null, 1, 50);
        Assert.Equal(new[] { "m9", "m8", "m7" }, rows.Select(r => r.Message), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ClearAll_EmptiesTheStore()
    {
        await _store.WriteAsync(Entry());
        await _store.WriteAsync(Entry());

        await _store.ClearAllAsync();

        Assert.Empty(await _store.GetPageAsync(null, null, 1, 50));
        Assert.Equal(0, await _store.CountAsync(null, null));
    }
}
