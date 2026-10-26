using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

/// <summary>
/// The K1 boxing tripwire: a strong-id struct passed raw to a SQLite parameter binds as a BLOB and
/// matches nothing — silently. Both raw-SQL front doors (SqlBatch and the guarded SqliteAdo.Param
/// overload) must throw instead; primitives and DBNull keep flowing.
/// </summary>
public class SqlBindGuardTests
{
    [Fact]
    public void SqlBatch_rejects_a_strong_id_struct()
    {
        var batch = new SqlBatch("SELECT 1;");
        var ex = Assert.Throws<ArgumentException>(() => batch.Param("$id", new CommitId(5)));
        Assert.Contains("CommitId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlBatch_accepts_primitives_and_strings()
    {
        var batch = new SqlBatch("SELECT 1;");
        batch.Param("$i", 5);
        batch.Param("$s", "text");
        batch.Param("$d", 1.5);
        batch.Param("$t", DateTime.UnixEpoch);
        Assert.Equal(4, batch.Params.Count);
    }

    [Fact]
    public void SqliteAdo_guarded_overload_rejects_a_strong_id_struct()
    {
        using var cmd = new SqliteCommand("SELECT 1;");
        Assert.Throws<ArgumentException>(() => SqliteAdo.Param(cmd, "$id", new CommitId(5)));
    }

    [Fact]
    public void SqliteAdo_guarded_overload_accepts_unwrapped_value_and_null()
    {
        using var cmd = new SqliteCommand("SELECT 1;");
        Assert.Equal(5, SqliteAdo.Param(cmd, "$id", new CommitId(5).Value).Value);
        Assert.Equal(DBNull.Value, SqliteAdo.Param(cmd, "$n", null).Value);
    }
}
