using Microsoft.Extensions.DependencyInjection;

namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// Drains <see cref="ErrorLogChannel"/> into the store (OBS-03). One serialized SQLite writer: reads entries
/// off the channel, resolves a scoped <see cref="ErrorLogStore"/> per entry (matching DatabaseMaintenance's
/// scope-per-run), and writes it under the <see cref="ErrorLogSuppression"/> flag so any log raised inside
/// the write can't re-enter. Per-entry try/catch swallows everything — a store failure must never stop the
/// drain or surface (fail-silent, PROCESS invariant 4). Errors are rare, so per-entry scopes beat batching.
/// </summary>
public sealed class ErrorLogWriteService : BackgroundService
{
    private readonly ErrorLogChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;

    public ErrorLogWriteService(ErrorLogChannel channel, IServiceScopeFactory scopeFactory)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ErrorLogStore>();
                ErrorLogSuppression.Active = true;
                try
                {
                    await store.WriteAsync(entry, stoppingToken);
                }
                finally
                {
                    ErrorLogSuppression.Active = false;
                }
            }
            catch
            {
                // Fail-silent per entry: keep draining subsequent entries even if this write threw.
            }
        }
    }
}
