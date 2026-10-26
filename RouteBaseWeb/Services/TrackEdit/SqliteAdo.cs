using System.Globalization;
using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Shared raw-ADO boilerplate for the TrackEdit stores (persistence-ignorant, no EF entity). Extracted from
/// the identical private helpers that were quadruplicated across EditSessionStore / TrackEditCommitStore /
/// TrackBlobHistoryStore (R-15). Timestamps use the stores' fixed "yyyy-MM-dd HH:mm:ss" UTC text format.
/// </summary>
internal static class SqliteAdo
{
    /// <summary>Add a named parameter to a command and return it (caller sets <c>.Value</c>).</summary>
    public static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>Add a named parameter with its value, guarded: a K1 strong-id struct (or any other
    /// non-primitive value type) passed raw would silently bind as a BLOB and match nothing —
    /// unwrap with <c>.Value</c> at the call site instead. Throwing here turns that silent
    /// wrong-answer into a loud test failure.</summary>
    public static SqliteParameter Param(SqliteCommand cmd, string name, object? value)
    {
        SqlBindGuard.ThrowIfUnbindable(value, name);
        var p = Param(cmd, name);
        p.Value = value ?? DBNull.Value;
        return p;
    }

    // Join the ambient EF transaction if one is active (RF-01 precedent).
    //
    // The original reason — "Microsoft.Data.Sqlite throws when a command runs on a connection with an open
    // transaction unless cmd.Transaction points at it" — NO LONGER HOLDS on the provider this repo pins
    // (verified 2026-08-02): removing every call here leaves the ambient-transaction tests green, and a
    // command with a null Transaction still reads the ambient transaction's uncommitted rows. So this is
    // defence-in-depth against a provider that may tighten again, not a live requirement — which is also
    // why its removal mutants are unkillable (13 of them, across TrackContentStore + RecordingStagingStore).
    // Kept deliberately; do not re-file those mutants as a coverage gap.
    public static void EnlistAmbientTx(RouteBaseDbContext context, SqliteCommand cmd) =>
        cmd.Transaction = (SqliteTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();

    public static string Fmt(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateTime ParseTs(string s) => DateTime.Parse(
        s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>The K1 boxing tripwire shared by <see cref="SqliteAdo"/> and Services/Sql/SqlBatch.</summary>
internal static class SqlBindGuard
{
    public static void ThrowIfUnbindable(object? value, string name)
    {
        if (value is ValueType and not (int or long or short or byte or bool or double or float
            or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or char or uint or ulong or ushort or sbyte))
            throw new ArgumentException(
                $"parameter {name}: {value.GetType().Name} is a non-primitive struct — SQLite would bind it "
                + "as a BLOB and match nothing. Unwrap the id (.Value) at the call site.", nameof(value));
    }
}
