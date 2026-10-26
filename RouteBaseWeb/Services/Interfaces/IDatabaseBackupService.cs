namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// WAL-safe DB snapshot backup (D3a). Writes a consistent <c>VACUUM INTO</c> copy of the live
/// database to <c>database/backups/</c>, prunes to the most-recent N daily snapshots, and mirrors
/// the Data Protection key ring beside the snapshots (PI-05 — the DB's encrypted credentials are
/// useless without it). Runs nightly (<see cref="DatabaseBackupHostedService"/>) and on demand via
/// the <c>Maintenance:BackupDatabase</c> startup key. A failed backup logs and is swallowed — it
/// must never crash the host or block the app from serving.
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>Takes one snapshot, prunes old snapshots to the retention count, mirrors the key ring.</summary>
    Task<DatabaseBackupResult> BackupAsync(CancellationToken ct = default);
}

/// <summary>
/// Outcome of a backup pass. <c>SnapshotPath</c> is the file just written, <c>SnapshotBytes</c> its
/// size, <c>Pruned</c> the number of older snapshots deleted, <c>Kept</c> those retained (incl. the
/// new one), <c>KeyRingFilesCopied</c> the Data Protection key files mirrored beside the snapshots.
/// </summary>
public record DatabaseBackupResult(string SnapshotPath, long SnapshotBytes, int Pruned, int Kept, int KeyRingFilesCopied);
