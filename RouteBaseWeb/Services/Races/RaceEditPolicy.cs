namespace RouteBaseWeb.Services.Races;

/// <summary>
/// Who may EDIT a race, a separate axis from view visibility (<see cref="Models.Race.EditPolicy"/>).
/// One place for the three values so the seam, the validator and the page gate can't drift apart — and
/// so a search finds every use. The DDL default and EF's <c>HasDefaultValue</c> spell <c>owner</c>
/// literally (SQL/config, not code), which is fine: they are the ONE source of the on-disk default.
/// </summary>
public static class RaceEditPolicy
{
    public const string Owner = "owner";     // owner-only — the default; nobody but the owner edits
    public const string Friends = "friends"; // owner + accepted friends
    public const string Public = "public";   // anyone who may VIEW the race (edit never exceeds view)

    public static bool IsValid(string? policy) => policy is Owner or Friends or Public;
}
