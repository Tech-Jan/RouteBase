namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Per-session receipt-rate limiter for recording point batches. Keyed on the server's clock, not the
/// client's GPS timestamps (RC-02): a skewed client clock could otherwise 429 legitimate batches, which
/// the web client turns into silent GPS loss.
/// </summary>
public interface IRecordingRateLimiter
{
    /// <summary>
    /// Atomically checks the window and reserves this instant on success (RS-04). Returns false when a
    /// batch was already accepted for this session within the last window. Fusing the check and the mark
    /// closes the check-then-act race two concurrent requests could otherwise both pass.
    /// </summary>
    bool TryAcquire(string sessionId);

    /// <summary>Forget a session (call when it stops/abandons/deletes) to bound the map.</summary>
    void Clear(string sessionId);
}
