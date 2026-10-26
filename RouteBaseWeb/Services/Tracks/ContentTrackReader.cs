using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Content-backed <see cref="ITrackReader"/>: decodes a track's CURRENT content — the object of the
/// version flagged <c>is_head</c> (<see cref="TrackContentStore"/>) — via <see cref="TrackBlobCodec"/>,
/// falling back to / concatenating live <c>Recording_Staging</c> rows for a still-recording session.
/// Concurrent first readers coalesce onto one decode via <see cref="SingleFlight"/> on a producer-owned
/// scope, so a requester abort never disturbs waiters. Singleton — no request-scoped state, no visibility logic.
///
/// The cache is keyed by the object sha, and objects are immutable: an edit writes a NEW object and moves the
/// head, so a cached decode can never be stale and no invalidation is needed. Single-flight is keyed by track
/// id (two readers of the same track want the same head), and a decode that includes staging rows is never
/// cached — it grows on every append.
/// </summary>
public sealed class ContentTrackReader : ITrackReader
{
    private readonly TrackReaderCache _cache;
    private readonly SingleFlight _singleFlight;
    private readonly IServiceScopeFactory _scopeFactory;

    public ContentTrackReader(TrackReaderCache cache, SingleFlight singleFlight, IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _singleFlight = singleFlight;
        _scopeFactory = scopeFactory;
    }

    public async Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
    {
        return await _singleFlight.RunAsync($"trackreader:{trackId}", async innerCt =>
        {
            // A coalesced waiter may still want the result after the requester aborts, so the producer
            // owns a fresh scope/context (never the request-scoped one).
            using var scope = _scopeFactory.CreateScope();
            var content = scope.ServiceProvider.GetRequiredService<TrackContentStore>();
            var staging = scope.ServiceProvider.GetRequiredService<RecordingStagingStore>();

            string? sha = await content.GetHeadShaAsync(trackId, innerCt);

            // A staged tail exists only while a session is recording. An EXISTS seek costs O(1); READING the
            // points cost a DataReader + a List<StagingPoint> on every read of every finalized track — i.e. on
            // ~100% of calls, this being the app's hottest read path.
            if (!await staging.ExistsAsync(trackId, innerCt))
                return sha is null ? null : await DecodeAsync(scope, sha, cache: true, innerCt);

            // Live/spanning session: concat the compacted head (if any) with the staging tail. Nothing on this
            // path is cached — the result grows on every append, so caching it would churn (TE-03 pin).
            var stagingPoints = await staging.GetPointsAsync(trackId, innerCt);
            var headPart = sha is null ? null : await DecodeAsync(scope, sha, cache: false, innerCt);
            return Merge(headPart, stagingPoints);
        }, ct);
    }

    /// <summary>Decode of one object, from the cache or the store. Cache hits skip the store entirely.</summary>
    private async Task<DecodedTrack?> DecodeAsync(IServiceScope scope, string sha, bool cache, CancellationToken ct)
    {
        var cached = _cache.TryGet(sha);
        if (cached != null) return cached;

        var objects = scope.ServiceProvider.GetRequiredService<ITrackObjectStore>();
        var data = await objects.GetAsync(sha, ct);
        if (data is null) return null;

        var decoded = TrackBlobCodec.Decode(data);
        if (cache) _cache.Set(sha, decoded);
        return decoded;
    }

    // Concat head decode (if any) then staging points into one ordinal-ramped DecodedTrack. Staging carries
    // no hr/grade/cadence/power — those stay null unless the head part supplied them (NULL over the tail).
    // Pressure IS staged (live barometer capture), so it flows through from both parts.
    //
    // PERF-P1: the head is ALREADY quantized int32 on the codec grid (it was decoded from the blob), so its
    // samples are copied verbatim (Array.Copy) — only the small staging tail is quantized. This is
    // value-identical to a full double-array round-trip (an already-quantized sample re-quantizes to
    // itself), but it skips the 22 full-length double[]/int[] arrays and the head's per-point dequantize→
    // requantize float work on the app's hottest (uncached, per-live-poll) read path. internal for the
    // value-identity + allocation pins in ContentTrackReaderMergeTests.
    internal static DecodedTrack Merge(DecodedTrack? headPart, IReadOnlyList<StagingPoint> staging)
    {
        int headN = headPart?.Count ?? 0;

        return new DecodedTrack
        {
            Count = headN + staging.Count,
            TimeEpochS = MergeTime(headPart, headN, staging),
            Lat = MergeRequired(headPart?.Lat ?? default, headN, staging, TrackBlobCodec.LatScale, static s => s.Lat),
            Lng = MergeRequired(headPart?.Lng ?? default, headN, staging, TrackBlobCodec.LngScale, static s => s.Lng),
            Altitude = MergeOptional(headPart?.Altitude ?? default, headN, staging, TrackBlobCodec.AltitudeScale, static s => s.Altitude),
            Distance = MergeOptional(headPart?.Distance ?? default, headN, staging, TrackBlobCodec.DistanceScale, static s => s.Distance),
            Speed    = MergeOptional(headPart?.Speed    ?? default, headN, staging, TrackBlobCodec.SpeedScale,    static s => s.Speed),
            Pressure = MergeOptional(headPart?.Pressure ?? default, headN, staging, TrackBlobCodec.PressureScale, static s => s.Pressure),
            // Staging never carries these — head-only, NULL over the tail (no tail selector).
            HeartRate   = MergeOptional(headPart?.HeartRate   ?? default, headN, staging, TrackBlobCodec.HeartRateScale,   null),
            GradeSmooth = MergeOptional(headPart?.GradeSmooth ?? default, headN, staging, TrackBlobCodec.GradeSmoothScale, null),
            Cadence     = MergeOptional(headPart?.Cadence     ?? default, headN, staging, TrackBlobCodec.CadenceScale,     null),
            Power       = MergeOptional(headPart?.Power       ?? default, headN, staging, TrackBlobCodec.PowerScale,       null),
        };
    }

    // Time: one QBias spans head+tail. A head with a present time sample keeps its own QBias so its samples
    // copy verbatim; otherwise rebase on the tail's first present epoch — exactly what RequiredTime picks
    // scanning the concatenated (head-then-tail) time for its first present epoch.
    private static TrackChannel MergeTime(DecodedTrack? headPart, int headN, IReadOnlyList<StagingPoint> staging)
    {
        var q = new int[headN + staging.Count];
        long qBias;
        if (headN > 0 && HasPresentSample(headPart!.TimeEpochS, headN))
        {
            qBias = headPart!.TimeEpochS.QBias;
            Array.Copy(headPart.TimeEpochS.RawQ!, q, headN);  // samples already relative to this QBias
        }
        else
        {
            if (headN > 0) Array.Fill(q, TrackChannel.NullQ, 0, headN); // head present but all-NULL time
            qBias = FirstPresentTailEpoch(staging) ?? 0;
        }

        for (int j = 0; j < staging.Count; j++)
        {
            long? ep = staging[j].TsEpoch;
            q[headN + j] = ep is null ? TrackChannel.NullQ : DecodedTrackExtensions.GuardQuantizeTime(ep.Value, qBias);
        }
        return new TrackChannel(q, TrackBlobCodec.TimeScale, qBias);
    }

    // Required (lat/lng): the head channel is always present when headN > 0; copy it, quantize the tail.
    private static TrackChannel MergeRequired(
        TrackChannel head, int headN, IReadOnlyList<StagingPoint> staging, double scale, Func<StagingPoint, double> tailSel)
    {
        var q = new int[headN + staging.Count];
        if (headN > 0) Array.Copy(head.RawQ!, q, headN);
        for (int j = 0; j < staging.Count; j++)
        {
            double v = tailSel(staging[j]);
            q[headN + j] = double.IsNaN(v) ? TrackChannel.NullQ : DecodedTrackExtensions.GuardQuantize(v, scale);
        }
        return new TrackChannel(q, scale);
    }

    // Optional: a whole-NULL channel (no present sample in head OR tail) stays whole-NULL (default),
    // identical to FromDoubleChannels.Optional. A present optional channel from the codec carries ≥1
    // present sample, so !IsNull is a sufficient head-present test. tailSel == null → staging never supplies
    // this channel (hr/grade/cadence/power).
    private static TrackChannel MergeOptional(
        TrackChannel head, int headN, IReadOnlyList<StagingPoint> staging, double scale, Func<StagingPoint, double?>? tailSel)
    {
        int tailN = staging.Count;
        bool headPresent = !head.IsNull;
        bool tailPresent = false;
        if (tailSel is not null)
            for (int j = 0; j < tailN && !tailPresent; j++)
                tailPresent = tailSel(staging[j]).HasValue;
        if (!headPresent && !tailPresent) return default;

        var q = new int[headN + tailN];
        if (headPresent) Array.Copy(head.RawQ!, q, headN);
        else if (headN > 0) Array.Fill(q, TrackChannel.NullQ, 0, headN);

        for (int j = 0; j < tailN; j++)
        {
            double? v = tailSel?.Invoke(staging[j]);
            q[headN + j] = v is double d && !double.IsNaN(d)
                ? DecodedTrackExtensions.GuardQuantize(d, scale)
                : TrackChannel.NullQ;
        }
        return new TrackChannel(q, scale);
    }

    private static bool HasPresentSample(TrackChannel c, int n)
    {
        var raw = c.RawQ;
        if (raw is null) return false;
        for (int i = 0; i < n; i++)
            if (raw[i] != TrackChannel.NullQ) return true;   // early-exits at the first present sample
        return false;
    }

    private static long? FirstPresentTailEpoch(IReadOnlyList<StagingPoint> staging)
    {
        for (int j = 0; j < staging.Count; j++)
            if (staging[j].TsEpoch is long ep)
                return TrackBlobCodec.Quantize(ep, TrackBlobCodec.TimeScale);
        return null;
    }
}
