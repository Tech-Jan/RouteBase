namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Ages out stale ACTIVE edit sessions (B6, user decision 2026-07-20). A checkout held by an idle
/// session blocks revert/restore/new checkout on its ride indefinitely (the live DB held one 8 days);
/// idle past <see cref="IdleDiscardThreshold"/> ⇒ DISCARD — uncommitted edits are dropped and the FK's
/// ON DELETE SET NULL releases the ride's <c>in_edit_session_id</c>, exactly like a user discard
/// (no track data was ever touched). Discard over parked drafts keeps the state model simple; SAVED
/// rows are commit provenance and never match (the store's status guard).
/// </summary>
public sealed class EditSessionAgeOutService : BackgroundService
{
    /// <summary>B6's N: an ACTIVE session with no write for this long is discarded.</summary>
    public static readonly TimeSpan IdleDiscardThreshold = TimeSpan.FromDays(14);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EditSessionAgeOutService> _logger;
    private readonly TimeProvider _clock;

    public EditSessionAgeOutService(IServiceScopeFactory scopeFactory, ILogger<EditSessionAgeOutService> logger, TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<EditSessionStore>();
            var discarded = await sessions.DiscardIdleActiveAsync(_clock.GetUtcNow().UtcDateTime - IdleDiscardThreshold, ct);
            if (discarded > 0)
                _logger.LogInformation(
                    "EditSessionAgeOut: discarded {Count} edit session(s) idle > {Days} days (checkouts released)",
                    discarded, IdleDiscardThreshold.TotalDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Edit-session age-out sweep failed");
        }
    }
}
