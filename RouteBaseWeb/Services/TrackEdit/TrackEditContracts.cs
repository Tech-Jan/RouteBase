using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Frozen shared constants + small value contracts (A1 §2.5-2.7). Error codes are grep-able string
/// constants — never built from fragments; mirrored 1:1 in track-edit-errors.ts (invariant 8).
/// </summary>
public static class TrackEditErrors
{
    public const string BadSchemaVersion = "badSchemaVersion";
    public const string UnknownOp = "unknownOp";
    public const string ScriptTooLong = "scriptTooLong";
    public const string SourceMissing = "sourceMissing";
    public const string DuplicateLoad = "duplicateLoad";
    public const string SlotNotFound = "slotNotFound";
    public const string SlotConsumed = "slotConsumed";
    public const string SlotCapExceeded = "slotCapExceeded";
    public const string RangeOutOfBounds = "rangeOutOfBounds";
    public const string RangeInverted = "rangeInverted";
    public const string ResultTooSmall = "resultTooSmall";
    public const string SplitIndexOutOfBounds = "splitIndexOutOfBounds";
    public const string SelfAppend = "selfAppend";
    public const string TimeModeInvalid = "timeModeInvalid";
    public const string GapSecondsMissing = "gapSecondsMissing";
    public const string GapSecondsOutOfRange = "gapSecondsOutOfRange";
    public const string TimeAnchorMissing = "timeAnchorMissing";
    public const string ValueOutOfRange = "valueOutOfRange";
}

/// <summary>Server-enforced limits (invariant 6 — slot cap is not a UI-only guard). Mirrored in
/// track-edit-errors.ts as TRACK_EDIT_LIMITS.</summary>
public static class TrackEditLimits
{
    public const int MaxSlots = 20;              // user decision (raised from 10, 2026-07-12)
    public const int MaxOps = 500;               // replay-cost abuse guard (replay is O(points × ops))
    public const long MaxGapSeconds = 2_592_000; // 30 days
    public const int MinSlotPoints = 2;          // Decision D4 — a 1-point track has no duration/distance
}

/// <summary>Per-source version stamp recorded at load time; save re-hashes the same bytes it decodes and
/// compares before any write (invariant 1). <c>BlobSha256</c> = lowercase-hex SHA-256 over the stored
/// Track_Object.data bytes exactly as returned by TrackObjectStore; <c>PointCount</c> = blob header count.</summary>
public sealed record TrackVersionStamp(int ActivityId, int PointCount, string BlobSha256);

/// <summary>Full session snapshot (row/wire payload shape; A2 builds the endpoints). Effective script =
/// <c>Ops[0..Cursor)</c>.</summary>
public sealed record EditSessionSnapshot(
    string SessionId, string UserId, EditScript Script, int Cursor,
    IReadOnlyList<TrackVersionStamp> Stamps, string Status,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// The row fact denormalized out of <c>script_json</c>: the banner's edit count. A pure function of (script,
/// cursor), so <see cref="EditSessionStore"/> re-derives it on every write and the column cannot drift from the
/// script it summarizes. ("Which ride is this session editing?" is NOT here — it is the ride's own
/// <c>Activity.in_edit_session_id</c> pointer, which is the checkout lock as well as the answer.)
/// </summary>
public static class EditSessionFacts
{
    /// <summary>Effective edits behind the cursor, excluding the leading seed loads — exactly the number the
    /// resume banner prints. Mirrors <c>countSeedOps()</c> in edit-session-state.ts (invariant 8).</summary>
    public static int EditCount(EditScript script, int cursor)
    {
        int seed = 0;
        while (seed < script.Ops.Count && script.Ops[seed] is LoadActivityOp) seed++;
        return Math.Max(0, cursor - seed);
    }
}

public static class TrackEditContracts
{
    /// <summary>Lowercase-hex SHA-256 over the stored blob bytes (no stored hash column exists; blobs are
    /// a few MB, hashing is µs–ms). Pure.</summary>
    public static string ComputeBlobSha256(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Point count from the blob header (uint32 LE at offset 1) — the canonical parse used by
    /// TrackBlobCodec.Decode; session/save stamp checks reuse it instead of re-implementing (R-14).</summary>
    public static int BlobHeaderPointCount(byte[] blob) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(1, 4));
}
