namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The names a new race's two mandatory checkpoints are seeded with.
///
/// PERSISTED, not rendered: they go into the row at creation, so they are the user's text from that
/// moment on and every checkpoint name — including these two — is editable
/// (<c>RaceCheckpointCrud.UpdateCheckpointAsync</c> allows a rename on the endpoints; only their POSITION
/// is fixed). Constants rather than inline literals so there is exactly one place to change the seed, and
/// so a grep for the default finds it.
///
/// A localised default would need the creating user's language at write time and would then be frozen in
/// the row anyway, which is a different (and much larger) feature than one string per race.
/// </summary>
public static class RaceCheckpointDefaults
{
    public const string Start = "Start";
    public const string Finish = "Finish";
}
