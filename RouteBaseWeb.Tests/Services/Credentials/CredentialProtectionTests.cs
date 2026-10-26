using RouteBaseWeb.Services.Credentials;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Credentials;

/// <summary>
/// Pins the credential-at-rest contract (decision ⑥ / SVC-10): protect/unprotect round-trip,
/// unprotected-value passthrough, and lost-key-ring → null (treat as missing, never throw into
/// the sync path).
/// </summary>
public class CredentialProtectionTests
{
    private static CredentialProtector NewProtector() => new(
        new EphemeralDataProtectionProvider(NullLoggerFactory.Instance),
        NullLogger<CredentialProtector>.Instance);

    [Fact]
    public void ProtectUnprotect_RoundTrips()
    {
        var p = NewProtector();
        var stored = p.Protect("6bf3a1c2plaintexttoken");

        Assert.True(p.IsProtected(stored));
        Assert.NotEqual("6bf3a1c2plaintexttoken", stored, StringComparer.Ordinal);
        Assert.Equal("6bf3a1c2plaintexttoken", p.Unprotect(stored));
    }

    [Fact]
    public void Unprotect_UnprotectedValue_PassesThrough()
    {
        var p = NewProtector();
        Assert.False(p.IsProtected("6bf3a1c2plaintexttoken"));
        Assert.Equal("6bf3a1c2plaintexttoken", p.Unprotect("6bf3a1c2plaintexttoken"));
    }

    [Fact]
    public void Unprotect_ForeignKeyRing_ReturnsNullNotThrow()
    {
        var stored = NewProtector().Protect("secret");
        var otherRing = NewProtector();

        Assert.True(otherRing.IsProtected(stored));
        Assert.Null(otherRing.Unprotect(stored));
    }

    [Fact]
    public void Unprotect_CorruptStoredValue_ReturnsNullNotThrow()
    {
        // DM-02 (data-model audit 2026-07-20): a CfDJ8-prefixed value that is not even valid base64url
        // (hand-edited or bit-rotted DB) must fail closed like a foreign-key-ring payload — null, not an
        // escaping decode exception. Holds today only because the string Unprotect extension wraps decode
        // errors in CryptographicException; this pin keeps that corner covered if the protector changes.
        var p = NewProtector();
        Assert.Null(p.Unprotect("CfDJ8!!not-base64url!!"));
    }

}
