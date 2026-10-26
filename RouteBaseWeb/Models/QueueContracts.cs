using System.Text.Json.Serialization;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Models;

// Response contracts for QueueApiController. Serialized camelCase (web defaults);
// wire shapes pinned by QueueProgressWireShapeTests (byte-identical key sets per branch —
// the op-toast client polls this every 1s, so idle stays minimal on purpose). The TS side is
// GENERATED (src/js/generated/, re-exported via core/queue-progress-contract.ts) — the drift
// gate regenerates it from this file, no hand edit needed.

/// <summary>
/// Base of the GET /api/queue/progress body. A [JsonPolymorphic] hierarchy (D13): the SHAPE discriminator
/// <c>$type</c> (idle | basic | import) is written FIRST and lets the generated TS union narrow on shape,
/// while the existing <c>operationType</c> label field (analysis | heatmap | import | trackImport | idle)
/// is kept untouched — old clients/tabs mid-deploy still read it, so <c>$type</c> is purely ADDITIVE.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(IdleProgressResponse), "idle")]
[JsonDerivedType(typeof(BasicProgressResponse), "basic")]
[JsonDerivedType(typeof(ImportProgressResponse), "import")]
[JsonDerivedType(typeof(ExportProgressResponse), "export")]
public abstract record ProgressResponse(string OperationType, bool IsRunning, string Phase);

/// <summary>Idle branch of GET /api/queue/progress — the minimal shape (base fields only).</summary>
public sealed record IdleProgressResponse(string OperationType, bool IsRunning, string Phase)
    : ProgressResponse(OperationType, IsRunning, Phase);

/// <summary>
/// Analysis/heatmap/queued-synthetic/most-recently-finished branches of GET /api/queue/progress.
/// </summary>
public sealed record BasicProgressResponse(
    string OperationType,
    bool IsRunning,
    string Phase,
    int Current,
    int Total,
    string Detail,
    string CompletedSummary,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int QueuePosition,
    int QueueTotal)
    : ProgressResponse(OperationType, IsRunning, Phase);

/// <summary>Import branch of GET /api/queue/progress — BasicProgressResponse plus per-file counters/results.</summary>
public sealed record ImportProgressResponse(
    string OperationType,
    bool IsRunning,
    string Phase,
    int Current,
    int Total,
    string Detail,
    string CompletedSummary,
    int Imported,
    int Skipped,
    int Failed,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int QueuePosition,
    int QueueTotal,
    IReadOnlyList<FileResult> Results)
    : ProgressResponse(OperationType, IsRunning, Phase);

/// <summary>
/// GPX bulk-export branch of GET /api/queue/progress — BasicProgressResponse plus the finished artifact.
/// A separate branch rather than new fields on <c>basic</c>: that shape is polled every second by every
/// page and is pinned key-for-key, and only this operation ends with something to hand back.
/// <c>DownloadUrl</c> is null until the ZIP is built; it is what turns the toast into a download.
/// </summary>
public sealed record ExportProgressResponse(
    string OperationType,
    bool IsRunning,
    string Phase,
    int Current,
    int Total,
    string Detail,
    string CompletedSummary,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int QueuePosition,
    int QueueTotal,
    string? DownloadUrl,
    string? FileName,
    long SizeBytes,
    int SkippedCount)
    : ProgressResponse(OperationType, IsRunning, Phase);

/// <summary>Response of POST /api/queue/cancel. Mirrors TS <c>CancelOperationsResponse</c> (core/op-toast.ts).</summary>
public sealed record CancelOperationsResponse(string Message);
