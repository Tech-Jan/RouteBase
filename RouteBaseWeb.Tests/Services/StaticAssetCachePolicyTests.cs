using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// C2: header policy for static-asset responses. Versioned (?v=) URLs cache immutably; unversioned
/// URLs get a short conservative TTL. The OnPrepareResponse wiring itself needs a host, so this pins
/// the pure decision the wiring delegates to.
/// </summary>
public class StaticAssetCachePolicyTests
{
    [Fact]
    public void Versioned_UsesImmutableLongMaxAge()
    {
        Assert.Equal("public, max-age=604800, immutable", StaticAssetCachePolicy.CacheControlFor(true, "/js/pages/dashboard.js"));
    }

    [Fact]
    public void Unversioned_UsesShortMaxAge()
    {
        var value = StaticAssetCachePolicy.CacheControlFor(false, "/js/pages/dashboard.js");
        Assert.Equal("public, max-age=3600", value);
        Assert.DoesNotContain("immutable", value, StringComparison.Ordinal);
    }

    [Fact]
    public void HashedChunk_IsVersionedEvenWithoutQuery()
    {
        // Chunks carry no ?v= — no .cshtml names them, the entry imports one by path — so the
        // content hash baked into the filename is their only cache-buster.
        var value = StaticAssetCachePolicy.CacheControlFor(false, "/js/chunks/camera-controls-DnUqPYlr.js");
        Assert.Equal("public, max-age=604800, immutable", value);
    }

    [Fact]
    public void NonChunkUnversionedPath_StaysShortMaxAge()
    {
        Assert.False(StaticAssetCachePolicy.IsHashedChunk("/js/pages/dashboard.js"));
    }

    [Fact]
    public void IsHashedChunk_MatchesAnyChunksSegment()
    {
        Assert.True(StaticAssetCachePolicy.IsHashedChunk("/js/chunks/edit-api-n4M3qYTV.js"));
        Assert.False(StaticAssetCachePolicy.IsHashedChunk("/css/site.css"));
    }
}
