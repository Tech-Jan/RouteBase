using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// Represents an in-progress or recently completed live GPS recording session.
/// Each session has exactly one associated Activity row (created at session start).
/// </summary>
[Table("RecordingSession")]
public class RecordingSession
{
    [Key]
    [Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [Column("track_id")]
    public TrackId TrackId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>recording | paused | stopped | finalized | abandoned | finalize_failed</summary>
    [Column("status")]
    public string Status { get; set; } = RecordingSessionStatus.Recording;

    [Column("activity_type")]
    public string ActivityType { get; set; } = "Run";

    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    [Column("stopped_at")]
    public DateTime? StoppedAt { get; set; }

    [Column("last_point_at")]
    public DateTime? LastPointAt { get; set; }

    [Column("point_count")]
    public int PointCount { get; set; }

    /// <summary>Last successfully processed client sequence number. Used for deduplication.</summary>
    [Column("last_sequence")]
    public int LastSequence { get; set; } = -1;

    /// <summary>private | friends | public</summary>
    [Column("visibility")]
    public string Visibility { get; set; } = Visibilities.Default;

    [Column("client_version")]
    public string? ClientVersion { get; set; }

    /// <summary>Device UTC offset in whole minutes (e.g. 60 = UTC+1). Used to compute local timestamps.</summary>
    [Column("timezone_offset_minutes")]
    public int? TimezoneOffsetMinutes { get; set; }
}
