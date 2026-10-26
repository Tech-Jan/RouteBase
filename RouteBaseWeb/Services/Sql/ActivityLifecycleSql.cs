namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// The lifecycle-log INSERT for the three guarded raw-SQL transitions that cannot go through
/// <c>ActivityLifecycleService</c> because their WHERE clause IS the state guard (check and write must be one
/// statement): merge-consume / merge-restore inside the editor's save/revert transaction, and the finalize
/// adjudicator's discard. Callers run this in the SAME transaction as the guarded UPDATE, and only when that
/// UPDATE reported exactly one affected row — a log row whose state never moved would be a lie. Every other
/// lifecycle write goes through the service, which logs itself.
/// ({0} = track, {1} = user or NULL, {2} = from, {3} = to, {4} = reason, {5} = commit or NULL)
/// </summary>
public static class ActivityLifecycleSql
{
    public const string InsertLogSql =
        "INSERT INTO Activity_Lifecycle_Log (track_id, user_id, from_state, to_state, reason, commit_id, at) " +
        "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, datetime('now'));";
}
