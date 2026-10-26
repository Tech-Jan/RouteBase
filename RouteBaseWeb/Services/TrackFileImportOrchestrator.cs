using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Imports;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services;

/// <summary>
/// Runs a staged activity-file batch import in parallel via the shared
/// <see cref="ParallelImportPipeline"/> skeleton (also consumed by
/// <see cref="StravaImportOrchestrator.ImportActivitiesParallelAsync"/>):
/// four files parse concurrently (fetcher) → bounded channel → single serial persister.
/// </summary>
public class TrackFileImportOrchestrator
{
    private readonly ITrackFileParser _parser;
    private readonly IActivityImportService _importService;
    private readonly TrackImportStaging _staging;
    private readonly IActivityTimezoneResolver _timezoneResolver;
    private readonly ILogger<TrackFileImportOrchestrator> _logger;

    public TrackFileImportOrchestrator(
        ITrackFileParser parser,
        IActivityImportService importService,
        TrackImportStaging staging,
        IActivityTimezoneResolver timezoneResolver,
        ILogger<TrackFileImportOrchestrator> logger)
    {
        _parser      = parser;
        _importService = importService;
        _staging       = staging;
        _timezoneResolver = timezoneResolver;
        _logger        = logger;
    }

    /// <summary>
    /// Parses and imports all files in the staged batch.
    /// <paramref name="onFileStarted"/> is invoked once per file just before persistence begins.
    /// <paramref name="onFileCompleted"/> is invoked exactly once per file with the final outcome:
    /// <c>"imported"</c>, <c>"skipped"</c>, or <c>"failed"</c> (parse failures and persist failures both).
    /// </summary>
    /// <param name="onActivityImported">
    /// Fired once per successfully persisted track with its fresh TrackId (never on skip/fail).
    /// The worker collects these to run the incremental per-import match after the batch drains.
    /// </param>
    public async Task<(int imported, int preSkipped, int failed)> ImportBatchParallelAsync(
        string userId,
        string batchId,
        Action<string> onFileStarted,
        Action<string, string, string?> onFileCompleted,
        Action<int> onActivityImported,
        CancellationToken ct,
        int maxConcurrency = 4)
    {
        var batch = await _staging.LoadAsync(userId, batchId);
        if (batch == null)
        {
            _logger.LogError("GPX staging batch {BatchId} not found for user {UserId}", batchId, userId);
            return (0, 0, 0);
        }

        var stagedFiles = batch.Files;
        bool useFilenameAsId = batch.UseFilenameAsId;

        int imported   = 0;
        int preSkipped = 0;
        int failed     = 0;

        await ParallelImportPipeline.RunAsync<TrackImportStaging.StagedFile, PreparedFile>(
            stagedFiles,
            prepareAsync: async (staged, t) =>
            {
                try
                {
                    // OBS-2: one staged file parses to ≥1 activities — a chained FIT splits into N. Each
                    // gets a distinct display name (base, "base (2)", …) so progress + dedup stay per-piece.
                    await using var stream = File.OpenRead(staged.StagedPath);
                    var results = _parser.Parse(stream, staged.OriginalName, useFilenameAsId);

                    var imports = new List<PreparedImport>(results.Count);
                    for (int i = 0; i < results.Count; i++)
                    {
                        var result = results[i];
                        var displayName = DisplayName(staged.OriginalName, i);

                        if (!result.Success || result.Summary == null)
                        {
                            _logger.LogWarning("GPX parse failed for {File}: {Error}",
                                displayName, result.ErrorMessage);
                            Interlocked.Increment(ref failed);
                            onFileCompleted(displayName, "failed",
                                result.ErrorMessage ?? "Parse failed.");
                            continue;
                        }

                        // GI-10: parser computes warnings (multi-track truncation, missing
                        // timestamps) but nothing read them — log so they're at least visible.
                        foreach (var w in result.Warnings)
                            _logger.LogWarning("GPX {File}: {Warning}", displayName, w);

                        imports.Add(new PreparedImport(result.Summary, result.GpsPoints, displayName));
                    }

                    return imports.Count > 0 ? new PreparedFile(imports) : null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GPX parse error for {File}", staged.OriginalName);
                    Interlocked.Increment(ref failed);
                    onFileCompleted(staged.OriginalName, "failed", ex.Message);
                    return null;
                }
            },
            persistAsync: async file =>
            {
                // One staged file yields ≥1 activities (chained FIT). Persist each under its OWN try so a
                // throwing piece — or a throwing progress callback (IE-07) — is counted failed without
                // dropping its chained siblings or wedging the bounded channel. The persister is serial,
                // so the post-batch incremental match still sees these in order.
                foreach (var p in file.Imports)
                {
                    try
                    {
                        onFileStarted(p.DisplayName);

                        // Blocking a re-import is correct in every case (re-importing a ride you merged away
                        // would double-count its distance). What was wrong was the MESSAGE: "duplicate" about
                        // a ride sitting in your trash tells you the file is redundant, when the ride is one
                        // click from coming back.
                        var existing = await _importService.FindExistingAsync(
                            p.Summary.ExternalId, p.Summary.Source ?? "gpx", userId);

                        if (existing is not null)
                        {
                            Interlocked.Increment(ref preSkipped);
                            onFileCompleted(p.DisplayName,
                                ImportSkipReason.For(existing), ImportSkipReason.Message(existing));
                            continue;
                        }

                        // GI-01: GPX carries no timezone, so resolve the offset from the start
                        // coordinates and stamp local wall time; keep UTC-as-local on failure.
                        await ApplyLocalTimeAsync(p.Summary, p.DisplayName, userId);

                        var activity = await _importService.ImportFromSummaryAsync(
                            p.Summary, p.GpsPoints, userId);

                        if (activity != null)
                        {
                            // GI-02: no FinalizeTrackAsync — the parser now supplies MovingTime/MaxSpeed,
                            // so device stats are no longer clobbered (segment cells are hashed in C# by the
                            // engine now, not stored per point).
                            Interlocked.Increment(ref imported);
                            onActivityImported(activity.TrackId.Value);
                            onFileCompleted(p.DisplayName, "imported", null);
                        }
                        else
                        {
                            _logger.LogError("ImportFromSummaryAsync returned null for {File}", p.DisplayName);
                            Interlocked.Increment(ref failed);
                            onFileCompleted(p.DisplayName, "failed", "Import returned null.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "GPX persist error for {File}", p.DisplayName);
                        Interlocked.Increment(ref failed);
                        onFileCompleted(p.DisplayName, "failed", ex.Message);
                    }
                }
            },
            onPersistError: (_, ex) =>
            {
                // Backstop only: the per-import try above catches everything, so this fires only for a
                // fault in the loop scaffolding itself. Count one failure so a bundle is never silently lost.
                _logger.LogError(ex, "GPX persist error for staged bundle");
                Interlocked.Increment(ref failed);
            },
            ct,
            maxConcurrency);

        return (imported, preSkipped, failed);
    }

    /// <summary>
    /// Resolves the timezone offset for a summary's start coordinates and stamps local wall
    /// time (GI-01). No timestamp → nothing to localise. Unknown offset / lookup failure →
    /// leave the parser's UTC-as-local values unchanged and warn.
    /// </summary>
    private async Task ApplyLocalTimeAsync(ExternalActivitySummary summary, string fileName, string userId)
    {
        if (!summary.StartDateUtc.HasValue) return;

        var offset = await _timezoneResolver.ResolveOffsetAsync(
            summary.StartLat, summary.StartLng, summary.StartDateUtc.Value, userId);

        if (offset.HasValue)
            TrackImportLocalTime.ApplyLocalTime(summary, offset.Value);
        else
            _logger.LogWarning("No timezone resolved for {File}; keeping UTC as local time", fileName);
    }

    /// <summary>Progress/dedup label for the i-th activity from one staged file: the raw name for the
    /// first, a " (n)" suffix for chained-FIT split pieces so each shows as its own line.</summary>
    private static string DisplayName(string originalName, int index) =>
        index == 0 ? originalName : $"{originalName} ({index + 1})";

    /// <summary>All activities parsed from one staged file (≥1; a chained FIT yields several).</summary>
    private sealed record PreparedFile(IReadOnlyList<PreparedImport> Imports);

    private sealed record PreparedImport(
        ExternalActivitySummary Summary,
        List<ExternalGpsPoint> GpsPoints,
        string DisplayName);
}
