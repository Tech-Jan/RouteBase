namespace RouteBaseWeb.Models;

/// <summary>
/// The Recording_Session status vocabulary, encoded once — the same reasoning as <see cref="Visibilities"/>:
/// writers (RecordingService, RecordingFinalizationService, AutoReviewService) and readers (sweep/purge
/// filters) branch on these exact strings, so a typo'd literal compiles and silently strands a session
/// (e.g. never matching the finalize/purge filters). Values are DB rows and part of the phone API's
/// observable behavior — renaming one is a data migration, not an edit here.
/// </summary>
public static class RecordingSessionStatus
{
    public const string Recording      = "recording";
    public const string Paused         = "paused";
    public const string Stopped        = "stopped";
    public const string Finalized      = "finalized";
    public const string FinalizeFailed = "finalize_failed";
    public const string Abandoned      = "abandoned";

    public static readonly IReadOnlyList<string> All =
        [Recording, Paused, Stopped, Finalized, FinalizeFailed, Abandoned];

    public static bool IsValid(string? value) => value != null && All.Contains(value, StringComparer.Ordinal);
}
