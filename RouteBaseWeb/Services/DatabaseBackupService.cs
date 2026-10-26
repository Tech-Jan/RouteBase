using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// WAL-safe DB backup (see <see cref="IDatabaseBackupService"/>). Uses <c>VACUUM INTO</c> — the same
/// mechanism proven in the (now-deleted, one-shot) <c>TrackBlobMigrationService</c> — to write a consistent snapshot of an
/// open WAL database in a single statement (a naive file copy would tear, since WAL frames aren't yet
/// in the main file). Snapshots live in <c>database/backups/</c> so they back up as a unit with the DB
/// data root; retention keeps the most-recent <see cref="RetentionCount"/> daily files. The Data
/// Protection key ring is mirrored beside the snapshots (PI-05) — without it a restored DB's
/// encrypted credentials are permanently undecryptable.
/// </summary>
public class DatabaseBackupService : IDatabaseBackupService
{
    // Keep one week of daily snapshots. On-box only — the off-server copy (D3b) is a user-hands runbook step.
    internal const int RetentionCount = 7;
    internal const string BackupsDirName = "backups";
    // Data Protection key ring beside the DB (Program.cs PersistKeysToFileSystem uses this same name).
    internal const string DataProtectionKeysDirName = "dataprotection-keys";

    private readonly RouteBaseDbContext _context;
    private readonly ILogger<DatabaseBackupService> _logger;

    private readonly TimeProvider _clock;

    public DatabaseBackupService(RouteBaseDbContext context, ILogger<DatabaseBackupService> logger, TimeProvider clock)
    {
        _clock = clock;
        _context = context;
        _logger = logger;
    }

    public async Task<DatabaseBackupResult> BackupAsync(CancellationToken ct = default)
    {
        var dbPath = _context.Database.GetDbConnection().DataSource;
        var dbDir = Path.GetDirectoryName(dbPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(dbPath);
        var backupsDir = Path.Combine(dbDir, BackupsDirName);
        Directory.CreateDirectory(backupsDir);

        // Daily granularity: one file per calendar day. A same-day re-run (e.g. Maintenance:BackupDatabase
        // before an upgrade) refreshes today's snapshot — VACUUM INTO refuses to overwrite, so drop first.
        // UTC + invariant: the name feeds PruneOldSnapshots' ordinal sort, so a culture-shaped year
        // (th-TH would write 2569) would invert retention and prune the newest snapshots.
        var target = Path.Combine(backupsDir, SnapshotFileName(stem, _clock.GetUtcNow().UtcDateTime));
        if (File.Exists(target))
            File.Delete(target);

        // EF1002: `target` is a server-config-derived path, not request input — but a bound parameter
        // is just as easy here and drops the hand-rolled quote-escaping in favor of the real thing.
        await _context.Database.ExecuteSqlAsync($"VACUUM INTO {target};", ct);

        var bytes = File.Exists(target) ? new FileInfo(target).Length : 0;
        if (bytes <= 0)
            throw new InvalidOperationException($"Backup VACUUM INTO produced no file at {target}.");

        var (pruned, kept) = PruneOldSnapshots(backupsDir, stem);
        var keyRingCopied = BackupKeyRing(dbDir, backupsDir);

        _logger.LogInformation(
            "DatabaseBackup: wrote {File} ({Bytes:N0} bytes); pruned {Pruned}, kept {Kept}; key-ring files {KeyRing}.",
            Path.GetFileName(target), bytes, pruned, kept, keyRingCopied);

        return new DatabaseBackupResult(target, bytes, pruned, kept, keyRingCopied);
    }

    // PI-05: the key ring must travel with the DB snapshot — a restored DB without it leaves every
    // ICredentialProtector payload (Strava/MapTiler/mobile tokens) and all auth cookies undecryptable.
    // A mirror copy is enough: the ring only grows (a new key ~every 90 days) and old key files are
    // immutable, so overwrite-copying all files each run loses nothing and needs no retention.
    private int BackupKeyRing(string dbDir, string backupsDir)
    {
        var source = Path.Combine(dbDir, DataProtectionKeysDirName);
        if (!Directory.Exists(source)) return 0;   // fresh install: no key persisted yet

        int copied = 0;
        try
        {
            var target = Path.Combine(backupsDir, DataProtectionKeysDirName);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                copied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The DB snapshot already succeeded — a key-ring copy failure must not fail the run.
            _logger.LogWarning(ex, "DatabaseBackup: key-ring copy failed after {Copied} files", copied);
        }
        return copied;
    }

    internal static string SnapshotFileName(string stem, DateTime utcNow) =>
        $"{stem}-{utcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.db";

    // Filenames embed yyyyMMdd, which sorts lexically = chronologically, so descending name order is
    // newest-first. Delete everything past the retention count. Scoped to this DB's snapshots by stem
    // so a shared backups dir (only this app writes it today) can't nuke unrelated files.
    private (int pruned, int kept) PruneOldSnapshots(string backupsDir, string stem)
    {
        var snapshots = Directory
            .EnumerateFiles(backupsDir, $"{stem}-*.db")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();

        int pruned = 0;
        foreach (var stale in snapshots.Skip(RetentionCount))
        {
            try
            {
                File.Delete(stale);
                pruned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked/undeletable snapshot must not fail the backup run — log and keep going.
                _logger.LogWarning(ex, "DatabaseBackup: could not prune {File}", Path.GetFileName(stale));
            }
        }
        return (pruned, Math.Min(snapshots.Count, RetentionCount));
    }
}
