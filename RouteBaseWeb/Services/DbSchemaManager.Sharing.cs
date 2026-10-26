using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Share slice schema (anonymous capability URLs).
///
/// Fresh DBs get this from EF's OnModelCreating; this patches the legacy DB into the same shape.
/// DbSchemaParityTests enforces the two converge — keep the DDL below in lockstep with the
/// modelBuilder.Entity&lt;ShareLink&gt; block in RouteBaseDbContext.
/// </summary>
public partial class DbSchemaManager
{
    /// <inheritdoc />
    public async Task EnsureShareLinkTableAsync()
    {
        // AUTOINCREMENT is load-bearing, not decorative: the expiry sweep deletes rows, and a bare rowid is
        // reused after a delete — see ShareLink.ShareLinkId.
        //
        // Every index below is declared even though EF would create the two FK ones by convention: the fresh
        // side has them either way, so omitting them here forks the schemas and DbSchemaParityTests fails on
        // exactly that gap (the Races.cs:74-78 lesson).
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS Share_Link (
                share_link_ID INTEGER PRIMARY KEY AUTOINCREMENT,
                token_hash    TEXT NOT NULL,
                kind          TEXT NOT NULL,
                track_id      INTEGER REFERENCES Activity(track_ID) ON DELETE CASCADE,
                segment_id    INTEGER REFERENCES Segment(segment_ID) ON DELETE CASCADE,
                race_id       INTEGER REFERENCES Race(race_ID) ON DELETE CASCADE,
                user_id       TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                expires_at    TEXT NOT NULL,
                revoked_at    TEXT,
                open_count    INTEGER NOT NULL DEFAULT 0,
                last_opened_at TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ShareLink_TokenHash ON Share_Link (token_hash);
            CREATE INDEX IF NOT EXISTS IX_ShareLink_UserId    ON Share_Link (user_id);
            CREATE INDEX IF NOT EXISTS IX_ShareLink_TrackId   ON Share_Link (track_id);
            CREATE INDEX IF NOT EXISTS IX_ShareLink_SegmentId ON Share_Link (segment_id);
            CREATE INDEX IF NOT EXISTS IX_ShareLink_ExpiresAt ON Share_Link (expires_at);");

        // CREATE TABLE IF NOT EXISTS is a no-op on the live DB, which already has Share_Link without the
        // race column — so the race kind needs this second pass or a deployed DB never grows it. Nullable
        // with an implicit NULL default, which is what SQLite requires to ADD a column carrying REFERENCES.
        await AddColumnIfMissingAsync("Share_Link", "race_id", "INTEGER REFERENCES Race(race_ID) ON DELETE CASCADE");

        // Open counting, same second-pass reason as race_id: a deployed DB already has Share_Link, so
        // CREATE TABLE IF NOT EXISTS never grows it. NOT NULL DEFAULT 0 is legal in ADD COLUMN because the
        // default is a constant — existing rows read 0, which is the truth for a link minted before this.
        await AddColumnIfMissingAsync("Share_Link", "open_count", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync("Share_Link", "last_opened_at", "TEXT");

        // After the ADD COLUMN: on a live DB the column does not exist until then, and indexing a missing
        // column throws. EF indexes the FK by convention, so omitting this would fork the two schemas and
        // DbSchemaParityTests fails on exactly that gap (the Races.cs:74-78 lesson).
        await _context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ShareLink_RaceId ON Share_Link (race_id);");
    }
}
