using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using RouteBaseWeb.Api.OpenApiContracts;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// AR-03 S8: <c>src/js/generated/vocabularies.ts</c> is rendered from the C# const classes and
/// diffed here — named unions + <c>*_VALUES</c> arrays for client code that BUILDS option lists,
/// covering entity-only sets the property stamping can't reach. Complementary to the
/// <c>[WireValues]</c> enum stamps: both derive from the same const classes, so neither can drift
/// from the other. Same mechanism as <see cref="SettingsDefaultsTsGateTests"/> (render by
/// reflection in C#, never parse C# from node).
///
/// To update: change the const class, run this test, paste the printed content into the file.
/// </summary>
public class VocabulariesTsGateTests
{
    private const string GeneratedRelativePath = "RouteBaseWeb/src/js/generated/vocabularies.ts";

    /// <summary>Vocabulary → TS names. Values render in declaration order, aliases deduped
    /// (Visibilities.Default). Entity-only sets (FriendshipStatuses, PhotoAssignments…) are
    /// deliberately absent: they never reach the client.</summary>
    private static readonly (Type ConstClass, string TsType, string ValuesConst)[] Contract =
    [
        (typeof(Visibilities), "Visibility", "VISIBILITY_VALUES"),
        (typeof(RecordingSessionStatus), "RecordingSessionStatus", "RECORDING_SESSION_STATUS_VALUES"),
        (typeof(ApiErrorCodes), "ApiErrorCode", "API_ERROR_CODE_VALUES"),
        (typeof(LifecycleStates), "LifecycleState", "LIFECYCLE_STATE_VALUES"),
        (typeof(LifecycleReasons), "LifecycleReason", "LIFECYCLE_REASON_VALUES"),
        (typeof(ShareKindNames), "ShareKind", "SHARE_KIND_VALUES"),
        (typeof(RaceParticipantKind), "RaceParticipantKind", "RACE_PARTICIPANT_KIND_VALUES"),
        (typeof(ActivitySources), "ActivitySource", "ACTIVITY_SOURCE_VALUES"),
        (typeof(HeatmapStatuses), "HeatmapStatus", "HEATMAP_STATUS_VALUES"),
        (typeof(ActivityEditSessionStatuses), "EditSessionStatus", "EDIT_SESSION_STATUS_VALUES"),
        (typeof(SegmentKinds), "SegmentKind", "SEGMENT_KIND_VALUES"),
        (typeof(QueueOperationTypes), "QueueOperationType", "QUEUE_OPERATION_TYPE_VALUES"),
        (typeof(PhotoLocationSources), "PhotoLocationSource", "PHOTO_LOCATION_SOURCE_VALUES"),
        (typeof(CostKinds), "CostKind", "COST_KIND_VALUES"),
        (typeof(SortDirections), "SortDirection", "SORT_DIRECTION_VALUES"),
        (typeof(ChartDetailLevels), "ChartDetailLevel", "CHART_DETAIL_LEVEL_VALUES"),
    ];

    [Fact]
    public void GeneratedTsVocabularies_MatchTheCSharpConstClasses()
    {
        var path = Path.Combine(RepoRoot(), GeneratedRelativePath);
        Assert.True(File.Exists(path), $"{GeneratedRelativePath} is missing. Expected content:\n\n{Render()}");

        var actual = Normalize(File.ReadAllText(path));
        var expected = Normalize(Render());

        Assert.True(
            actual == expected,
            $"{GeneratedRelativePath} has drifted from the C# const classes. It is GENERATED — do not "
            + $"hand-edit it; change the const class and paste this:\n\n{Render()}");
    }

    /// <summary>Vacuity: every vocabulary must yield values, and the big ones their known floor.</summary>
    [Fact]
    public void EveryVocabulary_YieldsValues()
    {
        foreach (var (constClass, tsType, _) in Contract)
        {
            var values = WireValueEnumSchemaTransformer.ConstStrings(constClass);
            Assert.True(values.Count >= 2, $"{tsType}: only {values.Count} values scraped from {constClass.Name}");
        }
        Assert.True(WireValueEnumSchemaTransformer.ConstStrings(typeof(ApiErrorCodes)).Count >= 9);
        Assert.True(WireValueEnumSchemaTransformer.ConstStrings(typeof(RecordingSessionStatus)).Count == 6);
        Assert.True(WireValueEnumSchemaTransformer.ConstStrings(typeof(Visibilities)).Count == 3);
    }

    private static string Render()
    {
        var sb = new StringBuilder();
        sb.Append(
            """
            // AUTO-GENERATED from the RouteBaseWeb C# const classes — do not make direct changes.
            // Regenerate: change the const class, run `dotnet test` and paste what VocabulariesTsGateTests prints.
            //
            // Named unions + *_VALUES arrays for client code that BUILDS option lists or validates raw
            // strings. Wire DTO properties carry the same unions via the [WireValues] enum stamps in
            // openapi-types.ts; both derive from the same const classes and cannot drift apart.


            """);

        foreach (var (constClass, tsType, valuesConst) in Contract)
        {
            var values = WireValueEnumSchemaTransformer.ConstStrings(constClass);
            var union = string.Join(" | ", values.Select(v => $"'{v}'"));
            var array = string.Join(", ", values.Select(v => $"'{v}'"));
            sb.Append(CultureInfo.InvariantCulture, $"// {constClass.Name}.cs\n");
            sb.Append(CultureInfo.InvariantCulture, $"export type {tsType} = {union};\n");
            sb.Append(CultureInfo.InvariantCulture, $"export const {valuesConst}: readonly {tsType}[] = [{array}];\n\n");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    // [CallerFilePath] → this file; up 2 (Architecture -> RouteBaseWeb.Tests) is the repo root.
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
