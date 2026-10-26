using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Contract tests for <see cref="MapTilerService"/> — per-user MapTiler API-key storage over the
/// unified <see cref="AppCredentials"/> table. Previously only indirectly exercised via
/// KeysAndDataModelTests. Pins: user scoping, the Type filter, the read-side whitespace guard
/// (a saved blank key reads as "no key"), most-recently-updated wins, in-place upsert, and the
/// catch-all resilience path (errors return null/false, never throw).
///
/// Shared-cache in-memory SQLite with a keep-alive connection; AppCredentials.user_id is an FK to
/// Users, so both test users are seeded (matching StravaAuthServiceTests).
/// </summary>
public class MapTilerServiceTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private RouteBaseDbContext _context = null!;

    private const string User = "maptiler-user";
    private const string OtherUser = "other-user";

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:maptiler_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync(); // keeps the in-memory DB alive for the fixture's lifetime

        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = User });
        _context.Users.Add(new AppUser { UserId = OtherUser });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private MapTilerService MakeSut(string userId = User) =>
        new(_context, NullLogger<MapTilerService>.Instance, new TestCurrentUserService(userId), TimeProvider.System);

    private async Task SeedKeyAsync(string? apiKey, string userId, string type = CredentialTypes.MapTilerCredentials,
        DateTime? updatedAt = null)
    {
        _context.AppCredentials.Add(new AppCredentials
        {
            Type = type,
            ApiKey = apiKey,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    // ─── GetApiKeyAsync ───

    [Fact]
    public async Task GetApiKey_NoRow_ReturnsNull()
    {
        var key = await MakeSut().GetApiKeyAsync();
        Assert.Null(key);
    }

    [Fact]
    public async Task GetApiKey_ReturnsSavedKeyForUser()
    {
        await SeedKeyAsync("abc123", User);

        var key = await MakeSut().GetApiKeyAsync();

        Assert.Equal("abc123", key);
    }

    [Fact]
    public async Task GetApiKey_WhitespaceKey_ReturnsNull()
    {
        // Read-side guard: a row exists but its key is blank → treated as no key.
        await SeedKeyAsync("   ", User);

        var key = await MakeSut().GetApiKeyAsync();

        Assert.Null(key);
    }

    [Fact]
    public async Task GetApiKey_IgnoresOtherUsersKey()
    {
        await SeedKeyAsync("other-secret", OtherUser);

        var key = await MakeSut(User).GetApiKeyAsync();

        Assert.Null(key); // scoped to current user — no cross-user bleed
    }

    [Fact]
    public async Task GetApiKey_IgnoresNonMapTilerType()
    {
        // A Strava-credentials row for the same user that happens to carry an ApiKey column value
        // must not be returned — the query filters on Type.
        await SeedKeyAsync("strava-not-maptiler", User, type: CredentialTypes.StravaCredentials);

        var key = await MakeSut(User).GetApiKeyAsync();

        Assert.Null(key);
    }

    [Fact]
    public async Task GetApiKey_MultipleRows_ReturnsMostRecentlyUpdated()
    {
        var now = DateTime.UtcNow;
        await SeedKeyAsync("old-key", User, updatedAt: now.AddMinutes(-10));
        await SeedKeyAsync("new-key", User, updatedAt: now);

        var key = await MakeSut(User).GetApiKeyAsync();

        Assert.Equal("new-key", key); // OrderByDescending(UpdatedAt).First()
    }

    [Fact]
    public async Task GetApiKey_BrokenContext_ReturnsNullNotThrow()
    {
        // Query against a DB with no schema throws SqliteException; the service must swallow it and
        // return null (keyless-map default is a normal state, never a hard failure).
        await using var broken = NewEmptyDb(out var conn);
        var sut = new MapTilerService(broken, NullLogger<MapTilerService>.Instance, new TestCurrentUserService(User), TimeProvider.System);

        var key = await sut.GetApiKeyAsync();

        Assert.Null(key);
        await conn.DisposeAsync();
    }

    // ─── SaveApiKeyAsync ───

    [Fact]
    public async Task SaveApiKey_NoExisting_InsertsRowWithCorrectFields()
    {
        var ok = await MakeSut(User).SaveApiKeyAsync("fresh-key");

        Assert.True(ok);
        var row = await _context.AppCredentials.SingleAsync(c => c.UserId == User);
        Assert.Equal(CredentialTypes.MapTilerCredentials, row.Type);
        Assert.Equal("fresh-key", row.ApiKey);
        Assert.NotEqual(default, row.CreatedAt);
        Assert.NotEqual(default, row.UpdatedAt);
    }

    [Fact]
    public async Task SaveApiKey_Existing_UpdatesInPlaceNoDuplicate()
    {
        var created = DateTime.UtcNow.AddDays(-1);
        _context.AppCredentials.Add(new AppCredentials
        {
            Type = CredentialTypes.MapTilerCredentials, ApiKey = "old-key", UserId = User,
            CreatedAt = created, UpdatedAt = created,
        });
        await _context.SaveChangesAsync();

        var ok = await MakeSut(User).SaveApiKeyAsync("rotated-key");

        Assert.True(ok);
        var row = await _context.AppCredentials.SingleAsync(c => c.UserId == User); // still exactly one
        Assert.Equal("rotated-key", row.ApiKey);
        Assert.Equal(created, row.CreatedAt);        // CreatedAt preserved
        Assert.True(row.UpdatedAt > created);        // UpdatedAt advanced
    }

    [Fact]
    public async Task SaveApiKey_ScopedToUser_DoesNotTouchOtherUser()
    {
        await SeedKeyAsync("other-key", OtherUser);

        var ok = await MakeSut(User).SaveApiKeyAsync("my-key");

        Assert.True(ok);
        Assert.Equal("my-key", await MakeSut(User).GetApiKeyAsync());
        Assert.Equal("other-key", await MakeSut(OtherUser).GetApiKeyAsync()); // untouched
        // A fresh row for User was inserted rather than the other user's row being hijacked.
        Assert.Equal(1, await _context.AppCredentials.CountAsync(c => c.UserId == User));
        Assert.Equal(1, await _context.AppCredentials.CountAsync(c => c.UserId == OtherUser));
    }

    [Fact]
    public async Task SaveApiKey_BrokenContext_ReturnsFalse()
    {
        await using var broken = NewEmptyDb(out var conn);
        var sut = new MapTilerService(broken, NullLogger<MapTilerService>.Instance, new TestCurrentUserService(User), TimeProvider.System);

        var ok = await sut.SaveApiKeyAsync("nope");

        Assert.False(ok);
        await conn.DisposeAsync();
    }

    // ─── HasApiKeyAsync ───

    [Fact]
    public async Task HasApiKey_TrueWhenKeyPresent()
    {
        await SeedKeyAsync("present", User);
        Assert.True(await MakeSut(User).HasApiKeyAsync());
    }

    [Fact]
    public async Task HasApiKey_FalseWhenNoRow()
    {
        Assert.False(await MakeSut(User).HasApiKeyAsync());
    }

    [Fact]
    public async Task HasApiKey_SavedBlankKey_RoundTripsAsAbsent()
    {
        // Save accepts a blank key (effectively "clear"); Get's whitespace guard then reports absence,
        // so Has is false. Pins the full Save→Has round-trip of the cleared state.
        var ok = await MakeSut(User).SaveApiKeyAsync("   ");

        Assert.True(ok); // the write itself succeeds
        Assert.False(await MakeSut(User).HasApiKeyAsync());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A context over a shared-cache in-memory DB whose schema was never created — any query throws
    /// SqliteException. The caller disposes the returned keep-alive connection after the assertion.
    /// </summary>
    private static RouteBaseDbContext NewEmptyDb(out SqliteConnection keepAlive)
    {
        var connectionString = $"Data Source=file:maptiler_empty_{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        return new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options);
    }
}
