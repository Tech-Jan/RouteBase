using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Process-wide gate around <see cref="IRecordingFinalizationService.FinalizeAsync"/>. A stop can be
/// retried by the client (90s read timeout + 3 retries) and the cleanup service retries stuck
/// finalizations — so two finalizations can target the same session concurrently, each encoding+writing
/// the blob and recomputing stats on the same track. Coalesces concurrent callers onto one execution via
/// <see cref="SingleFlight"/> keyed by session id; sequential calls are a fast no-op (the "already
/// finalized" guard inside the service).
///
/// The producer creates its OWN scope — never the caller's scoped DbContext — because a coalesced
/// execution can outlive the request that started it (see <see cref="SingleFlight"/> remarks).
/// </summary>
public sealed class RecordingFinalizationCoordinator
{
    private readonly SingleFlight _singleFlight;
    private readonly IServiceScopeFactory _scopeFactory;

    public RecordingFinalizationCoordinator(SingleFlight singleFlight, IServiceScopeFactory scopeFactory)
    {
        _singleFlight = singleFlight;
        _scopeFactory = scopeFactory;
    }

    public Task FinalizeAsync(string sessionId, CancellationToken ct = default) =>
        _singleFlight.RunAsync($"recording-finalize-{sessionId}", async _ =>
        {
            using var scope = _scopeFactory.CreateScope();
            var finalization = scope.ServiceProvider.GetRequiredService<IRecordingFinalizationService>();
            await finalization.FinalizeAsync(sessionId);
            return true;
        }, ct);
}
