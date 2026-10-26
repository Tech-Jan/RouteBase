using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Xunit;

// Namespace kept at the Tests root (not .Models) so it doesn't shadow the unqualified `Models.`
// prefix that RuleDocumentTests relies on. File location still mirrors Models/ChartSettings.cs.
namespace RouteBaseWeb.Tests;

/// <summary>
/// ChartSettings.LoadAsync must parse persisted-invariant values regardless of the server's
/// current culture (the bug the page-model copies had: bare TryParse under a comma-decimal culture).
/// </summary>
public class ChartSettingsTests
{
    // Stub returning a fixed Charts-category dictionary; asserts the requested category.
    private sealed class StubSettings : IProfileSettingsService
    {
        private readonly Dictionary<string, string> _charts;
        public StubSettings(Dictionary<string, string> charts) => _charts = charts;

        // Reads only — no fake here stores anything a delete could remove.
        public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
            Task.FromResult(false);

        public Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
        {
            Assert.Equal(SettingCategories.Charts, category);
            return Task.FromResult(_charts);
        }

        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default) =>
            throw new NotSupportedException();
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null) =>
            throw new NotSupportedException();
        public Task<bool> SetSettingAsync(string category, string key, object value, string userId) =>
            throw new NotSupportedException();
        public Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> settings, string userId) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task LoadAsync_ParsesDecimalInvariant()
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
        try
        {
            var settings = new StubSettings(new Dictionary<string, string>(StringComparer.Ordinal) { [SettingKeys.ChartTension] = "0.35" });

            var result = await ChartSettings.LoadAsync(settings, "user");

            Assert.Equal(0.35m, result.Tension);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public async Task LoadAsync_MissingKeys_ReturnsDefaults()
    {
        var settings = new StubSettings(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await ChartSettings.LoadAsync(settings, "user");

        Assert.Equal(30, result.SpeedSmoothingSeconds);
        Assert.Equal(250, result.SpeedBinCount);
        Assert.Equal("smoothBin", result.DefaultSpeedView);
        Assert.Equal(7, result.ElevationSmoothingWindow);
        Assert.Equal(0.2m, result.Tension);
        Assert.Equal(2, result.BorderWidth);
    }

    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var d = ChartSettings.Defaults;

        Assert.Equal(30, d.SpeedSmoothingSeconds);
        Assert.Equal(250, d.SpeedBinCount);
        Assert.Equal("smoothBin", d.DefaultSpeedView);
        Assert.Equal(7, d.ElevationSmoothingWindow);
        Assert.Equal(0.2m, d.Tension);
        Assert.Equal(2, d.BorderWidth);
    }
}
