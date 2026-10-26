using RouteBaseWeb.Helpers;
using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Add-track suggestions + search (A4 §3.3). Session-scoped convenience over the owner's OWN confirmed
/// activities — invariant 1 still governs the actual load (the ops-endpoint guards are the authority).
/// Every query reads Activity SCALAR columns only (StartGps*, StartDateTime*, TrackName) — zero per-point
/// work, no blob reads, no decodes. Proximity uses a SQL bbox prefilter then an exact in-memory
/// <see cref="GeoUtils.HaversineDistance"/> on the survivors (§3.3 cost model). A recipe-(b) reader — it
/// never produces ops or touches session state; the client turns a pick into one <c>loadActivity</c> op.
/// </summary>
public sealed class EditSuggestionService
{
    private const double SuggestionRadiusMeters = 1000;
    private const int SuggestionCap = 20;
    private const int SearchCap = 50;
    private const double MetersPerDegreeLat = 111_320.0;

    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly EditSessionStore _sessions;

    public EditSuggestionService(
        RouteBaseDbContext context, IVisibilityService visibility, EditSessionStore sessions)
    {
        _context = context;
        _visibility = visibility;
        _sessions = sessions;
    }

    /// <summary>Proximity (end ↔ candidate start ≤ 1 km) ∪ same-day offers, loaded ids excluded server-side
    /// (D-A4-3). Null when the session is missing/foreign (controller → 404); absent classes contribute
    /// nothing. Order: NearEnd by gap asc, then SameDay-only by local date asc; capped at 20.</summary>
    public async Task<TrackSuggestionsResponse?> GetSuggestionsAsync(
        string sessionId, double? endLat, double? endLng, string? localDate, CancellationToken ct = default)
    {
        var ctx = await LoadContextAsync(sessionId, ct);
        if (ctx is null) return null;
        var (loaded, baseQ) = ctx.Value;

        var byId = new Dictionary<int, Candidate>();

        if (endLat is double lat && endLng is double lng)
        {
            var dLat = SuggestionRadiusMeters / MetersPerDegreeLat;
            var dLng = dLat / Math.Max(0.2, Math.Cos(lat * Math.PI / 180.0)); // clamp cos near the poles
            var near = await baseQ
                .Where(a => a.StartGpsLat != null && a.StartGpsLong != null
                    && a.StartGpsLat >= lat - dLat && a.StartGpsLat <= lat + dLat
                    && a.StartGpsLong >= lng - dLng && a.StartGpsLong <= lng + dLng)
                .Select(Projection)
                .ToListAsync(ct);
            foreach (var row in near)
            {
                if (loaded.Contains(row.TrackId)) continue;
                var gap = GeoUtils.HaversineDistance(lat, lng, row.StartGpsLat!.Value, row.StartGpsLong!.Value);
                if (gap > SuggestionRadiusMeters) continue;
                var c = Get(byId, row);
                c.NearEnd = true;
                c.StartGapMeters = c.StartGapMeters is double g ? Math.Min(g, gap) : gap;
            }
        }

        if (TryParseDay(localDate, out var day))
        {
            var next = day.AddDays(1);
            var sameDay = await baseQ
                .Where(a => a.StartDateTimeLocal != null && a.StartDateTimeLocal >= day && a.StartDateTimeLocal < next)
                .Select(Projection)
                .ToListAsync(ct);
            foreach (var row in sameDay)
            {
                if (loaded.Contains(row.TrackId)) continue;
                Get(byId, row).SameDay = true;
            }
        }

        var suggestions = byId.Values
            .OrderByDescending(c => c.NearEnd)                                // proximity first
            .ThenBy(c => c.StartGapMeters ?? double.MaxValue)                 // nearer first
            .ThenBy(c => c.Row.StartDateTimeLocal ?? DateTime.MaxValue)       // then same-day by date asc
            .Take(SuggestionCap)
            .Select(c => c.ToDto(alreadyLoaded: false))
            .ToList();
        return new TrackSuggestionsResponse(suggestions);
    }

    /// <summary>Name substring (LIKE, ASCII-case-insensitive; wildcards in <paramref name="q"/> escaped) and/or
    /// same-day filter, newest-first, capped at 50. Loaded ids are INCLUDED but flagged <c>AlreadyLoaded</c>
    /// (D-A4-8 — rendered disabled). Null when the session is missing/foreign (controller → 404).</summary>
    public async Task<TrackSuggestionsResponse?> SearchAsync(
        string sessionId, string? q, string? date, CancellationToken ct = default)
    {
        var ctx = await LoadContextAsync(sessionId, ct);
        if (ctx is null) return null;
        var (loaded, baseQ) = ctx.Value;

        var query = baseQ;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{EscapeLike(q)}%";
            query = query.Where(a => a.TrackName != null && EF.Functions.Like(a.TrackName, pattern, "\\"));
        }
        if (TryParseDay(date, out var day))
        {
            var next = day.AddDays(1);
            query = query.Where(a => a.StartDateTimeLocal != null && a.StartDateTimeLocal >= day && a.StartDateTimeLocal < next);
        }

        var rows = await query
            .OrderByDescending(a => a.StartDateTimeUtc)
            .Take(SearchCap)
            .Select(Projection)
            .ToListAsync(ct);
        var suggestions = rows
            .Select(row => ToDto(row, sameDay: false, nearEnd: false, startGap: null, alreadyLoaded: loaded.Contains(row.TrackId)))
            .ToList();
        return new TrackSuggestionsResponse(suggestions);
    }

    // ---- shared base + helpers ----

    private async Task<(HashSet<int> Loaded, IQueryable<Activity> BaseQ)?> LoadContextAsync(
        string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null || session.UserId != _visibility.GetCurrentUserId()) return null;
        // Exclude what the effective (visible) script already loads — a redo-tail load isn't "loaded" (re-adding
        // truncates it), matching the ops endpoint's duplicate-load semantics.
        var loaded = session.Script.Ops.Take(session.Cursor)
            .OfType<LoadActivityOp>().Select(o => o.ActivityId).ToHashSet();
        var baseQ = _visibility.OwnedActivities(_context.Activities).Where(a => a.Lifecycle == LifecycleStates.Released);
        return (loaded, baseQ);
    }

    // Project to scalars in the DB query — never materialize a full Activity (no per-point cost).
    private static readonly System.Linq.Expressions.Expression<Func<Activity, ActivityRow>> Projection =
        a => new ActivityRow(
            a.TrackId.Value, a.TrackName, a.ActivityType, a.StartDateTimeLocal,
            a.TotalDistance, a.StartGpsLat, a.StartGpsLong);

    private static Candidate Get(Dictionary<int, Candidate> byId, ActivityRow row)
    {
        if (!byId.TryGetValue(row.TrackId, out var c)) { c = new Candidate(row); byId[row.TrackId] = c; }
        return c;
    }

    private static bool TryParseDay(string? value, out DateTime day) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static SuggestedActivityDto ToDto(
        ActivityRow row, bool sameDay, bool nearEnd, double? startGap, bool alreadyLoaded) =>
        new(row.TrackId, row.TrackName ?? $"Activity {row.TrackId}", row.ActivityType,
            row.StartDateTimeLocal.ToWallClockIso(), row.TotalDistance, startGap, sameDay, nearEnd, alreadyLoaded);

    private sealed record ActivityRow(
        int TrackId, string? TrackName, string? ActivityType, DateTime? StartDateTimeLocal,
        double? TotalDistance, double? StartGpsLat, double? StartGpsLong);

    private sealed class Candidate(ActivityRow row)
    {
        public ActivityRow Row { get; } = row;
        public bool NearEnd { get; set; }
        public bool SameDay { get; set; }
        public double? StartGapMeters { get; set; }

        public SuggestedActivityDto ToDto(bool alreadyLoaded) =>
            EditSuggestionService.ToDto(Row, SameDay, NearEnd, StartGapMeters, alreadyLoaded);
    }
}
