namespace RouteBaseWeb.Services;

/// <summary>
/// SVC-08: a monotonic generation stamp mixed into the api-key auth cache key. Bumping it (on revoke or
/// regenerate) makes every previously-cached <c>hash → userId</c> entry unreachable at once, closing the
/// ≤30 s window where a read-before-delete race could re-cache a just-revoked key.
///
/// Lock-free by design — the whole point of the auth cache is to keep the 5 s GPS POST off SQLite, so the
/// hot path must not contend: <see cref="Current"/> is a single <c>Volatile.Read</c>; <see cref="Bump"/>
/// (revocations are rare) is a single <c>Interlocked.Increment</c>. Registered <c>AddSingleton</c> so the
/// stamp is process-wide, shared by the handler that reads it and the service that bumps it.
/// </summary>
public sealed class ApiKeyCacheGeneration
{
    private long _current;

    /// <summary>The current generation. A cache entry written under an older value is never read again.</summary>
    public long Current => Volatile.Read(ref _current);

    /// <summary>Advance the generation, invalidating every entry cached under the previous value at once.</summary>
    public void Bump() => Interlocked.Increment(ref _current);
}
