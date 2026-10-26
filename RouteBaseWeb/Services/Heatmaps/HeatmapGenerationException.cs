namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// A heatmap-generation failure whose <see cref="System.Exception.Message"/> is CURATED to be safe and
/// useful to show a viewer — the heatmap poll endpoint (<c>GetHeatmap</c>) surfaces it to friends' and
/// public-segment viewers. Every OTHER exception's message is replaced with a generic string before it is
/// persisted to <c>Heatmap.Error</c>, so a raw provider/IO/exception message never leaks through that read
/// path (API-04). Throw this — never a bare <see cref="System.Exception"/> — for a condition whose text the
/// viewer is meant to see (e.g. "run analysis first", "segment too large to render").
/// </summary>
public sealed class HeatmapGenerationException : Exception
{
    public HeatmapGenerationException(string message) : base(message) { }
    public HeatmapGenerationException(string message, Exception inner) : base(message, inner) { }
}
