using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Conditional-GET (ETag) helper for JSON bodies that are a pure function of immutable content — a track's
/// head object, a segment's fixed geometry. Serializes the payload ONCE with MVC's configured options (so the
/// wire bytes match what <c>Ok(payload)</c> would send), hashes those bytes into a content ETag, and returns
/// 304 when the caller's <c>If-None-Match</c> already matches — otherwise the body with a short private
/// <c>max-age</c> so a burst of per-card thumbnail requests on a page revisit collapses to zero round-trips.
///
/// Correct by construction: any change to the content (e.g. a re-analysis writing a new head object, so a
/// re-analysed track's GPS differs) changes the serialized bytes, hence the ETag — a stale body can never be
/// served once the browser revalidates. Mirrors the <see cref="RacesApiController"/> replay idiom (which also
/// hashes bytes rather than trusting a stored version column). Hashing the bytes also stays correct for a
/// still-recording track whose GPS grows behind a fixed head sha, which a head-sha-keyed ETag would not.
/// </summary>
internal static class ConditionalJson
{
    // Long enough to collapse a page-load burst of thumbnail requests into one network hit; short enough
    // that a re-analysis becomes visible within a minute even inside the no-revalidation window.
    private const int MaxAgeSeconds = 60;

    /// <summary>
    /// Serialize <paramref name="payload"/>, attach a content ETag + short private max-age, and return either
    /// 304 (If-None-Match hit) or the body as <c>application/json</c>. Requires <c>controller.Response</c>
    /// (an <c>HttpContext</c>) to be present.
    /// </summary>
    public static IActionResult ETagged(ControllerBase controller, object payload, JsonSerializerOptions options)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(body))}\"";

        controller.Response.Headers.CacheControl = $"private, max-age={MaxAgeSeconds}";
        controller.Response.Headers.ETag = etag;

        if (controller.Request.Headers.IfNoneMatch == etag)
            return controller.StatusCode(StatusCodes.Status304NotModified);

        return controller.File(body, "application/json");
    }
}
