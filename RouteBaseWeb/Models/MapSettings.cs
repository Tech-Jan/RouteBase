using RouteBaseWeb.Constants;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Models;

/// <summary>
/// Shared map profile settings loaded from the profile settings store.
/// Used by Activity Details, Activities Overview, and Segment Statistics pages
/// to avoid duplicating the same setting-load logic in every page model.
/// </summary>
public sealed class MapSettings
{
    // Every default comes from ProfileSettingsDefaults — the PS-03 single source of truth. These were
    // literals, and HillshadeExaggeration had drifted to 0.1 against the const's 0.2 (user decision M3b /
    // BasemapsTerrain BT-08), so a share viewer got a different terrain than the owner.
    // SettingsDefaultsParityTests pins the two together now.
    public string DefaultMapStyle { get; init; } = ProfileSettingsDefaults.DefaultMapStyle;
    public decimal HillshadeExaggeration { get; init; } = ProfileSettingsDefaults.HillshadeExaggeration;
    public decimal TerrainExaggeration { get; init; } = ProfileSettingsDefaults.TerrainExaggeration;
    public int FogDistance { get; init; } = ProfileSettingsDefaults.FogDistance;
    public int DefaultMapZoom { get; init; } = ProfileSettingsDefaults.DefaultMapZoom;

    /// <summary>
    /// Load all map settings in a single batch read from the profile settings store.
    /// Pass the current user's id so per-user overrides win over global defaults; a null userId
    /// reads global defaults only (never a merge of every user's rows).
    /// </summary>
    public static async Task<MapSettings> LoadAsync(IProfileSettingsService settingsService, string? userId)
    {
        var map = await settingsService.GetCategorySettingsAsync(SettingCategories.Map, userId);

        // Clamped/coerced through SettingsCatalog on the way OUT of the store — see ChartSettings for why
        // the write path alone is not enough. A basemap id dropped from MapBasemaps.Catalog lands here
        // too: it coerces to the default instead of reaching the registry as an id that resolves to null.
        return new MapSettings
        {
            DefaultMapStyle = SettingsCatalog.DefaultMapStyle.Coerce(
                GetString(map, SettingKeys.DefaultMapStyle, ProfileSettingsDefaults.DefaultMapStyle)),
            HillshadeExaggeration = SettingsCatalog.HillshadeExaggeration.Clamp(
                GetDecimal(map, SettingKeys.HillshadeExaggeration, ProfileSettingsDefaults.HillshadeExaggeration)),
            TerrainExaggeration = SettingsCatalog.TerrainExaggeration.Clamp(
                GetDecimal(map, SettingKeys.TerrainExaggeration, ProfileSettingsDefaults.TerrainExaggeration)),
            FogDistance = SettingsCatalog.FogDistance.Clamp(
                GetInt(map, SettingKeys.FogDistance, ProfileSettingsDefaults.FogDistance)),
            DefaultMapZoom = SettingsCatalog.DefaultMapZoom.Clamp(
                GetInt(map, SettingKeys.DefaultMapZoom, ProfileSettingsDefaults.DefaultMapZoom)),
        };
    }

    /// <summary>
    /// Returns sensible defaults without any DB access.
    /// </summary>
    public static MapSettings Defaults => new();

    // Tiny dictionary-read helpers — parse invariant (values are persisted invariant).
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static int GetInt(Dictionary<string, string> d, string k, int def) =>
        d.TryGetValue(k, out var v) && int.TryParse(v, System.Globalization.NumberStyles.Integer, Inv, out var r) ? r : def;

    private static decimal GetDecimal(Dictionary<string, string> d, string k, decimal def) =>
        d.TryGetValue(k, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out var r) ? r : def;

    private static string GetString(Dictionary<string, string> d, string k, string def) =>
        d.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : def;
}
