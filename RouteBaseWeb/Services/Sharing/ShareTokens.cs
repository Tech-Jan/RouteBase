using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Share-token generation and hashing. Deliberately the same construction as
/// <see cref="ApiKeyService"/>: 32 CSPRNG bytes → Base64Url (43 URL-safe chars, unpadded), SHA-256 hex
/// at rest. 256 bits is far past enumerable, which is what lets the URL itself be the credential.
///
/// The plaintext is never persisted and never logged — only <see cref="Hash"/> reaches the DB.
/// </summary>
public static class ShareTokens
{
    /// <summary>Length of a Base64Url-encoded 32-byte token. Callers length-check BEFORE hashing.</summary>
    public const int TokenLength = 43;

    /// <summary>A fresh 256-bit token, URL-safe and unpadded so it needs no escaping in a path segment.</summary>
    public static string New()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 (lowercase hex) — the stored form and the lookup key.</summary>
#pragma warning disable CA1308 // token hash stored/looked-up as lowercase hex (matches ApiKeyService + DB rows)
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
#pragma warning restore CA1308

    /// <summary>
    /// <see cref="Hash"/> for a well-formed token, empty string otherwise. For call sites that take
    /// attacker-controlled input of unbounded length (rate-limiter partition keys, resolver lookups):
    /// hashing an arbitrarily large string on every probe is free work for an attacker.
    /// </summary>
    public static string HashOrEmpty(string? token)
        => IsWellFormed(token) ? Hash(token) : "";

    /// <summary>
    /// Shape check only — says nothing about whether the token exists. Guards the length and the
    /// alphabet so a malformed probe is rejected before it costs a hash or a DB round-trip.
    /// </summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? token)
    {
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
