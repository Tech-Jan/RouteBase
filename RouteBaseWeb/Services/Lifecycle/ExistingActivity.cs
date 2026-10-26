using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Lifecycle;

/// <summary>An activity the import dedupe already knows about — and what it currently IS.</summary>
public sealed record ExistingActivity(int TrackId, string? Name, string Lifecycle, string? Reason);

/// <summary>
/// Why an import skipped a file. Pure — no DB, no DTOs.
///
/// A skip is always CORRECT here (re-importing a ride you merged away would double-count its distance); the
/// bug this fixes was never the blocking, it was the message. "Duplicate" is a lie about a ride sitting in
/// your trash — it tells you the file is redundant when in fact the ride is one click from coming back, which
/// is exactly the dead end the original incident left the user in.
/// </summary>
public static class ImportSkipReason
{
    /// <summary>
    /// The outcome code the import UI renders (see import.ts <c>Status</c>) and the progress counters bucket.
    /// <c>Skipped</c> keeps its original wire value — an ordinary duplicate is exactly what it always was, and
    /// changing it would silently drop it out of the skipped counter. The two new codes are the cases that
    /// could not be told apart before.
    /// </summary>
    public const string Skipped = "skipped";
    public const string InTrash = "in-trash";
    public const string InWork  = "in-work";

    public static string For(ExistingActivity existing) => existing.Lifecycle switch
    {
        LifecycleStates.Trashed => InTrash,
        LifecycleStates.InWork  => InWork,
        _                       => Skipped,
    };

    /// <summary>The human sentence, naming the ride so it can actually be found again.</summary>
    public static string Message(ExistingActivity existing)
    {
        string name = string.IsNullOrWhiteSpace(existing.Name)
            ? $"Activity {existing.TrackId}"
            : existing.Name;

        return existing.Lifecycle switch
        {
            LifecycleStates.Trashed when existing.Reason == LifecycleReasons.EditMerge =>
                $"Already imported — in the trash as “{name}”, merged into another ride",
            LifecycleStates.Trashed => $"Already imported — in the trash as “{name}”",
            LifecycleStates.InWork  => $"Already imported — awaiting review as “{name}”",
            _                       => $"Already imported as “{name}”",
        };
    }
}
