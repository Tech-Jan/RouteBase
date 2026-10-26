namespace RouteBaseWeb.Models;

/// <summary>
/// The Heatmap.Status state machine, encoded once (D1). pending → running → done|failed; done → stale
/// when the underlying activity set changes; the startup reconcile flips interrupted pending/running to
/// failed (HM-11). Values are DB rows and on the wire (<see cref="HeatmapMetaResponse"/>, stamped) —
/// renaming one is a data migration, not an edit here.
/// </summary>
public static class HeatmapStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done    = "done";
    public const string Failed  = "failed";
    public const string Stale   = "stale";
}
