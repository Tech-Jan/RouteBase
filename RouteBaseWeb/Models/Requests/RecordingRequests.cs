namespace RouteBaseWeb.Models.Requests;

public class StartRecordingRequest
{
    /// <summary>Name for a session whose client sent none — the phone never sends one.</summary>
    public const string DefaultTrackName = "Live Recording";

    /// <summary>Substituted for <see cref="DefaultTrackName"/> when <see cref="AutoDetected"/> is set,
    /// so an auto-started ride is telling apart from a manual one in the review queue by name alone.</summary>
    public const string AutoDetectedTrackName = "Live Auto Recording";

    public string ActivityType { get; set; } = "Run";
    public string TrackName { get; set; } = DefaultTrackName;
    public string Visibility { get; set; } = Visibilities.Default;
    public string? ClientVersion { get; set; }

    /// <summary>
    /// Device UTC offset in whole minutes (e.g. 60 = UTC+1, -300 = UTC-5).
    /// Stored on the RecordingSession at start; applied only at finalization to compute
    /// Activity.start_datetime_loc and datetime_timezone (RecordingFinalizationService).
    /// GPS points carry UTC only — no per-point local time is written.
    /// </summary>
    public int? TimezoneOffsetMinutes { get; set; }

    /// <summary>
    /// True when the phone's always-on auto-detection started this session (not a manual tap).
    /// Stamps Activity.source='auto-recording' + lifecycle in-work/awaiting-review so the ride lands in the
    /// Record page's pending-review rows instead of the normal lists until the user confirms it.
    /// </summary>
    public bool AutoDetected { get; set; }
}

public class RecordingPointsBatch
{
    public List<RecordingGpsPoint> Points { get; set; } = [];

    /// <summary>
    /// Monotonically increasing counter from the client. Used to detect and reject
    /// duplicate batches from retry storms without re-inserting GPS points.
    /// </summary>
    public int ClientSequence { get; set; }
}

public class RecordingGpsPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? Altitude { get; set; }
    public double? Speed { get; set; }

    /// <summary>Raw barometric pressure in hPa when the phone has a barometer (null otherwise). Stored
    /// verbatim in its own blob channel — never overwritten by the DEM elevation replace.</summary>
    public double? Pressure { get; set; }

    private DateTime _timestampUtc;

    /// <summary>
    /// The wire field is an instant. The slice normalizes DateTimeKind on the way OUT (LiveSessionDto,
    /// RecordingSessionDto) but had no guard on the way IN, and two consumers are Kind-sensitive:
    /// the validator compares raw ticks against <c>DateTime.UtcNow</c>, and staging calls
    /// <c>ToUniversalTime()</c>. A valid ISO instant with a numeric offset (<c>+02:00</c>) deserializes
    /// to Kind=Local with the value shifted to server-local — 2 h "in the future" on a non-UTC server,
    /// which 400s the whole batch. Converted (Local) or assumed-Utc (Unspecified) exactly once, here —
    /// CC2-04 / LV-02.
    /// </summary>
    public DateTime TimestampUtc
    {
        get => _timestampUtc;
        set => _timestampUtc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
