using System.Globalization;

namespace RouteBaseWeb.Helpers;

/// <summary>
/// Sibling rule for calendar-DAY filtering (stated once, at the owning seam): filter by the
/// subject's own local calendar day, never by a UTC column — see
/// <see cref="Services.Photos.IPhotoQueryService.ListAsync"/>.
/// </summary>
public static class DateTimeIso
{
    /// <summary>
    /// The wall-clock wire format (FE-12): <c>2026-07-28T14:05:00</c> — no offset, no Z, no sub-seconds.
    /// </summary>
    public const string WallClockFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>
    /// For a LOCAL wall-clock value the browser buckets by calendar day (dashboard KPIs, the calendar
    /// heatmap, grid date filters) — the opposite case from <see cref="AsUtc(DateTime)"/>.
    ///
    /// These went out as <c>ToString("o")</c> of a Kind-Unspecified DateTime, which happens to emit no
    /// offset and so happens to parse as local in the browser. That is an accident of Kind, not a
    /// contract: a backfill that stored the same values as <c>Kind.Utc</c> would append <c>Z</c>, and
    /// every day bucket would silently shift for a CET viewer — an evening ride landing on the next day.
    /// Formatting explicitly makes the wire say what it means and pins it against that Kind change.
    /// </summary>
    public static string? ToWallClockIso(this DateTime? dt) =>
        dt?.ToString(WallClockFormat, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToWallClockIso(DateTime?)" />
    public static string ToWallClockIso(this DateTime dt) =>
        dt.ToString(WallClockFormat, CultureInfo.InvariantCulture);

    /// <summary>UTC-stored timestamps lose Kind on the SQLite round-trip; stamp Utc so "O" emits Z.</summary>
    public static string ToUtcIso(this DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The <see cref="DateTimeOffset"/> projection of a stored-UTC value (AR-02) — the instant half of
    /// the migration off raw DateTime response fields. The IMPLICIT DateTime→DateTimeOffset conversion
    /// is the trap this exists to avoid: for the Kind-Unspecified values SQLite hands back it assumes
    /// the SERVER'S LOCAL offset — the CC2-04 bug rebuilt at the type boundary. Convert through this at
    /// the projection; implicit conversion is safe only for values that provably carry Kind.Utc.
    /// </summary>
    public static DateTimeOffset ToUtcOffset(this DateTime dt) => new(dt.AsUtc());

    /// <inheritdoc cref="ToUtcOffset(DateTime)" />
    public static DateTimeOffset? ToUtcOffset(this DateTime? dt) =>
        dt is { } value ? value.ToUtcOffset() : null;

    /// <summary>
    /// The Kind-stamping half of the round-trip fix (CC2-04): every timestamp in this app is stored UTC,
    /// but SQLite hands it back as <c>Unspecified</c>. Since AR-02 no raw <see cref="DateTime"/> is on the
    /// JSON wire (ResponseDtoDateTimeRatchetTests pins that at zero), so this survives as the plumbing
    /// under <see cref="ToUtcOffset(DateTime)"/> and for non-JSON surfaces that format a DateTime directly.
    ///
    /// Apply in the in-memory projection, never inside an IQueryable — it does not translate to SQL.
    /// </summary>
    public static DateTime AsUtc(this DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    /// <inheritdoc cref="AsUtc(DateTime)" />
    public static DateTime? AsUtc(this DateTime? dt) =>
        dt is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
}
