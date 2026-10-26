using System.Text.Json.Serialization;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Everything an activity detail render needs, independent of WHO is rendering it and under which route.
///
/// Property names mirror the ones <c>DetailsModel</c> exposed before this extraction, on purpose: it lets
/// the page markup move to a partial over this type without touching a single expression, so the
/// extraction diff stays reviewable and cannot quietly change what is rendered.
/// </summary>
public sealed class ActivityDetailView
{
    public required Activity Activity { get; init; }
    public List<GpsData> GpsData { get; init; } = [];
    public List<Split> Splits { get; init; } = [];

    /// <summary>Whether the VIEWER owns this activity. Distinct from <see cref="Surfaces"/>: a share view
    /// forces every surface off regardless of this, so the two must not be conflated.</summary>
    public bool IsOwner { get; init; }

    public ActivityViewSurfaces Surfaces { get; init; } = ActivityViewSurfaces.ReadOnlyShare;

    /// <summary>The open edit session this ride is CHECKED OUT into, if any. Null means the ride is free.</summary>
    public EditResumeHint? ResumeHint { get; init; }

    /// <summary>What the ride's revision list amounts to, without reading it — one indexed seek.</summary>
    public TrackHistoryFacts HistoryFacts { get; init; } = new(0, false);

    /// <summary>Empty on a share render: the key would be public to anyone holding the link, so share
    /// pages fall back to the keyless basemaps.</summary>
    public string MapTilerApiKey { get; init; } = string.Empty;

    /// <summary>Base path for this render's photo read endpoints — see ActivityViewOptions.</summary>
    public string PhotosApiBase { get; init; } = "/api/photos";

    /// <summary>
    /// True if the activity has usable heart rate data — either the summary avg column is set, or at least
    /// one GPS point has a non-zero HR value. Guards the HR chart card.
    /// </summary>
    public bool HasHeartRateData =>
        Activity.HeartRateAvg.HasValue ||
        GpsData.Any(p => p.HeartRate.HasValue && p.HeartRate.Value > 0);

    /// <summary>
    /// GPS track as parallel typed arrays instead of one JSON object per point — the per-point shape
    /// repeats 9 keys across every row (~0.4-4 MB for a full-res ride); columnar arrays carry each key's
    /// name once. Single pass over GpsData.
    ///
    /// Values are rounded to display precision on the way out (<see cref="GpsPrecision"/>) — the unit
    /// conversions below (<c>* 3.6</c>, <c>/ 1000.0</c>) otherwise emit IEEE-754 artifacts like
    /// <c>9.072000000000001</c>, costing bytes and compressor entropy for digits nothing can use.
    ///
    /// Two channels are omitted rather than sent as filler; see <see cref="GpsTrackWireDto"/>.
    /// </summary>
    public GpsTrackWireDto GetGpsTrackForJavaScript()
    {
        var n = GpsData.Count;
        var gpsIds = new List<int>(n);
        var lats = new List<double>(n);
        var lngs = new List<double>(n);
        var altitudes = new List<double>(n);
        var speeds = new List<double>(n);
        var distances = new List<double>(n);
        var t = new List<long?>(n);
        var heartRates = new List<double>(n);
        var cadences = new List<double>(n);
        var anyHeartRate = false;
        var anyCadence = false;

        foreach (var p in GpsData)
        {
            gpsIds.Add(p.GpsId);
            lats.Add(GpsPrecision.Coord(p.GpsLat ?? 0));
            lngs.Add(GpsPrecision.Coord(p.GpsLong ?? 0));
            altitudes.Add(GpsPrecision.Alt(p.Altitude ?? 0));
            speeds.Add(GpsPrecision.Spd((p.Speed ?? 0) * 3.6));
            distances.Add(GpsPrecision.Dist((p.Distance ?? 0) / 1000.0));
            t.Add(p.DateTimeUtc.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(p.DateTimeUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : (long?)null);

            var hr = GpsPrecision.Sensor(p.HeartRate ?? 0);
            var cad = GpsPrecision.Sensor(p.Cadence ?? 0);
            heartRates.Add(hr);
            cadences.Add(cad);
            anyHeartRate |= hr != 0;
            anyCadence |= cad != 0;
        }

        return new GpsTrackWireDto(
            // Only the segment panel reads gpsId (activity-segments.ts maps effort entry/exit ids onto
            // points). Where that panel cannot open, the channel is one int per point of dead weight.
            GpsId: Surfaces.CanCreateSegments ? gpsIds : null,
            Lat: lats,
            Lng: lngs,
            Altitude: altitudes,
            Speed: speeds,
            Distance: distances,
            T: t,
            // A ride recorded without an HR strap or cadence sensor sends thousands of zeros. The charts
            // for both are already gated server-side (HasHeartRateData), so their absence renders the same.
            HeartRate: anyHeartRate ? heartRates : null,
            Cadence: anyCadence ? cadences : null);
    }

    /// <summary>The whole #activityDetailsConfig island, named (F2a). The factory keeps every
    /// historical rounding/cast (km rounding, decimal→double, float→double tz) so the wire bytes
    /// are unchanged; [JsonPropertyName] pins carry the casing because the island is serialized
    /// with NO naming policy.</summary>
    public ActivityDetailsConfigDto GetConfigForJavaScript() => new(
        MapTilerApiKey,
        GetGpsTrackForJavaScript(),
        Activity.TrackId.Value,
        Activity.TimeTotal ?? 0,
        Math.Round((Activity.TotalDistance ?? 0) / 1000.0, 2),
        IsOwner,
        Surfaces.CanCreateSegments,
        PhotosApiBase,
        (double)(Activity.DateTimeTimezone ?? 0f));
}

/// <summary>The #activityDetailsConfig island (F2a). [JsonPropertyName] on every member is
/// load-bearing: the island is serialized by bare JsonSerializer.Serialize with NO naming policy
/// (same rule as <see cref="GpsTrackWireDto"/>).
///
/// It carries no SETTINGS: the layouts deliver those once per page as #appSettings
/// (Models/Settings/SettingsPayload.cs). This island used to ship a superset of them, which is how
/// smoothing could be ON here and OFF on a page that got the key-gated bootstrap instead.</summary>
public sealed record ActivityDetailsConfigDto(
    [property: JsonPropertyName("mapTilerApiKey")] string MapTilerApiKey,
    [property: JsonPropertyName("gpsTrack")] GpsTrackWireDto GpsTrack,
    [property: JsonPropertyName("activityId")] int ActivityId,
    [property: JsonPropertyName("totalTimeSeconds")] double TotalTimeSeconds,
    [property: JsonPropertyName("maxDistance")] double MaxDistance,
    [property: JsonPropertyName("isOwner")] bool IsOwner,
    [property: JsonPropertyName("canCreateSegments")] bool CanCreateSegments,
    [property: JsonPropertyName("photosApiBase")] string PhotosApiBase,
    [property: JsonPropertyName("tzOffsetHours")] double TzOffsetHours);

/// <summary>
/// The Details/share config island's GPS track: parallel arrays, one key per channel rather than per point.
/// Mirrors the TS <c>GpsTrackWire</c> (src/js/core/core-types.ts) — edit both in the same commit.
///
/// [JsonPropertyName] on every member is load-bearing, not decoration: this object is serialized by the
/// bare <c>JsonSerializer.Serialize</c> in _ActivityDetailScripts.cshtml, which applies NO naming policy,
/// so a record property would otherwise reach the browser as "Lat" and silently break every consumer. The
/// previous anonymous object got the casing right only because its members were literally lowercase.
///
/// Nullable channels are OMITTED from the JSON, never sent as null — the TS side reads them with
/// <c>?.[i]</c> and a zero fallback. Omission is the point: it is what removes the bytes.
/// </summary>
public sealed record GpsTrackWireDto(
    [property: JsonPropertyName("gpsId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<int>? GpsId,
    [property: JsonPropertyName("lat")] List<double> Lat,
    [property: JsonPropertyName("lng")] List<double> Lng,
    [property: JsonPropertyName("altitude")] List<double> Altitude,
    [property: JsonPropertyName("speed")] List<double> Speed,
    [property: JsonPropertyName("distance")] List<double> Distance,
    /// <summary>Epoch UTC seconds; null where the source point had no timestamp.</summary>
    [property: JsonPropertyName("t")] List<long?> T,
    [property: JsonPropertyName("heartRate")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<double>? HeartRate,
    [property: JsonPropertyName("cadence")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<double>? Cadence);
