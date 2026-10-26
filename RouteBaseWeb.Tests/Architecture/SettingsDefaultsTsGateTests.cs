using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using RouteBaseWeb.Constants;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// The TS half of the PS-03 single-source-of-truth rule: <c>src/js/generated/settings-defaults.ts</c> is
/// rendered from <see cref="ProfileSettingsDefaults"/> and diffed here.
///
/// Before this gate, ~16 TS call sites hand-copied the C# defaults as literal <c>?? 0.2</c> fallbacks, and
/// they had drifted: tension was <c>?? 0</c> against the C# 0.2, and hillshadeExaggeration was <c>?? 0.0</c>
/// against 0.2 — where 0.0 does not merely look different, it makes the caller skip the hillshade layer
/// entirely. Nothing could catch that, because nothing related the two numbers.
///
/// Hosted in <c>dotnet test</c> rather than a script, for the same reason as
/// <see cref="RouteBaseWeb.Tests.Api.ContractsDriftGateTests"/>: no CI exists, so a script invoked by
/// nothing is not a gate. Rendering happens in C# via reflection over the consts, which avoids parsing C#
/// from node — the fragile way to do this.
///
/// To update: change the const, run this test, paste the printed content into the generated file.
/// </summary>
public class SettingsDefaultsTsGateTests
{
    private const string GeneratedRelativePath = "RouteBaseWeb/src/js/generated/settings-defaults.ts";

    /// <summary>
    /// The wire contract: const name → (TS section, TS property). Section+property names match the
    /// <c>ProfileSettings</c> TS interface in core-types.ts and the camelCase the bootstrap island emits,
    /// so a reader can write <c>getProfileSettings()?.charts?.tension ?? DEFAULTS.charts.tension</c>.
    ///
    /// <c>internal</c> so <see cref="SettingsPayloadContractGateTests"/> can hold the payload's field set
    /// against it: one list of settings, not two that drift.
    /// </summary>
    internal static readonly (string Const, string Section, string Property)[] Contract =
    [
        (nameof(ProfileSettingsDefaults.SpeedSmoothingSeconds), "charts", "speedSmoothingSeconds"),
        (nameof(ProfileSettingsDefaults.SpeedBinCount), "charts", "speedBinCount"),
        (nameof(ProfileSettingsDefaults.DefaultSpeedView), "charts", "defaultSpeedView"),
        (nameof(ProfileSettingsDefaults.ElevationSmoothingWindow), "charts", "elevationSmoothingWindow"),
        (nameof(ProfileSettingsDefaults.ChartTension), "charts", "tension"),
        (nameof(ProfileSettingsDefaults.ChartBorderWidth), "charts", "borderWidth"),
        (nameof(ProfileSettingsDefaults.ChartDetail), "charts", "chartDetail"),
        (nameof(ProfileSettingsDefaults.HillshadeExaggeration), "map", "hillshadeExaggeration"),
        (nameof(ProfileSettingsDefaults.TerrainExaggeration), "map", "terrainExaggeration"),
        (nameof(ProfileSettingsDefaults.FogDistance), "map", "fogDistance"),
        (nameof(ProfileSettingsDefaults.DefaultMapZoom), "map", "defaultZoom"),
        (nameof(ProfileSettingsDefaults.DefaultMapStyle), "map", "defaultStyle"),
        (nameof(ProfileSettingsDefaults.DefaultColorMode), "ui", "defaultColorMode"),
        (nameof(ProfileSettingsDefaults.ShowDebugInfo), "ui", "showDebugInfo"),
    ];

    [Fact]
    public void GeneratedTsDefaults_MatchTheCSharpConsts()
    {
        var path = Path.Combine(RepoRoot(), GeneratedRelativePath);
        Assert.True(File.Exists(path), $"{GeneratedRelativePath} is missing. Expected content:\n\n{Render()}");

        var actual = Normalize(File.ReadAllText(path));
        var expected = Normalize(Render());

        Assert.True(
            actual == expected,
            $"{GeneratedRelativePath} has drifted from ProfileSettingsDefaults. It is GENERATED — do not "
            + $"hand-edit it; change the const and paste this:\n\n{Render()}");
    }

    /// <summary>Every const in the contract must exist — a rename must not silently drop a default.</summary>
    [Fact]
    public void EveryContractEntry_ResolvesToAConst()
    {
        Assert.NotEmpty(Contract);
        foreach (var (constName, _, _) in Contract)
        {
            Assert.True(
                ConstField(constName) is not null,
                $"ProfileSettingsDefaults.{constName} does not exist — the TS contract references it.");
        }
    }

    private static FieldInfo? ConstField(string name) =>
        typeof(ProfileSettingsDefaults).GetField(name, BindingFlags.Public | BindingFlags.Static);

    private static string Render()
    {
        var sb = new StringBuilder();
        sb.Append(
            """
            // AUTO-GENERATED from RouteBaseWeb/Constants/ProfileSettingsDefaults.cs — do not make direct changes.
            // Regenerate: change the const, run `dotnet test` and paste what SettingsDefaultsTsGateTests prints.
            //
            // These are the SAME numbers the server applies, so a page that was delivered no settings island
            // renders exactly as a keyed page would with untouched preferences. Before this existed, each call
            // site carried its own literal and two of them had drifted from the C# value.

            export const PROFILE_SETTINGS_DEFAULTS = {

            """);

        foreach (var section in Contract.Select(c => c.Section).Distinct(StringComparer.Ordinal))
        {
            sb.Append('\t').Append(section).Append(": {\n");
            foreach (var (constName, _, property) in Contract.Where(c => c.Section == section))
            {
                var value = ConstField(constName)!.GetValue(null);
                sb.Append("\t\t").Append(property).Append(": ").Append(TsLiteral(value)).Append(",\n");
            }
            sb.Append("\t},\n");
        }

        sb.Append("} as const;\n");
        return sb.ToString();
    }

    // GetValue, not GetRawConstantValue: C# compiles `const decimal` to a static field carrying
    // [DecimalConstant] rather than a metadata constant, so GetRawConstantValue throws on it.
    private static string TsLiteral(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => $"'{s}'",
        decimal d => d.ToString("0.0###", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    // [CallerFilePath] → this file; up 2 (Architecture -> RouteBaseWeb.Tests) is the repo root.
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
