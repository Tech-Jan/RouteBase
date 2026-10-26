using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// Leak hygiene for share URLs: a live token must never be persisted, and must never travel to a third
/// party or a shared cache.
/// </summary>
public class ShareUrlRedactionTests
{
    [Fact]
    public void RedactPath_ReplacesTheTokenSegment_KeepingTheRestDiagnosable()
    {
        var token = ShareTokens.New();

        Assert.Equal("/s/{token}", ShareUrlRedaction.RedactPath($"/s/{token}"));
        Assert.Equal("/s/{token}/api/photos/3/image",
            ShareUrlRedaction.RedactPath($"/s/{token}/api/photos/3/image"));
    }

    [Fact]
    public void RedactPath_LeavesNonSharedPathsAlone()
    {
        Assert.Equal("/Activities/Details/42", ShareUrlRedaction.RedactPath("/Activities/Details/42"));
        Assert.Equal("/api/photos", ShareUrlRedaction.RedactPath("/api/photos"));
        Assert.Equal("/", ShareUrlRedaction.RedactPath("/"));
        Assert.Equal("/segments", ShareUrlRedaction.RedactPath("/segments"));   // not /s/ despite the prefix letters
    }

    [Fact]
    public void RedactPath_HandlesNullAndEmpty()
    {
        Assert.Null(ShareUrlRedaction.RedactPath(null));
        Assert.Equal("", ShareUrlRedaction.RedactPath(""));
    }

    [Fact]
    public void RedactPath_IsCaseInsensitiveOnThePrefix()
    {
        // Routing is case-insensitive, so /S/<token> reaches the same endpoint and must redact too.
        var token = ShareTokens.New();
        Assert.Equal("/s/{token}", ShareUrlRedaction.RedactPath($"/S/{token}"));
    }

    [Fact]
    public void RedactMethodAndPath_RedactsTheStoredErrorLogForm()
    {
        // This is the exact string ErrorLogLoggerProvider persists into Error_Log and /Admin/Errors renders.
        var token = ShareTokens.New();

        Assert.Equal("GET /s/{token}", ShareUrlRedaction.RedactMethodAndPath($"GET /s/{token}"));
        Assert.Equal("POST /s/{token}/api/segment/efforts",
            ShareUrlRedaction.RedactMethodAndPath($"POST /s/{token}/api/segment/efforts"));
        Assert.Equal("GET /Activities/Details/42",
            ShareUrlRedaction.RedactMethodAndPath("GET /Activities/Details/42"));
    }

    [Fact]
    public void RedactedOutput_NeverContainsTheToken()
    {
        // The property that actually matters, asserted directly rather than inferred from the shape.
        var token = ShareTokens.New();

        foreach (var input in new[] { $"/s/{token}", $"/s/{token}/api/x", $"GET /s/{token}/api/x" })
        {
            var redacted = ShareUrlRedaction.RedactMethodAndPath(input)!;
            Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Regression: redacting only the structured `path` column left the token in the log MESSAGE beside
    /// it. ApiExceptionFilter formats the raw path into its own text ("Unhandled exception: GET /s/…"),
    /// and that string is persisted to Error_Log and rendered on /Admin/Errors — so any unhandled
    /// exception on a share endpoint wrote a WORKING credential to the database in plaintext.
    /// </summary>
    [Fact]
    public void RedactAnywhere_StripsATokenEmbeddedInFreeText()
    {
        var token = ShareTokens.New();

        foreach (var input in new[]
                 {
                     $"Unhandled exception: GET /s/{token}/api/photos",
                     $"something failed at https://routebase.example/s/{token} while reading",
                     $"two of them: /s/{token} and /s/{token}/api/photos/3/image",
                 })
        {
            var redacted = ShareUrlRedaction.RedactAnywhere(input);
            Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
            Assert.Contains("/s/{token}", redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RedactAnywhere_IsCaseInsensitiveOnThePrefix()
    {
        // Routing is case-insensitive, so /S/<token> authenticates and serves like /s/<token> — an
        // unhandled exception on that spelling must not evade the free-text redaction (RedactPath already
        // handles it; this pins the RedactAnywhere side, which feeds Error_Log messages and stack traces).
        var token = ShareTokens.New();

        var redacted = ShareUrlRedaction.RedactAnywhere($"Unhandled exception: GET /S/{token}/api/photos");

        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Contains("{token}", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactAnywhere_LeavesUnrelatedTextAlone()
    {
        Assert.Equal("no tokens here", ShareUrlRedaction.RedactAnywhere("no tokens here"));
        // Right prefix, wrong length — not a token, so not redacted.
        Assert.Equal("/s/tooshort", ShareUrlRedaction.RedactAnywhere("/s/tooshort"));
        Assert.Null(ShareUrlRedaction.RedactAnywhere(null));
    }

    /// <summary>
    /// The free-text matcher hard-codes the token length (GeneratedRegex needs a literal), so it cannot
    /// reference ShareTokens.TokenLength directly. This is the guard against the two drifting apart —
    /// without it, changing the token length would silently stop redacting anything.
    /// </summary>
    [Fact]
    public void RedactAnywhere_MatchesTheActualTokenLength()
    {
        Assert.Equal(43, ShareTokens.TokenLength);
        Assert.Equal(ShareTokens.TokenLength, ShareTokens.New().Length);
    }
}

/// <summary>Response headers applied to every share response.</summary>
public class ShareResponseHeadersTests
{
    private static HttpResponse Apply()
    {
        var context = new DefaultHttpContext();
        ShareResponseHeaders.Apply(context.Response);
        return context.Response;
    }

    [Fact]
    public void Apply_SetsNoindex_SoADiscoveredLinkStaysOutOfSearchResults()
        => Assert.Equal("noindex, nofollow, noarchive, nosnippet", Apply().Headers["X-Robots-Tag"]);

    [Fact]
    public void Apply_SendsOriginOnlyReferrer_SoTheTokenNeverReachesTileHosts()
    {
        // The token is in the URL PATH. Without a policy, every third-party map-tile request leaks it in
        // the Referer header — the single most likely way a share link escapes. Origin-only withholds the
        // whole path, so the token still never leaves this origin.
        //
        // Not 'no-referrer': sending nothing at all makes OpenStreetMap's tile servers answer with their
        // "Access blocked — Referer is required by tile usage policy" image, which broke the OSM basemap
        // on every share view.
        Assert.Equal("strict-origin", Apply().Headers["Referrer-Policy"]);
    }

    /// <summary>
    /// The invariant behind the value above, pinned separately: whatever the policy is, it must never be
    /// one that carries the URL PATH to another host. A plain string equality would happily accept
    /// 'strict-origin-when-cross-origin' — which still sends the full token URL on same-origin requests —
    /// as a "small" edit.
    /// </summary>
    [Fact]
    public void Apply_UsesAPolicyThatCannotCarryTheTokenPath()
    {
        string[] pathCarrying =
            ["unsafe-url", "no-referrer-when-downgrade", "origin-when-cross-origin", "strict-origin-when-cross-origin", "same-origin"];
        string[] safe = ["strict-origin", "origin", "no-referrer"];

        var policy = Apply().Headers["Referrer-Policy"].ToString();

        Assert.DoesNotContain(policy, pathCarrying, StringComparer.Ordinal);
        Assert.Contains(policy, safe, StringComparer.Ordinal);
    }

    [Fact]
    public void Apply_ForbidsSharedCaching_BecauseTheUrlIsACredential()
    {
        var cacheControl = Apply().Headers.CacheControl.ToString();
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("public", cacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_SetsFramingAndSniffingGuards()
    {
        var response = Apply();
        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }
}

/// <summary>Token generation and shape-checking.</summary>
public class ShareTokensTests
{
    [Fact]
    public void New_Produces43UrlSafeChars()
    {
        var token = ShareTokens.New();

        Assert.Equal(ShareTokens.TokenLength, token.Length);
        Assert.True(ShareTokens.IsWellFormed(token));
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void New_IsUnpredictable_AcrossManyDraws()
    {
        // Entropy sanity: 256 bits means a collision here would indicate a broken RNG, not bad luck.
        var tokens = Enumerable.Range(0, 1000).Select(_ => ShareTokens.New()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(1000, tokens.Count);
    }

    [Fact]
    public void Hash_IsStable_AndNotThePlaintext()
    {
        var token = ShareTokens.New();

        Assert.Equal(ShareTokens.Hash(token), ShareTokens.Hash(token));
        Assert.NotEqual(token, ShareTokens.Hash(token), StringComparer.Ordinal);
        Assert.Equal(64, ShareTokens.Hash(token).Length);       // SHA-256 as lowercase hex
        Assert.DoesNotContain(token, ShareTokens.Hash(token), StringComparison.Ordinal);
    }

    [Fact]
    public void IsWellFormed_RejectsWrongLengthAndWrongAlphabet()
    {
        Assert.False(ShareTokens.IsWellFormed(null));
        Assert.False(ShareTokens.IsWellFormed(""));
        Assert.False(ShareTokens.IsWellFormed(new string('a', ShareTokens.TokenLength - 1)));
        Assert.False(ShareTokens.IsWellFormed(new string('a', ShareTokens.TokenLength + 1)));
        Assert.False(ShareTokens.IsWellFormed(new string('!', ShareTokens.TokenLength)));
        Assert.False(ShareTokens.IsWellFormed("../" + new string('a', ShareTokens.TokenLength - 3)));
        Assert.True(ShareTokens.IsWellFormed(new string('a', ShareTokens.TokenLength)));
    }

    [Fact]
    public void HashOrEmpty_ReturnsEmptyForMalformedInput_WithoutHashing()
    {
        // Used as a rate-limiter partition key on attacker-controlled input: a malformed probe must not
        // buy an arbitrarily expensive hash, nor its own partition.
        Assert.Equal("", ShareTokens.HashOrEmpty(null));
        Assert.Equal("", ShareTokens.HashOrEmpty(""));
        Assert.Equal("", ShareTokens.HashOrEmpty(new string('x', 100_000)));
        Assert.NotEqual("", ShareTokens.HashOrEmpty(ShareTokens.New()), StringComparer.Ordinal);
    }
}
