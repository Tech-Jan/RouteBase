using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

public class ProfileSettingsService : IProfileSettingsService
{
    private readonly RouteBaseDbContext _context;
    private readonly ILogger<ProfileSettingsService> _logger;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    // Settings rarely change — 5-minute TTL keeps the cache fresh without stale risk.
    private static readonly TimeSpan SettingsCacheTtl = TimeSpan.FromMinutes(5);

    private static string CategoryCacheKey(string category, string? userId) =>
        CacheKeys.ProfileSettingsCategory(category, userId);

    /// <summary>
    /// Invalidate the cached category dictionary after a write. One key: writes target a specific user and
    /// reads for that user hit the same key. The second "also clear the null key" removal died with the
    /// global rows — a null-userId read no longer returns rows a write could have changed.
    /// </summary>
    private void InvalidateCategoryCache(string category, string userId)
    {
        _cache.Remove(CategoryCacheKey(category, userId));
    }

    public ProfileSettingsService(
        RouteBaseDbContext context,
        ILogger<ProfileSettingsService> logger,
        IMemoryCache cache,
        TimeProvider clock)
    {
        _context = context;
        _logger  = logger;
        _cache   = cache;
        _clock   = clock;
    }

    /// <summary>
    /// Get a setting value with type conversion
    /// </summary>
    public async Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
    {
        // GetCategorySettingsAsync handles DB exceptions internally (logs at Error, returns empty dict).
        // ConvertValue<T> handles parse exceptions internally (returns defaultValue).
        // Callers see defaultValue for both "key not found" and "DB/parse error" --
        // the distinction is visible only in logs: DB errors appear at Error level,
        // missing keys produce no log entry.
        var categorySettings = await GetCategorySettingsAsync(category, userId);

        if (!categorySettings.TryGetValue(key, out var rawValue))
            return defaultValue;

        return ConvertValue<T>(rawValue, defaultValue);
    }

    /// <summary>
    /// Get setting value as string
    /// </summary>
    public async Task<string?> GetSettingAsync(string category, string key, string? userId = null)
    {
        return await GetSettingAsync<string>(category, key, userId);
    }

    /// <summary>
    /// Set a setting value
    /// </summary>
    public async Task<bool> SetSettingAsync(string category, string key, object value, string userId)
    {
        // No try/catch: the sole caller (ProfileSettings page) wraps saves and shows an error banner.
        // Swallowing here reported "saved successfully" on a silently-failed save.
        var valueString = await UpsertSettingEntityAsync(category, key, value, userId);

        await _context.SaveChangesAsync();

        // Invalidate the category cache so the next read reflects the new value.
        InvalidateCategoryCache(category, userId);

        _logger.LogInformation("Setting saved: {Category}.{Key} = {Value}", category, key, valueString);
        return true;
    }

    /// <summary>
    /// Persist multiple settings for one user in a single SaveChanges (one SQLite write transaction),
    /// so a Save click is atomic instead of 18 separate commits (PS-05). Mid-batch failure rolls back
    /// the whole set; caller shows an error banner.
    /// </summary>
    public async Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> settings, string userId)
    {
        foreach (var (category, key, value) in settings)
            await UpsertSettingEntityAsync(category, key, value, userId);

        await _context.SaveChangesAsync();

        // Same cache invalidation as the per-item path, once per touched category.
        foreach (var category in settings.Select(s => s.Category).Distinct(StringComparer.Ordinal))
            InvalidateCategoryCache(category, userId);

        _logger.LogInformation("Saved {Count} settings for user {UserId}", settings.Count, userId);
    }

    // Adds or updates the entity for one setting on the tracked context WITHOUT calling SaveChanges —
    // so callers can commit one item or a batch in a single transaction. Returns the invariant-serialized
    // value string for logging.
    private async Task<string> UpsertSettingEntityAsync(string category, string key, object value, string userId)
    {
        var setting = await _context.ProfileSettings
            .FirstOrDefaultAsync(s => s.Category == category && s.SettingKey == key && s.UserId == userId);

        // Invariant serialization — persisted numerics are culture-independent (matches GridFilterParsing,
        // GpxExportService). CurrentCulture would write "0,2" on a de-DE host and break invariant readers.
        var valueString = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        if (setting == null)
        {
            setting = new ProfileSettings
            {
                Category     = category,
                SettingKey   = key,
                SettingValue = valueString,
                UserId       = userId,
                CreatedAt    = _clock.GetUtcNow().UtcDateTime,
                UpdatedAt    = _clock.GetUtcNow().UtcDateTime
            };
            _context.ProfileSettings.Add(setting);
        }
        else
        {
            setting.SettingValue = valueString;
            setting.UpdatedAt    = _clock.GetUtcNow().UtcDateTime;
        }

        return valueString;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSettingAsync(string category, string key, string userId)
    {
        var setting = await _context.ProfileSettings
            .FirstOrDefaultAsync(s => s.Category == category && s.SettingKey == key && s.UserId == userId);
        if (setting == null) return false;

        _context.ProfileSettings.Remove(setting);
        await _context.SaveChangesAsync();

        // Same invalidation as a write — a deleted row that stays in the cached category dictionary reads
        // as still-present for the next five minutes, which is exactly how a "deleted" profile comes back.
        InvalidateCategoryCache(category, userId);

        _logger.LogInformation("Setting deleted: {Category}.{Key}", category, key);
        return true;
    }

    /// <summary>
    /// Get all settings for a category
    /// </summary>
    public async Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
    {
        // No user, no rows. Every row is user-scoped since step 1b, so there is nothing a null userId could
        // match — return empty without a query and let the caller apply ProfileSettingsDefaults, which is
        // what it would have got from the old global rows anyway. Not cached: there is nothing to cache.
        if (userId == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var cacheKey = CategoryCacheKey(category, userId);
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
            return cached;

        try
        {
            // One row per (Category, SettingKey) for this user, enforced by the unique index — so no
            // GroupBy/merge. That collapse is the point of UserId NOT NULL.
            var result = await _context.ProfileSettings
                .Where(s => s.Category == category && s.UserId == userId)
                .ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue, StringComparer.Ordinal);

            _cache.Set(cacheKey, result, SettingsCacheTtl);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting category settings for {Category}", category);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Convert string value to target type
    /// </summary>
    private T? ConvertValue<T>(string value, T? defaultValue)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            var targetType = typeof(T);
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            if (targetType == typeof(string))
                return (T)(object)value;

            // Invariant parsing — values are persisted invariant (see SetSettingAsync). No thousands separators.
            if (targetType == typeof(int) || targetType == typeof(int?))
                return (T)(object)int.Parse(value, System.Globalization.NumberStyles.Integer, inv);

            if (targetType == typeof(decimal) || targetType == typeof(decimal?))
                return (T)(object)ParseInvariantWithCommaFallback(value, s => decimal.Parse(s, System.Globalization.NumberStyles.Float, inv));

            if (targetType == typeof(bool) || targetType == typeof(bool?))
                return (T)(object)bool.Parse(value);

            if (targetType == typeof(double) || targetType == typeof(double?))
                return (T)(object)ParseInvariantWithCommaFallback(value, s => double.Parse(s, System.Globalization.NumberStyles.Float, inv));

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    // Parses invariant; if that fails and the string uses a comma decimal separator, retries with a
    // dot. Covers rows written earlier under a comma culture before invariant serialization landed.
    private static TNum ParseInvariantWithCommaFallback<TNum>(string value, Func<string, TNum> parse)
    {
        try { return parse(value); }
        catch (FormatException) when (value.Contains(',', StringComparison.Ordinal)) { return parse(value.Replace(',', '.')); }
    }
}
