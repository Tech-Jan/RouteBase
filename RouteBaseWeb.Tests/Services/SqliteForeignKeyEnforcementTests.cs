using Microsoft.Data.Sqlite;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization pin for FK enforcement (decision H, 2026-07-17). Production has ALWAYS
/// enforced foreign keys: the bundled e_sqlite3 native lib is compiled with
/// SQLITE_DEFAULT_FOREIGN_KEYS (verified against SQLitePCLRaw.lib.e_sqlite3 3.53.3, the
/// version RouteBaseWeb pins). Program.cs additionally sets Foreign Keys=True explicitly so a
/// future provider/bundle swap can't silently drop enforcement. These tests pin both layers
/// against the real engine — if either ever regresses, deletes stop cascading and orphans
/// stop being rejected, silently.
/// </summary>
public class SqliteForeignKeyEnforcementTests
{
    private static SqliteConnection Open(string connectionString)
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void DefaultConnectionString_AlreadyEnforces_BundleCompileFlagPin()
    {
        // The guarantee the app relied on before it was explicit: e_sqlite3's
        // SQLITE_DEFAULT_FOREIGN_KEYS compile flag. If this fails, the native bundle changed.
        using var c = Open("Data Source=:memory:");
        Assert.Equal(1, Scalar(c, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void ExplicitConnectionString_Enforces_ProgramCsShapePin()
    {
        // The shape Program.cs uses — holds even on a bundle without the compile flag.
        using var c = Open("Data Source=:memory:;Foreign Keys=True");
        Assert.Equal(1, Scalar(c, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void OrphanInsert_IsRejected()
    {
        using var c = Open("Data Source=:memory:;Foreign Keys=True");
        Exec(c, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Exec(c, "CREATE TABLE child(id INTEGER PRIMARY KEY, pid INTEGER REFERENCES parent(id))");

        var ex = Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO child(pid) VALUES (999)"));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void OnDeleteCascade_Fires()
    {
        // What DatabaseResetService's parents-last delete order relies on.
        using var c = Open("Data Source=:memory:;Foreign Keys=True");
        Exec(c, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Exec(c, "CREATE TABLE child(id INTEGER PRIMARY KEY, pid INTEGER REFERENCES parent(id) ON DELETE CASCADE)");
        Exec(c, "INSERT INTO parent(id) VALUES (1)");
        Exec(c, "INSERT INTO child(pid) VALUES (1)");

        Exec(c, "DELETE FROM parent WHERE id = 1");

        Assert.Equal(0, Scalar(c, "SELECT COUNT(*) FROM child"));
    }
}
