using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Key-value store for one user's saved preferences.
///
/// WRITES require a user — there are no global rows to write. READS still take a nullable userId because
/// several callers sit on a path that can be anonymous (a share page, the error page); those get an empty
/// result and apply <see cref="Constants.ProfileSettingsDefaults"/>, which is the same value the deleted
/// global rows were supposed to hold.
/// </summary>
public interface IProfileSettingsService
{
    Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default);
    Task<string?> GetSettingAsync(string category, string key, string? userId = null);
    Task<bool> SetSettingAsync(string category, string key, object value, string userId);
    Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> settings, string userId);
    Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null);

    /// <summary>
    /// Remove one setting. False when the user had no row for it.
    ///
    /// The store had no delete at all, because the settings page only ever OVERWRITES — a catalog setting
    /// always has a value. Keys that are DATA rather than catalog entries (a saved import mapping the user
    /// named) need one: "unset it" is not the same as "set it back to the default".
    /// </summary>
    Task<bool> DeleteSettingAsync(string category, string key, string userId);
}
