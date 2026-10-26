using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

/// <summary>
/// CR5-05 guard: ChildTableRegistry must know every keyed child table the app schema actually has.
/// Builds the real app-created schema (fresh EF EnsureCreated + the full DbSchemaManager startup
/// sequence — the DbSchemaParityTests path, no live DB), introspects <c>sqlite_master</c> /
/// <c>pragma_table_info</c> for track-/segment-/user-keyed tables, and asserts set-parity with the
/// registry. A future feature adding e.g. a track-keyed table without registering it fails here —
/// the historical miss class (Activity_Bbox leak, ME-01 RecordingSession gap, F-3
/// SegmentEffortExclusion follow-up) can no longer ship silently.
/// </summary>
public class ChildTableRegistryParityTests : IAsyncLifetime
{
    private RouteBaseDbContext _ctx = null!;
    private Dictionary<string, List<string>> _columnsByTable = null!; // table -> declared column names

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:ctreg_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _ctx = new RouteBaseDbContext(options);
        await _ctx.Database.OpenConnectionAsync();
        await _ctx.Database.EnsureCreatedAsync();

        // Program.cs schema-init order (tables → drops → columns → indexes), same as DbSchemaParityTests,
        // so the introspected schema is exactly what production startup produces.
        var m = new DbSchemaManager(_ctx);
        await m.EnsureUsersTableAsync();
        await m.EnsureProfileSettingsTableAsync();
        await m.EnsureFriendshipsTableAsync();
        await m.EnsureRecordingSessionTableAsync();
        await m.EnsureHeatmapTablesAsync();
        await m.EnsureSegmentEffortExclusionTableAsync();
        await m.EnsureActivityBboxTableAsync();
        await m.EnsureDemTilesTableAsync();
        await m.EnsureTrackContentTablesAsync();
        await m.EnsureRecordingStagingTableAsync();
        await m.EnsurePhotosTableAsync();
        await m.EnsureErrorLogTableAsync(); // B8: raw-only table — without it the user-key sweep was blind to Error_Log
        await m.EnsureTrackEditTablesAsync();
        await m.EnsureNewColumnsAsync();
        await m.NormalizeDateTimeTextFormatsAsync();
        await m.EnsureIndexesAsync();

        _columnsByTable = await IntrospectColumnsAsync(_ctx);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public void TrackKeyedTables_ExactlyMatchRegistry()
    {
        // Every table with a track_id/track_ID column (case-insensitive), except the Activity parent
        // itself, must be a registered track child — and vice versa (registry rot guard).
        var schema = TablesWithColumn("track_id", except: "Activity");
        var registered = ChildTableRegistry.TrackChildTables.Select(t => t.Table)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(schema.SequenceEqual(registered),
            "Track-keyed tables in the app schema and ChildTableRegistry.TrackChildTables diverge — " +
            "register the new table (with its key-column spelling) so every delete seam cleans it up.\n" +
            $"  schema:   [{string.Join(", ", schema)}]\n  registry: [{string.Join(", ", registered)}]");
    }

    [Fact]
    public void SegmentKeyedTables_ExactlyMatchRegistryPlusCascadeCovered()
    {
        var schema = TablesWithColumn("segment_id", except: "Segment");
        var registered = ChildTableRegistry.SegmentChildTables.Select(t => t.Table)
            .Concat(ChildTableRegistry.SegmentCascadeCoveredTables)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(schema.SequenceEqual(registered),
            "Segment-keyed tables in the app schema and ChildTableRegistry (SegmentChildTables + " +
            "SegmentCascadeCoveredTables) diverge — register the new table or document its cascade.\n" +
            $"  schema:   [{string.Join(", ", schema)}]\n  registry: [{string.Join(", ", registered)}]");
    }

    [Fact]
    public void RaceKeyedTables_ExactlyMatchTheCascadeCoveredList()
    {
        // The race axis has no hand-written delete seam — every race-keyed table cascades off Race — so the
        // registry's job here is purely the rot guard: a new race-keyed table (or a lost cascade) has to be
        // declared. Share_Link.race_id is the entry that carries a security consequence: a race share link
        // must die with its race, exactly as an activity's and a segment's links do.
        var schema = TablesWithColumn("race_id", except: "Race");
        var registered = ChildTableRegistry.RaceCascadeCoveredTables
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(schema.SequenceEqual(registered),
            "Race-keyed tables in the app schema and ChildTableRegistry.RaceCascadeCoveredTables diverge — " +
            "register the new table (and confirm it carries ON DELETE CASCADE in BOTH schema authors).\n" +
            $"  schema:   [{string.Join(", ", schema)}]\n  registry: [{string.Join(", ", registered)}]");
    }

    [Fact]
    public void UserKeyedTables_AllCoveredByRegistry()
    {
        // A user-keyed table must have a registered cleanup path: wiped per-user at reset
        // (UserOwnedTables) or cleaned via its track key (TrackChildTables — Photos/RecordingSession
        // carry a denormalized user_id). Users itself is the account root, never wiped by a data reset.
        var schema = TablesWithColumn("user_id", except: "Users")
            .Concat(TablesWithColumn("UserId", except: "Users"))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var covered = ChildTableRegistry.UserOwnedTables.Select(t => t.Table)
            .Concat(ChildTableRegistry.TrackChildTables.Select(t => t.Table))
            .ToHashSet(StringComparer.Ordinal);

        var unregistered = schema.Where(t => !covered.Contains(t)).ToList();
        Assert.True(unregistered.Count == 0,
            "User-keyed tables without a registered cleanup path in ChildTableRegistry: " +
            $"[{string.Join(", ", unregistered)}]");

        // Reverse direction: every registered user-owned table must exist with its user key column.
        foreach (var t in ChildTableRegistry.UserOwnedTables)
            Assert.Contains(t.Table, (IEnumerable<string>)schema, StringComparer.Ordinal);
    }

    [Fact]
    public void RegisteredKeyColumns_ExistWithDeclaredSpelling()
    {
        // Each registry entry's key column must exist on its table with the exact declared spelling
        // (Track_Blob/Recording_Staging/Activity_Bbox use track_ID; the EF-era tables use track_id).
        var all = ChildTableRegistry.TrackChildTables
            .Concat(ChildTableRegistry.SegmentChildTables)
            .Concat(ChildTableRegistry.UserOwnedTables);
        foreach (var entry in all)
        {
            Assert.True(_columnsByTable.TryGetValue(entry.Table, out var cols),
                $"Registered table {entry.Table} does not exist in the app schema.");
            Assert.Contains(entry.KeyColumn, cols!, StringComparer.Ordinal);
        }
    }

    // ---- helpers ----

    /// <summary>Tables (except <paramref name="except"/>) with a column named <paramref name="column"/>,
    /// case-insensitive, sorted ordinally.</summary>
    private string[] TablesWithColumn(string column, string except) =>
        _columnsByTable
            .Where(kv => kv.Key != except &&
                         kv.Value.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static async Task<Dictionary<string, List<string>>> IntrospectColumnsAsync(RouteBaseDbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT m.name, c.name FROM sqlite_master m, pragma_table_info(m.name) c " +
            "WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'";
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var cols))
                result[table] = cols = new List<string>();
            cols.Add(reader.GetString(1));
        }
        return result;
    }
}
