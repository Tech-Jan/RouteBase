using RouteBaseWeb.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests (CA-04) pinning <see cref="UserCacheInvalidator"/> — the per-user
/// change-token wiring every cache-consuming service relies on. Plain class, no host.
/// </summary>
public class UserCacheInvalidatorTests
{
    [Fact]
    public void GetChangeToken_FreshUser_NotCanceled()
    {
        var invalidator = new UserCacheInvalidator();
        var token = invalidator.GetChangeToken("A");
        Assert.False(token.HasChanged);
    }

    [Fact]
    public void InvalidateUser_CancelsOutstandingToken()
    {
        var invalidator = new UserCacheInvalidator();
        var token = invalidator.GetChangeToken("A");

        invalidator.InvalidateUser("A");

        Assert.True(token.HasChanged);
    }

    [Fact]
    public void GetChangeToken_AfterInvalidate_ReturnsFreshToken()
    {
        var invalidator = new UserCacheInvalidator();
        invalidator.GetChangeToken("A");

        invalidator.InvalidateUser("A");
        var fresh = invalidator.GetChangeToken("A");

        Assert.False(fresh.HasChanged);
    }

    [Fact]
    public void InvalidateUser_UnknownUser_NoThrow()
    {
        var invalidator = new UserCacheInvalidator();
        invalidator.InvalidateUser("never-seen"); // no-op, must not throw
    }

    [Fact]
    public void InvalidateUser_OtherUsersTokenUnaffected()
    {
        var invalidator = new UserCacheInvalidator();
        var tokenB = invalidator.GetChangeToken("B");

        invalidator.InvalidateUser("A");

        Assert.False(tokenB.HasChanged);
    }

    [Fact]
    public void InvalidateUser_EvictsTokenedMemoryCacheEntry()
    {
        var invalidator = new UserCacheInvalidator();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(
            "entry",
            "value",
            new MemoryCacheEntryOptions().AddExpirationToken(invalidator.GetChangeToken("A")));

        invalidator.InvalidateUser("A");

        Assert.False(cache.TryGetValue("entry", out _));
    }
}
