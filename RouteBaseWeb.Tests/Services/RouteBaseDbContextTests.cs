using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Guards the fresh-vs-legacy schema fork (ME-02/ME-03): the EF model (used by EnsureCreated on
/// fresh DBs) must carry the same indexes/FKs that DbSchemaManager patches into legacy DBs.
/// Each assertion here is the cheap parity check the two-track schema otherwise lacks — extend the
/// index list as Backend-Data settles which side is canonical.
/// </summary>
public class RouteBaseDbContextTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:schema_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public async Task FreshDatabase_HasLegacyParityIndexes()
    {
        var indexes = await _context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_index_list('Activity')")
            .ToListAsync();

        // The one legacy index EF omitted before ME-02 — used for ordering in GetActivitiesAsync.
        Assert.Contains("IX_Activity_StartDate", indexes, StringComparer.Ordinal);
    }
}
