using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RouteBaseWeb.Models;

// K1 strong ids — INTERNAL tier only. Wire DTOs and route params stay int; controllers wrap at
// entry (`new CommitId(x)`) and unwrap at exit (`.Value`). EF maps each id through its nested
// EfConverter (registered in RouteBaseDbContext.ConfigureConventions), so the column stays INTEGER
// and the DDL-parity gates see no change. Design rules, deliberate:
//   - readonly record struct: value equality + Where ==/joins/Contains translate through the
//     converter (proven by StrongIdEfProbeTests — the K1 gate).
//   - explicit ToString override: the record default would leak "CommitId { Value = 5 }" into
//     cache keys and log lines.
//   - NO implicit/explicit conversion operators: the whole point is that an int is not an id;
//     conversions would silently re-open every confusion this type closes.
// STOP-LINE: loner ids with a single call surface (LogId, HeatmapId, SaveMapper slot indexes,
// checkpoint ordinals…) deliberately STAY int — a wrapper that never crosses a seam buys nothing.
// Documented defers (S9): SegmentGps.SegGpsId and the GPS point ids (GpsData.GpsId, the
// entry/exit windows on GpsInSegment) stay int — both are ARITHMETIC id spaces, not opaque ids:
// the match engine does zone math in seg_GPS_ID units (SegmentGeometry.StartZoneEnd/GapThreshold)
// and GpsId is the 0-based track ordinal post-BlobStore (offsets, -1 sentinel, window spans).
// Neither crosses a seam as a scalar param; a wrapper would add .Value at every arithmetic site.
// Share_Link.share_link_ID stays int (S10): its AUTOINCREMENT is load-bearing (a revoked token id
// must never be reused — DbSchemaParityTests pins it) and the SQLite provider refuses to emit
// AUTOINCREMENT for a converter-mapped key (SqliteAnnotationProvider requires Converter == null).

/// <summary>Track_Edit_Commit.commit_id (+ FKs Activity.edited/consumed_commit_id,
/// Activity_Lifecycle_Log.commit_id, raw Activity_Version.commit_id).</summary>
public readonly record struct CommitId(int Value) : IComparable<CommitId>
{
    public int CompareTo(CommitId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<CommitId, int>
    {
        public EfConverter() : base(id => id.Value, v => new CommitId(v)) { }
    }
}

/// <summary>Activity_Version.version_id — raw-DDL only (no EF entity, no converter needed);
/// the S7 proof of the unwrap style for raw-SQL id families.</summary>
public readonly record struct VersionId(int Value) : IComparable<VersionId>
{
    public int CompareTo(VersionId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Activity.track_ID — the app's most viral id (S8). Key + every EF FK to it
/// (Photo, GpsInSegment, RecordingSession, ActivityLifecycleLog, SegmentEffortExclusion,
/// Race.Course/Reference, RaceParticipant?, ShareLink?, Photo.MergedFrom?).</summary>
public readonly record struct TrackId(int Value) : IComparable<TrackId>
{
    public int CompareTo(TrackId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<TrackId, int>
    {
        public EfConverter() : base(id => id.Value, v => new TrackId(v)) { }
    }
}

/// <summary>Segment.segment_ID (S9). Key + EF FKs SegmentGps, GpsInSegment, Heatmap,
/// SegmentEffortExclusion, ShareLink?.</summary>
public readonly record struct SegmentId(int Value) : IComparable<SegmentId>
{
    public int CompareTo(SegmentId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<SegmentId, int>
    {
        public EfConverter() : base(id => id.Value, v => new SegmentId(v)) { }
    }
}

/// <summary>GPSinSegment.GPSinSegment_ID (S9) — one matched effort; the "effortId" that crosses
/// the efforts/exclusion/performance-cache seams.</summary>
public readonly record struct GpsInSegmentId(int Value) : IComparable<GpsInSegmentId>
{
    public int CompareTo(GpsInSegmentId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<GpsInSegmentId, int>
    {
        public EfConverter() : base(id => id.Value, v => new GpsInSegmentId(v)) { }
    }
}

/// <summary>Race.race_ID (S10). Key + FKs RaceCheckpoint, RaceParticipant, ShareLink?.</summary>
public readonly record struct RaceId(int Value) : IComparable<RaceId>
{
    public int CompareTo(RaceId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<RaceId, int>
    {
        public EfConverter() : base(id => id.Value, v => new RaceId(v)) { }
    }
}

/// <summary>Race_Checkpoint.checkpoint_ID (S10). Key + half of RaceSplitTime's composite key.</summary>
public readonly record struct CheckpointId(int Value) : IComparable<CheckpointId>
{
    public int CompareTo(CheckpointId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<CheckpointId, int>
    {
        public EfConverter() : base(id => id.Value, v => new CheckpointId(v)) { }
    }
}

/// <summary>Race_Participant.participant_ID (S10). Key + half of RaceSplitTime's composite key.</summary>
public readonly record struct ParticipantId(int Value) : IComparable<ParticipantId>
{
    public int CompareTo(ParticipantId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<ParticipantId, int>
    {
        public EfConverter() : base(id => id.Value, v => new ParticipantId(v)) { }
    }
}

/// <summary>Photo.photo_id (S10) — key only; crosses the photo read/derivative seams.</summary>
public readonly record struct PhotoId(int Value) : IComparable<PhotoId>
{
    public int CompareTo(PhotoId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfConverter : ValueConverter<PhotoId, int>
    {
        public EfConverter() : base(id => id.Value, v => new PhotoId(v)) { }
    }
}

