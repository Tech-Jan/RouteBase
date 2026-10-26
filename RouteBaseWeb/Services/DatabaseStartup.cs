using System.Globalization;
using RouteBaseWeb.Configuration;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Boot-time database preparation, run once from Program.cs before the app serves traffic. Moved whole
/// out of Program.cs (A9 line-cap); the phase ORDER is the load-bearing part — see the inline comments.
/// Exercised end-to-end by every full-pipeline test boot (Tests/Hosting).
/// </summary>
public static class DatabaseStartup
{
    public static async Task RunAsync(IServiceProvider services, LocalModeOptions localMode, bool createdNewDatabase)
    {
        using var scope = services.CreateScope();

        var schemaManager = scope.ServiceProvider.GetRequiredService<IDbSchemaManager>();

        // Schema init runs in explicit phases so each new Ensure* lands in the right slot and dependencies
        // hold: tables → columns → indexes. Every statement is IF NOT EXISTS / OR IGNORE / guarded ALTER,
        // so the whole block is an idempotent no-op on an up-to-date DB.
        if (createdNewDatabase)
        {
            var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            await context.Database.EnsureCreatedAsync();
            Console.WriteLine("✅ New empty database created successfully.");
        }

        // Phase 1 — tables: create every app-era table before anything ALTERs or indexes it.
        // RecordingSession/ProfileSettings columns and indexes below depend on the table existing.
        await schemaManager.EnsureUsersTableAsync();
        await schemaManager.EnsureProfileSettingsTableAsync();
        await schemaManager.EnsureFriendshipsTableAsync();
        await schemaManager.EnsureRecordingSessionTableAsync();
        await schemaManager.EnsureHeatmapTablesAsync();
        await schemaManager.EnsureSegmentEffortExclusionTableAsync();
        await schemaManager.EnsureActivityBboxTableAsync();
        await schemaManager.EnsureDemTilesTableAsync();
        await schemaManager.EnsureRecordingStagingTableAsync();
        await schemaManager.EnsurePhotosTableAsync();
        await schemaManager.EnsureErrorLogTableAsync();
        await schemaManager.EnsureRaceTablesAsync();
        await schemaManager.EnsureShareLinkTableAsync();
        await schemaManager.EnsureTrackEditTablesAsync();
        // After the edit tables: Activity_Version.commit_id FKs Track_Edit_Commit.
        await schemaManager.EnsureTrackContentTablesAsync();

        // Phase 2 — columns: add columns introduced since the pre-web schema (assumes phase 1 ran), then keep
        // Activity's TEXT datetimes in the canonical space-separated form (a LIKE '%T%' no-op on clean rows —
        // a guard against a bad writer, not a migration: EF's date filters compare TEXT lexicographically).
        await schemaManager.EnsureNewColumnsAsync();
        await schemaManager.NormalizeDateTimeTextFormatsAsync();

        // Phase 2b — lifecycle: the append-only Activity_Lifecycle_Log; runs after the columns phase.
        await schemaManager.EnsureActivityLifecycleAsync();

        // Phase 3 — indexes: unconditional and last, so indexes on freshly-created/altered tables
        // (e.g. IX_RecordingSession_TrackId) always have their table + columns present.
        await schemaManager.EnsureIndexesAsync();

        // Phase 4 — object GC safety net: reclaim Track_Object rows nothing references. The per-purge sweep is
        // candidate-scoped (it can only see what THAT purge orphaned), so orphans from a crashed/aborted save
        // would otherwise accumulate forever. One indexed anti-join per boot; count is 0 in the steady state.
        {
            var objectStore = scope.ServiceProvider.GetRequiredService<RouteBaseWeb.Services.Tracks.ITrackObjectStore>();
            int swept = await objectStore.SweepUnreferencedAsync();
            if (swept > 0) Console.WriteLine($"🧹 Track objects: swept {swept} unreferenced object(s).");
        }
        Console.WriteLine("✅ Database schema verified.");

        // Establish WAL once at boot, pre-traffic. This used to be set inside every analysis run (an
        // analysis pragma deleted at Cutover); journal_mode is a persistent, database-scoped property, so
        // setting it once here keeps the guarantee. Mode transitions remain owned by SqliteHousekeeping.
        {
            var walContext = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            await walContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
        }

        // Localhost mode: create the single local user up-front. Every activity/segment/photo FKs to
        // Users(user_id); in server mode that row is created on OIDC login, which never happens here.
        if (localMode.Enabled)
        {
            var userContext = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            if (await userContext.Users.FindAsync(LocalModeOptions.UserId) is null)
            {
                var now = scope.ServiceProvider.GetRequiredService<TimeProvider>()
                    .GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                userContext.Users.Add(new AppUser
                {
                    UserId = LocalModeOptions.UserId,
                    DisplayName = LocalModeOptions.DisplayName,
                    IsAdmin = true,
                    CreatedAt = now,
                    LastLogin = now
                });
                await userContext.SaveChangesAsync();
                Console.WriteLine($"✅ Localhost mode: local user '{LocalModeOptions.UserId}' ready.");
            }
        }
    }
}
