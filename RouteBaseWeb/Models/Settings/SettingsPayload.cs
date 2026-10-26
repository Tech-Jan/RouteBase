using RouteBaseWeb.Constants;

namespace RouteBaseWeb.Models.Settings;

/// <summary>
/// The one settings payload, delivered to every page as the <c>#appSettings</c> JSON island.
///
/// COMPLETE AND NON-NULLABLE, which is the whole point. The shape it replaces had optional sections, so
/// "delivered but empty" was a state every reader had to have an opinion about — and two of them disagreed:
/// <c>core/smoothing.ts</c> read a null <c>charts</c> section as smoothing OFF while
/// <c>segment-stats-delta.ts</c> read it as ON, so the same saved preference rendered differently on two
/// pages. Removing "absent" as a representable state is the fix; a reader that cannot ask the question
/// cannot answer it inconsistently.
///
/// Field set = (old bootstrap payload) ∪ (the Details island's superset) ∪ (segment <c>chartDetail</c>)
/// − <c>trackLineWidth</c>, and is pinned equal to the generated-defaults contract by
/// <c>SettingsPayloadContractGateTests</c>: a setting that can be saved but never delivered, or delivered
/// with nothing to fall back to, fails there.
///
/// Sections are NESTED on purpose. They inline into the generated document rather than becoming components,
/// so a top-level <c>MapSection</c> would be one rename away from colliding with <see cref="MapSettings"/>
/// under <c>IslandSchemasDocumentTransformer</c>'s name-keyed collision check.
///
/// ⚠ ISLAND-ONLY. Never make this a request body. <c>gen-contracts.mjs</c>'s
/// <c>relaxRequestOnlySchemas</c> step relaxes any schema reachable only from request bodies back to
/// all-optional, so a <c>PUT</c> taking this type would silently evaporate the non-nullability above with
/// NO test failing. A write endpoint needs its own <c>SettingsUpdateRequest</c>.
/// </summary>
public sealed record SettingsPayload(
    SettingsPayload.MapSection Map,
    SettingsPayload.ChartsSection Charts,
    SettingsPayload.UiSection Ui)
{
    public sealed record MapSection(
        decimal HillshadeExaggeration,
        decimal TerrainExaggeration,
        int FogDistance,
        int DefaultZoom,
        string DefaultStyle);

    public sealed record ChartsSection(
        int SpeedSmoothingSeconds,
        int SpeedBinCount,
        string DefaultSpeedView,
        int ElevationSmoothingWindow,
        decimal Tension,
        int BorderWidth,
        string ChartDetail);

    public sealed record UiSection(
        bool ShowDebugInfo,
        string DefaultColorMode);

    /// <summary>
    /// What every page renders with when no user's settings could be loaded — an anonymous share viewer,
    /// or a failure the provider swallowed rather than turning the error page into an error (R1).
    ///
    /// Every value comes from <see cref="ProfileSettingsDefaults"/>. Restating one as a literal here would
    /// recreate exactly the second-copy problem that made the seed dangerous.
    /// </summary>
    public static SettingsPayload Defaults { get; } = new(
        new MapSection(
            ProfileSettingsDefaults.HillshadeExaggeration,
            ProfileSettingsDefaults.TerrainExaggeration,
            ProfileSettingsDefaults.FogDistance,
            ProfileSettingsDefaults.DefaultMapZoom,
            ProfileSettingsDefaults.DefaultMapStyle),
        new ChartsSection(
            ProfileSettingsDefaults.SpeedSmoothingSeconds,
            ProfileSettingsDefaults.SpeedBinCount,
            ProfileSettingsDefaults.DefaultSpeedView,
            ProfileSettingsDefaults.ElevationSmoothingWindow,
            ProfileSettingsDefaults.ChartTension,
            ProfileSettingsDefaults.ChartBorderWidth,
            ProfileSettingsDefaults.ChartDetail),
        new UiSection(
            ProfileSettingsDefaults.ShowDebugInfo,
            ProfileSettingsDefaults.DefaultColorMode));
}
