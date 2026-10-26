using RouteBaseWeb.Constants;
using System.Reflection;
using System.Text.RegularExpressions;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The regression suite for "someone re-widens the share segment read".
///
/// ShareSegmentReaderTests pins the RESULT (a friend's effort is absent). These pin the STRUCTURE, which
/// is what actually survives a refactor: a reader that cannot reach an ambient identity cannot widen
/// through one, whatever a future edit does to its predicates.
///
/// They fail at the design step — the moment someone adds the dependency — rather than at a leaked row.
/// </summary>
public class ShareSegmentReaderIsolationTests
{
    [Fact]
    public void ShareSegmentReader_DependsOnNeitherVisibilityNorCurrentUser()
    {
        // THE test. Reaching for VisibleActivities to "fix a missing effort" means injecting
        // IVisibilityService, which fails here — before it can hand an anonymous stranger the owner's
        // friend graph.
        var ctor = typeof(ShareSegmentReader)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var parameters = ctor.GetParameters();
        Assert.NotEmpty(parameters); // anti-vacuity: a parameterless ctor would pass the assertions below

        Assert.DoesNotContain(parameters,
            p => typeof(IVisibilityService).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(parameters,
            p => typeof(ICurrentUserService).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void ShareSegmentReader_SourceNeverMentionsAVisibilityQuery()
    {
        // Belt and braces to the constructor check: it also catches a service-locator lookup, a static
        // helper, or a second constructor overload added later.
        var source = ReadSource("Services/Sharing/ShareSegmentReader.cs");
        var code = StripComments(source);

        Assert.DoesNotContain("VisibleActivities", code, StringComparison.Ordinal);
        Assert.DoesNotContain("VisibleSegments", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentUserId", code, StringComparison.Ordinal);

        // Anti-vacuity: prove the extraction actually read the reader, not an empty string.
        Assert.Contains("PermittedEfforts", code, StringComparison.Ordinal);
        Assert.Contains("OwnerUserId", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareSegmentReader_RestatesTheLifecycleGate()
    {
        // The clause most likely to be dropped: it is only there because we deliberately do NOT go through
        // VisibleActivities, which would otherwise carry it. Without it an owner's TRASHED ride would
        // appear on a public link.
        var code = StripComments(ReadSource("Services/Sharing/ShareSegmentReader.cs"));

        Assert.Contains("LifecycleStates.Released", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SharePage_DoesNotWidenTheSegmentBranch()
    {
        // Widening the segment read would land it on the OWNER's cache namespace — the one their normal
        // page filled with their friends' efforts — and every SegmentEfforts read checks its cache BEFORE
        // its visibility gate, so a cache hit would return third-party efforts before any predicate ran.
        // The activity branch DOES widen, correctly, so this cannot just assert "no WidenAsync anywhere".
        var code = StripComments(ReadSource("Pages/Share/View.cshtml.cs"));

        // End marker "\n    }" = the METHOD's closing brace (4-space indent) — a bare "}" would stop at
        // the first inner block and silently shrink what the guard below actually reads.
        var segmentBranch = Between(code, "LoadSegmentAsync(ShareGrant", "\n    }");
        Assert.DoesNotContain("WidenAsync", segmentBranch, StringComparison.Ordinal);

        // Anti-vacuity in both directions: the activity branch must still widen, or this test is passing
        // because the extraction silently matched nothing.
        var activityBranch = Between(code, "LoadActivityAsync(ShareGrant", "\n    }");
        Assert.Contains("WidenAsync", activityBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareCacheKeys_AreASeparateFamilyFromTheOwnersOwnKeys()
    {
        // Reusing CacheKeys.SegmentEfforts under a widened identity is the failure this design exists to
        // avoid; the share keys must be their own namespace.
        var code = StripComments(ReadSource("Services/CacheKeys.cs"));

        Assert.Contains("_share_seg_efforts_", code, StringComparison.Ordinal);
        Assert.Contains("_share_seg_totals_", code, StringComparison.Ordinal);
        Assert.Contains("_share_seg_perf_", code, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidateSegment_EvictsTheShareEntriesToo()
    {
        // Without this, an owner excluding an effort would leave every live share link serving the
        // pre-exclusion leaderboard until TTL — revocation-by-eviction would silently stop covering shares.
        var code = StripComments(ReadSource("Services/SegmentCache.cs"));
        var body = Between(code, "public void InvalidateSegment(", "\n    }");

        Assert.Contains("ShareSegmentEfforts", body, StringComparison.Ordinal);
        Assert.Contains("ShareSegmentTotals", body, StringComparison.Ordinal);
    }

    // ---- the raw-decode boundary -------------------------------------------------------------------

    /// <summary>
    /// <see cref="ITrackReader"/> has NO visibility check — its contract is "callers pre-authorize track
    /// ids", because it is the one shared decode cache serving segment matching, heatmaps, races and
    /// photos. Held by a share-slice type, it is raw read access to EVERY track in the database, and
    /// track ids are sequential.
    ///
    /// So exactly one share type may hold it, and that one funnels every decode through a permitted-set
    /// check. A new share reader taking the dependency fails HERE — the allowlist idiom of
    /// <c>ShareEndpointAllowlistTests</c>: widening is possible, but only as a deliberate act.
    /// </summary>
    [Fact]
    public void OnlyTheSegmentReader_HoldsRawTrackDecode()
    {
        string[] allowed = ["ShareSegmentReader"];

        var holders = SharingTypes()
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                         .SelectMany(c => c.GetParameters())
                         .Any(p => typeof(ITrackReader).IsAssignableFrom(p.ParameterType)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowed, holders);
    }

    [Fact]
    public void SharingTypes_IsNotVacuous()
    {
        // Guards the reflection above: a namespace filter that silently stopped matching would make the
        // set-equality test pass only while the holder list was empty.
        var types = SharingTypes();
        Assert.NotEmpty(types);
        Assert.Contains(types, t => t.Name == nameof(ShareSegmentReader));
    }

    /// <summary>
    /// The assertion the behavioural tests cannot make. <c>Track_ForAFriendsTrack_IsNull</c> proves the
    /// door that exists today is locked; it knows nothing about a SECOND door added later — a new
    /// endpoint, a perf refactor that hoists the decode, a badly resolved merge.
    ///
    /// Pins that every decode goes through <c>DecodeAsync</c>, which is only callable with a
    /// <c>PermittedTrackId</c>, which is only mintable by a gated factory.
    /// </summary>
    [Fact]
    public void TrackDecode_HappensInExactlyOnePlace()
    {
        var code = StripComments(ReadSource("Services/Sharing/ShareSegmentReader.cs"));

        var callSites = Occurrences(code, "trackReader.GetTrackAsync");
        Assert.True(callSites == 1,
            $"ShareSegmentReader calls trackReader.GetTrackAsync {callSites} times; exactly 1 is allowed. "
            + "ITrackReader performs no authorization, so every decode on this anonymous surface must go "
            + "through DecodeAsync, whose PermittedTrackId argument can only come from a gated factory.");

        // ...and that the one call sits inside DecodeAsync, not merely somewhere in the file. Anchored on
        // the RETURN TYPE so the marker cannot match a `await DecodeAsync(...)` call site instead — Between
        // takes the first hit, and the call sites appear earlier in the file.
        var decodeBody = Between(code, "Task<List<GpsCoordinate>?> DecodeAsync", "\n    }");
        Assert.Contains("trackReader.GetTrackAsync", decodeBody, StringComparison.Ordinal);

        // The token type is what makes DecodeAsync unreachable with a raw int — if it is deleted and the
        // signature widened back to int, the chokepoint stops being one.
        Assert.Contains("private readonly record struct PermittedTrackId", code, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>Concrete types of the share slice — the scope the raw-decode allowlist governs.</summary>
    private static Type[] SharingTypes() =>
        typeof(ShareSegmentReader).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                     && t.Namespace == typeof(ShareSegmentReader).Namespace)
            .ToArray();

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }


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
