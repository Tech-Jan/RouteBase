namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// The raw Activity-table write the multi-day-tour feature performs on its own behalf: when a tour is
/// deleted, its member activities keep living and only lose the tour link. Nulling <c>Activity.multi_day_ID</c>
/// is an <c>Activity</c>-table DML, so it lives here in the SQL seam rather than inlined in the tour service —
/// every Activity writer is discoverable by grepping <c>Services/Sql</c>. Legacy <c>Activity</c> has no SET NULL
/// FK on <c>multi_day_ID</c>, so the explicit unlink is required there; on fresh EF-created DBs the FK does the
/// same and this is an idempotent no-op. Run inside the same transaction as the tour-row delete.
/// ({0} = tourId)
/// </summary>
public static class MultiDayTourSql
{
    public const string UnlinkActivitiesSql =
        "UPDATE Activity SET multi_day_ID = NULL WHERE multi_day_ID = {0};";
}
