using Microsoft.AspNetCore.Http;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Response hygiene for every share-link response. Applied by the share page and every share read
/// endpoint — the token is in the URL, so these headers are what stop that URL from spreading.
/// </summary>
public static class ShareResponseHeaders
{
    public static void Apply(HttpResponse response)
    {
        // Keeps a discovered link out of search results. robots.txt is advisory and only consulted for
        // crawling; this header governs INDEXING and is the control that actually binds.
        response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive, nosnippet";

        // MANDATORY, not defensive. The share page loads map tiles from third-party hosts, and a default
        // policy would send them the token — which sits in the URL path — in the Referer header on every
        // single tile request.
        //
        // 'strict-origin', NOT 'no-referrer': origin-only already withholds the whole path (so the token
        // never leaves this origin), while sending NOTHING makes OpenStreetMap's tile servers refuse the
        // request and serve their "Access blocked — Referer is required by tile usage policy" image. That
        // broke the OSM basemap on every share view. Do not "harden" this back to no-referrer, and do not
        // relax it to a value that carries the path (see ShareResponseHeadersTests).
        response.Headers["Referrer-Policy"] = "strict-origin";

        // A URL bearing a credential must never be held by a shared or CDN cache.
        response.Headers.CacheControl = "private, no-store, max-age=0";

        // A share view is not something to embed; framing it only enables clickjacking around whatever
        // controls the page does expose.
        response.Headers["X-Frame-Options"] = "DENY";

        // The response is served from a token URL and renders user-supplied text (activity names).
        response.Headers["X-Content-Type-Options"] = "nosniff";

        response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }

    /// <summary>
    /// Hosts the MAP stack fetches from.
    ///
    /// The source of truth is the frontend — <c>src/js/core/basemap-registry.ts</c> (basemap style URLs),
    /// <c>src/js/maplibre/terrarium-dem.ts</c> (elevation DEM), and the self-hosted style JSON under
    /// <c>wwwroot/map-styles/</c>, whose registry entries are same-origin paths that hide the third-party
    /// tile host one level down. This list is a C# copy of all of those, and a copy that silently drifts
    /// breaks the map with a console error and nothing else — twice now: <c>tiles.openfreemap.org</c>
    /// (default basemap) in the first version, then <c>server.arcgisonline.com</c> (satellite), which was
    /// invisible to the parity test until it started reading the style JSON too.
    ///
    /// <c>ShareCspHostParityTests</c> reads those sources and fails if any gains a host this list lacks.
    /// Add hosts HERE and there in the same commit.
    /// </summary>
    private static readonly string[] MapHosts =
    [
        "https://tiles.openfreemap.org",        // default vector basemaps (liberty/bright/positron)
        "https://*.tile.openstreetmap.org",     // raster OSM basemap option (a./b./c. subdomains)
        "https://api.maptiler.com",             // key-gated basemaps + MapTiler DEM
        "https://elevation-tiles-prod.s3.amazonaws.com", // terrarium DEM (SVC-11: one bucket, not all of S3)
        "https://server.arcgisonline.com",      // Esri World Imagery (satellite-esri basemap)
    ];

    /// <summary>CDNs serving the pinned libraries. Every tag referencing these is SRI-checked in
    /// <c>_CdnLibs.cshtml</c>, so the allowance here is a second lock on a bolted door.</summary>
    private static readonly string[] CdnHosts =
    [
        "https://cdn.jsdelivr.net",
        "https://unpkg.com",
    ];

    /// <summary>
    /// The share view is the only anonymously-reachable page in the app that renders ANOTHER user's
    /// content, so it is the one that most wants a script allowlist. Scoped to share responses rather than
    /// applied app-wide: the authenticated pages carry inline handlers this policy would break, and a
    /// header that has to be relaxed everywhere protects nothing.
    ///
    /// 'unsafe-inline' is present for STYLE only, never script — MapLibre writes inline styles for
    /// marker/canvas positioning and cannot run without it. The JSON config island is
    /// <c>type="application/json"</c>, not executable, so it needs no script allowance.
    /// </summary>
    private static readonly string ContentSecurityPolicy = string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' {Hosts(CdnHosts)}",
        $"style-src 'self' 'unsafe-inline' {Hosts(CdnHosts)} https://fonts.googleapis.com",
        "font-src 'self' https://cdn.jsdelivr.net https://fonts.gstatic.com data:",
        // Sprite sheets and raster tiles load as images; blob:/data: are MapLibre's own canvas sources.
        $"img-src 'self' data: blob: {Hosts(MapHosts)}",
        // Vector tiles, style JSON and glyph ranges are fetched, not <img>-loaded — so the same map hosts
        // are needed again here. The CDNs are included for the sourcemap fetches devtools makes.
        $"connect-src 'self' {Hosts(MapHosts)} {Hosts(CdnHosts)}",
        "worker-src 'self' blob:",
        // No plugins, no <base> rewriting, and no framing — the last pairs with X-Frame-Options for
        // browsers that honour only one of the two.
        "object-src 'none'",
        "base-uri 'none'",
        "frame-ancestors 'none'",
        // A share page never submits anything; it is a read-only view.
        "form-action 'none'");

    private static string Hosts(string[] hosts) => string.Join(' ', hosts);

    /// <summary>The map hosts the policy allows. Exposed for the parity test, which is the only guard
    /// against this list drifting from the frontend registry it copies.</summary>
    internal static IReadOnlyList<string> AllowedMapHosts => MapHosts;
}
