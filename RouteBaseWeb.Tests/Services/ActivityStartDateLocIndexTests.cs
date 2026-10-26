using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Perf decision ⑳ (2026-07-17): ActivityQueryService.GetAllActivitiesAsync (the dashboard/overview
/// grid) orders by start_datetime_loc, which had no index — SQLite fell back to a TEMP B-TREE sort.
/// Pins the query plan, not just that the index exists (DbSchemaParityTests already covers that): an
/// index can exist and still go unused if the query shape doesn't match it.
/// </summary>
public class ActivityStartDateLocIndexTests
{
    [Fact]
    public async Task GridSortOrder_UsesIndex_NotATempBTree()
    {
        var conn = $"Data Source=file:startdateloc_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        await using var ctx = new RouteBaseDbContext(options);
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();

        var plan = await ExplainAsync(ctx,
            "SELECT track_ID FROM Activity ORDER BY start_datetime_loc DESC");

        Assert.Contains("IX_Activity_StartDateLoc", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ExplainAsync(RouteBaseDbContext ctx, string sql)
    {
        var connection = ctx.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(reader.GetValue(reader.FieldCount - 1)?.ToString() ?? "");
        return string.Join("\n", lines);
    }
}
