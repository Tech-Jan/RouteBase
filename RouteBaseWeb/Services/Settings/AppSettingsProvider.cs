using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Settings;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Settings;

/// <summary>
/// Builds the <see cref="SettingsPayload"/> the layouts render into every page's <c>#appSettings</c> island.
/// </summary>
public interface IAppSettingsProvider
{
    /// <summary>
    /// The current user's settings, or <see cref="SettingsPayload.Defaults"/> when there is no user or
    /// anything at all goes wrong. NEVER throws — see the implementation for why that is load-bearing.
    /// </summary>
    Task<SettingsPayload> GetAsync();
}

/// <summary>
/// Scoped and memoized per request: three layouts render the island, a page uses exactly one of them, but
/// the read should not repeat if that ever stops being true.
///
/// This is <c>ActivityDetailViewBuilder.LoadProfileSettingsAsync</c> hoisted out — the same two batched
/// category reads over the same parity-gated <see cref="ChartSettings"/>/<see cref="MapSettings"/> loaders,
/// so there is one place that turns stored rows into settings rather than one per page.
///
/// 🔴 IT MUST NEVER THROW. <c>_Layout</c> is the ERROR PAGE's layout, so anything it renders runs while the
/// app is already failing. If a database blip escaped from here, the exception handler's own page would
/// throw while rendering — turning a handled 500 into a blank one with no trace id, on the single route
/// whose entire job is to still work when things are broken. That is R1, and it is why the catch below is
/// deliberately total rather than typed: there is no exception from this path worth preferring a broken
/// error page over stale-but-correct defaults.
/// </summary>
public sealed class AppSettingsProvider(
    IProfileSettingsService settingsService,
    ICurrentUserService currentUser,
    ILogger<AppSettingsProvider> logger) : IAppSettingsProvider
{
    private SettingsPayload? _memoized;

    public async Task<SettingsPayload> GetAsync()
    {
        if (_memoized is not null) return _memoized;

        return _memoized = await LoadAsync();
    }

    private async Task<SettingsPayload> LoadAsync()
    {
        try
        {
            // Anonymous is not a failure and not a lookup: /s/{token} and /Error serve requests with no
            // user, and asking the store for "user null" would read global rows that no longer exist.
            // Returning here also keeps an anonymous page free of DB work entirely.
            var userId = currentUser.TryGetUserId();
            if (userId is null) return SettingsPayload.Defaults;

            // Sequential on purpose: these share the request-scoped DbContext, and EF Core forbids
            // concurrent operations on one context.
            var charts = await ChartSettings.LoadAsync(settingsService, userId);
            var map = await MapSettings.LoadAsync(settingsService, userId);

            var defaults = SettingsPayload.Defaults;
            // Coerced, like every other read: the two LoadAsync loaders clamp their own categories, and
            // these two single-key reads are the only settings in the payload that bypass them.
            var colorMode = SettingsCatalog.DefaultColorMode.Coerce(await settingsService.GetSettingAsync(
                SettingCategories.UI, SettingKeys.DefaultColorMode, userId, defaults.Ui.DefaultColorMode));
            var showDebug = await settingsService.GetSettingAsync(
                SettingCategories.UI, SettingKeys.ShowDebugInfo, userId, defaults.Ui.ShowDebugInfo);

            return new SettingsPayload(
                new SettingsPayload.MapSection(
                    map.HillshadeExaggeration, map.TerrainExaggeration, map.FogDistance,
                    map.DefaultMapZoom, map.DefaultMapStyle),
                new SettingsPayload.ChartsSection(
                    charts.SpeedSmoothingSeconds, charts.SpeedBinCount, charts.DefaultSpeedView,
                    charts.ElevationSmoothingWindow,
                    charts.Tension, charts.BorderWidth, charts.ChartDetail),
                new SettingsPayload.UiSection(showDebug, colorMode));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load app settings; falling back to defaults");
            return SettingsPayload.Defaults;
        }
    }
}
