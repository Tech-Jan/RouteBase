using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// D3a: nightly WAL-safe VACUUM INTO snapshot + prune. Real file-backed WAL SQLite (VACUUM INTO
/// requires a real DB file, like SqliteHousekeepingTests). Snapshots land in a <c>backups/</c>
/// subdir beside the DB and retention keeps the most-recent N.
/// </summary>
public class DatabaseBackupServiceTests
{
    private static async Task<RouteBaseDbContext> NewWalDbAsync(string path)
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        var context = new RouteBaseDbContext(options);
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
        return context;
    }

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        var backups = Path.Combine(Path.GetDirectoryName(path)!, DatabaseBackupService.BackupsDirName);
        if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
    }

    [Fact]
    public async Task BackupAsync_WritesNonEmptySnapshotUnderBackupsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dbbak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "routebase.db");
        try
        {
            await using var context = await NewWalDbAsync(path);
            var service = new DatabaseBackupService(context, NullLogger<DatabaseBackupService>.Instance, TimeProvider.System);

            var result = await service.BackupAsync(CancellationToken.None);

            Assert.True(File.Exists(result.SnapshotPath), "snapshot file should exist");
            Assert.True(result.SnapshotBytes > 0, "snapshot should be non-empty");
            Assert.Equal(Path.Combine(dir, DatabaseBackupService.BackupsDirName),
                Path.GetDirectoryName(result.SnapshotPath));
            Assert.Equal(1, result.Kept);
            Assert.Equal(0, result.Pruned);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_CopiesDataProtectionKeyRingBesideSnapshot()
    {
        // PI-05: a DB snapshot without the key ring restores to undecryptable credentials.
        var dir = Path.Combine(Path.GetTempPath(), $"dbbak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "routebase.db");
        var keysDir = Path.Combine(dir, DatabaseBackupService.DataProtectionKeysDirName);
        Directory.CreateDirectory(keysDir);
        await File.WriteAllTextAsync(Path.Combine(keysDir, "key-1.xml"), "<key id=\"1\" />");
        await File.WriteAllTextAsync(Path.Combine(keysDir, "key-2.xml"), "<key id=\"2\" />");
        try
        {
            await using var context = await NewWalDbAsync(path);
            var service = new DatabaseBackupService(context, NullLogger<DatabaseBackupService>.Instance, TimeProvider.System);

            var result = await service.BackupAsync(CancellationToken.None);

            Assert.Equal(2, result.KeyRingFilesCopied);
            var backedUpKeys = Path.Combine(
                dir, DatabaseBackupService.BackupsDirName, DatabaseBackupService.DataProtectionKeysDirName);
            Assert.Equal("<key id=\"1\" />", await File.ReadAllTextAsync(Path.Combine(backedUpKeys, "key-1.xml")));
            Assert.Equal("<key id=\"2\" />", await File.ReadAllTextAsync(Path.Combine(backedUpKeys, "key-2.xml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_NoKeyRingDir_ReportsZeroAndSucceeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dbbak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "routebase.db");
        try
        {
            await using var context = await NewWalDbAsync(path);
            var service = new DatabaseBackupService(context, NullLogger<DatabaseBackupService>.Instance, TimeProvider.System);

            var result = await service.BackupAsync(CancellationToken.None);

            Assert.Equal(0, result.KeyRingFilesCopied);
            Assert.True(File.Exists(result.SnapshotPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFileName_UnderThaiBuddhistCulture_StaysGregorian()
    {
        // th-TH formats 2026 CE as Buddhist year 2569; a 2569-prefixed name would sort ordinally
        // AFTER every Gregorian name and invert retention. The name must stay Gregorian.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            Assert.Equal("routebase-20260801.db", DatabaseBackupService.SnapshotFileName(
                "routebase", new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task BackupAsync_UnderThaiBuddhistCulture_RetentionKeepsNewestSnapshots()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        var dir = Path.Combine(Path.GetTempPath(), $"dbbak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "routebase.db");
        try
        {
            await using var context = await NewWalDbAsync(path);
            var backupsDir = Path.Combine(dir, DatabaseBackupService.BackupsDirName);
            Directory.CreateDirectory(backupsDir);

            var seeded = new List<string>();
            for (var i = 1; i <= 10; i++)
            {
                var day = DateTime.UtcNow.AddDays(-i).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var f = Path.Combine(backupsDir, $"routebase-{day}.db");
                await File.WriteAllTextAsync(f, "stale");
                seeded.Add(f);
            }

            var service = new DatabaseBackupService(context, NullLogger<DatabaseBackupService>.Instance, TimeProvider.System);
            var result = await service.BackupAsync(CancellationToken.None);

            // Pre-fix this wrote routebase-2569….db, which ordinal sort ranks above every
            // Gregorian name — retention then counts it plus the 6 newest seeds and prunes the rest
            // by ordinal order, not age. Post-fix the name is Gregorian and the newest survive.
            Assert.Matches(@"routebase-20\d{6}\.db$", result.SnapshotPath);
            Assert.True(File.Exists(result.SnapshotPath), "today's snapshot must survive pruning");
            Assert.True(File.Exists(seeded[0]), "newest seeded snapshot must survive");
            Assert.False(File.Exists(seeded[9]), "oldest seeded snapshot must be pruned");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_PrunesToRetentionCount_OldestDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dbbak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "routebase.db");
        try
        {
            await using var context = await NewWalDbAsync(path);
            var backupsDir = Path.Combine(dir, DatabaseBackupService.BackupsDirName);
            Directory.CreateDirectory(backupsDir);

            // Seed 10 past-dated dummy snapshots (older than today so today's real one is newest).
            // UTC + invariant to match the service's own naming clock.
            var seeded = new List<string>();
            for (var i = 1; i <= 10; i++)
            {
                var day = DateTime.UtcNow.AddDays(-i).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var f = Path.Combine(backupsDir, $"routebase-{day}.db");
                await File.WriteAllTextAsync(f, "stale");
                seeded.Add(f);
            }

            var service = new DatabaseBackupService(context, NullLogger<DatabaseBackupService>.Instance, TimeProvider.System);
            var result = await service.BackupAsync(CancellationToken.None);

            var remaining = Directory.GetFiles(backupsDir, "routebase-*.db");
            Assert.Equal(DatabaseBackupService.RetentionCount, remaining.Length);
            Assert.Equal(DatabaseBackupService.RetentionCount, result.Kept);
            // 11 total (10 seeded + today's) → prune 4 oldest.
            Assert.Equal(11 - DatabaseBackupService.RetentionCount, result.Pruned);
            // The oldest seeded files (largest -i) must be the ones gone.
            Assert.False(File.Exists(seeded[9]), "oldest snapshot should be pruned");
            Assert.False(File.Exists(seeded[8]));
            Assert.True(File.Exists(result.SnapshotPath), "today's fresh snapshot should be kept");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
