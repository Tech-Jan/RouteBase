using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Mechanically enforces the fresh-EF vs legacy-DDL schema fork (ID-05, SL-02): DbSchemaManager must
/// patch a legacy DB into exactly the schema EF's OnModelCreating builds on a fresh DB. Replaces the two
/// hand-picked assertions in RouteBaseDbContextTests (kept for documented history) with full diffs.
///
/// Test (a) — fresh EF DB: running DbSchemaManager must be a no-op except the one non-EF helper table,
/// and every CREATE INDEX in <see cref="DbSchemaManager.IndexDdl"/> must match the fresh index definition.
/// Test (b) — a real legacy-DB schema dump, patched by DbSchemaManager, must converge on the fresh EF
/// schema modulo a small documented allow-list of accepted legacy leftovers.
/// </summary>
public class DbSchemaParityTests
{
    // DatabaseStartup.RunAsync schema-init order (tables → columns → lifecycle → indexes). Both tests run
    // this exact sequence so the parity check mirrors real startup — keep in lockstep with DatabaseStartup.cs.
    private static async Task RunInitInProgramOrderAsync(DbSchemaManager m)
    {
        await m.EnsureUsersTableAsync();
        await m.EnsureProfileSettingsTableAsync();
        await m.EnsureFriendshipsTableAsync();
        await m.EnsureRecordingSessionTableAsync();
        await m.EnsureHeatmapTablesAsync();
        await m.EnsureSegmentEffortExclusionTableAsync();
        await m.EnsureActivityBboxTableAsync();
        await m.EnsureDemTilesTableAsync();
        await m.EnsureRecordingStagingTableAsync();
        await m.EnsurePhotosTableAsync();
        await m.EnsureErrorLogTableAsync();
        await m.EnsureRaceTablesAsync();
        await m.EnsureShareLinkTableAsync();
        await m.EnsureTrackEditTablesAsync();
        // After the edit tables: Activity_Version.commit_id FKs Track_Edit_Commit (DatabaseStartup order).
        await m.EnsureTrackContentTablesAsync();
        await m.EnsureNewColumnsAsync();
        await m.NormalizeDateTimeTextFormatsAsync();
        await m.EnsureActivityLifecycleAsync();
        await m.EnsureIndexesAsync();
    }

    [Fact]
    public async Task FreshEfDatabase_SchemaManagerIsNoOp_AndIndexDefinitionsMatch()
    {
        await using var ctx = NewContext();
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();

        var before = await SnapshotObjectsAsync(ctx);
        await RunInitInProgramOrderAsync(new DbSchemaManager(ctx));
        var after = await SnapshotObjectsAsync(ctx);

        // The only objects DbSchemaManager may add to a fresh EF DB are the non-EF helper table and its
        // index. Anything else means DbSchemaManager knows DDL the EF model lacks (silent fork drift).
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        // Dem_Tiles is a non-EF helper table (raw DDL, no EF entity), so it's absent on the fresh EF
        // side and added by DbSchemaManager — the test's documented allow-list extension. WITHOUT ROWID
        // means its (z,x,y) PK creates no separate sqlite_master index object, so it adds one entry only.
        // The content axis (Track_Object / Activity_Version / Version_Parent) is likewise non-EF: rowid
        // tables (their INTEGER/TEXT PKs alias rowid or are the btree) plus Activity_Version's three named
        // indexes; Version_Parent is WITHOUT ROWID (its composite PK is the table btree, no index object).
        // Recording_Staging is a non-EF WITHOUT ROWID
        // helper table (its composite PK is the table btree, no separate index object) — one added entry.
        // Error_Log is a non-EF rowid helper table (INTEGER PK AUTOINCREMENT aliases rowid; its lazily-
        // created sqlite_sequence is filtered out as a sqlite_* internal) — one added entry, no index.
        // EditCore (A1 §2.8): neither Track_Edit_Commit nor Activity_Edit_Session is in this list — both are EF
        // entities (EF tables carry real FKs to them: Activity.edited_commit_id/consumed_commit_id and
        // Activity.in_edit_session_id, the checkout pointer), so the fresh side already has them and their
        // indexes, and DbSchemaManager only mirrors their DDL for legacy DBs.
        // (Track_Blob_History is gone: a commit's versions ARE its history.)
        Assert.Equal(
            new[]
            {
                "Activity_Bbox", "Activity_Version", "Dem_Tiles", "Error_Log",
                "IX_ActivityBbox_TrackId",
                "IX_ActivityVersion_Commit", "IX_ActivityVersion_Head", "IX_ActivityVersion_Object",
                "IX_ActivityVersion_Track", "IX_VersionParent_Parent",
                "Recording_Staging", "Track_Object", "Version_Parent",
            },
            added);

        // No pre-existing object's DDL was rewritten (all patches are IF NOT EXISTS / guarded).
        foreach (var name in before.Keys)
            Assert.Equal(before[name], after[name]);

        // Every CREATE INDEX in the legacy mirror must exist on the fresh side with identical column
        // spec + sort direction — this is the assertion that catches ASC/DESC drift (SL-04).
        foreach (var stmt in DbSchemaManager.IndexDdl)
        {
            var parsed = ParseIndex(stmt);
            if (parsed is null) continue; // DROP INDEX / DELETE statements
            var (name, cols) = parsed.Value;
            Assert.True(after.TryGetValue(name, out var sql) && sql is not null,
                $"Index {name} from DbSchemaManager.IndexDdl is missing on the fresh EF schema.");
            Assert.Equal(cols, ParseIndex(sql!)!.Value.Cols);
        }
    }

    [Fact]
    public async Task LegacyBaseline_AfterAllPatches_MatchesFreshEfSchema()
    {
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        // Legacy side: load the real live-DB schema dump, then apply the full startup patch sequence.
        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        // Fresh side: EF model + the same patch sequence (adds only the Activity_Bbox helper table).
        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        var diffs = new List<string>();
        await DiffTablesAndColumnsAsync(legacy, fresh, diffs);
        await DiffIndexesAsync(legacy, fresh, diffs);
        await DiffForeignKeysAsync(legacy, fresh, diffs);

        // Allow-list — accepted legacy leftovers a patcher can't fix without a table rebuild.
        // Each entry justified inline; extend only with a documented reason.
        var allowList = new[]
        {
            // Legacy shape-segment discriminator, superseded by Segment.match_rules_json. SQLite ignores
            // the unused column; no Ensure adds it to fresh DBs (DbSchemaManager EnsureNewColumnsAsync).
            "COLUMN-ONLY-LEGACY Segment.shape_match_mode",
            // Legacy index was created ASC before the SL-04 IsDescending fix; CREATE INDEX IF NOT EXISTS
            // won't re-direct an existing index and a rebuild isn't worth it. Fresh/canonical shape is DESC.
            "INDEX-COLS IX_Activity_UserId_StartDate",
            // C5 NOT NULL allow-list — ALTER-added columns can't be tightened to NOT NULL without a table
            // rebuild, so legacy stays nullable where fresh EF declares NOT NULL. One entry per column, each
            // with the reason the looseness is benign at runtime.
            //
            // segment_kind: AddColumnIfMissing adds it nullable, and SQLite cannot tighten it in place.
            // Benign because the column carries DEFAULT 'polyline' — every writer supplies a value, and
            // one that omitted it would still land 'polyline' rather than NULL.
            "COLUMN-NOTNULL Segment.segment_kind",
            // session_id (both session stores): the legacy DDL says TEXT PRIMARY KEY without NOT NULL,
            // and on a rowid table SQLite genuinely admits a NULL TEXT PK; fresh EF spells NOT NULL.
            // Can't tighten without a table rebuild. Benign: the PK is caller-assigned in both writers
            // (GUID — RecordingService.StartAsync / EditSessionStore.CreateAsync), so no insert path
            // can produce a NULL key.
            "COLUMN-NOTNULL RecordingSession.session_id",
            "COLUMN-NOTNULL Activity_Edit_Session.session_id",
            // J2 (2026-08-01): elevation went int?→double? in the EF model. The legacy columns stay
            // declared INTEGER — SQLite's INTEGER affinity stores a fractional write as REAL and reads
            // back losslessly (proven on a live-DB snapshot copy before the type change), so a table
            // rebuild would churn the largest table for a purely cosmetic type name.
            "COLUMN-TYPE Segment.segment_ascent",
            "COLUMN-TYPE Segment_GPS.altitude",
            // The baseline dump models a real live DB, i.e. one where every one-shot migration has already
            // run: no GPS/Segment_GPS_Cells/GPS.datetime_loc (BlobStore), no Track_Blob (content axis), no
            // Pictures/Heatmap_Tiles/Segment_Shapes (retired stores). The patchers that dropped them are
            // gone, so a baseline carrying them would now diverge for real — refresh the dump, not this list.
        };

        var unexplained = diffs.Where(d => !allowList.Any(a => d.StartsWith(a, StringComparison.Ordinal)))
                               .OrderBy(d => d, StringComparer.Ordinal).ToList();

        Assert.True(unexplained.Count == 0,
            "Legacy schema (patched) diverges from fresh EF schema beyond the allow-list:\n  " +
            string.Join("\n  ", unexplained));
    }

    [Fact]
    public async Task Race_EditPolicyColumn_DefaultsToOwner_OnBothFreshAndPatchedLegacy()
    {
        // AC-03: edit_policy's default is an AUTHORIZATION default — a wrong or missing one silently opens a
        // legacy DB's races up, or mis-defaults a fresh one. Pin the on-disk default in BOTH schema paths.
        //
        // Note: a GENERAL column-default parity check is deliberately NOT added — SQLite requires a default
        // on ADD COLUMN for a NOT NULL column but not on CREATE TABLE, so a legacy AddColumnIfMissing column
        // legitimately carries a default a fresh CREATE lacks (the Photos NOT NULL columns are exactly this,
        // and EF supplies the value on every insert regardless). So the one default that MATTERS — this one —
        // is pinned directly instead.
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        var freshDefault = (await ColumnsAsync(fresh, "Race"))["edit_policy"].Default;   // EF HasDefaultValue
        var legacyDefault = (await ColumnsAsync(legacy, "Race"))["edit_policy"].Default;  // AddColumnIfMissing

        Assert.Equal("'owner'", freshDefault);
        Assert.Equal(freshDefault, legacyDefault);   // the two schema paths agree
    }

    [Fact]
    public async Task Activity_LifecycleAndVisibilityDefaults_PinnedOnBothFreshAndPatchedLegacy()
    {
        // DM-01 (data-model audit 2026-07-20): ActivityImportSql.InsertActivitySql omits BOTH columns, so
        // every import (Strava, file) takes the STORE default. lifecycle is also the visibility gate —
        // VisibilityService's single clause is Lifecycle == 'released' — so a lost or wrong default makes
        // imports invisible (or crash); a lost visibility default silently changes their audience. Same
        // AUTHORIZATION-default class as Race.edit_policy above; pin both schema paths the same way.
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        foreach (var ctx in new[] { fresh, legacy })
        {
            var cols = (await QueryAsync(ctx,
                    "SELECT name, dflt_value, \"notnull\" FROM pragma_table_info('Activity') " +
                    "WHERE name IN ('lifecycle','visibility')", 3))
                .ToDictionary(r => r[0]!, r => (Default: r[1], NotNull: r[2]), StringComparer.Ordinal);

            Assert.Equal("'released'", cols["lifecycle"].Default);
            Assert.Equal("1", cols["lifecycle"].NotNull);   // NULL would slip past the released-only gate
            Assert.Equal("'friends'", cols["visibility"].Default);
            Assert.Equal("1", cols["visibility"].NotNull);
        }
    }

    [Fact]
    public async Task ActivityVersion_IsHeadDefault_PinnedOnBothFreshAndPatchedLegacy()
    {
        // Data-model audit 2026-07-22: TrackContentStore.InsertVersionAsync deliberately OMITS is_head,
        // so every version row is born non-head purely by this store default — the partial unique head
        // index and the paired head-move UPDATEs both assume it. Single-author DDL (DbSchemaManager on
        // both vintages), but pin both paths so a one-sided rewrite can't silently change birth state.
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        foreach (var ctx in new[] { fresh, legacy })
        {
            var isHead = (await ColumnsAsync(ctx, "Activity_Version"))["is_head"];
            Assert.Equal("0", isHead.Default);
            Assert.True(isHead.NotNull);
        }
    }

    [Fact]
    public async Task EfSentinelConsumedDefaults_PointCountAndSeed_PinnedOnBothFreshAndPatchedLegacy()
    {
        // Data-model audit 2026-07-22: EF omits a value-type property from INSERT when its value equals
        // the CLR sentinel (0) and a store default is configured — so RecordingSession.point_count and
        // Race_Participant.seed are the two defaults the EF write path genuinely CONSUMES on every
        // zero-valued insert (RecordingService.StartAsync always, seed-0 participants). General default
        // parity is deliberately not diffed (see the Race_EditPolicy test's note), so losing the legacy
        // DDL default would crash session starts on legacy DBs only. Pin both paths.
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        foreach (var ctx in new[] { fresh, legacy })
        {
            var pointCount = (await ColumnsAsync(ctx, "RecordingSession"))["point_count"];
            Assert.Equal("0", pointCount.Default);
            Assert.True(pointCount.NotNull);

            var seed = (await ColumnsAsync(ctx, "Race_Participant"))["seed"];
            Assert.Equal("0", seed.Default);
            Assert.True(seed.NotNull);
        }
    }

    [Fact]
    public async Task LoadBearingAutoincrement_ShareLinkAndActivityVersion_PinnedOnBothFreshAndPatchedLegacy()
    {
        // Data-model audit 2026-07-22: AUTOINCREMENT is documented load-bearing on these two tables — a
        // bare INTEGER PK reuses deleted rowids, so Share_Link's expiry sweep could resurrect a revoked
        // token id and a purged Activity_Version id could be handed to a new version, silently re-linking
        // Version_Parent's deliberately-dangling parent pointers. The schema diffs above can't see it
        // (rowid-PK tables store no per-column DDL and table sql isn't compared), so pin it directly.
        var baselineSql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-baseline.sql"));

        await using var legacy = NewContext();
        await legacy.Database.OpenConnectionAsync();
        await ExecuteScriptAsync(legacy, baselineSql);
        await RunInitInProgramOrderAsync(new DbSchemaManager(legacy));

        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await RunInitInProgramOrderAsync(new DbSchemaManager(fresh));

        foreach (var ctx in new[] { fresh, legacy })
        foreach (var table in new[] { "Share_Link", "Activity_Version" })
        {
            var sql = (await QueryAsync(ctx,
                $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'", 1))
                .Single()[0];
            Assert.True(sql!.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase),
                $"{table} lost its load-bearing AUTOINCREMENT.");
        }
    }

    [Fact]
    public async Task RecordingStaging_TsUtc_IsNotNull()
    {
        // API-10: a single NULL ts_utc row makes LivePointDto.From throw → 500s both live surfaces. The
        // schema makes the bad state unrepresentable, so pin ts_utc = INTEGER NOT NULL — and pin the
        // WITHOUT ROWID composite-PK shape with it, since the table has no EF entity to hold that shape.
        // The full-DB LegacyBaseline test converges the whole table on the fresh schema; this one names
        // the specific column contract.
        await using var fresh = NewContext();
        await fresh.Database.OpenConnectionAsync();
        await fresh.Database.EnsureCreatedAsync();
        await new DbSchemaManager(fresh).EnsureRecordingStagingTableAsync();

        var tsUtc = (await ColumnsAsync(fresh, "Recording_Staging"))["ts_utc"];
        Assert.Equal("INTEGER", tsUtc.Type);
        Assert.True(tsUtc.NotNull, "Recording_Staging.ts_utc must be NOT NULL (API-10).");

        var sql = (await QueryAsync(fresh,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='Recording_Staging'", 1)).Single()[0];
        Assert.Contains("WITHOUT ROWID", sql!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static RouteBaseDbContext NewContext()
    {
        var conn = $"Data Source=file:parity_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        return new RouteBaseDbContext(options);
    }

    // name -> CREATE sql (null for objects SQLite stores without DDL, e.g. rowid PKs); excludes sqlite_*.
    private static async Task<Dictionary<string, string?>> SnapshotObjectsAsync(RouteBaseDbContext ctx)
    {
        var rows = await QueryAsync(ctx,
            "SELECT name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'", 2);
        return rows.ToDictionary(r => r[0]!, r => r[1], StringComparer.Ordinal);
    }

    private static async Task ExecuteScriptAsync(RouteBaseDbContext ctx, string script)
    {
        foreach (var raw in script.Split(';'))
        {
            var stmt = raw.Trim();
            if (stmt.Length == 0) continue;
            // The .schema dump lists SQLite-internal tables that cannot be created by hand.
            if (stmt.Contains("sqlite_sequence", StringComparison.Ordinal) || stmt.Contains("sqlite_stat1", StringComparison.Ordinal)) continue;
            await ctx.Database.ExecuteSqlRawAsync(stmt);
        }
    }

    private static async Task DiffTablesAndColumnsAsync(
        RouteBaseDbContext legacy, RouteBaseDbContext fresh, List<string> diffs)
    {
        var legacyTables = await TableNamesAsync(legacy);
        var freshTables = await TableNamesAsync(fresh);

        foreach (var t in legacyTables.Except(freshTables, StringComparer.Ordinal)) diffs.Add($"TABLE-ONLY-LEGACY {t}");
        foreach (var t in freshTables.Except(legacyTables, StringComparer.Ordinal)) diffs.Add($"TABLE-ONLY-FRESH {t}");

        foreach (var table in legacyTables.Intersect(freshTables, StringComparer.Ordinal))
        {
            var lc = await ColumnsAsync(legacy, table);
            var fc = await ColumnsAsync(fresh, table);
            foreach (var (col, meta) in lc)
            {
                if (!fc.TryGetValue(col, out var fmeta)) { diffs.Add($"COLUMN-ONLY-LEGACY {table}.{col} {meta.Type}"); continue; }
                if (!string.Equals(meta.Type, fmeta.Type, StringComparison.OrdinalIgnoreCase))
                    diffs.Add($"COLUMN-TYPE {table}.{col} legacy={meta.Type} fresh={fmeta.Type}");
                // C5: NOT NULL is contract, not cosmetics — a nullable column on one vintage admits rows
                // the other rejects. Legitimate ALTER-history drift goes on the allow-list, justified.
                if (EffectiveNotNull(meta) != EffectiveNotNull(fmeta))
                    diffs.Add($"COLUMN-NOTNULL {table}.{col} " +
                        $"legacy={(EffectiveNotNull(meta) ? "notnull" : "nullable")} fresh={(EffectiveNotNull(fmeta) ? "notnull" : "nullable")}");
            }
            foreach (var (col, meta) in fc)
                if (!lc.ContainsKey(col)) diffs.Add($"COLUMN-ONLY-FRESH {table}.{col} {meta.Type}");
        }
    }

    // SQLite semantics, not a waiver: an INTEGER PRIMARY KEY can never hold NULL (rowid alias on
    // rowid tables; WITHOUT ROWID keys are NOT NULL-enforced) whether or not the DDL spells NOT NULL
    // — legacy CREATEs omit the keyword, EF writes it, and only that spelling differs. Non-INTEGER
    // PKs are NOT normalized: on a rowid table SQLite genuinely admits a NULL TEXT PK (the historic
    // PRIMARY-KEY-≠-NOT-NULL wart), so that drift is real and needs a per-column allow-list entry.
    private static bool EffectiveNotNull((string Type, string Default, bool NotNull, bool Pk) c) =>
        c.NotNull || (c.Pk && string.Equals(c.Type, "INTEGER", StringComparison.OrdinalIgnoreCase));

    private static async Task DiffIndexesAsync(
        RouteBaseDbContext legacy, RouteBaseDbContext fresh, List<string> diffs)
    {
        var li = await IndexesAsync(legacy);
        var fi = await IndexesAsync(fresh);
        foreach (var name in li.Keys.Except(fi.Keys, StringComparer.Ordinal)) diffs.Add($"INDEX-ONLY-LEGACY {name}");
        foreach (var name in fi.Keys.Except(li.Keys, StringComparer.Ordinal)) diffs.Add($"INDEX-ONLY-FRESH {name}");
        foreach (var name in li.Keys.Intersect(fi.Keys, StringComparer.Ordinal))
            if (li[name] != fi[name]) diffs.Add($"INDEX-COLS {name} legacy=({li[name]}) fresh=({fi[name]})");
    }

    private static async Task DiffForeignKeysAsync(
        RouteBaseDbContext legacy, RouteBaseDbContext fresh, List<string> diffs)
    {
        var tables = (await TableNamesAsync(legacy)).Intersect(await TableNamesAsync(fresh), StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var lf = await ForeignKeysAsync(legacy, table);
            var ff = await ForeignKeysAsync(fresh, table);
            foreach (var fk in lf.Except(ff, StringComparer.Ordinal)) diffs.Add($"FK-ONLY-LEGACY {table} {fk}");
            foreach (var fk in ff.Except(lf, StringComparer.Ordinal)) diffs.Add($"FK-ONLY-FRESH {table} {fk}");
        }
    }

    private static async Task<HashSet<string>> TableNamesAsync(RouteBaseDbContext ctx)
    {
        var rows = await QueryAsync(ctx,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", 1);
        return rows.Select(r => r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, (string Type, string Default, bool NotNull, bool Pk)>> ColumnsAsync(
        RouteBaseDbContext ctx, string table)
    {
        var rows = await QueryAsync(ctx,
            $"SELECT name, type, dflt_value, \"notnull\", pk FROM pragma_table_info('{table}')", 5);
        return rows.ToDictionary(r => r[0]!, r => (r[1] ?? "", r[2] ?? "", r[3] == "1", r[4] != "0"),
            StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string>> IndexesAsync(RouteBaseDbContext ctx)
    {
        var rows = await QueryAsync(ctx,
            "SELECT name, sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%'", 2);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var parsed = ParseIndex(r[1]!);
            if (parsed is not null) result[parsed.Value.Name] = parsed.Value.Cols;
        }
        return result;
    }

    // (from-column -> to-table.to-column onDelete=...) as a normalized set, order-independent.
    private static async Task<HashSet<string>> ForeignKeysAsync(RouteBaseDbContext ctx, string table)
    {
        var rows = await QueryAsync(ctx,
            $"SELECT \"from\", \"table\", \"to\", on_delete FROM pragma_foreign_key_list('{table}')", 4);
        return rows.Select(r => $"{r[0]}->{r[1]}.{r[2]} onDelete={r[3]}").ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<List<string?[]>> QueryAsync(RouteBaseDbContext ctx, string sql, int cols)
    {
        var conn = ctx.Database.GetDbConnection();
        var rows = new List<string?[]>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new string?[cols];
            for (int i = 0; i < cols; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
            rows.Add(row);
        }
        return rows;
    }

    // Parses a CREATE [UNIQUE] INDEX statement into (name, normalized column spec). Returns null for
    // DROP/DELETE/other statements. Column spec is unquoted, upper-cased (CA1308: invariant normalization
    // for comparison only — both operands go through here, so the direction is symmetric), whitespace-collapsed, order kept.
    private static (string Name, string Cols)? ParseIndex(string ddl)
    {
        var m = Regex.Match(ddl.Trim().TrimEnd(';'),
            @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[""\[\]`\w]+)\s+ON\s+[""\[\]`\w]+\s*\((?<cols>.*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        if (!m.Success) return null;
        var name = Unquote(m.Groups["name"].Value);
        var cols = string.Join(", ", m.Groups["cols"].Value
            .Split(',')
            .Select(p => Regex.Replace(Unquote(p), @"\s+", " ", RegexOptions.None, RegexDefaults.MatchTimeout).Trim().ToUpperInvariant()));
        return (name, cols);
    }

    private static string Unquote(string s) => s.Replace("\"", "", StringComparison.Ordinal).Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
}
