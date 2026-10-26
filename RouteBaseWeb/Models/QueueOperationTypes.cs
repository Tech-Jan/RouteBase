namespace RouteBaseWeb.Models;

/// <summary>
/// The queue's wire LABEL vocabulary (D2): the <c>operationType</c> field of the
/// GET /api/queue/progress body, produced by each handler's <c>QueueDiscriminator</c> and the
/// progress projectors. Distinct from the <c>$type</c> SHAPE discriminators on
/// <see cref="ProgressResponse"/> (idle | basic | import), which the serializer owns — the two
/// vocabularies overlap by coincidence, not by contract.
/// </summary>
public static class QueueOperationTypes
{
    public const string Idle        = "idle";
    public const string Analysis    = "analysis";
    public const string Import      = "import";       // activity import (FIT/TCX/Strava)
    public const string TrackImport = "trackImport";  // GPX file batch
    public const string Heatmap     = "heatmap";
    public const string Export      = "export";       // GPX bulk export (ZIP built in the background)
}
