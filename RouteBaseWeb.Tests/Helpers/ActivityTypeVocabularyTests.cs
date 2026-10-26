using System.Runtime.CompilerServices;
using System.Xml.Linq;
using RouteBaseWeb.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Pins <see cref="ActivityTypeVocabulary"/>'s closed lists + the cross-repo contract with
/// GpsRecorder's <c>activity_types</c> string-array (AU-09: order is load-bearing, the app sends
/// the label not the position).
/// </summary>
public class ActivityTypeVocabularyTests
{
    // [CallerFilePath] gives this file's absolute path; walk up 2 dirs (Helpers -> RouteBaseWeb.Tests)
    // to reach the repo root, then down into GpsRecorder's resources. No Gradle needed — plain XML read.
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Fact]
    public void Recordable_MatchesAndroidStringsXml()
    {
        var stringsXmlPath = Path.Combine(RepoRoot(), "GpsRecorder", "app", "src", "main", "res", "values", "strings.xml");
        var doc = XDocument.Load(stringsXmlPath);
        var items = doc.Descendants("string-array")
            .Single(e => (string?)e.Attribute("name") == "activity_types")
            .Elements("item")
            .Select(e => e.Value)
            .ToList();

        Assert.True(items.SequenceEqual(ActivityTypeVocabulary.Recordable, StringComparer.Ordinal),
            $"Android activity_types [{string.Join(",", items)}] != ActivityTypeVocabulary.Recordable [{string.Join(",", ActivityTypeVocabulary.Recordable)}] — do not reorder either side independently (AU-09).");
    }

    [Theory]
    [InlineData("AlpineSki", "Alpine Ski")]
    [InlineData("NordicSki", "Nordic Ski")]
    [InlineData("Run", "Run")]
    public void DisplayLabel_DefaultsToValue_ExceptSkiTypes(string value, string expected)
    {
        Assert.Equal(expected, ActivityTypeVocabulary.DisplayLabel(value));
    }

    [Theory]
    [MemberData(nameof(AllLists))]
    public void AllLists_HaveNoDuplicates(IReadOnlyList<string> list)
    {
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }

    public static IEnumerable<object[]> AllLists()
    {
        yield return new object[] { ActivityTypeVocabulary.Recordable };
        yield return new object[] { ActivityTypeVocabulary.EditableKnown };
        yield return new object[] { ActivityTypeVocabulary.SegmentFilter };
    }

    // ─── Characterization pins: DC-06 / LR-06 out-of-whitelist passthrough ───
    // Details.cshtml and _RecentSessionsTable.cshtml both do `!list.Contains(storedValue)` to decide
    // whether to inject an extra selected <option> for a value outside the closed set (DC-06 / LR-06).
    // No Razor-rendering harness exists in this repo (grepped RouteBaseWeb.Tests for DC-06/LR-06/
    // activityTypeOptions — no hits before this file); pin the invariant those branches depend on at
    // the source-of-truth level instead: an arbitrary imported/detected type must never accidentally
    // be a member of the closed lists.

    [Theory]
    [InlineData("VirtualRide")] // valid EditableKnown value — sanity check the pin isn't vacuous
    public void EditableKnown_ContainsKnownValue_Sanity(string value)
    {
        Assert.Contains(value, ActivityTypeVocabulary.EditableKnown, StringComparer.Ordinal);
    }

    [Fact]
    public void EditableKnown_DoesNotContainArbitraryImportedType_DC06Passthrough()
    {
        // e.g. a raw Strava SportType or FIT sport string that never made it into the curated list.
        Assert.DoesNotContain("SomeExoticImportedSportType", ActivityTypeVocabulary.EditableKnown, StringComparer.Ordinal);
    }

    [Fact]
    public void Recordable_DoesNotContainArbitraryDetectedType_LR06Passthrough()
    {
        // e.g. an auto-detect result or legacy value outside the 7-value recordable set.
        Assert.DoesNotContain("SomeExoticImportedSportType", ActivityTypeVocabulary.Recordable, StringComparer.Ordinal);
    }
}
