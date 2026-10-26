namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Points as per-channel <see cref="TrackChannel"/>s, ordinal-indexed, ordered by GPS_ID.
/// Each channel stores quantized int32 samples and dequantizes on read (value = (q + QBias) * Scale);
/// a per-point NULL reads as double.NaN, a whole-NULL channel is the default <see cref="TrackChannel"/>.
/// GPS_ID addressing: ordinal ≡ GPS_ID post re-key (ids are the ramp 0..Count-1, not stored); use
/// <see cref="OrdinalRange"/> for GPS_ID windows.
///
/// Pure CONTENT — it carries no track id, deliberately. A decode is addressed by the object sha and shared
/// across every activity that has those bytes (two imports of the same GPX, a save-as-copy of an untouched
/// slot), so an id stored here would be whichever activity happened to decode first and would then be handed
/// to all the others. Callers know which track they asked for; they stamp the id at the boundary where it
/// belongs (<see cref="DecodedTrackExtensions.ToGpsDataList"/>, <see cref="RouteBaseWeb.Services.GpsSlice"/>).
/// Immutable after decode — cached instances are shared across consumers.
/// </summary>
public sealed class DecodedTrack
{
    public required int Count { get; init; }
    public required TrackChannel Lat        { get; init; }
    public required TrackChannel Lng        { get; init; }
    public required TrackChannel TimeEpochS { get; init; }
    public TrackChannel Altitude    { get; init; }   // default = whole-NULL
    public TrackChannel Distance    { get; init; }
    public TrackChannel Speed       { get; init; }
    public TrackChannel HeartRate   { get; init; }
    public TrackChannel GradeSmooth { get; init; }
    public TrackChannel Cadence     { get; init; }
    public TrackChannel Power       { get; init; }
    public TrackChannel Pressure    { get; init; }   // raw barometric hPa (live recording); default = whole-NULL

    /// <summary>Ordinals covering window [entry..exit] inclusive. GPS_IDs are the ordinal
    /// ramp 0..Count-1 (post re-key), so this is pure clamping — same point set as the old
    /// bisection over the ramp, including out-of-range and inverted bounds (lo &gt; hi = empty).</summary>
    public (int lo, int hi) OrdinalRange(int entryGpsId, int exitGpsId)
    {
        int lo = entryGpsId < 0 ? 0 : Math.Min(entryGpsId, Count);
        int hi = exitGpsId < 0 ? -1 : Math.Min(exitGpsId, Count - 1);
        return (lo, hi);
    }
}
