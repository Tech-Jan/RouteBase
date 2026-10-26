using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for <see cref="ProfileSettingsService"/>: per-user scoping (US-01) and invariant number
/// parsing (US-02).
///
/// GLOBAL ROWS NO LONGER EXIST (step 1b): UserId is NOT NULL, so the old "user row overrides global row"
/// and "writes invalidate both cache keys" cases pin a deleted concept rather than a broken one — they are
/// rewritten below to pin what replaced it. Defaults are applied on read from ProfileSettingsDefaults; that
/// they survive an empty store is SettingsEmptyStoreDefaultsTests' job, not this file's.
/// </summary>
public class ProfileSettingsServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private IMemoryCache _cache = null!;
    private ProfileSettingsService _service = null!;

    private const string Cat = "TestCat";

    public async Task InitializeAsync()
    {
        var dbName = $"settings_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, _cache, TimeProvider.System);
    }

    public Task DisposeAsync()
    {
        _cache.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    private async Task SeedAsync(string key, string value, string userId)
    {
        _context.ProfileSettings.Add(new ProfileSettings
        {
            Category = Cat, SettingKey = key, SettingValue = value, UserId = userId
        });
        await _context.SaveChangesAsync();
    }

    // ─── US-01: per-user scoping ───

    [Fact]
    public async Task GetCategorySettings_NullUser_ReturnsEmpty()
    {
        await SeedAsync("K", "95", "userA");
        await SeedAsync("K", "80", "userB");

        var result = await _service.GetCategorySettingsAsync(Cat, null);

        // No user, no rows — and emphatically not a merge of every user's rows, which is the cross-user
        // leak the old null-key path was fixed for. Callers apply ProfileSettingsDefaults on empty.
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCategorySettings_ReturnsOnlyThisUsersRows()
    {
        await SeedAsync("K", "95", "userA");
        await SeedAsync("Other", "5", "userB");

        var result = await _service.GetCategorySettingsAsync(Cat, "userA");

        Assert.Equal("95", result["K"]);
        // A key this user never saved is simply absent — the reader's default fills it, no stored fallback.
        Assert.False(result.ContainsKey("Other"));
    }

    [Fact]
    public async Task GetCategorySettings_IsCachedPerUserKey()
    {
        await SeedAsync("K", "95", "userA");
        await SeedAsync("K", "80", "userB");

        var a = await _service.GetCategorySettingsAsync(Cat, "userA");
        var b = await _service.GetCategorySettingsAsync(Cat, "userB");

        Assert.Equal("95", a["K"]); // no shared-entry bleed between users
        Assert.Equal("80", b["K"]);
    }

    [Fact]
    public async Task SetSetting_InvalidatesTheUsersCacheKey()
    {
        await SeedAsync("K", "95", "userA");
        await SeedAsync("K", "80", "userB");

        // Warm both users' entries so a stale read would be visible.
        await _service.GetCategorySettingsAsync(Cat, "userA");
        await _service.GetCategorySettingsAsync(Cat, "userB");

        await _service.SetSettingAsync(Cat, "K", 20, "userA");

        Assert.Equal("20", (await _service.GetCategorySettingsAsync(Cat, "userA"))["K"]); // write took
        Assert.Equal("80", (await _service.GetCategorySettingsAsync(Cat, "userB"))["K"]); // untouched
    }

    // ─── US-02: invariant number handling ───

    [Fact]
    public async Task ConvertAndStore_RoundTripsDecimal_UnderCommaCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            await _service.SetSettingAsync(Cat, "D", 0.2m, "userA");
            var read = await _service.GetSettingAsync<decimal>(Cat, "D", "userA");

            Assert.Equal(0.2m, read);
            var stored = _context.ProfileSettings.Single(s => s.SettingKey == "D").SettingValue;
            Assert.Equal("0.2", stored); // persisted invariant, not "0,2"
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Convert_LegacyCommaValue_ParsesViaFallback()
    {
        _context.ProfileSettings.Add(new ProfileSettings
        {
            Category = Cat, SettingKey = "D", SettingValue = "0,2", UserId = "userA"
        });
        await _context.SaveChangesAsync();

        var read = await _service.GetSettingAsync<decimal>(Cat, "D", "userA");

        Assert.Equal(0.2m, read); // comma legacy row parsed via dot fallback
    }

    // ─── PS-05: batch write (one SaveChanges) ───

    [Fact]
    public async Task SetSettingsAsync_UpsertsExistingAndNew()
    {
        await SeedAsync("Existing", "1", "userA"); // pre-existing row to update

        await _service.SetSettingsAsync(new (string, string, object)[]
        {
            (Cat, "Existing", 42),  // update
            (Cat, "New", 7),        // insert
        }, "userA");

        Assert.Equal("42", (await _service.GetCategorySettingsAsync(Cat, "userA"))["Existing"]);
        Assert.Equal("7", (await _service.GetCategorySettingsAsync(Cat, "userA"))["New"]);
        // Exactly two rows for userA in this category (update didn't duplicate).
        Assert.Equal(2, await _context.ProfileSettings.CountAsync(s => s.Category == Cat && s.UserId == "userA"));
    }

    [Fact]
    public async Task SetSettingsAsync_AllOrNothing_OnBatchFailure()
    {
        var before = await _context.ProfileSettings.CountAsync();

        // Two NEW rows sharing (category,key,userId) violate the unique index → SaveChanges throws,
        // and the single transaction rolls the whole batch back.
        await Assert.ThrowsAnyAsync<Exception>(() => _service.SetSettingsAsync(new (string, string, object)[]
        {
            (Cat, "Dup", 1),
            (Cat, "Dup", 2),
        }, "userA"));

        Assert.Equal(before, await _context.ProfileSettings.CountAsync()); // nothing persisted
    }

}
