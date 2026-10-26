using System.Security.Cryptography;
using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins two contracts on <see cref="ApiKeyService"/>:
/// <list type="bullet">
/// <item>CC2-04: <see cref="ApiKeyService.GetStatusAsync"/> returns the DB-round-tripped CreatedAt with
/// <see cref="DateTimeKind.Utc"/> — SQLite strips the Kind, and an Unspecified DateTime serializes as
/// offset-less ISO which JS <c>new Date()</c> parses as *local* time. Named in-memory SQLite so the value
/// really round-trips through the provider.</item>
/// <item>SVC-08: revoke and regenerate <see cref="ApiKeyCacheGeneration.Bump"/> the cache generation, so a
/// stale <c>hash → userId</c> entry a race may have re-cached for a just-revoked key becomes unreachable
/// (the auth handler builds its cache key with the current generation).</item>
/// </list>
/// </summary>
public class ApiKeyServiceTests : IAsyncLifetime
{
    private const string UserId = "apikey-test-user";

    private RouteBaseDbContext _context = null!;
    private ApiKeyCacheGeneration _generation = null!;
    private ApiKeyService _service = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"apikey_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        // AppCredentials.user_id has an FK to Users — seed the owner row.
        _context.Users.Add(new AppUser
        {
            UserId = UserId, DisplayName = "ApiKey Test User", Email = "apikey@test.com", IsAdmin = false,
        });
        await _context.SaveChangesAsync();

        _generation = new ApiKeyCacheGeneration();
        _service = new ApiKeyService(_context, _generation, TimeProvider.System);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    // Mirror of the handler's/service's private key hashing (SHA-256 → lowercase hex).
#pragma warning disable CA1308 // must match ApiKeyService.HashKey lowercase-hex output; ToUpperInvariant would break the lookup
    private static string HashKey(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
#pragma warning restore CA1308

    [Fact]
    public async Task GetStatus_NoKey_ReturnsHasKeyFalseAndNullCreatedAt()
    {
        var status = await _service.GetStatusAsync(UserId);

        Assert.False(status.HasKey);
        Assert.Null(status.CreatedAt);
    }

    [Fact]
    public async Task GetStatus_AfterGenerate_CreatedAtRoundTripsAsUtcInstant()
    {
        var generated = await _service.GenerateAsync(UserId);

        // Re-read through a fresh context so the value truly round-trips SQLite (Kind stripped there —
        // AR-02: the DTO is a DateTimeOffset, so the guard is a zero offset, not DateTimeKind).
        _context.ChangeTracker.Clear();
        var status = await _service.GetStatusAsync(UserId);

        Assert.True(status.HasKey);
        Assert.NotNull(status.CreatedAt);
        Assert.Equal(TimeSpan.Zero, status.CreatedAt!.Value.Offset);
        // Same instant as generation reported (tolerate sub-second storage rounding).
        Assert.True((status.CreatedAt.Value - generated.CreatedAt).Duration() < TimeSpan.FromSeconds(1));
    }

    // ── SVC-08: generation bump invalidates the auth cache ────────────────────────

    [Fact]
    public async Task Generate_BumpsCacheGeneration()
    {
        var before = _generation.Current;

        await _service.GenerateAsync(UserId);

        Assert.Equal(before + 1, _generation.Current);
    }

    [Fact]
    public async Task Revoke_BumpsCacheGeneration()
    {
        await _service.GenerateAsync(UserId);       // seed a key to revoke (this also bumps once)
        var before = _generation.Current;

        var revoked = await _service.RevokeAsync(UserId);

        Assert.True(revoked);
        Assert.Equal(before + 1, _generation.Current);
    }

    [Fact]
    public async Task Revoke_LeavesAnyStaleAuthCacheEntry_Unreachable()
    {
        // The auth handler caches hash → userId under CacheKeys.ApiKeyUser(generation.Current, hash). Model a
        // race: the entry gets written for the CURRENT generation, right as the key is being revoked.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var generated = await _service.GenerateAsync(UserId);
        var hash = HashKey(generated.Plaintext);

        long genAtCacheTime = _generation.Current;
        cache.Set(CacheKeys.ApiKeyUser(genAtCacheTime, hash), UserId, TimeSpan.FromSeconds(30));

        // Revoke moves the generation.
        Assert.True(await _service.RevokeAsync(UserId));
        Assert.NotEqual(genAtCacheTime, _generation.Current);

        // The handler now reads under the NEW generation — the stale entry is unreachable, so the revoked
        // key does not authenticate from cache. (The stale entry still exists under its old key, unused.)
        Assert.False(cache.TryGetValue(CacheKeys.ApiKeyUser(_generation.Current, hash), out string? _));
        Assert.True(cache.TryGetValue(CacheKeys.ApiKeyUser(genAtCacheTime, hash), out string? _));

        cache.Dispose();
    }
}
