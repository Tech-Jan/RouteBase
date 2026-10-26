using RouteBaseWeb.Constants;
using System.Reflection;
using System.Text.RegularExpressions;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Parity;

/// <summary>
/// Cross-tier pin (C3): the phone's <c>util/WireEnums.kt</c> names the same closed sets as the
/// server's <see cref="RecordingSessionStatus"/> and <see cref="Visibilities"/> const classes. This
/// scrapes the Kotlin source and compares BOTH directions — a value added or renamed on either tier
/// fails here, in the suite that runs on every commit, instead of surfacing as a phone that
/// mis-handles a status it has never heard of. (ShareCspHostParityTests is the worked example of
/// this scrape-the-other-tier pattern; WireEnumsTest is the phone-local half.)
///
/// Skips silently when GpsRecorder/ is absent (a server-only checkout must stay green).
/// </summary>
public class RecorderWireParityTests
{
    private const string WireEnumsRelativePath =
        "app/src/main/java/com/routebase/gpsrecorder/util/WireEnums.kt";

    /// <summary>Repo-root GpsRecorder dir, or null when this checkout doesn't carry the app.</summary>
    private static string? RecorderRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "GpsRecorder");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>NAME("wire") pairs of one Kotlin enum, scraped from its declaration block.</summary>
    private static IReadOnlySet<string> KotlinWireValues(string source, string enumName)
    {
        var block = Regex.Match(source, $@"enum class {enumName}[^{{]*\{{(?<body>[^}}]*?)companion",
            RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        Assert.True(block.Success, $"enum class {enumName} not found in WireEnums.kt — path or shape changed");

        return Regex.Matches(block.Groups["body"].Value, @"[A-Z_]+\(""(?<wire>[^""]*)""\)", RegexOptions.None, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups["wire"].Value)
            .Where(v => v.Length > 0)   // UNKNOWN("") is the client-only widening member, not a wire value
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> ServerConstValues(Type constClass) =>
        constClass.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);   // Visibilities.Default aliases Friends → distinct by value

    [Fact]
    public void PhoneWireEnums_MatchServerConstClasses_BothDirections()
    {
        var root = RecorderRoot();
        if (root is null) return;   // server-only checkout

        var path = Path.Combine(root, WireEnumsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{WireEnumsRelativePath} has moved — update this test.");
        var source = File.ReadAllText(path);

        var phoneStatuses = KotlinWireValues(source, "SessionStatus");
        var phoneVisibilities = KotlinWireValues(source, "Visibility");

        // Vacuity guards: a broken scrape must not pass as "no drift".
        Assert.True(phoneStatuses.Count >= 6, $"scraped only {phoneStatuses.Count} session statuses — scrape broken");
        Assert.True(phoneVisibilities.Count >= 3, $"scraped only {phoneVisibilities.Count} visibilities — scrape broken");

        AssertSetEqual(ServerConstValues(typeof(RecordingSessionStatus)), phoneStatuses, "session status");
        AssertSetEqual(ServerConstValues(typeof(Visibilities)), phoneVisibilities, "visibility");
    }

    private static void AssertSetEqual(IReadOnlySet<string> server, IReadOnlySet<string> phone, string what)
    {
        var serverOnly = server.Except(phone, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var phoneOnly = phone.Except(server, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.True(serverOnly.Count == 0 && phoneOnly.Count == 0,
            $"{what} vocabulary drifted between tiers.\n"
            + $"  on the server but not the phone: [{string.Join(", ", serverOnly)}]\n"
            + $"  on the phone but not the server: [{string.Join(", ", phoneOnly)}]\n"
            + "Fix WireEnums.kt or the C# const class — the wire set is one set.");
    }
}
