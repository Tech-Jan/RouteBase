using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>Typed snapshot of GPX bulk-export progress. Property names map 1:1 to the wire keys.</summary>
public sealed record ActivityExportProgressSnapshot(
    bool IsRunning, int Current, int Total, string Phase, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int QueuePosition, int QueueTotal,
    long JobId, long SizeBytes, int SkippedCount, bool HasFile);

/// <summary>
/// Thread-safe per-user progress for the background GPX export. Lifecycle lives in
/// <see cref="UserProgressCore{TExtra}"/>; this class owns only the per-activity counters and the finished
/// artifact's job id/size, which is what lets the op-toast turn into a download link when the job lands.
/// </summary>
public class ActivityExportProgress(TimeProvider clock)
{
    private sealed class ExportExtras
    {
        public int Current;
        public int Total;
        public long JobId;
        public long SizeBytes;
        public int SkippedCount;
        public bool HasFile;
    }

    private readonly UserProgressCore<ExportExtras> _core = new(clock);

    private static void ResetExtras(ExportExtras x, long jobId)
    {
        x.Current = 0;
        x.Total = 0;
        x.JobId = jobId;
        x.SizeBytes = 0;
        x.SkippedCount = 0;
        x.HasFile = false;
    }

    public bool TryStart(string userId, long jobId) =>
        _core.TryStart(userId, "Starting...", x => ResetExtras(x, jobId));

    public void ForceStart(string userId, long jobId) =>
        _core.ForceStart(userId, "Starting...", x => ResetExtras(x, jobId));

    public void SetQueueInfo(string userId, int position, int total) =>
        _core.SetQueueInfo(userId, position, total);

    public void UpdateProgress(string userId, string phase, int current, int total) =>
        _core.Mutate(userId, s =>
        {
            s.Phase = phase;
            s.Extra.Current = current;
            s.Extra.Total = total;
        });

    /// <summary>Finish with a downloadable ZIP. Size and skip count are stamped in the same locked
    /// transition as the completion, so a poll can never see "done" without the file details.</summary>
    public void CompleteWithFile(string userId, long sizeBytes, int skippedCount) =>
        _core.Complete(userId, x =>
        {
            x.SizeBytes = sizeBytes;
            x.SkippedCount = skippedCount;
            x.HasFile = true;
        });

    public void Fail(string userId, string error) => _core.Fail(userId, error);

    public ActivityExportProgressSnapshot GetStatus(string userId) =>
        _core.Read(userId,
            s => new ActivityExportProgressSnapshot(
                IsRunning: s.IsRunning, Current: s.Extra.Current, Total: s.Extra.Total,
                Phase: s.Phase, Error: s.Error, StartedAt: s.StartedAt, CompletedAt: s.CompletedAt,
                QueuePosition: s.QueuePosition, QueueTotal: s.QueueTotal,
                JobId: s.Extra.JobId, SizeBytes: s.Extra.SizeBytes,
                SkippedCount: s.Extra.SkippedCount, HasFile: s.Extra.HasFile),
            idle: new ActivityExportProgressSnapshot(
                IsRunning: false, Current: 0, Total: 0, Phase: "", Error: null,
                StartedAt: null, CompletedAt: null, QueuePosition: 0, QueueTotal: 0,
                JobId: 0, SizeBytes: 0, SkippedCount: 0, HasFile: false));

    public bool IsRunning(string userId) => _core.IsRunning(userId);

    /// <summary>Clear a finished state so the toast stops re-showing it. Dismissing the toast does NOT
    /// delete the ZIP — the download URL keeps working until the retention sweep takes it.</summary>
    public void Acknowledge(string userId) => _core.Acknowledge(userId);

    /// <summary>Op-toast body (<see cref="IOperationHandler.GetQueueProgress"/>).</summary>
    public QueueProgressSnapshot BuildQueueProgress(string userId, int pendingCountForUser)
    {
        var s = GetStatus(userId);

        int liveTotal = s.QueuePosition + pendingCountForUser;
        var body = new ExportProgressResponse(
            OperationType: QueueOperationTypes.Export,
            IsRunning: s.IsRunning,
            Phase: s.Phase,
            Current: s.Current,
            Total: s.Total,
            Detail: s.Total > 0 ? $"{s.Current} / {s.Total}" : "",
            CompletedSummary: CompletedSummary(s),
            Error: s.Error,
            StartedAt: s.StartedAt,
            CompletedAt: s.CompletedAt,
            QueuePosition: s.QueuePosition,
            QueueTotal: Math.Max(s.QueueTotal, liveTotal),
            // Null until the ZIP exists — this field is what turns the toast into a download.
            DownloadUrl: s.HasFile ? $"/api/activities/export/download/{s.JobId.ToString(CultureInfo.InvariantCulture)}" : null,
            FileName: s.HasFile ? ExportFileName : null,
            SizeBytes: s.SizeBytes,
            SkippedCount: s.SkippedCount);
        return new QueueProgressSnapshot(s.IsRunning, s.CompletedAt, body);
    }

    /// <summary>The name the browser saves the ZIP under. Shared with the download endpoint so the
    /// Content-Disposition and the toast never disagree.</summary>
    public const string ExportFileName = "activities-gpx.zip";

    private static string CompletedSummary(ActivityExportProgressSnapshot s)
    {
        if (!s.HasFile) return "Export finished";
        var files = s.Current == 1 ? "1 activity" : $"{s.Current.ToString(CultureInfo.InvariantCulture)} activities";
        var size = FormatSize(s.SizeBytes);
        return $"{files} · {size}";
    }

    private static string FormatSize(long bytes)
    {
        const long Mb = 1024 * 1024;
        if (bytes >= Mb) return $"{(bytes / (double)Mb).ToString("0.#", CultureInfo.InvariantCulture)} MB";
        return $"{Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture)} KB";
    }
}
