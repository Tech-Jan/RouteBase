using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// The successor to gate (b), which pinned seed-row ↔ const parity. That gate is obsolete rather than
/// violated: the seed is deliberately gone, so there are no seeded rows left to compare.
///
/// What replaces it is the claim that made deleting the seed SAFE — an empty store yields the consts.
/// Every loader already falls back per key to <see cref="RouteBaseWeb.Constants.ProfileSettingsDefaults"/>,
/// so the 20 seeded global rows were a second copy of the defaults whose only job was to be identical to
/// the first. That is precisely how HillshadeExaggeration shipped 0.1 against a const of 0.2m: a seeded
/// row is a STORED value, so it beat the const for every user who had never touched the setting, and the
/// const became decoration.
///
/// Read against a REAL empty table rather than a fake: the fallback lives in the dictionary reads inside
/// LoadAsync, and a stub that returns an empty dictionary would exercise a different code path than a
/// store that genuinely has no rows.
///
/// Compared against <c>.Defaults</c>, not against restated literals —
/// <see cref="RouteBaseWeb.Tests.SettingsDefaultsParityTests"/> already pins <c>.Defaults</c> to the
/// consts reflectively, so this closes the chain (empty store → .Defaults → consts) without a third copy
/// of the numbers, and a setting added next month is covered with no edit here.
/// </summary>
public class SettingsEmptyStoreDefaultsTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private IMemoryCache _cache = null!;
    private ProfileSettingsService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:empty_store_{Guid.NewGuid():N}?mode=memory&cache=shared")
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

    [Fact]
    public async Task NothingIsSeeded_AtAll()
    {
        // The point of the deletion, stated directly: startup no longer writes settings rows. If seeding
        // ever comes back, the two tests below would still pass (the seed wrote the same values) while the
        // drift class it creates comes back with it — so this is the assertion that has to exist.
        Assert.Equal(0, await _context.ProfileSettings.CountAsync());
    }

    [Fact]
    public async Task EmptyStore_MapSettings_AreTheDefaults()
    {
        var loaded = await MapSettings.LoadAsync(_service, userId: "nobody");

        AssertEquivalent(MapSettings.Defaults, loaded);
    }

    [Fact]
    public async Task EmptyStore_ChartSettings_AreTheDefaults()
    {
        var loaded = await ChartSettings.LoadAsync(_service, userId: "nobody");

        AssertEquivalent(ChartSettings.Defaults, loaded);
    }

    /// <summary>Property-by-property so a failure names the setting, not just "objects differ".</summary>
    private static void AssertEquivalent(object expected, object actual)
    {
        // Instance only: MapSettings/ChartSettings expose a STATIC `Defaults` property, and an unfiltered
        // walk compares two freshly-constructed instances of a class with no value equality — which fails
        // while printing two identical-looking objects.
        var properties = expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Vacuity guard: a reflection walk that found nothing would pass hollowly.
        Assert.NotEmpty(properties);

        foreach (var property in properties)
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
    }
}
