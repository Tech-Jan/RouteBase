using RouteBaseWeb.Constants;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Models;

/// <summary>
/// Shared chart profile settings loaded from the profile settings store.
/// Used by Activity Details and Segment Statistics pages to avoid duplicating the same
/// setting-load logic (and its invariant parsing) in every page model. Mirror of MapSettings.
/// </summary>
public sealed class ChartSettings
{
    // Every default comes from ProfileSettingsDefaults — the PS-03 single source of truth. Restating one
    // as a literal here is what let HillshadeExaggeration diverge twice; SettingsDefaultsParityTests pins it.
    public int SpeedSmoothingSeconds { get; init; } = ProfileSettingsDefaults.SpeedSmoothingSeconds;
    public int SpeedBinCount { get; init; } = ProfileSettingsDefaults.SpeedBinCount;
    public string DefaultSpeedView { get; init; } = ProfileSettingsDefaults.DefaultSpeedView;
    public int ElevationSmoothingWindow { get; init; } = ProfileSettingsDefaults.ElevationSmoothingWindow;
    public decimal Tension { get; init; } = ProfileSettingsDefaults.ChartTension;
    public int BorderWidth { get; init; } = ProfileSettingsDefaults.ChartBorderWidth;
    // Segment chart point detail (low/normal/high/full).
    public string ChartDetail { get; init; } = ProfileSettingsDefaults.ChartDetail;

    /// <summary>
    /// Load all chart settings in a single batch read from the profile settings store.
    /// Pass the current user's id so per-user overrides win over global defaults; a null userId
    /// reads global defaults only (never a merge of every user's rows).
    /// </summary>
    public static async Task<ChartSettings> LoadAsync(IProfileSettingsService settingsService, string? userId)
    {
        var charts = await settingsService.GetCategorySettingsAsync(SettingCategories.Charts, userId);

        // Clamped/coerced through SettingsCatalog on the way OUT of the store, not just on the way in:
        // the form is one writer, but a row hand-edited in SQLite (or written by an older build with a
        // wider range) has no writer at all. Without this, tension 5.0 reaches chart-factory and a
        // ChartDetail typo silently becomes a point budget nobody chose.
        return new ChartSettings
        {
            SpeedSmoothingSeconds = SettingsCatalog.SpeedSmoothingSeconds.Clamp(
                GetInt(charts, SettingKeys.SpeedSmoothingSeconds, ProfileSettingsDefaults.SpeedSmoothingSeconds)),
            SpeedBinCount = SettingsCatalog.SpeedBinCount.Clamp(
                GetInt(charts, SettingKeys.SpeedBinCount, ProfileSettingsDefaults.SpeedBinCount)),
            ElevationSmoothingWindow = SettingsCatalog.ElevationSmoothingWindow.Clamp(
                GetInt(charts, SettingKeys.ElevationSmoothingWindow, ProfileSettingsDefaults.ElevationSmoothingWindow)),
            Tension = SettingsCatalog.ChartTension.Clamp(
                GetDecimal(charts, SettingKeys.ChartTension, ProfileSettingsDefaults.ChartTension)),
            BorderWidth = SettingsCatalog.ChartBorderWidth.Clamp(
                GetInt(charts, SettingKeys.ChartBorderWidth, ProfileSettingsDefaults.ChartBorderWidth)),
            DefaultSpeedView = SettingsCatalog.DefaultSpeedView.Coerce(
                GetString(charts, SettingKeys.DefaultSpeedView, ProfileSettingsDefaults.DefaultSpeedView)),
            ChartDetail = SettingsCatalog.ChartDetail.Coerce(
                GetString(charts, SettingKeys.ChartDetail, ProfileSettingsDefaults.ChartDetail)),
        };
    }

    /// <summary>
    /// Returns sensible defaults without any DB access.
    /// </summary>
    public static ChartSettings Defaults => new();

    // Tiny dictionary-read helpers — parse invariant (values are persisted invariant).
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static int GetInt(Dictionary<string, string> d, string k, int def) =>
        d.TryGetValue(k, out var v) && int.TryParse(v, System.Globalization.NumberStyles.Integer, Inv, out var r) ? r : def;

    private static decimal GetDecimal(Dictionary<string, string> d, string k, decimal def) =>
        d.TryGetValue(k, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out var r) ? r : def;

    private static string GetString(Dictionary<string, string> d, string k, string def) =>
        d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
}
