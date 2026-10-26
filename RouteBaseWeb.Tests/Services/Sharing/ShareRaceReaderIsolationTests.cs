using RouteBaseWeb.Constants;
using System.Reflection;
using System.Text.RegularExpressions;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Sharing;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The regression suite for "someone re-widens the share race read" — the ShareSegmentReader isolation
/// tests, applied to the race surface.
///
/// <see cref="ShareRacePayloadRedactionTests"/> pins the RESULT (no identity, no foreign GPS). These pin
/// the STRUCTURE, which is what survives a refactor: a reader that cannot reach an ambient identity cannot
/// widen through one, whatever a future edit does to its predicates. They fail at the design step — the
/// moment someone adds the dependency — rather than at a leaked row.
///
/// The race surface has one dependency the segment surface does not: <see cref="IRaceAccess"/>. It is the
/// slice's proper authorization seam for every signed-in caller, and exactly the wrong thing here — it
/// re-authorizes each real participant against the CURRENT user's friend graph, which under a widened read
/// would be the OWNER's. So it is named explicitly below rather than left to the IVisibilityService check.
/// </summary>
public class ShareRaceReaderIsolationTests
{
    [Fact]
    public void ShareRaceReader_DependsOnNoAmbientIdentity()
    {
        var ctor = typeof(ShareRaceReader)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var parameters = ctor.GetParameters();
        Assert.NotEmpty(parameters); // anti-vacuity: a parameterless ctor would pass everything below

        Assert.DoesNotContain(parameters,
            p => typeof(IVisibilityService).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(parameters,
            p => typeof(ICurrentUserService).IsAssignableFrom(p.ParameterType));
        // IRaceAccess holds an IVisibilityService and re-authorizes the field against it — injecting it
        // here would reintroduce the friend-graph read through the back door.
        Assert.DoesNotContain(parameters,
            p => typeof(IRaceAccess).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(parameters,
            p => typeof(IRaceReplayService).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void ShareRaceReader_SourceNeverMentionsAVisibilityQuery()
    {
        // Belt and braces to the constructor check: it also catches a service-locator lookup, a static
        // helper, or a second constructor overload added later.
        var code = StripComments(ReadSource("Services/Sharing/ShareRaceReader.cs"));

        Assert.DoesNotContain("VisibleActivities", code, StringComparison.Ordinal);
        Assert.DoesNotContain("VisibleRaces", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CanViewActivityAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentUserId", code, StringComparison.Ordinal);

        // Anti-vacuity: prove the extraction actually read the reader, not an empty string.
        Assert.Contains("PublishableActivities", code, StringComparison.Ordinal);
        Assert.Contains("OwnerUserId", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareRaceReader_RestatesTheLifecycleGate()
    {
        // The clause most likely to be dropped: it is only there because we deliberately do NOT go through
        // VisibleActivities, which would otherwise carry it. Without it an owner's TRASHED course — or a
        // trashed rider's ride — would appear on a public link.
        var code = StripComments(ReadSource("Services/Sharing/ShareRaceReader.cs"));

        Assert.Contains("LifecycleStates.Released", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharePage_DoesNotWidenTheRaceBranch()
    {
        // Widening would run RaceAccess's per-rider re-authorization as the OWNER, whose friend graph then
        // admits every friend's ride — name and full GPS — to an anonymous stranger. The activity branch
        // DOES widen, correctly, so this cannot just assert "no WidenAsync anywhere".
        var code = StripComments(ReadSource("Pages/Share/View.cshtml.cs"));

        // End marker "\n    }" = the METHOD's closing brace (4-space indent) — a bare "}" would stop at
        // the first inner block and silently shrink what the guard below actually reads.
        var raceBranch = Between(code, "LoadRaceAsync(ShareGrant", "\n    }");
        Assert.DoesNotContain("WidenAsync", raceBranch, StringComparison.Ordinal);

        // Anti-vacuity in both directions: the activity branch must still widen, or this test is passing
        // because the extraction silently matched nothing.
        var activityBranch = Between(code, "LoadActivityAsync(ShareGrant", "\n    }");
        Assert.Contains("WidenAsync", activityBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareRaceReadController_ReadsTheRaceIdFromTheGrant_NeverTheRoute()
    {
        // The structural half of Covers: with no id parameter there is no id to re-assert, so the endpoint
        // cannot serve a race the token does not name however wrong a future filter becomes. Same property
        // the segment and live surfaces have.
        var code = StripComments(ReadSource("Api/ShareRaceReadController.cs"));

        Assert.DoesNotContain("{raceId", code, StringComparison.Ordinal);
        Assert.Contains("grant.Kind != ShareKind.Race", code, StringComparison.Ordinal);
        Assert.Contains("ShareResponseHeaders.Apply", code, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(string relativeToWeb)
    {
        var path = Path.Combine(RepoRoot(), "RouteBaseWeb", relativeToWeb);
        Assert.True(File.Exists(path), $"source not found: {relativeToWeb}");
        return File.ReadAllText(path);
    }

    /// <summary>Strips // and /* */ comments so the assertions read CODE, not the prose explaining it —
    /// every one of these files discusses VisibleActivities at length in its remarks.</summary>
    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        return Regex.Replace(noBlock, @"//.*?$", string.Empty, RegexOptions.Multiline, RegexDefaults.MatchTimeout);
    }

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found after: {startMarker}");
        return source[start..end];
    }
}
