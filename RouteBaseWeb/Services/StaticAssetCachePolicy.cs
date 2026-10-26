namespace RouteBaseWeb.Services;

/// <summary>
/// C2: Cache-Control for <c>UseStaticFiles</c> responses. Local script/style tags are cache-busted with
/// <c>asp-append-version</c> (a <c>?v=&lt;hash&gt;</c> query), so a versioned URL's bytes never change and
/// can be cached immutably; an unversioned direct hit gets a short, conservative TTL instead. Assets are
/// non-sensitive and served before auth, so <c>public</c> is correct. Pure → unit-testable.
/// </summary>
public static class StaticAssetCachePolicy
{
    // Content-addressed via ?v= (or a hashed chunk filename) → safe to pin for a week and skip revalidation.
    public const string Versioned = "public, max-age=604800, immutable";
    // No version bust on the URL → short TTL so a changed file can't be served stale for long.
    public const string Unversioned = "public, max-age=3600";

    /// <summary>
    /// True for a Vite-emitted chunk under wwwroot/js/chunks/ — <c>chunks/[name]-[hash].js</c>, the hash
    /// baked into the filename by content (see vite.config.esm.ts chunkFileNames). No .cshtml ever names a
    /// chunk directly (entries import it by path), so asp-append-version's <c>?v=</c> query can never reach
    /// it — the filename hash is the only cache-buster it has, so it's already as immutable as ?v= makes
    /// an entry file.
    /// </summary>
    public static bool IsHashedChunk(string requestPath) =>
        requestPath.Contains("/chunks/", StringComparison.Ordinal);

    public static string CacheControlFor(bool hasVersionQuery, string requestPath) =>
        hasVersionQuery || IsHashedChunk(requestPath) ? Versioned : Unversioned;
}
