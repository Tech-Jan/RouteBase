using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

/// <summary>
/// A user's imported photo, always assigned to an Activity (Phase-1 §5 — no unassigned rows).
/// Replaces the legacy pre-web <c>Pictures</c> table (dropped in the same migration).
/// The rendition JPEGs (thumb + preview) live on the filesystem under <c>database/photos/</c>,
/// addressed by <see cref="PhotoId"/> + tier through <c>IPhotoImageStore</c> — never DB blobs.
/// Only the ~20-byte ThumbHash placeholder is stored here (instant blur-up, no fetch).
/// </summary>
[Table("Photos")]
public class Photo
{
    [Key]
    [Column("photo_id")]
    public PhotoId PhotoId { get; set; }

    /// <summary>Owner (uploader). Serving inherits the assigned activity's visibility.</summary>
    [Column("user_id")]
    public string? UserId { get; set; }

    /// <summary>Assigned activity — NOT NULL (every photo is assigned). FK cascades on activity delete.</summary>
    [Column("track_id")]
    public TrackId TrackId { get; set; }

    // Where this photo lived before a merge moved it onto the merged ride. NULL = it has never been moved.
    // A merge must not leave its photos on rides that are now in the trash (they belong to the ride that now
    // contains that geometry), but a revert has to be able to send each one home — and the photo is the only
    // thing that knows where home was. FK ON DELETE SET NULL: purge the source and the way back is genuinely
    // gone, which is exactly what the "this merge can never be undone" confirmation warned about.
    [Column("merged_from_track_id")]
    public TrackId? MergedFromTrackId { get; set; }

    /// <summary><c>auto</c> (single clear match) | <c>manual</c> (user picked).</summary>
    [Column("assignment")]
    [MaxLength(16)]
    public string? Assignment { get; set; }

    /// <summary>Capture instant (UTC); null if unknown. Exact equality is the D5 same-time dedup key.
    /// Local time is derived from this + <see cref="TzOffset"/> — no separate local column (DOCS item 4).</summary>
    [Column("captured_utc")]
    public DateTime? CapturedUtc { get; set; }

    /// <summary>UTC offset in hours at capture (EXIF OffsetTimeOriginal / GPS-derived tz).</summary>
    [Column("tz_offset")]
    public double? TzOffset { get; set; }

    /// <summary>The photo's single resolved map location, written once at import (or on manual placement /
    /// reassign). NULL until known. <see cref="LocationSource"/> records how it was derived. This one pair
    /// is what every consumer reads — no read-time interpolation or track loading.</summary>
    [Column("lat")]
    public double? Lat { get; set; }

    [Column("lng")]
    public double? Lng { get; set; }

    /// <summary>How <see cref="Lat"/>/<see cref="Lng"/> were derived: <c>exif</c> (photo's own GPS) |
    /// <c>interpolated</c> (along the track by capture time) | <c>manual</c> (hand-placed) | <c>none</c>
    /// (not yet placed). Governs UI (exact vs estimated) and whether a reassign recomputes the point.</summary>
    [Column("location_source")]
    [MaxLength(16)]
    public string? LocationSource { get; set; }

    /// <summary>Whether the stored <see cref="Lat"/>/<see cref="Lng"/> is shown on maps. Unpinning sets this
    /// false (the photo becomes "free" — still on the activity, off the map) while keeping the coordinates,
    /// so it can be restored to the same spot. Default true; only meaningful when a location exists.</summary>
    [Column("on_map")]
    public bool OnMap { get; set; } = true;

    /// <summary>Along-track distance (m); from GPS-nearest or timestamp interpolation. A 1-D scalar for
    /// ordering — not the map point (that's <see cref="Lat"/>/<see cref="Lng"/>).</summary>
    [Column("track_position_m")]
    public double? TrackPositionM { get; set; }

    [Column("orig_filename")]
    [MaxLength(512)]
    public string? OrigFilename { get; set; }

    /// <summary>SHA-256 of the uploaded bytes — exact-byte dedup (D5), indexed per user.</summary>
    [Column("content_hash")]
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    [Column("camera_model")]
    [MaxLength(255)]
    public string? CameraModel { get; set; }

    [Column("has_exif_time")]
    public bool HasExifTime { get; set; }

    /// <summary>ThumbHash placeholder (~20–25 B) — instant blur-up, no fetch.</summary>
    [Column("thumbhash")]
    public byte[]? Thumbhash { get; set; }

    /// <summary>Which storage backend holds the rendition files. Phase-2 value is always <c>"local"</c>
    /// — forward-compat hook for multi-location (§1c); old rows read as local when a router lands.</summary>
    [Column("storage")]
    [MaxLength(32)]
    public string? Storage { get; set; }

    /// <summary>Actual stored preview dimensions (orientation-corrected; mixed sizes possible, §1a).</summary>
    [Column("width")]
    public int Width { get; set; }

    [Column("height")]
    public int Height { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("imported_utc")]
    public DateTime ImportedUtc { get; set; }
}
