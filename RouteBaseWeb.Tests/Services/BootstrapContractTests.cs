using System.Globalization;
using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Settings;
using RouteBaseWeb.Pages;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the activities-grid bootstrap wire contract (MD-03): the camelCase JSON keys that
/// overview/activities-grid.ts reads off the #activitiesGridBootstrap JSON island must survive C# renames.
/// </summary>
public class BootstrapContractTests
{
    [Fact]
    public void ActivitiesGridBootstrap_SerializesExpectedKeys()
    {
        var boot = new ActivitiesGridBootstrap
        {
            InitialData = new ActivityGridResponse(
                Rows: [], FilteredIds: [], OwnerIds: [], Total: 1, UnfilteredTotal: 2, OwnedFilteredCount: 0),
            MultiUser = true,
            TypeOptions = ["Run"],
            ExtraColumns = ["power"],
            PageSize = 50,
        };

        var json = BootstrapJson.Serialize(boot);

        foreach (var key in new[]
        {
            "initialData", "rows", "filteredIds", "ownerIds", "total", "unfilteredTotal",
            "multiUser", "typeOptions", "ownerOptions", "extraColumns", "sort", "pageSize",
        })
            Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);

        // Sort sub-object keys the TS reads.
        Assert.Contains("\"column\"", json, StringComparison.Ordinal);
        Assert.Contains("\"direction\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the #appSettings island wire contract: camelCase keys read by maplibre-helpers.ts /
    /// chart-factory.ts / smoothing.ts / segment-stats-*.ts, and culture-invariant decimals — the old
    /// Activities.cshtml hand-built literal used CurrentCulture and emitted "0,2" (broken JS) on
    /// comma-decimal server locales.
    ///
    /// The ARRANGE was rewritten when the per-page ProfileSettingsBootstrap was deleted: the payload is
    /// built by IAppSettingsProvider now, so there is no page model to construct. Every assertion is
    /// unchanged — the wire shape this pins did not move, only who produces it.
    /// </summary>
    [Fact]
    public void AppSettingsPayload_SerializesExpectedKeys_CultureInvariant()
    {
        var payload = new SettingsPayload(
            new SettingsPayload.MapSection(0.2m, 1.5m, 10, 13, "streets"),
            new SettingsPayload.ChartsSection(15, 300, "smoothBin", 7, 0.35m, 2, "auto"),
            new SettingsPayload.UiSection(false, "none"));

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        string json;
        try
        {
            json = BootstrapJson.Serialize(payload);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // The four at the end are what makes this payload COMPLETE: a page that supplies only the two map
        // values still emits them, from SettingsPayload.Defaults. Without them pinned, the payload could
        // silently go back to carrying whatever subset each call site happened to have — which is the
        // defect this contract exists to remove. Nothing was dropped from this list.
        foreach (var key in new[]
        {
            "map", "hillshadeExaggeration", "terrainExaggeration", "fogDistance",
            "charts", "speedSmoothingSeconds", "speedBinCount", "defaultSpeedView", "elevationSmoothingWindow",
            "tension", "borderWidth",
            "ui", "showDebugInfo",
            "defaultZoom", "defaultStyle", "chartDetail", "defaultColorMode",
        })
            Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);

        // No section may serialize as null — "delivered but empty" is the state whose removal lets every
        // reader drop its fallback (smoothing.ts read a null charts section as OFF, segment-stats-delta as ON).
        Assert.DoesNotContain(":null", json, StringComparison.Ordinal);

        // Ordinal on purpose: xunit's string Contains/DoesNotContain default to CurrentCulture
        // comparison, and under th-TH collation "," and "." are ignorable-equal — the culture
        // this test exists to guard against would corrupt its own asserts.
        Assert.Contains("\"hillshadeExaggeration\":0.2", json, StringComparison.Ordinal);
        Assert.Contains("\"terrainExaggeration\":1.5", json, StringComparison.Ordinal);
        Assert.Contains("\"tension\":0.35", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0,2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", json, StringComparison.Ordinal);

        // The API key goes to the #mapTilerKey island (core/map-api-key seam), never into profileSettings.
        Assert.DoesNotContain("mapTilerApiKey", json, StringComparison.Ordinal);
    }
}
