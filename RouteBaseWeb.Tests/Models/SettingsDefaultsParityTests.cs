using System.Reflection;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using Xunit;

// Namespace is RouteBaseWeb.Tests, NOT .Tests.Models, matching every sibling in this folder: declaring
// RouteBaseWeb.Tests.Models makes an unqualified `Models.X` elsewhere in the suite bind here instead of to
// RouteBaseWeb.Models, which breaks RaceWireContractTests and friends.
namespace RouteBaseWeb.Tests;

/// <summary>
/// PS-03 parity: every default on <see cref="MapSettings"/> / <see cref="ChartSettings"/> must come from
/// <see cref="ProfileSettingsDefaults"/>, the declared single source of truth.
///
/// This exists because the consolidation did not hold. ProfileSettingsDefaults was created *because*
/// HillshadeExaggeration had diverged across three copies — and then it diverged again: the const said
/// 0.2m (user decision M3b / BasemapsTerrain BT-08) while MapSettings restated 0.1m in two places, so an
/// anonymous share viewer (ActivityDetailViewBuilder's no-user path hands out MapSettings.Defaults) got a
/// visibly different terrain than the owner. A comment saying "single source of truth" is not a gate.
///
/// Reflection rather than a hand-listed set of asserts, deliberately: the failure mode being guarded is
/// someone ADDING a setting with a literal default, which a fixed list silently would not cover.
/// </summary>
public class SettingsDefaultsParityTests
{
    /// <summary>Settings-property name → const name, for the few that are not spelled identically.</summary>
    private static readonly Dictionary<string, string> ConstNameByProperty = new(StringComparer.Ordinal)
    {
        [nameof(ChartSettings.Tension)] = nameof(ProfileSettingsDefaults.ChartTension),
        [nameof(ChartSettings.BorderWidth)] = nameof(ProfileSettingsDefaults.ChartBorderWidth),
    };

    [Fact]
    public void ChartSettingsDefaults_ComeFromProfileSettingsDefaults() =>
        AssertEveryDefaultMatchesItsConst(ChartSettings.Defaults);

    [Fact]
    public void MapSettingsDefaults_ComeFromProfileSettingsDefaults() =>
        AssertEveryDefaultMatchesItsConst(MapSettings.Defaults);

    private static void AssertEveryDefaultMatchesItsConst(object settings)
    {
        var properties = settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Vacuity guard: a reflection walk that finds nothing would pass hollowly.
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            var constName = ConstNameByProperty.GetValueOrDefault(property.Name, property.Name);
            var field = typeof(ProfileSettingsDefaults)
                .GetField(constName, BindingFlags.Public | BindingFlags.Static);

            Assert.True(
                field is not null,
                $"{settings.GetType().Name}.{property.Name} has no ProfileSettingsDefaults.{constName}. "
                + "Add the const there and reference it, or map the name in ConstNameByProperty — do not "
                + "restate the value as a literal.");

            // GetValue, not GetRawConstantValue: C# compiles `const decimal` to a static field carrying
            // [DecimalConstant] rather than a metadata constant, so GetRawConstantValue throws on it.
            Assert.Equal(field!.GetValue(null), property.GetValue(settings));
        }
    }
}
