using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;

namespace RouteBaseWeb.Services;

/// <summary>
/// Singleton that provides per-user CancellationChangeTokens for cache eviction.
/// When a friendship changes, both users' tokens are cancelled, which evicts
/// all IMemoryCache entries linked to those tokens.
/// </summary>
public class UserCacheInvalidator
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a change token for the given user. Cache entries should use
    /// <c>MemoryCacheEntryOptions.AddExpirationToken(invalidator.GetChangeToken(userId))</c>
    /// so they are evicted when the user's friendships change.
    /// </summary>
    public IChangeToken GetChangeToken(string userId)
    {
        var cts = _tokens.GetOrAdd(userId, _ => new CancellationTokenSource());
        return new CancellationChangeToken(cts.Token);
    }

    /// <summary>
    /// Invalidates all cache entries for the given user by cancelling their token
    /// and replacing it with a fresh one.
    /// </summary>
    public void InvalidateUser(string userId)
    {
        if (_tokens.TryRemove(userId, out var oldCts))
        {
            // Not disposed: a concurrent GetChangeToken may still touch cts.Token after we remove it;
            // a cancelled CTS without timers holds no resources worth the ObjectDisposedException race.
            oldCts.Cancel();
        }
    }
}
